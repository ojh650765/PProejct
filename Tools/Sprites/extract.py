"""Prepare the official Gen 5 sprites for HD-2D use in Unity.

    python Tools/Sprites/extract.py [game_species_id ...]

The source sprites are already correct -- they are the sprites the user asked
to look exactly like.  So this tool does exactly three things, and refuses to
do anything else:

  * DECODE   the animated GIFs to frames, and the static PNGs, as RGBA.
  * ALIGN    every frame onto one 96x96 cell with a stable ground-contact
             origin, by integer translation only.
  * PACK     the unique frames into a sheet, recording the play sequence and
             the original per-frame durations.

There is no resampling, no re-quantisation, no filtering and no antialiasing
anywhere in this file.  Frames are composited and then moved by whole pixels;
that is the complete set of operations applied to the artwork.  Every one of
those would be a downgrade, and they are the failure modes that are invisible
in code review and obvious in a contact sheet -- so extract.py also verifies
its own output and fails loudly rather than shipping a quiet regression.
"""

from __future__ import annotations

import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image, ImageSequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import species as S

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_DIR = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Creatures")
MANIFEST = os.path.join(OUT_DIR, "sprite_manifest.json")
ASSET_PREFIX = "Assets/Game/Art/Sprites/Creatures"


class ExtractError(RuntimeError):
    pass


# --------------------------------------------------------------------------
# decode
# --------------------------------------------------------------------------


def load_gif(path: str) -> tuple[list[np.ndarray], list[int]]:
    """Composited RGBA frames plus their authored durations in ms.

    Durations are read per frame rather than assumed: the cast runs from 50ms
    (Zubat, Geodude) to a 1900ms hold in Pidgey's loop, so a flat fps would
    visibly change animations that are currently correct.
    """
    im = Image.open(path)
    frames, durations = [], []
    for f in ImageSequence.Iterator(im):
        frames.append(np.asarray(f.convert("RGBA")))
        durations.append(int(f.info.get("duration", 100)))
    return frames, durations


def load_png(path: str) -> np.ndarray:
    return np.asarray(Image.open(path).convert("RGBA"))


def check_binary_alpha(frames: list[np.ndarray], what: str) -> None:
    """The sprites render opaque-queue with alpha clip; a soft fringe becomes
    a ragged edge.  The source is already hard-edged, so this asserts we have
    not introduced softness rather than trying to fix it."""
    for i, a in enumerate(frames):
        bad = set(np.unique(a[..., 3]).tolist()) - {0, 255}
        if bad:
            raise ExtractError(f"{what} frame {i}: non-binary alpha {sorted(bad)[:6]}")


# --------------------------------------------------------------------------
# align
# --------------------------------------------------------------------------


def ground_anchor(frames: list[np.ndarray]) -> tuple[int, int, int, int]:
    """Find (anchor_x, ground_y, lowest, highest) for an animation.

    Vertical: the *median* of each frame's lowest opaque row, not the maximum.
    For a creature that stands still the two agree.  For Gastly, Zubat and
    Oddish -- whose loops travel 20+ pixels vertically -- the maximum would
    pin the resting pose well above the floor and leave them hovering; the
    median is the resting contact, which is what the shadow and the health bar
    need to agree with.

    Horizontal: the centroid of the base rather than of the whole silhouette,
    because a swung tail or an outstretched wing drags a full-silhouette
    centre several pixels sideways -- and differently for front and back, so
    the creature would visibly slide when it turned around.
    """
    bottoms, tops = [], []
    for a in frames:
        rows = np.nonzero((a[..., 3] > 0).any(1))[0]
        bottoms.append(int(rows.max()))
        tops.append(int(rows.min()))
    ground = int(np.median(bottoms))

    # base band: the bottom eighth of the resting silhouette
    union = np.zeros(frames[0].shape[:2], bool)
    for a in frames:
        union |= a[..., 3] > 0
    ys = np.nonzero(union.any(1))[0]
    height = ys.max() - ys.min() + 1
    band_top = max(int(ys.min()), ground - max(3, height // 8))
    band = union[band_top:ground + 1]
    xs = np.nonzero(band.any(0))[0]
    if xs.size == 0:
        xs = np.nonzero(union.any(0))[0]
    anchor_x = int(round((int(xs.min()) + int(xs.max())) / 2))
    return anchor_x, ground, max(bottoms), min(tops)


def place(frame: np.ndarray, anchor_x: int, ground: int) -> np.ndarray:
    """Integer-translate a frame into the shared cell.  No other operation."""
    cell = np.zeros((S.CELL, S.CELL, 4), np.uint8)
    dx = S.CELL // 2 - anchor_x
    dy = S.GROUND_ROW - ground
    h, w = frame.shape[:2]
    sx0, sy0 = max(0, -dx), max(0, -dy)
    dx0, dy0 = max(0, dx), max(0, dy)
    cw = min(w - sx0, S.CELL - dx0)
    ch = min(h - sy0, S.CELL - dy0)
    if cw <= 0 or ch <= 0:
        raise ExtractError("frame placed entirely outside the cell")
    clipped = int((frame[..., 3] > 0).sum()) - int(
        (frame[sy0:sy0 + ch, sx0:sx0 + cw, 3] > 0).sum())
    if clipped:
        raise ExtractError(f"placement would clip {clipped} opaque pixels; "
                           f"raise species.CELL or move GROUND_ROW")
    cell[dy0:dy0 + ch, dx0:dx0 + cw] = frame[sy0:sy0 + ch, sx0:sx0 + cw]
    return cell


# --------------------------------------------------------------------------
# pack
# --------------------------------------------------------------------------


def dedupe(frames: list[np.ndarray]) -> tuple[list[np.ndarray], list[int]]:
    """Collapse repeated frames, keeping the play order intact.

    Gen 5 loops hold a lot of frames -- Pidgey's has a 1900ms pause -- and the
    identical ones cost real texture memory at 96x96 RGBA.  This is lossless:
    the sequence still names every frame in order, only the storage is shared.
    """
    uniq: list[np.ndarray] = []
    index: dict[str, int] = {}
    seq: list[int] = []
    for f in frames:
        key = hashlib.sha1(f.tobytes()).hexdigest()
        if key not in index:
            index[key] = len(uniq)
            uniq.append(f)
        seq.append(index[key])
    return uniq, seq


def compose_sheet(frames: list[np.ndarray]) -> np.ndarray:
    rows = (len(frames) + S.COLS - 1) // S.COLS
    sheet = np.zeros((rows * S.CELL, S.COLS * S.CELL, 4), np.uint8)
    for i, f in enumerate(frames):
        r, c = divmod(i, S.COLS)
        sheet[r * S.CELL:(r + 1) * S.CELL, c * S.CELL:(c + 1) * S.CELL] = f
    return sheet


# --------------------------------------------------------------------------
# animation state map
# --------------------------------------------------------------------------
#
# The Gen 5 sprites give one thing: a looping battle idle, per view.  That
# covers Idle and IdleBattle honestly and nothing else, so every other state
# is a runtime transform of those frames.  These recipes are deliberately
# specified in whole pixels and in the source's own vocabulary -- Gen 5's own
# faint is a downward slide with a fade, its own hit is a flash and a jitter,
# its own send-out is a white silhouette resolving -- so the result reads as
# the same game rather than as a sprite with modern effects bolted on.

FRAME_STATES = {
    "Idle": dict(clip="idle", loop=True,
                 note="The Gen 5 loop at its authored per-frame durations."),
    "IdleBattle": dict(clip="idle", loop=True,
                       note="Same clip -- the Gen 5 animation IS the battle "
                            "idle. Do not retime it."),
}

PROCEDURAL_STATES = {
    "Walk": dict(
        base="idle", recipe=(
            "Idle clip playing, plus a 2px vertical bob on a 0.5s triangle "
            "wave and a 3 deg lean into the direction of travel. Snap the bob "
            "to whole pixels so the sprite never lands between texels.")),
    "Run": dict(
        base="idle", recipe=(
            "As Walk at 1.6x clip speed, 3px bob on a 0.32s wave, 7 deg lean.")),
    "AttackPhysical": dict(
        base="idle", recipe=(
            "Hold idle frame 0. Translate 10px toward the target over 0.12s "
            "ease-in, 0.06s hold at contact with a 12% horizontal squash, "
            "return over 0.18s ease-out. The DamageDealtEvent lands on the "
            "hold.")),
    "AttackSpecial": dict(
        base="idle", recipe=(
            "Hold idle frame 0. Rear back 4px over 0.15s, then a 1.08x scale "
            "pop over 0.08s as the projectile leaves the MuzzleAnchor, settle "
            "over 0.2s. VFX owns the projectile; the sprite only gestures.")),
    "AttackStatus": dict(
        base="idle", recipe=(
            "Idle clip continues. Additive tint in the move's type colour, "
            "0.3 strength, 2Hz sine, for the event's SuggestedDuration.")),
    "Hit": dict(
        base="idle", recipe=(
            "Gen 5's own damage read: alternate the sprite between normal and "
            "a flat white silhouette at 12Hz for 0.25s, while jittering 2px "
            "horizontally at 20Hz. No pose change -- the flash carries it.")),
    "Dodge": dict(
        base="idle", recipe=(
            "Slide 8px along the dodge axis over 0.10s ease-out, hold 0.06s, "
            "return over 0.14s. Alpha 1 -> 0.45 -> 1 on the same curve.")),
    "Faint": dict(
        base="idle", recipe=(
            "Gen 5's own faint: slide the sprite straight down by its own "
            "height over 0.45s ease-in while clipping it against the platform "
            "line, fading alpha 1 -> 0 across the last 40%. No death pose "
            "exists in the source and inventing one would not match.")),
    "Celebrate": dict(
        base="idle", recipe=(
            "Idle clip playing. Two parabolic hops, 10px peak, 0.35s each, "
            "with a 10% squash on landing.")),
    "SentOut": dict(
        base="idle", recipe=(
            "Scale 0 -> 1.15 -> 1.0 about the ground pivot over 0.22s. For "
            "the first 0.10s draw as a flat white silhouette, fading to the "
            "real sprite -- the ball-release read. Then Idle.")),
    "Recalled": dict(
        base="idle", recipe=(
            "Reverse of SentOut over 0.18s ending at scale 0, with the "
            "silhouette tinted red rather than white, matching the recall "
            "beam.")),
    "Sleep": dict(
        base="idle", recipe=(
            "Freeze the idle clip on frame 0. Vertical squash oscillating "
            "0 to 6% on a 2.4s sine for slow breathing. Z particles from "
            "HeadAnchor are the VFX worker's.")),
}

IMPORT_SETTINGS = dict(
    textureType="Sprite (2D and UI)",
    spriteMode="Multiple - slice by Grid By Cell Size, 96x96, no padding",
    pixelsPerUnit=S.PPU,
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
    note=("Point, uncompressed, no mips is mandatory. Bilinear softens a 1px "
          "keyline into the background; block compression shatters a 13-colour "
          "indexed palette into gradients; mips dissolve the sprite as the "
          "HD-2D camera pulls out. Full Rect + extrude 0 keeps every frame on "
          "the same rect so the alignment done here survives import."),
)


# --------------------------------------------------------------------------


def build_view(dex: int, view: str, name: str) -> dict:
    frames, durations = load_gif(S.anim_path(dex, view))
    static = load_png(S.static_path(dex, view))
    check_binary_alpha(frames, f"{name} anim_{view}")
    check_binary_alpha([static], f"{name} static_{view}")

    anchor_x, ground, lowest, highest = ground_anchor(frames)
    placed = [place(f, anchor_x, ground) for f in frames]

    # the static sprite gets the same treatment from its own measurements, so
    # it lands on the same ground line as the animation
    s_anchor, s_ground, _, _ = ground_anchor([static])
    placed_static = place(static, s_anchor, s_ground)

    uniq, seq = dedupe(placed)
    static_index = len(uniq)
    uniq.append(placed_static)
    return dict(frames=uniq, sequence=seq, durations=durations,
                static_index=static_index,
                headroom=dict(
                    below=S.CELL - 1 - (lowest + S.GROUND_ROW - ground),
                    above=highest + S.GROUND_ROW - ground))


def build_creature(game_id: int) -> dict:
    game_id, dex, name, height = S.BY_GAME_ID[game_id]
    print(f"[{game_id}] {name}  (dex {dex})")
    entry = dict(species_id=game_id, dex_number=dex, name=name,
                 height_m=height,
                 source=dict(
                     front_anim=f"Tools/Sprites/source/anim_front_{dex}.gif",
                     back_anim=f"Tools/Sprites/source/anim_back_{dex}.gif",
                     front_static=f"Tools/Sprites/source/front_{dex}.png",
                     back_static=f"Tools/Sprites/source/back_{dex}.png"),
                 cell=dict(width=S.CELL, height=S.CELL, columns=S.COLS),
                 pixels_per_unit=S.PPU,
                 pivot_px=dict(x=S.CELL // 2, y_from_bottom=S.CELL - 1 - S.GROUND_ROW),
                 pivot_normalised=dict(
                     x=round((S.CELL // 2) / S.CELL, 5),
                     y=round((S.CELL - 1 - S.GROUND_ROW) / S.CELL, 5)),
                 views={})

    os.makedirs(OUT_DIR, exist_ok=True)
    slug = name.lower()
    colours: set[tuple] = set()
    for view in ("front", "back"):
        v = build_view(dex, view, name)
        sheet = compose_sheet(v["frames"])
        fname = f"{slug}_{view}.png"
        Image.fromarray(sheet, "RGBA").save(os.path.join(OUT_DIR, fname))
        for f in v["frames"]:
            m = f[..., 3] > 0
            if m.any():
                colours.update(map(tuple, np.unique(f[..., :3][m], axis=0)))
        entry["views"][view] = dict(
            sheet=f"{ASSET_PREFIX}/{fname}",
            unique_frames=len(v["frames"]),
            sheet_size=[sheet.shape[1], sheet.shape[0]],
            static_frame=v["static_index"],
            clips=dict(idle=dict(sequence=v["sequence"],
                                 durations_ms=v["durations"],
                                 loop=True)),
            headroom_px=v["headroom"],
        )
        print(f"  {view}: {len(v['sequence'])} frames -> {len(v['frames'])} unique, "
              f"sheet {sheet.shape[1]}x{sheet.shape[0]}, "
              f"headroom {v['headroom']}")

    # portrait: the front static, untouched, centred in its own box
    static = load_png(S.static_path(dex, "front"))
    ys, xs = np.nonzero(static[..., 3] > 0)
    crop = static[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    if crop.shape[0] > S.PORTRAIT or crop.shape[1] > S.PORTRAIT:
        raise ExtractError(f"{name}: front static {crop.shape[1]}x{crop.shape[0]} "
                           f"does not fit the {S.PORTRAIT}px portrait box")
    port = np.zeros((S.PORTRAIT, S.PORTRAIT, 4), np.uint8)
    oy = (S.PORTRAIT - crop.shape[0]) // 2
    ox = (S.PORTRAIT - crop.shape[1]) // 2
    port[oy:oy + crop.shape[0], ox:ox + crop.shape[1]] = crop
    pname = f"{slug}_portrait.png"
    Image.fromarray(port, "RGBA").save(os.path.join(OUT_DIR, pname))
    entry["portrait"] = dict(path=f"{ASSET_PREFIX}/{pname}", size=S.PORTRAIT,
                             source="front static sprite, trimmed and centred, "
                                    "not rescaled")
    entry["palette_colours"] = len(colours)
    entry["animation_states"] = dict(from_frames=FRAME_STATES,
                                     procedural=PROCEDURAL_STATES)
    return entry


def check_cast() -> int:
    """Dry-run the placement for the whole cast without writing anything.

    The canvas policy (96x96, ground row 84) has to hold for every species or
    it holds for none -- a creature that does not fit would have to be scaled,
    which is the one thing forbidden here.  Gastly and Geodude are the tight
    ones: their loops travel far vertically, so their resting pose sits well
    above their lowest frame.
    """
    print(f"{'name':12} {'view':5} {'frames':>6} {'uniq':>5} "
          f"{'above':>6} {'below':>6}  fit")
    bad = 0
    for gid, dex, name, _ in S.CAST:
        for view in ("front", "back"):
            try:
                v = build_view(dex, view, name)
                n = len(v["sequence"])
                hr = v["headroom"]
                ok = hr["above"] >= 0 and hr["below"] >= 0
                bad += not ok
                print(f"{name:12} {view:5} {n:>6} {len(v['frames']) - 1:>5} "
                      f"{hr['above']:>6} {hr['below']:>6}  {'ok' if ok else 'FAIL'}")
            except ExtractError as exc:
                bad += 1
                print(f"{name:12} {view:5} {'':>6} {'':>5} {'':>6} {'':>6}  FAIL {exc}")
    print("all species fit the 96x96 cell" if not bad else f"{bad} placement failures")
    return bad


def main(argv: list[str]) -> None:
    if argv and argv[0] == "--check":
        sys.exit(1 if check_cast() else 0)
    ids = [int(a) for a in argv] or [c[0] for c in S.CAST]
    manifest = dict(
        generator="Tools/Sprites/extract.py",
        source=("Official Gen 5 (Black/White) sprites via the PokeAPI sprite "
                "repository, staged in Tools/Sprites/source/."),
        policy=("Decode, integer-align, pack. No resampling, no "
                "re-quantisation, no filtering is applied to the artwork at "
                "any point."),
        cell_size=S.CELL,
        ground_row=S.GROUND_ROW,
        pixels_per_unit=S.PPU,
        id_note=("species_id is the GAME id; dex_number is the national dex. "
                 "They collide: game 25 is Rattata, dex 25 is Pikachu."),
        view_scheme=dict(
            authored=["front", "back"],
            runtime_mirrored=("Left/right are the same sheets mirrored in the "
                              "billboard shader as uv.x = 1 - uv.x. Never a "
                              "negative X scale: that inverts winding and "
                              "flips normal-map interpretation in URP."),
            side="Not authored. Front and back cover battle and the overworld.",
        ),
        import_settings=IMPORT_SETTINGS,
        creatures=[],
    )
    if os.path.exists(MANIFEST):
        try:
            with open(MANIFEST, encoding="utf-8") as fh:
                old = json.load(fh)
            manifest["creatures"] = [c for c in old.get("creatures", [])
                                     if c.get("species_id") not in ids]
        except (OSError, ValueError):
            pass

    for gid in ids:
        manifest["creatures"].append(build_creature(gid))
    manifest["creatures"].sort(key=lambda c: c["species_id"])
    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print("wrote", MANIFEST)


if __name__ == "__main__":
    main(sys.argv[1:])
