using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using D2.Rules.Units;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окно выбора навыка лидера за уровень (<c>DLG_UPGRADE_LEADER</c>, <c>CUpgradeLdrInterf</c> <c>0x4a6bfd</c>) и очередь таких окон
/// в начале хода человека (<c>0x5edbf7</c>).</summary>
[Trait("Category", "Corpus")]
public class UpgradeLeaderWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Два отложенных выбора — два окна подряд у одного отряда. Список — имена навыков предложения в его порядке, выбран первый,
    /// описание — выбранного; заголовок — X005TA0476 с именем лидера, портрет отражён. «ОК» (щелчок даёт и подтверждение списка) берёт
    /// навык один раз: очередь выборов −1, модификатор — в цепочке лидера; во втором окне взятого уже нет.</summary>
    [SkippableFact]
    public void Upgrade_ListDescriptionAndChoice()
    {
        var (session, stackId) = Game(pending: 2);
        var context = Context(session, session.Map.Stacks[stackId].Owner);
        var texts = SkillTexts.Load(Path.Combine(Data, "rules"));

        var queue = UpgradeLeaderQueue.For(session, context.Viewer);
        Assert.Equal(2, queue.Count(id => id == stackId));
        int first = queue.ToList().IndexOf(stackId);
        Assert.Equal(stackId, queue[first + 1]);

        var logic = Assert.IsType<UpgradeLeaderLogic>(UpgradeLeaderQueue.Window(context, stackId, texts)!.Logic);
        var ui = Open(logic);
        var offer = UpgradeLeaderQueue.Offer(session, stackId);
        output.WriteLine($"{logic.Summary} | {ui.TextOf("TXT_INSTRUCT")} | {string.Join(" / ", ui.Items("TLBOX_BONUS_LIST"))} | {ui.TextOf("TXT_INFO")}");
        Assert.True(offer.Count >= 2, "предложение из двух навыков и больше");
        Assert.Equal(offer.Select(u => context.Text(texts.Skills[u.ModifId].Name)), ui.Items("TLBOX_BONUS_LIST"));
        Assert.Equal(0, ui.ListSelected("TLBOX_BONUS_LIST"));
        Assert.Equal(context.Text(texts.Skills[offer[0].ModifId].Desc), ui.TextOf("TXT_INFO"));
        string name = session.Looks.Stacks.First(s => s.Id == stackId).LeaderName is { Length: > 0 } own ? own
            : context.UnitName(session.Map.Units[session.Map.Stacks[stackId].LeaderId].TypeId);
        Assert.Equal(context.Text("X005TA0476").Replace("%NAME%", name, StringComparison.Ordinal), ui.TextOf("TXT_INSTRUCT"));
        Assert.EndsWith(UiFrameRef.Mirror.ToString(), ui.Frame("IMG_LEADER"));
        Assert.False(logic.Allows("BTN_OK", ui));

        // Выбор второй строки — её описание; «ОК» — навык второй строки.
        ui.Select("TLBOX_BONUS_LIST", 1);
        logic.Handle(new UiListSelected("TLBOX_BONUS_LIST", 1), ui);
        Assert.Equal(context.Text(texts.Skills[offer[1].ModifId].Desc), ui.TextOf("TXT_INFO"));
        logic.Handle(new UiClicked("BTN_OK"), ui);
        logic.Handle(new UiListConfirmed("TLBOX_BONUS_LIST", 1), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Null(logic.TakeRequest());
        Assert.Equal((SkillRefusal.None, offer[1].ModifId), (logic.Refusal, logic.Chosen));
        var stack = session.Map.Stacks[stackId];
        Assert.Equal(1, stack.UpgradeCount);
        Assert.Contains(offer[1].ModifId, session.Map.Units[stack.LeaderId].Modifiers, StringComparer.OrdinalIgnoreCase);

        // Второе окно: взятого навыка в списке нет; «ОК» по первой строке — выборов не осталось, очередь пуста.
        var second = Assert.IsType<UpgradeLeaderLogic>(UpgradeLeaderQueue.Window(context, stackId, texts)!.Logic);
        var ui2 = Open(second);
        Assert.DoesNotContain(second.Offer, u => u.ModifId.Equals(offer[1].ModifId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UpgradeLeaderQueue.Offer(session, stackId).Select(u => context.Text(texts.Skills[u.ModifId].Name)), ui2.Items("TLBOX_BONUS_LIST"));
        second.Handle(new UiClicked("BTN_OK"), ui2);
        Assert.IsType<UiClose>(second.TakeRequest());
        Assert.Equal(0, session.Map.Stacks[stackId].UpgradeCount);
        Assert.DoesNotContain(stackId, UpgradeLeaderQueue.For(session, context.Viewer));
    }

    /// <summary>Нет отложенных выборов — нет очереди. Пустое предложение окно не отменяет (очередь <c>0x5edbf7</c> его не проверяет): список
    /// пуст, описания нет, «ОК» закрывает без выбора, отложенный выбор остаётся.</summary>
    [SkippableFact]
    public void Upgrade_NoPendingChoice_NoQueue_EmptyOfferStillOpens()
    {
        var (session, stackId) = Game(pending: 0);
        var context = Context(session, session.Map.Stacks[stackId].Owner);
        Assert.Empty(session.SkillOffer(stackId));
        Assert.DoesNotContain(stackId, UpgradeLeaderQueue.For(session, context.Viewer));
        var logic = Assert.IsType<UpgradeLeaderLogic>(UpgradeLeaderQueue.Window(context, stackId, SkillTexts.Empty)!.Logic);
        var ui = Open(logic);
        Assert.Empty(ui.Items("TLBOX_BONUS_LIST"));
        Assert.Equal("", ui.TextOf("TXT_INFO"));
        logic.Handle(new UiClicked("BTN_OK"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.Null(logic.Chosen);
    }

    /// <summary>Фильтры окна (<c>0x4a6fea</c>): из навыков лидерства — только первый (<c>0x4a732f</c>); при лидерстве 6 — ни одного
    /// (<c>0x5a3688</c>); навык способности, которая у лидера уже есть по цепочке (надетое, <c>0x5a364c</c>), — прочь. Предложение ядра
    /// (<see cref="GameSession.SkillOffer"/>, общее с ИИ) не меняется.</summary>
    [SkippableFact]
    public void Upgrade_WindowFilters_LeadershipOnceAndCapped_KnownAbilityHidden()
    {
        var (session, stackId) = Game(pending: 1);
        var book = session.RequireBook();
        var modifiers = book.Rules.Modifiers!;
        bool Leads(LeaderUpgrade u) => modifiers.TryGetValue(u.ModifId, out var m) && m.Elements.Any(e => e.Type == ModifierElementType.Leadership);
        var raw = session.SkillOffer(stackId);
        var shown = UpgradeLeaderQueue.Offer(session, stackId);
        output.WriteLine($"ядро {string.Join(",", raw.Select(u => u.ModifId))} | окно {string.Join(",", shown.Select(u => u.ModifId))}");
        Assert.True(shown.Count(Leads) <= 1);
        if (raw.Any(Leads)) Assert.Equal(raw.First(Leads).ModifId, shown.First(Leads).ModifId);

        // Лидерство 6: навыков лидерства в окне нет.
        var boost = modifiers.Values.First(m => m.Elements.Any(e => e.Type == ModifierElementType.Leadership && e.Number > 0));
        var stack = session.Map.Stacks[stackId];
        var leader = session.Map.Units[stack.LeaderId];
        var full = WithLeader(session, leader with { Modifiers = [.. leader.Modifiers, .. Enumerable.Repeat(boost.Id, UpgradeLeaderQueue.MaxLeadership)] });
        Assert.Equal(UpgradeLeaderQueue.MaxLeadership, Math.Min(full.Leadership(full.Map.Stacks[stackId]), UpgradeLeaderQueue.MaxLeadership));
        Assert.DoesNotContain(UpgradeLeaderQueue.Offer(full, stackId), Leads);

        // Способность навыка уже дана другим модификатором цепочки — навыка нет в окне, в ядре есть.
        var ability = raw.Select(u => (u, e: modifiers.GetValueOrDefault(u.ModifId)?.Elements.FirstOrDefault(e => e.Type == ModifierElementType.LeaderAbility)))
                         .FirstOrDefault(p => p.e is not null);
        Skip.If(ability.e is null, "в предложении нет навыка способности");
        var giver = modifiers.Values.FirstOrDefault(m => !m.Id.Equals(ability.u.ModifId, StringComparison.OrdinalIgnoreCase)
            && !(book.Rules.LeaderUpgradeModifiers?.Contains(m.Id) ?? false)
            && m.Elements.Any(e => e.Type == ModifierElementType.LeaderAbility && e.Ability == ability.e!.Ability));
        Skip.If(giver is null, "нет другого модификатора с той же способностью");
        var equipped = WithLeader(session, leader with { Modifiers = [.. leader.Modifiers, giver!.Id] });
        Assert.Contains(equipped.SkillOffer(stackId), u => u.ModifId == ability.u.ModifId);
        Assert.DoesNotContain(UpgradeLeaderQueue.Offer(equipped, stackId), u => u.ModifId == ability.u.ModifId);
    }

    private static GameSession WithLeader(GameSession session, D2.Rules.Map.MapUnit leader)
    {
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[leader.Id] = leader;
        return new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(session.Map, units: units) } }, session.Art, session.Book);
    }

    /// <summary>Правая кнопка на портрете — карточка лидера; мимо портрета — ничего.</summary>
    [SkippableFact]
    public void Upgrade_RightPressOnLeader_OpensCard()
    {
        var (session, stackId) = Game(pending: 1);
        var context = Context(session, session.Map.Stacks[stackId].Owner);
        var logic = new UpgradeLeaderLogic(context, stackId, SkillTexts.Empty);
        var ui = Open(logic);
        logic.RightPress(0, 0, ui);
        Assert.Null(logic.TakeRequest());
        logic.RightPress(90, 150, ui);   // IMG_LEADER: 87,146 84×99
        var card = Assert.IsType<UnitCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        Assert.NotNull(card);
    }

    private static UiScreenState Open(IWindowLogic logic)
    {
        var ui = new UiScreenState(logic.Shape(UiScreens.Load(Path.Combine(Data, "ui", "screens.json")).First(s => s.Id == UpgradeLeaderLogic.Screen)));
        logic.Open(ui);
        return ui;
    }

    private static WindowContext Context(GameSession session, string viewer) =>
        new(session, viewer, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

    /// <summary>Сохранение с человеком: его отряд с героем своей расы не ниже третьего уровня, <paramref name="pending"/> отложенных выборов.</summary>
    private static (GameSession Session, string StackId) Game(int pending)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var book = session.RequireBook();
        var map = session.Map;
        var human = map.Players.Values.First(p => p.IsHuman);
        var candidates = map.Stacks.Values.Where(s => s.Owner == human.Id && s.LeaderAlive && map.Units.TryGetValue(s.LeaderId, out var l)
            && book.CategoryOf(l.TypeId) != D2.Rules.Battle.UnitCategory.Noble
            && book.RaceOf(l.TypeId).Equals(human.RaceId, StringComparison.OrdinalIgnoreCase)).ToList();
        // Герой, у которого при отложенном выборе больше всего навыков на выбор.
        var best = candidates.Select(s => (Stack: s, Offer: With(session, human.Id, s, 1).SkillOffer(s.Id).Count)).OrderByDescending(p => p.Offer).FirstOrDefault();
        Skip.If(best.Stack is null || best.Offer < 3, "у человека нет героя с тремя навыками на выбор");
        return (With(session, human.Id, best.Stack!, pending), best.Stack!.Id);
    }

    /// <summary>У отрядов человека выборов нет, у <paramref name="stack"/> — <paramref name="pending"/>.</summary>
    private static GameSession With(GameSession session, string human, MapStack stack, int pending)
    {
        var map = session.Map;
        var stacks = map.Stacks.Values.ToDictionary(s => s.Id, s => s.Owner == human ? s with { UpgradeCount = 0 } : s, StringComparer.OrdinalIgnoreCase);
        stacks[stack.Id] = stack with { UpgradeCount = pending };
        return new GameSession(session.State with { Looks = session.Looks with { Map = GameSession.With(map, stacks: stacks) } }, session.Art, session.Book);
    }
}
