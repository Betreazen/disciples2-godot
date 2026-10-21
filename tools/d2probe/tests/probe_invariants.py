#!/usr/bin/env python3
r"""Probe journal invariants: battle-shape checks that go beyond "is it valid JSONL"
(that part is probe_json_check.py; see load_events() below, which mirrors its per-line
validation because this script needs the parsed events, not just pass/fail counts).

Eight checks, one line of output each. Per the repo rule "an exploratory counter with a
budget hides the rare case" every check also prints an unbounded total next to whatever it
filtered on.

  1. every non-empty line is a JSON object with "ev"                      -- exit-worthy
  2. there is a battle_start and a battle_end, battle_end after           -- exit-worthy
     battle_start by seq (paired in seq order)
  3. for every "miss", rng_after >= rng_before                            -- informational
     (also prints the share where rng_after > rng_before, strictly)
  4. HP growth between a turn_before/turn_after pair of the same turn     -- informational
     (healing is legitimate, so this is a count, not an error)
  5. "order" (in "round", "turn_before", "turn_after", "battle_units") has  -- exit-worthy
     no "INVALID-ID" entry, is duplicate-free, and every id in it is also in
     that event's own "units". An "order" that is empty everywhere fails too:
     an empty field proves nothing, so it must not read as a pass
  6. count(miss) <= count(power)                                          -- informational
  7. totals per "ev" plus the total line count                            -- always printed
  8. if any "rng" line carries "rng_index" (schema v2.1, Gog only), replay -- informational
     it with the R250 model (rng_model.py, ported from disciples-modding's
     tools/re/rng.py): for every pair of "rng" lines adjacent by "n" (no gap),
     feed the first one's rng_index into the model and check its next(max)
     equals the second one's "r". Prints reproduced=K/N.

Usage: python probe_invariants.py <file.jsonl>
Exit code 1 only if check 1, 2 or 5 found a problem (per the brief -- 3, 4, 6, 8 are
diagnostic: a mismatch there is "new mechanic or probe bug, write it up", not "reject
the journal").
"""
import collections
import json
import sys

import rng_model


def load_events(path):
    """Mirrors probe_json_check.check()'s per-line validation, but returns the parsed
    events (each tagged with its 1-based line number) instead of just counts, since every
    check below needs the actual fields."""
    events = []
    errors = []
    total = 0
    with open(path, encoding="utf-8", newline="") as f:
        for number, line in enumerate(f, 1):
            if not line.strip():
                continue
            total += 1
            try:
                event = json.loads(line)
            except ValueError as e:
                errors.append(f"{path}:{number}: not JSON: {e}")
                continue
            if not isinstance(event, dict) or "ev" not in event:
                errors.append(f"{path}:{number}: no 'ev' field")
                continue
            event["_line"] = number
            events.append(event)
    return events, errors, total


def check_battle_bounds(events):
    starts = sorted(e["seq"] for e in events if e["ev"] == "battle_start" and "seq" in e)
    ends = sorted(e["seq"] for e in events if e["ev"] == "battle_end" and "seq" in e)
    # zip() silently drops the unpaired tail if the counts differ -- a truncated journal (the
    # game crashed mid-battle, or the last battle_end never landed) is exactly what this check
    # exists to catch, so an unequal count is a violation on its own, not just the pairs zip
    # happens to form.
    unequal_counts = len(starts) != len(ends)
    out_of_order = sum(1 for bs, be in zip(starts, ends) if be < bs)
    violations = out_of_order + (1 if unequal_counts else 0)
    ok = bool(starts) and bool(ends) and violations == 0
    print(f"[2] battle_start/battle_end: battle_start={len(starts)} battle_end={len(ends)} "
          f"unequal_counts={unequal_counts} out_of_order_pairs={out_of_order} ok={ok}")
    return ok


def check_miss_rng(events):
    misses = [e for e in events if e["ev"] == "miss" and "rng_before" in e and "rng_after" in e]
    violations = [e for e in misses if e["rng_after"] < e["rng_before"]]
    strict = [e for e in misses if e["rng_after"] > e["rng_before"]]
    share = (len(strict) / len(misses)) if misses else 0.0
    print(f"[3] miss rng_after>=rng_before: miss={len(misses)} violations={len(violations)} "
          f"strict_growth={len(strict)} ({share:.1%})")


def by_seq(events):
    """Events in call order (by "seq"), not file order -- a nested call (rng inside dmg
    inside a turn) gets a bigger seq but its line lands earlier in the file (see README,
    the "seq" field), so file order alone would pair the wrong turn_before/turn_after.
    Warns once if seq repeats: that breaks "seq order" as a stand-in for call order."""
    with_seq = [e for e in events if isinstance(e.get("seq"), int)]
    seqs = [e["seq"] for e in with_seq]
    if len(seqs) != len(set(seqs)):
        print(f"warning: {len(seqs) - len(set(seqs))} duplicate seq value(s) -- "
              "turn pairing below may be off", file=sys.stderr)
    return sorted(with_seq, key=lambda e: e["seq"])


def check_hp_growth(events):
    """Pairs each turn_before with the next turn_after carrying the same "unit" id, and
    counts units whose hp is higher in the "after" snapshot than in the "before" one.
    Informational: healing between the two is a legitimate reason for growth."""
    pending = {}
    growths = 0
    pairs = 0
    for e in by_seq(events):
        if e["ev"] == "turn_before" and "unit" in e:
            pending[e["unit"]] = e
        elif e["ev"] == "turn_after" and "unit" in e and e["unit"] in pending:
            before = pending.pop(e["unit"])
            before_hp = {u["id"]: u["hp"] for u in before.get("units", []) if "id" in u}
            after_hp = {u["id"]: u["hp"] for u in e.get("units", []) if "id" in u}
            growths += sum(1 for uid, hp in after_hp.items()
                           if uid in before_hp and hp > before_hp[uid])
            pairs += 1
    print(f"[4] hp growth turn_before->turn_after (informational, healing is legitimate): "
          f"pairs={pairs} growth_occurrences={growths}")


# The probe writes "order" from four events, because the engine fills turnsOrder at a point of
# its own and a hook that runs before it sees an all-invalid array (docs/corpus-schema.md).
ORDER_EVENTS = ("round", "turn_before", "turn_after", "battle_units")


def check_order(events):
    """Checks "order" wherever it is written, and refuses to pass on an "order" that is empty
    in every single event that carries it: the repo rule is that an empty log proves nothing,
    and "no violations found in nothing" is exactly the vacuum it warns about."""
    violations = 0
    invalid_id_entries = 0
    carriers = 0
    empty = 0
    for e in events:
        if e["ev"] not in ORDER_EVENTS or "order" not in e:
            continue
        carriers += 1
        order_ids = [o["id"] for o in e["order"] if "id" in o]
        if not order_ids:
            empty += 1
            continue
        unit_ids = {u["id"] for u in e.get("units", []) if "id" in u}
        # "INVALID-ID" in order means the empty-slot filter missed a reset (unfilled) turnsOrder
        # entry -- see isUnoccupied()/probehooks.cpp. Caught below too (an invalid id is never a
        # real unit id, so it also fails the units-subset check), but called out on its own since
        # it is the exact symptom the 05.09 bug produced.
        if any(oid == "INVALID-ID" for oid in order_ids):
            invalid_id_entries += 1
            violations += 1
            continue
        if len(order_ids) != len(set(order_ids)):
            violations += 1
            continue
        if not set(order_ids) <= unit_ids:
            violations += 1
    all_empty = carriers > 0 and empty == carriers
    ok = violations == 0 and not all_empty
    print(f"[5] order in {'/'.join(ORDER_EVENTS)}: events={carriers} empty={empty} "
          f"violations={violations} invalid_id_entries={invalid_id_entries} ok={ok}")
    if all_empty:
        print("[5] FAIL: every event carrying 'order' has it empty -- the engine had not built "
              "turnsOrder at any of the hooks yet, so this check verified nothing", file=sys.stderr)
    elif empty:
        print(f"[5] warning: {empty} of {carriers} events have an empty 'order' -- expected at "
              "the hooks that run before the engine builds turnsOrder", file=sys.stderr)
    return ok


def check_miss_le_power(events):
    miss = sum(1 for e in events if e["ev"] == "miss")
    power = sum(1 for e in events if e["ev"] == "power")
    print(f"[6] miss<=power (informational): miss={miss} power={power} ok={miss <= power}")


def check_rng_index(events):
    """Replays "rng" draws with the R250 model from a recorded rng_index (schema v2.1,
    Gog builds only -- docs/corpus-schema.md, docs/re-rng.md in disciples-modding).
    For every pair of "rng" lines adjacent by "n" (n2 == n1 + 1, i.e. no draw between them
    went unlogged), the model seeded with the first line's rng_index must produce the
    second line's "r" for its "max". Informational: absence of rng_index (any non-Gog
    build, or a journal older than v2.1) is not a failure, just nothing to replay."""
    draws = sorted(
        (e["n"], e["max"], e["r"], e.get("rng_index"))
        for e in events
        if e["ev"] == "rng" and "n" in e and "max" in e and "r" in e)

    reproduced = 0
    attempted = 0
    for (n1, _max1, _r1, index1), (n2, max2, r2, _index2) in zip(draws, draws[1:]):
        if index1 is None or n2 != n1 + 1:
            continue
        attempted += 1
        if rng_model.EngineRng(index1).next(max2) == r2:
            reproduced += 1

    print(f"[8] rng_index replay (informational): rng_index_present="
          f"{sum(1 for *_, i in draws if i is not None)}/{len(draws)} "
          f"reproduced={reproduced}/{attempted}")


def check(path):
    events, load_errors, total = load_events(path)
    valid_ok = not load_errors
    print(f"[1] valid JSON with 'ev': lines={total} errors={len(load_errors)} ok={valid_ok}")
    for e in load_errors:
        print(e, file=sys.stderr)

    battle_ok = check_battle_bounds(events)
    check_miss_rng(events)
    check_hp_growth(events)
    order_ok = check_order(events)
    check_miss_le_power(events)

    counts = collections.Counter(e["ev"] for e in events)
    parts = " ".join(f"{ev}={n}" for ev, n in sorted(counts.items()))
    print(f"[7] totals: {parts} lines={total}")

    check_rng_index(events)

    return valid_ok and battle_ok and order_ok


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    return 0 if check(sys.argv[1]) else 1


if __name__ == "__main__":
    sys.exit(main())
