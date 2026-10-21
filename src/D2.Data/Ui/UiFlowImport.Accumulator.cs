namespace D2.Data.Ui;

public static partial class UiFlowImport
{
    /// <summary>Сбор переходов и счётчиков по всем журналам; на выходе — упорядоченный граф и отчёт.</summary>
    private sealed class Accumulator(IReadOnlySet<string> screenIds)
    {
        private readonly Dictionary<(string Src, UiFlowEdge Edge), int> _edges = [];
        private readonly Dictionary<string, SortedSet<UiPhase>> _phases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);

        public int Clicks, ClicksNoChange, ClicksUnnamed, ToggleDuplicates, EngineBursts, StackMismatches,
            ShowRefused, Clears, MsgBoxes, MsgBoxesUnpaired, OpenFailed;

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Clicked { get; } = new(StringComparer.Ordinal);
        public List<string> Unnamed { get; } = [];

        /// <summary>Экран встретился в фазе <paramref name="phase"/>: щелчок по нему или показ (фаза — после вспышки:
        /// заставка сценария показывается до <c>game_phase enter</c>, но принадлежит партии).</summary>
        public void SourcePhase(string screen, UiPhase phase) => PhasesOf(screen).Add(phase);

        public void Show(string screen, string kind)
        {
            Seen.Add(screen);
            _kinds.TryAdd(screen, kind);
        }

        public void Add(string source, string? element, UiFlowEdge edge)
        {
            var key = (source, edge with { Element = element });
            _edges[key] = _edges.GetValueOrDefault(key) + 1;
        }

        private SortedSet<UiPhase> PhasesOf(string screen) =>
            _phases.TryGetValue(screen, out var set) ? set : _phases[screen] = [];

        public UiFlow ToFlow(IReadOnlyList<string> sources)
        {
            var bySource = _edges.GroupBy(e => e.Key.Src).ToDictionary(g => g.Key,
                g => g.Select(e => e.Key.Edge with { Seen = e.Value }).OrderBy(e => e.Element is null)
                      .ThenBy(e => e.Element, StringComparer.Ordinal).ThenBy(e => e.Action)
                      .ThenBy(e => e.Target, StringComparer.Ordinal).ThenBy(e => e.Via, StringComparer.Ordinal)
                      .ThenBy(e => e.Phase).ThenBy(e => e.Message, StringComparer.Ordinal).ToList());

            var screens = _phases.Keys.Union(bySource.Keys).Order(StringComparer.Ordinal)
                .Select(id => new UiFlowScreen
                {
                    Id = id,
                    Phases = _phases.TryGetValue(id, out var p) ? p.ToList() : [],
                    Edges = bySource.TryGetValue(id, out var e) ? e : [],
                }).ToList();

            var engine = Names().Where(n => !screenIds.Contains(n)).Order(StringComparer.Ordinal)
                .Select(n => new UiFlowEngineScreen(n, _kinds.GetValueOrDefault(n, "other"))).ToList();
            return new UiFlow { Sources = sources, EngineScreens = engine, Screens = screens };
        }

        /// <summary>Все имена экранов, на которые ссылается граф.</summary>
        private IEnumerable<string> Names() =>
            _phases.Keys.Concat(_edges.Keys.Select(k => k.Src))
                .Concat(_edges.Keys.SelectMany(k => new[] { k.Edge.Via, k.Edge.Target }))
                .OfType<string>().Distinct(StringComparer.Ordinal);

        public UiFlowReport ToReport() => new()
        {
            Clicks = Clicks, ClicksNoChange = ClicksNoChange, ClicksUnnamed = ClicksUnnamed,
            ToggleDuplicates = ToggleDuplicates, EngineBursts = EngineBursts, StackMismatches = StackMismatches,
            ShowRefused = ShowRefused, Clears = Clears, MsgBoxes = MsgBoxes, MsgBoxesUnpaired = MsgBoxesUnpaired,
            OpenFailed = OpenFailed,
            SeenScreens = Seen.Order(StringComparer.Ordinal).ToList(),
            ClickedElements = Clicked.Order(StringComparer.Ordinal).ToList(),
            UnknownNames = Unnamed,
        };
    }
}
