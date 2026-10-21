using D2.Data.Diagnostics;

namespace D2.Data.Corpus;

// Схема 7.3 (probeav.cpp, probeanim.cpp, probecov.cpp): звук, музыка, ролики, анимации карты и боя, курсор, кадры — те же
// строки пишет зонд порта (AvTrace), сверка — AvJournal; покрытие кода — строка coverage и файл <журнал>.cov.txt.
// docs/corpus-schema.md, раздел «Схема 7.3».

/// <summary>Строка звука, музыки, ролика или анимации (7.3): вид — <see cref="AvEvent.Ev"/>, поля — как в журнале
/// (<see cref="AvJournal.Events"/>). Поля одни у оригинала и порта, поэтому запись общая, а не по виду.</summary>
public sealed record AvProbeEvent(long Seq, long Tid, AvEvent Av) : CorpusEvent(Seq, Tid);

/// <summary><c>coverage</c> (7.3) — точки покрытия поставлены (<c>probe/coverage.txt</c>): сколько начал функций стоит
/// (<see cref="Armed"/>), сколько в списке (<see cref="Listed"/>) и пропущено (вне секции кода, уже <c>0xCC</c>, неразборчиво),
/// имя файла покрытия рядом с журналом.</summary>
public sealed record CoverageEvent(long Seq, long Tid, long T, long Armed, long Listed, long Skipped, string File)
    : CorpusEvent(Seq, Tid);
