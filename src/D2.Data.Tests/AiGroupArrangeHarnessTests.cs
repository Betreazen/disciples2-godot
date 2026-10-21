using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Data.Tests;

/// <summary>
/// Расстановка групп ИИ (<c>docs/ai-items.md</c> §6, <see cref="AiStackUnits.Arrange"/>) против сохранений: группа, чей
/// состав за день не менялся, в сохранении следующего дня стоит так, как её расставляет порт. Гарнизоны — укрепления ИИ без
/// отряда-гостя в оба дня (начало стратегии <c>0x44edee</c>); отряды — отряды ИИ вне укреплений в оба дня (подготовка
/// <c>0x451fc4</c>). Здоровье и опыт берутся из сохранения следующего дня.
/// <para>24.09: гарнизоны 66/67 и 102/105, отряды 520/540 и 656/665. Остаток — порядок бойцов одного ряда: сила в момент
/// расстановки другая, чем в сохранении (раны и гибель в боях того же дня: у лидера в сохранении сила 0).</para>
/// <para>20.10 (r20/last, И33 — «не баг»): остаток — артефакт этой сверки, а не правило. Посуточные партии (карта 23, переигровки 24–27,
/// карты 5, 21, 22) проверяют то же в миг хода: порт играет день с сохранения движка, и клетки отрядов и гарнизонов ИИ, порядок сумок и
/// складов наутро — как у движка во всех днях (<c>DailySaveAiTurnsHarnessTests</c>, <c>Long_AiTurnsMatchTheOriginalJournal</c>: 0), а здесь
/// по той же карте 23 «не сходятся» 28 групп из 1527. Сила из сохранения этого дня вместо следующего — ещё хуже (карта 23: гарнизоны
/// 523/558, отряды 922/969: нет дневного чита, лечения начала хода и боёв до окна).</para>
/// </summary>
public abstract class AiGroupArrangeHarnessTests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [SkippableTheory]
    [CorpusRow("20260919-human", 67, 66, 540, 520)]
    [CorpusRow("20260920-legions", 105, 102, 665, 656)]
    // 19.10 (r19b/cov): партия с покрытием 20261019c-cov (Greenskins, человек, дни 1–18, зонд 7.5) — гарнизоны 51 из 52, отряды 111 из 122: равные по
    // месту юниты (S092KC0023 00bc/00bd) — тот же остаток, что у human (520 из 540).
    [CorpusRow("20261019c-cov", 52, 51, 122, 111)]
    // 20.10 (r20/batB): карта 23 (20261020-port-synth, 60 дней) — гарнизоны 556 из 558, отряды 943 из 969: юниты равной силы (в том числе
    // павшие, сила 0) в соседних клетках у порта переставлены наоборот — тот же остаток, что у human и cov; сохранение следующего дня — после
    // боёв дня.
    [CorpusRow("20261020-port-synth", 558, 556, 969, 943)]
    public void Arrangement_MatchesNextDaySave(string corpus, int wantForts, int wantFortsSame, int wantStacks, int wantStacksSame)
    {
        string dir = Path.Combine(GameSaveTests.CorpusDir(), corpus);
        Skip.IfNot(Directory.Exists(dir), "нет корпуса");
        var content = SharedContent.Load(Data);
        var book = GameBook.Load(Path.Combine(Data, "rules"));
        var saves = Directory.GetFiles(dir, "*.sg").Order(StringComparer.Ordinal)
            .GroupBy(f => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"-t(\d+)-").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key).Select(g => (Day: g.Key, File: g.Last())).ToList();
        int forts = 0, fortsSame = 0, stacks = 0, stacksSame = 0, shown = 0;
        for (int i = 0; i + 1 < saves.Count; i++)
        {
            if (saves[i + 1].Day != saves[i].Day + 1) continue;
            var before = MapLooksLoader.Load(saves[i].File).Map;
            var looks = MapLooksLoader.Load(saves[i + 1].File);
            var after = looks.Map;
            var human = after.Players.Values.First(p => p.IsHuman);
            var session = new GameSession(new GameState(new GameInfo("", "", [human.Id], 1, 1), looks), content.MapArt, book);
            foreach (var fort in after.Sites.Where(s => s.Kind is SiteKind.Capital or SiteKind.Village && s.StackId.Length == 0))
            {
                var was = before.Sites.FirstOrDefault(s => Same(s.Id, fort.Id));
                if (was is null || was.StackId.Length > 0 || !Same(was.Owner, fort.Owner) || !Ai(session, fort.Owner)
                    || fort.Guards.Count < 2 || !fort.Guards.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(was.Guards)) continue;
                forts++;
                if (Check(session, fort.Id, fort.GuardSlots)) fortsSame++;
                else if (shown++ < 20) Show(session, fort.Id, fort.GuardSlots);
            }
            foreach (var stack in after.Stacks.Values.Where(s => s.Inside.Length == 0 && s.UnitIds.Count >= 2 && Ai(session, s.Owner)))
            {
                if (!before.Stacks.TryGetValue(stack.Id, out var was) || was.Inside.Length > 0
                    || !stack.UnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(was.UnitIds)) continue;
                stacks++;
                if (Check(session, stack.Id, stack.Slots)) stacksSame++;
                else if (shown++ < 20) Show(session, stack.Id, stack.Slots);
            }
        }
        output.WriteLine($"{corpus}: гарнизонов {forts}, сошлось {fortsSame}; отрядов {stacks}, сошлось {stacksSame}");
        Assert.Equal(wantForts, forts);
        Assert.Equal(wantFortsSame, fortsSame);
        Assert.Equal(wantStacks, stacks);
        Assert.Equal(wantStacksSame, stacksSame);
    }

    private static bool Check(GameSession session, string groupId, IReadOnlyList<int> slots)
    {
        var group = session.GroupCells(groupId)!;
        var cells = GroupArrange.Layout(group.Units.Select(id => AiStackUnits.Describe(session, id)), center: true);
        return (group with { Cells = cells }).Slots().SequenceEqual(slots);
    }

    private void Show(GameSession session, string groupId, IReadOnlyList<int> slots)
    {
        var group = session.GroupCells(groupId)!;
        var cells = GroupArrange.Layout(group.Units.Select(id => AiStackUnits.Describe(session, id)), center: true);
        output.WriteLine($"{groupId}: сохранение [{string.Join(",", group.Cells.Select(c => c.Length == 0 ? "-" : c[^4..]))}] " +
                         $"порт [{string.Join(",", cells.Select(c => c.Length == 0 ? "-" : c[^4..]))}]; " +
                         string.Join(" ", group.Units.Select(id => AiStackUnits.Describe(session, id)).Select(u => $"{u.Id[^4..]}:{u.Line}/{u.Score:0.#}")));
    }

    private static bool Ai(GameSession session, string playerId) =>
        session.Map.Players.TryGetValue(playerId, out var p) && !p.IsHuman && !AiTactics.NeutralOwner(session, playerId);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Партии харнесса — отдельными классами, чтобы шли параллельно (<see cref="CorpusRowAttribute"/>).</summary>
[OnlyCorpus("20260919-human")]
[Trait("Category", "Corpus")]
public sealed class AiGroupArrangeHarnessTests_Human(ITestOutputHelper output) : AiGroupArrangeHarnessTests(output);

[OnlyCorpus("20260920-legions")]
[Trait("Category", "Corpus")]
public sealed class AiGroupArrangeHarnessTests_Legions(ITestOutputHelper output) : AiGroupArrangeHarnessTests(output);

[OnlyCorpus("20261019c-cov")]
[Trait("Category", "Corpus")]
public sealed class AiGroupArrangeHarnessTests_Cov(ITestOutputHelper output) : AiGroupArrangeHarnessTests(output);

[OnlyCorpus("20261020-port-synth")]
[Trait("Category", "Synth")]
public sealed class AiGroupArrangeHarnessTests_PortSynth23(ITestOutputHelper output) : AiGroupArrangeHarnessTests(output);
