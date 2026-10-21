using System.Text.Json;
using D2.Data;
using D2.Data.Map;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>
/// Клетка жезла ИИ (<see cref="AiTacticScore.RodCell"/>, <c>0x46164d</c>) против живых постановок всех сессий
/// <see cref="AiCorpus"/> (зонд 6.5+; прежние прогоны 16.09/17.09 потеряны). Для каждого <c>rod_drop</c> берётся
/// последнее сохранение сессии того же дня до него, позиция ставившего отряда в нём и единственный кристалл, соседний с
/// клеткой жезла; правило выбирает соседа кристалла, ближайшего к отряду. Жезл ставится, когда отряд стоит в
/// выбранной точке (<c>0x46180c</c>: слот 11 «в точке?» → <c>CAiPlantRodAction</c>, иначе <c>CAiMoveStackAction</c>).
/// <para><b>Проверки клетки — приближение.</b> <c>0x5d9bdb</c> — жезл в 5×5 (жезлы сохранения плюс поставленные в
/// этот день до строки); <c>0x5d9d1d</c> — земля союзника (<see cref="Diplomacy.AllyTerrain"/>);
/// <c>0x5cd11f</c> — клетка и соседи без чужого отряда и крепости; к нему добавлена клиентская калитка
/// <see cref="Rods"/>: вне карты, не равнина, объект в клетке кроме области и самого отряда. Отряды других игроков
/// стоят там, где их застало сохранение.</para>
/// <para>Без соседей в <c>0x5cd11f</c> сходилось 32 из 36: оба расхождения (по два в каждом прогоне) — клетка
/// правила рядом с нейтральным отрядом (<c>S143KC0065</c> у (30,55), <c>S143KC0019</c> у (12,50)), движок взял
/// следующую по порядку клетку на том же расстоянии.</para>
/// <para>Сессии и сохранения — из данных (<see cref="AiCorpus"/>), не из констант <c>seq</c>. Жезлы человека (игрок без
/// <c>ai_turn</c> с <c>cause = ai</c> в сессии) не сверяются — правило ИИ к ним не относится.</para>
/// <para><b>Зонд 6.5+.</b> Перед постановкой ИИ зонд пишет сам вызов <c>ai_rod_cell</c> (<c>0x46164d</c>) с ответом;
/// правило порта против каждого такого вызова сверяет <see cref="AiTacticProbeHarnessTests"/>
/// (<c>RodCell_MatchesEveryProbeCall</c>). Здесь тогда сверяется звено «жезл лёг туда, куда указал последний вызов для
/// этого отряда», а приближение по сохранению лишь печатается: на корпусе 18.09 оно расходится 5 раз из 77 — отряды и
/// чужие отряды в сохранении стоят не там, где в момент вызова, а равные по расстоянию клетки приближение не
/// различает.</para>
/// </summary>
[Trait("Category", "Corpus")]
public class AiTacticHarnessTests(ITestOutputHelper output)
{
    private sealed record Drop(long Seq, int Turn, string Player, string Stack, int X, int Y);
    private sealed record Cell(long Seq, string Stack, int X, int Y);

    [SkippableFact] public void RodCell_MatchesAiRodDrops()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.If(AiCorpus.Sessions.Count == 0, $"нет журналов зонда 6.5+ в {CorpusFiles.Dir}");
        var econ = EconomyLoader.Load(GamePaths.Globals);

        int ok = 0, bad = 0, skipped = 0, approxOk = 0, approxBad = 0, human = 0;
        var files = new Dictionary<string, MapState>(StringComparer.Ordinal);
        var lines = new JournalLines(RodWanted);
        foreach (var session in AiCorpus.Sessions)
        {
            var (drops, cells, ai) = Read(session, lines);
            output.WriteLine($"{session.Name}: rod_drop {drops.Count}, ai_rod_cell {cells.Count}");
            foreach (var d in drops)
            {
                if (!ai.Contains(d.Player)) { human++; continue; }
                var sameDay = drops.Where(o => o.Turn == d.Turn && o.Seq < d.Seq).Select(o => (o.X, o.Y)).ToList();
                var file = session.SaveBefore(d.Seq, d.Turn)?.File;
                var map = file is null ? null : files.TryGetValue(file, out var m) ? m : files[file] = SaveMaps.Map(file);
                string? approx = Check(d, map, sameDay, econ, out string line);
                var call = cells.LastOrDefault(c => c.Seq < d.Seq && c.Stack.Equals(d.Stack, StringComparison.OrdinalIgnoreCase));
                string? verdict = call is null ? approx : (call.X, call.Y) == (d.X, d.Y) ? null : "клетка не та, что у вызова";
                if (call is not null)
                {
                    line += $"; вызов seq {call.Seq} → ({call.X},{call.Y}); приближение {(approx is null ? "сошлось" : approx == "" ? "пропуск" : "разошлось")}";
                    if (approx is null) approxOk++;
                    else if (approx != "") approxBad++;
                }
                output.WriteLine($"  {(verdict is null ? "ok " : verdict == "" ? "?? " : "BAD")} {line}");
                if (verdict is null) ok++;
                else if (verdict == "") skipped++;
                else bad++;
            }
        }
        output.WriteLine($"rod_cell: сошлось {ok}, разошлось {bad}, пропущено {skipped}, жезлов человека {human}; " +
                         $"приближение по сохранению там, где есть вызов: сошлось {approxOk}, разошлось {approxBad}");
        Assert.Equal(0, bad);
        Assert.True(ok > 0, "ни одной сверки");
    }

    /// <summary><c>null</c> — сошлось; пусто — пропуск; иначе причина расхождения.</summary>
    private static string? Check(Drop d, MapState? map, IReadOnlyList<(int X, int Y)> sameDay, EconomyRules econ,
                                 out string line)
    {
        line = $"t{d.Turn:D2} seq {d.Seq} {d.Stack} → ({d.X},{d.Y})";
        if (map is null) { line += ": нет сохранения дня"; return ""; }
        if (!map.Stacks.TryGetValue(d.Stack, out var stack)) { line += ": отряда нет в сохранении"; return ""; }
        var crystals = map.Objects.Values
            .Where(o => o.Kind.Equals("CR", StringComparison.OrdinalIgnoreCase) && Math.Max(Math.Abs(o.X - d.X), Math.Abs(o.Y - d.Y)) == 1).ToList();
        if (crystals.Count != 1) { line += $": соседних кристаллов {crystals.Count}"; return ""; }
        var cr = crystals[0];

        var rods = map.Rods.Values.Select(r => (r.X, r.Y)).Concat(sameDay).ToList();
        bool RodNear(int x, int y) => rods.Any(r => Math.Abs(r.X - x) <= Rods.RodKeepOut && Math.Abs(r.Y - y) <= Rods.RodKeepOut);
        var ally = AllyLand(map, econ, d.Player);
        bool Ally(int x, int y) => map.Contains(x, y) && ally(map.At(x, y).Terrain);
        bool Blocked(int x, int y) => (x, y) == (stack.X, stack.Y) || Refusal(map, x, y, stack) is not null;

        var chosen = AiTacticScore.RodCell(cr.X, cr.Y, stack.X, stack.Y, RodNear, Ally, Blocked);
        line += $" от ({stack.X},{stack.Y}), кристалл {cr.Id} ({cr.X},{cr.Y}), правило {chosen}";
        if (chosen == (d.X, d.Y)) return null;
        string why = Refusal(map, d.X, d.Y, stack) ?? (RodNear(d.X, d.Y) ? "жезл рядом" : Ally(d.X, d.Y) ? "земля союзника" : "годна");
        line += $"; клетка журнала по приближению: {why}, d = {Cheb(stack.X, stack.Y, d.X, d.Y)} против " +
                (chosen is { } c ? Cheb(stack.X, stack.Y, c.X, c.Y).ToString() : "—");
        return "расхождение";
    }

    /// <summary>Клиентская калитка жезла без жезла, казны и способности плюс <c>0x5cd11f</c>: клетка и восемь её
    /// соседей (таблица <c>0x7b0718</c>) без чужого отряда (<c>0x5cd301</c>) и крепости (тип 0x1d). Кого
    /// <c>0x5cd301</c> считает чужим, не разобрано — здесь любой отряд другого игрока.</summary>
    private static string? Refusal(MapState map, int x, int y, MapStack own)
    {
        string ownStack = own.Id;
        if (!map.Contains(x, y)) return "вне карты";
        if (map.At(x, y).Ground != Ground.Plain) return $"грунт {map.At(x, y).Ground}";
        if (map.Plan.TryGetValue((x, y), out var ids) &&
            ids.FirstOrDefault(id => Kind(id) != "LO" && !id.Equals(ownStack, StringComparison.OrdinalIgnoreCase)) is { } here)
            return $"объект {here}";
        for (int dx = -Rods.FortKeepOut; dx <= Rods.FortKeepOut; dx++)
            for (int dy = -Rods.FortKeepOut; dy <= Rods.FortKeepOut; dy++)
            {
                if (!map.Plan.TryGetValue((x + dx, y + dy), out var near)) continue;
                if (near.Any(id => Kind(id) == "FT")) return "укрепление в 3×3";
                if (near.FirstOrDefault(id => map.Stacks.TryGetValue(id, out var s) &&
                                              !s.Owner.Equals(own.Owner, StringComparison.OrdinalIgnoreCase)) is { } foe)
                    return $"чужой отряд {foe} в 3×3";
            }
        return null;
    }

    private static Func<int, bool> AllyLand(MapState map, EconomyRules econ, string playerId)
    {
        if (!map.Players.TryGetValue(playerId, out var p) || !econ.Races.TryGetValue(p.RaceId, out var race))
            return _ => false;
        return Diplomacy.AllyTerrain(map.Relations, Diplomacy.PlayerRaces(map, econ), race.RaceType);
    }

    private static readonly string[] RodWanted = ["\"ev\":\"save\"", "\"ev\":\"rod_drop\"", "\"ev\":\"ai_rod_cell\"", "\"ev\":\"ai_turn\""];

    /// <summary><c>rod_drop</c> сессии с днём последней строки <c>save</c> перед ним, удачные <c>ai_rod_cell</c> и игроки
    /// с ходом ИИ. Строки журнала — <paramref name="lines"/> (<see cref="JournalLines"/> по <see cref="RodWanted"/>: журнал читается
    /// один раз на все свои сессии).</summary>
    private static (List<Drop> Drops, List<Cell> Cells, HashSet<string> Ai) Read(AiSession session, JournalLines lines)
    {
        var drops = new List<Drop>();
        var cells = new List<Cell>();
        var ai = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int turn = 0;
        foreach (var (k, seq, l) in lines.Of(session))
        {
            using var doc = JsonDocument.Parse(l);
            var e = doc.RootElement;
            string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            switch (k)
            {
                case 0: turn = e.GetProperty("turn").GetInt32(); break;
                case 1:
                    drops.Add(new Drop(seq, turn, S("player")!, S("stack")!, e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32()));
                    break;
                case 2 when e.GetProperty("ret").GetBoolean() && e.GetProperty("result") is { ValueKind: JsonValueKind.Object } r:
                    cells.Add(new Cell(seq, S("stack") ?? "", r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32()));
                    break;
                case 3 when S("player") is { } p && S("cause") == "ai": ai.Add(p); break;
            }
        }
        drops.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        cells.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return (drops, cells, ai);
    }

    private static int Cheb(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    private static string Kind(string id) => id.Length >= 6 ? id.Substring(4, 2).ToUpperInvariant() : "";
}
