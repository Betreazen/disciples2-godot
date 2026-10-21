using System.Text.Json;
using D2.Rules.Battle;
using D2.Rules.Rng;
using Xunit.Abstractions;

/// <summary>Харнесс снятия призыва очередью (<c>CBatAttackUnsummonEffectQueued</c>, <see cref="BattleSim.ClearFallenSummons"/>) по корпусу.
///
/// <para>Движок в начале обработки каждого хода (<c>0x627ffa</c> → <c>0x628a84</c>, после снимка <c>turn_before</c>) изгоняет живого
/// призванного, если его призыватель пал, ушёл с поля или его нет в бою. Харнесс берёт каждый <c>turn_before</c>, где есть живой призванный
/// с известным призывателем (тот, после чьего хода призванный впервые появился в снимке), строит по снимку состояние, зовёт модель и
/// сверяет её ответ со следующим снимком другого хода или <c>turn_after</c> этого: изгнанного в нём нет, оставленный есть. Если следующий —
/// <c>battle_end</c>, сверяется только изгнание: бой кончился без единого удара (изгнали последнего на стороне); оставленных конец боя
/// снимает сам (<c>0x62953c</c>), их там не сверить.</para>
///
/// <para>Журналы читаются построчно, разбираются только снимки боя — большие журналы целиком здесь не нужны.</para></summary>
[Trait("Category", "Corpus")]
public class SummonUnsummonHarnessTests(ITestOutputHelper output)
{
    private const long SummonBit = 1L << 27, RetreatedBit = 1L << 28, RetreatingBit = 1L << 29;
    private static readonly AttackType Strike = new("g000aa0019", 50, AttackClass.Damage, 100, 2, 10, 0, false, 1);
    private static readonly UnitType Plain = new("g000uu0019", 1, 100, 0, Strike.Id, "g000000000", false, true, 10, 100);
    private static readonly RulesConstants Constants = new(BatInit: 10, BatDamage: 6);

    private sealed record Snap(string Id, long Hp, long St);

    /// <summary>Ответ модели на снимок <c>turn_before</c>: живые призванные в нём и те, кого модель изгоняет.</summary>
    private sealed record Pending(string Where, string Unit, IReadOnlyList<string> Alive, IReadOnlySet<string> Predicted);

    private sealed class Tally
    {
        public int Kept, Unsummoned, UnknownSummoner, EndNotChecked;
        public List<string> Bad { get; } = [];
        public List<string> Hits { get; } = [];
    }

    [SkippableFact] public void EverySummonerLoss_UnsummonsAtTurnStart()
    {
        Skip.IfNot(CorpusFiles.Available, "нет корпуса");
        var (files, _) = JournalSet.Distinct(CorpusFiles.Journals());
        var tally = new Tally();
        foreach (var file in files) Scan(file, tally);

        output.WriteLine($"журналов {files.Count}: оставлен {tally.Kept}, изгнан {tally.Unsummoned}, призыватель не известен " +
                         $"{tally.UnknownSummoner}, конец боя без сверки оставленных {tally.EndNotChecked}, расхождений {tally.Bad.Count}");
        foreach (string line in tally.Hits) output.WriteLine("  изгнание: " + line);
        foreach (string line in tally.Bad.Take(15)) output.WriteLine("  " + line);
        Assert.Empty(tally.Bad);
        Assert.True(tally.Unsummoned >= 2, $"изгнаний по корпусу {tally.Unsummoned} — ждали не меньше двух (20261014-replay seq 245972, 2050988)");
        Assert.True(tally.Kept > 0, "ни одного оставленного призванного — сверять нечего");
    }

    private static void Scan(string file, Tally tally)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;
        var summoner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Pending? pending = null;
        bool struck = false;
        foreach (string line in File.ReadLines(file))
        {
            string? ev = Ev(line);
            if (ev is "dmg" or "miss" or "attack_hit") { struck = true; continue; }
            if (ev is not ("battle_end" or "turn_before" or "turn_after")) continue;
            // Разбирать стоит только снимок с призванным или следующий за ожидающим сверки — прочие ничего не меняют.
            if (ev != "battle_end" && pending is null && !HasSummoned(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (ev == "battle_end")
            {
                if (pending is { Predicted.Count: > 0 } p)
                {
                    if (struck) tally.Bad.Add($"{p.Where}: модель изгоняет {string.Join(",", p.Predicted)}, а до конца боя был удар");
                    else { tally.Unsummoned += p.Predicted.Count; tally.Hits.Add($"{p.Where}: {string.Join(",", p.Predicted)} — бой кончен без удара"); }
                }
                else if (pending is not null) tally.EndNotChecked++;
                pending = null;
                summoner.Clear();
                continue;
            }
            var units = Units(e);
            string unit = e.GetProperty("unit").GetString()!;
            bool after = ev == "turn_after";
            // Второй turn_before того же хода (ход человека) — снимок до обработки хода, сверять по нему рано.
            if (pending is { } p2 && (after || !Same(p2.Unit, unit))) Compare(p2, units, tally);
            pending = after ? null : Predict($"{name} seq {e.GetProperty("seq").GetInt64()}", unit, units, summoner, tally);
            struck = false;
            if (!after) continue;
            // Призыватель — ходивший, после чьего хода призванный впервые в снимке.
            foreach (var u in units.Where(u => (u.St & SummonBit) != 0)) summoner.TryAdd(u.Id, unit);
        }
    }

    private static void Compare(Pending p, IReadOnlyList<Snap> next, Tally tally)
    {
        var present = next.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string s in p.Alive)
        {
            bool gone = !present.Contains(s), predicted = p.Predicted.Contains(s);
            if (gone != predicted)
                tally.Bad.Add($"{p.Where}: {s} — модель {(predicted ? "изгоняет" : "оставляет")}, движок {(gone ? "изгнал" : "оставил")}");
            else if (gone) { tally.Unsummoned++; tally.Hits.Add($"{p.Where}: {s} изгнан"); }
            else tally.Kept++;
        }
    }

    /// <summary>Модель на снимке: состояние из <c>units</c> (HP, ушёл, уходит, призван, призыватель) и начало хода ходящего
    /// (<see cref="BattleSim.BeginTurn"/>: <see cref="BattleSim.ClearFallenSummons"/>, а у призванного беглеца — изгнание вместо ухода,
    /// <c>0x628fb4</c>; первые случаи — <c>20261016e-av</c>, карта 8, seq 15960701 и ещё 6). Сторон снимок не знает, поэтому все юниты
    /// снимка — одна сторона, а против неё — живой враг вне снимка: бой по ходу не кончается, и начало хода доходит до бегства.</summary>
    private static Pending? Predict(string where, string unit, IReadOnlyList<Snap> units, Dictionary<string, string> summoner, Tally tally)
    {
        var alive = units.Where(u => (u.St & SummonBit) != 0 && u.Hp > 0).Select(u => u.Id).ToList();
        if (alive.Count == 0) return null;
        if (alive.Any(s => !summoner.ContainsKey(s))) { tally.UnknownSummoner++; return null; }
        var state = new BattleState
        {
            AttackerGroup = "A", DefenderGroup = "D",
            Units = [.. units.Select(u => new BattleUnit
            {
                Id = u.Id, Type = Plain, Attack = Strike, Attacker = false, Slot = 0, HpMax = Math.Max(1, (int)u.Hp), Hp = (int)u.Hp,
                Retreated = (u.St & RetreatedBit) != 0, Retreating = (u.St & RetreatingBit) != 0, Summoned = (u.St & SummonBit) != 0,
                SummonerId = (u.St & SummonBit) != 0 ? summoner[u.Id] : null,
            }), new BattleUnit { Id = "enemy", Type = Plain, Attack = Strike, Attacker = true, Slot = 0, HpMax = 100, Hp = 100 }],
        };
        var sim = new BattleSim(state, new ScriptedRng([]), Constants);
        if (state.Units.FirstOrDefault(u => Same(u.Id, unit)) is { } turning) sim.BeginTurn(turning);
        else sim.ClearFallenSummons();
        var predicted = alive.Where(s => !state.Units.Any(u => Same(u.Id, s))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Pending(where, unit, alive, predicted);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<Snap> Units(JsonElement e) =>
        [.. e.GetProperty("units").EnumerateArray().Select(u => new Snap(u.GetProperty("id").GetString()!, u.GetProperty("hp").GetInt64(),
                                                                         u.GetProperty("st").GetInt64()))];

    /// <summary>Есть ли в строке снимка юнит с битом 27 в <c>st</c> — без разбора JSON.</summary>
    private static bool HasSummoned(string line)
    {
        const string key = "\"st\":";
        for (int at = line.IndexOf(key, StringComparison.Ordinal); at >= 0; at = line.IndexOf(key, at + key.Length, StringComparison.Ordinal))
        {
            long st = 0;
            for (int i = at + key.Length; i < line.Length && char.IsAsciiDigit(line[i]); i++) st = st * 10 + (line[i] - '0');
            if ((st & SummonBit) != 0) return true;
        }
        return false;
    }

    private static string? Ev(string line)
    {
        const string key = "\"ev\":\"";
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line[start..end];
    }
}
