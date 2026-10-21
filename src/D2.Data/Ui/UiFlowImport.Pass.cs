using D2.Data.Corpus;

namespace D2.Data.Ui;

public static partial class UiFlowImport
{
    /// <summary>Экран в стеке менеджера: имя, как его пишет зонд, и класс. Сравнивается по ссылке — два окна
    /// сообщений в стеке разные экраны.</summary>
    private sealed class Entry(string screen, string cls)
    {
        public string Screen { get; set; } = screen;
        public string Class { get; set; } = cls;
        public string Logical => UiFlowImport.Logical(Screen, Class);
    }

    /// <summary>Итог одной вспышки.</summary>
    private sealed class Outcome(Entry? source)
    {
        public Entry? Source { get; } = source;
        public string? Target { get; set; }
        public string? Via { get; set; }
        public string? Message { get; set; }
        public bool Hidden { get; set; }
        public bool PhaseEvent { get; set; }
        public Dictionary<string, int> Net { get; } = new(StringComparer.Ordinal);
        public List<string> Opened { get; } = [];
        public List<string> Shown { get; } = [];
    }

    /// <summary>Проход по одному журналу: состояние стека и фазы плюс разбиение на вспышки.</summary>
    private sealed class Pass(Accumulator acc, List<CorpusEvent> ev)
    {
        private readonly List<Entry> _stack = [];
        private UiPhase _phase = UiPhase.Menu;
        private string _top = "none";

        private UiPhase Phase => _stack.Any(s => s.Logical == BattleScreen) ? UiPhase.Battle : _phase;

        public void Run()
        {
            int i = 0;
            while (i < ev.Count)
                i = ev[i] switch
                {
                    UiClickEvent c => OnClick(i, c),
                    UiStackEvent or UiClearEvent or UiMsgBoxEvent => OnEngine(i),
                    _ => Step(i),
                };
        }

        private int Step(int i)
        {
            Apply(ev[i], null);
            return i + 1;
        }

        private int OnClick(int i, UiClickEvent c)
        {
            acc.Clicks++;
            if (c.Kind == "button" && i + 1 < ev.Count && ev[i + 1] is UiClickEvent { Kind: "toggle" } t &&
                t.Screen == c.Screen && t.Element == c.Element)
            {
                acc.ToggleDuplicates++;
                return i + 1;
            }

            int end = BurstEnd(i);
            string source = Alias(c.Screen);
            if (c.Screen.Length == 0 || c.Element.Length == 0)
            {
                acc.ClicksUnnamed++;
                acc.Unnamed.Add($"{c.Screen}.{c.Element} seq {c.Seq}");
                Replay(i + 1, end, null);
                return end;
            }

            acc.SourcePhase(source, Phase);
            acc.Clicked.Add($"{source}.{c.Element}");
            var edge = Replay(i + 1, end, _stack.LastOrDefault());
            if (edge.Action == UiFlowAction.None) acc.ClicksNoChange++;
            acc.Add(source, c.Element, edge);
            return end;
        }

        private int OnEngine(int i)
        {
            int end = BurstEnd(i);
            var top = _stack.LastOrDefault();
            if (top is not null) acc.SourcePhase(top.Logical, Phase);
            var edge = Replay(i, end, top);
            acc.EngineBursts++;
            if (top is not null && edge.Action != UiFlowAction.None) acc.Add(top.Logical, null, edge);
            return end;
        }

        /// <summary>Конец вспышки, начатой событием <paramref name="i"/> (правило — в комментарии класса).</summary>
        private int BurstEnd(int i)
        {
            long last = T(ev[i]);
            string top = ev[i] is UiStackEvent s ? s.Top : _top;
            int j = i + 1;
            for (; j < ev.Count; j++)
            {
                var e = ev[j];
                if (e is UiClickEvent) break;
                bool holding = top == "none" || Transitions.Contains(top);
                if (!holding && (e is InputEvent || T(e) - last > GapMs)) break;
                last = T(e);
                if (e is UiStackEvent st && st.Action != "show_refused") top = st.Top;
                else if (e is UiClearEvent) top = "none";
            }

            return j;
        }

        private UiFlowEdge Replay(int from, int end, Entry? source)
        {
            var o = new Outcome(source);
            var before = Phase;
            for (int k = from; k < end; k++) Apply(ev[k], o);

            var after = Phase;
            foreach (var shown in o.Shown) acc.SourcePhase(shown, after);
            string? panel = o.Target is null ? o.Opened.FirstOrDefault(n => o.Net[n] > 0) : null;
            var action = o.Target is not null ? (o.Hidden ? UiFlowAction.Goto : UiFlowAction.Open)
                : o.Hidden ? UiFlowAction.Close
                : panel is not null ? UiFlowAction.Panel
                : UiFlowAction.None;
            return new UiFlowEdge
            {
                Action = action,
                Target = action == UiFlowAction.Panel ? panel : o.Target,
                Via = action is UiFlowAction.Open or UiFlowAction.Goto ? o.Via : null,
                Phase = after != before || o.PhaseEvent ? after : null,
                Message = o.Message,
            };
        }

        private void Apply(CorpusEvent e, Outcome? o)
        {
            switch (e)
            {
                case UiStackEvent s: ApplyStack(s, o); break;
                case UiClearEvent:
                    acc.Clears++;
                    _stack.Clear();
                    _top = "none";
                    break;
                case UiOpenEvent op when !op.Ok: acc.OpenFailed++; break;
                case UiOpenEvent op:
                    string name = Alias(op.Name);
                    acc.Seen.Add(name);
                    if (o is null) break;
                    if (!o.Net.ContainsKey(name)) o.Opened.Add(name);
                    o.Net[name] = o.Net.GetValueOrDefault(name) + 1;
                    break;
                case UiCloseEvent cl when o is not null:
                    o.Net[Alias(cl.Name)] = o.Net.GetValueOrDefault(Alias(cl.Name)) - 1;
                    break;
                case UiMsgBoxEvent m:
                    acc.MsgBoxes++;
                    if (o is null) acc.MsgBoxesUnpaired++;
                    else o.Message = m.LastTextId ?? "";
                    break;
                case GamePhaseEvent g:
                    _phase = g.IsEnter ? UiPhase.Game : UiPhase.Menu;
                    if (o is not null) o.PhaseEvent = true;
                    break;
            }
        }

        /// <summary>Показ экрана в модели стека. Два случая, где зонд называет экран не так, как снимает:
        /// (1) повторный показ того, что уже наверху (глубина не выросла): при первом показе объект ещё
        /// строился — класс прочитан базовый (<c>CAnimInterf</c>), диалога не было — и имя уточняется;
        /// (2) показ под модальным окном (<c>top</c> после вызова — не он): зонд пишет <c>ui_show</c> после
        /// оригинала, поэтому окно, показанное изнутри показа экрана, стоит в журнале раньше него.</summary>
        private Entry Push(UiStackEvent s)
        {
            if (_stack.Count > 0 && _stack.Count == s.Depth && s.Top == s.Screen)
            {
                var top = _stack[^1];
                (top.Screen, top.Class) = (s.Screen, s.Class);
                return top;
            }

            var entry = new Entry(s.Screen, s.Class);
            if (s.Top != s.Screen && _stack.Count > 0) _stack.Insert(_stack.Count - 1, entry);
            else _stack.Add(entry);
            return entry;
        }

        private void ApplyStack(UiStackEvent s, Outcome? o)
        {
            if (s.Action == "show_refused")
            {
                acc.ShowRefused++;
                return;
            }

            if (s.Action == "show")
            {
                var entry = Push(s);
                acc.Show(entry.Logical, s.Kind);
                o?.Shown.Add(entry.Logical);
                if (o is not null && Transitions.Contains(s.Screen)) o.Via ??= s.Screen;
                else if (o is not null) o.Target ??= entry.Logical;
            }
            else
            {
                int idx = _stack.FindLastIndex(x => x.Screen == s.Screen && x.Class == s.Class);
                if (idx < 0) idx = _stack.FindLastIndex(x => x.Screen == s.Screen);
                // Тот же экран, названный при показе иначе (см. Push), — снят верхний.
                if (idx < 0 && _stack.Count > s.Depth) idx = _stack.Count - 1;
                if (idx >= 0)
                {
                    if (o is not null && ReferenceEquals(_stack[idx], o.Source)) o.Hidden = true;
                    _stack.RemoveAt(idx);
                }
            }

            _top = s.Top;
            string actualTop = _stack.Count > 0 ? _stack[^1].Screen : "none";
            if (_stack.Count == s.Depth && actualTop == s.Top) return;
            acc.StackMismatches++;
            if (s.Depth == 0) _stack.Clear();
            // Глубина сошлась, имя верхнего — нет: тот же объект, зонд прочёл его имя позже иначе (см. Push).
            else if (_stack.Count == s.Depth && s.Action == "hide") _stack[^1].Screen = s.Top;
        }
    }
}
