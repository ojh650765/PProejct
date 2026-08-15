"""Build creature sprite sheets, portraits and the manifest.

    python Tools/Sprites/build.py [species_id ...]

Order of operations matters and is deliberate:

    1. build every HIGH-RES base view (front, back, closed-eye variants)
    2. pose every animation frame, still at high res
    3. learn ONE material palette from the union of all of them
    4. downsample + quantise + outline every frame through that one palette

Learning the palette from the union rather than from the front idle frame is
what keeps the back view's markings from quantising to mud: the brown back
stripes are absent from the front, so a front-only palette has no brown to
snap them to.
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import palette as PAL
import pixelize as P
import poses as PO
import recipes as R
import views as V

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_DIR = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Creatures")
PAD = 320          # transparent border so the 256 cell never falls off the source

# animation state -> (pose key, view requirement).  States absent here are
# procedural; see PROCEDURAL below.
FRAME_STATES = [
    ("Idle", "idle", "normal"),
    ("IdleBattle", "idle_battle", "normal"),
    ("Walk", "walk", "normal"),
    ("AttackPhysical", "attack", "normal"),
    ("AttackSpecial", "attack_special", "normal"),
    ("Hit", "hit", "normal"),
    ("Faint", "faint", "closed"),
    ("Celebrate", "celebrate", "normal"),
    ("Sleep", "sleep", "closed"),
]

PROCEDURAL = {
    "Run": dict(
        frames="Walk", fps=14,
        recipe="Walk frames at 14fps; add a constant 6 deg forward lean about "
               "the foot pivot and a 2px vertical bob at 2x the frame rate."),
    "AttackStatus": dict(
        frames="AttackSpecial", fps=6,
        recipe="AttackSpecial frames held on frame 1; pulse an additive tint "
               "of the move's type colour at 0.25 strength, 2 Hz, for the "
               "event's SuggestedDuration."),
    "Dodge": dict(
        frames="Idle", fps=0,
        recipe="Idle frame 0; translate 7px along the dodge axis over 0.10s "
               "ease-out, hold 0.06s, return over 0.14s. Alpha 1 -> 0.45 -> 1 "
               "on the same curve. Add a 4 deg lean into the movement."),
    "SentOut": dict(
        frames="Idle", fps=0,
        recipe="Idle frame 0; scale 0 -> 1.15 -> 1.0 over 0.22s from the foot "
               "pivot. For the first 0.10s render the sprite as a flat white "
               "silhouette (tint colour white, factor 1) fading to 0 -- this "
               "is the ball-release read. Never scale the sprite between "
               "frames of a loop, only during this one-shot."),
    "Recalled": dict(
        frames="Idle", fps=0,
        recipe="Reverse of SentOut over 0.18s, ending at scale 0. Same white "
               "silhouette flash, ramping 0 -> 1 across the collapse."),
}


# --------------------------------------------------------------------------


def _pad(img: np.ndarray, n: int) -> np.ndarray:
    return np.pad(img, ((n, n), (n, n), (0, 0)))


def _shape_cover(shape, bbox, spec):
    kind = spec.get("kind", "ellipse")
    args = {k: v for k, v in spec.items() if k not in ("kind", "colour")}
    return (V.ellipse if kind == "ellipse" else V.band)(shape, bbox, **args)


def build_base_views(src: np.ndarray, bbox, rec: dict) -> dict:
    """front / back / closed-eye variants, all at source resolution."""
    mask = src[..., 3] > 0.5

    # front, with the small features the downsample would otherwise average
    # away restamped at high res so they survive the reduction
    rgb = src[..., :3].copy()
    for mk in rec.get("front_markings", []):
        rgb = V.paint(rgb, mask, _shape_cover(mask.shape, bbox, mk), mk["colour"])
    front = src.copy()
    front[..., :3] = rgb

    out = {"front": (front, bbox)}
    out["back"] = V.build_back(src, bbox, rec)

    # closed-eye front: erase the eyes, then draw the lids back on
    rgb = src[..., :3].copy()
    hole = np.zeros(mask.shape, bool)
    for e in rec.get("eyes", []):
        hole |= _shape_cover(mask.shape, bbox, e) > 0.5
    hole &= mask
    if hole.any():
        rgb = V.inpaint(rgb, mask, hole, smooth_iters=140)
    for mk in rec.get("eyes_closed", []):
        rgb = V.paint(rgb, mask, _shape_cover(mask.shape, bbox, mk), mk["colour"])
    closed = src.copy()
    closed[..., :3] = rgb
    out["front_closed"] = (closed, bbox)

    # the back view has no eyes to close
    out["back_closed"] = out["back"]
    return out


def build_frames(bases: dict, rec: dict) -> dict:
    """(view, state) -> list of posed HIGH-RES frames."""
    frames = {}
    for view in ("front", "back"):
        tail = dict(rec["tail"])
        if view == "back":
            tail = dict(tail, cx=1.0 - tail["cx"])
        lib = PO.pose_library(tail=tail)
        for state, key, variant in FRAME_STATES:
            base_name = view if variant == "normal" else f"{view}_closed"
            img, bbox = bases[base_name]
            frames[(view, state)] = [
                (PO.apply_pose(img, bbox, eff) if eff else img.copy(), bbox)
                for eff in lib[key]
            ]
    return frames


def canvas_rect(bbox, sprite_h: int):
    """Source-space rect that maps onto the fixed CELL x CELL sprite cell.

    Solved rather than guessed, so that in every cell of every species the
    creature's feet land on GROUND_ROW and its silhouette is centred.  A
    creature therefore never shifts when it turns, changes animation, or is
    swapped for another species.
    """
    x0, y0, x1, y1 = bbox
    scale = sprite_h / (y1 - y0)
    side = R.CELL / scale
    cx = (x0 + x1) / 2
    rx0 = cx - (R.CELL / 2) / scale
    ry0 = y1 - R.GROUND_ROW / scale
    return (int(round(rx0)), int(round(ry0)),
            int(round(rx0 + side)), int(round(ry0 + side)))


def build_creature(rec: dict, verbose: bool = True) -> dict:
    src = _pad(P.load_rgba(R.source_path(rec)), PAD)
    bbox = P.alpha_bbox(src)
    if verbose:
        print(f"  source bbox {bbox[2]-bbox[0]}x{bbox[3]-bbox[1]}")

    bases = build_base_views(src, bbox, rec)
    frames = build_frames(bases, rec)

    # ---- one palette for the whole creature -------------------------------
    union = np.concatenate([bases[k][0] for k in ("front", "back", "front_closed")], 1)
    mats = PAL.segment_materials(union[..., :3], union[..., 3] > 0.5,
                                 n_clusters=rec["n_clusters"],
                                 forced=rec.get("forced_colours", R.FORCED_COLOURS))
    n_colours = sum(len(m.ramp) for m in mats)
    if verbose:
        print(f"  {len(mats)} materials / {n_colours} colours")

    sprite_h = R.sprite_height_px(rec["height_m"])
    cell = R.CELL

    # the canvas is solved from the FRONT bbox and reused for every frame and
    # every view, so nothing shifts between frames or when a creature turns
    rect = canvas_rect(bbox, sprite_h)

    def render(img, rect):
        """high-res frame -> final pixel-art RGBA in the shared cell."""
        small, mask = P.pixelize(img, cell, bbox=rect)
        if not mask.any():
            return np.zeros((cell, cell, 4), np.float32)
        q, midx = PAL.quantise(small[..., :3], mask, mats)
        q = P.add_outline(q, mask, midx, mats)
        out = np.concatenate([q, mask[..., None].astype(np.float32)], 2)
        return P.bleed_alpha(out)

    result = dict(cell=cell, sprite_h=sprite_h, mats=mats,
                  n_colours=n_colours, ground_y=R.GROUND_ROW,
                  pivot_x=cell // 2, views={})

    for view in ("front", "back"):
        # the back view's own bbox is the mirror of the front's, so its canvas
        # rect must be mirrored too or the sprite would slide sideways when
        # the creature turns around
        r = rect if view == "front" else (
            src.shape[1] - rect[2], rect[1], src.shape[1] - rect[0], rect[3])
        order, layout, imgs = [], {}, []
        for state, _, _ in FRAME_STATES:
            fr = frames[(view, state)]
            layout[state] = dict(start=len(imgs), count=len(fr))
            imgs.extend(render(img, r) for img, _ in fr)
            order.append(state)
        result["views"][view] = dict(frames=imgs, layout=layout, order=order)

    # ---- portrait ---------------------------------------------------------
    p = rec["portrait"]
    bh = bbox[3] - bbox[1]
    bw = bbox[2] - bbox[0]
    half = p["size"] * bh / 2
    cx = bbox[0] + p["cx"] * bw
    cy = bbox[1] + p["cy"] * bh
    prect = (int(round(cx - half)), int(round(cy - half)),
             int(round(cx + half)), int(round(cy + half)))
    ps, pmask = P.pixelize(bases["front"][0], R.PORTRAIT_SIZE, bbox=prect)
    pq, pmidx = PAL.quantise(ps[..., :3], pmask, mats)
    pq = P.add_outline(pq, pmask, pmidx, mats)
    result["portrait"] = P.bleed_alpha(
        np.concatenate([pq, pmask[..., None].astype(np.float32)], 2))
    return result


# --------------------------------------------------------------------------


def compose_sheet(frames: list[np.ndarray], cell_w: int, cell_h: int,
                  cols: int) -> np.ndarray:
    rows = (len(frames) + cols - 1) // cols
    sheet = np.zeros((rows * cell_h, cols * cell_w, 4), np.float32)
    for i, f in enumerate(frames):
        r, c = divmod(i, cols)
        sheet[r * cell_h:(r + 1) * cell_h, c * cell_w:(c + 1) * cell_w] = f
    return sheet


def write_creature(rec: dict, built: dict) -> dict:
    os.makedirs(OUT_DIR, exist_ok=True)
    slug = rec["name"].lower()
    cw = ch = built["cell"]
    cols = 5
    entry = dict(
        species_id=rec["species_id"],
        name=rec["name"],
        source_artwork=rec["source"],
        height_m=rec["height_m"],
        pixels_per_unit=R.PPU,
        sprite_height_px=built["sprite_h"],
        world_height_m=round(built["sprite_h"] / R.PPU, 4),
        cell=dict(width=cw, height=ch, columns=cols),
        pivot_px=dict(x=built["pivot_x"], y_from_bottom=ch - built["ground_y"]),
        pivot_normalised=dict(x=round(built["pivot_x"] / cw, 5),
                              y=round((ch - built["ground_y"]) / ch, 5)),
        palette_colours=built["n_colours"],
        views={},
        mirrored_views=dict(
            left="side, uv.x = 1 - uv.x",
            right="side",
            note=("Mirror in the billboard shader's UV, never with a negative "
                  "X scale -- negative scale inverts triangle winding and "
                  "flips normal-map interpretation in URP, so the creature "
                  "would re-light backwards as it turns under the moving key."),
        ),
        procedural_states=PROCEDURAL,
    )
    colours: set[tuple] = set()
    box = [cw, ch, 0, 0]
    for view, data in built["views"].items():
        sheet = compose_sheet(data["frames"], cw, ch, cols)
        name = f"{slug}_{view}.png"
        P.save_rgba(sheet, os.path.join(OUT_DIR, name))
        for f in data["frames"]:
            m = f[..., 3] > 0.5
            if not m.any():
                continue
            colours.update(map(tuple, np.unique(
                (f[..., :3][m] * 255 + 0.5).astype(np.uint8), axis=0)))
            ys, xs = np.nonzero(m)
            box = [min(box[0], int(xs.min())), min(box[1], int(ys.min())),
                   max(box[2], int(xs.max()) + 1), max(box[3], int(ys.max()) + 1)]
        entry["views"][view] = dict(
            sheet=f"Assets/Game/Art/Sprites/Creatures/{name}",
            frame_count=len(data["frames"]),
            states={s: data["layout"][s] for s in data["order"]},
        )
    # the true colour count, after the outline pass adds a darkened tone per
    # material -- the ramp count alone under-reports it
    entry["palette_colours"] = len(colours)
    # every frame of every view fits inside this box within the 256 cell; the
    # rest of the cell is empty, which is what a tight-packed atlas reclaims
    entry["content_box_px"] = dict(x=box[0], y=box[1],
                                   width=box[2] - box[0], height=box[3] - box[1])
    pname = f"{slug}_portrait.png"
    P.save_rgba(built["portrait"], os.path.join(OUT_DIR, pname))
    entry["portrait"] = dict(
        path=f"Assets/Game/Art/Sprites/Creatures/{pname}",
        size=R.PORTRAIT_SIZE)
    return entry


MANIFEST = os.path.join(OUT_DIR, "sprite_manifest.json")

IMPORT_SETTINGS = dict(
    textureType="Sprite (2D and UI)",
    spriteMode="Multiple (sliced by the cell size in this manifest)",
    pixelsPerUnit=R.PPU,
    filterMode="Point (no filter)",
    compression="None",
    generateMipMaps=False,
    sRGBTexture=True,
    alphaIsTransparency=True,
    maxTextureSize=2048,
    wrapMode="Clamp",
    meshType="Full Rect",
    extrudeEdges=0,
    note=("Point + uncompressed + no mips is mandatory: bilinear filtering "
          "blurs the 1px outline into the background, block compression "
          "shatters the flat palette into gradients, and mips dissolve the "
          "sprite the moment the HD-2D camera pushes out."),
)


def main(argv: list[str]) -> None:
    ids = [int(a) for a in argv] or sorted(R.CREATURES)
    manifest = dict(
        generator="Tools/Sprites/build.py",
        pixels_per_unit=R.PPU,
        cell_size=R.CELL,
        ground_row=R.GROUND_ROW,
        size_policy=dict(
            cell_px=R.CELL,
            reference_height_m=R.SIZE_REF_M,
            exponent=R.SIZE_EXPONENT,
            max_fill_px=R.SIZE_MAX_PX,
            note=("Constant sprite resolution: every species is authored into "
                  "the same 256x256 cell. How much of the cell it fills is "
                  "(height_m / 0.6) ** 0.35, normalised so the tallest of the "
                  "cast fills 240px -- the Gen 5 model, where one box size "
                  "served every species but a small species did not fill it. "
                  "ICreatureArtRegistry.GetDisplayHeight must return "
                  "world_height_m (sprite_height_px / PPU), because that is "
                  "the height actually on screen and health bars sit on it."),
        ),
        view_scheme=dict(
            authored=["front", "back", "side"],
            runtime_mirrored={"left": "side", "right": "side"},
            note=("Three authored views per species. Left/right are the side "
                  "view mirrored in the billboard shader's UV at runtime -- "
                  "not authored, and never a negative X scale. Front is "
                  "derived from the official artwork; back and side are "
                  "authored from the front silhouette plus the species' "
                  "known design."),
        ),
        import_settings=IMPORT_SETTINGS,
        creatures=[],
    )
    if os.path.exists(MANIFEST):
        try:
            with open(MANIFEST, encoding="utf-8") as fh:
                old = json.load(fh)
            manifest["creatures"] = [c for c in old.get("creatures", [])
                                     if c["species_id"] not in ids]
        except (OSError, ValueError):
            pass

    for sid in ids:
        rec = R.CREATURES[sid]
        print(f"[{sid}] {rec['name']}")
        built = build_creature(rec)
        entry = write_creature(rec, built)
        manifest["creatures"].append(entry)
        print(f"  {entry['sprite_height_px']}px tall, cell "
              f"{entry['cell']['width']}x{entry['cell']['height']}, "
              f"{entry['palette_colours']} colours")

    manifest["creatures"].sort(key=lambda c: c["species_id"])
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print("wrote", MANIFEST)


if __name__ == "__main__":
    main(sys.argv[1:])
