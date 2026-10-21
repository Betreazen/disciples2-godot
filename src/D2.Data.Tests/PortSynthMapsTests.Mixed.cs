using D2.Data.Play;
using D2.Rules.Battle;

namespace D2.Data.Tests;

/// <summary>Карта 17 <c>port-synth-17-mixed</c> (<c>tools/synth-maps/maps_mixed.py</c>): ситуации памятки складываются в модели порта —
/// Гоблин-страж по шаблону нападает на ходока сам (C5t не обойти), «Туман Смерти» дня 3 на Советника закрывает у Империи квадрат без
/// чужого обзора, вор гибнет у неподкупного лидера Кланов. Что движок сделает с призванным у своей деревни и кто убийца вора — печать:
/// ответ — журнал и сохранения оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string MixedMap = "port-synth-17-mixed";

    /// <summary>«Туман Смерти» (AREA 9) по разведанному без чужого обзора — квадрат 9 × 9 без четырёх углов (листинг <c>0x5d4dfc</c>).</summary>
    private const int FogOfDeathCells = 77;

    /// <summary>C5t: ходок шагает на ворота, Гоблин-страж (<c>L_GUARD</c>) появляется вплотную и в ход нейтралов того же дня нападает сам
    /// (<c>0x457317</c>: Чебышёв ≤ <c>GU_RANGE / 2</c>); после боя — <c>L_KILL_STACK</c> по шаблону.</summary>
    [SkippableFact]
    public void Mixed_C5t_GuardGoblinAttacksTheWalker()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        var facts = Facts(MixedMap);
        var session = Begin(MixedMap, 1);
        session.EventBattlesOnScreen = false;
        var seen = new Recorder();
        session.Watch(seen);
        var days = new EventDays(() => session.Map.Turn);
        session.Watch(days);
        var c5 = Zone(facts, "C5t");
        string walker = Str(c5, "stack"), tn = Str(c5, "template");
        var (gx, gy) = XY(c5, "gate_at");
        session.MoveStack(walker, gx, gy, 0, 0);
        Assert.Contains(days.Fired, f => Same(f.Id, Str(c5, "create_event")));
        var made = session.Map.Stacks.Values.Single(s => Same(s.TemplateId, tn));
        Assert.Equal(XY(c5, "made_at"), (made.X, made.Y));
        Assert.Equal((int)StackOrder.Guard, made.Order);
        session.EndTurn();
        var fight = Assert.Single(seen.Outcomes, o => Same(o.Defender, walker));
        Out.WriteLine($"бой: нападал {fight.Attacker}, победил {fight.Winner}; C5t — {string.Join(", ", days.Fired.Where(f => Same(f.Id, Str(c5, "event"))).Select(f => $"день {f.Day}"))}");
        Assert.Equal(made.Id, fight.Attacker, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(days.Fired, f => Same(f.Id, Str(c5, "event")));
    }

    /// <summary>FG: отряды Империи в дни 1–2 уходят от Советника за 15+ клеток, в день 3 весь квадрат 9 × 9 вокруг Советника у Империи
    /// разведан и не виден, и «Туман Смерти» закрывает его без чужого обзора — квадрат без углов (77, листинг <c>0x5d4dfc</c>). Форма у
    /// движка — сохранения оригинала до и после литья.</summary>
    [SkippableFact]
    public void Mixed_FogOfDeath_DayThree_OnTheBeacon()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        var facts = Facts(MixedMap);
        var session = Begin(MixedMap, 1);
        string human = session.ActiveHuman, empire = Str(facts, "watched");
        var fg = Zone(facts, "FG");
        var (bx, by) = XY(fg, "at");
        int half = fg.GetProperty("area").GetInt32() / 2;
        int castDay = fg.GetProperty("day").GetInt32();
        while (session.Map.Turn < castDay) session.EndTurn();
        Assert.Equal(castDay, session.Map.Turn);
        bool[,] Fog() => session.Map.Fog[session.Map.Players[empire].FogId];
        foreach (var l in fg.GetProperty("leavers").EnumerateArray())
        {
            var st = session.Map.Stacks[Str(l, "stack")];
            Out.WriteLine($"{Str(l, "key")}: ({st.X},{st.Y})");
            Assert.True(st.X - bx >= 15, $"{Str(l, "key")}: отряд Империи ушёл только на {st.X - bx} клеток");
        }
        int seen = 0;   // клетки квадрата, открытые у Империи (разведанные)
        for (int x = bx - half; x <= bx + half; x++)
            for (int y = by - half; y <= by + half; y++)
                seen += Fog()[x, y] ? 0 : 1;
        Assert.Equal((2 * half + 1) * (2 * half + 1), seen);
        int before = HiddenCount(Fog());
        Assert.Null(session.CastSpell(human, Str(fg, "spell"), bx, by, useSpellBank: false));
        int closed = HiddenCount(Fog()) - before;
        Out.WriteLine($"«Туман Смерти» на ({bx},{by}): у Империи закрыто {closed} из {seen} разведанных клеток квадрата");
        Assert.Equal(FogOfDeathCells, closed);
    }

    /// <summary>Г14: призванный у своей деревни — у движка ни пути, ни входа (<c>0x4d1590</c> → <c>0x4d1a6c</c>, <see cref="GameSession.OwnFortRefuses"/>),
    /// курсор над воротами — не <c>ENTERCITY</c> (<c>0x4d0bb5</c> → <c>CTaskStrat</c>); обычный отряд и вор получают курсор входа, путь и
    /// входят. Тест B: журнал оригинала даст партия карты 17.</summary>
    [SkippableFact]
    public void Mixed_SummonedAtOwnVillage()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        var facts = Facts(MixedMap);
        var session = Begin(MixedMap, 1);
        foreach (string key in new[] { "G14", "G14n", "G14t" })
        {
            var z = Zone(facts, key);
            var stack = session.Map.Stacks[Str(z, "stack")];
            var gate = D2.Rules.Map.PathFinder.EntranceOf(session.Map, Str(z, "target"))!.Value;
            var ctx = new D2.Data.Map.MapCursors.Context(session.Looks, session.RequireBook(), stack.Owner, stack.Id, null, (_, _) => false,
                                                         (s, x, y) => session.PathTo(s, x, y).Points.Count > 1);
            string cursor = D2.Data.Map.MapCursors.At(ctx, gate.X, gate.Y);
            bool refused = session.OwnFortRefuses(stack, session.ClickAim(stack, gate.X, gate.Y));
            Out.WriteLine($"{key}: курсор {cursor}, щелчок {(refused ? "без пути" : "путь")}");
            Assert.Equal(key == "G14", refused);
            Assert.Equal(key == "G14" ? D2.Data.Map.MapCursors.Default : D2.Data.Map.MapCursors.EnterCity, cursor);
            if (key == "G14") continue;
            var report = session.Enter(Str(z, "stack"), Str(z, "target"));
            var st = session.Map.Stacks[Str(z, "stack")];
            Out.WriteLine($"{key}: вход в свою деревню — {report}; отряд в ({st.X},{st.Y})");
            Assert.True(report.Entered, $"{key}: не вошёл");
        }
    }

    /// <summary>Покупки с «Лютней» — со скидкой лидера, иллюзия у пустой деревни — отказ захвата (И47, по листингу; ответ движка — журнал
    /// карты 17); вор гибнет у неподкупного лидера Кланов до свитка, его вещи — Кланам, убийца — печать.</summary>
    [SkippableFact]
    public void Mixed_LuteIllusion_AndIncorruptibleAi()
    {
        Skip.IfNot(MapIds().Contains(MixedMap));
        var facts = Facts(MixedMap);
        var session = Begin(MixedMap, 1);
        var lute = Zone(facts, "LUTE");
        string buyer = Str(lute, "stack"), human = session.ActiveHuman;
        long gold = session.Map.Players[human].Bank.Gold;
        Assert.Null(session.BuySpell(buyer, Str(lute, "mage"), Str(lute, "spell")));
        long afterSpell = session.Map.Players[human].Bank.Gold;
        Assert.Null(session.BuyItem(buyer, Str(lute, "shop"), Str(lute, "item")));
        long spellPaid = gold - afterSpell, itemPaid = afterSpell - session.Map.Players[human].Bank.Gold;
        Out.WriteLine($"LUTE: заклинание {spellPaid} золота, эликсир {itemPaid}");
        // И47: скидка лидера (Лютня очарования, L_LOWER_COST 10) — у мага (0x5d7a36…0x5d7a4b) и у торговца (0x5dfd5f): × (100 − 10) / 100.
        var book = session.RequireBook();
        Assert.Equal(10, session.LowerCostPercent(session.Map.Stacks[buyer]));
        Assert.Equal(D2.Rules.Map.Treasury.SpellBuyPrice(book.Spells[Str(lute, "spell")]).Gold * 90 / 100, spellPaid);
        Assert.Equal(D2.Rules.Map.Treasury.ItemBuyPrice(book.Items[Str(lute, "item")]).Gold * 90 / 100, itemPaid);
        // И47: иллюзия — IUsSummon (0x5a192e), захват отвергнут, как у призванного (X005TA0458); Герцог берёт.
        foreach (string key in new[] { "ILL", "ILLn" })
        {
            var z = Zone(facts, key);
            var report = session.Enter(Str(z, "stack"), Str(z, "target"));
            var fort = session.Map.Sites.Single(x => Same(x.Id, Str(z, "target")));
            Out.WriteLine($"{key}: пустая нейтральная деревня — {report}; хозяин {fort.Owner}");
            if (key == "ILL") Assert.Equal(GameSession.CaptureRefusal, report.Refused);
            else Assert.True(report.Captured, "ILLn: Герцог не взял деревню");
        }
        var inc = Zone(facts, "INC2");
        var thief = session.Map.Stacks[Str(inc, "stack")];
        string target = Str(inc, "target");
        Assert.NotEmpty(thief.Items);
        var expected = session.Map.Stacks[target].Items.Concat(thief.Items).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(session.ThiefIncorruptible(thief.Id, target));
        Assert.False(session.Map.Stacks.ContainsKey(thief.Id));
        Assert.Equal(expected, session.Map.Stacks[target].Items.Order(StringComparer.OrdinalIgnoreCase));
        var gone = Assert.Single(session.Map.Destroyed, d => Same(d.StackId, thief.Id));
        Out.WriteLine($"INC2: вор {thief.Id} погиб, убийца в CMidStackDestroyed — {gone.KillerId}");
    }
}
