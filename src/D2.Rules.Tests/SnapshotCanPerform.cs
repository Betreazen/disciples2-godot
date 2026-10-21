using D2.Data.Corpus;
using D2.Rules.Battle;

/// <summary><see cref="Attacks.CanPerform"/> на состоянии цели из снимка журнала — общая часть харнессов С1 и С2
/// для классов, чьё «нельзя» зависит от статусов цели (16.10.2026, третья партия оригинала): разбить броню
/// (снятая броня, укрепление), понизить уровень (тип цели), замедлить (статусы 2, 3, 13), ослабить (статусы 2, 3, 10, 11).
/// Отступление (бит 29) подставляется для всех.</summary>
internal static class SnapshotCanPerform
{
    /// <summary>Классы, у которых <c>canPerform</c> смотрит на снимок цели сверх отступления.</summary>
    public static readonly AttackClass[] StateClasses = [AttackClass.Shatter, AttackClass.DrainLevel, AttackClass.LowerInitiative,
                                                          AttackClass.LowerDamage];

    /// <summary>Поля юнита модели на время вопроса берутся из снимка и возвращаются как были. Тип — тот, что у
    /// юнита модели (журнал называет текущий в <c>battle_units</c>, С1 его подставляет).</summary>
    public static bool Of(BattleUnit attacker, AttackType attack, BattleUnit target, UnitState st)
    {
        var saved = (target.Paralyzed, target.Petrified, target.Retreating, target.LowerInitiativeLevel,
                     target.ShatteredArmor, target.FortificationArmor, target.LowerDamageLevel);
        target.Paralyzed = Bit(st.St, 2); target.Petrified = Bit(st.St, 3); target.Retreating = Bit(st.St, 29);
        target.LowerInitiativeLevel = Bit(st.St, 13) ? 1 : 0;
        target.LowerDamageLevel = Bit(st.St, 11) ? 2 : Bit(st.St, 10) ? 1 : 0;
        target.ShatteredArmor = (int)st.Shattered; target.FortificationArmor = (int)st.Fort;
        try { return Attacks.CanPerform(attacker, attack, target); }
        finally
        {
            (target.Paralyzed, target.Petrified, target.Retreating, target.LowerInitiativeLevel,
             target.ShatteredArmor, target.FortificationArmor, target.LowerDamageLevel) = saved;
        }
    }

    public static bool Bit(long st, int bit) => (st >> bit & 1) != 0;
}
