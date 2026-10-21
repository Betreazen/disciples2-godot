using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Rules.Tests;

/// <summary>Отряд-вор, опознанный по сохранениям каталога: тип лидера и его полный запас хода.</summary>
internal sealed record ThiefStack(string StackId, string LeaderType, int Move);

/// <summary>Списание очков хода за действие вора (схема 6.0): <c>ap_effect</c> с причиной и его <c>ap_apply</c>.</summary>
internal sealed record ThiefCharge(ApEffectEvent Made, ApApplyEvent Applied);

/// <summary>То, что задаче 12 нужно из журнала: броски вора, дневные тики, ходы отрядов, а в схеме 6.0 —
/// списания за действия вора (<see cref="Charges"/>) и кто действовал (<see cref="Actions"/>). Журнал
/// партии весит 51 МБ, поэтому читается один раз и только этими видами событий.</summary>
/// <param name="ApSeen">В журнале есть <c>ap_apply</c> — списания видны прямо, цепочка <c>stack_move</c> не нужна.</param>
internal sealed record ThiefJournal(string Path, IReadOnlyList<ThiefRollEvent> Rolls,
                                    IReadOnlyList<IncomeEvent> Incomes, IReadOnlyList<StackMoveEvent> Moves,
                                    IReadOnlyList<ThiefCharge> Charges, IReadOnlyList<NobleActionEvent> Actions,
                                    bool ApSeen)
{
    /// <summary>Причины <c>ap_effect</c>, которые платит вор (<c>docs/corpus-schema.md</c>: <c>0x4431a5</c>,
    /// <c>0x41d8ca</c>, <c>0x443591</c>, <c>0x442fb1</c>).</summary>
    public static readonly IReadOnlySet<string> ThiefCauses =
        new HashSet<string>(StringComparer.Ordinal) { "thief_roll", "duel", "thief_cancel", "thief_no_actions" };

    public static ThiefJournal Load(string path)
    {
        List<ThiefRollEvent> rolls = [];
        List<IncomeEvent> incomes = [];
        List<StackMoveEvent> moves = [];
        List<ThiefCharge> charges = [];
        List<NobleActionEvent> actions = [];
        // Адрес объекта переиспользуется, но ap_effect всегда раньше своего ap_apply — берётся последний.
        var pending = new Dictionary<string, ApEffectEvent>(StringComparer.Ordinal);
        bool apSeen = false;
        foreach (var e in JournalCache.Read(path))
            switch (e)
            {
                case ThiefRollEvent r: rolls.Add(r); break;
                case IncomeEvent i when i.Turn is not null: incomes.Add(i); break;
                case StackMoveEvent m when m.Ret: moves.Add(m); break;
                case NobleActionEvent n: actions.Add(n); break;
                case ApEffectEvent a: pending[a.Effect] = a; break;
                case ApApplyEvent a:
                    apSeen = true;
                    if (pending.TryGetValue(a.Effect, out var effect) && ThiefCauses.Contains(effect.Cause))
                        charges.Add(new ThiefCharge(effect, a));
                    break;
            }
        incomes.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        moves.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        charges.Sort((x, y) => x.Applied.Seq.CompareTo(y.Applied.Seq));
        actions.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        return new ThiefJournal(path, rolls, incomes, moves, charges, actions, apSeen);
    }
}

/// <summary>
/// Разбор корпуса для задачи 12 Ф7 — броски вора, порог, гибель и цена действия в очках хода.
///
/// <para><b>Где смотреть.</b> Броски даёт событие <c>thief_roll</c> (28 строк на весь корпус, из них
/// 11 — схемы 5.1 с полями <c>action</c>/<c>dice1</c>/<c>dice2</c>/<c>chance</c>). Порог и кубики —
/// прямо из строки, поэтому <c>thief_roll_exact</c> и <c>thief_two_draws</c> считаются по журналу без
/// сохранений. Порог модели (<c>thief_chance</c>) и список действий (<c>thief_gate</c>) требуют
/// снимка: сопротивление цели — это <c>NEGOTIATE</c> её лидера или <c>MORALE</c> её уровня, а гейт —
/// лорд игрока. Гибель (<c>thief_death</c>) видна следующим снимком.</para>
///
/// <para><b>Кто вор, событие не говорит:</b> поле <c>stack</c>/<c>target</c> — это МИШЕНЬ (П§12.5).
/// Вор восстанавливается двумя шагами. Во-первых, по сохранениям каталога известно, какие отряды —
/// воры (лидер с <c>Gunits.LEADER_CAT = 5</c>, <see cref="Thieves.LeaderCategory"/>); у id бывает
/// несколько вариантов, потому что у синтетических карт одного каталога общий префикс и
/// <c>S143KC0003</c> на соседней карте — другой отряд, поэтому нужный вариант выбирается по запасу
/// хода (<see cref="Chain"/>). Во-вторых, действовал тот вор, чей ход (<c>stack_move</c>) в журнале
/// ближе всех предшествует броску: вор доходит до цели и тут же действует. На <c>m13-thief</c>
/// правило называет <c>KC000c</c> — того самого, кого П§12.5 назвал погибшим.</para>
/// </summary>
internal sealed class ThiefAudit(ProgressBook book, Counters c)
{
    /// <summary>
    /// Разброс <c>seq</c> внутри одной серии <c>setPosition</c>. Ходьба пишется серией строк
    /// (последняя несёт остаток, предыдущие — число ДО списания: задача 11, <c>leader_move_max</c>
    /// 99/0), и между соседними строками серии в <c>seq</c> попадают чужие события — <c>move_cost</c>,
    /// <c>path</c>, <c>can_move</c>. Наблюдённый разброс внутри серии — до <b>104</b>
    /// (<c>passive-004</c>, <c>S073KC0076</c> 4171368 → 4171472), а списание за действие приходит
    /// минимум через <b>2247</b> (<c>m13-thief</c> <c>S143KC0001</c> 168191 → 170438; там же 7323 и
    /// 38008). 1024 лежит на порядок выше первого и на порядок ниже второго.
    /// </summary>
    private const long BurstSeq = 1024;

    private readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ThiefStack>>> _thievesByDir =
        new(StringComparer.OrdinalIgnoreCase);
    private int _rolls, _v51, _failures, _thiefDays;
    private readonly List<string> _observed = [], _charges = [], _probes = [];
    private readonly Dictionary<string, int> _actions = new(StringComparer.Ordinal);

    // ---- броски: два кубика и точный предикат -------------------------------------------------------

    /// <summary><c>thief_two_draws</c> — <c>rng_after − rng_before = 2</c> у каждой строки (два вызова
    /// <c>rand(100)</c> всегда, даже при пороге 100); <c>thief_roll_exact</c> — <c>(d1 + d2) &gt;&gt; 1
    /// ≤ chance</c> совпадает с <c>ret</c>. Обе сверки — по журналу, сохранений не требуют.</summary>
    public void Draws(ThiefJournal j)
    {
        string name = Path.GetFileName(j.Path);
        foreach (var r in j.Rolls)
        {
            _rolls++;
            if (!r.Ret) _failures++;
            string kind = r.Action is { } a ? ((ThiefAction)a).ToString() : "схема 5 — действие не записано";
            _actions[kind] = _actions.GetValueOrDefault(kind) + 1;
            c.Count("thief_two_draws", r.RngAfter - r.RngBefore == 2,
                    $"{name} seq {r.Seq}: rng {r.RngBefore} → {r.RngAfter}");
            if (r.Chance is not { } chance || r.Dice1 is not { } d1 || r.Dice2 is not { } d2)
            { c.Skip("thief_roll_exact", "строка схемы 5 — кубиков и порога зонд не писал"); continue; }
            if (chance < 0 || d1 < 0 || d2 < 0)
            { c.Skip("thief_roll_exact", "−1 в кубике или пороге — величина не досталась зонду"); continue; }
            _v51++;
            var roll = new ThiefRoll((int)chance, (int)d1, (int)d2);
            c.Count("thief_roll_exact", roll.Success == r.Ret,
                    $"{name} seq {r.Seq}: ({d1} + {d2}) >> 1 = {roll.Average} против порога {chance} — " +
                    $"модель {roll.Success}, журнал {r.Ret}");
            _observed.Add($"{name} seq {r.Seq} действие {(ThiefAction)r.Action!.Value} цель {r.Target} " +
                          $"{d1}+{d2}→{roll.Average} порог {chance} {(r.Ret ? "успех" : "провал")}");
        }
    }

    // ---- порог и список действий --------------------------------------------------------------------

    /// <summary><c>thief_chance</c> и <c>thief_gate</c>: порог модели против поля <c>chance</c> и
    /// действие против нашего списка для категории лорда и вида цели.</summary>
    public void Thresholds(TreasuryJournal j)
    {
        string name = Path.GetFileName(j.Path);
        var rolls = j.Events.OfType<ThiefRollEvent>().OrderBy(x => x.Seq).ToList();
        var actions = j.Events.OfType<NobleActionEvent>().OrderBy(x => x.Seq).ToList();
        var leaderXp = j.Events.OfType<XpEvent>().Where(x => x.Leader == true && x.Level is not null).OrderBy(x => x.Seq).ToList();
        for (int k = 0; k < rolls.Count; k++)
        {
            var r = rolls[k];
            long nextRoll = k + 1 < rolls.Count ? rolls[k + 1].Seq : long.MaxValue;
            if (r.Action is not { } action)
            { c.Skip("thief_chance", "строка схемы 5 — действия зонд не писал"); continue; }
            if (j.SaveNear(r.T) is not { } save)
            { c.Skip("thief_chance", "у журнала нет сохранений в манифесте"); continue; }
            if (TargetOf(save, r.Target, LeaderLevels(leaderXp, save, r.Seq)) is not { } target)
            { c.Skip("thief_chance", $"цели {r.Target} нет в сохранении рядом с броском"); continue; }

            int ours = Thieves.Chance(book.ThiefRules, (ThiefAction)action, target.Resistance);
            if (r.Chance is { } chance && chance >= 0)
                c.Count("thief_chance", ours == chance,
                        $"{name} seq {r.Seq} действие {(ThiefAction)action} по {r.Target} ({target.Kind}, " +
                        $"сопротивление {target.Resistance}): журнал {chance}, модель {ours}");
            else c.Skip("thief_chance", "строка схемы 5.1 без порога (−1) — величина не досталась зонду");

            Gate(name, save, r, (ThiefAction)action, target, ActorOf(actions, r.Seq, nextRoll, r));
        }
    }

    /// <summary><c>thief_gate</c>: действие сверяется со списком для категории лорда того, чей вор бросал. Актёр — из
    /// <c>noble_action</c> того же броска (схема 6.0: игрок отряда-вора); в журналах без неё все броски — человека (до
    /// 16.10 у ИИ в корпусе бросков нет), и категория берётся у человека снимка. 16.10, <c>20261016-port-synth</c>
    /// passive-002 (карта 4): воры ИИ бросают сами — у Нежити (лорд-правитель) «Подложный приказ», а человек там — маг,
    /// поэтому прежняя догадка «бросал человек» давала ложное расхождение. Мишень своя у актёра — пропуск с причиной.</summary>
    private void Gate(string name, SaveAt save, ThiefRollEvent r, ThiefAction action, ThiefTarget target, string? actor)
    {
        var human = save.Map.Players.Values.FirstOrDefault(p => p.IsHuman);
        var player = actor is null ? human : save.Map.Players.Values.FirstOrDefault(p => JournalSaves.Same(p.Id, actor));
        if (player is null) { c.Skip("thief_gate", actor is null ? "в сохранении нет игрока-человека" : $"игрока {actor} нет в сохранении"); return; }
        if (JournalSaves.Same(target.Owner, player.Id))
        { c.Skip("thief_gate", "мишень принадлежит самому действующему — чей это вор, из журнала не следует"); return; }
        if (!book.Prices.Treasury.LordCategory.TryGetValue(player.LordId, out int category))
        { c.Skip("thief_gate", $"лорда {player.LordId} нет в Glord.dbf"); return; }

        var lord = (LordCategory)category;
        var list = Thieves.Actions(lord, target.Kind, target.Playable);
        c.Count("thief_gate", list.Contains(action),
                $"{name} seq {r.Seq}: {action} по {target.Kind} (раса играбельна: {target.Playable}) " +
                $"при лорде {player.LordId} ({lord}, {player.Id}); наш список — {string.Join(",", list)}");
    }

    /// <summary>Игрок, чей вор сделал бросок <paramref name="r"/>: первая после броска строка <c>noble_action</c> с той же
    /// мишенью и действием, пока не начался следующий бросок. null — такой строки нет (журнал до схемы 6.0 или действие
    /// отменено).</summary>
    private static string? ActorOf(IReadOnlyList<NobleActionEvent> actions, long rollSeq, long nextRollSeq, ThiefRollEvent r)
    {
        foreach (var n in actions)
        {
            if (n.Seq <= rollSeq) continue;
            if (n.Seq >= nextRollSeq) return null;
            if (n.Action == r.Action && string.Equals(n.Target, r.Target, StringComparison.OrdinalIgnoreCase)) return n.Player;
        }
        return null;
    }

    /// <summary>Мишень броска глазами сохранения: вид, сопротивление, владелец и играбельность его
    /// расы. null — id в сохранении не нашёлся.</summary>
    private ThiefTarget? TargetOf(SaveAt save, string id, IReadOnlyDictionary<string, int>? levels = null)
    {
        if (save.Map.Stacks.TryGetValue(id, out var stack))
        {
            if (!save.Map.Units.TryGetValue(stack.LeaderId, out var leader)) return null;
            int level = levels?.GetValueOrDefault(leader.Id, leader.Level) ?? leader.Level;
            if (book.NegotiateOf(leader.TypeId, level) is not { } negotiate) return null;
            return new ThiefTarget(ThiefTargetKind.Stack, Thieves.StackResistance(negotiate),
                                   stack.Owner, Playable(save, stack.Owner));
        }
        var fort = save.Sites.FirstOrDefault(s => JournalSaves.Same(s.Id, id)
                                                  && s.Kind is SiteKind.Capital or SiteKind.Village);
        if (fort is null) return null;
        return new ThiefTarget(ThiefTargetKind.Fortification, Thieves.FortResistance(book.ThiefRules, fort),
                               fort.Owner, Playable(save, fort.Owner));
    }

    /// <summary>Уровни лидеров на миг броска: снимок до броска (<see cref="TreasuryJournal.SaveBefore"/>), затем строки <c>xp</c> лидеров
    /// журнала между ним и броском — последняя строка лидера даёт его уровень, а дошедшая до <c>xp_next</c> ниже <c>max_leader</c> —
    /// уровень выше (переход уровня лидера, <c>Experience.LevelUpLeader</c>). Сопротивление отряда растёт с уровнем лидера
    /// (<c>GDynUpgr.NEGOTIATE</c> +5): карта 23 (<c>20261020-port-synth</c>) — лидер KC0006 S143UN0014 G000UU0047 дневным читом 119 → 120 =
    /// XP_NEXT (seq 3297806), уровень 2, и бросок Нежити seq 3369405 «Убийство» с порогом 55 = 70 − 15, а не 60 по снимку дня.</summary>
    private Dictionary<string, int>? LeaderLevels(IReadOnlyList<XpEvent> leaderXp, SaveAt save, long rollSeq)
    {
        if (save.Seq is not { } from || from > rollSeq) return null;
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in leaderXp)
        {
            if (x.Seq >= rollSeq) break;
            if (x.Seq <= from || x.Level is not { } level) continue;
            // Вор (L_NOBLE) и прочие без опыта (0x5d8f84) уровня не берут — стоят на xp_next (карта 23: G000UU0048 120 → 120).
            bool gains = x.Impl is null || book.CategoryOf(x.Impl) is not { } category || Experience.GainsXp(category);
            bool up = gains && x.XpAfter is { } after && x.XpNext is { } next && next > 0 && after >= next
                      && (x.MaxLeader is not { } max || level < max);
            levels[x.Unit] = (int)level + (up ? 1 : 0);
        }
        return levels;
    }

    /// <summary>Играбельна ли раса владельца (<c>Grace.PLAYABLE</c>; у нейтрала <c>g000rr0004</c> —
    /// «нет»). Владельца нет в снимке — считаем нейтралом: <c>0x5E5358</c> на пустой ссылке тоже
    /// отвечает «не играбельна».</summary>
    private bool Playable(SaveAt save, string owner) =>
        save.Map.Players.TryGetValue(owner, out var p)
        && book.Prices.Economy.Races.TryGetValue(p.RaceId, out var race) && race.Playable;

    // ---- гибель вора ---------------------------------------------------------------------------------

    /// <summary><c>thief_death</c>: после провала действовавшего вора в СЛЕДУЮЩЕМ сохранении нет.
    /// Действовавший — тот, чей ход в журнале ближе всех предшествует броску. Рядом печатается
    /// строка-гипотеза «успех вора не убивает»: в приёмку она не входит, потому что между броском и
    /// снимком бывают бои, и исчезновение вора там объясняется не броском.</summary>
    public void Deaths(TreasuryJournal saves, ThiefJournal j)
    {
        var thieves = ThievesOfDir(Path.GetDirectoryName(j.Path)!);
        string name = Path.GetFileName(j.Path);
        foreach (var r in j.Rolls)
        {
            string counter = r.Ret ? "thief_death · гипотеза «успех вора не убивает»" : "thief_death";
            if (Actor(j, thieves, r) is not { } actor)
            { c.Skip(counter, "действовавшего вора не назвать: перед броском нет хода отряда-вора"); continue; }
            if (saves.SaveAfter(r.T) is not { } after)
            { c.Skip(counter, "после броска нет сохранения в манифесте"); continue; }
            // Успешный вор бывает убит ПОСЛЕ броска в бою: Дуэль сама переходит в бой (20260915 seq 5309785 →
            // battle_end 5310214, победил S143KC0028), а отряд-мишень может напасть на вора (20260910 passive-004
            // seq 4716844 → battle_end 4790664, победил S073KC0070). Это не исход броска.
            if (r.Ret && LostBattle(saves, actor.StackId, r.T, after.T) is { } lost)
            {
                c.Skip(counter, "вор проиграл бой между броском и снимком — исчезновение объяснено боем");
                _probes.Add($"гибель {name} seq {r.Seq}: вор {actor.StackId} проиграл бой " +
                            $"(battle_end seq {lost.Seq}, победил {lost.Winner})");
                continue;
            }
            bool alive = after.Map.Stacks.ContainsKey(actor.StackId);
            string detail = $"{name} seq {r.Seq} по {r.Target}: вор {actor.StackId} ({actor.LeaderType}) в " +
                            $"{Path.GetFileName(after.Path)} {(alive ? "жив" : "исчез")}";
            c.Count(counter, r.Ret ? alive : !alive, detail);
        }
    }

    /// <summary>Кто действовал. Схема 6.0 — <c>noble_action.stack</c> (ранг 1: строка лежит сразу за своим
    /// <c>thief_roll</c>, та же мишень). Раньше — отряд-вор, чей <c>stack_move</c> ближе всех предшествует броску.</summary>
    private static ThiefStack? Actor(ThiefJournal j, IReadOnlyDictionary<string, IReadOnlyList<ThiefStack>> thieves,
                                     ThiefRollEvent roll)
    {
        var action = j.Actions.FirstOrDefault(a => a.Seq > roll.Seq);
        if (action is not null && JournalSaves.Same(action.Target, roll.Target)
            && !j.Rolls.Any(r => r.Seq > roll.Seq && r.Seq < action.Seq))
            return thieves.TryGetValue(action.Stack, out var own) ? own[0] : null;
        for (int i = j.Moves.Count - 1; i >= 0; i--)
        {
            if (j.Moves[i].Seq >= roll.Seq) continue;
            if (thieves.TryGetValue(j.Moves[i].Stack, out var variants)) return variants[0];
        }
        return null;
    }

    /// <summary>Бой, который отряд <paramref name="stack"/> проиграл во времени (<paramref name="from"/>,
    /// <paramref name="to"/>]: участники — из последней <c>battle_start</c> перед <c>battle_end</c>, проиграл —
    /// участник, не названный победителем. null — такого боя нет.</summary>
    private static BattleEndEvent? LostBattle(TreasuryJournal saves, string stack, long from, long to)
    {
        BattleStartEvent? start = null;
        foreach (var e in saves.Events)
        {
            if (e is BattleStartEvent s) { start = s; continue; }
            if (e is not BattleEndEvent { T: { } t } end || t <= from || t > to || start is null) continue;
            bool took = JournalSaves.Same(start.AttackerGroup, stack) || JournalSaves.Same(start.DefenderGroup, stack);
            if (took && !JournalSaves.Same(end.Winner, stack)) return end;
        }
        return null;
    }

    // ---- цена действия в очках хода ------------------------------------------------------------------

    /// <summary>
    /// <c>thief_move_cost</c>: списания очков хода у воров равны <c>ceil(MOVE / 2)</c> с полом нуля.
    ///
    /// <para><b>Как читается цепочка.</b> Внутри дня настоящий остаток не растёт, а строки
    /// <c>stack_move</c> пишут ещё и число ДО списания: у первой строки серии это установлено
    /// задачей 11 (<c>leader_move_max</c> 99/0), а в <c>m13-thief</c> то же делает поток окна (после
    /// 21 идут две строки с 25 и другим <c>tid</c>). Поэтому строка с выросшим остатком отбрасывается,
    /// а падение относится к ходьбе или к действию по <c>seq</c>: внутри серии
    /// <c>setPosition</c> строки идут через 2…7 <c>seq</c> (<see cref="BurstSeq"/>), а списание за
    /// действие приходит через тысячи. Отмена действия стоит столько же и от выполнения в журнале не
    /// отличается.</para>
    /// <para>Цепочка — только для журналов до схемы 6.0. В 6.0 списание видно прямо (<see cref="DirectCharges"/>),
    /// а цепочка там врёт: окно дня не знает загрузок карт, вор выбирается по совпадению запаса (разбор 17.09,
    /// 20260916-synth: 7 ложных «25 → 20» — строки разных карт или не-вор с тем же id, и 2 ложных «сошлось»
    /// 19 → 6 — это <c>ap_effect cause attack</c>).</para>
    /// </summary>
    public void MoveCosts(ThiefJournal j, TreasuryJournal? saves)
    {
        if (j.ApSeen) { DirectCharges(j, saves); return; }
        if (j.Moves.Count == 0) return;
        var thieves = ThievesOfDir(Path.GetDirectoryName(j.Path)!);
        if (thieves.Count == 0)
        { c.Skip("thief_move_cost", "в каталоге журнала нет сохранений с ворами"); return; }

        string name = Path.GetFileName(j.Path);
        // Окна дней РАЗНЫХ игроков пересекаются (доход ИИ приходит внутри дня человека), и одно и то
        // же падение попало бы в счётчик по разу на окно — пять раз на наблюдение в первой большой
        // партии. Поэтому каждая строка учитывается один раз, по её seq.
        var counted = new HashSet<long>();
        foreach (var income in j.Incomes)
        {
            long end = j.Incomes.FirstOrDefault(e => e.Seq > income.Seq && JournalSaves.Same(e.Player, income.Player))
                                ?.Seq ?? long.MaxValue;
            foreach (var day in j.Moves.Where(m => m.Seq > income.Seq && m.Seq < end)
                                       .GroupBy(m => m.Stack, StringComparer.OrdinalIgnoreCase))
            {
                if (!thieves.TryGetValue(day.Key, out var variants)) continue;
                _thiefDays++;
                Chain(name, variants, [.. day], counted);
            }
        }
    }

    /// <summary>Разбор одной цепочки <c>move_left</c> отряда-вора за день. Вариант вора выбирается по
    /// запасу первой строки дня: он и есть полный запас (задача 11), поэтому равенство ему — и опознание
    /// карты, и проверка, что окно начинается с начала дня.</summary>
    private void Chain(string journal, IReadOnlyList<ThiefStack> variants, IReadOnlyList<StackMoveEvent> day,
                       HashSet<long> counted)
    {
        int prev = (int)day[0].MoveLeft;
        if (variants.FirstOrDefault(v => v.Move == prev) is not { } thief)
        {
            c.Skip("thief_move_cost", "запас первой строки дня не равен MOVE ни одного вора с этим id — " +
                                      "день начался не с него или в этот момент загружена другая карта");
            return;
        }
        int cost = Thieves.MoveCost(thief.Move);
        for (int i = 1; i < day.Count; i++)
        {
            int left = (int)day[i].MoveLeft;
            if (left >= prev) continue;                      // строка с числом ДО списания
            bool fresh = counted.Add(day[i].Seq);
            if (day[i].Seq - day[i - 1].Seq <= BurstSeq)
            {
                if (fresh) c.Skip("thief_move_cost", "падение внутри серии setPosition — это ходьба");
                prev = left;
                continue;
            }
            if (!fresh) { prev = left; continue; }
            string detail = $"{journal} {thief.StackId} ({thief.LeaderType} MOVE {thief.Move}): {prev} → " +
                            $"{left} отдельной строкой seq {day[i - 1].Seq} → {day[i].Seq}, ждали " +
                            $"ceil({thief.Move} / 2) = {cost} с полом (то есть {Math.Min(cost, prev)})";
            _charges.Add(detail);
            c.Count("thief_move_cost", prev - left == Math.Min(cost, prev), detail);
            prev = left;
        }
    }

    /// <summary>
    /// Схема 6.0: <c>thief_move_cost</c> по паре <c>ap_effect</c>/<c>ap_apply</c> с причиной вора — правило порта
    /// по причине: бросок и Дуэль — <see cref="Thieves.MoveCost"/> со <see cref="Thieves.Spend"/>, отмена и пустой
    /// список — <see cref="Thieves.CancelCost"/> с <see cref="Thieves.Cancel"/>. <c>thief_no_actions_twice</c> —
    /// за <c>thief_no_actions</c> того же вора сразу идёт <c>thief_cancel</c>, и итог равен
    /// <see cref="Thieves.NoActions"/> (C-thieves.md, 15/0). MOVE вора — из снимка той же загрузки карты
    /// (<see cref="TreasuryJournal.SaveNear"/>): у синтетических карт общий префикс id, и <c>S143KC0027</c> —
    /// вор с MOVE 25 на ai-q11 и 30 на другой карте каталога.
    /// </summary>
    private void DirectCharges(ThiefJournal j, TreasuryJournal? saves)
    {
        string name = Path.GetFileName(j.Path);
        for (int i = 0; i < j.Charges.Count; i++)
        {
            var (made, applied) = j.Charges[i];
            if (saves?.SaveNear(applied.T) is not { } save)
            { c.Skip("thief_move_cost", "журнал без казны или без сохранений в манифесте — MOVE вора не взять"); continue; }
            if (ThiefIn(save, applied.Stack) is not { } thief)
            { c.Skip("thief_move_cost", "отряда нет среди воров в снимке той же загрузки карты"); continue; }

            int before = (int)applied.MoveBefore;
            bool cancel = made.Cause is "thief_cancel" or "thief_no_actions";
            int cost = cancel ? Thieves.CancelCost(thief.Move) : Thieves.MoveCost(thief.Move);
            int left = cancel ? Thieves.Cancel(thief.Move, before) : Thieves.Spend(before, cost);
            string detail = $"{name} seq {applied.Seq} {made.Cause} {thief.StackId} ({thief.LeaderType} MOVE " +
                            $"{thief.Move}): delta {applied.Delta?.ToString() ?? "null"}, {before} → {applied.MoveAfter}; " +
                            $"ждали {cost}, {before} → {left}";
            _charges.Add(detail);
            c.Count("thief_move_cost", applied.Delta == cost && applied.MoveAfter == left, detail);

            if (made.Cause != "thief_no_actions") continue;
            var next = i + 1 < j.Charges.Count ? j.Charges[i + 1] : null;
            bool paired = next is { Made.Cause: "thief_cancel" } && JournalSaves.Same(next.Applied.Stack, applied.Stack);
            c.Count("thief_no_actions_twice",
                    paired && next!.Applied.MoveAfter == Thieves.NoActions(thief.Move, before),
                    $"{name} seq {applied.Seq} {thief.StackId}: пустой список с {before}, следом " +
                    (paired ? $"thief_cancel → {next!.Applied.MoveAfter}" : "нет thief_cancel того же вора") +
                    $", ждали {Thieves.NoActions(thief.Move, before)}");
        }
    }

    /// <summary>Вор <paramref name="stackId"/> в снимке: тип лидера и полный запас; null — не вор или нет отряда.</summary>
    private ThiefStack? ThiefIn(SaveAt save, string stackId)
    {
        if (!save.Map.Stacks.TryGetValue(stackId, out var s) || !save.Map.Units.TryGetValue(s.LeaderId, out var u))
            return null;
        if (book.LeaderCategoryOf(u.TypeId) != Thieves.LeaderCategory) return null;
        return book.Grown(u.TypeId, u.Level) is { MoveAllowance: > 0 } grown
            ? new ThiefStack(s.Id, u.TypeId, grown.MoveAllowance) : null;
    }

    /// <summary>
    /// Отряды-воры каталога: id → все варианты «тип лидера + его <c>MOVE</c>», какие встретились в
    /// сохранениях каталога. Вариантов бывает несколько, потому что у синтетических карт одного
    /// каталога общий префикс id: <c>S143KC0003</c> — вор на <c>m13-thief</c> и совсем другой отряд на
    /// <c>m15-diplo</c>. Какая карта загружена в этот момент журнала, из журнала не следует, поэтому
    /// разбор дня выбирает вариант сам — по запасу первой строки дня (<see cref="Chain"/>), а если ни
    /// один не подошёл, день уходит в пропуск с причиной. Отряд, который ни в одном сохранении не вор,
    /// в справочник не попадает вовсе.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<ThiefStack>> ThievesOfDir(string dir)
    {
        if (_thievesByDir.TryGetValue(dir, out var cached)) return cached;
        var found = new Dictionary<string, List<ThiefStack>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.sg").OrderBy(f => f, StringComparer.Ordinal))
        {
            var save = new SaveAt(0, file);
            foreach (var s in save.Map.Stacks.Values)
            {
                if (!save.Map.Units.TryGetValue(s.LeaderId, out var u)) continue;
                if (book.LeaderCategoryOf(u.TypeId) != Thieves.LeaderCategory) continue;
                if (book.Grown(u.TypeId, u.Level) is not { MoveAllowance: > 0 } grown) continue;
                var variant = new ThiefStack(s.Id, u.TypeId, grown.MoveAllowance);
                if (!found.TryGetValue(s.Id, out var list)) found[s.Id] = list = [];
                if (!list.Contains(variant)) list.Add(variant);
            }
        }
        return _thievesByDir[dir] =
            found.ToDictionary(p => p.Key, p => (IReadOnlyList<ThiefStack>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ---- замеры для открытых пунктов §3 --------------------------------------------------------------

    /// <summary>
    /// §3 п. 12, урон отравления: у успешного Poison Party сравниваются HP юнитов мишени в снимках до
    /// и после. Не счётчик, а печатная строка: единицы <c>GVars.POISON_S</c> неизвестны, и это как раз
    /// то, что измеряется. Окна тут дневные, и в них бывают бои, поэтому вывод — «что видно», а не
    /// «сошлось».
    /// </summary>
    public void PoisonProbe(TreasuryJournal saves, ThiefJournal j)
    {
        foreach (var r in j.Rolls.Where(r => r.Ret && r.Action is (long)ThiefAction.PoisonStack
                                                              or (long)ThiefAction.PoisonCity))
        {
            if (saves.SaveBefore(r.T) is not { } before || saves.SaveAfter(r.T) is not { } after)
            { _probes.Add($"отравление seq {r.Seq} по {r.Target}: нет пары снимков"); continue; }
            if (!before.Map.Stacks.TryGetValue(r.Target, out var was))
            { _probes.Add($"отравление seq {r.Seq} по {r.Target}: мишени нет в снимке до"); continue; }
            var lines = new List<string>();
            foreach (var id in was.UnitIds)
            {
                if (!before.Map.Units.TryGetValue(id, out var a)) continue;
                int hpMax = book.Grown(a.TypeId, a.Level)?.Type.HitPoints ?? 0;
                string now = after.Map.Units.TryGetValue(id, out var b) ? b.Hp.ToString() : "нет";
                lines.Add($"{id} {a.TypeId} HP {a.Hp} → {now} из {hpMax}");
            }
            _probes.Add($"отравление seq {r.Seq} по {r.Target} " +
                        $"({Path.GetFileName(before.Path)} → {Path.GetFileName(after.Path)}): " +
                        string.Join("; ", lines));
        }
    }

    /// <summary>§3 п. 11, длительность мятежа: есть ли в корпусе вообще укрепление с непустым
    /// <c>RIOT_T</c>. Печатная строка — по всем сохранениям каталогов корпуса.</summary>
    public void RiotProbe()
    {
        int forts = 0;
        var riots = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(CorpusFiles.Dir)
                     .Where(d => !Path.GetFileName(d).StartsWith('_')))
            foreach (var file in Directory.EnumerateFiles(dir, "*.sg"))
                foreach (var site in new SaveAt(0, file).Sites
                             .Where(s => s.Kind is SiteKind.Capital or SiteKind.Village))
                {
                    forts++;
                    if (site.RiotTurn is > 0)
                        riots.Add($"{Path.GetFileName(file)} {site.Id} RIOT_T {site.RiotTurn} " +
                                  $"при CUR_TURN {new SaveAt(0, file).Map.Turn}");
                }
        _probes.Add($"мятеж (§3 п. 11): укреплений в сохранениях {forts}, с непустым RIOT_T — " +
                    $"{riots.Count}{(riots.Count == 0 ? " (длительность мерить не на чем)" : ": " + string.Join("; ", riots.Take(8)))}");
    }

    // ---- дословные строки разбора --------------------------------------------------------------------

    public void Print()
    {
        c.Note($" бросков вора в корпусе: {_rolls}, из них с кубиками и порогом (схема 5.1): {_v51}, " +
               $"провалов: {_failures}");
        c.Note($" действий по видам: {string.Join(", ", _actions.OrderBy(p => p.Key).Select(p => $"{p.Key} — {p.Value}"))}" +
               $"; §3 п. 12 без наблюдений: StealGold, Bribe, RiotCity, StealMerchant, StealMage, SpyRuin, " +
               $"StealItem, StealSpell, Assassinate, PoisonCity");
        c.Note($" дней отрядов-воров в журналах с ходами: {_thiefDays}");
        foreach (var line in _observed) c.Note($"  бросок {line}");
        foreach (var line in _charges) c.Note($"  списание {line}");
        foreach (var line in _probes) c.Note($"  замер {line}");
    }
}

/// <summary>Мишень броска глазами сохранения.</summary>
internal sealed record ThiefTarget(ThiefTargetKind Kind, int Resistance, string Owner, bool Playable);
