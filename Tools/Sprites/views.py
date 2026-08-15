"""Constructing the views the official artwork does not contain.

The source is a single three-quarter FRONT view per creature.  Rather than
redrawing a back view from scratch (which would throw away the thing that makes
this pipeline work -- the official proportions, palette and shading), the back
view is *derived* from the front at high resolution:

    mirror  ->  erase the face by diffusion inpaint  ->  paint the back
    markings back on, modulated by the body's existing shading

Because the result then goes through the exact same downsample + material
quantise as the front, the two views share a palette and a rendering style
automatically, which is what makes them look like a matched set.

All shapes are expressed in normalised coordinates of the artwork's alpha
bounding box, so a recipe is resolution-independent.
"""

from __future__ import annotations

import numpy as np


# --------------------------------------------------------------------------
# shape primitives, in normalised bbox space
# --------------------------------------------------------------------------


def _grid(shape, bbox):
    x0, y0, x1, y1 = bbox
    ys, xs = np.mgrid[0:shape[0], 0:shape[1]].astype(np.float32)
    return (xs - x0) / (x1 - x0), (ys - y0) / (y1 - y0)


def ellipse(shape, bbox, cx, cy, rx, ry, rot=0.0, soft=0.06) -> np.ndarray:
    """Soft-edged ellipse; returns coverage in 0..1."""
    u, v = _grid(shape, bbox)
    du, dv = u - cx, v - cy
    if rot:
        c, s = np.cos(rot), np.sin(rot)
        du, dv = du * c + dv * s, -du * s + dv * c
    d = np.sqrt((du / rx) ** 2 + (dv / ry) ** 2)
    return np.clip((1.0 - d) / max(soft, 1e-4) + 0.5, 0.0, 1.0)


def band(shape, bbox, cy, half_h, cx=0.5, half_w=0.6, bow=0.0,
         soft=0.35) -> np.ndarray:
    """Horizontal band, optionally bowed, for back stripes and belly bands."""
    u, v = _grid(shape, bbox)
    centre = cy + bow * ((u - cx) / max(half_w, 1e-4)) ** 2
    dy = np.abs(v - centre) / max(half_h, 1e-4)
    dx = np.abs(u - cx) / max(half_w, 1e-4)
    a = np.clip((1.0 - dy) / soft + 0.5, 0, 1) * np.clip((1.0 - dx) / 0.5 + 0.5, 0, 1)
    return a


# --------------------------------------------------------------------------
# diffusion inpaint -- the face-removal tool
# --------------------------------------------------------------------------


def inpaint(rgb: np.ndarray, mask: np.ndarray, hole: np.ndarray,
            smooth_iters: int = 220) -> np.ndarray:
    """Replace `hole` with a smooth continuation of the surrounding body.

    Solving a (crude) Laplace fill rather than blurring means the erased face
    inherits the head's own light direction and falloff, so the back of the
    head looks lit by the same key light as the rest of the creature instead
    of looking like a flat patch.
    """
    known = mask & ~hole
    out = rgb.copy()
    out[~known] = 0.0
    filled = known.copy()

    # flood the hole inward from its rim so every unknown pixel starts from a
    # plausible value, otherwise the relaxation takes thousands of iterations
    while not filled[mask].all():
        f = filled.astype(np.float32)[..., None]
        acc = np.zeros_like(out)
        cnt = np.zeros(out.shape[:2] + (1,), np.float32)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            acc += np.roll(out * f, (dy, dx), (0, 1))
            cnt += np.roll(f, (dy, dx), (0, 1))
        grow = mask & ~filled & (cnt[..., 0] > 0)
        if not grow.any():
            break
        out[grow] = (acc / np.maximum(cnt, 1e-6))[grow]
        filled |= grow

    # relax, holding the known pixels fixed
    target = hole & mask
    for _ in range(smooth_iters):
        acc = np.zeros_like(out)
        cnt = np.zeros(out.shape[:2] + (1,), np.float32)
        mf = mask.astype(np.float32)[..., None]
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            acc += np.roll(out * mf, (dy, dx), (0, 1))
            cnt += np.roll(mf, (dy, dx), (0, 1))
        avg = acc / np.maximum(cnt, 1e-6)
        out[target] = avg[target]
    out[~mask] = 0.0
    return out


# --------------------------------------------------------------------------
# markings
# --------------------------------------------------------------------------


def paint(rgb: np.ndarray, mask: np.ndarray, cover: np.ndarray,
          colour, shade_ref: float | None = None) -> np.ndarray:
    """Lay a marking down *through* the existing shading.

    Flat-filling a stripe would flatten the form exactly where the form is
    most visible.  So the marking colour is scaled by how light the body
    already is at that pixel, and the creature's volume survives the edit.
    """
    out = rgb.copy()
    col = np.asarray(colour, np.float32)
    if col.max() > 1.5:
        col = col / 255.0
    luma = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
    if shade_ref is None:
        body = luma[mask & (cover > 0.05)]
        shade_ref = float(np.median(body)) if body.size else 0.6
    factor = np.clip(luma / max(shade_ref, 1e-3), 0.55, 1.45)[..., None]
    a = (cover * mask)[..., None]
    out = rgb * (1 - a) + np.clip(col * factor, 0, 1) * a
    out[~mask] = 0.0
    return out


# --------------------------------------------------------------------------
# view assembly
# --------------------------------------------------------------------------


def mirror(img: np.ndarray) -> np.ndarray:
    return img[:, ::-1].copy()


def mirror_shape(spec: dict) -> dict:
    """Reflect a shape spec across the vertical midline of the bbox.

    Every recipe is written in FRONT-view coordinates -- reading a recipe
    should not require mentally flipping it -- so the back-view builder does
    the reflection itself.  Getting this backwards silently produces a back
    view that still has a face, which is exactly the sort of error that is
    invisible in code and obvious in a contact sheet.
    """
    out = dict(spec)
    if "cx" in out:
        out["cx"] = 1.0 - out["cx"]
    if out.get("rot"):
        out["rot"] = -out["rot"]
    return out


def cover_of(shape, bbox, spec: dict) -> np.ndarray:
    kind = spec.get("kind", "ellipse")
    args = {k: v for k, v in spec.items() if k not in ("kind", "colour")}
    return (ellipse if kind == "ellipse" else band)(shape, bbox, **args)


def build_back(src: np.ndarray, bbox, recipe: dict):
    """front three-quarter artwork -> back three-quarter artwork."""
    img = mirror(src)
    x0, y0, x1, y1 = bbox
    w = src.shape[1]
    bbox_m = (w - x1, y0, w - x0, y1)   # the bbox follows the mirror

    rgb = img[..., :3].copy()
    mask = img[..., 3] > 0.5
    shape = mask.shape

    # 1. erase everything that only exists on the creature's front
    hole = np.zeros(shape, bool)
    for e in recipe.get("face_erase", []):
        hole |= cover_of(shape, bbox_m, mirror_shape(e)) > 0.5
    hole &= mask
    if hole.any():
        rgb = inpaint(rgb, mask, hole)

    # 2. paint on what only exists on its back
    for mk in recipe.get("back_markings", []):
        cover = cover_of(shape, bbox_m, mirror_shape(mk))
        rgb = paint(rgb, mask, cover, mk["colour"])

    out = img.copy()
    out[..., :3] = rgb
    return out, bbox_m
