using D2.Data;
using D2.Data.Corpus;
using D2.Rules.Map;
using Xunit.Abstractions;

namespace D2.Rules.Tests;

/// <summary>Вид дипломатического действия, восстановленного из журнала.</summary>
public enum DiploKind
{
    Attack,
    Noble,
    Tribute,
    AllianceMake,
    AllianceBreak,
    AllianceRefuse,
    Trade,
}

/// <summary>Одно дипломатическое действие на оси <c>seq</c>: кто, по кому и с каким контекстом.
/// Величину действие не несёт — её считает <see cref="Diplomacy"/>, иначе сверка стала бы
/// тавтологией.</summary>
public sealed record DiploAction(long Seq, DiploKind Kind, int RaceA, int RaceB)
{
    /// <summary>Вид цели боя: укрепление отличается от отряда величиной (<c>D_ATTAK_FO</c>).</summary>
    public BattleTarget Target { get; init; } = BattleTarget.Stack;

    /// <summary>Вид цели вора: руина и лавки отношений не двигают.</summary>
    public ThiefTargetKind ThiefKind { get; init; } = ThiefTargetKind.Stack;

    /// <summary>Золото дани; у обмена — доплата покупателя.</summary>
    public long Gold { get; init; }

    /// <summary>Цена предмета или заклинания в обмене (<c>ai_answer.price</c>).</summary>
    public long Price { get; init; }

    /// <summary>Ход заключения или разрыва союза.</summary>
    public int Turn { get; init; }

    /// <summary>Отказ от союза между двумя людьми — мутатор не зовётся (<c>0x5d98e2</c>).</summary>
    public bool BothHuman { get; init; }
}

/// <summary>
/// Харнесс задачи 13 Ф7: дипломатия против корпуса. Два независимых водителя — <b>журнал</b>
/// (события <c>relation</c> схемы 5.2, шесть вызовов мутатора) и <b>сохранения</b> (слово
/// <c>RELATION</c> раз в ход, куда события 5.2 не писались вовсе — прогоны до 11.09).
///
/// <list type="bullet">
/// <item><c>relation_main</c> — наш мутатор над <c>before</c> даёт <c>after</c> основной пары;</item>
/// <item><c>relation_third</c> — наш список изменённых пар равен полю <c>changed</c>, включая третьи
/// стороны (состояние восстановлено из соседнего снимка той же эпохи);</item>
/// <item><c>relation_value</c> — наши <c>value</c> и <c>absolute</c> по причине и контексту (мир /
/// отряд / укрепление / жезл) равны журнальным;</item>
/// <item><c>diplo_snapshots</c> — проигрыш модели между сохранениями <c>m15-diplo</c>: 12 снимков
/// П§13.1, пара человек — Кланы;</item>
/// <item><c>diplo_human_ai</c> — та же модель на паре человек — Империя второй большой партии
/// (пары ИИ ↔ ИИ в критерий не входят, план §3 п. 9);</item>
/// <item><c>diplo_pairs_human</c> и <c>diplo_pairs_ai</c> — те же снимки по всем парам всех карт
/// корпуса, справочно: пары с человеком отделены от пар ИИ ↔ ИИ, потому что действия ИИ по ИИ
/// журнал не показывает вовсе (план §3 п. 9) и красное там ожидаемо;</item>
/// <item><c>alliance_threshold</c> — перед каждым наблюдённым союзом отношение проходит наш
/// <c>canMakeAlliance</c>, а после союза стоит <c>D_MK_ALLY</c> и ход заключения;</item>
/// <item><c>diplo_break</c> — разрыв: <c>D_BK_ALLY</c>, бит союза снят, «ход союза» ← ход разрыва,
/// <c>QTY_BREAKS</c> +1 ровно у одного игрока;</item>
/// <item><c>ally_no_attack</c> — ни один бой корпуса не идёт между союзниками (запрет
/// <c>X005TA0303</c>).</item>
/// </list>
///
/// <para><b>Что модель берёт наблюдением, а не считает.</b> Решения ИИ о союзе, разрыве и дани в Ф7
/// намеренно не переносятся, а зонд их не пишет (событий <c>alliance_*</c> в корпусе нет ни одного).
/// Поэтому союз и разрыв опознаются по <b>снимкам</b> — биту 0 пары, — и подставляются в конец
/// интервала; величину, ход союза и <c>QTY_BREAKS</c> считает модель, и их-то и сверяют
/// <c>alliance_threshold</c> с <c>diplo_break</c>. Кто из двух сторон предложил союз, не наблюдаемо
/// (<c>docs/corpus-schema.md</c>: «кто предложил — не читано»), а от этого зависит, какая пара
/// получит слагаемое третьей стороны, — поэтому интервал с союзом в <c>diplo_all_pairs</c> идёт
/// пропуском, а пара самого союза считается (для неё направление безразлично).</para>
///
/// <para><b>Снос жезла и вор ИИ.</b> Снос жезла своего события не имеет (задача 8), а в событии
/// <c>thief_roll</c> нет вора (план §3 п. 41). Поэтому <c>D_ROD</c> в проигрыше не участвует вовсе,
/// а бросок вора приписывается человеку только тогда, когда цель ему не принадлежит; действия ИИ
/// по человеку идут пропуском. Это ровно тот вариант, который на 300 замерах большой партии дал
/// лучшее согласие (261 против 246, П§16.3), и он же оставляет расхождения пар ИИ ↔ ИИ видимыми.</para>
///
/// <para><b>Зонд 7.x</b> пишет <c>noble_action</c> с игроком-вором — тогда бросок приписывается ему, и правило
/// выше остаётся только для старых журналов. Обмен (<c>trade_accept</c>) идёт своим действием по
/// <see cref="Diplomacy.TradeValue"/> с ценой из <c>Gspells</c>/<c>GItem</c>, а его пара строк <c>bank</c>
/// данью не считается (иначе продажа 300-золотого предмета за 200 давала +4 вместо +2).</para>
/// </summary>
[Trait("Category", "Corpus")]
public class DiplomacyHarnessTests(ITestOutputHelper output)
{
    private const string MainKey = "relation_main";
    private const string ThirdKey = "relation_third";
    private const string ValueKey = "relation_value";
    private const string SnapshotsKey = "diplo_snapshots";
    private const string HumanAiKey = "diplo_human_ai";
    private const string HumanPairsKey = "diplo_pairs_human";
    private const string AiPairsKey = "diplo_pairs_ai";
    private const string AllianceKey = "alliance_threshold";
    private const string BreakKey = "diplo_break";
    private const string NoAttackKey = "ally_no_attack";

    /// <summary>Карта П§13.1: двенадцать снимков, два прохода, пара человек (Империя) — Кланы.</summary>
    private const string SnapshotMap = "m15-diplo";

    /// <summary>Вторая большая партия: человек — Легионы, критерий — его пара с Империей
    /// (<c>Grace.RACE_TYPE</c> 0). Пары ИИ между собой в критерий не входят.</summary>
    private const string BigPartyMap = "rastlennaya-vlast-heretic";
    private const int EmpireRace = 0;

    /// <summary>Приёмка пары человек — Империя. П§16.3 воспроизвёл <b>28 снимков из 30</b>, то есть
    /// два расхождения; здесь сравнений 28 (первые снимки двух проходов базы не имеют и идут
    /// пропуском), и планка та же: не меньше 27 совпадений и не больше двух расхождений. Остаток —
    /// подпись сносов жезлов ИИ и броска вора ИИ (план §3 п. 9).</summary>
    private const int HumanAiFloor = 27;
    private const int HumanAiMismatches = 2;
    private const int HumanAiComparisons = 28;

    /// <summary>Справочный счёт по картам: имя карты -> сошлось/разошлось по всем её парам. Нужен,
    /// чтобы красное в <c>diplo_pairs_*</c> читалось по картам, а не одним числом.</summary>
    private readonly SortedDictionary<string, (int Ok, int Bad)> _byMap = new(StringComparer.Ordinal);

    /// <summary>Интервалы между снимками партии <see cref="BigPartyMap"/> в корпусе. Ноль — партии в корпусе нет
    /// (журнал и сохранения потеряны 18.09, прогоны 20260919-* её не переигрывали), и критерий
    /// <c>diplo_human_ai</c> сверять не на чем; не ноль — критерий строгий, как был.</summary>
    private int _bigPartyIntervals;

    [SkippableFact] public void Diplomacy_MatchesTheCorpus()
    {
        Skip.IfNot(GamePaths.IsAvailable, $"нет Globals: {GamePaths.Globals}");
        Skip.IfNot(CorpusFiles.Dir.Length > 0 && Directory.Exists(CorpusFiles.Dir), "нет корпуса");
        var book = ProgressBook.Load(GamePaths.Globals);
        var rules = book.DiplomacyRules;
        var c = new Counters();
        c.Note($"GVars: D_WAR {rules.War}, D_NEUTRAL {rules.Neutral}, D_PEACE {rules.Peace}, " +
               $"D_GOLD {rules.GoldPerPoint}, D_MK_ALLY {rules.MakeAlly}, D_BK_ALLY {rules.BreakAlly}, " +
               $"D_REF_ALLY {rules.RefuseAlly}, D_ATTAK_PC {rules.AttackAtPeace}, " +
               $"D_ATTAK_SC {rules.AttackStack}, D_ATTAK_FO {rules.AttackFort}, " +
               $"D_ROD {rules.RodDestroyed}, D_NOBLE {rules.NobleAction}; обрезка {Diplomacy.Max}");

        foreach (var path in CorpusFiles.Journals())
        {
            if (!TreasuryJournal.HasBank(path)) continue;        // схема ниже 5 — оси t нет вовсе
            var j = TreasuryJournal.Load(path);
            Journal(j, book, c);
        }

        c.Note("по картам, все пары: " + string.Join(", ", _byMap.Select(m => $"{m.Key} {m.Value.Ok}/{m.Value.Bad}")));
        c.Print(output, "задача 13 — дипломатия: мутатор, третья сторона, союз и дань");
        c.Require(MainKey, ThirdKey, ValueKey, AllianceKey, BreakKey, NoAttackKey);
        var (humanAi, humanAiBad) = c.Of(HumanAiKey);
        if (_bigPartyIntervals > 0)
        {
            Assert.True(humanAi + humanAiBad >= HumanAiComparisons,
                $"{HumanAiKey}: сравнений {humanAi + humanAiBad}, а снимков партии {HumanAiComparisons}");
            Assert.True(humanAi >= HumanAiFloor && humanAiBad <= HumanAiMismatches,
                $"{HumanAiKey}: сошлось {humanAi}, разошлось {humanAiBad} — хуже планки П§16.3 " +
                $"({HumanAiFloor} совпадений и не больше {HumanAiMismatches} расхождений)");
        }
        c.RequireOrSkip((SnapshotsKey, "карта плана f7-m15-diplo: 12 снимков П§13.1 — два прохода с хода 1, человек (Империя) " +
                                       "и Кланы; сохраняться каждый ход"));
        Skip.If(_bigPartyIntervals == 0,
            $"{HumanAiKey}: в корпусе нет партии {BigPartyMap} (человек — Легионы, 30 снимков П§16.3; потеряна 18.09) — " +
            "сыграть её заново с сохранением каждый ход; прочие критерии выше проверены");
    }

    /// <summary>Один журнал: сначала события <c>relation</c> (мутатор поимённо), потом проигрыш
    /// между сохранениями каждой карты.</summary>
    private void Journal(TreasuryJournal j, ProgressBook book, Counters c)
    {
        var relations = j.Events.OfType<RelationEvent>().OrderBy(e => e.Seq).ToList();
        foreach (var e in relations) Mutator(j, relations, book, e, c);

        if (j.Saves.Count == 0) return;
        var points = JournalSaves.Of(j);
        if (points.Count == 0)
        {
            c.Skip(HumanPairsKey, "у сохранений журнала нет среза seq (нет строки income хода снимка)");
            return;
        }
        foreach (var map in points.GroupBy(p => MapOf(p.Save), StringComparer.OrdinalIgnoreCase))
        {
            // Действия считаются по снимкам ЭТОЙ карты: id игроков у синтетических карт общие
            // по префиксу, а расы за ними стоят разные, и чужая карта дала бы чужую расу.
            var chain = map.OrderBy(x => x.Cut).ToList();
            var actions = Actions(j, chain, book, c);
            Map(map.Key, chain, j, book, actions, c);
            NoAttackOnAllies(j, chain, actions, book, c);
        }
    }

    // ── Журнал: мутатор поимённо ─────────────────────────────────────────────────────────────

    /// <summary>Одно событие <c>relation</c>: основная пара, третьи стороны и величина.</summary>
    private void Mutator(TreasuryJournal j, IReadOnlyList<RelationEvent> all, ProgressBook book,
                         RelationEvent e, Counters c)
    {
        var rules = book.DiplomacyRules;
        string tag = $"{Path.GetFileName(j.Path)} seq {e.Seq} {e.Cause} пара {e.RaceA}-{e.RaceB}";
        if (e.Before is null || e.After is null) { c.Skip(MainKey, "в событии нет слова пары"); return; }

        // Основная пара: мутатор над одним словом. Третьи стороны здесь не участвуют — в списке
        // игроков только A и B, как в самом мутаторе при двух игроках карты.
        var only = new List<Relation> { new((int)e.RaceA, (int)e.RaceB, (uint)e.Before.Raw) };
        var main = Diplomacy.Change(only, [(int)e.RaceA, (int)e.RaceB], rules, (int)e.RaceA, (int)e.RaceB,
                                    (int)e.Value, e.Absolute);
        uint got = main.Relations.First(r => r.Pairs((int)e.RaceA, (int)e.RaceB)).Raw;
        c.Count(MainKey, got == (uint)e.After.Raw,
                $"{tag}: наше слово {got}, журнальное {e.After.Raw} (было {e.Before.Raw}, " +
                $"value {e.Value}, absolute {e.Absolute})");

        Value(j, book, e, tag, c);
        Third(j, all, book, e, tag, c);
    }

    /// <summary>Величина и признак присваивания по причине и контексту.</summary>
    private void Value(TreasuryJournal j, ProgressBook book, RelationEvent e, string tag, Counters c)
    {
        var rules = book.DiplomacyRules;
        int current = (int)(e.Before?.Cur ?? 0);
        (int Value, bool Absolute) ours;
        switch (e.Cause)
        {
            case "attack":
                if (TargetOfAttack(j, e) is not { } target)
                { c.Skip(ValueKey, "у атаки не нашлось боя следом — вид цели неизвестен"); return; }
                ours = Diplomacy.AttackValue(rules, current, target);
                break;
            case "rod_destroyed": ours = (rules.RodDestroyed, false); break;
            case "noble": ours = (rules.NobleAction, false); break;
            case "tribute": ours = (Diplomacy.TributeValue(rules, GoldAt(j, e)), false); break;
            case "alliance_make":
                // Союз двух людей — max(rel, D_NEUTRAL + 1) (ai-q47, 80 и 67); у пары с ИИ — D_MK_ALLY.
                ours = (BothHuman(j, e) ? Math.Max(current, rules.Neutral + 1) : rules.MakeAlly, true);
                break;
            case "alliance_break": ours = (rules.BreakAlly, true); break;
            case "alliance_refuse": ours = (rules.RefuseAlly, true); break;
            case "trade_item" or "trade_spell":
                var trade = j.Events.OfType<TradeAcceptEvent>().FirstOrDefault(t => t.Seq <= e.Seq && e.Seq <= t.SeqEnd);
                if (trade is null || PriceOf(j, book, trade) is not { } price)
                { c.Skip(ValueKey, $"у {e.Cause} нет trade_accept с ценой из Gspells/GItem"); return; }
                ours = (Diplomacy.TradeValue(rules, price, trade.Gold), false);
                break;
            default: c.Skip(ValueKey, $"причина {e.Cause} в Ф7 не переносится"); return;
        }
        c.Count(ValueKey, ours.Value == e.Value && ours.Absolute == e.Absolute,
                $"{tag}: наши ({ours.Value}, {ours.Absolute}), журнальные ({e.Value}, {e.Absolute})");
    }

    /// <summary>Список изменённых пар: основная плюс третьи стороны. Состояние — соседний снимок
    /// той же эпохи, докрученный словами самих событий; если оно не сходится с <c>before</c>
    /// события, сверка не состоялась и идёт пропуском, а не расхождением.</summary>
    private void Third(TreasuryJournal j, IReadOnlyList<RelationEvent> all, ProgressBook book,
                       RelationEvent e, string tag, Counters c)
    {
        if (StateAt(j, all, e) is not { } state)
        { c.Skip(ThirdKey, "нет сохранения в эпохе события — состояние пар неоткуда взять"); return; }
        if (RewoundAcrossAlliance(j, e))
        { c.Skip(ThirdKey, "состояние откручено назад от снимка через alliance_change — слов пар союза и событий сценария зонд не пишет"); return; }
        if (j.SaveNear(e.T) is not { } near) return;
        var races = Diplomacy.PlayerRaces(near.Map, book.Prices.Economy);
        if (!state.Any(r => r.Pairs((int)e.RaceA, (int)e.RaceB) && r.Raw == (uint)(e.Before?.Raw ?? -1)))
        { c.Skip(ThirdKey, "восстановленное состояние не сошлось с before события"); return; }

        var ours = Diplomacy.Change(state, races, book.DiplomacyRules, (int)e.RaceA, (int)e.RaceB,
                                    (int)e.Value, e.Absolute).Changed;
        var mine = ours.Select(x => (Pair(x.Race1, x.Race2), x.Before, x.After)).ToHashSet();
        var theirs = e.Changed.Select(x => (Pair((int)x.Race1, (int)x.Race2), (uint)x.Before, (uint)x.After))
                              .ToHashSet();
        c.Count(ThirdKey, mine.SetEquals(theirs), $"{tag}: наши {Show(mine)}, журнальные {Show(theirs)}");
    }

    /// <summary>Вид цели боя у события атаки: мутатор зовётся из <c>CEffectBattle::apply</c>, и бой
    /// начинается сразу за событием — в корпусе через один-два <c>seq</c> (359751 → 359753). Класс id
    /// защищающейся группы различает отряд (<c>KC</c>) и укрепление (<c>FT</c>). Дальний бой целью
    /// этого события не считается: за <see cref="AttackBattleSeqGap"/> сверка пропускается.</summary>
    private static BattleTarget? TargetOfAttack(TreasuryJournal j, RelationEvent e) =>
        j.Events.OfType<BattleStartEvent>()
            .Where(b => b.Seq > e.Seq && b.Seq - e.Seq <= AttackBattleSeqGap && b.Round == 0)
            .OrderBy(b => b.Seq).Select(b => (BattleTarget?)TargetOf(b.DefenderGroup)).FirstOrDefault();

    /// <summary>Насколько близко к событию атаки стоит начало боя: в корпусе 2 <c>seq</c>, 10 — запас.</summary>
    private const long AttackBattleSeqGap = 10;

    /// <summary>Оба ли участника союза люди: строка relation лежит внутри окна своего alliance_change.</summary>
    private static bool BothHuman(TreasuryJournal j, RelationEvent e) =>
        j.Events.OfType<AllianceChangeEvent>()
            .Any(a => a.Seq <= e.Seq && e.Seq <= a.SeqEnd && a.HumanA == true && a.HumanB == true);

    /// <summary>Золото дани у события <c>tribute</c>: последняя строка <c>bank</c> плательщика (<c>a</c>) того же времени
    /// перед событием. В ту же миллисекунду бывают и траты ИИ до дани: карта 15 (<c>20261018-port-synth</c>, seq 3852606) —
    /// Легионы платят 250 за найм (seq 3852446), затем дань 178 (seq 3852605); первая строка времени давала 250 / 50 = 5
    /// вместо 3.</summary>
    private static long GoldAt(TreasuryJournal j, RelationEvent e) =>
        j.Events.OfType<BankEvent>()
            .Where(b => b.T == e.T && b.Seq < e.Seq && b.Change.Gold < 0 && JournalSaves.Same(b.Player, e.A))
            .Select(b => -b.Change.Gold).LastOrDefault();

    /// <summary>Состояние пар на момент события: снимок той же эпохи, докрученный словами событий
    /// <c>relation</c> между ним и событием (вперёд — по <c>after</c>, назад — по <c>before</c>).</summary>
    private static List<Relation>? StateAt(TreasuryJournal j, IReadOnlyList<RelationEvent> all, RelationEvent e)
    {
        var save = j.SaveBefore(e.T) ?? j.SaveAfter(e.T);
        if (save is null) return null;
        var state = save.Map.Relations.ToList();
        if (state.Count == 0) return null;
        // Ось — seq строки save, если она есть: mtime файла отстаёт от строки на секунды, и событие ИИ
        // сразу после автосохранения (ai-q11, seq 10525150 через 0,27 с) по mtime попадало «внутрь» файла.
        bool Inside(RelationEvent f) => save.Seq is { } s ? f.Seq < s : f.T <= save.T;
        if (save.Seq is { } at ? at < e.Seq : save.T <= e.T)
            foreach (var f in all.Where(f => f.Seq < e.Seq && !Inside(f)).OrderBy(f => f.Seq))
                Rewind(state, f, forward: true);
        else
            foreach (var f in all.Where(f => f.Seq >= e.Seq && Inside(f)).OrderByDescending(f => f.Seq))
                Rewind(state, f, forward: false);
        return state;
    }

    /// <summary>Снимок есть только после события, и между ними — <c>alliance_change</c>: союз и смена отношений эффектом события
    /// сценария (<c>L_ALLY</c>, <c>L_DIPLOMACY</c>) двигают слова пар мимо строк <c>relation</c> (у <c>alliance_make</c> поле
    /// <c>before</c> снято уже после записи союза), так что откат назад даёт чужое состояние. Пример — <c>20261016b-port-synth</c>,
    /// карта 3: атака нейтрала (seq 2016221) до союза с Кланами и войны с Легионами событиями E08/E10 — первый снимок дня уже
    /// после них (Империя–Кланы 0 → 100, Империя–Легионы 40 → 10), и третьи стороны выходили «Нежить и Легионы» вместо «Нежить и
    /// Кланы» журнала (у движка правило то же, <c>0x5d94f3</c>: война X с целью — при исходных 0 и 40 это Нежить и Кланы).</summary>
    private static bool RewoundAcrossAlliance(TreasuryJournal j, RelationEvent e)
    {
        if (j.SaveBefore(e.T) is not null || j.SaveAfter(e.T) is not { } save) return false;
        return j.Events.OfType<AllianceChangeEvent>().Any(a => a.Seq > e.Seq && (save.Seq is { } s ? a.Seq < s : a.T <= save.T));
    }

    /// <summary>Слова пар одного события — вперёд (<c>after</c>) или назад (<c>before</c>).</summary>
    private static void Rewind(List<Relation> state, RelationEvent e, bool forward)
    {
        foreach (var ch in e.Changed)
        {
            long word = forward ? ch.After : ch.Before;
            if (word < 0) continue;                      // пары до вызова не было
            for (int i = 0; i < state.Count; i++)
                if (state[i].Pairs((int)ch.Race1, (int)ch.Race2))
                    state[i] = state[i] with { Raw = (uint)word };
        }
    }

    // ── Сохранения: проигрыш модели между снимками ───────────────────────────────────────────

    /// <summary>Все дипломатические действия журнала на оси <c>seq</c>: бои, успешные броски вора и
    /// дань, отказ от союза. Союз и разрыв сюда не идут — их видно только по снимкам.</summary>
    private static IReadOnlyList<DiploAction> Actions(TreasuryJournal j, IReadOnlyList<SavePoint> points,
                                                      ProgressBook book, Counters c)
    {
        var list = new List<DiploAction>();
        var near = points[0].Save;
        int? Race(string playerId) => RaceOfPlayer(near, book, playerId);

        foreach (var b in j.Events.OfType<BattleStartEvent>().Where(b => b.Round == 0))
            if (Race(b.AttackerPlayer) is { } a && Race(b.DefenderPlayer) is { } d)
                list.Add(new DiploAction(b.Seq, DiploKind.Attack, a, d) { Target = TargetOf(b.DefenderGroup) });

        string? human = near.Map.Players.Values.FirstOrDefault(p => p.IsHuman)?.Id;
        var nobles = j.Events.OfType<NobleActionEvent>().Where(n => n.Success).ToList();
        foreach (var t in j.Events.OfType<ThiefRollEvent>().Where(t => t.Ret))
        {
            var owner = OwnerOf(points, t.Target);
            if (owner is null || human is null) { c.Skip(HumanPairsKey, "у цели вора нет владельца"); continue; }
            // Зонд 7.x называет вора: noble_action (CEffectNobleAction::apply 0x43a335) идёт сразу за своим
            // thief_roll с той же целью. Без этого бросок ИИ по человеку шёл пропуском и терял D_NOBLE −5
            // (rastlennaya-vlast-heretic seq 2983569 и 4109525: вор Империи по отряду Легионов, 10 → 5, 5 → 0),
            // а бросок ИИ по ИИ приписывался человеку.
            var named = nobles.FirstOrDefault(n => n.Target == t.Target && n.Seq > t.Seq
                                                   && n.Seq - t.Seq <= NobleAfterRollSeqGap);
            if (named is not null)
            {
                if (Race(named.Player) is { } by && Race(owner) is { } to)
                    list.Add(new DiploAction(t.Seq, DiploKind.Noble, by, to) { ThiefKind = ThiefTargetOf(t.Target) });
                continue;
            }
            if (JournalSaves.Same(owner, human))
            {
                c.Skip(HumanPairsKey,
                       "бросок вора по человеку — действовал ИИ, вор в событии не назван (§3 п. 41)");
                continue;
            }
            if (Race(human) is { } thief && Race(owner) is { } victim)
                list.Add(new DiploAction(t.Seq, DiploKind.Noble, thief, victim)
                { ThiefKind = ThiefTargetOf(t.Target) });
        }

        // Отказ от союза виден сам по себе — alliance_change accept=false (f7-m15-diplo, 20260919-f7b seq 504498:
        // ИИ Кланов предложил, человек отказал, 75 → D_REF_ALLY 50). Без него проигрыш держал 75.
        foreach (var r in j.Events.OfType<AllianceChangeEvent>().Where(r => !r.Accept))
            if (Race(r.A) is { } a && Race(r.B) is { } b)
                list.Add(new DiploAction(r.Seq, DiploKind.AllianceRefuse, a, b)
                { BothHuman = r.HumanA == true && r.HumanB == true });

        // Обмен (trade_accept) тоже двигает золото парой строк bank — внутри своего окна seq..seq_end.
        // Без этого исключения продажа предмета ИИ опознавалась данью: 200 золота -> +4 вместо
        // trade_item max(1, 300 - 200) / 50 = +2 (rastlennaya-vlast-heretic, seq 223516).
        var trades = j.Events.OfType<TradeAcceptEvent>().Where(t => t.Accepted && t.Ret).ToList();
        foreach (var t in trades)
        {
            if (PriceOf(j, book, t) is not { } price) { c.Skip(HumanPairsKey, "у обмена нет цены (Gspells/GItem)"); continue; }
            if (Race(t.B) is { } rcv && Race(t.A) is { } pay)
                list.Add(new DiploAction(t.Seq, DiploKind.Trade, rcv, pay) { Gold = t.Gold, Price = price });
        }
        foreach (var (seq, payer, recipient, gold) in Tributes(j))
            if (!trades.Any(t => t.Seq <= seq && seq <= t.SeqEnd)
                && Race(payer) is { } p && Race(recipient) is { } r)
                list.Add(new DiploAction(seq, DiploKind.Tribute, p, r) { Gold = gold });

        return [.. list.OrderBy(a => a.Seq)];
    }

    /// <summary>Насколько далеко от <c>thief_roll</c> стоит его <c>noble_action</c>: в корпусе 4 <c>seq</c>, 20 — запас.</summary>
    private const long NobleAfterRollSeqGap = 20;

    /// <summary>Цена обмена для <c>trade_*</c>: золото <c>Gspells.BUY_C</c> заклинания или <c>GItem.VALUE</c>
    /// предмета (тип — по экземпляру <c>IM</c> ближайшего снимка). Не <c>ai_answer.price</c>: у заклинаний
    /// там оценка ИИ (400 при <c>BUY_C</c> 200), и величина с ней расходится — журнал 20260919-human
    /// seq 15731127: G000SS0022, доплата 100, <c>relation</c> +2 = (200 − 100) / 50, а не (400 − 100) / 50.
    /// Null — цену взять неоткуда.</summary>
    private static long? PriceOf(TreasuryJournal j, ProgressBook book, TradeAcceptEvent t)
    {
        if (book.Prices.Spells.TryGetValue(t.Object, out var spell)) return spell.BuyCost.Gold;
        return j.SaveNear(t.T) is { } near && near.Map.ItemTypes.TryGetValue(t.Object, out var type)
               && book.Prices.Items.TryGetValue(type, out var item) ? item.Value.Gold : null;
    }

    /// <summary>Дань в журнале — <b>пара</b> строк <c>bank</c> одного времени: получателю плюс
    /// столько же золота, сколько минус плательщику, и ничего кроме золота (ранг 1, П§13.1: три
    /// передачи прогона 4 и пять второй большой партии). Перевод казны — <see cref="Treasury.Tribute"/>.</summary>
    private static IEnumerable<(long Seq, string Payer, string Recipient, long Gold)> Tributes(TreasuryJournal j)
    {
        foreach (var same in j.Events.OfType<BankEvent>().Where(b => GoldOnly(b)).GroupBy(b => b.T))
            foreach (var plus in same.Where(b => b.Change.Gold > 0))
                foreach (var minus in same.Where(b => b.Change.Gold == -plus.Change.Gold
                                                      && !JournalSaves.Same(b.Player, plus.Player)))
                    yield return (Math.Max(plus.Seq, minus.Seq), minus.Player, plus.Player, plus.Change.Gold);
    }

    /// <summary>Строка <c>bank</c>, в которой двинулось только золото: дань — золотая операция,
    /// а переход казны при захвате столицы двигает все шесть валют и обнуляет погибшего.</summary>
    private static bool GoldOnly(BankEvent b) =>
        b.Change.Gold != 0 && b.Change with { Gold = 0 } == Bank.Zero;

    /// <summary>Одна карта корпуса: снимки по эпохам, проигрыш каждого интервала от НАБЛЮДЁННОГО
    /// состояния предыдущего снимка (одна ошибка не тянется через всю партию, как и в
    /// <c>diplo_replay</c> П§16.3).</summary>
    private void Map(string name, IReadOnlyList<SavePoint> points, TreasuryJournal j, ProgressBook book,
                     IReadOnlyList<DiploAction> actions, Counters c)
    {
        var byEpoch = points.GroupBy(p => p.Epoch).Where(g => g.Any()).OrderBy(g => g.Key).ToList();
        if (points[0].Save.Map.Relations.Count == 0) return;       // карта без дипломатии
        foreach (var epoch in byEpoch)
        {
            var chain = epoch.OrderBy(p => p.Cut).ToList();
            SavePoint? prev = null;
            foreach (var p in chain)
            {
                // Первый снимок эпохи: база — первый снимок ДРУГОГО прохода той же карты, и только
                // если день у обоих один. Пройденная заново карта возвращает слово сценария (так
                // сыграна m15-diplo: два прохода с t01), а загрузка сохранения посреди партии — нет,
                // и её первый снимок базой быть не может.
                var start = prev ?? byEpoch.Where(g => g.Key != epoch.Key).Select(g => g.OrderBy(x => x.Cut).First())
                                           .FirstOrDefault(x => x.Save.Map.Turn == p.Save.Map.Turn);
                if (start is null)
                {
                    c.Skip(HumanPairsKey,
                           $"{name}: первый снимок прохода, а другого прохода того же дня нет — базы нет");
                    prev = p;
                    continue;
                }
                long from = prev is null ? EpochStartSeq(j, epoch.Key) : prev.Cut;
                Interval(name, start.Save, p, from, j, book, actions, c);
                prev = p;
            }
        }
    }

    /// <summary>Один интервал: от состояния снимка <paramref name="start"/> прогоняются действия
    /// журнала и, если бит союза за интервал сменился, союз или разрыв в конце интервала.</summary>
    private void Interval(string name, SaveAt start, SavePoint end, long from, TreasuryJournal j,
                          ProgressBook book, IReadOnlyList<DiploAction> actions, Counters c)
    {
        var races = Diplomacy.PlayerRaces(end.Save.Map, book.Prices.Economy);
        var state = start.Map.Relations.ToList();
        var window = actions.Where(a => a.Seq > from && a.Seq <= end.Cut).ToList();
        var alliance = AllianceOf(state, end.Save);
        if (alliance is not null) window.Add(alliance);

        var model = Play(state, races, book, window);
        Alliances(name, start, end, book, c, j.Events.OfType<RelationEvent>()
            .Where(r => r.Seq > from && r.Seq <= end.Cut).ToList());

        bool allianceHere = alliance is not null;
        if (name.Contains(BigPartyMap, StringComparison.OrdinalIgnoreCase)) _bigPartyIntervals++;
        int? human = HumanRace(end.Save, book);
        string rods = RodsGone(start, end.Save);
        foreach (var pair in end.Save.Map.Relations)
        {
            int ours = Diplomacy.Current(model, pair.Race1, pair.Race2);
            bool ok = ours == pair.Current;
            string detail = $"{name} {Path.GetFileName(end.Save.Path)} пара {pair.Race1}-{pair.Race2}: " +
                            $"наше {ours}, в снимке {pair.Current}; действий {window.Count}" +
                            (rods.Length > 0 ? $"; за интервал снесён жезл {rods}" : "");
            bool ofHuman = human is { } h && (pair.Race1 == h || pair.Race2 == h);
            if (allianceHere) c.Skip(ofHuman ? HumanPairsKey : AiPairsKey,
                                     "интервал с союзом: сторона-инициатор не наблюдаема");
            else
            {
                c.Count(ofHuman ? HumanPairsKey : AiPairsKey, ok, detail);
                var (mapOk, mapBad) = _byMap.GetValueOrDefault(name);
                _byMap[name] = ok ? (mapOk + 1, mapBad) : (mapOk, mapBad + 1);
            }
            foreach (var key in Keys(name, pair, human, book))
            {
                c.Count(key, ok, detail);
                // Двенадцать снимков П§13.1 печатаются поимённо: это и есть та таблица, на которой
                // держится модель отношений, и в выводе харнесса она должна читаться целиком.
                if (key == SnapshotsKey)
                    c.Note($"  {Path.GetFileName(end.Save.Path)} пара {pair.Race1}-{pair.Race2}: "
                           + $"ждали {ours}, в снимке {pair.Current}{(ok ? "" : "  РАСХОЖДЕНИЕ")}");
            }
        }
    }

    /// <summary>Жезлы, исчезнувшие за интервал: у сноса нет ни своего события, ни виновника в
    /// снимке (задача 8, план §3 п. 9) — поэтому <c>D_ROD</c> в проигрыше не участвует, и это
    /// подпись ожидаемых расхождений пар ИИ.</summary>
    private static string RodsGone(SaveAt start, SaveAt end) =>
        string.Join(",", start.Map.Rods.Values.Where(r => !end.Map.Rods.ContainsKey(r.Id)).Select(r => r.Id));

    /// <summary>В какие именные счётчики идёт пара этого снимка: критерий П§13.1 (пара человека с
    /// играбельной расой карты <c>m15-diplo</c>) и критерий второй большой партии (пара человека с
    /// Империей).</summary>
    private static IReadOnlyList<string> Keys(string name, Relation pair, int? human, ProgressBook book)
    {
        if (human is not { } h || (pair.Race1 != h && pair.Race2 != h)) return [];
        int other = pair.Race1 == h ? pair.Race2 : pair.Race1;
        var keys = new List<string>();
        if (name.Contains(SnapshotMap, StringComparison.OrdinalIgnoreCase) && Playable(book, other))
            keys.Add(SnapshotsKey);
        if (name.Contains(BigPartyMap, StringComparison.OrdinalIgnoreCase) && other == EmpireRace)
            keys.Add(HumanAiKey);
        return keys;
    }

    /// <summary>Проигрыш действий по нашим правилам.</summary>
    private static IReadOnlyList<Relation> Play(IReadOnlyList<Relation> state, IReadOnlyList<int> races,
                                                ProgressBook book, IEnumerable<DiploAction> actions)
    {
        var rules = book.DiplomacyRules;
        var now = state;
        foreach (var a in actions.OrderBy(a => a.Seq))
            now = a.Kind switch
            {
                DiploKind.Attack => Diplomacy.Attack(now, races, rules, a.RaceA, a.RaceB, a.Target,
                                                     Playable(book, a.RaceB)).Relations,
                DiploKind.Noble => Diplomacy.NobleAction(now, races, rules, a.RaceA, a.RaceB, a.ThiefKind,
                                                         Playable(book, a.RaceB), success: true).Relations,
                DiploKind.Tribute => Diplomacy.Tribute(now, races, rules, a.RaceA, a.RaceB, a.Gold).Relations,
                DiploKind.Trade => Diplomacy.Change(now, races, rules, a.RaceA, a.RaceB,
                                                    Diplomacy.TradeValue(rules, a.Price, a.Gold), false).Relations,
                DiploKind.AllianceMake => Diplomacy.MakeAlliance(now, races, rules, a.RaceA, a.RaceB,
                                                                 a.Turn).Relations,
                DiploKind.AllianceBreak => Diplomacy.BreakAlliance(now, races, rules, a.RaceA, a.RaceB,
                                                                   a.Turn).Change.Relations,
                DiploKind.AllianceRefuse => Diplomacy.RefuseAlliance(now, races, rules, a.RaceA, a.RaceB,
                                                                     a.BothHuman).Relations,
                _ => now,
            };
        return now;
    }

    /// <summary>Сменился ли за интервал бит союза какой-нибудь пары — то самое решение ИИ, которое
    /// модель берёт наблюдением. Действие кладётся в конец интервала.</summary>
    private static DiploAction? AllianceOf(IReadOnlyList<Relation> before, SaveAt after)
    {
        foreach (var pair in after.Map.Relations)
        {
            bool was = before.FirstOrDefault(r => r.Pairs(pair.Race1, pair.Race2)).Ally;
            if (was == pair.Ally) continue;
            return new DiploAction(long.MaxValue, pair.Ally ? DiploKind.AllianceMake : DiploKind.AllianceBreak,
                                   pair.Race1, pair.Race2) { Turn = after.Map.Turn };
        }
        return null;
    }

    /// <summary>Союз и разрыв на самих снимках: порог до союза, присвоенная величина, ход союза и
    /// <c>QTY_BREAKS</c> после.</summary>
    private void Alliances(string name, SaveAt start, SavePoint end, ProgressBook book, Counters c,
                           IReadOnlyList<RelationEvent> inWindow)
    {
        var rules = book.DiplomacyRules;
        var before = start.Map.Relations;
        foreach (var pair in end.Save.Map.Relations)
        {
            var was = before.FirstOrDefault(r => r.Pairs(pair.Race1, pair.Race2));
            if (was.Ally == pair.Ally) continue;
            string tag = $"{name} {Path.GetFileName(end.Save.Path)} пара {pair.Race1}-{pair.Race2}";
            if (pair.Ally && inWindow.Any(r => r.Cause != "alliance_make"
                                              && r.Changed.Any(x => Same(x, pair.Race1, pair.Race2))))
                // ai-q49: союз с Легионами при 95 → 100, а следом отказ Нежити снял третьей стороне −25 — в снимке 75.
                c.Skip(AllianceKey, "после союза пару в том же окне сдвинуло событие relation — величину сверяет relation_value");
            else if (pair.Ally)
                // Ход заключения в критерий не идёт: союз заключает ИИ на СВОЁМ ходе, и «ход союза»
                // снимку не равен (в большой партии 14 против снимка 15) — момент решения ИИ
                // ненаблюдаем, план §3 п. 9.
            {
                // ai-q47b (20260917-synth): союз двух людей при 80 → max(80, D_NEUTRAL + 1) = 80, не D_MK_ALLY.
                bool humans = Humans(end.Save, book, pair.Race1) && Humans(end.Save, book, pair.Race2);
                int want = humans ? Math.Max(was.Current, rules.Neutral + 1) : rules.MakeAlly;
                c.Count(AllianceKey,
                    Diplomacy.CanMakeAlliance(before, rules, pair.Race1, pair.Race2, humans)
                    && pair.Current == want,
                    $"{tag}: союз при {was.Current} → {pair.Current} (ждали {want}), " +
                    $"ход союза {pair.AllianceTurn}, порог D_NEUTRAL {rules.Neutral}");
            }
            else
            {
                // Величину после разрыва в том же окне двигают и следующие события relation: 20260919-human,
                // разрыв Империи с Кланами seq 15422164 → 10, затем бой Кланов с Нежитью seq 15467141 дал паре
                // 0-3 третьей стороной +3 — в t62-102 13. Тогда величину сверяет relation_value по самому событию,
                // а здесь — ход союза и QTY_BREAKS (как у союза выше).
                // Разрыв именно этой пары (стороны строки — сама пара); следом пару двигает и разрыв другой пары тем же игроком — третьей
                // стороной (карта 22, 20261019b, день 10: Кланы рвут с Легионами → 10, затем с Нежитью — пара Кланы–Легионы 10 → 5).
                long breakSeq = inWindow.Where(r => r.Cause == "alliance_break" && Same(r, pair.Race1, pair.Race2))
                                        .Select(r => r.Seq).DefaultIfEmpty(long.MinValue).Max();
                bool movedAfter = inWindow.Any(r => r.Seq > breakSeq && r.Changed.Any(x => Same(x, pair.Race1, pair.Race2)));
                // «Ход союза» перезаписывается ходом разрыва (3 → 5 в m15, 14 → 19 в партии). Человек рвёт на
                // своём ходе — это ход снимка; ИИ — на своём, и снимок человека бывает уже следующим днём
                // (ai-q49: разрыв Легионами на 10-м ходу, снимок 11) — поэтому ход разрыва лежит внутри окна.
                c.Count(BreakKey,
                    (movedAfter || pair.Current == rules.BreakAlly)
                    && pair.AllianceTurn >= start.Map.Turn && pair.AllianceTurn <= end.Save.Map.Turn
                    && pair.AllianceTurn != was.AllianceTurn && Broke(start, end.Save) == 1,
                    $"{tag}: разрыв → {pair.Current} (ждали {rules.BreakAlly}" +
                    (movedAfter ? ", после разрыва пару сдвинуло событие relation — величина не сверяется" : "") +
                    $"), ход союза {was.AllianceTurn} → {pair.AllianceTurn} (ход снимка {end.Save.Map.Turn}), " +
                    $"QTY_BREAKS выросло у {Broke(start, end.Save)}");
            }
        }
    }

    private static bool Same(RelationChange x, int race1, int race2) =>
        (x.Race1 == race1 && x.Race2 == race2) || (x.Race1 == race2 && x.Race2 == race1);

    private static bool Same(RelationEvent r, int race1, int race2) =>
        (r.RaceA == race1 && r.RaceB == race2) || (r.RaceA == race2 && r.RaceB == race1);

    /// <summary>У скольких игроков <c>QTY_BREAKS</c> выросло за интервал: на разрыве оно растёт на
    /// 1 ровно у одного (ранг 1, П§13.1 и §16.3).</summary>
    private static int Broke(SaveAt start, SaveAt end) =>
        end.Map.Players.Values.Count(
            p => start.Map.Players.TryGetValue(p.Id, out var was) && p.QtyBreaks > was.QtyBreaks);

    /// <summary>Ни один бой не идёт между союзниками (<c>X005TA0303</c>): состояние пары берётся из
    /// снимка, в который бой ещё не вошёл.</summary>
    private void NoAttackOnAllies(TreasuryJournal j, IReadOnlyList<SavePoint> points,
                                  IReadOnlyList<DiploAction> actions, ProgressBook book, Counters c)
    {
        foreach (var a in actions.Where(a => a.Kind == DiploKind.Attack))
        {
            var before = points.Where(p => p.Cut <= a.Seq).MaxBy(p => p.Cut);
            if (before is null) { c.Skip(NoAttackKey, "бой раньше первого снимка — состояние пары неизвестно"); continue; }
            // ai-q27 → ai-q12: одна основа, союз Империи и Эльфов только в первой — бой следующей загрузки
            // со снимком прежней не сверяется.
            if (before.Epoch + 1 < j.EpochStarts.Count && a.Seq > EpochStartSeq(j, before.Epoch + 1))
            { c.Skip(NoAttackKey, "бой в следующей загрузке карты — снимка его эпохи нет"); continue; }
            // Союз, разорванный между снимком и боем (alliance_change), — уже не союз: карта 22 (20261019b), день 10 — Кланы рвут с Нежитью
            // (seq 5625480), вор Нежити дерётся с отрядом Кланов на дуэли (seq 5632222), снимок t10 — ещё с союзом.
            var relations = before.Save.Map.Relations.ToList();
            foreach (var change in j.Events.OfType<AllianceChangeEvent>().Where(x => x.Seq > before.Cut && x.Seq < a.Seq && x.After is not null))
                if (RaceOfPlayer(before.Save, book, change.A) is { } ra && RaceOfPlayer(before.Save, book, change.B) is { } rb)
                {
                    int at = relations.FindIndex(r => r.Pairs(ra, rb));
                    if (at >= 0) relations[at] = relations[at] with { Raw = (uint)change.After!.Raw };
                }
            c.Count(NoAttackKey, Diplomacy.AttackRefusal(relations, a.RaceA, a.RaceB) is null,
                    $"{Path.GetFileName(before.Save.Path)}: бой {a.RaceA} → {a.RaceB} при союзе");
        }
    }

    // ── Мелочь ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Первое событие эпохи на оси <c>seq</c> минус один: начало окна первого снимка.</summary>
    private static long EpochStartSeq(TreasuryJournal j, int epoch)
    {
        if (epoch < 0 || epoch >= j.EpochStarts.Count) return 0;
        long t = j.EpochStarts[epoch];
        return j.Events.Where(e => TreasuryJournal.TimeOf(e) is { } x && x >= t)
                       .Select(e => e.Seq).DefaultIfEmpty(0).Min() - 1;
    }

    /// <summary>Имя карты — часть имени файла до счётчика хода (<c>p1-f7-m15-diplo-human-t01-017.sg</c>).</summary>
    private static string MapOf(SaveAt save)
    {
        string name = Path.GetFileNameWithoutExtension(save.Path);
        int cut = name.LastIndexOf("-t", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? name[..cut] : name;
    }

    /// <summary>Раса игрока по снимку; <c>null</c> — игрока или его расы в снимке нет.</summary>
    private static int? RaceOfPlayer(SaveAt save, ProgressBook book, string playerId) =>
        save.Map.Players.TryGetValue(playerId, out var player)
        && book.Prices.Economy.Races.TryGetValue(player.RaceId, out var race) ? race.RaceType : null;

    /// <summary>Раса человека среди игроков снимка; <c>null</c> — человека нет или его расы нет в
    /// <c>Grace.dbf</c>.</summary>
    private static int? HumanRace(SaveAt save, ProgressBook book) =>
        save.Map.Players.Values.FirstOrDefault(p => p.IsHuman) is { } human
            ? RaceOfPlayer(save, book, human.Id) : null;

    /// <summary>Играет ли расой человек (по снимку).</summary>
    private static bool Humans(SaveAt save, ProgressBook book, int raceType) =>
        save.Map.Players.Values.Any(p => p.IsHuman && RaceOfPlayer(save, book, p.Id) == raceType);

    /// <summary><c>Grace.PLAYABLE</c> расы: непроходная (нейтралы) отношений не двигает вовсе.</summary>
    private static bool Playable(ProgressBook book, int raceType) =>
        book.Prices.Economy.Races.Values.FirstOrDefault(r => r.RaceType == raceType)?.Playable ?? false;

    /// <summary>Владелец объекта по снимкам: отряд (<c>KC</c>) — из <c>CMidStack</c>, укрепление
    /// (<c>FT</c>) — из записи города.</summary>
    private static string? OwnerOf(IReadOnlyList<SavePoint> points, string objectId)
    {
        foreach (var p in points)
        {
            if (p.Save.Map.Stacks.TryGetValue(objectId, out var stack)) return stack.Owner;
            if (p.Save.Sites.FirstOrDefault(s => JournalSaves.Same(s.Id, objectId)) is { } site
                && site.Owner.Length > 0) return site.Owner;
        }
        return null;
    }

    /// <summary>Класс id цели боя: укрепление (<c>FT</c>) или отряд.</summary>
    private static BattleTarget TargetOf(string groupId) =>
        Kind(groupId) == "FT" ? BattleTarget.Fortification : BattleTarget.Stack;

    /// <summary>Класс id цели вора.</summary>
    private static ThiefTargetKind ThiefTargetOf(string objectId) => Kind(objectId) switch
    {
        "FT" => ThiefTargetKind.Fortification,
        "RU" => ThiefTargetKind.Ruin,
        "SI" => ThiefTargetKind.Merchant,
        _ => ThiefTargetKind.Stack,
    };

    /// <summary>Две буквы класса из id объекта (<c>S143<b>KC</b>0001</c>).</summary>
    private static string Kind(string id) => id.Length >= 6 ? id.Substring(4, 2).ToUpperInvariant() : "";

    private static (int, int) Pair(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

    private static string Show(IEnumerable<((int, int) Pair, uint Before, uint After)> set) =>
        "[" + string.Join(", ", set.OrderBy(x => x.Pair).Select(
            x => $"{x.Pair.Item1}-{x.Pair.Item2} {x.Before}→{x.After}")) + "]";
}
