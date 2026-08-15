"""Cast table and canvas policy.

The two id spaces in this project do not agree and one of the collisions is
live: national dex 25 is Pikachu, while game species id 25 is Rattata.  Every
lookup here goes through GAME_ID -> dex, never the other way round by
coincidence, and the mapping below is cross-checked against the original
artwork filenames (which encode the dex number: Pikachu was 002501.png).
"""

from __future__ import annotations

import os

SOURCE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "source")

# --------------------------------------------------------------------------
# canvas policy
# --------------------------------------------------------------------------
#
# 96x96 is not a choice so much as the format the source is already in: the
# static Gen 5 sprites are 96x96, and every animation canvas in the cast fits
# inside it (the largest content is Gastly at 67x78).  Using anything else
# would mean rescaling official pixel art, which is the one thing this
# pipeline must never do.
CELL = 96
COLS = 8

# Row that ground contact is pinned to, in every cell of every species and
# every view.  Leaves 11px below for creatures whose animation dips under
# their resting pose (Gastly, Zubat, Oddish all do) and 84 above for the tall
# ones.  Verified against the whole cast by extract.py, which fails loudly
# rather than silently clipping.
GROUND_ROW = 84

# 1 world unit = 1 metre, so a full 96px cell is one metre tall.  Gen 5 sprite
# sizes already track species size roughly, which lands Pikachu at ~0.48m
# against a true 0.40m -- close enough that no per-species fudge is needed.
# At 1080p integer magnifications of 4x/5x/6x are all clean.
PPU = 96

PORTRAIT = 72        # fits every front static in the cast (largest is 65x55)

# --------------------------------------------------------------------------
# the cast
# --------------------------------------------------------------------------
#
# (game species id, national dex, name, height in metres)
CAST = [
    (1,   1, "Bulbasaur",  0.70),
    (5,   4, "Charmander", 0.60),
    (10,  7, "Squirtle",   0.50),
    (21, 16, "Pidgey",     0.30),
    (25, 19, "Rattata",    0.30),
    (31, 25, "Pikachu",    0.40),
    (47, 41, "Zubat",      0.80),   # 0.80 is wingspan, not height
    (49, 43, "Oddish",     0.50),
    (66, 60, "Poliwag",    0.60),
    (73, 66, "Machop",     0.80),
    (81, 74, "Geodude",    0.40),
    (100, 92, "Gastly",    1.30),
]

BY_GAME_ID = {c[0]: c for c in CAST}


def anim_path(dex: int, view: str) -> str:
    return os.path.join(SOURCE_DIR, f"anim_{view}_{dex}.gif")


def static_path(dex: int, view: str) -> str:
    return os.path.join(SOURCE_DIR, f"{view}_{dex}.png")
