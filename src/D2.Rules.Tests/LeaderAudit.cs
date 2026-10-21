using D2.Data;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Rules.Magic;
using D2.Rules.Map;
using D2.Rules.Units;

namespace D2.Rules.Tests;

/// <summary>Один лидер в одном сохранении глазами задачи 11: уровень, опыт, отложенные выборы и
/// набор взятых навыков (<c>GleaUpg.MODIF_ID</c> из цепочки <c>MidUnit.MODIF_ID</c>).</summary>
internal sealed record LeaderSeen(string Group, string Save, int Turn, string UnitId, string TypeId,
                                  int Level, int Xp, string LordId, bool Human, int Pending,
                                  IReadOnlyList<string> Skills, bool DynLevel);

/// <summary>
/// Разбор корпуса для задачи 11 Ф7 — навыки лидеров по сохранениям и запас хода по журналу.
///
/// <para><b>Почему по сохранениям, а не по журналу.</b> До схемы 6.0 (партия 2) событий выбора навыка у зонда
/// не было (план §3 п. 32): единственный след выбора — новая строка <c>GleaUpg</c> в цепочке
/// <c>MidUnit.MODIF_ID</c> и убывший <c>MidStack.UPGCOUNT</c>. Поэтому <c>skill_count</c> считается
/// по каждому снимку, а <c>skill_available</c> — по тому, что появилось между двумя соседними
/// снимками одной партии. Выбор ИИ сверяется по строкам <c>skill_offer</c>/<c>skill_pick</c>
/// (<see cref="SkillPicks"/>).</para>
///
/// <para><b>Чем это не тавтология.</b> Ни один счётчик не сверяет модель с самой собой: число
/// навыков сверяется с <b>уровнем</b> (другое поле файла), список доступного — с <b>таблицей</b>
/// <c>GleaUpg</c> и лордом игрока (третий источник), а запас хода — с <c>move_left</c> журнала,
/// который модель не читает вовсе.</para>
/// </summary>
internal sealed class LeaderAudit(ProgressBook book, Counters c)
{
    private const string EmptyId = "G000000000";

    /// <summary>Все <c>MODIF_ID</c> таблицы <c>GleaUpg</c> — 27 разных строк на 391 запись
    /// (лорды делят одни и те же модификаторы). Пересечения с <c>GItem.MOD_EQUIP</c> нет ни
    /// одного, поэтому «навык уровня» отличается от «способности предмета» по этому набору без
    /// вычитания надетого (иначе пришлось бы звать <see cref="LeaderAbilities.Split"/>).</summary>
    private readonly IReadOnlySet<string> _skillIds =
        book.LeaderUpgrades.Select(u => u.ModifId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly List<LeaderSeen> _seen = [];
    private int _pairs, _reloads, _noLord, _firstMoves, _pathfinders, _late;

    /// <summary>Сохранения корпуса по каталогам и картам: все <c>*.sg</c> каталогов, не начинающихся
    /// на «_», в порядке имени. Пары для <see cref="NewSkills"/> берутся внутри одной карты одного
    /// каталога — id объектов у синтетических карт общий префикс, и <c>S143UN0005</c> на соседней
    /// карте это другой юнит.</summary>
    public static IEnumerable<(string Group, IReadOnlyList<SaveAt> Saves)> AllSaves()
    {
        foreach (var dir in Directory.EnumerateDirectories(CorpusFiles.Dir)
                     .Where(d => !Path.GetFileName(d).StartsWith('_'))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var groups = Directory.EnumerateFiles(dir, "*.sg")
                .GroupBy(MapOf, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in groups)
                yield return ($"{Path.GetFileName(dir)}/{g.Key}",
                              g.OrderBy(f => f, StringComparer.Ordinal).Select(f => new SaveAt(0, f)).ToList());
        }
    }

    /// <summary>Имя карты из имени файла: всё до «-tNN-NNN.sg»; такого хвоста нет — файл сам себе
    /// группа (<c>map-start.sg</c>, <c>probe-squad.sg</c>).</summary>
    private static string MapOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int cut = name.LastIndexOf("-t", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? name[..cut] : name;
    }

    /// <summary><c>skill_count</c>: у героя уровня N ровно N − 1 взятых навыков минус отложенные
    /// (<c>UPGCOUNT</c>) — считая от того, с чем герой пришёл с карты.
    /// <para>Уровень берётся у реализации (порождённый <c>G000UG****</c> разбирается
    /// <see cref="UnitGenerator"/>), а не у поля <c>LEVEL</c> как есть: у прокачанного героя поле
    /// несёт уровень реализации, и <see cref="ProgressBook.TryGlobal"/> — то же место, которым
    /// это делают задачи 4 и 9.</para>
    /// <para><b>Уровень и навыки, выставленные редактором.</b> Редактор пишет <c>LEVEL</c> героя прямо, без выборов:
    /// в <c>game-pristine/Exports</c> 49 лидеров с <c>LEVEL</c> &gt; 1, и ни у одного нет строки <c>GleaUpg</c>
    /// (ранг 1, файлы) — так пришёл «Karasten» <c>S143UN005f</c> карты Arrival of the Pilgrims (основа ai-q25):
    /// ур. 7, навыков 0, <c>UPGCOUNT</c> 0. Строители синтетических карт, наоборот, дают навык герою 1-го уровня
    /// (m17, ai-t05e, ai-q03…). Признака «выставлено редактором» в файле нет — тип и у выращенного движком героя
    /// остаётся <c>G000UU</c>, — поэтому точка отсчёта — первый снимок карты: его расхождение с правилом и есть
    /// смещение редактора, а дальше правило сверяется за вычетом смещения. Движок смещения не создаёт, так что
    /// на последующих снимках оно обязано держаться.</para></summary>
    public void Skills(string group, IReadOnlyList<SaveAt> saves)
    {
        var authored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var save in saves)
            foreach (var seen in LeadersOf(group, save))
            {
                _seen.Add(seen);
                if (seen.LordId.Length == 0)
                { c.Skip("skill_count", "у игрока нет лорда (нейтралы) — списка навыков нет"); continue; }
                if (!book.LeaderUpgrades.Any(u => Same(u.LordId, seen.LordId)))
                { c.Skip("skill_count", $"лорд {seen.LordId} строк GleaUpg не имеет (нейтральные лорды)"); continue; }
                // Правило «N − 1» описывает выдачу движком, а не редактором (§1.5): первый снимок карты задаёт
                // смещение редактора, дальше оно вычитается.
                int byEngine = Math.Max(seen.Level - 1 - seen.Pending, 0);
                if (ReferenceEquals(save, saves[0]) && byEngine != seen.Skills.Count)
                {
                    authored[seen.UnitId] = byEngine - seen.Skills.Count;
                    c.Skip("skill_count", $"уровень или навыки выставлены редактором карты — точка отсчёта ({seen.UnitId} " +
                                          $"ур. {seen.Level}, навыков {seen.Skills.Count}, UPGCOUNT {seen.Pending})");
                    continue;
                }
                int offset = authored.GetValueOrDefault(seen.UnitId);
                int expected = byEngine - offset;
                c.Count("skill_count", seen.Skills.Count == expected,
                        $"{seen.Save} {seen.UnitId} {seen.TypeId} ур. {seen.Level}: навыков {seen.Skills.Count} " +
                        $"({string.Join(",", seen.Skills)}), ждали {seen.Level} − 1 − {seen.Pending}" +
                        $"{(offset == 0 ? "" : $" − {offset} (редактор)")} = {expected}");
            }
    }

    /// <summary><c>skill_available</c>: каждый навык, появившийся у лидера между двумя снимками, обязан
    /// входить в наш накопительный список для его лорда и уровня. Уровень берётся у ПОЗДНЕГО снимка —
    /// выбор сделан где-то между снимками, и это верхняя граница; взятое — у РАННЕГО. Выбор ИИ точнее
    /// сверяет <see cref="SkillPicks"/>.</summary>
    public void NewSkills()
    {
        foreach (var (unit, chain) in _seen.GroupBy(s => (s.Group, s.UnitId.ToLowerInvariant()))
                                           .Select(g => (g.Key.Item2, g.ToList())))
        {
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                var (a, b) = (chain[i], chain[i + 1]);
                _pairs++;
                // Перезагрузка карты (второй прогон той же карты): ход или уровень откатились —
                // снимки соседними не являются, и «новый навык» между ними ничего не значит.
                if (b.Turn < a.Turn || b.Level < a.Level) { _reloads++; continue; }
                if (b.LordId.Length == 0 || !Same(a.LordId, b.LordId)) { _noLord++; continue; }
                var list = LeaderUpgrades.Available(book.LeaderUpgrades, b.LordId, b.Level, a.Skills);
                foreach (var fresh in b.Skills.Where(s => !a.Skills.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    var row = list.FirstOrDefault(u => Same(u.ModifId, fresh));
                    bool ok = row is not null;
                    // «Поздний» выбор — навык, ставший доступным на уровне СТРОГО НИЖЕ текущего:
                    // ровно он и опровергает правило «предлагаются только навыки нового уровня».
                    if (row is not null && row.MinLevel < b.Level) _late++;
                    string detail = $"{b.Save} {unit} {b.TypeId} ур. {a.Level} → {b.Level}, лорд {b.LordId}: " +
                                    $"новый навык {fresh}; наш список — {string.Join(",", list.Select(u => u.ModifId))}";
                    c.Count("skill_available", ok, detail);
                }
            }
        }
    }

    /// <summary><c>ai_skill_argmax</c>: каждый выбор навыка героем ИИ (<c>skill_pick</c>, не бесплатный) обязан
    /// совпасть с <see cref="SkillChoice.ByAi"/> над <see cref="LeaderUpgrades.Available"/> с фильтром врождённых.
    /// <para>Список движка — последняя строка <c>skill_offer</c> того же лидера <b>в той же загрузке карты</b>; наш
    /// список строится из таблицы (взятым считается всё, что таблица лорда даёт на этом уровне, а движок не
    /// предложил), так что сверяются порядок таблицы, фильтр и argmax. Строка <c>skill_offer</c> пишется по ключу
    /// «лидер:уровень:список» один раз на журнал: на повторной загрузке карты (ai-q57, ai-q03) и на соседней карте с
    /// теми же id её нет, а последняя напечатанная — чужая (8 из 8 «расхождений» без этой оговорки). Человек или ИИ и
    /// тип героя — из сохранения той же загрузки (id у карт на одной основе общие).</para></summary>
    public void SkillPicks(string path, TreasuryJournal j)
    {
        var starts = new List<long>();
        var offers = new Dictionary<string, SkillOfferEvent>(StringComparer.OrdinalIgnoreCase);
        var picks = new List<(SkillPickEvent Pick, SkillOfferEvent? Offer)>();
        var loads = new JournalLoads();
        foreach (var e in JournalCache.Read(path))
        {
            if (loads.Opens(e)) { starts.Add(e.Seq); offers.Clear(); }
            if (e is SkillOfferEvent o) offers[o.Leader] = o;
            else if (e is SkillPickEvent p && p.Apply && !p.Free) picks.Add((p, offers.GetValueOrDefault(p.Leader)));
        }
        int SessionOf(long seq) => starts.FindLastIndex(s => s <= seq);
        foreach (var (pick, offer) in picks)
        {
            if (offer is null)
            { c.Skip("ai_skill_argmax", "в этой загрузке карты строки skill_offer нет — ключ напечатан раньше"); continue; }
            var save = j.Saves.Where(s => s.Seq is { } q && SessionOf(q) == SessionOf(pick.Seq))
                              .MinBy(s => Math.Abs(s.Seq!.Value - pick.Seq));
            if (save is null)
            { c.Skip("ai_skill_argmax", "у загрузки карты нет своего сохранения — владелец и тип героя неизвестны"); continue; }
            if (save.Map.Players.GetValueOrDefault(offer.Player) is not { } player
                || save.Map.Units.GetValueOrDefault(pick.Leader) is not { } leader
                || !book.TryGlobal(leader.TypeId, leader.Level, out var typeId, out _))
            { c.Skip("ai_skill_argmax", "игрока или героя нет в сохранении своей загрузки"); continue; }
            if (player.IsHuman) continue;                            // выбор человека — skill_available
            int level = (int)offer.Level;
            var table = LeaderUpgrades.Available(book.LeaderUpgrades, offer.Lord, level);
            var taken = table.Select(u => u.ModifId)
                             .Where(id => !offer.Ids.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            var list = LeaderUpgrades.Available(book.LeaderUpgrades, offer.Lord, level, taken,
                                                LeaderAbilities.Innate(typeId, book.InnateAbilities), book.Rules.Modifiers);
            var ours = SkillChoice.ByAi(list);
            bool inTable = offer.Ids.All(id => table.Any(u => Same(u.ModifId, id)));
            c.Count("ai_skill_argmax", inTable && ours is not null && Same(ours.ModifId, pick.Modif),
                    $"{System.IO.Path.GetFileName(path)} seq {pick.Seq} {pick.Leader} {typeId} ур. {level} лорд {offer.Lord}: " +
                    $"движок {pick.Modif}, модель {ours?.ModifId ?? "—"}; предложено {string.Join(",", offer.Ids)}" +
                    $"{(inTable ? "" : " (есть вне таблицы лорда)")}");
        }
    }

    /// <summary><c>leader_move_max</c>: <c>move_left</c> ПЕРВОГО за день <c>stack_move</c> отряда и
    /// есть полный запас дня — у первого вызова зонд пишет очки до списания шага (измерено: 99/0
    /// против 0/90 у второй гипотезы «плюс цена шага», подпись <c>StackMoveEvent.MoveLeft</c> для
    /// первого вызова дня неверна). Сверяется с <see cref="Movement.StartMovement"/>, которому подан
    /// справочник <c>Gmodif</c>: у героя с Pathfinding модель обязана дать <c>MOVE</c> с процентом.
    /// <para>День игрока — от его строки <c>income</c> до следующей ЕГО строки <c>income</c>: в
    /// больших партиях доходы ИИ идут внутри того же дня. Отряд и цепочка берутся из снимка того же
    /// дня; если за день лидер вырос или сменил цепочку, наблюдение уходит в пропуск.</para></summary>
    public void DayStartMovement(string path, TreasuryJournal j)
    {
        var points = JournalSaves.Of(j);
        if (points.Count == 0) { c.Skip("leader_move_max", "у журнала нет сохранений в манифесте"); return; }
        // Ходов и цен шага в TreasuryJournal нет вовсе: у stack_move и move_cost нет поля t, а он
        // требует время у каждого своего события. Поэтому журнал читается второй раз — за этими двумя
        // и за строками дня.
        List<IncomeEvent> incomes = [];
        List<StackMoveEvent> moves = [];
        List<MoveCostEvent> costs = [];
        List<ItemEquipEvent> bootsOff = [];
        foreach (var e in JournalCache.Read(path))
            switch (e)
            {
                case IncomeEvent i when i.T is not null: incomes.Add(i); break;
                case StackMoveEvent m when m.Ret: moves.Add(m); break;
                case MoveCostEvent mc when mc.Ret > 0: costs.Add(mc); break;
                case ItemEquipEvent ie when !ie.Equip && ie.Apply && ie.Ret && ie.Slot == Equipment.SlotBoots:
                    bootsOff.Add(ie); break;
            }
        incomes.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        moves.Sort((x, y) => x.Seq.CompareTo(y.Seq));
        costs.Sort((x, y) => x.Seq.CompareTo(y.Seq));

        foreach (var income in incomes)
        {
            // День игрока — от его строки income до СЛЕДУЮЩЕЙ ЕГО строки income, а не до чужой:
            // в больших партиях доходы ИИ идут внутри того же дня и разрезали бы ход человека.
            long dayEnd = incomes.FirstOrDefault(e => e.Seq > income.Seq && Same(e.Player, income.Player))
                                 ?.Seq ?? long.MaxValue;
            int epoch = j.EpochOf(income.T!.Value);
            var point = JournalSaves.After(points, income.Seq, epoch);
            if (point is null) continue;
            var before = JournalSaves.Before(points, income.Seq, epoch);
            foreach (var first in moves.Where(m => m.Seq > income.Seq && m.Seq < dayEnd)
                                       .GroupBy(m => m.Stack, StringComparer.OrdinalIgnoreCase)
                                       .Select(g => g.First()))
                FirstMove(point.Save, before?.Save, first, income, costs,
                          bootsOff.Any(b => b.Seq > income.Seq && b.Seq < first.Seq && Same(b.Stack, first.Stack)));
        }
    }

    private void FirstMove(SaveAt save, SaveAt? before, StackMoveEvent first, IncomeEvent income,
                           List<MoveCostEvent> costs, bool bootsTakenOff)
    {
        if (!save.Map.Stacks.TryGetValue(first.Stack, out var stack)) return;
        if (!JournalSaves.Same(stack.Owner, income.Player)) return;
        _firstMoves++;
        if (!save.Map.Units.TryGetValue(stack.LeaderId, out var leader)) return;
        var chain = leader.Modifiers.Where(m => m.Length > 0 && !Same(m, EmptyId))
            .Select(id => book.Rules.Modifiers!.GetValueOrDefault(id)).OfType<Modifier>().ToList();
        int boosted = EffectiveStats.OfStack(chain, moveAllowance: 1000).MoveAllowance;
        if (boosted == 1000) return;                    // навыков хода у лидера нет — не наблюдение
        _pathfinders++;
        // Снятые до первого шага сапоги режут уже выданный запас дня (ai-t05e, сохранения 035–037: MOVE 24 → 20, и
        // повторное надевание его не вернуло), а move_left первого шага пишется после снятия. Снятие в порте запаса
        // не трогает (ponytail в Equipment.Unequip), так что запаса дня эта строка не показывает. Сапоги — единственные
        // предметы с ходом: MOD_EQUIP с GmodifL TYPE 7 у пяти предметов, все категории 13 (ранг 1, таблицы).
        if (bootsTakenOff)
        { c.Skip("leader_move_max", "до первого шага дня отряд снимал сапоги — запас дня урезан снятием"); return; }
        // MOVE лидера растёт с уровнем (GDynUpgr.MOVE), и база цепочки — выращенное число, а не
        // колонка Gunits: у героя 11-го уровня они различаются, и на базовом числе сверка красная.
        if (book.Grown(leader.TypeId, leader.Level) is not { } grown)
        { c.Skip("leader_move_max", $"тип лидера {leader.TypeId} не выращивается — MOVE неизвестен"); return; }
        var moveOfLeader = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { [leader.TypeId] = grown.MoveAllowance };
        // Снимок берётся в конце хода человека, а очки дня движок выдал в начале: если лидер за этот
        // день вырос в уровне или сменил цепочку, MOVE снимка — уже не тот, с которым начинался день.
        if (before?.Map.Units.GetValueOrDefault(leader.Id) is { } was
            && (was.Level != leader.Level || !was.Modifiers.SequenceEqual(leader.Modifiers, StringComparer.OrdinalIgnoreCase)))
        { c.Skip("leader_move_max", "за этот день у лидера сменились уровень или цепочка — запас дня неизвестен"); return; }
        int expected = Movement.StartMovement(stack, save.Map, moveOfLeader, modifiers: book.Rules.Modifiers);
        string detail = $"{Path.GetFileName(save.Path)} {first.Stack} лидер {leader.TypeId} ур. {leader.Level} " +
                        $"MOVE {grown.MoveAllowance} " +
                        $"({string.Join(",", leader.Modifiers.Where(m => !Same(m, EmptyId)))}): " +
                        $"журнал {first.MoveLeft}, модель {expected}";
        c.Count("leader_move_max", expected == first.MoveLeft, detail);

        // Вторая гипотеза рядом, как у задачи 4: «move_left — остаток ПОСЛЕ шага, и запас дня равен
        // move_left плюс цена шага». Печатается для разбора, в приёмку не входит: на корпусе она
        // красная целиком, а первая — зелёная целиком (см. отчёт задачи 11, раздел про move_left).
        var step = costs.LastOrDefault(m => m.Seq < first.Seq && Same(m.Stack, first.Stack)
                                            && m.X == first.X && m.Y == first.Y && m.Ret > 0);
        if (step is null) return;
        c.Count("leader_move_max · гипотеза «плюс цена шага»", expected == first.MoveLeft + step.Ret,
                $"{detail}, цена шага {step.Ret}");
    }

    /// <summary>Дословные строки разбора: сколько лидеров увидено, у кого стоит <c>DYNLEVEL</c>
    /// (план §3 п. 6 — урезание строки <c>xp</c>), у кого копится очередь выбора, кто носит навыки
    /// хода и обзора.</summary>
    public void Print()
    {
        var lords = _seen.Select(s => s.LordId).Where(l => l.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.Ordinal);
        c.Note($" лидеров в сохранениях: {_seen.Count} записей, {_seen.Select(s => s.UnitId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} разных; " +
               $"лорды: {string.Join(",", lords)}");
        c.Note($" пар соседних снимков одного лидера: {_pairs}; из них перезагрузка карты {_reloads}, " +
               $"игрок без лорда {_noLord}");
        c.Note($" из новых навыков поздних (MIN_LEVEL строго ниже уровня героя): {_late}");
        c.Note($" первых за день ходов отряда владельца в журналах с казной: {_firstMoves}; " +
               $"из них у лидера есть навык хода: {_pathfinders}");
        var dyn = _seen.Where(s => s.DynLevel).Select(s => $"{s.Save} {s.UnitId} ур. {s.Level}").Distinct().ToList();
        c.Note($" лидеров с DYNLEVEL: {dyn.Count}{(dyn.Count == 0 ? "" : " — " + string.Join("; ", dyn.Take(6)))}");
        var pending = _seen.Where(s => s.Pending > 0)
            .Select(s => $"{s.Save} {s.UnitId} ур. {s.Level} UPGCOUNT {s.Pending} навыков {s.Skills.Count}")
            .Distinct().ToList();
        c.Note($" снимков с отложенным выбором (UPGCOUNT > 0): {pending.Count}" +
               $"{(pending.Count == 0 ? "" : " — " + string.Join("; ", pending.Take(6)))}");
        foreach (var s in _seen.Where(s => s.Skills.Count > 0)
                     .GroupBy(s => s.UnitId, StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var last = s.Last();
            c.Note($" {last.UnitId} {last.TypeId} ур. {last.Level} лорд {last.LordId}" +
                   $"{(last.Human ? " (человек)" : " (ИИ)")}: {string.Join(",", last.Skills)}");
        }
    }

    /// <summary>Лидеры сохранения: по одному на отряд (<c>MidStack.LEADER_ID</c>), только категории
    /// <see cref="Experience.LeaderCategory"/>. Гарнизоны лидеров не держат, и <c>UPGCOUNT</c>
    /// живёт у отряда — поэтому обход идёт по отрядам, а не по юнитам.</summary>
    private IEnumerable<LeaderSeen> LeadersOf(string group, SaveAt save)
    {
        foreach (var stack in save.Map.Stacks.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!save.Map.Units.TryGetValue(stack.LeaderId, out var leader)) continue;
            if (book.CategoryOf(leader.TypeId) != Experience.LeaderCategory) continue;
            if (!book.TryGlobal(leader.TypeId, leader.Level, out var typeId, out int level)) continue;
            var player = save.Map.Players.GetValueOrDefault(stack.Owner);
            var skills = leader.Modifiers.Where(m => _skillIds.Contains(m)).ToList();
            yield return new LeaderSeen(group, Path.GetFileName(save.Path), save.Map.Turn, leader.Id, typeId,
                                        level, leader.Xp, player?.LordId ?? "", player?.IsHuman ?? false,
                                        stack.UpgradeCount, skills, leader.DynLevel);
        }
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
