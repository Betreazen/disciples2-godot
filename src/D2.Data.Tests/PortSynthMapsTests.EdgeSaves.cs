using D2.Data.Corpus;
using D2.Data.Map;
using D2.Rules.Map;
using D2.Rules.Rng;

namespace D2.Data.Tests;

/// <summary>Карта 8 и карта 1 против журналов и сохранений оригинала: строки реестра, которые журнал уже закрыл без новой карты (В14, В15, Б22).</summary>
public partial class PortSynthMapsTests
{
    private const string EdgeRun = "20261016g-port-synth";

    /// <summary>Класс атаки «двойник» (<c>attack_hit.disp_class</c>).</summary>
    private const long DoppelgangerClass = 20;

    /// <summary>Бит 3 флагов юнита <c>battle_start</c>: сторона нападающего.</summary>
    private const long AttackerSideFlag = 8;

    /// <summary>
    /// Воры карты 8 у движка (<c>corpus/20261016g-port-synth</c>, вторая загрузка): K2 убил Гоблина (<c>thief_roll</c> 29 и 77 при 60), K1
    /// провалился, H1 и H2 повешены у Хьюберта (79 и 55, 79 и 89 при 10). Порт с теми же исходами бросков — против сохранений движка
    /// <c>t01-011 … t01-014</c>: убийца в <c>CMidStackDestroyed</c> — хозяин вора при убийстве (В14), нейтралы у повешенного вора, а вещи
    /// повешенного — мешком на его клетке (В15, <c>0x4413d9</c>).
    /// </summary>
    [SkippableFact]
    public void Edge_ThiefDeaths_LikeTheOriginalSaves()
    {
        Skip.IfNot(MapIds().Contains(EdgeMap));
        string dir = Path.Combine(GameSaveTests.CorpusDir(), EdgeRun);
        string last = Path.Combine(dir, EdgeMap + "-undead-t01-014.sg");
        Skip.IfNot(File.Exists(last), $"нет {last}");
        var engine = MapLooksLoader.Load(last).Map;
        var facts = Facts(EdgeMap);
        var session = Begin(EdgeMap, 2);
        int bags = 0;
        string neutralId = session.Map.Players.Values.Single(p => p.RaceId.Equals("G000RR0004", StringComparison.OrdinalIgnoreCase)).Id;
        // исходы журнала: K1 провал, K2 успех, H1 и H2 провал (кубики — тем же ScriptedRng: 0 — успех, 99 — провал)
        foreach (var (key, success) in new[] { ("K1", false), ("K2", true), ("H1", false), ("H2", false) })
        {
            var z = Zone(facts, key);
            var thief = session.Map.Stacks[Str(z, "stack")];
            var carried = thief.Items.Select(i => session.Map.ItemTypes.GetValueOrDefault(i, i).ToUpperInvariant()).Order().ToList();
            var report = session.Thief(thief.Id, Str(z, "target"), ThiefAction.Assassinate, new ScriptedRng(success ? [0, 0, 0, 0] : [99, 99, 99, 99]));
            Assert.Equal(success, report.Success);
            string victim = success ? Str(z, "target") : thief.Id;
            var mine = Assert.Single(session.Map.Destroyed, d => Same(d.StackId, victim));
            var theirs = Assert.Single(engine.Destroyed, d => Same(d.StackId, victim));
            Out.WriteLine($"{key}: погиб {victim}, убийца — порт {mine.KillerId}, движок {theirs.KillerId}");
            Assert.Equal(theirs.KillerId, mine.KillerId, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(success ? thief.Owner : neutralId, mine.KillerId, StringComparer.OrdinalIgnoreCase);
            if (success || carried.Count == 0) continue;   // у K1 вещей нет
            var bag = session.Map.Sites.Single(s => s.Kind == SiteKind.Bag && s.X == thief.X && s.Y == thief.Y);
            var engineBag = engine.Sites.Single(s => s.Kind == SiteKind.Bag && s.X == thief.X && s.Y == thief.Y);
            Out.WriteLine($"   мешок на ({thief.X},{thief.Y}): порт {BagTypes(session.Map, bag)}, движок {BagTypes(engine, engineBag)}");
            Assert.Equal(BagTypes(engine, engineBag), BagTypes(session.Map, bag));
            bags++;
        }
        Assert.Equal(2, bags);   // H1, H2 — повешенные с вещами
    }

    private static string BagTypes(MapState map, MapSite bag) =>
        string.Join(", ", bag.Items.Select(i => map.ItemTypes.GetValueOrDefault(i, i).ToUpperInvariant()).Order());

    /// <summary>
    /// Двойник выбирает целью и своих (Б22): у движка в журнале копия своего — <c>attack_hit</c> класса 20 (<c>Doppelganger</c>), где цель —
    /// юнит той же группы, у человека (карта 1, A3: <c>20261016-port-synth</c>, S143UN0019 копирует S143UN001a). Порт берёт целями своих
    /// так же (<c>BattleTargets.Copies</c>, <c>0x661a7a</c>).
    /// </summary>
    [SkippableFact]
    public void Battle_DoppelgangerCopiesOwn_InTheOriginalJournal()
    {
        string journal = Path.Combine(GameSaveTests.CorpusDir(), "20261016-port-synth", "passive-001.jsonl");
        Skip.IfNot(File.Exists(journal), $"нет {journal}");
        // сторона юнита — бит 3 флагов battle_start (у нападающих 8…13, у защитников 0…5)
        var attackerSide = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int own = 0, foreign = 0;
        bool a3 = false;
        foreach (var e in JournalCache.Read(journal, new[] { "battle_start", "attack_hit" }))
        {
            if (e is BattleStartEvent start)
            {
                foreach (var u in start.Units) attackerSide[u.Id] = (u.Flags & AttackerSideFlag) != 0;
                continue;
            }
            if (e is not AttackHitEvent { DispClass: DoppelgangerClass } hit) continue;
            a3 |= Same(hit.Attacker, "S143UN0019") && Same(hit.Target, "S143UN001a");
            if (attackerSide.TryGetValue(hit.Attacker, out bool a) && attackerSide.TryGetValue(hit.Target, out bool t))
            {
                if (a == t) own++;
                else foreign++;
            }
        }
        Out.WriteLine($"копий двойника: своих {own}, чужих {foreign}");
        Assert.True(a3, "нет копии S143UN0019 → S143UN001a (A3)");
        Assert.True(own > 0, "у движка двойник своих не копировал");
    }
}
