using D2.Data.Content;
using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>
/// Прогон журнала по живому дню (<see cref="AiLiveDay"/>) для харнессов схемы 7.2: день — с его сохранения, начало хода игрока ИИ —
/// портом с наймами лидеров журнала до оперативного слоя (<see cref="AiLiveDay.Begin"/>; юнитов в гарнизоны нанимает сам порт), поверх — позиции, приказы, призывы, наймы, бои,
/// здоровье, захваты, жезлы, подобранные мешки (как <c>ExecutionPaths_MatchJournal</c>). <see cref="Run"/> отдаёт каждое событие
/// <b>до</b> того, как оно изменит живой день. Журнал нескольких партий одной карты (20261014-replay) — партия отрезком seq
/// [<paramref name="fromSeq"/>, <paramref name="toSeq"/>] (0 — без границы) и сохранения, чьё имя начинается с <paramref name="stem"/>.
/// </summary>
internal sealed class LiveDayReplay(string corpus, string file, IReadOnlyList<string> extra, string stem = "", long fromSeq = 0,
                                    long toSeq = 0)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    public bool Exists => File.Exists(Journal);

    private string Dir => Path.Combine(GameSaveTests.CorpusDir(), corpus);

    private string Journal => Path.Combine(Dir, file);

    public IEnumerable<(CorpusEvent Event, AiLiveDay? Live)> Run()
    {
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = Directory.GetFiles(Dir, "*.sg").Order(StringComparer.Ordinal)
            .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Last());
        var events = JournalCache.Read(Journal, [.. Kinds, .. extra])
            .Where(e => e.Seq >= fromSeq && (toSeq == 0 || e.Seq <= toSeq)).OrderBy(e => e.Seq).ToList();
        var spends = AiSpendMemory.From(events);
        var hiresOfTurn = AiSpellHarnessTests.HiresOfTurns(events);
        var battleXp = new List<XpEvent>();
        AiLiveDay? live = null;
        var begun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var engineIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e is AiTurnEvent { Phase: "begin" } t)
            {
                // Переигранный день (тот же игрок начинает тот же день снова — загрузка сохранения): живой день — заново.
                bool replay = live is not null && live.Day == t.Turn && !begun.Add(t.Player ?? "");
                if (live is null || live.Day != t.Turn || replay)
                {
                    begun.Clear();
                    begun.Add(t.Player ?? "");
                    live = saves.TryGetValue((int)t.Turn, out var sg)
                        ? new AiLiveDay(MapLooksLoader.Load(sg), (int)t.Turn, content.MapArt, book, spends, EventLoader.Load(sg))
                        : null;
                }
            }
            yield return (e, live);
            // Счётчики движка (только если журнал схемы 7.2 читается с id_alloc): на начало каждого хода ИИ — в живой день.
            if (e is IdAllocEvent { Id: { Length: 10 } id, Index: { } index }) engineIds[id.Substring(4, 2)] = (int)index + 1;
            if (live is null) continue;
            switch (e)
            {
                case AiTurnEvent { Phase: "begin", Player: { } player } begin:
                    live.SyncIds(engineIds);
                    live.Begin(player, hires: hiresOfTurn.GetValueOrDefault(begin.Seq));
                    break;
                case AiTacticResultEvent r when r.Status is AiTactic.StatusProgress or 2 && r.Target is { Length: > 0 } bagId
                                                && (r.Status == 2 || live.Reached(bagId, r.Stack)):
                    live.TakeBag(bagId, r.Stack);
                    break;
                case StackMoveEvent m:
                    live.Move(m.Stack, (int)m.X, (int)m.Y, (int)m.MoveLeft);
                    break;
                case EquipCheckEvent q:
                    live.BagItemSeen(q.Stack, q.Item);
                    break;
                case NobleActionEvent na:
                    live.Noble(na);   // провал — вор гибнет (probe72 seq 3038526 KC0095: путь KC0099 через его клетку в 3042984)
                    break;
                case AiOrderEvent o:
                    live.Order(o);
                    break;
                case SpellCastEvent sc:
                    live.Cast(sc);
                    break;
                case HireLeaderEvent hl:
                    live.HireLeader(hl);
                    break;
                case IdAllocEvent ia when ia.Callers.Contains(AiLiveDay.SummonCaller):
                    live.BattleSummon();
                    break;
                case BattleEndEvent b:
                    live.Battle(b);
                    foreach (var x in battleXp) live.Xp(x);
                    battleXp.Clear();
                    break;
                case XpEvent x when x.RetAddr != AiLiveDay.DailyXpCheat:
                    battleXp.Add(x);
                    break;
                case HealEvent { Ret: true } h:
                    live.Heal(h.Unit);
                    break;
                case HpChangeEvent { Ret: true } hc:
                    live.Hp(hc.Unit, (int)hc.HpAfter);
                    break;
                case ReviveEvent { Ret: true, HpAfter: { } hpAfter } rv:
                    live.Hp(rv.Unit, (int)hpAfter);
                    break;
                case FortCaptureEvent fc:
                    live.Capture(fc);
                    break;
                case RodDropEvent rd:
                    live.Rod(rd);
                    break;
                case AllianceChangeEvent { Ret: true } ac:
                    live.Alliance(ac);   // разрыв посреди дня: зона контроля бывших союзников (карта 22, день 10 — вор Нежити у отрядов Кланов)
                    break;
                case PotionUseEvent { Ret: true, InBattle: false } pu:
                    live.Hp(pu.Target, (int)pu.HpAfter);
                    break;
                case ApRestoreEvent { Ret: true } ap when ap.Cause is not ("stack_move" or "turn_start"):
                    live.MoveLeft(ap.Stack, (int)ap.MoveAfter);
                    break;
            }
        }
    }

    private static readonly string[] Kinds =
    [
        "ai_turn", "ai_tactic_result", "ai_order", "hire_leader", "stack_move", "battle_end", "spell_cast", "ap_restore", "heal",
        "hp_change", "revive", "potion_use", "xp", "ai_budget_spend", "fort_capture", "rod_drop", "equip_check", "noble_action",
        "alliance_change",
    ];
}
