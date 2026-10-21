using D2.Data.Corpus;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 17 <c>port-synth-17-mixed</c> против партии оригинала 19.10 (<c>corpus/20261018b-port-synth</c>, сохранения t01-016 … t04-030;
/// разбор — <c>docs/synthetic-maps-port/analysis/20261019-map17.md</c>). Каждая ситуация повторяется портом с сохранения оригинала перед ней и
/// сверяется с сохранением после и со строками журнала.</summary>
public partial class PortSynthMapsTests
{
    private const string MixedRun = "20261018b-port-synth";
    private const string MixedHuman = "S143PL0003";

    /// <summary>Первый щелчок оригинала по самой гончей (seq журнала): щелчки по деревне с выбранной гончей — до него.</summary>
    private const long MixedHoundSelfClickSeq = 5176947;

    private static string MixedJournal => Path.Combine(GameSaveTests.CorpusDir(), MixedRun, "passive-001.jsonl");

    /// <summary>Сохранение карты 17 по номеру партии (<c>t01-016</c> … <c>t04-030</c>).</summary>
    private static string MixedSave(string suffix)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), MixedRun);
        Skip.IfNot(Directory.Exists(dir), $"нет {MixedRun}");
        var hit = Directory.GetFiles(dir, $"{MixedMap}-*-{suffix}.sg");
        Skip.If(hit.Length != 1, $"нет сохранения {suffix} карты 17");
        return hit[0];
    }

    /// <summary>Партия порта с сохранения оригинала карты 17 (как <see cref="Path3Session"/>).</summary>
    private static GameSession MixedSession(string sg)
    {
        var looks = MapLooksLoader.Load(sg);
        var events = EventLoader.Load(sg);
        var state = new GameState(new GameInfo(MixedMap, MixedMap, [MixedHuman], 1, 1), looks) { Events = events, EventProgress = EventLoader.LoadProgress(sg, events) };
        return new GameSession(state, Shared.Value.Content.MapArt, Shared.Value.Book) { EventBattlesOnScreen = false };
    }

    private static MapStack EngineStack(string sg, string id) => MapLooksLoader.Load(sg).Map.Stacks[id];

    /// <summary>E01g и PRI: отряды по шаблону (<c>L_CREATE_STACK</c>, <c>0x436c9a</c>) — клетка, приказ, ход, шаблон и <c>AIPRIORITY</c>
    /// созданного против сохранения оригинала после ворот. У движка приоритет — из шаблона (<c>+0x58</c> → <c>0x5e80cc</c>, 0…6): Гоблин
    /// C5t (шаблон 3) — 3, Гоблин PRI (шаблон 6) — 6; порт до 19.10 ставил 3 обоим. Поправка счёта ИИ Кланов по нему (<c>ai_tactic_adjust</c>
    /// <c>priority</c> 6: −89,25 → 10,75 дня 1) — <see cref="AiTacticScore.EditorPriority"/>.</summary>
    [SkippableFact]
    public void Mixed_TemplateStacks_LikeOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        Skip.IfNot(File.Exists(MixedJournal), $"нет {MixedRun}");
        var facts = Facts(MixedMap);
        foreach (var (key, before, after, made) in new[] { ("C5t", "t01-018", "t01-019", "S143KC0014"), ("PRI", "t01-024", "t01-025", "S143KC0015") })
        {
            var z = Zone(facts, key);
            var session = MixedSession(MixedSave(before));
            var (gx, gy) = XY(z, "gate_at");
            Walk(session, Str(z, "stack"), gx, gy);
            var port = session.Map.Stacks.Values.Single(s => Same(s.TemplateId, Str(z, "template")));
            var engine = EngineStack(MixedSave(after), made);
            Out.WriteLine($"{key}: движок {made} ({engine.X},{engine.Y}) приказ {engine.Order} ход {engine.Move} приоритет {engine.AiPriority}; " +
                          $"порт {port.Id} ({port.X},{port.Y}) приказ {port.Order} ход {port.Move} приоритет {port.AiPriority}");
            Assert.Equal(made, port.Id, StringComparer.OrdinalIgnoreCase);
            Assert.Equal((engine.X, engine.Y, engine.Order, engine.Move, engine.AiPriority, engine.TemplateId.ToUpperInvariant()),
                         (port.X, port.Y, port.Order, port.Move, port.AiPriority, port.TemplateId.ToUpperInvariant()));
        }
        Assert.Equal(6, EngineStack(MixedSave("t01-025"), "S143KC0015").AiPriority);

        // Поправка счёта Кланов к атаке созданного PRI (день 1 — порт, дни 2, 3 — те же строки движка).
        var adjust = JournalCache.Read(MixedJournal, "ai_tactic_adjust").OfType<AiTacticAdjustEvent>()
            .Where(a => a.Kind == "priority" && Same(a.Action?.Target ?? "", "S143KC0015")).ToList();
        Assert.Equal(3, adjust.Count);
        var pri = MixedSession(MixedSave("t01-025"));
        int? mine = AiTactics.TargetPriority(pri.Map, "S143KC0015");
        foreach (var a in adjust)
        {
            Assert.Equal(a.Priority, mine);
            Assert.Equal(a.After!.Value, AiTacticScore.EditorPriority(a.Before!.Value, mine, targetIsOrderTarget: false), 6);
        }
    }

    /// <summary>LUTE: Герцог с «Лютней очарования» (<c>L_LOWER_COST</c> 10) покупает «Молнию» (<c>BUY_C</c> 200) — движок списал 180
    /// (<c>bank</c> 1100 → 920, <c>site_buy_spell</c>; сохранение t01-023), порт по той же формуле <c>× (100 − 10) / 100</c> (<c>0x5d7a36</c>, И47) —
    /// не совпадение: без лютни цена 200.</summary>
    [SkippableFact]
    public void Mixed_LuteSpell_PriceLikeOriginal()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        Skip.IfNot(File.Exists(MixedJournal), $"нет {MixedRun}");
        var lute = Zone(Facts(MixedMap), "LUTE");
        var buy = JournalCache.Read(MixedJournal, "site_buy_spell").OfType<SiteBuySpellEvent>().Single(b => Same(b.Player, MixedHuman));
        var bank = JournalCache.Read(MixedJournal, "bank").OfType<BankEvent>().First(b => b.Seq > buy.Seq && Same(b.Player, MixedHuman));
        var session = MixedSession(MixedSave("t01-022"));
        long before = session.Map.Players[MixedHuman].Bank.Gold;
        Assert.Equal(bank.Before.Gold, before);
        Assert.Null(session.BuySpell(Str(lute, "stack"), buy.Site, buy.Spell));
        long paid = before - session.Map.Players[MixedHuman].Bank.Gold;
        Out.WriteLine($"LUTE: движок {bank.Before.Gold} → {bank.After.Gold}, порт заплатил {paid}");
        Assert.Equal(bank.Before.Gold - bank.After.Gold, paid);
        Assert.Equal(MapLooksLoader.Load(MixedSave("t01-023")).Map.Players[MixedHuman].Bank.Gold, session.Map.Players[MixedHuman].Bank.Gold);
        Assert.Equal(200, Treasury.SpellBuyPrice(session.RequireBook().Spells[buy.Spell]).Gold);   // без лютни — 200
    }

    /// <summary>INC2, ILL, ILLn: вор у неподкупного лидера Кланов гибнет (<c>noble_choose</c> count 0), убийца в <c>CMidStackDestroyed</c> —
    /// игрок Кланов, вещи вора — у отряда Кланов (t01-021 → t01-022); иллюзия у пустой деревни — отказ <c>X005TA0458</c> (<c>ui_refusal</c>
    /// <c>capture_summon_thief</c>, <c>0x4cee14</c>), Герцог берёт свою (t01-023 → t01-024).</summary>
    [SkippableFact]
    public void Mixed_ThiefAndIllusion_LikeOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        Skip.IfNot(File.Exists(MixedJournal), $"нет {MixedRun}");
        var facts = Facts(MixedMap);
        var inc = Zone(facts, "INC2");
        string thief = Str(inc, "stack"), target = Str(inc, "target");
        var session = MixedSession(MixedSave("t01-021"));
        Assert.True(session.ThiefIncorruptible(thief, target));
        var engine = MapLooksLoader.Load(MixedSave("t01-022")).Map;
        var engineGone = Assert.Single(engine.Destroyed, d => Same(d.StackId, thief));
        var portGone = Assert.Single(session.Map.Destroyed, d => Same(d.StackId, thief));
        Out.WriteLine($"INC2: убийца у движка {engineGone.KillerId}, у порта {portGone.KillerId}; вещи Кланов {engine.Stacks[target].Items.Count}");
        Assert.Equal(engineGone.KillerId, portGone.KillerId, StringComparer.OrdinalIgnoreCase);
        Assert.False(session.Map.Stacks.ContainsKey(thief));
        Assert.Equal(engine.Stacks[target].Items.Count, session.Map.Stacks[target].Items.Count);

        Assert.Contains(JournalCache.Read(MixedJournal, "ui_refusal").OfType<UiRefusalEvent>(), r => r.Text == "X005TA0458");
        var ill = MixedSession(MixedSave("t01-023"));
        var after = MapLooksLoader.Load(MixedSave("t01-024")).Map;
        foreach (string key in new[] { "ILL", "ILLn" })
        {
            var z = Zone(facts, key);
            var report = ill.Enter(Str(z, "stack"), Str(z, "target"));
            string owner = ill.Map.Sites.Single(x => Same(x.Id, Str(z, "target"))).Owner;
            string engineOwner = after.Sites.Single(x => Same(x.Id, Str(z, "target"))).Owner;
            Out.WriteLine($"{key}: порт {report}; хозяин деревни у движка {engineOwner}, у порта {owner}");
            Assert.Equal(engineOwner, owner, StringComparer.OrdinalIgnoreCase);
            if (key == "ILL") Assert.Equal(GameSession.CaptureRefusal, report.Refused);
        }
    }

    /// <summary>G14: щелчки оригинала по своей деревне с выбранной Адской гончей (призыв) — (16,24) и (15,23), <c>map_click</c> над
    /// <c>S143FT0004</c>, — у движка ни пути, ни хода (<c>stack_move</c> гончей нет, t01-019 → t01-020: она в (18,24)); второй щелчок выбирает
    /// деревню (курсор <c>VIEWCITY</c>, окно города — третьим). У порта на обеих клетках отказ входа (<see cref="GameSession.OwnFortRefuses"/>).
    /// SU: выбранный Tab Герцог идёт к (11,34) за 2 очка (t01-017: ход 18), после загрузки дня 2 — к (10,33) за 2 (t02-027).</summary>
    [SkippableFact]
    public void Mixed_SummonedClicks_AndSu_LikeOriginal()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        Skip.IfNot(File.Exists(MixedJournal), $"нет {MixedRun}");
        var facts = Facts(MixedMap);
        var g14 = Zone(facts, "G14");
        string hound = Str(g14, "stack");
        var clicks = JournalCache.Read(MixedJournal, "map_click").OfType<MapClickEvent>()
            .Where(c => c.Button == 1 && c.Cell is not null && c.Objects?.Contains(Str(g14, "target")) == true && c.Task == "CTaskSelectUnit"
                        && c.Seq < MixedHoundSelfClickSeq).ToList();
        Assert.Equal(2, clicks.Count);
        var session = MixedSession(MixedSave("t01-019"));
        var stack = session.Map.Stacks[hound];
        foreach (var c in clicks)
        {
            var aim = session.ClickAim(stack, (int)c.Cell!.X, (int)c.Cell.Y);
            Assert.True(session.OwnFortRefuses(stack, aim), $"щелчок ({c.Cell.X},{c.Cell.Y}): у порта путь");
        }
        var engineHound = EngineStack(MixedSave("t01-020"), hound);
        Assert.Equal((stack.X, stack.Y), (engineHound.X, engineHound.Y));

        var su = Zone(facts, "SU");
        foreach (var (before, after, x, y) in new[] { ("t01-016", "t01-017", 11, 34), ("t02-026", "t02-027", 10, 33) })
        {
            var s = MixedSession(MixedSave(before));
            Walk(s, Str(su, "stack"), x, y);
            var port = s.Map.Stacks[Str(su, "stack")];
            var engine = EngineStack(MixedSave(after), Str(su, "stack"));
            Out.WriteLine($"SU {before}: движок ({engine.X},{engine.Y}) ход {engine.Move}, порт ({port.X},{port.Y}) ход {port.Move}");
            Assert.Equal((engine.X, engine.Y, engine.Move), (port.X, port.Y, port.Move));
        }
    }

    /// <summary>FG: «Туман Смерти» (<c>AREA</c> 9) на своего Советника (40,44) в день 3 — у движка Империя (<c>S143PL0001</c>) теряет клетки
    /// t03-028 → t03-029; порт с t03-028 бросает то же и закрывает те же клетки (форма и число).</summary>
    [SkippableFact]
    public void Mixed_FogOfDeath_ShapeLikeOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        var facts = Facts(MixedMap);
        var fg = Zone(facts, "FG");
        string empire = facts.GetProperty("watched").GetString()!;
        var (bx, by) = XY(fg, "at");
        var before = MapLooksLoader.Load(MixedSave("t03-028")).Map;
        var after = MapLooksLoader.Load(MixedSave("t03-029")).Map;
        var session = MixedSession(MixedSave("t03-028"));
        var portBefore = (bool[,])session.Map.Fog[session.Map.Players[empire].FogId].Clone();
        Assert.Null(session.CastSpell(MixedHuman, Str(fg, "spell"), bx, by, useSpellBank: false));
        static HashSet<(int, int)> Closed(bool[,] was, bool[,] now)
        {
            var set = new HashSet<(int, int)>();
            for (int x = 0; x < now.GetLength(0); x++)
                for (int y = 0; y < now.GetLength(1); y++)
                    if (now[x, y] && !was[x, y]) set.Add((x, y));
            return set;
        }
        var engine = Closed(before.Fog[before.Players[empire].FogId], after.Fog[after.Players[empire].FogId]);
        var port = Closed(portBefore, session.Map.Fog[session.Map.Players[empire].FogId]);
        Out.WriteLine($"FG: у Империи закрыто — движок {engine.Count}, порт {port.Count}; углы квадрата у движка: " +
                      string.Join(" ", new[] { (bx - 4, by - 4), (bx + 4, by - 4), (bx - 4, by + 4), (bx + 4, by + 4) }.Select(c => engine.Contains(c) ? "1" : "0")));
        Assert.Equal(FogOfDeathCells, engine.Count);
        Assert.True(engine.SetEquals(port), $"движок {engine.Count}, порт {port.Count}");
    }
}
