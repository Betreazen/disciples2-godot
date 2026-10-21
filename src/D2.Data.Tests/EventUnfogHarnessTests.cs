using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// П. 25 <c>docs/port-status-20261016.md</c> — туман от события (<c>L_UNFOG</c>, <c>CEffectFog</c> <c>0x438314</c> → <c>0x5d5230</c>) против
/// сохранения оригинала: синтетическая карта 3 (<c>docs/synthetic-maps-port/port-synth-3-events</c>, метка E12), партия
/// <c>corpus/20261016b-port-synth</c>.
/// </summary>
[Trait("Category", "Synth")]
public class EventUnfogHarnessTests(ITestOutputHelper output)
{
    private const string Run = "20261016b-port-synth";
    private const string Day1 = "port-synth-3-events-human-t01-009.sg";

    /// <summary>Событие E12 (<c>facts.json</c> карты 3): ходок человека с (30,22) входит в ворота (32,22), туман открывается вокруг (60,22).</summary>
    private const string E12 = "S143EV000b", Trigger = "S143KC0011";

    /// <summary>Шаг ходоков памятки: ворота — клетка в двух шагах правее ходока.</summary>
    private const int WalkerStep = 2;

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly Lazy<(GameContent Content, GameBook Book)> Shared = new(() =>
        (SharedContent.Load(Data), GameBook.Load(Path.Combine(Data, "rules"))));

    /// <summary>
    /// E12 сработало в день 1 до первого сохранения (в нём событие уже выключено, ходок стоит в воротах), сохранения «до» нет. Поэтому
    /// сверка по клеткам, которые мог открыть только эффект: порт берёт сохранение дня 1, скрывает человеку всю карту, открывает обзор его
    /// отрядов и крепостей (<see cref="GameSession.Reveal"/>) и обзор отрядов с клетки на <see cref="WalkerStep"/> левее (откуда шли ходоки) —
    /// это «другие источники»; затем ставит ходока E12 назад на (30,22) и вводит в ворота (<see cref="GameSession.MoveStack"/> →
    /// проверка событий шага, в наборе только E12 — включённое, как до срабатывания). В окне эффекта (<c>±(r + 2)</c> вокруг центра
    /// локации) каждая клетка, которую другие источники не открывают, у движка открыта ровно тогда, когда её открыл эффект порта.
    /// Измерено 16.10: квадрат <c>r = радиус + 1 = 4</c> без угловых клеток — у движка угол (64,18) скрыт, клетки x = 65 (r = 5) скрыты.
    /// </summary>
    [SkippableFact]
    public void E12_Unfog_MatchesOriginalSave()
    {
        string path = Path.Combine(GameSaveTests.CorpusDir(), Run, Day1);
        Skip.IfNot(File.Exists(path), $"нет {Run}/{Day1}");
        var engine = MapLooksLoader.Load(path).Map;
        var looks = MapLooksLoader.Load(path);
        var events = EventLoader.Load(path);
        var e12 = events.Events.Single(e => Same(e.Id, E12));
        Assert.False(e12.Enabled);   // одноразовое событие уже сработало
        var unfog = e12.Effects.OfType<UnfogEffect>().Single();
        var zone = events.Zones[unfog.LocationId];
        string human = Assert.Single(unfog.Entries.Select(x => x.PlayerId).Distinct(StringComparer.OrdinalIgnoreCase));
        var stack = looks.Map.Stacks[Trigger];

        var session = new GameSession(new GameState(GameInfo.FromMap("x", looks), looks)
        {
            Events = events with { Events = [e12 with { Enabled = true }] },
            EventProgress = EventProgress.From(events with { Events = [e12 with { Enabled = true }] }),
        }, Shared.Value.Content.MapArt, Shared.Value.Book);
        var fog = session.Map.Fog[session.Map.Players[human].FogId];
        for (int x = 0; x < session.Map.Size; x++)
            for (int y = 0; y < session.Map.Size; y++) fog[x, y] = true;
        session.MoveStack(Trigger, stack.X - WalkerStep, stack.Y, 0, 0);
        Assert.DoesNotContain(E12, session.MoveCheckOf(session.Map.Stacks[Trigger]) ?? [], StringComparer.OrdinalIgnoreCase);
        session.Reveal(human);
        foreach (var s in session.Map.Stacks.Values.Where(s => Same(s.Owner, human)))
            Fog.Reveal(fog, s.X - WalkerStep, s.Y, session.ScoutRange(s), Fog.DefaultShape);
        var others = (bool[,])fog.Clone();
        session.MoveStack(Trigger, stack.X, stack.Y, 0, 0);
        Assert.Contains(E12, session.MoveCheckOf(session.Map.Stacks[Trigger]) ?? [], StringComparer.OrdinalIgnoreCase);

        var engineFog = engine.Fog[engine.Players[human].FogId];
        int r = (unfog.Value == -1 ? zone.Radius : unfog.Value) + 1, window = r + 2;
        int attributable = 0, open = 0, differ = 0;
        for (int x = Math.Max(0, zone.X - window); x <= Math.Min(engine.Size - 1, zone.X + window); x++)
            for (int y = Math.Max(0, zone.Y - window); y <= Math.Min(engine.Size - 1, zone.Y + window); y++)
            {
                if (!others[x, y]) continue;   // открыто другим источником — эффекту не приписать
                attributable++;
                bool want = !engineFog[x, y], got = !fog[x, y];
                if (got) open++;
                if (want == got) continue;
                differ++;
                output.WriteLine($"({x},{y}): движок {(want ? "открыто" : "скрыто")}, порт {(got ? "открыто" : "скрыто")}");
            }
        output.WriteLine($"центр ({zone.X},{zone.Y}), r {r}: клеток окна только от эффекта {attributable}, открыл эффект {open}, расхождений {differ}");
        Assert.True(attributable >= 10 && open > 0, "окно без клеток, которые открывает только эффект");
        Assert.Equal(0, differ);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
