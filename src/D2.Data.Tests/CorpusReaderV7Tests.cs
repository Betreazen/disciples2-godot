using D2.Data.Corpus;

/// <summary>Схема 7.0: события интерфейса и игрового цикла разбираются в свои записи, ни одно не уходит в
/// <see cref="UnknownEvent"/>. Строки — по форматам <c>probeui.cpp</c>; живых журналов 7.0 ещё нет.</summary>
public class CorpusReaderV7Tests
{
    private static CorpusEvent One(string line)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, line + "\n");
        try { return CorpusReader.Read(path).Single(); }
        finally { File.Delete(path); }
    }

    [Fact] public void UiOpenAndClose_PairByDialogAddress()
    {
        var o = Assert.IsType<UiOpenEvent>(One("""{"seq":5,"tid":1,"t":20,"ev":"ui_open","name":"DLG_MAIN_MENU","dialog":"0x1a2b3c","parent_class":"CMenuMain","kind":"menu","top":"none","ok":true,"ret_addr":"0x4dadfe"}"""));
        Assert.Equal(("DLG_MAIN_MENU", "0x1a2b3c", "CMenuMain", "menu", "none", true, "0x4dadfe"),
            (o.Name, o.Dialog, o.ParentClass, o.Kind, o.Top, o.Ok, o.RetAddr));

        var c = Assert.IsType<UiCloseEvent>(One("""{"seq":9,"tid":1,"t":30,"ev":"ui_close","name":"DLG_MAIN_MENU","dialog":"0x1a2b3c"}"""));
        Assert.Equal((o.Name, o.Dialog), (c.Name, c.Dialog));
    }

    [Fact] public void UiStack_ShowHideRefusedAndClear()
    {
        var show = Assert.IsType<UiStackEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ui_show","screen":"DLG_MESSAGE_BOX","class":"CMidgardMsgBox","kind":"popup","depth":2,"top":"DLG_MESSAGE_BOX","ret_addr":"0x5c79a0"}"""));
        Assert.Equal(("show", "DLG_MESSAGE_BOX", "CMidgardMsgBox", "popup", 2L), (show.Action, show.Screen, show.Class, show.Kind, show.Depth));

        var hide = Assert.IsType<UiStackEvent>(One("""{"seq":2,"tid":1,"t":3,"ev":"ui_hide","screen":"DLG_MESSAGE_BOX","class":"CMidgardMsgBox","kind":"popup","depth":1,"top":"DLG_ISO","ret_addr":"0x5c7a00"}"""));
        Assert.Equal(("hide", 1L, "DLG_ISO"), (hide.Action, hide.Depth, hide.Top));

        Assert.Equal("show_refused", Assert.IsType<UiStackEvent>(One("""{"seq":3,"tid":1,"t":3,"ev":"ui_show_refused","screen":"CFoo","class":"CFoo","kind":"other","depth":1,"top":"DLG_ISO","ret_addr":"0x1"}""")).Action);

        var clear = Assert.IsType<UiClearEvent>(One("""{"seq":4,"tid":1,"t":4,"ev":"ui_clear","depth_before":3,"ret_addr":"0x401000"}"""));
        Assert.Equal((3L, "0x401000"), (clear.DepthBefore, clear.RetAddr));
    }

    [Fact] public void UiClick_KindSpecificFieldsAreNullWhenAbsent()
    {
        var b = Assert.IsType<UiClickEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ui_click","kind":"button","screen":"DLG_MAIN_MENU","element":"BTN_SINGLE","hotkey":true,"key":13,"top":"DLG_MAIN_MENU","ret_addr":"0x5318da"}"""));
        Assert.Equal(("button", "DLG_MAIN_MENU", "BTN_SINGLE", (bool?)true, (long?)13, (bool?)null, (long?)null),
            (b.Kind, b.Screen, b.Element, b.Hotkey, b.Key, b.Checked, b.Index));

        var mouse = Assert.IsType<UiClickEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ui_click","kind":"button","screen":"DLG_ISO","element":"BTN_END_TURN","hotkey":false,"top":"DLG_ISO","ret_addr":"0x1"}"""));
        Assert.Equal(((bool?)false, (long?)null), (mouse.Hotkey, mouse.Key));

        var tog = Assert.IsType<UiClickEvent>(One("""{"seq":3,"tid":1,"t":2,"ev":"ui_click","kind":"toggle","screen":"DLG_OPTIONS","element":"TOG_MUSIC","checked":false,"top":"DLG_OPTIONS","ret_addr":"0x534bf5"}"""));
        Assert.Equal(((bool?)false, (bool?)null), (tog.Checked, tog.Hotkey));

        var lbox = Assert.IsType<UiClickEvent>(One("""{"seq":4,"tid":1,"t":2,"ev":"ui_click","kind":"lbox","screen":"DLG_LOAD_GAME","element":"","index":2,"top":"DLG_LOAD_GAME","ret_addr":"0x1"}"""));
        Assert.Equal(("", (long?)2), (lbox.Element, lbox.Index));

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":5,"tid":1,"t":2,"ev":"ui_click","kind":"button","screen":"X","top":"X","ret_addr":"0x1"}"""));
    }

    [Fact] public void UiTextMsgBoxAndGamePhase()
    {
        var text = Assert.IsType<UiTextEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ui_text","screen":"DLG_MAIN_MENU","id":"X005TA0001","opening":true}"""));
        Assert.Equal(("DLG_MAIN_MENU", "X005TA0001", true), (text.Screen, text.Id, text.Opening));

        var box = Assert.IsType<UiMsgBoxEvent>(One("""{"seq":2,"tid":1,"t":3,"ev":"ui_msgbox","text":"В отряде нет \"места\"","last_text_id":"X005TA0247","yes_no":false,"top":"DLG_ISO","ret_addr":"0x49846c"}"""));
        Assert.Equal(("В отряде нет \"места\"", "X005TA0247", false), (box.Text, box.LastTextId, box.YesNo));
        Assert.Null(Assert.IsType<UiMsgBoxEvent>(One("""{"seq":3,"tid":1,"t":3,"ev":"ui_msgbox","text":"","last_text_id":null,"yes_no":true,"top":"none","ret_addr":"0x1"}""")).LastTextId);

        var enter = Assert.IsType<GamePhaseEvent>(One("""{"seq":4,"tid":1,"t":4,"ev":"game_phase","what":"enter","phase":"0x2b00f0","top":"DLG_LOAD_GAME"}"""));
        Assert.Equal(("enter", "0x2b00f0", "DLG_LOAD_GAME"), (enter.What, enter.Phase, enter.Top));
    }

    [Fact] public void Screenshot_CameraAndDisplay()
    {
        var shot = Assert.IsType<ScreenshotEvent>(One("""{"seq":5,"tid":1,"t":9,"ev":"screenshot","file":"ScreenShot003.bmp","camera":{"x":12,"y":30,"dx":0,"dy":0},"display":{"width":800,"height":600,"use_d3d":false,"fullscreen":false,"animated_iso":true,"iso_anim":true,"flipped_battle":false,"turn_summary":true},"ai_player":null,"top":"DLG_ISO","top_class":"CMainView2"}"""));
        Assert.Equal(("ScreenShot003.bmp", new ShotCamera(12, 30, 0, 0)), (shot.File, shot.Camera));
        Assert.Equal((800L, 600L, false), (shot.Display!.Width, shot.Display.Height, shot.Display.UseD3d));

        var menu = Assert.IsType<ScreenshotEvent>(One("""{"seq":6,"tid":1,"t":9,"ev":"screenshot","file":null,"camera":null,"display":null,"ai_player":null,"top":"DLG_MAIN_MENU","top_class":"CMenuMain"}"""));
        Assert.Null(menu.Camera);
        Assert.Null(menu.File);

        var key = Assert.IsType<ScreenshotEvent>(One("""{"seq":7,"tid":1,"t":9,"ev":"screenshot","via":"key","file":null,"time_ms":1790000000000,"camera":{"x":1,"y":2,"dx":0,"dy":0},"display":null,"ai_player":null,"top":"DLG_ISO","top_class":"CStratInterf"}"""));
        Assert.Equal(("key", (long?)1790000000000), (key.Via, key.TimeMs));
    }

    [Fact] public void MapClick_CellObjectsAndTask()
    {
        var click = Assert.IsType<MapClickEvent>(One("""{"seq":8,"tid":1,"t":9,"ev":"map_click","msg":513,"button":1,"action":"down","x":412,"y":305,"cell":{"x":17,"y":22},"objects":["S143KC0005","S143FT0001"],"task":"CTaskStrat","busy":0,"ret":1,"top":"DLG_ISO","top_class":"CStratInterf"}"""));
        Assert.Equal((513L, 1L, "down", 412L, 305L), (click.Msg, click.Button, click.Action, click.X, click.Y));
        Assert.Equal(new MapClickCell(17, 22), click.Cell);
        Assert.Equal(["S143KC0005", "S143FT0001"], click.Objects);
        Assert.Equal(("CTaskStrat", 0L, 1L, "CStratInterf"), (click.Task, click.Busy, click.Ret, click.TopClass));

        // Щелчок по панели справа: клетки нет, объектов нет; задачи ещё нет.
        var panel = Assert.IsType<MapClickEvent>(One("""{"seq":9,"tid":1,"t":9,"ev":"map_click","msg":516,"button":2,"action":"down","x":760,"y":40,"cell":null,"objects":null,"task":null,"busy":0,"ret":0,"top":"DLG_ISO","top_class":"CStratInterf"}"""));
        Assert.Null(panel.Cell);
        Assert.Null(panel.Objects);
        Assert.Null(panel.Task);

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":10,"tid":1,"t":9,"ev":"map_click","msg":513,"button":1,"action":"down","x":1,"y":2,"objects":null,"task":null,"busy":0,"ret":0,"top":"DLG_ISO"}"""));
    }

    [Fact] public void RngState_LoadKeepsProcessGenerator()
    {
        var table = string.Concat(Enumerable.Range(0, RngSeedEvent.TableSize).Select(i => (i * 7).ToString("x4")));
        var load = Assert.IsType<RngStateEvent>(One($$"""{"seq":11,"tid":2,"t":9,"ev":"rng_state","what":"load","n":131284,"index":144,"wide":0,"table":"{{table}}","saved_index":93,"saved_same":false,"random":true,"ret_addr":"0x429e39"}"""));
        Assert.Equal(("load", 131284L, 144L, 0L, "0x429e39"), (load.What, load.N, load.Index, load.Wide, load.RetAddr));
        Assert.Equal(((long?)93, (bool?)false, (bool?)true), (load.SavedIndex, load.SavedSame, load.Random));
        Assert.Equal(RngSeedEvent.TableSize, load.Table.Count);
        Assert.Equal((ushort)7, load.Table[1]);

        // Путь без заголовка сохранения (0x429c92) и незаданные настройки — null.
        var bare = Assert.IsType<RngStateEvent>(One($$"""{"seq":12,"tid":2,"t":9,"ev":"rng_state","what":"new","n":0,"index":50,"wide":0,"table":"{{table}}","saved_index":null,"saved_same":null,"random":null,"ret_addr":"0x429d0a"}"""));
        Assert.Equal(((long?)null, (bool?)null, (bool?)null), (bare.SavedIndex, bare.SavedSame, bare.Random));

        Assert.Throws<System.Text.Json.JsonException>(() => One("""{"seq":13,"tid":2,"t":9,"ev":"rng_state","what":"load","n":0,"index":0,"wide":0,"table":"00","saved_index":null,"saved_same":null,"random":null,"ret_addr":"0x1"}"""));
    }

    [Fact] public void V70Lbox_WhatDerivedFromReturnAddress()
    {
        // 20260919-human: 111 строк с возвратом 0x535f8a (прокрутка) и 204 с 0x536168 (щелчок по строке).
        UiClickEvent Lbox(string ret) => Assert.IsType<UiClickEvent>(One($$"""{"seq":1,"tid":1,"t":2,"ev":"ui_click","kind":"lbox","screen":"DLG_CHOOSE_SKIRMISH","element":"TLBOX_GAME_SLOT","index":51,"top":"X","ret_addr":"{{ret}}"}"""));
        Assert.Equal("scroll", Lbox("0x535f8a").What);
        Assert.Equal("select", Lbox("0x536168").What);
        Assert.Equal("other", Lbox("0x1").What);

        var b = Assert.IsType<UiClickEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ui_click","kind":"button","screen":"X","element":"BTN_OK","hotkey":false,"top":"X","ret_addr":"0x535f8a"}"""));
        Assert.Null(b.What);
        Assert.Null(b.TopClass);
    }

    [Fact] public void V71Fields_Parsed()
    {
        var show = Assert.IsType<UiStackEvent>(One("""{"seq":1,"tid":1,"t":2,"ev":"ui_show","screen":"DLG_ISO","iface":"0x26e3aca8","class":"CDiplomacyInterf","kind":"fullscreen","depth":1,"top":"DLG_ISO","top_class":"CDiplomacyInterf","ret_addr":"0x40597e"}"""));
        Assert.Equal(("0x26e3aca8", "CDiplomacyInterf", (string?)null), (show.Iface, show.TopClass, show.Base));

        var open = Assert.IsType<UiOpenEvent>(One("""{"seq":2,"tid":1,"t":2,"ev":"ui_open","name":"DLG_BATTLE_B","base":"DLG_BATTLE_A","dialog":"0x10","parent":"0x20","parent_class":"CBattleViewerInterf","kind":"fullscreen","top":"DLG_ISO","top_class":"CStratInterf","ok":true,"ret_addr":"0x1"}"""));
        Assert.Equal(("0x20", "CStratInterf", "DLG_BATTLE_A"), (open.Parent, open.TopClass, open.Base));

        var lbox = Assert.IsType<UiClickEvent>(One("""{"seq":3,"tid":1,"t":2,"ev":"ui_click","kind":"lbox","screen":"DLG_LOAD_GAME","element":"TLBOX_SAVES","what":"select","index":2,"top":"DLG_LOAD_GAME","top_class":"CLoadGameInterf","ret_addr":"0x536168"}"""));
        Assert.Equal(("select", "CLoadGameInterf"), (lbox.What, lbox.TopClass));

        var unnamed = Assert.IsType<UiClickEvent>(One("""{"seq":4,"tid":1,"t":2,"ev":"ui_click","kind":"button","screen":"DLG_ISO_DIPLO","element":"","child":27,"area":[260,285,390,427],"hotkey":false,"top":"DLG_ISO","top_class":"CDiplomacyInterf","ret_addr":"0x531ebf"}"""));
        Assert.Equal(27L, unnamed.Child);
        Assert.Equal(new long[] { 260, 285, 390, 427 }, unnamed.Area);

        var tog = Assert.IsType<UiClickEvent>(One("""{"seq":5,"tid":1,"t":2,"ev":"ui_click","kind":"toggle","screen":"DLG_BATTLE_B","base":"DLG_BATTLE_A","element":"TOG_AUTOBATTLE","hotkey":true,"key":65,"checked":true,"top":"DLG_BATTLE_B","top_class":"CBattleViewerInterf","ret_addr":"0x534bf5"}"""));
        Assert.Equal(((bool?)true, (long?)65, (bool?)true, "DLG_BATTLE_A"), (tog.Hotkey, tog.Key, tog.Checked, tog.Base));

        var load = Assert.IsType<GamePhaseEvent>(One("""{"seq":6,"tid":1,"t":2,"ev":"game_phase","what":"load","phase":"0x1","top":"DLG_WAIT","top_class":"CWaitInterf"}"""));
        Assert.True(load.IsEnter);
        Assert.Equal("CWaitInterf", load.TopClass);
        Assert.False(Assert.IsType<GamePhaseEvent>(One("""{"seq":7,"tid":1,"t":2,"ev":"game_phase","what":"leave","phase":"0x1","top":"DLG_WAIT"}""")).IsEnter);
    }

    [Fact] public void JournalMixingOldAndNewEvents_ReadsBoth()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, [
            """{"seq":1,"tid":1,"ev":"start","exe_size":1,"version":"Gog","probe":"dev","autobattle":false}""",
            """{"seq":2,"tid":1,"t":2,"ev":"ui_refusal","text":"X005TA0247","what":"no_space","ret_addr":"0x498477"}""",
            """{"seq":3,"tid":1,"t":2,"ev":"ui_text","screen":"DLG_ISO","id":"X005TA0247","opening":false}""",
        ]);
        try
        {
            var events = CorpusReader.Read(path).ToList();
            Assert.Equal(3, events.Count);
            Assert.DoesNotContain(events, e => e is UnknownEvent);
        }
        finally { File.Delete(path); }
    }
}
