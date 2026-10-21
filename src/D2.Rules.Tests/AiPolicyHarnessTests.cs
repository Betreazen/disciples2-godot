using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit.Abstractions;

/// <summary>Политика ИИ в бою (<see cref="BattleAi"/>) против решений журнала — по каждому
/// <c>battle_turn</c>. Разбор — как у модели <c>re-ai/decisions.py</c> и сверка — как <c>evalai.py</c>
/// (docs/synthetic-maps/analysis/G-ai-battle.md §4–5C): снимок до решения — последний из
/// <c>battle_start</c>/<c>round</c>/<c>turn_before</c>/<c>turn_after</c>; окно бросков — от <c>turn_before</c>
/// до <c>battle_turn</c>; ходящий — <c>turn_after</c> после <c>battle_turn</c> (иначе последний
/// <c>turn_before</c>); действие — события до <c>turn_after</c>: цель из <c>dmg</c>/<c>attack_hit</c>/
/// <c>immune</c> (<c>always_only = false</c>), <c>potion_use</c> — предмет, <c>miss</c> без цели — удар без
/// цели; иначе по снимку <c>turn_after</c>: бит 31 — защита, новый бит 29 — бегство, один новый бит статуса —
/// цель, рост HP — лечение.
/// <para>Счётчики — «политика ИИ &lt;часть&gt; сошлось/разошлось»; тест падает только если не сверено ни
/// одного решения. Бои под принудительным автобоем зонда и без <c>battle_end</c> не берутся.</para></summary>
[Trait("Category", "Corpus")]
public class AiPolicyHarnessTests(ITestOutputHelper output)
{
    private sealed record Decision(long Seq, string Actor, IReadOnlyList<UnitState> Snapshot,
                                   IReadOnlyList<RngEvent> Trace, IReadOnlyList<CorpusEvent> Post,
                                   TurnEvent? After, long Round, IReadOnlyDictionary<string, UnitIdentity> Types);

    private sealed record Tally(SortedDictionary<string, (int Ok, int Bad)> Parts, SortedDictionary<string, int> Notes,
                                List<string> Diverged, List<string> Skipped, int Decisions);

    private static readonly Lazy<Tally> Result = new(Measure);

    [SkippableFact] public void AiPolicy_DecisionsMatchJournal()
    {
        var t = Result.Value;
        foreach (var (part, (o, b)) in t.Parts)
            output.WriteLine($"политика ИИ {part}: сошлось {o}, разошлось {b}");
        foreach (var (note, n) in t.Notes) output.WriteLine($"политика ИИ, справка — {note}: {n}");
        foreach (var line in t.Diverged.Take(60)) output.WriteLine("  расхождение: " + line);
        foreach (var line in t.Skipped.Take(60)) output.WriteLine("  пропуск: " + line);
        int ok = t.Parts.Values.Sum(p => p.Ok), bad = t.Parts.Values.Sum(p => p.Bad);
        Console.WriteLine($"[AI policy] decisions {t.Decisions}, matched {ok}, diverged {bad}, skipped {t.Skipped.Count}");
        Skip.If(t.Decisions == 0 && t.Skipped.Count == 0, "в корпусе нет боёв без принудительного автобоя");
        Assert.True(ok + bad > 0, "политика ИИ: не сверено ни одного решения");
    }

    private static Tally Measure()
    {
        Skip.IfNot(GamePaths.IsAvailable, "нет данных игры");
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var rules = RulesLoader.Load(GamePaths.Globals);
        var items = D2.Data.Magic.ItemLoader.Load(GamePaths.Globals);   // боевые предметы лидеров — из сохранений
        var tally = new Tally(new(StringComparer.Ordinal), new(StringComparer.Ordinal), [], [], 0);
        int decisions = 0;
        foreach (var file in JournalSet.Distinct(CorpusFiles.Journals()).Unique)
        {
            if (JournalCache.Read(file).OfType<StartEvent>().FirstOrDefault()?.Autobattle == true) continue;
            var saves = SaveSnapshots.Of(file);
            var marks = saves.Count == 0 ? [] : RouteCasts.TurnMarks(file);
            // Смены приказов до боя (событие сценария ставит «берсерк» посреди дня, И37) — своей загрузки карты: после последнего входа в
            // партию и тем же потоком, что бой (у журналов без game_phase загрузки разводит поток, как SaveSnapshots.At). Строки save
            // границей не служат: снимок окна — не обязательно последнее сохранение (human: «берсерк» seq 15533326, затем Q 15539872 без
            // копии в корпусе, бой 15601485).
            var orderLog = JournalCache.Read(file, ["ai_order", "game_phase"]).OrderBy(e => e.Seq).ToList();
            var bounds = orderLog.OfType<GamePhaseEvent>().Where(g => g.IsEnter).Select(e => e.Seq).ToList();
            var orderEvents = orderLog.OfType<AiOrderEvent>().ToList();
            foreach (var battle in JournalCache.Battles(file))
            {
                if (battle.End is null) continue;
                string name = $"{Path.GetFileName(file)} [{battle.Start.Seq}]";
                int turn = saves.Count == 0 ? 0 : RouteCasts.TurnAt(marks, battle.Start.Seq);
                long entered = bounds.LastOrDefault(s => s < battle.Start.Seq);
                var orders = orderEvents.Where(o => o.Seq > entered && o.Seq < battle.Start.Seq && o.Tid == battle.Start.Tid).ToList();
                if (!CorpusBattleLoader.TryLoad(battle, rules, out var state, out var reason, saves: saves, turn: turn, items: items, orders: orders))
                { tally.Skipped.Add($"{name}: {reason}"); continue; }
                Note(tally, $"сведения сторон для бегства (нападающий/защитник): {Known(state.AttackerSide)}/{Known(state.DefenderSide)}");
                foreach (var d in Decisions(battle, rules))
                {
                    decisions++;
                    Check(tally, name, state, rules.Constants, d);
                    MarkUsedItems(state, d);
                }
            }
        }
        return tally with { Decisions = decisions };
    }

    /// <summary>Предмет, применённый в этом решении (зелье — <c>potion_use</c> в бою; предмет с атакой — <c>attack_new</c>, чей
    /// <c>impl</c> не сам ходящий), помечается применённым у носителя (<see cref="BattleUnit.AppliedItems"/>, как
    /// <c>BattleSim.MarkUsed</c>): политика (<see cref="AiItems.Choose"/>) второй раз его не берёт. Карта 23 (20261020-port-synth) бой
    /// seq 9482868: лидер Нежити S143UN0028 выпил оба G000IG0006 (seq 9483079, 9483536), в раунде 3 у движка удар, а модель без
    /// пометки снова пила.</summary>
    private static void MarkUsedItems(BattleState state, Decision d)
    {
        if (state.Unit(d.Actor) is not { } bearer) return;
        var potion = d.Post.OfType<PotionUseEvent>().FirstOrDefault(p => p.InBattle && p.Ret);
        var itemAttack = potion is null ? d.Post.OfType<AttackNewEvent>().FirstOrDefault(n => Same(n.Attacker, d.Actor) && !Same(n.Impl, d.Actor)) : null;
        for (int i = 0; i < bearer.BattleItems.Count; i++)
        {
            if (bearer.AppliedItems.Contains(i)) continue;
            var item = bearer.BattleItems[i];
            if (potion is not null && Same(item.Id, potion.ItemType) || itemAttack is not null && Same(item.AttackId, itemAttack.Attack))
            { bearer.AppliedItems.Add(i); return; }
        }
    }

    /// <summary>Входы бегства для строки расхождения: сила, статус, сведения стороны и лидер.</summary>
    private static string Why(BattleState state, BattleUnit actor, RulesConstants c)
    {
        var side = state.SideOf(actor);
        var status = actor.Attacker ? state.AttackersRetreat : state.DefendersRetreat;
        return $" [rel {AiStrength.Relative(state, actor, c):F3}, {(actor.Attacker ? "нап" : "защ")}, статус {status}, " +
               $"решено {state.RetreatDecided}, лидер {actor.Type.IsLeader}, сторона {side}, " +
               $"ход нападающего {state.AttackerSide.Movement}, отступить можно {AiRetreat.CanRetreat(state, actor, false, false)}/" +
               $"{AiRetreat.CanRetreat(state, actor, true, true)}]";
    }

    private static string Known(BattleSide side) => side.Known ? "есть" : side.IsStack ? "нет" : "не отряд";

    private static IEnumerable<Decision> Decisions(D2.Data.Corpus.Battle battle, GameRules rules)
    {
        var events = battle.Events;
        IReadOnlyList<UnitState>? snap = null;
        var trace = new List<RngEvent>();
        string? lastBefore = null;
        var types = new Dictionary<string, UnitIdentity>(StringComparer.OrdinalIgnoreCase);
        var transformedAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Превращённые чужой атакой (attack_hit класса 22 с битом превращения после) — юнит → атака (И37).
        var morphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long typesSeq = -1;
        bool turned = false;   // был ли уже ход с решением (battle_turn) — первое battle_units без него и есть первый ход
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is BattleUnitsEvent bu)
            {
                foreach (var u in bu.Units) types[u.Id] = u;
                typesSeq = bu.Seq;
            }
            if (events[i] is AttackHitEvent { DispClass: (long)AttackClass.TransformOther } hit && (hit.StAfter & TransformBits) != 0)
                morphs[hit.Target] = hit.Attack;
            if (events[i] is BattleStartEvent or RoundEvent or TurnEvent)
            {
                TrackTransforms(events[i], transformedAt);
                foreach (var id in morphs.Keys.Where(k => !transformedAt.ContainsKey(k)).ToList()) morphs.Remove(id);
            }
            switch (events[i])
            {
                case BattleStartEvent s: snap = s.Units; break;
                case RoundEvent r: snap = r.Units; trace.Clear(); break;
                case TurnEvent { After: false } t: snap = t.Units; lastBefore = t.Unit; trace.Clear(); break;
                case TurnEvent t: snap = t.Units; lastBefore = null; trace.Clear(); break;
                case RngEvent g when !g.Std && g.Tid == battle.Start.Tid: trace.Add(g); break;
                // Первый ход боя battle_turn не пишет (окно от battle_units, C2HarnessTests.Recorded) — решение там же; без него
                // защитник с прикрытием решал не на своём первом ходе (20260919-human [12429316]: S073UN01e8 в seq 12429354 —
                // rel движка 0,3046, прикрытия нет; следом решавший S073UN0255 с rel 0,259 включал прикрытие).
                case BattleUnitsEvent when !turned && lastBefore is null && !HumanBefore(events, i):
                case BattleTurnEvent:
                    turned = true;
                    var post = events.Skip(i + 1).TakeWhile(e => e is not (TurnEvent { After: true } or BattleEndEvent or BattleStartEvent)).ToList();
                    int j = i + 1 + post.Count;
                    var after = j < events.Count ? events[j] as TurnEvent : null;
                    string? actor = after?.Unit ?? lastBefore;
                    long round = events[i] is BattleTurnEvent bt ? bt.Round : after?.Round ?? 1;
                    if (actor is not null && snap is not null)
                        yield return new Decision(events[i].Seq, actor, snap, [.. trace], post, after, round,
                                                  TypesNow(events, i, types, typesSeq, transformedAt, Kept(morphs, actor, after), rules));
                    trace.Clear();
                    break;
            }
        }
    }

    /// <summary>Перед первым battle_units — ввод человека (кнопка хода или щелчок по цели): ход решал не ИИ.</summary>
    private static bool HumanBefore(IReadOnlyList<CorpusEvent> events, int at) =>
        events.Take(at).Any(e => e is UiClickEvent { Element: "BTN_DEFEND" or "BTN_WAIT" or "BTN_RETREAT" } or InputEvent { Msg: 513 });

    private const long TransformBits = 0x1FL << 22;   // BattleStatus 22..26: Transform..TransformDrainLevel

    /// <summary>С какого события у юнита взведён бит превращения; снят (смерть откатывает тип) — забыт.</summary>
    private static void TrackTransforms(CorpusEvent e, Dictionary<string, long> transformedAt)
    {
        IReadOnlyList<UnitState> units = e switch
        {
            BattleStartEvent s => s.Units, RoundEvent r => r.Units, TurnEvent t => t.Units, _ => [],
        };
        foreach (var u in units)
        {
            if ((u.St & TransformBits) == 0) transformedAt.Remove(u.Id);
            else transformedAt.TryAdd(u.Id, e.Seq);
        }
    }

    /// <summary>Типы на момент решения. <c>battle_units</c> зонд пишет лениво: map-001 [944403] — Doppelganger
    /// превращается в 946399, снимок 946428 уже с битом 25, а новый тип — только в <c>battle_units</c> 946466.
    /// Поэтому юниту, превращённому позже последнего <c>battle_units</c>, берётся его первое опознание после
    /// решения (превращение в бою однократно, <c>0x6618e9</c>).</summary>
    private static Dictionary<string, UnitIdentity> TypesNow(IReadOnlyList<CorpusEvent> events, int at,
        Dictionary<string, UnitIdentity> types, long typesSeq, Dictionary<string, long> transformedAt,
        IReadOnlyDictionary<string, string> morphs, GameRules rules)
    {
        var now = new Dictionary<string, UnitIdentity>(types, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, since) in transformedAt)
        {
            if (typesSeq > since) continue;
            var later = events.Skip(at + 1).OfType<BattleUnitsEvent>().SelectMany(b => b.Units)
                              .FirstOrDefault(u => Same(u.Id, id));
            if (later is not null) now[id] = later;
            else if (Morph(id, types, morphs, rules) is { } morphed) now[id] = morphed;
        }
        return now;
    }

    /// <summary>Превращения на миг решения: у ходящего, с которого бит превращения снят его же ходом (живой), превращение спало в начале
    /// хода — бросок срока длинного эффекта <c>0x628821</c> (<c>BattleSim</c>, бит 23), и ходит он своим типом. Харнесс здесь
    /// смотрит в снимок после хода (<c>turn_after</c>): бросок срока журнал пишет, а состояние между ним и решением — нет.</summary>
    private static IReadOnlyDictionary<string, string> Kept(Dictionary<string, string> morphs, string? actor, TurnEvent? after)
    {
        if (actor is null || !morphs.ContainsKey(actor)
            || after?.Units.FirstOrDefault(u => Same(u.Id, actor)) is not { } me || me.Hp <= 0 || (me.St & TransformBits) != 0) return morphs;
        return morphs.Where(p => !Same(p.Key, actor)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Превращённый чужой атакой (TransformOther), когда зонд больше не пишет <c>battle_units</c>: тип — единственный кандидат
    /// <c>Gtransf</c> этой атаки по размеру исходного (<see cref="GameRules.SummonCandidates"/>, как <c>TransformAttacks.Morph</c>). 20260919-human
    /// [4934456]: <c>S073UN010c</c> превратил <c>S073UN019c</c> (G000AA0067 → G000UU6104, <c>attack_hit</c> seq 4934696), в раунде 2 срок
    /// держит (rng(100) = 55, seq 4934745), и тот бьёт атакой нового облика G000AA6004 (соседняя, 30) — меньшую HP 010b; с прежним
    /// типом (G000AA0058, 80) модель добивала 010c (реестр И37).</summary>
    private static UnitIdentity? Morph(string id, Dictionary<string, UnitIdentity> types, IReadOnlyDictionary<string, string> morphs, GameRules rules)
    {
        if (!morphs.TryGetValue(id, out var attack) || types.GetValueOrDefault(id)?.Type is not { } was
            || rules.ImplOf(was) is not { } impl) return null;
        return rules.SummonCandidates(attack, impl.Type.SizeSmall) is [var only] ? new UnitIdentity(id, only.Id, null, null) : null;
    }

    /// <summary>Тип, атаки и потолок HP — из опознания решения (превращения, понижение уровня).</summary>
    private static void Retype(BattleUnit u, UnitIdentity? id, GameRules? rules)
    {
        if (id?.Type is null || Same(u.Type.Id, id.Type) || rules?.ImplOf(id.Type) is not { } impl || impl.Attack is null) return;
        u.Type = impl.Type;
        u.Attack = impl.Attack;
        u.Attack2 = impl.Attack2;
        u.HpMax = (int)(id.HpMax ?? impl.Type.HitPoints);
    }

    /// <summary>Действие журнала: вид (attack/item/defend/retreat/wait/heal/other) и цель (<c>?</c> — не видна).</summary>
    private static (string Kind, string? Target) Actual(Decision d)
    {
        foreach (var e in d.Post)
        {
            switch (e)
            {
                case ItemEquipEvent { Equip: false, Apply: true }: return ("item", null);   // зелье до схемы potion_use
                case DamageEvent dm when Same(dm.Attacker, d.Actor) && dm.Target.Length > 0: return ("attack", dm.Target);
                case AttackHitEvent h when Same(h.Attacker, d.Actor) && h.Target.Length > 0: return ("attack", h.Target);
                case ImmuneEvent im when !im.AlwaysOnly && im.Unit.Length > 0: return ("attack", im.Unit);
                case PotionUseEvent: return ("item", null);
            }
        }
        if (d.Post.Any(e => e is MissEvent)) return ("attack", "?");
        return d.After is null ? ("other", null) : FromSnapshot(d);
    }

    private static (string Kind, string? Target) FromSnapshot(Decision d)
    {
        var before = d.Snapshot.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var after = d.After!.Units;
        var me = after.FirstOrDefault(u => Same(u.Id, d.Actor));
        if (me is null || !before.TryGetValue(d.Actor, out var mine)) return ("other", null);
        if (me.Defend) return ("defend", null);
        if (Bit(me.St, 29) && !Bit(mine.St, 29)) return ("retreat", null);
        if ((me.Flags & 16) != 0 && (mine.Flags & 16) == 0) return ("wait", null);
        const long mask = 0x1E0 | 0xC | (1 << 13);
        var gained = after.Where(u => before.TryGetValue(u.Id, out var b) && (u.St & ~b.St & mask) != 0).ToList();
        if (gained.Count == 1) return ("attack", gained[0].Id);
        var healed = after.Where(u => !Same(u.Id, d.Actor) && before.TryGetValue(u.Id, out var b) && u.Hp > b.Hp).ToList();
        if (healed.Count > 0) return ("heal", healed.Count == 1 ? healed[0].Id : "?");
        return me.Hp > mine.Hp ? ("heal", d.Actor) : ("other", null);
    }

    private static void Check(Tally tally, string name, BattleState state, RulesConstants c, Decision d)
    {
        var unknown = d.Snapshot.FirstOrDefault(s => state.Unit(s.Id) is null);
        if (unknown is not null) { Note(tally, "юнит вне состояния (призван в бою)"); return; }
        foreach (var s in d.Snapshot)
        {
            Retype(state.Unit(s.Id)!, d.Types.GetValueOrDefault(s.Id), state.Rules);
            Restore(state.Unit(s.Id)!, s);
        }
        // Снимок turn_before ходящего — до отложенной части «Исцеления» (0x628874 в начале обработки хода, BattleSim.BeginTurn): у
        // юнита с битом Cured паралич к решению уже снят. Карта 23 (20261020-port-synth) бой seq 6629119: S143UN00bf исцелил
        // парализованного S143UN00ff (st 644 → 2097796, бит 21), следом S143UN00ba (усиление уровня 3 и Cure) — у движка защита: усиление
        // уровня 3 на S143UN00ff уже лежит, лечить нечего; без снятия модель лечила паралич.
        Attacks.ResolveCured(state);
        state.Round = (int)d.Round;
        var actor = state.Unit(d.Actor)!;
        var (kind, target) = Actual(d);
        var rng = new TraceRng(d.Trace);
        AiDecision pred;
        try { pred = BattleAi.Choose(state, actor, rng, c); }
        catch (NotSupportedException ex) { Note(tally, ex.Message); return; }

        Count(tally, "ничьи rng(2)", rng.Asked == rng.Recorded);
        string? part = kind switch
        {
            "attack" or "heal" when Blind(actor, kind, target) => "удар без видимой цели",
            "attack" or "heal" => $"цель {actor.Attack.Class}",
            "defend" => "защита",
            "retreat" => "бегство",
            "item" => "предметы",
            _ => null,
        };
        if (part is null) { Note(tally, $"действие {kind}"); return; }
        bool ok = kind switch
        {
            "defend" => pred.Action == AiAction.Defend,
            "retreat" => pred.Action == AiAction.Retreat,
            "item" => pred.Action == AiAction.UseItem,
            _ when Blind(actor, kind, target) => pred.Action == AiAction.Attack,
            _ => pred.Action == AiAction.Attack && Same(pred.Target?.Id, target),
        };
        Count(tally, part, ok);
        if (!ok) tally.Diverged.Add($"{name} seq {d.Seq} раунд {d.Round} {d.Actor} ({actor.Attack.Class}): " +
                                    $"журнал {kind} {target}, модель {pred.Action} {pred.Target?.Id}" +
                                    (kind is "retreat" or "defend" || pred.Action is AiAction.Retreat or AiAction.Defend ? Why(state, actor, c) : ""));
    }

    /// <summary>Цель не сравнивается: журнал её не показал, атака площадная, призыв (цель — позиция),
    /// самопревращение (запасная атака площадная — первая цель в журнале случайна) или «лечение» у не-лекаря
    /// (рост HP — превращение или дрейн, а не цель).</summary>
    private static bool Blind(BattleUnit actor, string kind, string? target) =>
        target == "?" || actor.Attack.Reach == AttackType.ReachAll ||
        actor.Attack.Class is AttackClass.Summon or AttackClass.TransformSelf ||
        kind == "heal" && actor.Attack.Class != AttackClass.Heal;

    private static void Note(Tally tally, string what) => tally.Notes[what] = tally.Notes.GetValueOrDefault(what) + 1;

    private static void Count(Tally tally, string part, bool ok)
    {
        var (o, b) = tally.Parts.GetValueOrDefault(part);
        tally.Parts[part] = ok ? (o + 1, b) : (o, b + 1);
    }

    /// <summary>Статусы юнита из снимка журнала (биты <c>BattleStatus</c>, battlemsgdata.h). Урон DoT снимок не
    /// несёт — ponytail: эффект кладётся с уроном 0 (<c>canPerform</c> «слабый яд на сильный» не сработает).</summary>
    private static void Restore(BattleUnit u, UnitState s)
    {
        u.Hp = (int)s.Hp;
        u.ShatteredArmor = (int)s.Shattered;
        u.FortificationArmor = (int)s.Fort;
        u.Paralyzed = Bit(s.St, 2); u.Petrified = Bit(s.St, 3);
        u.BoostDamageLevel = s.BoostDamageLevels.DefaultIfEmpty(0).Max(); u.BoostDamageLong = Bit(s.St, 9);
        u.LowerDamageLevel = s.LowerDamageLevels.DefaultIfEmpty(0).Max(); u.LowerDamageLong = Bit(s.St, 12);
        u.LowerInitiativeLevel = Bit(s.St, 13) ? 1 : 0; u.LowerInitiativeLong = Bit(s.St, 14);
        u.Cured = Bit(s.St, 21);
        u.Transformed = Bit(s.St, 22); u.TransformedLong = Bit(s.St, 23); u.TransformedSelf = Bit(s.St, 24);
        u.TransformedDoppelganger = Bit(s.St, 25); u.LevelDrained = Bit(s.St, 26);
        u.Summoned = Bit(s.St, 27); u.Retreated = Bit(s.St, 28); u.Retreating = Bit(s.St, 29);
        u.Defending = Bit(s.St, 31);
        u.Revived = (s.Flags & 64) != 0;   // UnitFlags бит 6 (+0x30), 0x623633
        u.Dots.Clear();
        foreach (var (bit, cls) in new[] { (15, AttackClass.Poison), (17, AttackClass.Frostbite), (19, AttackClass.Blister) })
            if (Bit(s.St, bit)) u.Dots.Add(new DotEffect { Class = cls, Damage = 0, AppliedRound = 0, Long = Bit(s.St, bit + 1) });
    }

    private static bool Bit(long st, int bit) => (st & (1L << bit)) != 0;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Броски окна решения по величине: модель спрашивает <c>rng(n)</c> — берётся следующий журнальный
    /// <c>rng(n)</c>; кончились — −1 (ничья не выпадает), как <c>next(it, -1)</c> в evalai.py.</summary>
    private sealed class TraceRng(IReadOnlyList<RngEvent> trace) : IRng
    {
        private readonly Dictionary<long, Queue<int>> _draws = trace.GroupBy(e => e.Max)
            .ToDictionary(g => g.Key, g => new Queue<int>(g.Select(e => (int)e.R)));

        public int Recorded { get; } = trace.Count(e => e.Max == 2);
        public int Asked { get; private set; }

        public int Next(int maxValue)
        {
            if (maxValue == 2) Asked++;
            return _draws.TryGetValue(maxValue, out var q) && q.Count > 0 ? q.Dequeue() : -1;
        }
    }
}
