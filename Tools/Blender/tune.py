"""Small helper for applying literal edits to creature scripts from a batch.

Kept as a committed tool rather than an ad hoc shell heredoc so the tuning steps
that produced the shipped creatures are reproducible.

    python Tools/Blender/tune.py <edits.json>

edits.json: {"c005_charmander.py": [["old", "new"], ...], ...}
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CREATURES = os.path.join(HERE, "creatures")


def main(path):
    with open(path, 'r', encoding='utf-8') as fh:
        edits = json.load(fh)
    for fn, reps in edits.items():
        target = os.path.join(CREATURES, fn)
        with open(target, 'r', encoding='utf-8') as fh:
            s = fh.read()
        for old, new in reps:
            if old not in s:
                raise SystemExit("MISS %s: %r" % (fn, old[:70]))
            s = s.replace(old, new)
        with open(target, 'w', encoding='utf-8') as fh:
            fh.write(s)
        print("patched %s (%d edits)" % (fn, len(reps)))


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(argv[0])
