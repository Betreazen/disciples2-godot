using D2.Rules.Map;

namespace D2.Data.Corpus;

// События схем 5–5.3, которых не разбирала задача 1 Ф7: вор, дипломатия, предметы, ввод и шесть причин
// трат казны (docs/corpus-schema.md). Все пишутся на каждый вызов и несут T — миллисекунды от старта
// прогона (probe.cpp:244 кладёт "t" в каждую строку с версии 5).

/// <summary><c>thief_roll</c> — бросок вора (<c>0x443067</c>, порог — хук <c>0x5d31a1</c>). Две формы
/// строки. Схема 5.1: <see cref="Action"/> (<c>LNobleActionCat::id</c>), <see cref="Target"/> (цель —
/// отряд или укрепление), кубики <see cref="Dice1"/>/<see cref="Dice2"/> и порог <see cref="Chance"/>.
/// Схема 5: вместо <c>action</c>/<c>target</c> поля <c>id1</c>/<c>stack</c>, кубиков и порога нет.
/// <c>id1</c> схемы 5 — первые байты vftable категории (во всех 14 бросках первой большой партии
/// <c>G001CRedb4</c>), поэтому <see cref="Action"/> там null, а не число; <c>stack</c> — та же цель,
/// что <c>target</c> схемы 5.1 (ранг 1, разбор <c>0x5d31a1</c> в corpus-schema.md «Бросок вора»).
/// <c>-1</c> в кубике или пороге схемы 5.1 — «значение не досталось зонду», а не число.</summary>
public sealed record ThiefRollEvent(
    long Seq, long Tid, long T, long? Action, string Target, long? Dice1, long? Dice2, long? Chance,
    long RngBefore, long RngAfter, bool Ret) : CorpusEvent(Seq, Tid);

/// <summary>Пара рас в <c>CMidDiplomacy</c> глазами <c>relation</c>: <see cref="Cur"/> — биты 1…7,
/// <see cref="Prev"/> — 8…14, <see cref="Ally"/> — бит 0, <see cref="EternalWar"/> — бит 30,
/// <see cref="Raw"/> — слово <c>RELATION</c> целиком (ранг 1: hook <c>probediplo.cpp</c>, план Ф7 §1.8).</summary>
public sealed record RelationWord(long Cur, long Prev, bool Ally, bool EternalWar, long Raw);

/// <summary>Элемент <c>changed</c>: пара рас, чьё слово <c>RELATION</c> за вызов изменилось.
/// <see cref="Before"/> = <c>-1</c> — пары до вызова не было.</summary>
public sealed record RelationChange(long Race1, long Race2, long Before, long After);

/// <summary><c>relation</c> (схема 5.2) — мутатор <c>changeRelations</c> (<c>0x5d9366</c>). Причина — по
/// <see cref="RetAddr"/>/<see cref="Cause"/>; <see cref="Value"/> — аргумент до обрезки в [0, 100];
/// <see cref="Before"/>/<see cref="After"/> — основная пара, null, если её нет; третьи стороны — элементы
/// <see cref="Changed"/> (corpus-schema.md «Дипломатические отношения»).</summary>
public sealed record RelationEvent(
    long Seq, long Tid, long T, string A, string B, long RaceA, long RaceB, long Value, bool Absolute,
    string RetAddr, string Cause, RelationWord? Before, RelationWord? After, IReadOnlyList<RelationChange> Changed,
    bool Ret) : CorpusEvent(Seq, Tid);

/// <summary><c>item_equip</c> (<c>equipItem</c> <c>0x5e741c</c>) и <c>item_unequip</c>
/// (<c>unEquipItem</c> <c>0x5e746d</c>) одной записью: <see cref="Equip"/> различает. <see cref="Item"/> —
/// экземпляр <c>IM</c>, у снятия его в строке нет (null). <see cref="Apply"/> = false — это вопрос
/// «можно ли», а не действие (407 таких строк надевания и 48 снятия в корпусе 11.09); факт — только
/// <c>Apply = true</c>. <see cref="Slot"/> — номер слота отряда 0…6 (план Ф7 §1.5).</summary>
public sealed record ItemEquipEvent(
    long Seq, long Tid, long T, bool Equip, string Stack, string? Item, long Slot, bool Apply, bool Ret)
    : CorpusEvent(Seq, Tid);

/// <summary><c>input</c> — нажатие кнопки мыши или клавиши из насоса сообщений
/// (<c>PeekMessageA</c>/<c>GetMessageA</c>). <see cref="Vk"/> — только у <c>kind = "key"</c>, иначе
/// <c>-1</c>; <see cref="Button"/> — у мыши, иначе <c>-1</c>; <see cref="X"/>/<see cref="Y"/> — клиентские
/// координаты (<c>-1</c>, если окно не определилось), <see cref="Sx"/>/<see cref="Sy"/> — экранные.</summary>
public sealed record InputEvent(
    long Seq, long Tid, long T, string Kind, long Msg, long Vk, long Button, long X, long Y, long Sx, long Sy)
    : CorpusEvent(Seq, Tid);

/// <summary>Причина траты казны, схема 5.3 (<c>probespend.cpp</c>, <c>emitSpend</c>): казна платящего
/// игрока до и после оригинала и <see cref="SeqEnd"/> — номер сразу после возврата. Все вложенные строки
/// вызова (<c>bank</c>, <c>stack_move</c>, ключевые проверки) лежат строго между <c>Seq</c> и
/// <see cref="SeqEnd"/>; сопоставлять с <c>bank</c> — по игроку и этому окну, а не по <c>seq + 1</c>.
/// <see cref="Ret"/> = false — движок отказал, строки <c>bank</c> нет.</summary>
public abstract record SpendEvent(
    long Seq, long Tid, long T, string Player, Bank Before, Bank After, long SeqEnd, bool Ret)
    : CorpusEvent(Seq, Tid)
{
    /// <summary>Цена — <c>before − after</c> (corpus-schema.md «Причины трат казны»).</summary>
    public Bank Price => Before - After;
}

/// <summary><c>hire_unit</c> — найм юнита в группу (<c>0x5d565f</c>): гарнизон столицы или отряд в ней.
/// <see cref="Position"/> — слот группы.</summary>
public sealed record HireUnitEvent(
    long Seq, long Tid, long T, string Player, string UnitType, string Group, long Position,
    Bank Before, Bank After, long SeqEnd, bool Ret) : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);

/// <summary><c>hire_leader</c> — найм лидера у укрепления (<c>0x5d6957</c>).</summary>
public sealed record HireLeaderEvent(
    long Seq, long Tid, long T, string Player, string Fort, string UnitType,
    Bank Before, Bank After, long SeqEnd, bool Ret) : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);

/// <summary><c>rod_drop</c> — постановка жезла (<c>CEffectDropRod::apply</c> <c>0x437606</c>); платит
/// владелец отряда, клетка <c>-1</c> — не прочиталась.</summary>
public sealed record RodDropEvent(
    long Seq, long Tid, long T, string Player, string Stack, long X, long Y,
    Bank Before, Bank After, long SeqEnd, bool Ret) : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);

/// <summary><c>spell_cast</c> — заклинание на карте (<c>0x5dc2d6</c>). <see cref="SpellBankBefore"/>/
/// <see cref="SpellBankAfter"/> — второй кошелёк <c>CMidPlayer::spellBank</c>.</summary>
public sealed record SpellCastEvent(
    long Seq, long Tid, long T, string Player, string Spell, long X, long Y, bool UseSpellBank, bool Free,
    Bank SpellBankBefore, Bank SpellBankAfter, Bank Before, Bank After, long SeqEnd, bool Ret)
    : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);

/// <summary><c>spell_research</c> — изучение заклинания в столице (<c>0x5dc1d7</c>).</summary>
public sealed record SpellResearchEvent(
    long Seq, long Tid, long T, string Player, string Spell, bool Free,
    Bank Before, Bank After, long SeqEnd, bool Ret) : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);

/// <summary><c>wand_cast</c> — посох или свиток на карте (<c>0x5dc4d6</c>); <see cref="Item"/> и клетка
/// сняты до вызова (оригинал может уничтожить предмет).</summary>
public sealed record WandCastEvent(
    long Seq, long Tid, long T, string Player, string Target, string Owner, string Item, long X, long Y,
    Bank Before, Bank After, long SeqEnd, bool Ret) : SpendEvent(Seq, Tid, T, Player, Before, After, SeqEnd, Ret);
