namespace D2.Data.Corpus;

/// <summary>One <c>battle_start</c>..<c>battle_end</c> span from a probe journal, in file order
/// re-sorted by <see cref="CorpusEvent.Seq"/> (call order) via <see cref="CorpusReader.Battles"/>.
/// <see cref="End"/> is null when the journal was truncated before the battle's <c>battle_end</c>
/// line — e.g. the game crashed mid-battle, or this is the last battle in a still-running probe.</summary>
public sealed record Battle(BattleStartEvent Start, BattleEndEvent? End, IReadOnlyList<CorpusEvent> Events)
{
    /// <summary>Unit identity from this battle's <c>battle_units</c> line (schema v2 only — see
    /// <see cref="BattleUnitsEvent"/>), or null when the journal has none: a v1 journal predating
    /// that event, or a battle truncated before its first <c>battleTurn</c>.</summary>
    public IReadOnlyList<UnitIdentity>? Units => Events.OfType<BattleUnitsEvent>().FirstOrDefault()?.Units;
}
