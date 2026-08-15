"""Per-creature authoring data, and the project-wide scale policy.

Coordinates are normalised to the artwork's alpha bounding box: x and y in
0..1 across the creature's own extents, so a recipe does not care what
resolution the source art happens to be.
"""

from __future__ import annotations

import os

SOURCE_DIR = (r"C:/Users/ojh65/AppData/Local/Temp/claude/C--PProejct"
              r"/8fbd9adb-7cdd-4c1d-a6d3-ce52c84c54e8/scratchpad/repo/data/pokemon_images")

# --------------------------------------------------------------------------
# scale policy
# --------------------------------------------------------------------------
#
# Constant sprite RESOLUTION, not constant pixels-per-metre: every species is
# authored into the same 256x256 cell, because the camera rig deliberately
# gives a 0.3m Pidgey and a 1.3m Gastly the same screen area.  This is the
# Gen 5 model, which used one 96x96 box for every species.
CELL = 256

# Species heights still modulate how much of that box each creature fills --
# again as in Gen 5, where a small species does not fill its box the way a
# large one does.  Filling it literally (a 4.3x range from 0.30m to 1.30m)
# would leave Pidgey at 54px of the 232 available and unreadable, so the range
# is compressed by a power curve, landing every creature between 144 and 240px
# inside the 256 cell.
SIZE_REF_M = 0.60
SIZE_EXPONENT = 0.35
SIZE_MAX_PX = 240          # what the tallest creature in the cast fills
SIZE_MAX_H_M = 1.30        # ... and its true height


def size_factor(true_h_m: float) -> float:
    return (true_h_m / SIZE_REF_M) ** SIZE_EXPONENT


def sprite_height_px(true_h_m: float) -> int:
    k = SIZE_MAX_PX / size_factor(SIZE_MAX_H_M)
    return int(round(size_factor(true_h_m) * k))


# Feet sit on a fixed row in every cell, for every species and every frame, so
# nothing pops when a creature is swapped, turns, or changes animation.
GROUND_ROW = 240

# Pixels-per-unit is then a single global number: it is what makes a 256 cell
# map to a consistent world size.  At 1080p an integer 2x magnification puts a
# Pikachu at ~318 screen px, which is the classic battle framing.
PPU = 256

PORTRAIT_SIZE = 64

# Guaranteed a palette slot for every creature: the specular catchlight in the
# eye. It is a few pixels of the artwork, so clustering never keeps it, and
# without it every creature stares out with flat dead eyes.
FORCED_COLOURS = [(250, 250, 248)]


# --------------------------------------------------------------------------
# creatures
# --------------------------------------------------------------------------

PIKACHU_BROWN = (132, 97, 50)      # sampled from the tail base of the artwork
PIKACHU_DARK = (46, 38, 34)

CREATURES = {
    31: dict(
        species_id=31,
        name="Pikachu",
        source="002501.png",
        height_m=0.40,
        n_clusters=10,

        # tail pivot for the secondary-motion effect in every pose
        tail=dict(cx=0.85, cy=0.34, rx=0.21, ry=0.31),

        # everything that exists only on the creature's front, erased when the
        # back view is derived: the face, and the paw held against the chest
        face_erase=[
            dict(cx=0.235, cy=0.420, rx=0.235, ry=0.165, soft=0.30),
            dict(cx=0.462, cy=0.652, rx=0.090, ry=0.088, soft=0.40),
        ],

        # ... and everything that exists only on its back, painted back on.
        # Pikachu's two brown back stripes are the single feature that makes a
        # back view read as Pikachu rather than as a yellow blob.
        back_markings=[
            dict(kind="band", cy=0.578, half_h=0.018, cx=0.490, half_w=0.175,
                 bow=0.026, soft=0.16, colour=PIKACHU_BROWN),
            dict(kind="band", cy=0.652, half_h=0.021, cx=0.505, half_w=0.200,
                 bow=0.028, soft=0.16, colour=PIKACHU_BROWN),
        ],

        # Restamped because the downsample averages a 3px catchlight away, and
        # a Pikachu with flat black eyes reads dead.
        front_markings=[
            dict(cx=0.293, cy=0.312, rx=0.016, ry=0.015, soft=0.5,
                 colour=(252, 252, 250)),
            dict(cx=0.106, cy=0.352, rx=0.011, ry=0.013, soft=0.5,
                 colour=(252, 252, 250)),
        ],

        # closed-eye variant used by Sleep and Faint
        eyes=[dict(cx=0.305, cy=0.326, rx=0.046, ry=0.044),
              dict(cx=0.114, cy=0.365, rx=0.034, ry=0.042)],
        eyes_closed=[
            dict(kind="band", cy=0.330, half_h=0.010, cx=0.305, half_w=0.044,
                 bow=0.022, soft=0.30, colour=PIKACHU_DARK),
            dict(kind="band", cy=0.368, half_h=0.009, cx=0.114, half_w=0.030,
                 bow=0.020, soft=0.30, colour=PIKACHU_DARK),
        ],

        # square head crop for the UI portrait, in bbox-height units
        portrait=dict(cx=0.255, cy=0.335, size=0.780),
    ),
}


def source_path(rec: dict) -> str:
    return os.path.join(SOURCE_DIR, rec["source"])
