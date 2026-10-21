using D2.Data.Play;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Карта 15 <c>port-synth-15-diplo</c> (<c>tools/synth-maps/maps_diplo.py</c>): у ИИ есть всё для дипломатии памятки — Кланы
/// в день 1 предлагают союз наверняка (отношение 95), у Легионов и Нежити есть кандидаты всех трёх видов подарка; десять дней по
/// памятке (Кланам «Да», остальным «Нет») проходят. Что и когда исполнит ИИ — печать: ответ — броски журнала оригинала.</summary>
public partial class PortSynthMapsTests
{
    private const string DiploMap = "port-synth-15-diplo";

    [SkippableFact]
    public void Diplo_DayOneOffer_GiftCandidates_TenDays()
    {
        Skip.IfNot(MapIds().Contains(DiploMap));
        var facts = Facts(DiploMap);
        var session = Begin(DiploMap, 1);
        var trace = new DiploTrace();
        session.AiTrace = trace;
        string human = session.ActiveHuman;
        var zones = facts.GetProperty("zones").EnumerateArray().ToList();
        string clans = Str(zones.Single(z => Str(z, "key") == "Кланы"), "player");
        foreach (var z in zones)
        {
            var player = session.Map.Players[Str(z, "player")];
            var cap = session.Map.Sites.Single(s => s.Kind == SiteKind.Capital && Same(s.Owner, player.Id));
            Assert.NotEmpty(cap.Items);
            Assert.True(player.Bank.Gold >= 2 * z.GetProperty("relation").GetInt32(), $"{Str(z, "key")}: казны на подарок золотом нет");
            Assert.All(z.GetProperty("spells").EnumerateArray(), sp => Assert.Contains(sp.GetString()!, player.KnownSpells, StringComparer.OrdinalIgnoreCase));
        }
        Assert.Empty(session.Map.Players[human].KnownSpells);
        for (int day = 1; day <= 9; day++)
        {
            session.EndTurn();
            foreach (var offer in session.State.Offers.Where(o => Same(o.To, human)).ToList())
            {
                string? answer = offer.Trade is not null ? session.AnswerTradeOffer(offer.From, human, accept: false)
                    : offer.Exchange ? null : session.AnswerAllianceOffer(offer.From, human, accept: Same(offer.From, clans));
                string what = offer.Trade is not null ? "сделка" : offer.Exchange ? "обмен картами" : "союз";
                string said = offer.Exchange ? "без ответа" : Same(offer.From, clans) && offer.Trade is null ? "да" : "нет";
                Out.WriteLine($"день {session.Map.Turn}: предложение {offer.From} ({what}, день {offer.Day}) — ответ {said} {answer}");
            }
            if (day == 1)
                Assert.True(Diplomacy.AreAllies(session.Map.Relations, 3, 5), "Кланы в день 1 союз не предложили (или он не принят)");
        }
        Assert.Equal(10, session.Map.Turn);
        foreach (var g in trace.Steps.GroupBy(s => (s.Player, s.Target)).OrderBy(g => g.Key.Player).ThenBy(g => g.Key.Target))
            Out.WriteLine($"{g.Key.Player} {g.Key.Target}: {string.Join(", ", g.Select(s => $"д{s.Day}→{s.Other}"))}");
        Assert.Contains(trace.Steps, s => Same(s.Player, clans) && s.Target == "Offer" && s.Day == 1);
    }

    private sealed class DiploTrace : IAiTrace
    {
        public List<(string Player, int Day, string Other, string Target)> Steps { get; } = [];

        public void Picked(string playerId, int day, AiTacticOption action, double score, int candidates) { }

        public void Stepped(string playerId, int day, AiExecuted step, int number)
        {
            if (step.Action.Type == AiStepType.Diplomacy) Steps.Add((playerId, day, step.Action.Id, step.Action.Target));
        }

        public void TurnEnded(string playerId, int day, long ms, int steps, int picks) { }
    }
}
