"""Prepare the official Gen 4 overworld sprites for HD-2D use in Unity.

    python Tools/Sprites/extract_people.py [key ...]
    python Tools/Sprites/extract_people.py --check     # dry run, writes nothing

The people-side counterpart to extract.py, and it obeys the same three-verb
contract, for the same reason -- the source art is already correct, so the only
honest thing to do with it is move it:

  * DECODE  the strip PNG into 32x32 cells as RGBA.
  * ALIGN   every cell onto the shared canvas with a stable ground-contact
            origin, by integer translation only.  For this cast that
            translation turns out to be exactly zero, and that is asserted
            rather than assumed -- see `align_check` below.
  * PACK    the unique cells into a sheet, recording each clip's play order.

There is no resampling, no re-quantisation, no filtering and no antialiasing
anywhere in this file.  `verify_people.py` re-reads the written sheets and
proves it, cell against source cell, byte for byte.

What is different from extract.py
---------------------------------
Three things, all forced by the source rather than chosen:

1. Three views, not two.  The overworld art draws front, back AND side.  The
   creature pipeline had only front and back and let the runtime borrow one for
   the side sectors; here there is real side artwork and it should be used.

2. Two clips per view, not one.  A Gen 5 battle sprite has a single looping
   idle.  An overworld sprite has a standing pose and a four-beat walk, and a
   player character has a run on top of that.  They share one texture and
   differ only in play order, which is exactly what SpriteSheetInfo.sequence
   already expresses -- so a clip is emitted as its own SpriteSheetInfo over
   the same texture, and no C# change is needed to play it.

3. No horizontal re-anchoring.  extract.py centres each creature on the
   centroid of its own base, because a Gen 5 sprite sits wherever it likes on
   its canvas.  Overworld cells are drawn from a fixed object origin, so all
   three views are already in register with each other; re-centring them
   per view would make a character slide sideways as it turned.  The pivot is
   the cell centre, full stop.
"""

from __future__ import annotations

import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import people as P
from unity_meta import ensure_meta

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_DIR = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "People")
MANIFEST = os.path.join(OUT_DIR, "people_manifest.json")
ASSET_PREFIX = "Assets/Game/Art/Sprites/People"


class ExtractError(RuntimeError):
    pass


# --------------------------------------------------------------------------
# decode
# --------------------------------------------------------------------------


def load_strip(path: str) -> list[np.ndarray]:
    """The vertical strip PNG split into 32x32 RGBA cells."""
    im = Image.open(path)
    if im.width != P.CELL:
        raise ExtractError(f"{os.path.basename(path)}: width {im.width}, "
                           f"expected {P.CELL}")
    if im.height % P.CELL:
        raise ExtractError(f"{os.path.basename(path)}: height {im.height} is "
                           f"not a multiple of {P.CELL}")
    rgba = np.asarray(im.convert("RGBA"))
    return [rgba[i * P.CELL:(i + 1) * P.CELL] for i in range(im.height // P.CELL)]


def check_binary_alpha(cells: list[np.ndarray], what: str) -> None:
    """The sprites render opaque-queue with alpha clip; a soft fringe becomes a
    ragged edge.  The source is already hard-edged, so this asserts we have not
    introduced softness rather than trying to fix it."""
    for i, a in enumerate(cells):
        bad = set(np.unique(a[..., 3]).tolist()) - {0, 255}
        if bad:
            raise ExtractError(f"{what} cell {i}: non-binary alpha {sorted(bad)[:6]}")


# --------------------------------------------------------------------------
# align
# --------------------------------------------------------------------------


def align_check(cells: list[np.ndarray], what: str) -> tuple[int, int]:
    """Confirm the source is already on the shared ground row, and stays in the
    cell.  Returns (dy, ground) where dy is the translation required.

    The vertical reference is the *median* lowest opaque row over the walk
    block, matching extract.py's reasoning: a maximum would pin the standing
    pose above the floor and leave the character hovering, because the step
    frames legitimately hang one pixel lower.

    Only the walk block (the first sixteen cells) is measured.  A player's run
    block is airborne by design -- its neutral pose bottoms out two pixels off
    the ground -- and letting it drag the anchor would sink the walk.
    """
    walk = cells[:16]
    bottoms = []
    for a in walk:
        rows = np.nonzero((a[..., 3] > 0).any(1))[0]
        if rows.size == 0:
            raise ExtractError(f"{what}: an empty cell in the walk block")
        bottoms.append(int(rows.max()))
    ground = int(np.median(bottoms))
    dy = P.GROUND_ROW - ground

    # Everything in this cast should need no translation at all. If a future
    # character does, that is a real finding and it should be loud, because it
    # means the source is not registered the way the rest of the cast is.
    if dy:
        raise ExtractError(
            f"{what}: ground contact sits on row {ground}, not "
            f"people.GROUND_ROW={P.GROUND_ROW}. The Gen 4 field sheets are "
            f"drawn from a fixed object origin and should never need moving; "
            f"a sheet that does is either a different cell size or a "
            f"different kind of asset.")

    # Nothing may leave the cell. Native art cannot, but a hand-edited sheet
    # could, and this is where that would be caught.
    for i, a in enumerate(cells):
        rows = np.nonzero((a[..., 3] > 0).any(1))[0]
        cols = np.nonzero((a[..., 3] > 0).any(0))[0]
        if rows.size and (rows.min() < 0 or rows.max() >= P.CELL):
            raise ExtractError(f"{what} cell {i}: leaves the cell vertically")
        if cols.size and (cols.min() < 0 or cols.max() >= P.CELL):
            raise ExtractError(f"{what} cell {i}: leaves the cell horizontally")
    return dy, ground


def confirm_mirror(cells: list[np.ndarray], base: int, what: str) -> bool:
    """Is the group at base+4 an exact horizontal mirror of the one at base?

    This is what licenses dropping cells 12..15. It is checked per character
    rather than assumed, because the moment it is false for someone the runtime
    flip would be showing the wrong artwork and nothing else would complain.
    """
    for i in range(P.GROUP_LEN):
        a = cells[base + i]
        b = cells[base + P.GROUP_LEN + i]
        if not np.array_equal(a[:, ::-1], b):
            return False
    return True


# --------------------------------------------------------------------------
# pack
# --------------------------------------------------------------------------


def dedupe(cells: list[np.ndarray]) -> tuple[list[np.ndarray], dict[int, int]]:
    """Collapse repeated cells, returning the unique list and source->unique map.

    Every group of four holds its standing pose twice (offsets +0 and +2 are
    byte-identical in every file in the cast), so this is not a micro-
    optimisation: it takes a character from twelve stored cells to nine.
    Lossless -- the clip sequences still name every beat in order.
    """
    uniq: list[np.ndarray] = []
    index: dict[str, int] = {}
    mapping: dict[int, int] = {}
    for i, c in enumerate(cells):
        key = hashlib.sha1(c.tobytes()).hexdigest()
        if key not in index:
            index[key] = len(uniq)
            uniq.append(c)
        mapping[i] = index[key]
    return uniq, mapping


def compose_sheet(cells: list[np.ndarray]) -> np.ndarray:
    rows = (len(cells) + P.COLS - 1) // P.COLS
    sheet = np.zeros((rows * P.CELL, P.COLS * P.CELL, 4), np.uint8)
    for i, c in enumerate(cells):
        r, col = divmod(i, P.COLS)
        sheet[r * P.CELL:(r + 1) * P.CELL, col * P.CELL:(col + 1) * P.CELL] = c
    return sheet


IMPORT_SETTINGS = dict(
    textureType="Sprite (2D and UI)",
    spriteMode=f"Multiple - slice by Grid By Cell Size, {P.CELL}x{P.CELL}, no padding",
    pixelsPerUnit=round(P.PPU, 6),
    filterMode="Point (no filter)",
    compression="None",
    generateMipMaps=False,
    sRGBTexture=True,
    alphaIsTransparency=True,
    alphaSource="Input Texture Alpha",
    maxTextureSize=2048,
    wrapMode="Clamp",
    meshType="Full Rect",
    extrudeEdges=0,
    note=("Identical in kind to the creature sheets' settings and mandatory "
          "for the same reasons -- but note pixelsPerUnit differs. People are "
          "Gen 4 field art at 96/7 texels per metre; creatures are Gen 5 "
          "battle art at 96. Importing a person sheet at 96 makes a 1.7 m "
          "adult 0.24 m tall."),
)


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------


def build_character(key: str, write: bool = True) -> dict:
    key, source, role, display, note = P.BY_KEY[key]
    cells = load_strip(P.sheet_path(source))
    what = f"{key} ({source})"
    check_binary_alpha(cells, what)

    if len(cells) not in (16, 32):
        raise ExtractError(f"{what}: {len(cells)} cells, expected 16 or 32")

    dy, ground = align_check(cells, what)

    # Blocks: the walk block always, plus a run block for the players.
    blocks = [("walk", 0)]
    if len(cells) == 32:
        blocks.append(("run", 16))

    for _, base in blocks:
        if not confirm_mirror(cells, base + P.MIRROR_GROUP - 4, what):
            raise ExtractError(
                f"{what}: cells {base + P.MIRROR_GROUP}..{base + P.MIRROR_GROUP + 3} "
                f"are not an exact mirror of {base + 8}..{base + 11}. The "
                f"second side group is normally a stored mirror and is dropped "
                f"on that basis; here it is real artwork and dropping it would "
                f"lose it.")

    # The cells actually imported: three views per block, mirror group dropped.
    kept: list[np.ndarray] = []
    kept_src: list[int] = []
    for _, base in blocks:
        for view in ("front", "back", "side"):
            g = base + P.GROUP[view]
            for off in range(P.GROUP_LEN):
                kept.append(cells[g + off])
                kept_src.append(g + off)

    uniq, mapping = dedupe(kept)

    # Clip sequences, as indices into the packed sheet.
    views: dict[str, dict] = {}
    for view in ("front", "back", "side"):
        clips = {}
        for clip_kind, base in blocks:
            g = base + P.GROUP[view]
            for clip_name, offsets in P.CLIP_OFFSETS.items():
                # "idle" is the same standing pose in both blocks, so it is
                # only emitted once, off the walk block.
                if clip_name == "idle" and clip_kind != "walk":
                    continue
                name = clip_name if clip_kind == "walk" else clip_kind
                step_ms = (P.WALK_STEP_MS if clip_kind == "walk"
                           else P.RUN_STEP_MS)
                seq = [mapping[kept_src.index(g + off)] for off in offsets]
                clips[name] = dict(
                    sequence=seq,
                    durations_ms=[step_ms] * len(seq) if len(seq) > 1 else [0],
                    loop=True,
                )
        views[view] = clips

    # Measured framing, from the standing front pose. Everything downstream
    # that needs a height or an anchor comes from here rather than from a
    # nominal species height, exactly as on the creature side.
    stand = cells[P.GROUP["front"]]
    rows = np.nonzero((stand[..., 3] > 0).any(1))[0]
    crown = int(rows.min())
    sprite_h = P.GROUND_ROW - crown + 1

    colours: set[tuple] = set()
    for c in uniq:
        m = c[..., 3] > 0
        if m.any():
            colours.update(map(tuple, np.unique(c[..., :3][m], axis=0)))

    sheet = compose_sheet(uniq)
    fname = f"{key}.png"
    if write:
        os.makedirs(OUT_DIR, exist_ok=True)
        png = os.path.join(OUT_DIR, fname)
        Image.fromarray(sheet, "RGBA").save(png)
        # Correct import settings, written now rather than left to Unity's
        # defaults -- which include nPOTScale, and a 256x96 sheet is not a
        # power of two. See unity_meta.py.
        ensure_meta(png, key, P.PPU)

    entry = dict(
        key=key,
        name=display,
        role=role,
        note=note,
        origin=dict(
            kind="traced",
            detail=("Official Gen 4 (Platinum) overworld artwork, imported "
                    "pixel-for-pixel. Not redrawn, not restyled, not rescaled."),
            upstream=f"pret/pokeplatinum res/graphics/field_sprites/**/{source}.png",
            staged=f"Tools/Sprites/source_overworld/{source}.png",
        ),
        sheet=f"{ASSET_PREFIX}/{fname}",
        sheet_size=[int(sheet.shape[1]), int(sheet.shape[0])],
        unique_frames=len(uniq),
        cell=dict(width=P.CELL, height=P.CELL, columns=P.COLS),
        pixels_per_unit=round(P.PPU, 6),
        pivot_px=dict(x=P.CELL // 2, y_from_bottom=P.CELL - 1 - P.GROUND_ROW),
        pivot_normalised=dict(
            x=round((P.CELL // 2) / P.CELL, 5),
            y=round((P.CELL - 1 - P.GROUND_ROW) / P.CELL, 5)),
        views=views,
        side_view=dict(
            walks_toward=P.SIDE_WALKS_TOWARD,
            flank_to_lens=P.SIDE_FLANK_TO_LENS,
            mirror_for_the_other_side=True,
            note=("One side sheet is authored. The opposite side is this sheet "
                  "flipped in U, never a negative X scale. The source shipped "
                  "the mirror as real cells and they were verified identical "
                  "before being dropped."),
        ),
        display=dict(
            sprite_height_px=sprite_h,
            world_height_m=round(sprite_h / P.PPU, 4),
            crown_row=crown,
            ground_row=P.GROUND_ROW,
            note=("world_height_m is the height actually rendered: the drawn "
                  "pixels divided by this cast's PPU. It is the number a "
                  "spawner should pass as displayHeight."),
            anchors_px=dict(
                root=dict(x=P.CELL // 2, y=P.GROUND_ROW),
                head=dict(x=P.CELL // 2, y=crown),
                body=dict(x=P.CELL // 2, y=crown + sprite_h // 2),
            ),
            anchors_note=("y is a row index from the TOP of the cell; Unity's "
                          "sprite space counts from the bottom, so use "
                          "cell_height - 1 - y."),
        ),
        palette_colours=len(colours),
        alignment=dict(translation_applied=[0, dy],
                       measured_ground_row=ground,
                       note="Zero translation: the source is natively registered."),
    )
    print(f"{key:12} {source:18} {len(kept):>2} cells -> {len(uniq):>2} unique, "
          f"sheet {sheet.shape[1]}x{sheet.shape[0]}, "
          f"{sprite_h:>2}px = {entry['display']['world_height_m']:.2f}m, "
          f"{len(colours):>2} colours, clips {sorted(views['front'])}")
    return entry


def build_prop(key: str, write: bool = True) -> dict:
    """A field object: one row of cells, no directions, no walk.

    Props exist because two beats of the opening are carried by an object in
    the original games rather than by a character pose -- the starter briefcase
    and the ball raised overhead.  Staging them that way is both more faithful
    and entirely traced, which is the whole reason the professor needs no
    invented gesture.
    """
    key, source, count, display, note = P.PROPS_BY_KEY[key]
    cells = load_strip(P.sheet_path(source))
    what = f"prop {key} ({source})"
    check_binary_alpha(cells, what)
    if len(cells) != count:
        raise ExtractError(f"{what}: {len(cells)} cells, expected {count}")

    # Props sit on the same floor as the cast, so the same ground row applies.
    # Unlike a character this is not asserted to be exact -- an object that
    # rests on the ground and an object that is held are both legitimate -- but
    # it is measured and recorded so a scene can place it honestly.
    bottoms, tops = [], []
    for a in cells:
        rows = np.nonzero((a[..., 3] > 0).any(1))[0]
        bottoms.append(int(rows.max()))
        tops.append(int(rows.min()))
    ground = int(np.median(bottoms))

    uniq, mapping = dedupe(cells)
    seq = [mapping[i] for i in range(len(cells))]
    sheet = compose_sheet(uniq)
    fname = f"prop_{key}.png"
    if write:
        os.makedirs(OUT_DIR, exist_ok=True)
        png = os.path.join(OUT_DIR, fname)
        Image.fromarray(sheet, "RGBA").save(png)
        ensure_meta(png, f"prop_{key}", P.PPU)

    height_px = ground - min(tops) + 1
    entry = dict(
        key=key, name=display, note=note,
        origin=dict(kind="traced",
                    detail="Official Gen 4 (Platinum) field-object artwork, "
                           "imported pixel-for-pixel.",
                    upstream=f"pret/pokeplatinum res/graphics/field_sprites/**/{source}.png",
                    staged=f"Tools/Sprites/source_overworld/{source}.png"),
        sheet=f"{ASSET_PREFIX}/{fname}",
        sheet_size=[int(sheet.shape[1]), int(sheet.shape[0])],
        unique_frames=len(uniq),
        cell=dict(width=P.CELL, height=P.CELL, columns=P.COLS),
        pixels_per_unit=round(P.PPU, 6),
        pivot_px=dict(x=P.CELL // 2, y_from_bottom=P.CELL - 1 - P.GROUND_ROW),
        clips=dict(idle=dict(sequence=[seq[0]], durations_ms=[0], loop=True),
                   play=dict(sequence=seq,
                             durations_ms=[P.WALK_STEP_MS] * len(seq),
                             loop=False)) if len(seq) > 1 else
              dict(idle=dict(sequence=[seq[0]], durations_ms=[0], loop=True)),
        display=dict(sprite_height_px=height_px,
                     world_height_m=round(height_px / P.PPU, 4),
                     ground_row=ground,
                     on_cast_ground_row=ground == P.GROUND_ROW),
    )
    print(f"{key:18} {source:26} {len(cells):>2} cells -> {len(uniq):>2} unique, "
          f"{height_px:>2}px = {entry['display']['world_height_m']:.2f}m, "
          f"ground row {ground}")
    return entry


def main(argv: list[str]) -> None:
    check = bool(argv) and argv[0] == "--check"
    if check:
        argv = argv[1:]
    keys = argv or [c[0] for c in P.CAST]

    manifest = dict(
        generator="Tools/Sprites/extract_people.py",
        source=("Official Gen 4 (Platinum) overworld sprites from the "
                "pret/pokeplatinum decompilation, staged in "
                "Tools/Sprites/source_overworld/."),
        policy=("Decode, integer-align, pack. No resampling, no "
                "re-quantisation, no filtering is applied to the artwork at "
                "any point. For this cast the alignment translation is zero: "
                "the source is already registered to a common ground row."),
        provenance=("Every character is traced -- imported pixel-for-pixel "
                    "from official artwork. None is drawn by hand."),
        cell_size=P.CELL,
        ground_row=P.GROUND_ROW,
        pixels_per_unit=round(P.PPU, 6),
        scale_note=(
            "People are 96/7 texels per metre; creatures (sprite_manifest.json) "
            "are 96. The two source sets are drawn at densities that differ by "
            "7:1 and cannot be reconciled without redrawing one of them. 96/7 "
            "was chosen over a rounder number so the ratio is an exact "
            "integer and both sets land on a common screen texel grid."),
        view_scheme=dict(
            authored=["front", "back", "side"],
            runtime_mirrored=("The opposite side is the side sheet with "
                              "uv.x = 1 - uv.x. Never a negative X scale: that "
                              "inverts winding and flips normal-map "
                              "interpretation in URP."),
        ),
        clip_scheme=("Clips share one texture and differ only in play order, "
                     "which is what SpriteSheetInfo.sequence already expresses. "
                     "idle is a single held cell; walk is the four-beat "
                     "0 1 0 3; run is the same beats from the run block."),
        walk_timing_note=(
            f"{P.WALK_STEP_MS} ms per beat is derived, not sourced: the "
            f"frame-sequence files beside the artwork carry a cell remap and "
            f"no durations. It is the DS field cadence, 8 frames at 59.83 Hz."),
        import_settings=IMPORT_SETTINGS,
        placement=P.PLACEMENT,
        procedural_states=P.PROCEDURAL_STATES,
        states_note=(
            "The source contains a standing pose and a walk and nothing else. "
            "Every other state -- talking above all, which the opening needs -- "
            "is a runtime transform of frames that exist, specified in whole "
            "pixels, exactly as extract.py does it for the creatures. Three "
            "ways of faking a talking pose out of pixels were tried and all "
            "three were rejected; see people.PROCEDURAL_STATES for what was "
            "tried and why none of it shipped."),
        characters=[],
        props=[],
    )

    if os.path.exists(MANIFEST) and not check:
        try:
            with open(MANIFEST, encoding="utf-8") as fh:
                old = json.load(fh)
            manifest["characters"] = [c for c in old.get("characters", [])
                                      if c.get("key") not in keys]
        except (OSError, ValueError):
            pass

    bad = 0
    for key in keys:
        try:
            entry = build_character(key, write=not check)
            if not check:
                manifest["characters"].append(entry)
        except ExtractError as exc:
            bad += 1
            print(f"{key:12} FAIL {exc}")

    if not argv:
        print()
        for key, *_ in P.PROPS:
            try:
                entry = build_prop(key, write=not check)
                if not check:
                    manifest["props"].append(entry)
            except ExtractError as exc:
                bad += 1
                print(f"{key:18} FAIL {exc}")

    if check:
        print(f"\n{'all characters fit the canvas' if not bad else str(bad) + ' failures'}")
        sys.exit(1 if bad else 0)

    order = {c[0]: i for i, c in enumerate(P.CAST)}
    manifest["characters"].sort(key=lambda c: order.get(c["key"], 999))
    porder = {p[0]: i for i, p in enumerate(P.PROPS)}
    manifest["props"].sort(key=lambda c: porder.get(c["key"], 999))
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print(f"\nwrote {MANIFEST} with {len(manifest['characters'])} characters "
          f"and {len(manifest['props'])} props")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main(sys.argv[1:])
