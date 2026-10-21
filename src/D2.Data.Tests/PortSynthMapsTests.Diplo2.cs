using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 22 <c>port-synth-22-ai-diplo2</c> (<c>tools/synth-maps/maps_diplo2.py</c>): полигоны в модели порта складываются — союзы
/// Кланов с хода 0 и их проверки разрыва, снос жезла Легионов Нежитью и вор Легионов у деревни Нежити (отношения ИИ ↔ ИИ), бой бойца
/// Легионов со слабым нейтралом и его повторная оценка, защитники Нежити идут к своим охраняемым, зоны событий человека срабатывают.
/// Что выберет движок (разрыв и его день, цели окна после атаки, путь защитника) — печать: ответ — журнал оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string Diplo2Map = "port-synth-22-ai-diplo2";

    [SkippableFact]
    public void Diplo2_PolygonsAct_TwelveDays()
    {
        Skip.IfNot(MapIds().Contains(Diplo2Map));
        var facts = Facts(Diplo2Map);
        var session = Begin(Diplo2Map, 1);
        string human = session.ActiveHuman;
        var zones = facts.GetProperty("zones").EnumerateArray().ToDictionary(z => Str(z, "key"));
        // Союзы с хода 0: бит союза и ход союза из слова RELATION.
        foreach (var pair in facts.GetProperty("relations").GetProperty("allies").EnumerateArray())
        {
            int a = RaceType(session, pair[0].GetString()!), b = RaceType(session, pair[1].GetString()!);
            Assert.True(Diplomacy.AreAllies(session.Map.Relations, a, b), $"{a}-{b}: союза нет");
        }
        var trace = new TacticTrace();
        session.AiTrace = trace;
        var seen = new Recorder();
        session.Watch(seen);
        // Ин49: оба ходока — в зоны событий (EV1 — конец пути, EV2 — клетка зоны; обрыв посреди пути — у человека в партии).
        foreach (string key in new[] { "EV1", "EV2" })
        {
            var z = zones[key];
            var to = z.GetProperty("target_at");
            int x = key == "EV1" ? to[0].GetInt32() : 15, y = to[1].GetInt32();
            session.MoveStack(Str(z, "stack"), x, y, 0, 0);
        }
        Assert.Contains(seen.Events, e => Same(e.Event.Id, Str(zones["EV1"], "event")));
        Assert.Contains(seen.Events, e => Same(e.Event.Id, Str(zones["EV2"], "event")));
        int days = int.Parse(Str(facts, "days"), System.Globalization.CultureInfo.InvariantCulture);
        for (int day = 1; day < days && !session.Over; day++)
        {
            session.EndTurn();
            foreach (var offer in session.State.Offers.Where(o => Same(o.To, human)).ToList())
            {
                if (offer.Trade is not null) session.AnswerTradeOffer(offer.From, human, accept: false);
                else if (!offer.Exchange) session.AnswerAllianceOffer(offer.From, human, accept: false);
                Out.WriteLine($"день {session.Map.Turn}: предложение {offer.From} — «Нет»");
            }
        }
        Assert.Equal(days, session.Map.Turn);
        var bk = zones["BK"];
        string clans = Str(bk, "player");
        foreach (var other in bk.GetProperty("allies").EnumerateArray())
        {
            var p = session.Map.Players[other.GetString()!];
            Out.WriteLine($"BK: Кланы — {p.Id}: союз {Diplomacy.AreAllies(session.Map.Relations, RaceType(session, clans), RaceType(session, p.Id))}");
        }
        foreach (var o in seen.Outcomes) Out.WriteLine($"бой: {o.Attacker} → {o.Defender}, победил {o.Winner}");
        foreach (string key in new[] { "RD", "NB", "RE", "DP1", "DP2" })
        {
            string stack = Str(zones[key], "stack");
            var picks = trace.Picks.Where(p => Same(p.Stack, stack)).ToList();
            foreach (var p in picks.Take(8)) Out.WriteLine($"{key} день {p.Day}: {p.Action} → {p.Target} ({p.X},{p.Y}) {p.Score}");
            Assert.True(picks.Count > 0, $"{key}: у {stack} нет выборов тактики");
        }
        string fighter = Str(zones["RE"], "stack");
        Assert.Contains(seen.Outcomes, o => Same(o.Attacker, fighter) && Same(o.Defender, Str(zones["RE"], "target")) && Same(o.Winner, fighter));
        Assert.True(trace.Picks.Count(p => Same(p.Stack, fighter) && p.Day == 1) >= 2, "RE: после атаки нет повторной оценки в тот же день");
    }

    private static int RaceType(GameSession session, string player) =>
        session.Book!.Economy.Races[session.Map.Players[player].RaceId].RaceType;
}
