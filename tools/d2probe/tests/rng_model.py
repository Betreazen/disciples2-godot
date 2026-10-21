"""Disciples 2 engine RNG model (R250), used by probe_invariants.py check [8] to replay
"rng" draws from a recorded rng_index.

Provenance: this file is part of disciples-godot, licensed GPLv3 (see LICENSE at the repo
root). The algorithm and the 250-word constant table below were reverse-engineered and
ported here from tools/re/rng.py in the sibling disciples-modding repository (MIT-licensed
reverse-engineering research, not GPL code) -- see docs/re-rng.md there for the full
disassembly writeup. Only the pieces this check needs are copied: the table, the R250
step, and next(); StdRng (CRT rand(), a separate "rng_std" counter this check does not
touch) is intentionally left out.

Reversed from Discipl2.exe (GOG 3.01):
  0x54851f  generateRandomNumber(unsigned max)  -- wrapper, critical section 0x836fd8
  0x67819c  one R250 step: table[i] ^= table[i-147]; i = (i+1) % 250
  0x6781dd  core: reject the tail of the range, then remainder
  0x7b4978  state table, 250 dwords (16-bit values), constant in .data
  0x838c24  index into that table -- this is "rng_index" in the probe journal
"""

LAG = 147
SIZE = 250

# .data:0x7b4978, 250 dwords (16-bit values)
TABLE = [
    15301, 57764, 10921, 56345, 19316, 43154, 54727, 49252, 32360, 49582,
    26124, 25833, 34404, 11030, 26232, 13965, 16051, 63635, 55860, 5184,
    15931, 39782, 16845, 11371, 38624, 10328, 9139, 1684, 48668, 59388,
    13297, 1364, 56028, 15687, 63279, 27771, 5277, 44628, 31973, 46977,
    16327, 23408, 36065, 52272, 33610, 61549, 58364, 3472, 21367, 56357,
    56345, 54035, 7712, 55884, 39774, 10241, 50164, 47995, 1718, 46887,
    47892, 6010, 29575, 54972, 30458, 21966, 54449, 10387, 4492, 644,
    57031, 41607, 61820, 54588, 40849, 54052, 59875, 43128, 50370, 44691,
    286, 12071, 3574, 61384, 15592, 45677, 9711, 23022, 35256, 45493,
    48913, 146, 9053, 5881, 36635, 43280, 53464, 8529, 34344, 64955,
    38266, 12730, 101, 16208, 12607, 58921, 22036, 8221, 31337, 11984,
    20290, 26734, 19552, 48, 31940, 43448, 34762, 53344, 60664, 12809,
    57318, 17436, 44730, 19375, 30, 17425, 14117, 5416, 23853, 55783,
    57995, 32074, 26526, 2192, 11447, 11, 53446, 35152, 64610, 64883,
    26899, 25357, 7667, 3577, 39414, 51161, 4, 58427, 57342, 58557,
    53233, 1066, 29237, 36808, 19370, 17493, 37568, 3, 61468, 38876,
    17586, 64937, 21716, 56472, 58160, 44955, 55221, 63880, 1, 32200,
    62066, 22911, 24090, 10438, 40783, 36364, 14999, 2489, 43284, 9898,
    39612, 9245, 593, 34857, 41054, 30162, 65497, 53340, 27209, 45417,
    37497, 4612, 58397, 52910, 56313, 62716, 22377, 40310, 15190, 34471,
    64005, 18090, 11326, 50839, 62901, 59284, 5580, 15231, 9467, 13161,
    58500, 7259, 317, 50968, 2962, 23006, 32280, 6994, 18751, 5148,
    52739, 49370, 51892, 18552, 52264, 54031, 2804, 17360, 1919, 19639,
    2323, 9448, 43821, 11022, 45500, 31509, 49180, 35598, 38883, 19754,
    987, 11521, 55494, 38056, 20664, 2629, 50986, 31009, 54043, 59743,
]


class EngineRng:
    """generateRandomNumber (0x54851f). state = (index, the 250-word table)."""

    def __init__(self, index):
        self.index = index % SIZE
        self.table = list(TABLE)

    def step(self):
        """0x67819c: table[i] ^= table[i-147]; i = (i + 1) % 250; return table[i]."""
        i = self.index
        j = i - LAG if i >= LAG else i + (SIZE - LAG)
        v = self.table[i] ^ self.table[j]
        self.table[i] = v
        self.index = 0 if i >= SIZE - 1 else i + 1
        return v

    def next(self, max_value):
        """0x6781dd: reject the tail of the range, then remainder.

        ponytail: one step per draw -- see docs/re-rng.md in disciples-modding for the
        static-vs-observed discrepancy (statically two steps are visible per attempt, but
        the corpus only matches a one-step model).
        """
        if max_value == 0:
            return 0
        limit = (0xFFFF // max_value) * max_value
        while True:
            v = self.step()
            if v < limit:
                return v % max_value
