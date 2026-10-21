using System.Text.Json;
using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Battle;
using D2.Rules.Magic;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Синтетические карты порта (<c>tools/synth-maps</c>, слой <c>mods/port-synth</c>, памятки — <c>docs/synthetic-maps-port</c>): импорт
/// повторяет то, что записал генератор (описание карты <c>facts.json</c>: отряды, клетки, приказы, снаряжение, деревни, события, лорды ИИ),
/// карта стоит в списке сценариев через слой мода, партия начинается по памятке и идёт, а ситуации памяток в порте складываются:
/// ворота событий срабатывают, у каждого вора есть его действие, деревня Кланов берётся, бои полигонов проходят и дают зонду классы атак.
/// </summary>
[Trait("Category", "Synth")]
public partial class PortSynthMapsTests(ITestOutputHelper output)
{
    /// <summary>Вывод теста для частей класса в других файлах (карты 5–9).</summary>
    private ITestOutputHelper Out => output;

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static string Mods => Path.Combine(AppContext.BaseDirectory, "mods");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (GameContent.Load(Data, GameContent.Discover(Mods)), GameBook.Load(Path.Combine(Data, "rules"))));

    public static TheoryData<string> Maps => [.. MapIds()];

    private static string FactsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Disciples.sln"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("нет Disciples.sln выше каталога тестов"),
                                "docs", "synthetic-maps-port");
        }
    }

    private static IEnumerable<string> MapIds() =>
        Directory.Exists(FactsDir) ? Directory.GetDirectories(FactsDir).Where(d => File.Exists(Path.Combine(d, "facts.json")))
                                              .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
                                   : [];

    private static JsonElement Facts(string id) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FactsDir, id, "facts.json"))).RootElement;

    /// <summary>Пустой id файла (<c>G000000000</c>) — у порта пустая строка.</summary>
    private static string Id(string? id) => id is null or "G000000000" ? "" : id;

    private static ScenarioData Scenario(string id) => ScenarioFile.Read(Shared.Value.Content.ScenarioPath(id));

    [Fact]
    public void EveryMap_InScenarioList_FromModLayer()
    {
        var ids = MapIds().ToList();
        Assert.True(ids.Count >= 4, $"описаний карт {ids.Count}");
        var content = Shared.Value.Content;
        var plain = SharedContent.Load(Data);
        foreach (string id in ids)
        {
            var card = Assert.Single(content.Catalog.Scenarios, s => s.Id == id);
            // Основы генератора — 144×144; карты переигровки (24–27, исходник байт в байт) — размера исходника (map_size описания).
            Assert.Equal(Facts(id).TryGetProperty("map_size", out var size) ? size.GetInt32() : 144, card.MapSize);
            Assert.StartsWith(Path.Combine(Mods, "port-synth"), content.ScenarioPath(id), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(plain.Catalog.Scenarios, s => s.Id == id);   // без слоя мода карт в списке нет
        }
        // Штатный сценарий по-прежнему из базы.
        Assert.StartsWith(Path.Combine(Data, "scenarios"), content.ScenarioPath("Tutorial"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Imported_AsGenerated(string id)
    {
        var facts = Facts(id);
        var map = Scenario(id).Looks.Map;
        string Type(string item) => map.ItemTypes.TryGetValue(item, out var t) ? t.ToUpperInvariant() : item;
        string[] slots = ["BANNER", "TOME", "BATTLE1", "BATTLE2", "ARTIFACT1", "ARTIFACT2", "BOOTS"];
        int stacks = 0;
        foreach (var st in facts.GetProperty("stacks").EnumerateArray())
        {
            string sid = st.GetProperty("id").GetString()!;
            var stack = Assert.Contains(sid, map.Stacks);
            var at = st.GetProperty("at");
            Assert.Equal((at[0].GetInt32(), at[1].GetInt32()), (stack.X, stack.Y));
            Assert.Equal(st.GetProperty("owner").GetString(), stack.Owner, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(st.GetProperty("order").GetInt32(), stack.Order);
            Assert.Equal(Id(st.GetProperty("order_target").GetString()), stack.OrderTarget, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(st.GetProperty("leader").GetString(), map.Units[stack.LeaderId].TypeId, StringComparer.OrdinalIgnoreCase);
            var units = stack.UnitIds.Where(u => u.Length > 0 && map.Units.ContainsKey(u)).Select(u => map.Units[u]).ToList();
            Assert.Equal(st.GetProperty("units").EnumerateArray().Select(u => u.GetString()!.ToUpperInvariant()),
                         units.Select(u => u.TypeId.ToUpperInvariant()).Order(StringComparer.Ordinal));
            Assert.Equal(st.GetProperty("hp").EnumerateArray().Select(h => h.GetInt32()), units.Select(u => u.Hp).Order());
            Assert.Equal(st.GetProperty("items").EnumerateArray().Select(i => i.GetString()!.ToUpperInvariant()).Order(StringComparer.Ordinal),
                         stack.Items.Select(Type).Order(StringComparer.Ordinal));
            foreach (var slot in st.GetProperty("equip").EnumerateObject())
                Assert.Equal(slot.Value.GetString(), Type(stack.Equipment[Array.IndexOf(slots, slot.Name)]), StringComparer.OrdinalIgnoreCase);
            stacks++;
        }
        if (facts.TryGetProperty("villages", out var villages))
            foreach (var v in villages.EnumerateArray())
            {
                var site = map.Sites.Single(s => s.Id.Equals(v.GetProperty("id").GetString(), StringComparison.OrdinalIgnoreCase));
                Assert.Equal(SiteKind.Village, site.Kind);
                Assert.Equal(v.GetProperty("owner").GetString(), site.Owner, StringComparer.OrdinalIgnoreCase);
                Assert.Equal((v.GetProperty("at")[0].GetInt32(), v.GetProperty("at")[1].GetInt32()), (site.X, site.Y));
                Assert.Equal(v.GetProperty("size").GetInt32(), site.Size);
                Assert.Equal(v.GetProperty("garrison").EnumerateArray().Select(u => u.GetString()!.ToUpperInvariant()).Order(StringComparer.Ordinal),
                             site.Guards.Where(g => g.Length > 0).Select(g => map.Units[g].TypeId.ToUpperInvariant()).Order(StringComparer.Ordinal));
                if (v.TryGetProperty("riot_t", out var riot)) Assert.Equal(riot.GetInt32(), site.RiotTurn);
                if (v.TryGetProperty("prev", out _)) Assert.Equal([0], site.PreviousOwners!);   // P_O_HU — Империя (RACE_TYPE 0)
            }
        if (facts.TryGetProperty("events", out var events))
        {
            var loaded = Scenario(id).Events.Events.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var e in events.EnumerateArray())
            {
                var ev = loaded[e.GetProperty("id").GetString()!];
                Assert.Equal(e.GetProperty("conditions").EnumerateArray().Select(c => c.GetInt32()), ev.Conditions.Select(c => c.Category));
                Assert.Equal(e.GetProperty("effects").EnumerateArray().Select(c => c.GetInt32()), ev.Effects.Select(c => c.Category));
                Assert.DoesNotContain(ev.Conditions, c => c is UnsupportedCondition);
            }
        }
        if (facts.TryGetProperty("lords", out var lords))
            foreach (var l in lords.EnumerateObject())
                Assert.Equal(l.Value.GetString(), map.Players[l.Name].LordId, StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"{id}: отрядов сверено {stacks}");
    }

    /// <summary>Каждый отряд карты ведёт тот, у кого есть картинка отряда на карте: лидер или вор (<c>Gunits.UNIT_CAT</c> 2 или 1, как у всех
    /// 5505 отрядов штатных карт), призванный или иллюзия (4 и 5 — их отряды ставят заклинания и сферы; карты 5 и 9). Солдат во главе —
    /// отряд на карте не виден (партия 15.10: нейтралы полигонов карт 1 и 3 не рисовались, пометка 1 и «C4 не было на карте»).</summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void EveryStack_LedByUnitWithMapArt(string id)
    {
        var map = Scenario(id).Looks.Map;
        var rules = Shared.Value.Book.Rules;
        var soldiers = map.Stacks.Values.Where(s => s.LeaderId.Length > 0 && map.Units.TryGetValue(s.LeaderId, out var u)
                                                    && rules.Units[u.TypeId].Category is not (1 or 2 or 4 or 5))
                                       .Select(s => $"{s.Id} ({s.X},{s.Y}) {map.Units[s.LeaderId].TypeId}").ToList();
        Assert.True(soldiers.Count == 0, string.Join("; ", soldiers));
    }

    /// <summary>Карта ИИ, лорд-маг человека: на очень трудной казну сценария движок обнуляет (<c>BONUS_V</c> 25 / 100 = 0,
    /// <c>NewGame.HumanBank</c>), поэтому мана на изучение и два заклинания дня 1 — от жил рун у столицы: доход дня 1 приходит до первого
    /// действия человека на любой сложности (пометка 4 партии 15.10: маны хватило лишь к 12-му дню).</summary>
    [SkippableFact]
    public void Ai_HumanMage_HasRunesForResearchAndTwoCasts_OnDayOne_EvenVeryHard()
    {
        const string id = "port-synth-4-ai";
        Skip.IfNot(MapIds().Contains(id));
        var facts = Facts(id);
        int need = facts.GetProperty("crystals").GetProperty("day1_runes_min").GetInt32();
        foreach (var difficulty in facts.GetProperty("human").GetProperty("difficulties").EnumerateArray())
        {
            var session = Begin(id, difficulty.GetInt32());
            var bank = session.Map.Players[session.ActiveHuman].Bank;
            output.WriteLine($"сложность {difficulty}: руны {bank.Runic}, золото {bank.Gold}");
            Assert.True(bank.Runic >= need, $"сложность {difficulty}: рун {bank.Runic} < {need}");
        }
    }

    /// <summary>Партия по памятке: раса, лорд и сложность человека из описания; два дня с ходами ИИ проходят, человек — тот игрок, лорды ИИ —
    /// из карты (у человека лорда выбирают в меню, у ИИ его не пересоздают). Тест — в <see cref="PortSynthMapsTests_NewGame"/>.</summary>
    internal void NewGame_TwoDaysPass(string id)
    {
        var facts = Facts(id);
        var human = facts.GetProperty("human");
        foreach (var difficulty in human.GetProperty("difficulties").EnumerateArray())
        {
            var session = Begin(id, difficulty.GetInt32());
            Assert.Equal(human.GetProperty("player").GetString(), session.ActiveHuman, StringComparer.OrdinalIgnoreCase);
            int day = session.Map.Turn;
            session.EndTurn();
            session.EndTurn();
            Assert.Equal(day + 2, session.Map.Turn);
            if (facts.TryGetProperty("lords", out var lords))
                foreach (var l in lords.EnumerateObject())
                    Assert.Equal(l.Value.GetString(), session.Map.Players[l.Name].LordId, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(D2.Data.Diagnostics.MapInvariants.Check(session.Map, session.Book!, null));
            output.WriteLine($"{id}, сложность {difficulty}: день {session.Map.Turn}, отрядов {session.Map.Stacks.Count}");
        }
    }

    [SkippableFact]
    public void Events_EveryGateFires_WhenItsWalkerSteps()
    {
        const string id = "port-synth-3-events";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        session.EventBattlesOnScreen = false;
        var seen = new Recorder();
        session.Watch(seen);
        var zones = Facts(id).GetProperty("zones").EnumerateArray().ToList();
        foreach (var z in zones.Where(z => z.TryGetProperty("gate_at", out _)))
        {
            var gate = z.GetProperty("gate_at");
            session.MoveStack(z.GetProperty("stack").GetString()!, gate[0].GetInt32(), gate[1].GetInt32(), 0, 0);
        }
        // герой — в свою зону, курьер — в точку предмета. Герой встаёт на (70,41), как в партии 15.10 (карта 3, пометка 1): зона C6 теперь
        // 5×5 вокруг (70,40), а при радиусе 0 эта клетка в неё не входила.
        foreach (var (key, dx, dy) in new[] { ("C5", 6, 0), ("C6", 6, 1) })
        {
            var z = zones.Single(z => z.GetProperty("key").GetString() == key);
            var at = z.GetProperty("at");
            session.MoveStack(z.GetProperty("stack").GetString()!, at[0].GetInt32() + dx, at[1].GetInt32() + dy, 0, 0);
        }
        var fired = seen.Events.Select(e => e.Event.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expected = [.. zones.Where(z => z.TryGetProperty("gate_at", out _) || z.GetProperty("key").GetString() is "C5" or "C6")
                                     .Select(z => z.GetProperty("event").GetString()!)];
        output.WriteLine($"сработало {fired.Count}: {string.Join(", ", seen.Events.Select(e => e.Event.Name))}");
        Assert.All(expected, e => Assert.Contains(e, fired));
        // цепочки: союз → условие союза, война → условие дипломатии, переменная → условие переменной
        foreach (string key in new[] { "E09", "E11", "E21" })
        {
            var check = Facts(id).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);
            Assert.Contains(check.GetProperty("needs")[0].GetProperty("where").GetProperty("id").GetString()!, fired);
        }
    }

    /// <summary>
    /// Эффекты событий карты 3 — как у оригинала (16.10, <c>20261016b-port-synth</c>, сохранение дня 1 <c>…-t01-009.sg</c>, после всех
    /// ворот): порт проходит те же ворота и сравнивает с сохранением движка — где встал созданный отряд (E01), куда подошёл и куда
    /// перенесён нейтрал (E04, E07), приказ «бродить» (E16), убранный отряд (E18), выданный и уничтоженный предметы (E06, E17), снесённый
    /// и сменённый ориентир (E13, E14), земля 3×3 (E19), союз и отношение (E08, E10), переменная (E20). Бой E05 и условия C1–C6 здесь не
    /// повторяются (бой — в журнале оригинала, условия — квест-лог сохранения: все 27 окон дня 1).
    /// </summary>
    [SkippableFact]
    public void Events_EffectsMatchOriginalDayOne()
    {
        const string id = "port-synth-3-events";
        Skip.IfNot(MapIds().Contains(id));
        string orig = Path.Combine(GameSaveTests.CorpusDir(), "20261016b-port-synth", "port-synth-3-events-human-t01-009.sg");
        Skip.IfNot(File.Exists(orig), "нет корпуса 20261016b-port-synth");
        var engine = MapLooksLoader.Load(orig).Map;
        var session = Begin(id, 1);
        session.EventBattlesOnScreen = false;
        var start = session.Map;
        foreach (var z in Facts(id).GetProperty("zones").EnumerateArray().Where(z => z.TryGetProperty("gate_at", out _)))
        {
            var gate = z.GetProperty("gate_at");
            session.MoveStack(z.GetProperty("stack").GetString()!, gate[0].GetInt32(), gate[1].GetInt32(), 0, 0);
        }
        var port = session.Map;
        static string Where(MapStack? s) => s is null ? "нет" : $"({s.X},{s.Y}) приказ {s.Order}";
        static IEnumerable<string> Created(MapState before, MapState after, string owner) =>
            after.Stacks.Values.Where(s => !before.Stacks.ContainsKey(s.Id) && s.Owner == owner).Select(s => $"({s.X},{s.Y})").Order();
        var rows = new List<(string Key, string Engine, string Port)>
        {
            ("E01 созданный отряд", string.Join(" ", Created(start, engine, "S143PL0001")), string.Join(" ", Created(start, port, "S143PL0001"))),
        };
        foreach (var (key, stack) in new[] { ("E04 подвести", "S143KC0008"), ("E07 перенести", "S143KC000d"), ("E16 приказ", "S143KC0015"),
                                             ("E18 убрать", "S143KC0018") })
            rows.Add((key, Where(engine.Stacks.GetValueOrDefault(stack)), Where(port.Stacks.GetValueOrDefault(stack))));
        string Items(MapState m, string stack) => m.Stacks.TryGetValue(stack, out var s)
            ? string.Join(" ", s.Items.Select(i => m.ItemTypes.GetValueOrDefault(i, i).ToUpperInvariant()).Order()) : "нет";
        rows.Add(("E06 выдать предмет", Items(engine, "S143KC000c"), Items(port, "S143KC000c")));
        rows.Add(("E17 уничтожить предмет", Items(engine, "S143KC0017"), Items(port, "S143KC0017")));
        string Mark(MapState m, string mark) => m.Objects.TryGetValue(mark, out var o) ? o.TypeId.ToUpperInvariant() : "нет";
        rows.Add(("E13 снести ориентир", Mark(engine, "S143MM0000"), Mark(port, "S143MM0000")));
        rows.Add(("E14 сменить ориентир", Mark(engine, "S143MM0001"), Mark(port, "S143MM0001")));
        string Land(MapState m) => string.Join("", from y in Enumerable.Range(21, 3) from x in Enumerable.Range(55, 3) select m.Tiles[x, y].Terrain);
        rows.Add(("E19 земля 3×3", Land(engine), Land(port)));
        string Rel(MapState m, int a, int b) => m.Relations.FirstOrDefault(r => r.Pairs(a, b)) is { } r ? $"{r.Current}{(r.Ally ? " союз" : "")}" : "нет";
        rows.Add(("E08 союз с Кланами", Rel(engine, 0, 3), Rel(port, 0, 3)));
        rows.Add(("E10 отношение к Легионам", Rel(engine, 0, 2), Rel(port, 0, 2)));
        foreach (var (key, e, p) in rows) output.WriteLine($"{key}: движок {e}, порт {p}");
        Assert.All(rows, r => Assert.Equal(r.Engine, r.Port));
    }

    [SkippableFact]
    public void World_ThievesHaveTheirActions_AndClanVillageIsTaken()
    {
        const string id = "port-synth-2-world";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        var seen = new Recorder();
        session.Watch(seen);
        var zones = Facts(id).GetProperty("zones").EnumerateArray().ToList();
        foreach (var z in zones.Where(z => z.TryGetProperty("action", out _)))
        {
            var action = Enum.Parse<ThiefAction>(z.GetProperty("action").GetString()!);
            var actions = session.ThiefActions(z.GetProperty("stack").GetString()!, z.GetProperty("target").GetString()!);
            Assert.True(actions.Contains(action), $"{z.GetProperty("key").GetString()}: {string.Join(", ", actions)}");
        }
        var v1 = zones.Single(z => z.GetProperty("key").GetString() == "V1");
        var report = session.Enter(v1.GetProperty("stack").GetString()!, v1.GetProperty("target").GetString()!);
        Assert.True(report.Captured, report.Refused);
        var capture = Assert.Single(seen.Sights, s => s.Kind == TurnSightKind.Capture);
        Assert.True(capture.Occupy);   // у Империи этой деревни не было
        var site = session.Map.Sites.Single(s => s.Id == v1.GetProperty("target").GetString());
        Assert.Contains(3, site.PreviousOwners!);   // прежний хозяин — Кланы (RACE_TYPE 3)

        // S1: «Живой доспех» на клетку у входа — призванный пустую деревню не берёт (CEffectCapture 0x433e2e: лидер IUsSummon, флаг 0):
        // деревня остаётся нейтральной, отряд — снаружи, хозяину — X005TA0458 (партия 15.10, карта 2, пометка 3: призванный вошёл).
        var s1 = zones.Single(z => z.GetProperty("key").GetString() == "S1");
        string s1Id = s1.GetProperty("target").GetString()!;
        var cast = s1.GetProperty("cast_at");
        var before = session.Map.Stacks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Null(session.CastSpell(session.ActiveHuman, "G000SS0008", cast[0].GetInt32(), cast[1].GetInt32(), useSpellBank: false));
        string summoned = Assert.Single(session.Map.Stacks.Keys, k => !before.Contains(k));
        var owner = session.Map.Sites.Single(s => s.Id == s1Id).Owner;
        var visit = session.Enter(summoned, s1Id);
        Assert.Equal(GameSession.CaptureRefusal, visit.Refused);
        Assert.False(visit.Captured);
        var after = session.Map.Sites.Single(s => s.Id == s1Id);
        Assert.Equal(owner, after.Owner);
        Assert.Equal("", after.StackId);
        Assert.Equal("", session.Map.Stacks[summoned].Inside);
        var refused = Assert.Single(seen.Sights, s => s.Kind == TurnSightKind.CaptureRefused);
        Assert.Equal((session.ActiveHuman, s1Id), (refused.PlayerId, refused.Id));
        Assert.Equal("Призванные существа и воры не могут захватывать города.",
                     NoticeWatch.Text(refused, session.Map, Shared.Value.Content.Catalog, UiText, t => t));
    }

    /// <summary>Надетые вещи — как надевание у движка (<c>0x5e746d</c>, цепочка модификаторов юнита <c>0x5ecb47</c>; порт —
    /// <see cref="D2.Rules.Magic.Equipment.Bearers"/>): <c>MOD_EQUIP</c> лежит у носителей — знамя у всех юнитов отряда, прочее у лидера. До
    /// 16.10 генератор клал «Ржавые кандалы» FR в слот без <c>g000um7546</c> (FastRetreat) у лидера. И у каждого лидера есть имя: пустое
    /// движок так и выводит (<c>0x5e97dc</c>), у нейтралов полигонов подписи не было.</summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void EquippedItems_GiveBearersTheirModifier_AndLeadersHaveNames(string id)
    {
        var session = Begin(id, Facts(id).GetProperty("human").GetProperty("difficulties")[0].GetInt32());
        var map = session.Map;
        // Карта переигровки — исходный сценарий байт в байт (maps_replay.py): безымянный лидер там свой (ai-q09 S143KC0089), не генератора.
        bool generated = !Facts(id).TryGetProperty("replay", out _);
        int equipped = 0;
        foreach (var stack in map.Stacks.Values)
        {
            foreach (string instance in stack.Equipment.Where(e => e.Length > 0))
            {
                var item = session.ItemOf(instance);
                Assert.NotNull(item);
                if (item!.EquipModifierId.Length == 0) continue;
                equipped++;
                foreach (string bearer in D2.Rules.Magic.Equipment.Bearers(item, stack))
                    Assert.True(map.Units[bearer].Modifiers.Contains(item.EquipModifierId, StringComparer.OrdinalIgnoreCase),
                                $"{id}: {stack.Id} {instance} — у {bearer} нет {item.EquipModifierId}");
            }
            var look = session.Looks.Stacks.Single(s => s.Id.Equals(stack.Id, StringComparison.OrdinalIgnoreCase));
            Assert.False(generated && string.IsNullOrWhiteSpace(look.LeaderName), $"{id}: у лидера {stack.Id} нет имени");
        }
        output.WriteLine($"{id}: вещей с MOD_EQUIP надето {equipped}");
        if (id == "port-synth-1-battle") Assert.True(equipped > 0);
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> UiTexts = new(() => UiStrings.Load(Path.Combine(Data, "ui"), "ru"));

    private static string UiText(string id) => UiTexts.Value.TryGetValue(id, out var s) ? s : id;

    /// <summary>Партия 15.10, карта 2, пометка 5: вор осмотрел руину, а правая кнопка по ней писала «Руина для вора, еще не исследованы.».
    /// У движка (<c>0x5796c0</c>) неразграбленная руина, среди посетивших которой есть зритель (<c>0x5fda67</c>, набор <c>+0x84</c>; осмотр
    /// пополняет его, <c>0x613164</c>), открывает охрану (<c>0x579831</c>, <c>DLG_R_C_RUIN</c>); осмотр той же руины тем же игроком больше не
    /// предлагается (<c>0x5d35a6</c>).</summary>
    [SkippableFact]
    public void World_SpyRuin_ThenRightClickShowsGuards()
    {
        const string id = "port-synth-2-world";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        var zone = Facts(id).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "T-SpyRuin");
        string thief = zone.GetProperty("stack").GetString()!, ruinId = zone.GetProperty("target").GetString()!;
        string rules = Path.Combine(Data, "rules");
        var context = new WindowContext(session, session.ActiveHuman, UiTexts.Value, UnitFaces.Load(rules));
        var ruin = session.Map.Sites.Single(s => s.Id == ruinId);
        var screens = UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));
        UiScreenState Open(MapCard card)
        {
            var ui = new UiScreenState(card.Logic.Shape(screens.First(s => s.Id == card.Screen)));
            card.Logic.Open(ui);
            return ui;
        }
        MapCard Card() => MapCards.At(context, RaceNames.Load(rules), MapCards.LandmarkNames(rules), ruin.X, ruin.Y, (_, _) => false)!;

        var unseen = Card();
        Assert.Equal("DLG_R_C_MISC", unseen.Screen);
        Assert.EndsWith("еще не исследованы.", Open(unseen).TextOf("TXT_INFO"), StringComparison.Ordinal);
        Assert.Contains(ThiefAction.SpyRuin, session.ThiefActions(thief, ruinId));

        var report = session.Thief(thief, ruinId, ThiefAction.SpyRuin, new D2.Rules.Rng.ScriptedRng([0, 0, 0, 0]));
        Assert.True(report.Success, report.Refused);
        var seen = Card();
        Assert.Equal(("DLG_R_C_RUIN", ruinId), (seen.Screen, seen.ObjectId));
        var ui = Open(seen);
        Assert.Equal(ruin.Name, ui.TextOf("TXT_NAME"));
        string guard = session.Map.Units[ruin.Guards[0]].TypeId;
        Assert.Contains(Enumerable.Range(1, 6), i => ui.TextOf($"TXT_UNIT_{i}") is { Length: > 0 } t
                                                     && t.EndsWith(context.UnitName(guard), StringComparison.Ordinal));
        var target = session.ThiefTarget(session.ActiveHuman, ruinId)!;
        Assert.True(target.RuinVisited);
        Assert.DoesNotContain(ThiefAction.SpyRuin, Thieves.Available(LordCategory.Diplomat, target, 0));
    }

    /// <summary>Бой у руины (<c>CBattleResultRuin</c> <c>0x41b290</c> → <c>CEffectVisitRuin</c>): хозяин нападавшего отряда — в посетивших
    /// руину, чем бы бой ни кончился; руина цела — правая кнопка показывает охрану (<c>DLG_R_C_RUIN</c>), разграблена — «разграблены»
    /// (<c>X005TA0186</c>). До боя — «еще не исследованы».</summary>
    [SkippableFact]
    public void World_BattleAtRuin_AddsVisitor_RightClickShowsGuards()
    {
        const string id = "port-synth-2-world";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        var zone = Facts(id).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "T-SpyRuin");
        string ruinId = zone.GetProperty("target").GetString()!, thief = zone.GetProperty("stack").GetString()!;
        string human = session.ActiveHuman;
        var raider = session.Map.Stacks.Values.First(s => Same(s.Owner, human) && s.Id != thief && s.Inside.Length == 0);
        string rules = Path.Combine(Data, "rules");
        var context = new WindowContext(session, human, UiTexts.Value, UnitFaces.Load(rules));
        var ruin = session.Map.Sites.Single(s => s.Id == ruinId);
        MapCard Card() => MapCards.At(context, RaceNames.Load(rules), MapCards.LandmarkNames(rules), ruin.X, ruin.Y, (_, _) => false)!;
        Assert.Equal("DLG_R_C_MISC", Card().Screen);

        var book = Shared.Value.Book;
        var state = session.StartBattle(raider.Id, ruinId, duel: true);
        var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, book.Rules.Constants), book.Rules.Constants) { Auto = true };
        battle.Resolve();
        var visit = session.FinishBattle(battle.Log);
        output.WriteLine($"победитель={battle.Winner} вход={visit?.Refused ?? "-"} лидер_жив={session.Map.Stacks.GetValueOrDefault(raider.Id)?.LeaderAlive} охрана={string.Join(",", session.Map.Sites.Single(s => s.Id == ruinId).Guards.Select(g => session.Map.Units.TryGetValue(g, out var u) ? u.Hp.ToString() : "нет"))} цель={state.DefenderGroup}");
        var after = session.Map.Sites.Single(s => s.Id == ruinId);
        Assert.Contains(human, after.Visitors, StringComparer.OrdinalIgnoreCase);
        var card = Card();
        output.WriteLine($"бой у руины: разграблена={after.LooterId.Length > 0} карточка={card.Screen}");
        Assert.Equal(after.LooterId.Length > 0 ? "DLG_R_C_MISC" : "DLG_R_C_RUIN", card.Screen);
    }

    /// <summary>Карта 2: <c>S143KC0005</c> бьёт охрану руины <c>S143RU0000</c> и побеждает, но его лидер пал. Движок грабит руину и тогда
    /// (<c>CBattleResultRuin</c> <c>0x41b290</c> → <c>0x44133a</c> → <c>CEffectLootRuin</c>, лидера не смотрит): 100 золота хозяину,
    /// руина разграблена; прежде вход победителя давал отказ «лидер пал» (<c>docs/ui-windows.md</c> §23).</summary>
    [SkippableFact]
    public void World_RuinBattle_WonWithFallenLeader_Loots()
    {
        const string id = "port-synth-2-world";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        var book = Shared.Value.Book;
        const string raider = "S143KC0005", ruinId = "S143RU0000";
        string owner = session.Map.Stacks[raider].Owner;
        var ruin = session.Map.Sites.Single(s => s.Id == ruinId);
        long gold = session.Map.Players[owner].Bank.Gold;
        var state = session.StartBattle(raider, ruinId);
        var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, book.Rules.Constants), book.Rules.Constants) { Auto = true };
        battle.Resolve();
        var visit = session.FinishBattle(battle.Log);
        Assert.Equal(raider, battle.Winner);
        Assert.False(session.Map.Stacks[raider].LeaderAlive);
        Assert.NotNull(visit);
        Assert.Null(visit.Refused);
        var after = session.Map.Sites.Single(s => s.Id == ruinId);
        Assert.Equal(owner, after.LooterId);
        Assert.Equal(gold + ruin.Cash.Gold, session.Map.Players[owner].Bank.Gold);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Партия 15.10, карта 2: у деревни подкупа (FT0009) зонд каждый ход писал «нарушение slots: клетка 2 → индекс 0, юнитов 0» —
    /// подкуп стирал гарнизон, а его раскладку оставлял (<see cref="Fortifications.Capture"/>).</summary>
    [SkippableFact]
    public void World_Bribe_LeavesNoGarrisonLayout()
    {
        const string id = "port-synth-2-world";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 1);
        var zone = Facts(id).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "T-Bribe");
        string thief = zone.GetProperty("stack").GetString()!, target = zone.GetProperty("target").GetString()!;
        var report = session.Thief(thief, target, ThiefAction.Bribe, new D2.Rules.Rng.ScriptedRng([0, 0, 0, 0]));
        Assert.True(report.Success, report.Refused);
        var village = session.Map.Sites.Single(s => s.Id == target);
        Assert.Equal(session.ActiveHuman, village.Owner, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(village.Guards);
        Assert.Empty(village.GuardSlots);
        Assert.Empty(D2.Data.Diagnostics.MapInvariants.Check(session.Map, session.Book!, null));
    }

    [SkippableFact]
    public void Battle_ArenasResolve_AndReportAttackClasses()
    {
        const string id = "port-synth-1-battle";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 0);
        var seen = new Recorder();
        session.Watch(seen);
        var rules = session.Book!.Rules;
        foreach (var z in Facts(id).GetProperty("zones").EnumerateArray())
        {
            string stack = z.GetProperty("stack").GetString()!, target = z.GetProperty("target").GetString()!;
            var state = session.StartBattle(stack, target);
            var battle = new D2.Data.Battle.BattleSession(state, new BattleSim(state, session.Rng, rules.Constants), rules.Constants) { Auto = true };
            var initial = state.Units.Where(u => u.Attacker).SelectMany(u => new[] { u.Attack.Class, u.Attack2?.Class }).ToList();
            battle.Resolve();
            // Бой доигран (а не встал на потолке действий Resolve): без этого «победа защитника» печаталась и при зацикленном автобое.
            Assert.True(battle.Finished, $"{z.GetProperty("key").GetString()}: автобой не кончился за {D2.Data.Battle.BattleSession.MaxActions} действий, раунд {battle.Round}");
            var allowed = initial.Concat(state.Units.Where(u => u.Attacker).Select(u => (AttackClass?)u.Attack.Class))
                                 .OfType<AttackClass>().Select(c => c.ToString()).ToHashSet();
            session.FinishBattle(battle.Log);
            var outcome = seen.Outcomes[^1];
            Assert.Equal(stack, outcome.Attacker, StringComparer.OrdinalIgnoreCase);
            // классы итога — только классы юнитов этой стороны (первая и вторая атака, призванные — своей)
            Assert.All(outcome.AttackerClasses, c => Assert.Contains(c, allowed));
            output.WriteLine($"{z.GetProperty("key").GetString()}: {string.Join(",", outcome.AttackerClasses)} / {string.Join(",", outcome.DefenderClasses)}; " +
                             $"предметы {string.Join(",", outcome.ItemsUsed)}; победил {outcome.Winner}, раундов {outcome.Rounds}, " +
                             $"пало {outcome.AttackersFallen}/{outcome.DefendersFallen}, бежало {outcome.AttackersRetreated}/{outcome.DefendersRetreated}");
        }
        // Автобой (политика ИИ за обе стороны) берёт не все редкие классы — призыв, усиление, воскрешение играет человек по памятке;
        // какие выпали — зависит от бросков, поэтому здесь только печать, а проверка — что каждый бой полигона прошёл и итог честный.
        Assert.Equal(Facts(id).GetProperty("zones").GetArrayLength(), seen.Outcomes.Count);
    }

    /// <summary>Зонд порта пишет то, по чему разбор закрывает вопросы карт: в <c>battle_end</c> — классы атак сторон, применённые предметы,
    /// бежавших (<c>classes</c>, <c>items</c>, <c>retreated</c>), в <c>sight</c> захвата — <c>occupy</c>.</summary>
    [SkippableFact]
    public void Probe_WritesBattleClasses_AndCaptureOccupy()
    {
        Skip.IfNot(MapIds().Contains("port-synth-1-battle") && MapIds().Contains("port-synth-2-world"));
        string path = Path.Combine(Path.GetTempPath(), $"portsynth-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var journal = new D2.Data.Diagnostics.PortJournal(path))
            {
                var battle = Begin("port-synth-1-battle", 0);
                battle.Watch(new D2.Data.Diagnostics.PortJournalWatcher(journal, battle, autosave: false));
                // I2: до первого хода лидер выпивает Эликсир исцеления (боевой слот 1) за раненого рыцаря — предмет обязан попасть в items
                var i2 = Facts("port-synth-1-battle").GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "I2");
                var rules = battle.Book!.Rules;
                var state = battle.StartBattle(i2.GetProperty("stack").GetString()!, i2.GetProperty("target").GetString()!);
                var sim = new BattleSim(state, battle.Rng, rules.Constants);
                var bearer = state.Units.Single(u => u.Attacker && u.BattleItems.Count > 0);
                var wounded = state.Units.First(u => u.Attacker && u.Hp > 0 && u.Hp < u.HpMax);
                int potion = bearer.BattleItems.FindIndex(i => i.Id.Equals("G000IG0005", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(PotionResult.Ok, sim.DrinkPotion(bearer, potion, wounded));
                var fight = new D2.Data.Battle.BattleSession(state, sim, rules.Constants) { Auto = true };
                fight.Resolve();
                battle.FinishBattle(fight.Log);

                var world = Begin("port-synth-2-world", 1);
                world.Watch(new D2.Data.Diagnostics.PortJournalWatcher(journal, world, autosave: false));
                var v1 = Facts("port-synth-2-world").GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "V1");
                Assert.True(world.Enter(v1.GetProperty("stack").GetString()!, v1.GetProperty("target").GetString()!).Captured);
            }
            var lines = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            var end = Assert.Single(lines, l => l.GetProperty("ev").GetString() == "battle_end");
            var classes = end.GetProperty("classes");
            Assert.Equal(2, classes.GetArrayLength());
            Assert.NotEmpty(classes[0].EnumerateArray());
            Assert.Equal(2, end.GetProperty("retreated").GetArrayLength());
            // Бежавшие поимённо: вопрос FR карты 1 закрывает бегство самого лидера, а не число бежавших.
            Assert.Equal(JsonValueKind.Array, end.GetProperty("retreated_units").ValueKind);
            Assert.Contains("G000IG0005", end.GetProperty("items").EnumerateArray().Select(i => i.GetString()), StringComparer.OrdinalIgnoreCase);
            var capture = Assert.Single(lines, l => l.GetProperty("ev").GetString() == "sight" && l.GetProperty("kind").GetString() == "Capture");
            Assert.True(capture.GetProperty("occupy").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Партия 15.10 (пометки 3, 4, 5, 12), полигон A2 боем с карты по ходам человека: павший сквайр лежит в бою на своей клетке
    /// (<see cref="MapBattle"/>), Повелитель стихий призывает на выбранную позицию, Патриарх поднимает сквайра второй атакой, а зонд пишет в
    /// <c>battle_end classes</c> и призыв, и воскрешение (прежде — только классы с записью хода).</summary>
    [SkippableFact]
    public void A2_HumanSummonsAndRevives_DeadSquireInBattle_ProbeSeesClasses()
    {
        const string id = "port-synth-1-battle";
        Skip.IfNot(MapIds().Contains(id));
        string path = Path.Combine(Path.GetTempPath(), $"portsynth-a2-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var journal = new D2.Data.Diagnostics.PortJournal(path))
            {
                var game = Begin(id, 0);
                game.Watch(new D2.Data.Diagnostics.PortJournalWatcher(journal, game, autosave: false));
                var a2 = Facts(id).GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("key").GetString() == "A2");
                var rules = game.Book!.Rules;
                var state = game.StartBattle(a2.GetProperty("stack").GetString()!, a2.GetProperty("target").GetString()!);
                var squire = Assert.Single(state.Units, u => u.Attacker && u.Type.Id.Equals("g000uu0001", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(0, squire.Hp);   // павший — в бою, костями на своей клетке
                var fight = new D2.Data.Battle.BattleSession(state, new BattleSim(state, game.Rng, rules.Constants), rules.Constants);
                int summoned = 0;
                for (int step = 0; step < 2000 && !fight.Finished; step++)
                {
                    if (!fight.WaitsPlayer) { fight.AiStep(); continue; }
                    var unit = fight.Current!;
                    if (fight.SummonSlots() is { Count: > 0 } cells && summoned++ == 0) fight.SummonAt(cells[^1]);
                    else if (unit.Attack2?.Class == AttackClass.Revive && fight.Targets().Contains(squire)) fight.Attack(squire);
                    else fight.AiStep();
                }
                Assert.True(squire.Revived);
                game.FinishBattle(fight.Log);
            }
            var end = Assert.Single(File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement.Clone()),
                                    l => l.GetProperty("ev").GetString() == "battle_end");
            var own = end.GetProperty("classes")[0].EnumerateArray().Select(c => c.GetString()).ToList();
            output.WriteLine("классы нападающих: " + string.Join(",", own));
            Assert.Contains("Summon", own);
            Assert.Contains("Revive", own);
            Assert.Contains("Heal", own);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Партия оригинала 16.10b: вторые атаки A3–A5 не сработали из-за целей — у Гномов нет <c>PREV_ID</c> (понижение уровня Сущего) и
    /// брони (разбить броню Тиурга), Отшельник бросил замедление раз из пяти по одному Хугину. Новые цели годятся по таблицам (у Ветеранов A3
    /// предыдущий уровень есть, у Латников A4 броня), в A5 два носителя замедления против шести целей, и бои полигонов с карты (ходы человека —
    /// политикой автобоя, Сущий бьёт непониженного с предыдущим уровнем) на 40 сидах дают вторую атаку почти всегда, а в A5 ходят все пятеро.
    /// С 16.10 порт отказывает замедлению, понижению уровня и разбить броню, как движок (<see cref="D2.Rules.Battle.Attacks.CanPerform"/>:
    /// <c>0x65de8f</c>, <c>0x662608</c>, <c>0x65d741</c>); третья партия оригинала (20261016c) — все три сработали.</summary>
    [SkippableFact]
    public void A3ToA5_SecondAttacks_HaveTargets_AndLand()
    {
        const string id = "port-synth-1-battle";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 0);
        var book = session.Book!;
        var zones = Facts(id).GetProperty("zones").EnumerateArray().ToDictionary(z => z.GetProperty("key").GetString()!);
        BattleState Fresh(string key, int seed)
        {
            var z = zones[key];
            string stack = z.GetProperty("stack").GetString()!;
            var state = MapBattle.Build(session.Map, stack, MapBattle.DefenderOf(session.Map, z.GetProperty("target").GetString()!)!, book, 0,
                                        session.Map.Stacks[stack].Move);
            state.ProcessRand = new D2.Rules.Rng.CrtRand((uint)seed);
            return state;
        }
        var a3 = Fresh("A3", 1).Units.Where(u => !u.Attacker).ToList();
        Assert.True(a3.Count(u => book.Rules.Units.ContainsKey(u.Type.PrevId)) >= 2, "A3: целей с предыдущим уровнем меньше двух");
        Assert.All(Fresh("A4", 1).Units.Where(u => !u.Attacker), u => Assert.True(u.Type.Armor > 0, $"A4: {u.Type.Id} без брони"));
        var a5 = Fresh("A5", 1);
        Assert.Equal(2, a5.Units.Count(u => u.Attacker && u.Attack2?.Class == AttackClass.LowerInitiative));
        Assert.Equal(6, a5.Units.Count(u => !u.Attacker));

        const int seeds = 40;
        // Легла ли вторая атака класса: запись хода номер 2 без промаха от её носителя (замедление снимается в ход цели, по концу боя не видно).
        static bool Landed(BattleState s, IReadOnlyList<TurnRecord> log, AttackClass cls) =>
            log.Any(r => r.Attack == 2 && !r.Miss && s.Units.Single(u => u.Id == r.Unit).Attack2?.Class == cls);
        foreach (var (key, landed, need) in new (string, Func<BattleState, IReadOnlyList<TurnRecord>, bool>, double)[]
                 {
                     ("A3", (s, _) => s.Units.Any(u => !u.Attacker && u.LevelDrained), 0.9),
                     ("A4", (s, _) => s.Units.Any(u => !u.Attacker && u.ShatteredArmor > 0), 0.9),
                     ("A5", (s, log) => Landed(s, log, AttackClass.LowerInitiative), 0.9),
                 })
        {
            int hits = 0, allActed = 0;
            for (int seed = 1; seed <= seeds; seed++)
            {
                var state = Fresh(key, seed);
                var fight = new D2.Data.Battle.BattleSession(state, new BattleSim(state, new D2.Rules.Rng.EngineRng(seed), book.Rules.Constants),
                                                             book.Rules.Constants);
                for (int step = 0; step < 2000 && !fight.Finished; step++)
                {
                    var unit = fight.Current;
                    var drainable = unit is { Attack2.Class: AttackClass.DrainLevel } && fight.WaitsPlayer
                        ? fight.Targets().Where(t => book.Rules.Units.ContainsKey(t.Type.PrevId) && !t.LevelDrained).MaxBy(t => t.Hp) : null;
                    if (drainable is not null) fight.Attack(drainable);
                    else fight.AiStep();
                }
                hits += landed(state, fight.Log) ? 1 : 0;
                var acted = state.Actions.Select(a => a.Unit).ToHashSet(StringComparer.OrdinalIgnoreCase);
                allActed += state.Units.Where(u => u.Attacker).All(u => acted.Contains(u.Id)) ? 1 : 0;
            }
            output.WriteLine($"{key}: вторая атака легла в {hits} боях из {seeds}, ходили все наши — в {allActed}");
            Assert.True(hits >= need * seeds, $"{key}: вторая атака легла в {hits} боях из {seeds}");
            Assert.True(allActed >= 0.9 * seeds, $"{key}: все наши ходили лишь в {allActed} боях из {seeds}");
        }
    }

    /// <summary>Итог боя зонду знает атаки по своим и призыв: у них нет записи хода (<see cref="BattleSim.Act"/>), и в партии 15.10 итог терял
    /// усиление, дар атаки, призыв, обереги и превращение себя, хотя на экране они были (полигоны A1–A5 «открыты» при сыгранных умениях).
    /// A1: Травница усиливает и Алхимик даёт атаку рыцарю; A2: Повелитель стихий призывает — классы есть в <c>battle_end</c>.</summary>
    [SkippableFact]
    public void Probe_BattleClasses_IncludeAllyAttacksAndSummon()
    {
        const string id = "port-synth-1-battle";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 0);
        var seen = new Recorder();
        session.Watch(seen);
        var rules = session.Book!.Rules;
        var zones = Facts(id).GetProperty("zones").EnumerateArray().ToDictionary(z => z.GetProperty("key").GetString()!);
        foreach (var (key, acts, want) in new (string, (string Actor, string? Target)[], string[])[]
                 {
                     ("A1", [("g000uu0033", "g000uu0019"), ("g000uu0161", "g000uu0019")], ["BoostDamage", "GiveAttack"]),
                     ("A2", [("g000uu0153", null)], ["Summon"]),
                 })
        {
            var z = zones[key];
            var state = session.StartBattle(z.GetProperty("stack").GetString()!, z.GetProperty("target").GetString()!);
            var sim = new BattleSim(state, session.Rng, rules.Constants);
            BattleUnit Own(string type) => state.Units.First(u => u.Attacker && u.Type.Id.Equals(type, StringComparison.OrdinalIgnoreCase));
            foreach (var (actor, target) in acts) sim.Act(state.Round, Own(actor), target is null ? null : Own(target));
            var fight = new D2.Data.Battle.BattleSession(state, sim, rules.Constants) { Auto = true };
            fight.Resolve();
            session.FinishBattle(fight.Log);
            var classes = seen.Outcomes[^1].AttackerClasses;
            output.WriteLine($"{key}: {string.Join(",", classes)}");
            Assert.All(want, c => Assert.Contains(c, classes));
        }
    }

    /// <summary>ИИ-карта: в день 1 каждый отряд полигонов ИИ (4 обычных, 2 вора, 10 с приказами; «стоять» и «охрана» стоят) выбирает действие — вопросы карты по
    /// <c>ai_pick</c> достижимы; печатается, какие тактики выбраны за три дня (лёгкая сложность).</summary>
    [SkippableFact]
    public void Ai_EveryPolygonStack_PicksOnDayOne()
    {
        const string id = "port-synth-4-ai";
        Skip.IfNot(MapIds().Contains(id));
        var session = Begin(id, 0);
        var trace = new PickTrace();
        session.AiTrace = trace;
        for (int k = 0; k < 3; k++) session.EndTurn();
        var day1 = trace.Picks.Where(p => p.Day == 1).Select(p => p.Stack).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var zone in Facts(id).GetProperty("zones").EnumerateArray())
        {
            var ids = zone.GetProperty("normals").EnumerateArray().Concat(zone.GetProperty("nobles").EnumerateArray()).Select(e => e.GetString()!)
                          .Concat(zone.GetProperty("ordered").EnumerateArray().Where(o => o[0].GetString() is not ("Stand" or "Guard"))
                                      .Select(o => o[2].GetString()!));
            missing.AddRange(ids.Where(s => !day1.Contains(s)).Select(s => $"{zone.GetProperty("key").GetString()} {s}"));
        }
        output.WriteLine("тактики за 3 дня: " + string.Join(", ", trace.Picks.GroupBy(p => p.Action).OrderByDescending(g => g.Count())
                                                                     .Select(g => $"{g.Key} {g.Count()}")));
        output.WriteLine("без выбора в день 1: " + string.Join(", ", missing));
        Assert.Empty(missing);
    }

    /// <summary>Партии 15.10 на ИИ-карте (лёгкая 12 дней, очень трудная 14): не выпали Retreat, Guard, NobleCity, HireMercs. У Легионов карта
    /// ставит их условия (<c>maps.py</c> <c>_rare_tactics</c>): гость Кланов в деревне Кланов у полигона (бегство и знатный по городу),
    /// страж Нежити вплотную к стражу Легионов (охрана), отряд у лагеря наёмников далеко от столицы. На обеих сложностях памятки все четыре
    /// выбираются за дни партии.</summary>
    [SkippableFact]
    public void Ai_RareTactics_PickedWithinMemoDays()
    {
        const string id = "port-synth-4-ai";
        Skip.IfNot(MapIds().Contains(id));
        string[] rare = ["Retreat", "Guard", "NobleCity", "HireMercs"];
        foreach (var difficulty in Facts(id).GetProperty("human").GetProperty("difficulties").EnumerateArray())
        {
            var session = Begin(id, difficulty.GetInt32());
            var trace = new PickTrace();
            session.AiTrace = trace;
            for (int k = 0; k < 10 && !rare.All(r => trace.Picks.Any(p => p.Action == r)); k++) session.EndTurn();
            var first = rare.ToDictionary(r => r, r => trace.Picks.FirstOrDefault(p => p.Action == r));
            output.WriteLine($"сложность {difficulty}: " + string.Join(", ", first.Select(f => $"{f.Key} — " +
                (f.Value.Stack is null ? "нет" : $"день {f.Value.Day} {f.Value.Stack}"))));
            Assert.All(rare, r => Assert.True(first[r].Stack is not null, $"сложность {difficulty}: {r} не выбран за 10 дней"));
        }
    }

    private sealed class PickTrace : IAiTrace
    {
        public List<(string Stack, int Day, string Action)> Picks { get; } = [];

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) =>
            Picks.Add((action.Stack, day, action.Type.ToString()));

        public void Stepped(string playerId, int day, AiExecuted step, int number) { }

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }

    private static GameSession Begin(string id, int difficulty, uint? reseed = null)
    {
        var facts = Facts(id);
        var human = facts.GetProperty("human");
        var content = Shared.Value.Content;
        var setup = new D2.Data.Menu.NewGameSetup
        {
            Scenario = content.Catalog.Scenarios.Single(s => s.Id == id), Race = human.GetProperty("race_type").GetInt32(),
            LordCategory = human.GetProperty("lord_category").GetInt32(), Difficulty = difficulty,
        };
        return NewGame.Begin(Scenario(id), setup, content.Catalog, Shared.Value.Book, content.MapArt, new Random(1), reseed);
    }

    private sealed class Recorder : ITurnObserver
    {
        public List<EventFiring> Events { get; } = [];

        public List<TurnSight> Sights { get; } = [];

        public List<BattleOutcome> Outcomes { get; } = [];

        public void PlayerTurn(string playerId) { }

        public void StackMoving(StackMove move) { }

        public IReadOnlyList<TurnRecord>? Battle(BattleState state) => null;

        public void Happened(TurnSight sight) => Sights.Add(sight);

        public void BattleEnded(BattleOutcome outcome) => Outcomes.Add(outcome);

        public void EventFired(EventFiring firing) => Events.Add(firing);
    }
}
