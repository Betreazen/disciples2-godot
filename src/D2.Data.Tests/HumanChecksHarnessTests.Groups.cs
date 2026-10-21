using D2.Data.Corpus;
using D2.Data.Play;
using D2.Data.Ui.Windows;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Data.Tests;

/// <summary>Перенос юнитов (<c>swap_check</c>, <c>unit_transfer</c>) и ответы ИИ на предложения (<c>ai_answer</c>).</summary>
public partial class HumanChecksHarnessTests
{
    /// <summary>Начало перетаскивания: проверки <c>swap_check</c> одной клетки источника в один миг — по каждой позиции групп окна;
    /// <c>other</c> каждой строки — кто стоит в клетке цели. Группа, у которой записаны все шесть позиций, известна целиком.</summary>
    internal sealed record SwapCluster(Dictionary<string, string?[]> Occupants, Dictionary<string, bool[]> Logged);

    private static Dictionary<long, SwapCluster> SwapClusters(CheckJournal j)
    {
        var bySeq = new Dictionary<long, SwapCluster>();
        SwapCheckEvent? head = null;
        SwapCluster? current = null;
        foreach (var q in j.Events.OfType<SwapCheckEvent>())
        {
            if (current is null || head is null || head.T != q.T || !Same(head.From, q.From) || head.FromPos != q.FromPos)
            {
                head = q;
                current = new SwapCluster(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
            }
            if (!current.Occupants.TryGetValue(q.To, out var cells))
            {
                current.Occupants[q.To] = cells = new string?[UnitCells.CellCount];
                current.Logged[q.To] = new bool[UnitCells.CellCount];
            }
            if (q.ToPos is >= 0 and < UnitCells.CellCount)
            {
                cells[q.ToPos] = q.Other;
                current.Logged[q.To][q.ToPos] = true;
            }
            bySeq[q.Seq] = current;
        }
        return bySeq;
    }

    /// <summary>
    /// <c>swap_check</c> (<c>0x5df4fb</c>) — ответ порта на тот же вопрос, что рамка перетаскивания (<see cref="DropFrames.Unit"/>: чужая
    /// группа — <see cref="GameSession.SwapRefusal"/>, своя — перестановка внутри). Клетки групп — из строк того же начала перетаскивания:
    /// группа, записанная целиком, ставится как в журнале (порядок юнитов — как в сохранении); у записанной частично записанные клетки
    /// обязаны совпасть с сохранением; юниты клеток обязаны быть в сохранении.
    /// </summary>
    private static long? Swap(GameSession s, Context c, SwapCheckEvent q)
    {
        if (c.Cluster is not { } cluster) return null;
        s = Replayed(s, c, [.. cluster.Occupants.Keys, q.From]);
        foreach (var (group, occupants) in cluster.Occupants)
        {
            if (s.GroupCells(group) is not { } cells) return null;
            var logged = cluster.Logged[group];
            if (occupants.Any(id => id is not null && !s.Map.Units.ContainsKey(id))) return null;
            if (logged.All(x => x)) s.SetCells(group, CellsOf(cells, occupants));
            else if (Enumerable.Range(0, UnitCells.CellCount).Any(p => logged[p] && !Same(cells.Cells[p], occupants[p] ?? ""))) return null;
        }
        if (s.GroupCells(q.From) is not { } from || q.FromPos is < 0 or >= UnitCells.CellCount) return null;
        if (!Same(from.Cells[(int)q.FromPos], q.Unit ?? "")) return null;
        return HumanCheckCodes.Swap(s, q.From, (int)q.FromPos, q.To, (int)q.ToPos);
    }

    /// <summary>Клетки группы по журналу; порядок юнитов — как в сохранении, пришедшие — в конец.</summary>
    private static UnitCells CellsOf(UnitCells before, string?[] occupants)
    {
        string[] cells = [.. occupants.Select(o => o ?? "")];
        var present = cells.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var units = before.Units.Where(id => present.Contains(id, StringComparer.OrdinalIgnoreCase))
                          .Concat(present.Where(id => !before.Units.Contains(id, StringComparer.OrdinalIgnoreCase))).ToList();
        return new UnitCells(units, cells);
    }

    /// <summary>
    /// Перенос рукой человека (<c>CStackSwapUnitMsg</c> → <c>0x5d5642</c>, <c>unit_transfer</c> <c>swap_msg</c>): клетки до вызова обязаны
    /// совпасть с сохранением (<c>unit</c>, <c>other</c>); затем тот же перенос портом — внутри группы <see cref="GameSession.MoveInGroup"/>,
    /// между группами <see cref="GameSession.ExchangeUnits"/>. Код: 0 — перенесено и клетки после (<c>from_after</c>, <c>to_after</c>) как у
    /// движка, 1 — отказ, 2 — перенесено иначе.
    /// </summary>
    private static long? Transfer(GameSession s, Context c, UnitTransferEvent q)
    {
        if (q.To is null) return null;
        s = Replayed(s, c, q.From, q.To);
        if (s.GroupCells(q.From) is not { } from || s.GroupCells(q.To) is not { } to) return null;
        if (q.FromPos is < 0 or >= UnitCells.CellCount || q.ToPos is < 0 or >= UnitCells.CellCount) return null;
        if (!Same(from.Cells[(int)q.FromPos], q.Unit ?? "") || !Same(to.Cells[(int)q.ToPos], q.Other ?? "")) return null;
        string? refused = q.SameGroup ? s.MoveInGroup(q.From, (int)q.FromPos, (int)q.ToPos)
                                      : s.ExchangeUnits(q.From, (int)q.FromPos, q.To, (int)q.ToPos);
        if (refused is not null) return 1;
        bool same = Same(s.GroupCells(q.From)!.Cells[(int)q.FromPos], q.FromAfter ?? "") && Same(s.GroupCells(q.To)!.Cells[(int)q.ToPos], q.ToAfter ?? "");
        return same ? 0 : 2;
    }

    /// <summary>
    /// Ответ ИИ на предложение (<c>ai_answer</c>): союз — <see cref="GameSession.ProposeAllianceBy"/> (<c>0x474b37</c>, шанс <c>0x474c21</c>),
    /// сделка — <see cref="GameSession.ProposeTradeBy"/> (<c>0x474841</c>, шанс <c>0x4749c8</c>); генератор партии — R250 движка перед
    /// броском ответа (<see cref="JournalRng"/>, <c>rng_before</c> строки). Код — <c>шанс · 2 + согласие</c>; шанс союза — по отношению и
    /// доле равнин из сохранения (<see cref="AiDiplomacy.AnswerAllianceChance"/>), сделки — по доплате строки и цене порта
    /// (<see cref="AiDiplomacy.TradeChance"/>: заклинание <see cref="AiDiplomacy.SpellTradePrice"/>, предмет <see cref="Treasury.ItemValue"/>).
    /// </summary>
    private static long? Answer(GameSession s, Context c, AiAnswerEvent q)
    {
        if (q.Player is not { } ai || q.Proposer is not { } proposer || !s.Map.Players.ContainsKey(ai) || !s.Map.Players.ContainsKey(proposer))
            return null;
        var book = s.RequireBook();
        int RaceType(string p) => book.Economy.Races[s.Map.Players[p].RaceId].RaceType;
        s = WithBank(s, c, ai);
        bool spell = q.Kind != "alliance" && q.Object is { } o && book.Spells.ContainsKey(o);
        string? fort = q.Kind == "alliance" || spell || q.Object is not { } item ? null
            : c.Journal.Events.OfType<TradeAcceptEvent>().FirstOrDefault(t => t.Seq > q.Seq && Same(t.Object, item))?.Id2;
        if (fort is not null) s = WithItemInStore(s, fort, q.Object!);
        // Генератор движка восстановлен верно, только если его первый бросок — бросок ответа строки; иначе согласие — бросок журнала
        // против шанса порта (сверяется шанс).
        var rng = EngineRngAt(c.Journal, q);
        var game = rng is null ? null : new GameSession(s.State with { Random = new RandomStreams(rng.Index, rng.TableSnapshot(), 1) }, s.Art, s.Book);
        if (q.Kind == "alliance")
        {
            int rel = (int?)q.Rel ?? Diplomacy.Current(s.Map.Relations, RaceType(ai), RaceType(proposer));
            int chance = AiDiplomacy.AnswerAllianceChance(rel, AiDiplomacy.PlainLandShare(s.Map, RaceType(proposer)));
            bool? accepted = game is null ? q.Roll < chance : game.ProposeAllianceBy(proposer, ai);
            return accepted is { } accept ? HumanCheckCodes.Answer(chance, accept) : null;
        }
        if (q.Object is not { } thing || q.Gold is not { } gold) return null;
        long price = spell ? AiDiplomacy.SpellTradePrice((int)book.Spells[thing].BuyCost.Gold, book.Spells[thing].Level)
            : s.Map.ItemTypes.TryGetValue(thing, out var type) && book.Items.TryGetValue(type, out var def)
                ? Treasury.ItemValue(def, s.Map.TalismanCharges.GetValueOrDefault(thing)).Gold : -1;
        if (price < 0) return null;
        int tradeChance = AiDiplomacy.TradeChance((int)gold, (int)price);
        bool? ok = game is null ? q.Roll < tradeChance
            : game.ProposeTradeBy(proposer, ai, spell ? AiGiftKind.Spell : AiGiftKind.Item, thing, (int)gold, fort).Accepted;
        return ok is { } yes ? HumanCheckCodes.Answer(tradeChance, yes) : null;
    }

    /// <summary>Предмет сделки — в складе крепости <paramref name="fort"/>, как у движка в миг предложения (<c>trade_accept</c> пишет её
    /// <c>id2</c>). Между сохранением и предложением предметы ходят между отрядами и складом без строк журнала (обмен предметами журнал
    /// не пишет, <c>docs/ai-tactic-candidates.md</c> §25; human seq 15731044: <c>S073IM0076</c> в сохранении дня 64 не в складе
    /// <c>S073FT0005</c>, а движок продаёт его оттуда). Предмет снимается с отряда (сумка и снаряжение) и кладётся в склад; нет на карте —
    /// как было.</summary>
    private static GameSession WithItemInStore(GameSession s, string fort, string item)
    {
        var map = s.Map;
        if (!map.ItemTypes.ContainsKey(item) || map.Sites.FirstOrDefault(x => Same(x.Id, fort)) is not { } site
            || site.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
            return s;
        bool Has(IReadOnlyList<string> list) => list.Contains(item, StringComparer.OrdinalIgnoreCase);
        var stacks = map.Stacks.ToDictionary(p => p.Key, p => Has(p.Value.Items) || Has(p.Value.Equipment)
            ? p.Value with { Items = [.. p.Value.Items.Where(x => !Same(x, item))], Equipment = [.. p.Value.Equipment.Select(x => Same(x, item) ? "" : x)] }
            : p.Value, StringComparer.OrdinalIgnoreCase);
        var sites = map.Sites.Select(x => Same(x.Id, fort) ? x with { Items = [.. x.Items, item] }
            : Has(x.Items) ? x with { Items = [.. x.Items.Where(y => !Same(y, item))] } : x).ToList();
        return Rebuilt(s, GameSession.With(map, stacks: stacks, sites: sites));
    }

    /// <summary>R250 движка перед броском ответа <paramref name="q"/>: номер вызова броска — <c>rng_before + 1</c> (<c>rng_before</c> —
    /// счётчик до броска, а <see cref="JournalRng"/> считает, как строки <c>rng</c>, номер вызова; так же опоры дипломатии в
    /// <c>AiDiplomacyHarnessTests.Anchors</c>). Годен, только если его первый бросок — бросок строки; иначе null.</summary>
    internal static D2.Rules.Rng.EngineRng? EngineRngAt(CheckJournal j, AiAnswerEvent q) =>
        j.Rng.At(q.Seq, q.RngBefore + 1) is { } g && g.Clone().Next(100) == q.Roll ? g : null;
}

/// <summary>Ответы порта кодами движка: строки отказов <see cref="GameSession"/> → коды проверок зонда (<c>docs/corpus-schema.md</c>,
/// разбор кодов — <c>docs/ai-spells.md</c> §3, <see cref="Rods"/>, <see cref="Fortifications"/>). −1 — отказ, которому кода движка нет.</summary>
internal static class HumanCheckCodes
{
    public static long Answer(long chance, bool? accept) => (chance * 2) + (accept == true ? 1 : 0);

    public static long Transfer(bool ret) => ret ? 0 : 1;

    /// <summary><c>hire_check</c> (<c>0x5deea7</c>): 7 — казна меньше <c>ENROLL_C</c>, 8 — нет постройки (вне списка найма), 10 — большому нет
    /// двух мест, 11 — место отвергнуто.</summary>
    public static long Hire(string? refused) => refused switch
    {
        null => 0,
        "не хватает казны" => 7,
        _ when refused.Contains("списке найма", StringComparison.Ordinal) => 8,
        "в гарнизоне нет места" => 10,
        _ when refused.StartsWith("клетка ", StringComparison.Ordinal) => 11,
        _ => -1,
    };

    /// <summary><c>hire_leader_check</c> (<c>0x5de921</c>): 1 — казна, 3 — нет в списке найма.</summary>
    public static long HireLeader(string? refused) => refused switch
    {
        null => 0,
        "не хватает казны" => 1,
        _ when refused.Contains("списке лидеров", StringComparison.Ordinal) => 3,
        _ => -1,
    };

    /// <summary><c>rod_check</c> (<c>0x5d9e59</c>): 9 — жезл в 5×5, 7 — грунт, объект в клетке или укрепление в 3×3, 8 — земля
    /// союзника, 5 — казна.</summary>
    public static long Rod(string? refused) => refused switch
    {
        null => 0,
        _ when refused.StartsWith("жезл ", StringComparison.Ordinal) => 9,
        _ when refused.Contains("грунт", StringComparison.Ordinal) || refused.Contains("стоит объект", StringComparison.Ordinal)
               || refused.StartsWith("укрепление", StringComparison.Ordinal) => 7,
        _ when refused.Contains("земля союзника", StringComparison.Ordinal) => 8,
        _ when refused.StartsWith("казны", StringComparison.Ordinal) => 5,
        _ => -1,
    };

    /// <summary>Посох и свиток (<c>wand_check</c> <c>0x5dbdd2</c>) и цель заклинания (<c>0x5dab48</c>, <c>0x5db2cb</c>, <c>0x5dac42</c>).</summary>
    public static long Spell(string? refused) => refused switch
    {
        null => 0,
        "герой не применяет посохи и свитки" => 0xE,
        "предмета нет в отряде" or "у предмета нет заклинания" => 1,
        GameSession.ItemCastNoMana => 9,
        GameSession.ItemCastWandUsed => 0x11,
        GameSession.ItemCastWandUsedTwice => 0x12,
        "клетка вне карты" => 7,
        "клетка под туманом" or "клетка не под туманом" => 8,
        "в центре площади крепость" or "сюда нельзя призвать" or "свой отряд" or "чужой отряд" or "отряд в крепости" or "лидер — иллюзия" => 2,
        "все иммунны" => 3,
        "уже наложено" => 4,
        "лечить некого" or "запас хода полон" or "земля здесь не меняется" or "брони нет" => 5,
        "нет цели" => 6,
        "союзник" => 15,
        "свой невидимый отряд" => 16,
        _ => -1,
    };

    /// <summary><c>swap_check</c> (<c>0x5df4fb</c>): 0 — рамка порта есть; 1 — группы нет или исходная клетка пуста; уходящий в другую
    /// группу лидер — 3, страж — 2; прочий отказ обмена (<c>CVisitorSwapUnitPosition::canApply</c>) — 0xB. Лидер на месте цели сам
    /// <c>0x5df4fb</c> не трогает: в гарнизон его не пускает <c>canApply</c> (0xB — 21 строка корпуса, гарнизон → лидер отряда), в
    /// другой отряд — пускает (0; рамку гасит <c>0x56e839</c>): <c>20261016g-port-synth</c> seq 886039, 895255, окно обмена — воин на
    /// место лидера соседнего отряда.</summary>
    public static long? Swap(GameSession s, string from, int fromPos, string to, int toPos)
    {
        if (DropFrames.Unit(s, from, fromPos, to, toPos) == DropFrame.Valid) return 0;
        if (s.GroupCells(from) is not { } cells || cells.Cells[fromPos].Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return 1;
        string unit = cells.Cells[fromPos];
        if (s.SwapRefusal(from, fromPos, to, toPos) is { } why && why.StartsWith(unit + " не покидает", StringComparison.OrdinalIgnoreCase))
            return s.Map.Stacks.TryGetValue(from, out var stack) && string.Equals(stack.LeaderId, unit, StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        return LeaderToOtherStack(s, from, fromPos, to, toPos) switch { true => 0, false => 0xB, null => null };
    }

    /// <summary>Обмен двух отрядов на карте (гарнизон в <c>Map.Stacks</c> не лежит), где отказ порта — только лидер цели, уходящий в другой
    /// отряд (уходящий лидер источника — код 3 выше): клетки годятся, заполнение — в пределах вместимости. Павший лидер любого из двух —
    /// null: отказ сервера <c>CStackSwapUnitMsg</c>, ответ <c>0x5df4fb</c> на него журналом не сверен.</summary>
    private static bool? LeaderToOtherStack(GameSession s, string from, int fromPos, string to, int toPos)
    {
        if (!s.Map.Stacks.TryGetValue(from, out var a) || !s.Map.Stacks.TryGetValue(to, out var b)) return false;
        if (s.GroupCells(from) is not { } ca || s.GroupCells(to) is not { } cb || !string.Equals(cb.Cells[toPos], b.LeaderId, StringComparison.OrdinalIgnoreCase)
            || GroupArrange.Swap(ca, fromPos, cb, toPos) is not { } swap || swap.Came.Count != 1) return false;
        bool alive = new[] { a.LeaderId, b.LeaderId }.All(id => s.Map.Units.TryGetValue(id, out var u) && u.Hp > 0);
        if (!alive) return null;
        return swap.From.Occupied <= Math.Max(s.GroupCapacity(from), ca.Occupied)
               && swap.To.Occupied <= Math.Max(s.GroupCapacity(to), cb.Occupied);
    }
}
