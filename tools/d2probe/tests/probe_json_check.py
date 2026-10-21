#!/usr/bin/env python3
r"""Probe journal check: every line must be a JSON object with "ev", "seq" and "tid".

"seq" is taken before the original engine call, so nested events (computeDamage calls
computeArmor and friends) carry a bigger seq while their line lands earlier. File order is
therefore NOT sorted by seq; what must hold is that seq is a monotonic counter, i.e. every
number appears exactly once.

Gaps in seq are normal, not a symptom: a seq is taken by every hooked call, but rng/rng_std
outside a battle is deliberately not journaled (README, "rng only while the battle is
active"), and in a real run that is tens of thousands of numbers. The gap count is printed as
information; a duplicate seq, which really would mean the counter broke, is an error.

Battle snapshots must carry the fields the corpus is built from: "units" wherever a state
snapshot is taken, plus "order" for a round (the other three events that write "order" were
added later, so a journal from an older probe build has none - not required here). No line
may contain a "\r": the journal is opened in binary mode exactly so that the CRT text mode
does not put one inside the data.

Usage: python probe_json_check.py <file.jsonl>
Prints per-event counters and the line total; exit code 1 if anything is broken.
Output is ASCII on purpose: the game console here is cp866.
"""
import collections
import json
import sys

# Events that carry a state snapshot, and the list fields each of them must have.
REQUIRED_FIELDS = {
    "round": ("units", "order"),
    "turn_before": ("units",),
    "turn_after": ("units",),
    "battle_start": ("units",),
    "battle_units": ("units",),
    "battle_end": ("units",),
}


def check(path):
    counts = collections.Counter()
    errors = []
    seen = {}
    total = 0

    with open(path, encoding="utf-8", newline="") as f:
        for number, line in enumerate(f, 1):
            if "\r" in line:
                errors.append(f"{path}:{number}: carriage return in line, journal must be binary")
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
            counts[event["ev"]] += 1
            for field in REQUIRED_FIELDS.get(event["ev"], ()):
                if not isinstance(event.get(field), list):
                    errors.append(f"{path}:{number}: {event['ev']} without a '{field}' list")
            if "tid" not in event:
                errors.append(f"{path}:{number}: no 'tid' field")
            seq = event.get("seq")
            if not isinstance(seq, int):
                errors.append(f"{path}:{number}: no integer 'seq' field")
            elif seq in seen:
                errors.append(f"{path}:{number}: seq {seq} already used on line {seen[seq]}")
            else:
                seen[seq] = number

    for ev, count in sorted(counts.items()):
        print(f"{ev}: {count}")
    print(f"lines: {total}")
    if seen:
        low, high = min(seen), max(seen)
        print(f"seq: {low}..{high}, gaps: {high - low + 1 - len(seen)} (rng outside battle)")
    for error in errors:
        print(error, file=sys.stderr)
    print(f"errors: {len(errors)}")
    return not errors


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    return 0 if check(sys.argv[1]) else 1


if __name__ == "__main__":
    sys.exit(main())
