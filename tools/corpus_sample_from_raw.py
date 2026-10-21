#!/usr/bin/env python3
r"""Cut a real one-battle sample (schema v2, see docs/corpus-schema.md) out of a raw probe
journal captured with the *pre-fix* DLL (before commit 7c11f9c in tools/d2probe). The raw
journal still carries the old shape: empty battle slots written as literal id "INVALID-ID"
(instead of being omitted), and a single "battle_start" line that already carries unit
identity (type/level/hp_max) instead of the identity being split into a separate
"battle_units" line. This script does the v1 -> v2 conversion by hand, once, to produce
corpus/sample/one_battle.jsonl from corpus/first-battle-raw.jsonl (the raw file itself is
53k+ lines and is not committed -- see corpus/.gitignore).

What it does, in order:
  1. Keep the "start" line as-is, first.
  2. Select the battle window: every event with seq in [first "round".seq, "battle_end".seq]
     (inclusive), in original file order -- NOT re-sorted by seq. Preserving file order also
     preserves the real quirk the schema documents: a nested call (dmg calling dmg_buffs/
     armor/power, or the engine's rng calls) writes its line before the outer event whose seq
     is smaller. Re-sorting would launder that away and invalidate
     CorpusReaderTests.Read_ReturnsFileOrderNotSeqOrder.
  3. Drop every unit whose "id" is "INVALID-ID" from every event's "units"/"order" list (the
     bug fixed in probehooks.cpp -- empty slots should never have been emitted at all).
  4. Split the old "battle_start" (which carried type/level/hp_max) into two v2 events:
       - a new "battle_start" with no identity fields, seq'd to sit right before the first
         "round" (probe now writes battle_start in beforeBattleRound, before objectMap is
         available -- see docs/corpus-schema.md);
       - a "battle_units" carrying only the identity (id/type/level/hp_max), at the old
         battle_start's position.
     Every other selected event's seq is shifted by +1 to free up the first round's original
     seq value for the new battle_start -- this keeps seq globally unique (required by
     tools/d2probe/tests/probe_json_check.py) without reordering anything.
  5. Drop any "rng"/"rng_std" line whose (post-shift) seq falls outside
     [new battle_start.seq, battle_end.seq] -- defensive; the selected window already implies
     this, but the schema note ("rng only while a battle is active") is enforced explicitly
     here rather than assumed.

Usage: python tools/corpus_sample_from_raw.py <in.jsonl> <out.jsonl>
"""
import json
import sys

IDENTITY_KEEP = ("id", "hp", "xp", "st", "st2", "flags", "shattered", "fort")
UNIT_ID_FIELDS = ("type", "level", "hp_max")


def strip_invalid_id(events):
    for e in events:
        if "units" in e:
            e["units"] = [u for u in e["units"] if u.get("id") != "INVALID-ID"]
        if "order" in e:
            e["order"] = [o for o in e["order"] if o.get("id") != "INVALID-ID"]


def new_battle_start(old_bs, seq):
    return {
        "seq": seq,
        "tid": old_bs["tid"],
        "ev": "battle_start",
        "attacker_group": old_bs["attacker_group"],
        "defender_group": old_bs["defender_group"],
        "attacker_player": old_bs["attacker_player"],
        "defender_player": old_bs["defender_player"],
        "round": old_bs["round"],
        "units": [{k: u[k] for k in IDENTITY_KEEP} for u in old_bs["units"]],
    }


def battle_units_from(old_bs, seq):
    return {
        "seq": seq,
        "tid": old_bs["tid"],
        "ev": "battle_units",
        "units": [
            {"id": u["id"], **{k: u[k] for k in UNIT_ID_FIELDS if k in u}}
            for u in old_bs["units"]
        ],
    }


def transform(lines):
    events = [json.loads(line) for line in lines if line.strip()]
    if not events or events[0]["ev"] != "start":
        raise ValueError('first non-blank line must be "ev":"start"')
    start_line, body = events[0], events[1:]

    round_seqs = [e["seq"] for e in body if e["ev"] == "round"]
    if not round_seqs:
        raise ValueError('no "round" event found')
    first_round_seq = min(round_seqs)

    battle_ends = [e for e in body if e["ev"] == "battle_end"]
    if len(battle_ends) != 1:
        raise ValueError(f'expected exactly one "battle_end", found {len(battle_ends)}')
    battle_end_seq = battle_ends[0]["seq"]

    selected = [e for e in body if first_round_seq <= e["seq"] <= battle_end_seq]
    strip_invalid_id(selected)

    old_bs_matches = [e for e in selected if e["ev"] == "battle_start"]
    if len(old_bs_matches) != 1:
        raise ValueError(f'expected exactly one "battle_start" in the window, found {len(old_bs_matches)}')
    old_bs = old_bs_matches[0]
    old_bs_seq = old_bs["seq"]
    old_bs_index = next(i for i, e in enumerate(selected) if e is old_bs)

    for e in selected:
        e["seq"] += 1

    bs = new_battle_start(old_bs, first_round_seq)
    selected[old_bs_index] = battle_units_from(old_bs, old_bs_seq + 1)

    lo, hi = bs["seq"], battle_end_seq + 1
    selected = [
        e for e in selected
        if e["ev"] not in ("rng", "rng_std") or lo <= e["seq"] <= hi
    ]

    return [start_line, bs] + selected


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    in_path, out_path = sys.argv[1], sys.argv[2]

    with open(in_path, encoding="utf-8") as f:
        out_events = transform(f)

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        for e in out_events:
            f.write(json.dumps(e, ensure_ascii=False, separators=(",", ":")))
            f.write("\n")

    print(f"{out_path}: {len(out_events)} lines")
    return 0


if __name__ == "__main__":
    sys.exit(main())
