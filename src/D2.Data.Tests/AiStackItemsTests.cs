using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;

namespace D2.Data.Tests;

/// <summary>Подготовка отряда ИИ (<see cref="AiStackItems"/>, <c>docs/ai-items.md</c>) на сохранении живой партии
/// (<c>20260919-human</c>, день 1, до хода ИИ).</summary>
[Trait("Category", "Corpus")]
public class AiStackItemsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Журнал, seq 1409899–1409916: PL0009 надевает лидеру <c>S073KC0000</c> лечебные зелья — <c>S073IM0000</c> в
    /// слот 2 (первое годное с наибольшей оценкой 100 + 20000), <c>S073IM0001</c> в слот 3 (после зелья лечебное —
    /// 100 + 40000); в прочие слоты зелья не годятся.</summary>
    [SkippableFact]
    public void Prepare_EquipsBattleSlots_AsJournal()
    {
        var game = Load();
        var report = new List<string>();

        AiStackItems.Prepare(game, "S073KC0000", report);

        var slots = Equipment.Slots(game.Map.Stacks["S073KC0000"]);
        Assert.Equal(["", "", "S073IM0000", "S073IM0001", "", "", ""], slots.Select(s => s.ToUpperInvariant()));
    }

    /// <summary>Оценка <c>0x45224b</c>: не боевой слот — 1; в первом боевом лечебное — лечит + 20000, воскрешение 10000;
    /// во втором после зелья — лечебное + 40000, воскрешение 30000.</summary>
    [SkippableFact]
    public void Score_BattleSlots()
    {
        var game = Load();
        var stack = game.Map.Stacks["S073KC0000"];
        Assert.Equal(1, AiStackItems.Score(game, stack, "S073IM0000", Equipment.SlotBanner, null));
        Assert.Equal(100 + 20000, AiStackItems.Score(game, stack, "S073IM0000", Equipment.SlotBattle1, null));
        Assert.Equal(10000, AiStackItems.Score(game, stack, "S073IM009b", Equipment.SlotBattle1, null));
        Assert.Equal(100 + 40000, AiStackItems.Score(game, stack, "S073IM0000", Equipment.SlotBattle2, ItemCategory.PotionHeal));
        Assert.Equal(30000, AiStackItems.Score(game, stack, "S073IM009b", Equipment.SlotBattle2, ItemCategory.PotionHeal));
        Assert.Equal(0, AiStackItems.Score(game, stack, "", Equipment.SlotBattle1, null));
    }

    /// <summary>Журнал, seq 1409862–1409866: PL0009 забирает в <c>S073KC0000</c> из гарнизона столицы <c>01a2</c> в клетку 0,
    /// <c>01e8</c> в 1, <c>01e9</c> в 3; сохранение дня 2 — лидер в 0, <c>01e8</c> в 1, <c>01a2</c> в 4, <c>01e9</c> в 5, порядок
    /// юнитов — в порядке переноса.</summary>
    [SkippableFact]
    public void TakeFromGarrison_AsJournalAndSave()
    {
        var game = Load();
        var report = new List<string>();

        AiStackUnits.TakeFromGarrison(game, "S073KC0000", report);

        Assert.Equal(["перенос S073FT0000[4] S073UN01a2 → S073KC0000[0] -", "перенос S073FT0000[1] S073UN01e8 → S073KC0000[1] -",
                      "перенос S073FT0000[3] S073UN01e9 → S073KC0000[3] -"], report, StringComparer.OrdinalIgnoreCase);
        var stack = game.Map.Stacks["S073KC0000"];
        Assert.Equal(["S073UN01F9", "S073UN01A2", "S073UN01E8", "S073UN01E9"], stack.UnitIds.Select(u => u.ToUpperInvariant()));
        Assert.Equal([0, 2, -1, -1, 1, 3], stack.Slots);
    }

    /// <summary>Вор и жезлоносец сдают сумку на склад крепости (<c>0x451a43</c>): надетое снимается, сумка пустеет.</summary>
    [SkippableFact]
    public void GiveFortItems_UnequipsAndEmptiesBag()
    {
        var game = Load();
        AiStackItems.Prepare(game, "S073KC0000", []);
        var bag = game.Map.Stacks["S073KC0000"].Items.ToList();
        Assert.NotEmpty(bag);
        string fortId = game.Map.Stacks["S073KC0000"].Inside;
        int fortBefore = game.Map.Sites.First(s => s.Id.Equals(fortId, StringComparison.OrdinalIgnoreCase)).Items.Count;

        game.GiveFortItems("S073KC0000");

        var stack = game.Map.Stacks["S073KC0000"];
        Assert.Empty(stack.Items);
        Assert.All(Equipment.Slots(stack), s => Assert.Equal("", s));
        var fort = game.Map.Sites.First(s => s.Id.Equals(stack.Inside, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(fortBefore + bag.Count, fort.Items.Count);
        Assert.Equal(bag.Order(StringComparer.OrdinalIgnoreCase), fort.Items.TakeLast(bag.Count));
    }

    /// <summary>«Предмет ничего не даст» (<c>0x5EC48F</c>) по бонусу хода и иммунитету, а не только по способности: лидеру, у
    /// которого (сохранение human дня 62) модификатор сапог или оберега уже в цепочке (<see cref="GameSession.LeaderEffectsOf"/>), тот же предмет не
    /// надевается ни окном (<see cref="GameSession.EquipItem"/>), ни подготовкой ИИ; без модификатора — надевается.</summary>
    [SkippableTheory]
    [InlineData(D2.Rules.Units.ModifierElementType.MoveAbility)]
    [InlineData(D2.Rules.Units.ModifierElementType.Immunity)]
    public void Equip_NoEffect_ByGroundAndImmunityInChain(D2.Rules.Units.ModifierElementType kind)
    {
        var game = Load("-t62-");
        var book = game.Book!;
        static int Slot(ItemCategory c) => c switch
        {
            ItemCategory.TravelItem => Equipment.SlotBoots,
            ItemCategory.Jewel => Equipment.SlotTome,
            _ => Equipment.SlotBanner,
        };
        // Лидер со свободным слотом, которому предмет сейчас что-то даёт.
        var found = (from it in book.Items.Values
                     where it.Category is ItemCategory.TravelItem or ItemCategory.Jewel or ItemCategory.Banner
                           && it.EquipModifierId.Length > 0
                           && book.Rules.Modifiers!.TryGetValue(it.EquipModifierId, out var m) && m.Elements.All(e => e.Type == kind)
                     from st in game.Map.Stacks.Values
                     where game.Map.Units.TryGetValue(st.LeaderId, out var l) && l.Hp > 0 && Equipment.Slots(st)[Slot(it.Category)].Length == 0
                           && Equipment.Check(it, Slot(it.Category), game.LeaderAbilitiesOf(st), true, book.Rules.Modifiers, game.LeaderEffectsOf(st)) == EquipRefusal.Ok
                     orderby it.Id, st.Id
                     select (Item: it, Stack: st)).FirstOrDefault();
        Skip.If(found.Item is null, "нет пары предмет — лидер");
        var (item, stack) = found;
        int slot = Slot(item.Category);
        const string Instance = "S073IMfffd";
        GameSession With(bool chain)
        {
            var map = game.Map;
            var stacks = map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            stacks[stack.Id] = stack with { Items = [.. stack.Items, Instance] };
            var units = map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (chain) units[stack.LeaderId] = units[stack.LeaderId] with { Modifiers = [.. units[stack.LeaderId].Modifiers, item.EquipModifierId.ToUpperInvariant()] };
            var types = map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            types[Instance] = item.Id;
            return new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(map, stacks: stacks, units: units, itemTypes: types), game.Looks.Stacks) },
                                   game.Art, book);
        }

        Assert.Equal(EquipRefusal.Ok, With(chain: false).EquipItem(stack.Id, Instance, slot));
        Assert.Equal(EquipRefusal.NoEffect, With(chain: true).EquipItem(stack.Id, Instance, slot));
        var ai = With(chain: true);
        AiStackItems.Prepare(ai, stack.Id, []);
        Assert.DoesNotContain(Instance, Equipment.Slots(ai.Map.Stacks[stack.Id]), StringComparer.OrdinalIgnoreCase);
    }

    private static GameSession Load(string day = "-t01-")
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        string sg = Directory.GetFiles(dir, $"*{day}*.sg").Order(StringComparer.Ordinal).Last();
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var looks = MapLooksLoader.Load(sg);
        var human = looks.Map.Players.Values.First(p => p.IsHuman);
        return new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book);
    }
}
