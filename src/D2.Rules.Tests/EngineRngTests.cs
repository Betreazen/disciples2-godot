using System.Text.Json;
using D2.Data.Corpus;
using D2.Rules.Rng;
using Xunit.Abstractions;

[Trait("Category", "Corpus")]
public class EngineRngTests(ITestOutputHelper output)
{
    // Векторы получены моделью ../disciples-modding/tools/re/rng.py (реверс docs/re-rng.md,
    // подтверждён на 125 бросках пяти боёв). Индекс по умолчанию 47 — .data:0x838c24 в образе.
    [Fact] public void Next100_FromDefaultIndex_MatchesPythonModel()
    {
        var rng = new EngineRng();
        var draws = Enumerable.Range(0, 10).Select(_ => rng.Next(100)).ToArray();
        Assert.Equal(new[] { 61, 65, 60, 57, 97, 57, 72, 73, 25, 44 }, draws);
    }

    [Fact] public void Step_FromDefaultIndex_MatchesPythonModel()
    {
        var rng = new EngineRng();
        var steps = Enumerable.Range(0, 10).Select(_ => (int)rng.Step()).ToArray();
        Assert.Equal(new[] { 49761, 22365, 44560, 21457, 39097, 23157, 18572, 39773, 55325, 21544 }, steps);
    }

    [Fact] public void Table_Is250WordsWithKnownChecksum()
    {
        Assert.Equal(250, RngTableProbe.Length);
        Assert.Equal(15301, RngTableProbe.First);
        Assert.Equal(59743, RngTableProbe.Last);
        Assert.Equal(7891135, RngTableProbe.Sum);
    }

    [Fact] public void Next_MaxZero_ReturnsZeroWithoutStepping()
    {
        var rng = new EngineRng();
        Assert.Equal(0, rng.Next(0));
        Assert.Equal(EngineRng.DefaultIndex, rng.Index);
    }

    [Fact] public void Next_NegativeMax_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineRng().Next(-1));

    [Fact] public void Next_MaxAboveUInt16_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineRng().Next(0x10000));

    [Fact] public void Index_WrapsAt250()
    {
        var rng = new EngineRng(249);
        rng.Step();
        Assert.Equal(0, rng.Index);
    }

    [Fact] public void Constructor_TakesIndexModulo250() => Assert.Equal(47, new EngineRng(297).Index);

    [Fact] public void Constructor_NegativeIndex_WrapsIntoRange() => Assert.Equal(247, new EngineRng(-3).Index);

    /// <summary>Для каждого журнала (файл — один бой, по манифесту) перебирает состояние R250 по
    /// всей записанной последовательности бросков (EngineRng.Fit — порт tools/re/rng_search.py) и
    /// требует единственное, найденное решение. Где в журнале есть rng_index (схема v2.1) — сверяет
    /// его с индексом генератора СРАЗУ ПОСЛЕ первого броска (rng_index в журнале — состояние после
    /// оригинального вызова, docs/corpus-schema.md, «Индекс генератора»). Первая строка журнала,
    /// которая не парсится или не 'start', — битый файл (Assert.Fail); журнал старой схемы без
    /// battle_units.order (пробник старше probe 9a4cd40) читается строго и падает JsonException —
    /// это отличимо от битого файла тем, что 'start' у него разобрался, и считается 'legacy',
    /// а не ошибкой теста.
    ///
    /// <para><b>Пассивные прогоны (<c>passive-*</c>) из требования выведены — по той же причине,
    /// что и «не первый бой файла», и это не поблажка.</b> <see cref="EngineRng.Fit"/> перебирает
    /// 250 траекторий по <b>исходной</b> таблице; таблица R250 не сеется, а мутирует на каждом
    /// броске (docs/re-rng.md), и <c>reseed(timeGetTime)</c> при загрузке карты меняет только
    /// индекс. Пока за сеанс карта грузилась один раз, состояние остаётся на исходной траектории —
    /// именно поэтому Fit сходится у прогонов драйвера (<c>battle-*</c>, <c>map-*</c>): их первый
    /// бой начинается после ≈50 689 бросков. Пассивный прогон играет человек, он загружает
    /// сохранения по ходу дела, и ко второй загрузке таблица уже мутирована — точки нет в
    /// пространстве перебора по построению (первый бой <c>20260909-passive/passive-001.jsonl</c>
    /// начинается после 354 817 бросков, при лимите Fit 1,2 млн шагов решение не находится).
    /// Такие журналы считаются отдельно и печатаются с числами; непрерывность их траектории внутри
    /// боя проверяет С2 (<c>EngineRng.FindAhead</c>, 255 боёв из 302). Снимет событие
    /// <c>reseed</c> в зонде — тогда точку старта не придётся искать перебором.</para></summary>
    [Fact] public void Fit_StateIsRestoredFromStartAndSteps()
    {
        var truth = new EngineRng(17);
        truth.Advance(123_456);
        var probe = truth.Clone();
        var draws = Enumerable.Range(0, 12).Select(_ => (100, probe.Next(100))).ToList();

        var fitted = EngineRng.Fit(draws);

        Assert.NotNull(fitted);
        Assert.Equal((17, 123_456), (fitted!.Start, fitted.Steps));
        Assert.True(EngineRng.At(fitted.Start, fitted.Steps).Replay(draws));   // кеш подборов хранит только эти два числа
    }

    [Fact] public void EveryBattleDrawSequence_IsReproducedByFit()
    {
        Assert.True(CorpusFiles.Available, "нет корпуса — battle-*.jsonl не найден");
        int legacy = 0, checkedBattles = 0, reloaded = 0;

        foreach (var file in CorpusFiles.Journals())
        {
            CorpusEvent? first;
            try { first = JournalCache.Read(file).FirstOrDefault(); }
            catch (JsonException) { first = null; }

            if (first is not StartEvent start)
            {
                Assert.Fail($"{file}: первая строка не 'start' или не парсится — битый журнал");
                continue; // недостижимо (Assert.Fail бросает), но так явнее для читателя
            }

            IReadOnlyList<Battle> battles;
            try
            {
                battles = JournalCache.Battles(file);
            }
            catch (JsonException)
            {
                legacy++;
                output.WriteLine($"{Path.GetFileName(file)}: legacy (probe={start.Probe}, схема без order)");
                continue;
            }

            bool firstBattleOfFile = true;
            foreach (var battle in battles)
            {
                var rngEvents = battle.Events.OfType<RngEvent>().Where(e => !e.Std)
                    .OrderBy(e => e.N).ToList();
                if (rngEvents.Count == 0) continue;

                // Подбор по ИСХОДНОЙ таблице возможен только для первого боя процесса: дальше в серии
                // «одна игра — много боёв» карта перезагружается, reseed(timeGetTime) ставит новый
                // индекс, а таблица к этому моменту уже мутирована предыдущим боем (она не сеется —
                // docs/re-rng.md), поэтому пространство перебора Fit такую точку не содержит.
                // Непрерывность траектории внутри процесса проверяет C2HarnessTests (FindAhead).
                if (!firstBattleOfFile) continue;
                firstBattleOfFile = false;

                var draws = rngEvents.Select(e => (Max: (int)e.Max, R: (int)e.R)).ToList();
                var fitted = EngineRng.Fit(draws);
                if (fitted is null && Path.GetFileName(file).StartsWith("passive-", StringComparison.Ordinal))
                {   // человек загружал сохранения по ходу партии — таблица мутирована, см. описание
                    reloaded++;
                    output.WriteLine($"{Path.GetFileName(file)}: траектория не подобрана — " +
                                     $"перед боем {rngEvents[0].N} бросков, в бою {draws.Count} (пассивный прогон)");
                    continue;
                }
                Assert.NotNull(fitted);
                checkedBattles++;

                int startIndex = ((fitted!.Index - fitted.Steps) % EngineRng.Size + EngineRng.Size) % EngineRng.Size;
                output.WriteLine($"{Path.GetFileName(file)}: index={startIndex} шагов={fitted.Steps} бросков={draws.Count}");

                if (rngEvents[0].RngIndex is long idx0)
                {
                    fitted.Next(draws[0].Max); // rng_index в журнале — состояние ПОСЛЕ вызова
                    Assert.Equal((int)idx0, fitted.Index);
                }
            }
        }

        output.WriteLine($"подобрано траекторий {checkedBattles}, legacy {legacy}, " +
                         $"пассивных прогонов с мутированной таблицей {reloaded}");
        Assert.True(checkedBattles > 0, $"ни один бой не проверен (legacy={legacy})");
    }

    // Схема 6.2: строка rng_seed несёт индекс и 250 слов таблицы — состояние без подбора.
    [Fact] public void FromSeed_MutatedTableAndIndex_ContinuesLikeTheSource()
    {
        var source = new EngineRng(123);
        source.Advance(5_000);
        var table = source.TableSnapshot();
        var seeded = EngineRng.FromSeed(table, source.Index);
        table[0] ^= 0xFFFF; // снимок - копия: правка массива генератор не трогает

        var clone = source.Clone();
        var expected = Enumerable.Range(0, 30).Select(k => clone.Next(2 + k % 7)).ToArray();
        Assert.Equal(expected, Enumerable.Range(0, 30).Select(k => seeded.Next(2 + k % 7)).ToArray());
        Assert.Equal(30, seeded.Steps); // шаги считаются от снимка
    }

    [Fact] public void FromSeed_IndexOutOfRangeNormalized_WrongTableThrows()
    {
        var table = new EngineRng().TableSnapshot();
        Assert.Equal(new EngineRng(47).Next(100), EngineRng.FromSeed(table, 297).Next(100));
        Assert.Throws<ArgumentException>(() => EngineRng.FromSeed(new ushort[249], 0));
    }
}
