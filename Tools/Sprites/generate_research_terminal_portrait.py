"""Generate the original pixel-art dialogue portrait for the research terminal.

The portrait is drawn on a 64x128 logical pixel grid and enlarged 6x with
nearest-neighbour sampling, matching the project's existing portrait pipeline.

Run from anywhere:
    python Tools/Sprites/generate_research_terminal_portrait.py
"""

from __future__ import annotations

import io
from pathlib import Path
import sys

import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
OUT = (
    ROOT
    / "Assets"
    / "Game"
    / "Art"
    / "Sprites"
    / "Resources"
    / "Portraits"
    / "research_terminal.png"
)
META_TEMPLATE = Path(__file__).with_name("portrait_meta_template.yaml")

LOW_SIZE = (64, 128)
SCALE = 6

# A compact, high-contrast palette related to the steel blues used by the
# professor/player portraits, with a cyan CRT ramp and one warm status lamp.
OUTLINE = (8, 14, 20, 255)
DEEP = (28, 37, 48, 255)
SHADOW = (48, 61, 76, 255)
STEEL = (74, 90, 107, 255)
STEEL_LIGHT = (119, 135, 148, 255)
HIGHLIGHT = (197, 205, 222, 255)
CYAN_DARK = (27, 104, 120, 255)
CYAN = (65, 190, 201, 255)
CYAN_LIGHT = (139, 231, 228, 255)
CYAN_WHITE = (218, 255, 247, 255)
GLOW_OUTER = (43, 177, 190, 34)
GLOW_INNER = (74, 217, 222, 64)
AMBER_DARK = (119, 70, 28, 255)
AMBER = (236, 166, 55, 255)
AMBER_LIGHT = (255, 225, 105, 255)


def add_crt_glow(pixels: np.ndarray) -> None:
    """Lay down two crisp, translucent glow bands behind the screen."""
    yy, xx = np.ogrid[: LOW_SIZE[1], : LOW_SIZE[0]]
    outer = ((xx - 32) / 27.0) ** 2 + ((yy - 43) / 34.0) ** 2 <= 1.0
    inner = ((xx - 32) / 23.0) ** 2 + ((yy - 43) / 30.0) ** 2 <= 1.0
    pixels[outer] = GLOW_OUTER
    pixels[inner] = GLOW_INNER


def draw_terminal(image: Image.Image) -> None:
    d = ImageDraw.Draw(image)

    # Wall rail, hanger and cable: these read behind the kiosk and make it a
    # mounted fixture rather than a small freestanding computer.
    d.polygon([(22, 8), (42, 8), (45, 11), (45, 22), (42, 25),
               (22, 25), (19, 22), (19, 11)], fill=OUTLINE)
    d.polygon([(24, 10), (40, 10), (42, 12), (42, 22), (39, 24),
               (25, 24), (22, 21), (22, 12)], fill=SHADOW)
    d.rectangle((25, 11, 39, 14), fill=STEEL)
    d.rectangle((25, 15, 27, 21), fill=STEEL_LIGHT)
    d.rectangle((37, 15, 39, 22), fill=DEEP)
    d.rectangle((25, 11, 27, 12), fill=HIGHLIGHT)
    d.rectangle((24, 17, 25, 18), fill=OUTLINE)
    d.rectangle((39, 17, 40, 18), fill=OUTLINE)

    # A visible side arm disappears behind the casing.
    d.polygon([(46, 30), (52, 32), (54, 37), (54, 75), (51, 80),
               (46, 81)], fill=OUTLINE)
    d.polygon([(48, 33), (51, 34), (52, 38), (52, 73), (49, 77),
               (47, 77)], fill=STEEL)
    d.rectangle((50, 39, 51, 65), fill=STEEL_LIGHT)
    d.rectangle((50, 71, 51, 73), fill=DEEP)

    # Power/data cable with a squared-off pixel bend.
    d.rectangle((28, 94, 35, 102), fill=OUTLINE)
    d.rectangle((30, 97, 33, 113), fill=OUTLINE)
    d.rectangle((27, 110, 33, 115), fill=OUTLINE)
    d.rectangle((26, 113, 29, 124), fill=OUTLINE)
    d.rectangle((31, 99, 32, 111), fill=STEEL)
    d.rectangle((28, 112, 31, 113), fill=STEEL)
    d.rectangle((27, 114, 28, 122), fill=SHADOW)
    d.rectangle((25, 122, 30, 125), fill=OUTLINE)
    d.rectangle((27, 122, 29, 123), fill=STEEL_LIGHT)

    # Rounded casing silhouette, built with stepped corners rather than smooth
    # vector curves so every contour remains deliberate at the logical grid.
    d.polygon([(20, 16), (44, 16), (48, 20), (50, 27), (50, 59),
               (47, 65), (47, 91), (43, 98), (21, 98), (17, 94),
               (17, 69), (13, 64), (12, 28), (15, 21)], fill=OUTLINE)
    d.polygon([(21, 18), (42, 18), (46, 21), (48, 28), (48, 58),
               (45, 63), (45, 90), (41, 95), (23, 95), (19, 92),
               (19, 68), (15, 62), (14, 29), (17, 22)], fill=STEEL)
    d.polygon([(18, 22), (22, 19), (39, 19), (43, 21), (45, 25),
               (45, 62), (42, 66), (42, 91), (39, 94), (23, 94),
               (20, 91), (20, 66), (17, 61), (16, 29)], fill=STEEL_LIGHT)
    d.polygon([(43, 22), (47, 27), (47, 59), (44, 63), (44, 90),
               (40, 95), (36, 95), (39, 91), (39, 65), (42, 60)], fill=SHADOW)
    d.rectangle((21, 19, 37, 21), fill=HIGHLIGHT)
    d.rectangle((16, 29, 17, 55), fill=HIGHLIGHT)
    d.rectangle((19, 64, 21, 67), fill=HIGHLIGHT)

    # Deep CRT bezel and rounded screen glass.
    d.polygon([(20, 23), (44, 23), (48, 27), (48, 57), (44, 62),
               (20, 62), (16, 58), (16, 27)], fill=OUTLINE)
    d.polygon([(21, 25), (43, 25), (46, 28), (46, 56), (42, 59),
               (21, 59), (18, 56), (18, 29)], fill=DEEP)
    d.polygon([(23, 28), (41, 28), (44, 31), (44, 53), (41, 56),
               (23, 56), (20, 53), (20, 31)], fill=CYAN_DARK)
    d.polygon([(24, 29), (40, 29), (43, 32), (43, 52), (40, 55),
               (24, 55), (21, 52), (21, 32)], fill=CYAN)
    d.polygon([(25, 30), (39, 30), (42, 33), (42, 50), (39, 53),
               (25, 53), (22, 50), (22, 33)], fill=CYAN_LIGHT)
    d.polygon([(25, 31), (33, 31), (28, 34), (24, 40), (23, 39),
               (23, 33)], fill=CYAN_WHITE)
    d.rectangle((23, 42, 41, 43), fill=CYAN)
    d.rectangle((24, 49, 40, 50), fill=CYAN)
    d.rectangle((39, 32, 41, 34), fill=CYAN_WHITE)

    # Lower service panel. The single amber lamp is deliberately the only warm
    # accent; all remaining marks are vents, fasteners or casing highlights.
    d.polygon([(20, 66), (44, 66), (46, 69), (46, 89), (42, 93),
               (22, 93), (19, 90), (19, 69)], fill=OUTLINE)
    d.polygon([(22, 68), (42, 68), (44, 70), (44, 87), (40, 91),
               (23, 91), (21, 89), (21, 70)], fill=DEEP)
    d.rectangle((23, 70, 41, 73), fill=SHADOW)
    d.rectangle((24, 75, 39, 77), fill=STEEL)
    d.rectangle((24, 79, 36, 81), fill=STEEL)
    d.rectangle((24, 83, 39, 85), fill=STEEL)
    d.rectangle((24, 75, 30, 75), fill=STEEL_LIGHT)
    d.rectangle((24, 79, 27, 79), fill=STEEL_LIGHT)
    d.rectangle((24, 83, 32, 83), fill=STEEL_LIGHT)
    d.rectangle((38, 78, 40, 80), fill=OUTLINE)
    d.point((39, 79), fill=STEEL_LIGHT)

    # Exactly one status lamp, with a dark socket and a two-tone lit lens.
    d.rectangle((37, 69, 42, 74), fill=AMBER_DARK)
    d.rectangle((38, 70, 41, 73), fill=AMBER)
    d.rectangle((38, 70, 40, 71), fill=AMBER_LIGHT)

    # Bottom lip and casing screws.
    d.rectangle((24, 89, 40, 91), fill=SHADOW)
    d.rectangle((24, 89, 34, 89), fill=STEEL_LIGHT)
    d.rectangle((21, 87, 22, 88), fill=HIGHLIGHT)
    d.rectangle((41, 87, 42, 88), fill=OUTLINE)


def write_meta() -> None:
    """Write Unity point-filtered Sprite import settings with a stable GUID."""
    sys.path.insert(0, str(Path(__file__).parent))
    import unity_meta  # pylint: disable=import-outside-toplevel

    meta_path = Path(str(OUT) + ".meta")
    guid = unity_meta.existing_guid(str(meta_path)) or unity_meta.stable_guid(
        "portrait_research_terminal"
    )
    text = META_TEMPLATE.read_text(encoding="utf-8").replace("{guid}", guid)
    with io.open(meta_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def main() -> int:
    pixels = np.zeros((LOW_SIZE[1], LOW_SIZE[0], 4), dtype=np.uint8)
    add_crt_glow(pixels)
    logical = Image.fromarray(pixels, mode="RGBA")
    draw_terminal(logical)

    final = logical.resize(
        (LOW_SIZE[0] * SCALE, LOW_SIZE[1] * SCALE), Image.Resampling.NEAREST
    )
    OUT.parent.mkdir(parents=True, exist_ok=True)
    final.save(OUT, format="PNG", optimize=False)
    write_meta()

    colors = len(final.getcolors(maxcolors=final.width * final.height) or [])
    print(f"wrote {OUT}")
    print(f"dimensions: {final.width}x{final.height}")
    print(f"logical grid: {LOW_SIZE[0]}x{LOW_SIZE[1]} at {SCALE}x nearest")
    print(f"RGBA colors: {colors}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
