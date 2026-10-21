using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Data.Ui;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>Окна площадок человека: торговец, маг, тренер, наёмники — покупка и продажа перетаскиванием с вопросом
/// (<c>docs/ui-windows.md</c> §4).</summary>
[Trait("Category", "Corpus")]
public class SiteWindowTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string StackId = "S073KC0001";
    private static readonly Bank Rich = new(5000, 9999, 9999, 9999, 9999, 9999);   // казна зажимается на 9999

    [SkippableFact]
    public void Merchant_BuysAndSells()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MerchantLogic.Screen);
        int bag = logic.BagItems.Count;
        long gold = Gold(session);
        string type = logic.Stock[0].Item;
        output.WriteLine($"{site}: товаров {logic.Stock.Count}, в сумке {bag}");

        logic.Press(484 + 10, 403 + 10, ui);   // склад торговца, первая ячейка
        Assert.NotEmpty(ui.TextOf("TXT_DDFEEDBACK")!);
        logic.Release(40 + 10, 403 + 10, ui);  // сумка
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        // Как у движка (0x505904…0x50597b): имя предмета в %NAME%, золото цены числом в %GOLD% — «… стоит 150. Вы хотите его купить?»
        // (журнал 20260919-human, ui_msgbox).
        Assert.Matches(@"^Этот предмет \(.+\) стоит \d+\. Вы хотите его купить\?$", ask.Text);
        ask.Yes();
        Assert.Null(logic.LastRefusal);
        Assert.Equal(bag + 1, logic.BagItems.Count);
        Assert.Equal(gold - Treasury.ItemBuyPrice(session.Book!.Items[type]).Gold, Gold(session));   // ровно VALUE, без множителя зарядов

        int at = logic.BagCells.ToList().FindLastIndex(c => session.ItemOf(c.Top)!.Category != D2.Rules.Magic.ItemCategory.Special);
        ui.Select("MLBOX_RINVENTORY", at);
        long before = Gold(session);
        logic.Press(40 + 10 + ((at - ui.ListTop("MLBOX_RINVENTORY")) * 92), 403 + 10, ui);
        logic.Release(484 + 10, 403 + 10, ui);
        Assert.StartsWith("Продадите", Assert.IsType<UiAsk>(logic.TakeRequest()).Text);
    }

    /// <summary>Имя лавки из сценария, картинка по «заданию»; кукла вместо героя: слоты видны, отряд и портрет убраны,
    /// надетое продаётся перетаскиванием на склад, снимается в сумку.</summary>
    [SkippableFact]
    public void Merchant_TitleAndPaperdoll()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")), Screens);
        var ui = Open(logic, MerchantLogic.Screen);
        string title = session.Map.Sites.First(s => s.Id == site).Name;
        output.WriteLine($"{site}: {title}");
        Assert.NotEmpty(title);
        Assert.Equal(title, ui.TextOf("TXT_LABEL1"));
        Assert.Equal(session.Map.Sites.First(s => s.Id == site).Mission ? "SITE0003" : "SITE0002", ui.Frame("IMG_MERCHANT"));
        Assert.True(ui.IsHidden("IMG_BANNER"));

        // Надеть что-нибудь из сумки, чтобы было что продать с куклы.
        var slot = Enumerable.Range(0, ManageStackLogic.SlotImages.Length)
            .SelectMany(s => logic.BagItems.Select(i => (Slot: s, Item: i)))
            .FirstOrDefault(p => session.EquipItem(StackId, p.Item, p.Slot) == D2.Rules.Magic.EquipRefusal.Ok);
        Skip.If(slot.Item is null, "в сумке нечего надеть");
        logic.Handle(new UiToggled("TOG_PAPERDOLL", true), ui);
        Assert.True(logic.Paperdoll);
        Assert.False(ui.IsHidden("IMG_BANNER"));
        Assert.Null(ui.Frame("IMG_LEADER"));
        Assert.All(Enumerable.Range(0, 6), p => Assert.Null(ui.Frame(UnitGroupLayout.CellImage(SiteLogic.Group, p))));
        var r = ui.Screen.Elements.First(e => e.Id == ManageStackLogic.SlotImages[slot.Slot]).Rect;
        Assert.NotNull(ui.Frame(ManageStackLogic.SlotImages[slot.Slot]));
        logic.Press(r.X + 5, r.Y + 5, ui);
        Assert.NotEmpty(ui.TextOf("TXT_DDFEEDBACK")!);
        logic.Release(484 + 10, 403 + 10, ui);
        Assert.StartsWith("Продадите", Assert.IsType<UiAsk>(logic.TakeRequest()).Text);
        int bag = logic.BagItems.Count;
        logic.Press(r.X + 5, r.Y + 5, ui);
        logic.Release(40 + 10, 403 + 10, ui);
        Assert.Equal(bag + 1, logic.BagItems.Count);
        Assert.Null(ui.Frame(ManageStackLogic.SlotImages[slot.Slot]));
    }

    /// <summary>Переходы графа на склад и сумку (вопросы <c>X005TA0102</c>/<c>0103</c>, записанные по перетаскиванию) не берутся: окно
    /// сообщения с «%NAME%». У движка двойного щелчка у этих списков нет (<c>0x53616c</c>: функтора <c>+0x84</c>, ставит <c>0x5353cb</c> —
    /// только воровство, книга заклинаний, выбор героя, и кнопки <c>+0x50</c>); у порта он свой (<see cref="Merchant_DoubleClick_BuysAndSellsLikeDrag"/>).</summary>
    [SkippableFact]
    public void Merchant_ListsDoNotTakeGraphEdges()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MerchantLogic.Screen);
        Assert.False(logic.Allows("MLBOX_LINVENTORY", ui));
        Assert.False(logic.Allows("MLBOX_RINVENTORY", ui));
        Assert.True(logic.Allows("BTN_CLOSE", ui));
    }

    /// <summary>Двойной щелчок по товару — как его бросок в сумку (решение пользователя 15.10, у движка двойного щелчка здесь нет): тот же
    /// вопрос с подставленными именем и ценой, «Да» — предмет в сумке, золото списано; по предмету сумки — как бросок на склад (продажа).</summary>
    [SkippableFact]
    public void Merchant_DoubleClick_BuysAndSellsLikeDrag()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MerchantLogic.Screen);
        string type = logic.Stock[0].Item;
        int bag = logic.BagItems.Count;
        long gold = Gold(session);
        logic.Press(484 + 10, 403 + 10, ui);   // перетаскивание склад → сумка — эталон вопроса
        logic.Release(40 + 10, 403 + 10, ui);
        string dragged = Assert.IsType<UiAsk>(logic.TakeRequest()).Text;

        DoubleClick(logic, ui, 484 + 10, 403 + 10);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        Assert.Equal(dragged, ask.Text);
        Assert.Matches(@"^Этот предмет \(.+\) стоит \d+\. Вы хотите его купить\?$", ask.Text);
        Assert.Null(logic.TakeRequest());   // отпускание после двойного щелчка ничего не бросает
        ask.Yes();
        Assert.Null(logic.LastRefusal);
        Assert.Equal(bag + 1, logic.BagItems.Count);
        Assert.Equal(gold - Treasury.ItemBuyPrice(session.Book!.Items[type]).Gold, Gold(session));

        int at = logic.BagCells.ToList().FindLastIndex(c => session.ItemOf(c.Top)!.Category != D2.Rules.Magic.ItemCategory.Special);
        ui.Select("MLBOX_RINVENTORY", at);
        int x = 40 + 10 + ((at - ui.ListTop("MLBOX_RINVENTORY")) * 92);
        logic.Press(x, 403 + 10, ui);
        logic.Release(484 + 10, 403 + 10, ui);
        string sold = Assert.IsType<UiAsk>(logic.TakeRequest()).Text;
        DoubleClick(logic, ui, x, 403 + 10);
        Assert.Equal(sold, Assert.IsType<UiAsk>(logic.TakeRequest()).Text);
        Assert.StartsWith("Продадите", sold);
    }

    /// <summary>Не хватает золота — двойной щелчок отказывает, как бросок в сумку: вопроса нет, отказ «не хватает золота», под складом —
    /// ответ торговца <c>X005TA0109</c>.</summary>
    [SkippableFact]
    public void Merchant_DoubleClick_NoGold_RefusedLikeDrag()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0), Bank.Zero);
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MerchantLogic.Screen);
        int bag = logic.BagItems.Count;
        logic.Press(484 + 10, 403 + 10, ui);
        logic.Release(40 + 10, 403 + 10, ui);
        Assert.Null(logic.TakeRequest());
        string refusal = logic.LastRefusal!;
        string feedback = ui.TextOf("TXT_DDFEEDBACK")!;

        DoubleClick(logic, ui, 484 + 10, 403 + 10);
        Assert.Null(logic.TakeRequest());
        Assert.Equal("не хватает золота", refusal);
        Assert.Equal(refusal, logic.LastRefusal);
        Assert.Equal(feedback, ui.TextOf("TXT_DDFEEDBACK"));
        Assert.Equal(bag, logic.BagItems.Count);
        Assert.Equal(0, Gold(session));
    }

    /// <summary>Двойной щелчок, как его доставляет вид экрана: щелчок, затем нажатие с признаком двойного и отпускание.</summary>
    private static void DoubleClick(IWindowLogic logic, UiScreenState ui, int x, int y)
    {
        logic.Press(x, y, ui);
        logic.Release(x, y, ui);
        logic.Press(x, y, ui);
        logic.DoubleClick(x, y, ui);
        logic.Release(x, y, ui);
    }

    /// <summary>Место отряда слева закрыто фоном группы <c>DLG_MERCHANT_GROUP_BG</c> (<c>0x49a449</c>: картинка в <c>+0x1c</c>, пока кукла
    /// выключена; включённая — фон куклы, <c>0x49a18a</c>) в размер картинки 350×386 и под портретом и ячейками, иначе сквозь окно видна карта.</summary>
    [SkippableFact]
    public void Merchant_GroupBackgroundCoversMap()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")), Screens);
        var ui = Open(logic, MerchantLogic.Screen);
        AssertUnderlay(ui, "DLG_MERCHANT_GROUP_BG");
        logic.Handle(new UiToggled("TOG_PAPERDOLL", true), ui);
        Assert.Equal("DLG_PAPERDOLL_PAPERDOLL_BG", ui.Frame("IMG_PAPERDOLL_BG"));
        logic.Handle(new UiToggled("TOG_PAPERDOLL", false), ui);
        Assert.Equal("DLG_MERCHANT_GROUP_BG", ui.Frame("IMG_PAPERDOLL_BG"));
    }

    /// <summary>У мага — то же: покупка перетаскиванием, двойной щелчок по спискам — ничего (вопрос <c>X005TA0108</c> с «%NAME%» — нет).</summary>
    [SkippableFact]
    public void Mage_ListsDoNotTakeGraphEdges()
    {
        var (session, site) = Game(SiteKind.Mage, s => s.Spells.Count > 0);
        var logic = new MageLogic(Context(session), StackId, site, SpellBookLogic.LoadTexts(Path.Combine(Data, "rules")));
        var ui = Open(logic, MageLogic.Screen);
        Assert.False(logic.Allows("LBOX_MAGE_INV", ui));
        Assert.False(logic.Allows("LBOX_SPELL", ui));
        Assert.True(logic.Allows("BTN_CLOSE", ui));
    }

    /// <summary>Ценность (<c>L_VALUABLE</c>) — своя строка цены <c>X005TA0589</c>.</summary>
    [SkippableFact]
    public void Merchant_ValuableOffer()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MerchantLogic.Screen);
        int at = logic.BagCells.ToList().FindIndex(c => session.ItemOf(c.Top)?.Category == D2.Rules.Magic.ItemCategory.Valuable);
        Skip.If(at < 0, "в сумке нет ценностей");
        ui.Select("MLBOX_RINVENTORY", at);
        logic.Press(40 + 10 + ((at - ui.ListTop("MLBOX_RINVENTORY")) * 92), 403 + 10, ui);
        string expected = Context(session).Text("X005TA0589");
        Assert.StartsWith(expected[..expected.IndexOf('%')], ui.TextOf("TXT_DDFEEDBACK"));
    }

    /// <summary>Карточка предмета (DLG_R_C_ITEM): у товара торговца — с ценой, у предмета героя — без; жезл — со стоимостью
    /// использования; талисман-тип — цена × 5 зарядов.</summary>
    [SkippableFact]
    public void Merchant_ItemCards()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var texts = StackTexts.Load(Path.Combine(Data, "rules"));
        var logic = new MerchantLogic(Context(session), StackId, site, texts);
        var ui = Open(logic, MerchantLogic.Screen);
        while (logic.TakeRequest() is not null) { }
        logic.RightPress(484 + 10, 403 + 10, ui);
        var card = Assert.IsType<ItemCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        var cardUi = Open(card, ItemCardLogic.Screen);
        output.WriteLine(cardUi.TextOf("TXT_PRICE") + " | " + cardUi.TextOf("TXT_SPELL_INFO"));
        Assert.StartsWith("Цена", cardUi.TextOf("TXT_PRICE"));
        Assert.Contains("MedBold;", cardUi.TextOf("TXT_SPELL_INFO"));
        logic.RightPress(40 + 10, 403 + 10, ui);
        var own = Open(Assert.IsType<ItemCardLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic), ItemCardLogic.Screen);
        Assert.Equal("", own.TextOf("TXT_PRICE"));

        var book = session.Book!;
        var context = Context(session);
        var wand = book.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.Wand);
        var wandUi = Open(new ItemCardLogic(context.Text, wand, texts.Items[wand.Id], texts.ItemDescs![wand.Id]), ItemCardLogic.Screen);
        Assert.Contains("Стоимость использования", wandUi.TextOf("TXT_SPELL_INFO"));
        var talisman = book.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.Talisman && i.Value.Gold > 0);
        Assert.Equal(talisman.Value.Gold * 5, new ItemCardLogic(context.Text, talisman, "", "", withPrice: true).Price!.Gold);
    }

    [SkippableFact]
    public void Mage_SellsSpellByDrag()
    {
        var (session, site) = Game(SiteKind.Mage, s => s.Spells.Count > 0);
        var logic = new MageLogic(Context(session), StackId, site, SpellBookLogic.LoadTexts(Path.Combine(Data, "rules")));
        var ui = Open(logic, MageLogic.Screen);
        Skip.If(logic.Offered.Count == 0, "у мага нет нового");
        string spell = logic.Offered[0];
        logic.Press(493 + 10, 404 + 10, ui);
        logic.Release(46 + 10, 404 + 10, ui);
        Assert.StartsWith("Хотите приобрести", Assert.IsType<UiAsk>(logic.TakeRequest()).Text);

        // Строка товара (0x4fcf00): имя без жирного, цена и «Щелкните правой кнопкой…», текст с x = 0x62 (снимок оригинала 19.10, LUTE).
        var row = ui.Cards("LBOX_MAGE_INV")[0];
        Assert.StartsWith(@"\vC;", row.Text);
        Assert.DoesNotContain("MedBold", row.Text, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\n" + Context(session).Text("X005TA0495"), row.Text);
        Assert.Equal(new UiRect(98, 0, 172, 80), row.TextAt);
    }

    /// <summary>Имя башни в <c>TXT_MAGE_NAME</c>, раса игрока в <c>TXT_LABEL2</c>; «Известные заклинания» — книга без «Сотворить».</summary>
    [SkippableFact]
    public void Mage_TitleRaceAndKnownBook()
    {
        var (session, site) = Game(SiteKind.Mage, s => s.Spells.Count > 0);
        var spells = SpellBookLogic.LoadTexts(Path.Combine(Data, "rules"));
        var context = Context(session);
        var logic = new MageLogic(context, StackId, site, spells, RulesTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MageLogic.Screen);
        while (logic.TakeRequest() is not null) { }
        Assert.Equal(session.Map.Sites.First(s => s.Id == site).Name, ui.TextOf("TXT_MAGE_NAME"));
        output.WriteLine(ui.TextOf("TXT_LABEL2"));
        Assert.DoesNotContain("%NAME%", ui.TextOf("TXT_LABEL2"));
        Assert.DoesNotContain(session.Map.Players[session.Map.Stacks[StackId].Owner].RaceId, ui.TextOf("TXT_LABEL2"));
        Assert.False(logic.Allows("BTN_KNOWN_SPELL", ui));
        logic.Handle(new UiClicked("BTN_KNOWN_SPELL"), ui);
        var open = Assert.IsType<UiOpen>(logic.TakeRequest());
        var book = Assert.IsType<CastSpellLogic>(open.Logic);
        var bookUi = Open(book, CastSpellLogic.Screen);
        Assert.True(bookUi.IsHidden("BTN_CAST_SPELL"));
    }

    [SkippableFact]
    public void Trainer_ToolClickAsksThenReports()
    {
        var (session, site) = Game(SiteKind.Trainer, _ => true);
        var logic = new TrainerLogic(Context(session), StackId, site);
        var ui = Open(logic, TrainerLogic.Screen);
        var cells = session.GroupCells(StackId)!;
        string unit = cells.Units.First(u => session.TrainRefusal(session.Map.Stacks[StackId].Owner, u) is null);
        var r = UnitGroupLayout.Cell(new UiRect(190, 68, 70, 85), true, cells.PositionOf(unit));
        logic.Handle(new UiToggled("TOG_TRAINING", true), ui);
        logic.Release(r.X + 5, r.Y + 5, ui);
        Assert.False(logic.Tool);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        output.WriteLine(ask.Text);
        int xp = session.Map.Units[unit].Xp;
        ask.Yes();
        Assert.Null(logic.LastRefusal);
        Assert.True(session.Map.Units[unit].Xp > xp);
        Assert.IsType<TrainingDoneLogic>(Assert.IsType<UiOpen>(logic.TakeRequest()).Logic);
        Assert.Contains(session.Looks.Stacks.First(s => s.Id == StackId).LeaderName, ui.TextOf("TXT_STACK_INFO"));
        Assert.Equal(session.Map.Sites.First(s => s.Id == site).Name, ui.TextOf("TXT_LABEL1"));
    }

    [SkippableFact]
    public void Mercenary_HiresIntoDroppedCell()
    {
        var (session, site) = Game(SiteKind.Mercenaries, s => s.Roster.Count > 0);
        // Освободить место: отпустить одного малого воина (не героя).
        var stack = session.Map.Stacks[StackId];
        string spare = stack.UnitIds.First(u => u != stack.LeaderId && session.Book!.Rules.Units.TryGetValue(session.Map.Units[u].TypeId, out var t) && t.SizeSmall);
        Assert.Equal(0, session.DismissUnit(StackId, spare));
        var logic = new MercenaryLogic(Context(session), StackId, site);
        var ui = Open(logic, MercenaryLogic.Screen);
        while (logic.TakeRequest() is UiTell) { }   // сообщения открытия о месте
        var cells = session.GroupCells(StackId)!;
        var book = session.Book!;
        int index = logic.Roster.ToList().FindIndex(o => book.Rules.Units.TryGetValue(o.UnitType, out var t) && t.SizeSmall);
        int empty = Enumerable.Range(0, 6).FirstOrDefault(p => cells.Cells[p].Length == 0, -1);
        Skip.If(index < 0 || empty < 0 || session.FreeCells(session.Map.Stacks[StackId]) <= 0, "нет малого наёмника или места");
        var r = UnitGroupLayout.Cell(new UiRect(191, 68, 70, 85), true, empty);
        logic.Press(486 + 10, 396 + 10 + (index - ui.ListTop("LBOX_UNIT_LIST")) * 195, ui);
        logic.Release(r.X + 5, r.Y + 5, ui);
        var ask = Assert.IsType<UiAsk>(logic.TakeRequest());
        ask.Yes();
        Assert.Null(logic.LastRefusal);
        Assert.NotEmpty(session.GroupCells(StackId)!.Cells[empty]);
    }

    /// <summary>Наёмники: имя площадки; взятый воин — имя и описание типа и цена, отпущенный — снова приглашение.</summary>
    [SkippableFact]
    public void Mercenary_DescribesTakenUnit()
    {
        var (session, site) = Game(SiteKind.Mercenaries, s => s.Roster.Count > 0);
        var context = Context(session);
        var logic = new MercenaryLogic(context, StackId, site, RulesTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(logic, MercenaryLogic.Screen);
        Assert.Equal(session.Map.Sites.First(s => s.Id == site).Name, ui.TextOf("TXT_LABEL1"));
        logic.Press(486 + 10, 396 + 10, ui);
        string shown = ui.TextOf("TXT_DISPLAY1")!;
        output.WriteLine(shown);
        string name = context.UnitName(logic.Roster[ui.ListTop("LBOX_UNIT_LIST")].UnitType);
        Assert.Contains(name, shown);
        Assert.True(shown.Length > name.Length + 40, "описание типа воина");
        Assert.NotEmpty(ui.TextOf("TXT_PRICE")!);
        logic.Release(0, 0, ui);
        Assert.Equal(context.Text("X005TA0492"), ui.TextOf("TXT_DISPLAY1"));
        Assert.Equal("", ui.TextOf("TXT_PRICE"));
    }

    /// <summary>
    /// Предмет, заклинание и наёмник «приклеиваются» к курсору (пользователь 17.10: «предмет не тянется под курсором, но покупка
    /// происходит»): у торговца, мага и наёмников тащимое из списка даёт картинку курсора (<see cref="IWindowLogic.DragImage"/> — значок
    /// товара, <c>IconSpel</c> заклинания, портрет наёмника) и «примет ли место» (<see cref="IWindowLogic.DropAccepts"/>; без него сцена
    /// картинку не показывала: <c>UiScreenView.DragCursorAt</c> берёт картинку, только когда тащат). Строка ростера наёмников
    /// (<c>0x503e47</c>): портрет в левом верхнем углу ячейки, подпись <c>0x61fe1e</c> — имя, ОЗ, цена — в месте (14, 115) пергамента.
    /// </summary>
    [SkippableFact]
    public void SiteLists_DragImageAndMercenaryRow()
    {
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0), Rich);
        var merchant = new MerchantLogic(Context(session), StackId, site, StackTexts.Load(Path.Combine(Data, "rules")));
        var ui = Open(merchant, MerchantLogic.Screen);
        // Картинка лавки 260×248 в месте 260×219 — без масштаба от угла, лишнее обрезается (снимок «Лавка Веррика», 0x49938e → 0x530db8).
        Assert.Equal(UiImageAlign.Clip, ui.ImageAlign("IMG_MERCHANT"));
        Assert.Null(merchant.DropAccepts(40 + 10, 403 + 10, ui));
        merchant.Press(484 + 10, 403 + 10, ui);   // товар
        Assert.Equal(ItemCells.IconFrame(merchant.Stock[ui.ListTop("MLBOX_LINVENTORY")].Item, Context(session)), merchant.DragImage);
        Assert.True(merchant.DropAccepts(40 + 10, 403 + 10, ui));     // сумка
        Assert.False(merchant.DropAccepts(484 + 10, 403 + 10, ui));   // свой же склад
        merchant.Release(0, 0, ui);
        Assert.Null(merchant.DragImage);

        (session, site) = Game(SiteKind.Mage, s => s.Spells.Count > 0, Rich);
        var mage = new MageLogic(Context(session), StackId, site, SpellBookLogic.LoadTexts(Path.Combine(Data, "rules")));
        ui = Open(mage, MageLogic.Screen);
        Skip.If(mage.Offered.Count == 0, "магу нечего предложить");
        var offer = ui.Screen.Elements.First(e => e.Id == "LBOX_MAGE_INV").Rect;
        var bought = ui.Screen.Elements.First(e => e.Id == "LBOX_SPELL").Rect;
        mage.Press(offer.X + 10, offer.Y + 10, ui);
        Assert.Equal($"IconSpel:{mage.Offered[ui.ListTop("LBOX_MAGE_INV")].ToUpperInvariant()}", mage.DragImage);
        Assert.True(mage.DropAccepts(bought.X + 10, bought.Y + 10, ui));
        Assert.False(mage.DropAccepts(offer.X + 10, offer.Y + 10, ui));

        (session, site) = Game(SiteKind.Mercenaries, s => s.Roster.Count > 0, Rich);
        var context = Context(session);
        var camp = new MercenaryLogic(context, StackId, site);
        ui = Open(camp, MercenaryLogic.Screen);
        int top = ui.ListTop("LBOX_UNIT_LIST");
        var row = ui.Cards("LBOX_UNIT_LIST")[top];
        var offerRow = camp.Roster[top];
        output.WriteLine($"строка наёмника: {row.Image} @{row.ImageAt} текст «{row.Text}» в {row.TextAt}");
        Assert.Equal(context.Faces.Small(offerRow.UnitType), row.Image);
        Assert.Equal(new UiPoint(0, 0), row.ImageAt);
        Assert.Equal(new UiRect(14, 115, 266 - 1 - 14, 195 - 1 - 115), row.TextAt);
        Assert.Contains(context.UnitName(offerRow.UnitType), row.Text);
        int hp = session.Book!.Grown(offerRow.UnitType, offerRow.Level)!.Type.HitPoints;
        Assert.Contains($"{hp}/{hp}", row.Text);
        Assert.Contains($"{session.MercenaryPrice(session.Map.Stacks[StackId], offerRow)!.Gold}", row.Text);
        camp.Press(486 + 10, 396 + 10, ui);
        Assert.Equal(context.Faces.Small(offerRow.UnitType), camp.DragImage);
        Assert.NotNull(camp.DropAccepts(191 + 10, 68 + 10, ui));
        // Занятая позиция: рамки нет — курсор «нельзя», и отпускание ничего не спрашивает (0x56eace).
        var cells = session.GroupCells(StackId)!;
        int busy = Enumerable.Range(0, 6).First(p => cells.Cells[p].Length > 0);
        var (bx, by) = Center(UnitGroupLayout.Cell(new UiRect(191, 68, 70, 85), true, busy));
        Assert.False(camp.DropAccepts(bx, by, ui));
        while (camp.TakeRequest() is not null) { }
        camp.Release(bx, by, ui);
        Assert.Null(camp.TakeRequest());
    }

    /// <summary>«Бросить предмет»: мешок на соседней пустой клетке, предмет туда и обратно; пустой мешок при закрытии убирается.</summary>
    [SkippableFact]
    public void DropBag_ExchangeAndCloseEmpty()
    {
        var (session, _) = Game(SiteKind.Merchant, _ => true);
        var stack = session.Map.Stacks[StackId];
        Assert.NotNull(session.DropBag(StackId, stack.X + 3, stack.Y, out _));   // не рядом
        var cell = new[] { (1, 1), (0, 1), (1, 0), (-1, 1), (1, -1), (-1, 0), (0, -1), (-1, -1) }
            .Select(d => (stack.X + d.Item1, stack.Y + d.Item2))
            .First(c => session.DropCheck(StackId, c.Item1, c.Item2, out _) is null);
        Assert.Null(session.DropBag(StackId, cell.Item1, cell.Item2, out string bag));
        Assert.Contains(session.Looks.Bags, b => b.Id == bag);
        Assert.Null(session.DropBag(StackId, cell.Item1, cell.Item2, out string again));   // лежащий мешок — в него же
        Assert.Equal(bag, again);
        Assert.Single(session.Looks.Bags, b => b.Id == bag);
        var logic = new PickupDropLogic(Context(session), StackId, bag, StackTexts.Load(Path.Combine(Data, "rules")), Screens);
        var ui = Open(logic, PickupDropLogic.Screen);
        AssertUnderlay(ui, "DLG_PICKUP_DROP_GROUP_BG");   // 0x49db06 — как у торговца
        string item = logic.StackItems[0];
        logic.Press(40 + 10, 403 + 10, ui);
        logic.Release(484 + 10, 403 + 10, ui);
        Assert.Equal([item], logic.GroundItems);
        logic.Press(484 + 10, 403 + 10, ui);
        logic.Release(40 + 10, 403 + 10, ui);
        Assert.Empty(logic.GroundItems);

        // Кукла: портрет убран, надетое — со слота на землю, с земли — снова на тот же слот.
        Assert.True(ui.IsEnabled("TOG_PAPERDOLL"));
        logic.Handle(new UiToggled("TOG_PAPERDOLL", true), ui);
        Assert.True(logic.Paperdoll);
        Assert.Null(ui.Frame("IMG_LEADER"));
        var worn = D2.Rules.Magic.Equipment.Slots(session.Map.Stacks[StackId]);
        int slot = Enumerable.Range(0, worn.Count).FirstOrDefault(s => worn[s].Length > 0, -1);
        if (slot >= 0)
        {
            string wornItem = worn[slot];
            var slotRect = ui.Screen.Elements.First(e => e.Id == ManageStackLogic.SlotImages[slot]).Rect;
            logic.Press(slotRect.X + 5, slotRect.Y + 5, ui);
            logic.Release(484 + 10, 403 + 10, ui);
            Assert.Equal([wornItem], logic.GroundItems);
            logic.Press(484 + 10, 403 + 10, ui);
            logic.Release(slotRect.X + 5, slotRect.Y + 5, ui);
            Assert.Null(logic.LastRefusal);
            Assert.Equal(wornItem, D2.Rules.Magic.Equipment.Slots(session.Map.Stacks[StackId])[slot]);
        }
        logic.Handle(new UiClicked("BTN_BACK"), ui);
        Assert.IsType<UiClose>(logic.TakeRequest());
        Assert.DoesNotContain(session.Map.Sites, s => s.Id == bag);
        Assert.DoesNotContain(session.Looks.Bags, b => b.Id == bag);
    }

    /// <summary>
    /// Перестановка воинов в окнах площадок (группа — <c>CDDStackGroup</c>, как у гостя окна армии; конструктор <c>0x4ff166</c> с теми же
    /// параметрами у мага, наёмников, торговца и тренера): нажатие на воина — жёлтые рамки у всех шести позиций своей группы, бросок на
    /// другую позицию — та же перестановка, что в окне армии (<see cref="GameSession.Within"/>), после броска рамок нет. У тренера с нажатым
    /// инструментом воина не тащат.
    /// </summary>
    [SkippableTheory]
    [InlineData(SiteKind.Merchant)]
    [InlineData(SiteKind.Mage)]
    [InlineData(SiteKind.Trainer)]
    [InlineData(SiteKind.Mercenaries)]
    public void SiteWindows_UnitDragRearranges(SiteKind kind)
    {
        var (session, site) = Game(kind, _ => true);
        var c = Context(session);
        var (logic, screen, anchor) = Site(kind, c, site);
        var ui = Open(logic, screen);
        var first = ui.Screen.Elements.First(e => e.Id == anchor).Rect;
        var cells = session.GroupCells(StackId)!;
        int from = Enumerable.Range(0, 6).First(p => cells.Cells[p].Length > 0 && cells.PositionOf(cells.Cells[p]) == p);
        int to = Enumerable.Range(0, 6).First(p => GameSession.Within(cells, from, p) is not null);
        var want = GameSession.Within(cells, from, to)!;
        var (fx, fy) = Center(UnitGroupLayout.Cell(first, true, from));
        var (tx, ty) = Center(UnitGroupLayout.Cell(first, true, to));
        Assert.Equal("......", DropFrames.Line(ui, SiteLogic.Group));
        logic.Press(fx, fy, ui);
        string line = DropFrames.Line(ui, SiteLogic.Group);
        output.WriteLine($"{kind}: воин {from} → {to}, рамки {line}, клетки {UnitCellsView.Line(c, StackId)}");
        Assert.All(line, ch => Assert.Equal('v', char.ToLowerInvariant(ch)));
        Assert.NotNull(logic.DragImage);
        Assert.True(logic.DropAccepts(tx, ty, ui));
        logic.Release(tx, ty, ui);
        Assert.Equal(want.Cells, session.GroupCells(StackId)!.Cells);
        Assert.Equal("......", DropFrames.Line(ui, SiteLogic.Group));
        Assert.Null(logic.DropAccepts(tx, ty, ui));

        if (logic is not TrainerLogic trainer) return;
        string before = string.Join(",", session.GroupCells(StackId)!.Cells);
        trainer.Handle(new UiToggled("TOG_TRAINING", true), ui);
        trainer.Press(tx, ty, ui);
        Assert.Null(trainer.DragImage);
        Assert.Equal(before, string.Join(",", session.GroupCells(StackId)!.Cells));
    }

    /// <summary>
    /// Харнесс по журналу human, день 30 (окно наёмников, <c>DLG_MERCENARIES</c> сверху): начало перетаскивания лидера с позиции 2 —
    /// <c>swap_check</c> всех шести позиций 0 (t=2360531), бросок — <c>unit_transfer</c> «move» 2 → 0 (<c>swap_msg</c>, seq 7730027).
    /// Тот же жест в окне порта: рамки жёлтые у всех шести, после броска лидер в позиции 0, позиция 2 пуста.
    /// </summary>
    [SkippableFact]
    public void Mercenary_UnitMove_MatchesJournal()
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human");
        string save = Path.Combine(dir, "rastlennaya-vlast-dwarf-t30-055.sg");
        Skip.IfNot(File.Exists(save), "нет сохранения в корпусе");
        var cluster = Directory.GetFiles(dir, "passive-*.jsonl").Order(StringComparer.Ordinal).SelectMany(f => DropFramesTests.Clusters(f))
                               .Single(x => x.T == 2360531);
        var looks = MapLooksLoader.Load(save);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var (occupants, ret) = cluster.Groups[StackId];
        string[] cellsBefore = [.. occupants.Select(o => o ?? "")];
        var present = cellsBefore.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        session.SetCells(StackId, new UnitCells(present, cellsBefore));
        var site = session.Map.Sites.First(s => s.Kind == SiteKind.Mercenaries);
        var logic = new MercenaryLogic(Context(session), StackId, site.Id);
        var ui = Open(logic, MercenaryLogic.Screen);
        var first = ui.Screen.Elements.First(e => e.Id == "IMG_RSLOT01").Rect;
        var (fx, fy) = Center(UnitGroupLayout.Cell(first, true, cluster.FromPos));
        logic.Press(fx, fy, ui);
        string line = DropFrames.Line(ui, SiteLogic.Group).ToLowerInvariant();
        string journal = new([.. ret.Select(r => r == 0 ? 'v' : '.')]);
        output.WriteLine($"до {string.Join(",", cellsBefore)}: рамки {line}, журнал {journal}");
        Assert.Equal(journal, line);
        var (tx, ty) = Center(UnitGroupLayout.Cell(first, true, 0));
        logic.Release(tx, ty, ui);
        var after = session.GroupCells(StackId)!;
        Assert.Null(logic.LastRefusal);
        Assert.Equal("S073UN01cc", after.Cells[0], ignoreCase: true);
        Assert.Equal("", after.Cells[2]);
    }

    /// <summary>Торговец: зелье лечения из сумки (<c>CDDStackInventoryForMerc</c> — наследник сумки с тем же <c>IMidItemExt</c>) — на раненого
    /// воина отряда: рамка жёлтая, бросок лечит, зелье уходит.</summary>
    [SkippableFact]
    public void Merchant_PotionFromBag_Heals()
    {
        const string instance = "S073IMfff5";
        var (session, site) = Game(SiteKind.Merchant, _ => true);
        var stack = session.Map.Stacks[StackId];
        var cells = session.GroupCells(StackId)!;
        int pos = Enumerable.Range(0, 6).First(p => cells.Cells[p].Length > 0 && cells.PositionOf(cells.Cells[p]) == p);
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[StackId] = stack with { Items = [.. stack.Items, instance] };
        var units = session.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[cells.Cells[pos]] = units[cells.Cells[pos]] with { Hp = 1 };
        var types = session.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[instance] = session.Book!.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionHeal).Id;
        var map = GameSession.With(session.Map, stacks: stacks, units: units, itemTypes: types);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Empty);
        var ui = Open(logic, MerchantLogic.Screen);
        int at = logic.BagCells.ToList().FindIndex(x => x.Ids.Contains(instance, StringComparer.OrdinalIgnoreCase));
        ui.Select("MLBOX_RINVENTORY", at);
        logic.Press(40 + 10 + ((at - ui.ListTop("MLBOX_RINVENTORY")) * 92), 403 + 10, ui);
        string line = DropFrames.Line(ui, SiteLogic.Group);
        output.WriteLine($"зелье: {line}");
        Assert.Equal('v', char.ToLowerInvariant(line[pos]));
        var first = ui.Screen.Elements.First(e => e.Id == "IMG_SLOT1").Rect;
        var (tx, ty) = Center(UnitGroupLayout.Cell(first, true, pos));
        logic.Release(tx, ty, ui);
        Assert.Null(logic.LastRefusal);
        Assert.True(session.Map.Units[cells.Cells[pos]].Hp > 1);
        Assert.DoesNotContain(instance, session.Map.Stacks[StackId].Items);
        Assert.Equal("......", DropFrames.Line(ui, SiteLogic.Group));
    }

    /// <summary>Кукла торговца рисует рамки, как кукла окна отряда (Ин40): слоты — те же <c>CDDEquipment</c> (кукла <c>0x4cbcea</c> →
    /// <c>0x50401c</c>). Зелье из сумки — жёлтые боевые слоты, склад — <c>DRAGDROP_INVENT_VALID</c> (купит, <c>0x4fe84a</c>), сумке — нет;
    /// товар склада — слотам нет (<c>0x504782</c>: <c>0x49a722</c>), сумке — по цене (<c>0x5df9ea</c>), складу — нет; знамя на слот сапог не
    /// принимается без сообщения (<c>0x56eace</c>); надетое — слот-источник жёлтый, сумка и склад принимают. Бросили — рамок нет.</summary>
    [SkippableFact]
    public void Merchant_ItemFrames_DollAndLists()
    {
        const string banner = "S073IMfff6", potion = "S073IMfff7";
        var (session, site) = Game(SiteKind.Merchant, s => s.Stock.Any(x => x.Count > 0));
        var stack = session.Map.Stacks[StackId];
        var stacks = session.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[StackId] = stack with { Items = [.. stack.Items, banner, potion] };
        var types = session.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[banner] = session.Book!.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.Banner).Id;
        types[potion] = session.Book!.Items.Values.First(i => i.Category == D2.Rules.Magic.ItemCategory.PotionHeal).Id;
        var map = GameSession.With(session.Map, stacks: stacks, itemTypes: types);
        session = new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book);
        var logic = new MerchantLogic(Context(session), StackId, site, StackTexts.Empty, Screens);
        var ui = Open(logic, MerchantLogic.Screen);
        logic.Handle(new UiToggled("TOG_PAPERDOLL", true), ui);
        string[] slots = ManageStackLogic.SlotImages;
        string? Bag() => ui.Frame(ItemFrames.Element("MLBOX_RINVENTORY"));
        string? Store() => ui.Frame(ItemFrames.Element("MLBOX_LINVENTORY"));
        string Show(string what)
        {
            string line = ItemFrames.Line(ui, slots);
            output.WriteLine($"{what}: слоты {line}, сумка {Bag() ?? "-"}, склад {Store() ?? "-"}");
            return line;
        }
        void PressBag(string item)
        {
            int at = logic.BagCells.ToList().FindIndex(c => c.Ids.Contains(item, StringComparer.OrdinalIgnoreCase));
            ui.Select("MLBOX_RINVENTORY", at);
            logic.Press(40 + 10 + ((at - ui.ListTop("MLBOX_RINVENTORY")) * 92), 403 + 10, ui);
        }
        (int X, int Y) Slot(int s) => Center(ui.Screen.Elements.First(e => e.Id == slots[s]).Rect);
        Assert.Equal(".......", ItemFrames.Line(ui, slots));

        PressBag(potion);
        Assert.Equal("..vv...", Show("зелье из сумки"));
        Assert.Null(Bag());
        Assert.Equal(ItemFrames.ListValid, Store());
        int battle1 = D2.Rules.Magic.Equipment.SlotBattle1;
        var frame = ui.Screen.Elements.First(e => e.Id == ItemFrames.Element(slots[battle1]));
        Assert.Equal(ui.Screen.Elements.First(e => e.Id == slots[battle1]).Rect, frame.Rect);   // по середине слота (0x5717f7)
        logic.Release(10, 10, ui);
        Assert.Equal(".......", Show("бросили"));
        Assert.Null(Store());

        logic.Press(484 + 10, 403 + 10, ui);   // товар склада
        bool affordable = Gold(session) >= Treasury.ItemBuyPrice(session.Book!.Items[logic.Stock[0].Item]).Gold;
        Assert.Equal(".......", Show("товар"));
        Assert.Equal(affordable ? ItemFrames.ListValid : null, Bag());
        Assert.Null(Store());
        logic.Release(10, 10, ui);

        PressBag(banner);
        char bannerFrame = session.EquipCheck(StackId, banner, D2.Rules.Magic.Equipment.SlotBanner) == D2.Rules.Magic.EquipRefusal.Ok ? 'v' : 'i';
        Assert.Equal(bannerFrame + "......", Show("знамя"));
        var (bx, by) = Slot(D2.Rules.Magic.Equipment.SlotBoots);
        Assert.False(logic.DropAccepts(bx, by, ui));
        logic.Release(bx, by, ui);   // слот сапог: не того рода — не принимается, без сообщения
        Assert.Null(logic.TakeRequest());
        Assert.Contains(banner, logic.BagItems);

        PressBag(potion);
        var (px, py) = Slot(battle1);
        logic.Release(px, py, ui);
        Assert.Null(logic.LastRefusal);
        Assert.Equal(potion, D2.Rules.Magic.Equipment.Slots(session.Map.Stacks[StackId])[battle1], ignoreCase: true);
        logic.Press(px, py, ui);   // надетое: слот-источник и второй боевой жёлтые, сумка и склад принимают
        Assert.Equal("..vv...", Show("надетое"));
        Assert.Equal(ItemFrames.ListValid, Bag());
        Assert.Equal(ItemFrames.ListValid, Store());
        logic.Release(10, 10, ui);
        Assert.Equal(".......", Show("бросили"));
        Assert.Null(Bag());
    }

    private static (SiteLogic Logic, string Screen, string Anchor) Site(SiteKind kind, WindowContext c, string site) => kind switch
    {
        SiteKind.Merchant => (new MerchantLogic(c, StackId, site, StackTexts.Empty), MerchantLogic.Screen, "IMG_SLOT1"),
        SiteKind.Mage => (new MageLogic(c, StackId, site, new Dictionary<string, SpellText>()), MageLogic.Screen, "IMG_RSLOT01"),
        SiteKind.Trainer => (new TrainerLogic(c, StackId, site), TrainerLogic.Screen, "IMG_RSLOT01"),
        _ => (new MercenaryLogic(c, StackId, site), MercenaryLogic.Screen, "IMG_RSLOT01"),
    };

    private static (int X, int Y) Center(UiRect r) => (r.X + (r.Width / 2), r.Y + (r.Height / 2));

    // --- помощники ---

    /// <summary>Фон места группы: кадр <paramref name="frame"/>, место в размер картинки 350×386 с угла окна, первым среди элементов
    /// (под портретом, ячейками и слотами куклы).</summary>
    private static void AssertUnderlay(UiScreenState ui, string frame)
    {
        Assert.Equal(frame, ui.Frame("IMG_PAPERDOLL_BG"));
        var bg = ui.Screen.Elements[0];
        Assert.Equal("IMG_PAPERDOLL_BG", bg.Id);
        Assert.Equal(new UiRect(0, 0, 350, 386), bg.Rect);
    }

    private static long Gold(GameSession session) => session.Map.Players[session.Map.Stacks[StackId].Owner].Bank.Gold;

    private static WindowContext Context(GameSession session) =>
        new(session, session.Map.Stacks[StackId].Owner, UiStrings.Load(Path.Combine(Data, "ui"), "ru"), UnitFaces.Load(Path.Combine(Data, "rules")));

    private static IReadOnlyList<UiScreen> Screens => UiScreens.Load(Path.Combine(Data, "ui", "screens.json"));

    private static UiScreenState Open(IWindowLogic logic, string screen)
    {
        var ui = new UiScreenState(logic.Shape(Screens.First(s => s.Id == screen)));
        logic.Open(ui);
        return ui;
    }

    private static (GameSession Session, string Site) Game(SiteKind kind, Func<MapSite, bool> fits, Bank? bank = null)
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", "rastlennaya-vlast-dwarf-t24-045.sg");
        Skip.IfNot(File.Exists(path), "нет сохранения в корпусе");
        var looks = MapLooksLoader.Load(path);
        var session = new GameSession(new GameState(GameInfo.FromMap("PtC", looks), looks), SharedContent.Load(Data).MapArt,
                                      GameBook.Load(Path.Combine(Data, "rules")));
        var site = session.Map.Sites.FirstOrDefault(s => s.Kind == kind && fits(s));
        Skip.If(site is null, $"нет площадки {kind}");
        var owner = session.Map.Players[session.Map.Stacks[StackId].Owner];
        var players = session.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[owner.Id] = owner with { Bank = bank ?? Rich };
        var map = GameSession.With(session.Map, players: players);
        return (new GameSession(session.State with { Looks = session.Looks with { Map = map } }, session.Art, session.Book), site!.Id);
    }
}
