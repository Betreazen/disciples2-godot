using D2.Data.Corpus;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Выдача id новым объектам (<c>id_alloc</c>, схема 7.2: <c>CMidgardScenarioMap</c> слот 9 <c>0x5ee8bd</c>) — счётчик по классу
/// (<see cref="MapState.IdNext"/>): при загрузке наибольший номер + 1 (<c>0x5ee996</c>), дальше только растёт.
/// </summary>
[Trait("Category", "Corpus")]
public sealed partial class IdAllocHarnessTests(ITestOutputHelper output)
{
    /// <summary>Журнал сам по себе: номера каждого класса идут подряд, без пропусков и повторов, — счётчик, а не «наибольший из живых»
    /// (иначе номер павшего отряда вернулся бы). Классы ai-q09 за 20 дней: UN 101, KC 29, TB 38, RD 18, IM 4.</summary>
    [SkippableFact]
    public void Journal_IdsAreCounters()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), "20260930-probe72", "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), "нет корпуса");
        var allocs = JournalCache.Read(journal, ["id_alloc"]).OfType<IdAllocEvent>().OrderBy(e => e.Seq).ToList();
        Assert.Equal(190, allocs.Count);
        foreach (var byType in allocs.GroupBy(a => a.Type))
        {
            var list = byType.ToList();
            foreach (var a in list)
                Assert.Equal(a.Index, Convert.ToInt64(a.Id![6..], 16));   // id = счётчик до выдачи
            for (int i = 1; i < list.Count; i++) Assert.Equal(list[i - 1].Index + 1, list[i].Index);
        }
        Assert.Equal(new[] { 29, 101, 4, 38, 18 }, allocs.GroupBy(a => a.Type).OrderBy(g => g.Key).Select(g => g.Count()));
    }

    /// <summary>Порт: номер удалённого объекта не возвращается — <see cref="GameSession.NewId"/> берёт счётчик карты, а не наибольший из
    /// живых + 1; счётчик после загрузки — наибольший номер + 1 (<see cref="Map.MapLoader"/>, <see cref="GameSession.IdNextOf"/>).</summary>
    [Fact]
    public void NewId_DoesNotReuseDeletedNumber()
    {
        IReadOnlyDictionary<string, int> next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["KC"] = 0x96 };
        // Живые отряды — до KC0094: KC0095 пал в бою, а счётчик уже 0x96.
        string[] alive = ["S143KC0093", "S143KC0094"];
        Assert.Equal("S143KC0096", GameSession.NewId(ref next, alive, "S143FT0010", "KC"));
        Assert.Equal("S143KC0097", GameSession.NewId(ref next, [.. alive, "S143KC0096"], "S143FT0003", "KC"));
        Assert.Equal(0x98, next["KC"]);
        // Карта без счётчика (синтетическая) — прежнее правило: наибольший из живых + 1.
        IReadOnlyDictionary<string, int> none = new Dictionary<string, int>();
        Assert.Equal("S143KC0095", GameSession.NewId(ref none, alive, "S143FT0010", "KC"));
        Assert.Equal(0x96, none["KC"]);
        Assert.Equal(0x249, Map.MapLoader.IdNextOf(["S143UN0248", "S143UN0010", "S143KC008e", "G000UU0001"])["UN"]);
    }

    /// <summary>
    /// Наймы и призывы живого дня (<see cref="LiveDayReplay"/>, день — с сохранения, наймы лидеров — до оперативного слоя) против
    /// <c>id_alloc</c> движка: по каждому ходу ИИ — множество новых id отрядов и юнитов, что создал порт (начало хода, найм, призыв),
    /// и выданных движком. Измерено 30.09.2026 (ai-q09, зонд 7.2). Ходов с выдачей 38, совпало 37 → 38: ход нейтрала PL0000 дня 5 — призыв
    /// в бою <c>KC0020</c> против <c>KC008f</c> (<c>UN024d</c>, атака <c>G000AA8012</c> класса 17) выдал <c>UN0274</c>…<c>UN0278</c> из
    /// счётчика карты (<c>CBatAttackSummon::onHit</c> <c>0x6631eb</c> → <c>0x5ee8bd</c>); бой живой день берёт из <c>battle_end</c>, номер
    /// призванного — <see cref="AiLiveDay.BattleSummon"/> (у порта — <c>BattleState.NewUnitId</c>). Без призыва — 37.
    /// </summary>
    [SkippableTheory]
    [InlineData("20260930-probe72", 38, 38)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — 33 из 42: события начала хода нейтралов (L_CREATE_STACK
    // 0x436e1c → 0x60cff2) у движка идут до строки ai_turn begin и попадают в окно прошлого игрока, порт создаёт их в BeginTurn; дни
    // с шансом события — броски порта, не движка (артефакт живого дня, реестр Зс30).
    [InlineData("20261019c-cov", 42, 33)]
    public void LiveDay_NewIdsMatchJournal(string corpus, int wantTurns, int wantSame) => LiveDayIds(corpus, wantTurns, wantSame);

    /// <summary>То же на партиях синтетических карт — группа <c>Synth</c>.</summary>
    [SkippableTheory, Trait("Category", "Synth")]
    // 20.10 (r20/batB): карта 23 — ходов с выдачей 119, совпало 105: наймы и призывы живого дня (найм юнитов в гарнизон — портом, Зс30).
    // 20.10 (r20/aiA, слияние): уход стратегии по юниту (И110) и отступление бежавшего (И93) — ходов с выдачей 119 → 118 (у порта
    // больше нет лишней выдачи в ходе, где движок id не выдавал), совпало по-прежнему 105.
    [InlineData("20261020-port-synth", 118, 105)]
    public void LiveDay_NewIdsMatchJournal_Synth(string corpus, int wantTurns, int wantSame) => LiveDayIds(corpus, wantTurns, wantSame);

    private void LiveDayIds(string corpus, int wantTurns, int wantSame)
    {
        var replay = new LiveDayReplay(corpus, "passive-001.jsonl", ["id_alloc"]);
        Skip.IfNot(replay.Exists, "нет корпуса");
        AiLiveDay? seen = null;
        HashSet<string> known = [];
        int summonsSeen = 0;
        CorpusEvent? last = null;
        (string Who, SortedSet<string> Port, SortedSet<string> Engine)? turn = null;
        int turns = 0, same = 0, shown = 0;

        void Close()
        {
            if (turn is not { } w || (w.Port.Count == 0 && w.Engine.Count == 0)) return;
            turns++;
            if (w.Port.SetEquals(w.Engine)) same++;
            else if (shown++ < 20) output.WriteLine($"{w.Who}: порт [{string.Join(" ", w.Port)}] движок [{string.Join(" ", w.Engine)}]");
        }

        foreach (var (e, live) in replay.Run())
        {
            if (live is not null)
            {
                var keys = Keys(live.Session().Map);
                // Новые id после применения прошлого события — если это начало хода, найм или призыв (переименование нанятого в id
                // движка при постановке, stack_move, — не выдача).
                if (ReferenceEquals(live, seen) && turn is { } w && last is AiTurnEvent or HireLeaderEvent or SpellCastEvent)
                    foreach (string id in keys.Where(k => !known.Contains(k))) w.Port.Add(id.ToUpperInvariant());
                if (!ReferenceEquals(live, seen)) summonsSeen = 0;
                // Призванные боя (AiLiveDay.BattleSummon: номер из счётчика карты) в карту не попадают — берутся списком.
                if (turn is { } ws)
                    foreach (string id in live.BattleSummoned.Skip(summonsSeen)) ws.Port.Add(id.ToUpperInvariant());
                summonsSeen = live.BattleSummoned.Count;
                seen = live;
                known = keys;
            }
            last = e;
            switch (e)
            {
                case AiTurnEvent t:
                    Close();
                    turn = t.Phase == "begin" ? ($"день {t.Turn} {t.Player} seq {t.Seq}", [], []) : null;
                    break;
                case IdAllocEvent { Type: KcType or UnType, Id: { } id } when turn is { } w:
                    w.Engine.Add(id.ToUpperInvariant());
                    break;
            }
        }
        Close();
        output.WriteLine($"ходов с выдачей {turns}, совпало {same}");
        Assert.Equal(wantTurns, turns);
        Assert.Equal(wantSame, same);
    }

    private const long KcType = 35;
    private const long UnType = 36;

    private static HashSet<string> Keys(MapState map) =>
        [.. map.Stacks.Keys.Concat(map.Units.Keys).Select(k => k.ToUpperInvariant())];
}
