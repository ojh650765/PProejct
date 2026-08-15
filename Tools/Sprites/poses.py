"""Animation poses, applied at HIGH resolution.

The one rule that makes derived pixel art survive animation: never deform the
pixel sprite.  Rotating or scaling a 77px sprite tears its outline apart and
makes the palette bleed.  So every pose is a smooth warp of the 390px artwork,
and each posed frame is then pushed through the identical downsample + quantise
as the idle frame.  Frames therefore share an exact palette, an exact outline
weight, and an exact pixel grid.

A pose is a list of weighted affine effects.  Each effect has a spatial weight
(which part of the creature it grabs) and a transform; the per-pixel inverse
displacement is blended by that weight.  That is an approximation to a real
skeleton, but for the amplitudes animation actually uses -- a few degrees, a
few percent -- it is indistinguishable and needs no rig.
"""

from __future__ import annotations

import numpy as np


# --------------------------------------------------------------------------
# resampling
# --------------------------------------------------------------------------


def remap(img: np.ndarray, sx: np.ndarray, sy: np.ndarray) -> np.ndarray:
    """Bilinear sample of `img` at float coords (sx, sy); outside -> transparent."""
    h, w = img.shape[:2]
    x0 = np.floor(sx).astype(np.int32)
    y0 = np.floor(sy).astype(np.int32)
    fx = (sx - x0)[..., None]
    fy = (sy - y0)[..., None]
    ok = (x0 >= 0) & (x0 < w - 1) & (y0 >= 0) & (y0 < h - 1)
    x0c = np.clip(x0, 0, w - 2)
    y0c = np.clip(y0, 0, h - 2)

    # premultiply so transparent source pixels cannot bleed colour into edges
    a = img[..., 3:4]
    pm = np.concatenate([img[..., :3] * a, a], 2)
    p00 = pm[y0c, x0c]
    p10 = pm[y0c, x0c + 1]
    p01 = pm[y0c + 1, x0c]
    p11 = pm[y0c + 1, x0c + 1]
    top = p00 * (1 - fx) + p10 * fx
    bot = p01 * (1 - fx) + p11 * fx
    out = top * (1 - fy) + bot * fy
    out[~ok] = 0.0
    oa = out[..., 3:4]
    rgb = np.where(oa > 1e-4, out[..., :3] / np.maximum(oa, 1e-4), 0.0)
    return np.concatenate([np.clip(rgb, 0, 1), oa], 2)


# --------------------------------------------------------------------------
# spatial weights, in normalised bbox space
# --------------------------------------------------------------------------


def _uv(shape, bbox):
    x0, y0, x1, y1 = bbox
    ys, xs = np.mgrid[0:shape[0], 0:shape[1]].astype(np.float32)
    return (xs - x0) / (x1 - x0), (ys - y0) / (y1 - y0)


def w_all(shape, bbox):
    return np.ones(shape[:2], np.float32)


def w_above(shape, bbox, y, soft=0.18):
    """1 above `y`, falling to 0 below it -- grabs the head and upper body."""
    _, v = _uv(shape, bbox)
    return np.clip((y - v) / soft + 0.5, 0, 1)


def w_below(shape, bbox, y, soft=0.18):
    _, v = _uv(shape, bbox)
    return np.clip((v - y) / soft + 0.5, 0, 1)


def w_ellipse(shape, bbox, cx, cy, rx, ry, soft=0.35):
    u, v = _uv(shape, bbox)
    d = np.sqrt(((u - cx) / rx) ** 2 + ((v - cy) / ry) ** 2)
    return np.clip((1 - d) / soft + 0.5, 0, 1)


WEIGHTS = {"all": w_all, "above": w_above, "below": w_below, "ellipse": w_ellipse}


# --------------------------------------------------------------------------
# effects
# --------------------------------------------------------------------------


def _affine_inv(shape, bbox, *, rot=0.0, sx=1.0, sy=1.0,
                tx=0.0, ty=0.0, shear=0.0, px=0.5, py=1.0):
    """Inverse map of an affine about pivot (px,py) in normalised bbox space.

    Returns source coords in pixels for each destination pixel.
    """
    x0, y0, x1, y1 = bbox
    bw, bh = x1 - x0, y1 - y0
    u, v = _uv(shape, bbox)
    du, dv = u - px, v - py
    # forward is: shear -> scale -> rotate -> translate;  invert in reverse
    du, dv = du - tx, dv - ty
    c, s = np.cos(-rot), np.sin(-rot)
    # work in a squared aspect so rotation is not skewed by the bbox ratio
    asp = bw / bh
    dvx = dv / asp
    ru, rv = du * c - dvx * s, du * s + dvx * c
    rv *= asp
    ru, rv = ru / sx, rv / sy
    ru = ru - shear * rv
    return (px + ru) * bw + x0, (py + rv) * bh + y0


def apply_pose(img: np.ndarray, bbox, effects: list[dict]) -> np.ndarray:
    """Blend a list of weighted affine effects into one displacement field."""
    shape = img.shape[:2]
    x0, y0, x1, y1 = bbox
    ys, xs = np.mgrid[0:shape[0], 0:shape[1]].astype(np.float32)
    sx, sy = xs.copy(), ys.copy()
    for e in effects:
        wkind = e.get("where", "all")
        wargs = e.get("where_args", {})
        w = WEIGHTS[wkind](shape, bbox, **wargs).astype(np.float32)
        args = {k: v for k, v in e.items() if k not in ("where", "where_args")}
        ex, ey = _affine_inv(shape, bbox, **args)
        # accumulate displacement rather than composing transforms; correct to
        # first order and stable when effects overlap
        sx = sx + (ex - xs) * w
        sy = sy + (ey - ys) * w
    return remap(img, sx, sy)


# --------------------------------------------------------------------------
# the pose library
# --------------------------------------------------------------------------

def _squash(a, ground=1.0):
    return dict(sy=1.0 - a, sx=1.0 + a * 0.55, py=ground)


def _stretch(a, ground=1.0):
    return dict(sy=1.0 + a, sx=1.0 - a * 0.45, py=ground)


def _lean(deg, pivot_y=0.98):
    return dict(rot=np.deg2rad(deg), py=pivot_y)


def _head(dx=0.0, dy=0.0, rot=0.0, split=0.52):
    return dict(where="above", where_args=dict(y=split, soft=0.20),
                tx=dx, ty=dy, rot=np.deg2rad(rot), px=0.5, py=split)


def _tail(rot, cx, cy, rx=0.30, ry=0.30):
    return dict(where="ellipse",
                where_args=dict(cx=cx, cy=cy, rx=rx, ry=ry, soft=0.9),
                rot=np.deg2rad(rot), px=cx, py=cy + ry * 0.7)


def pose_library(tail=None) -> dict[str, list[list[dict]]]:
    """state -> list of frames, each frame a list of effects.

    Amplitudes are tuned for readability at ~70-110px: anything subtler than
    about 1.5% of body height simply does not survive to the pixel grid, and
    anything past ~10 degrees of lean starts to shear the outline.
    """
    T = (lambda a: [_tail(a, **tail)]) if tail else (lambda a: [])

    return {
        # breathing: a 2.5% squash is the smallest that moves a whole pixel
        "idle": [
            [],
            [_squash(0.028), _head(dy=0.012)] + T(-3),
        ],
        # battle idle is the same breath with a weight shift, read as a lean
        "idle_battle": [
            [_lean(0.8)] + T(2),
            [_squash(0.032), _head(dy=0.014, dx=-0.006), _lean(-0.8)] + T(-4),
        ],
        # a bounce walk rather than a leg cycle: there is no rig to swing legs
        # with, and a bounce is what Pokemon's own follower sprites use anyway
        "walk": [
            [_stretch(0.030), _lean(1.5), _head(dy=-0.010)] + T(5),
            [_squash(0.026), _lean(0.0), _head(dy=0.010)] + T(0),
            [_stretch(0.030), _lean(-1.5), _head(dy=-0.010)] + T(-5),
            [_squash(0.026), _lean(0.0), _head(dy=0.010)] + T(0),
        ],
        "attack": [
            [_lean(-7.0), _squash(0.045), _head(dx=0.020, rot=-5)] + T(-12),   # wind up
            [_lean(6.5), _stretch(0.040), _head(dx=-0.030, rot=6),
             dict(tx=-0.045)] + T(14),                                          # strike
            [_lean(1.5), _squash(0.018), _head(dx=-0.010)] + T(4),              # recover
        ],
        "attack_special": [
            [_lean(-4.0), _squash(0.055), _head(dy=0.020, rot=-3)] + T(-8),     # charge
            [_lean(2.0), _stretch(0.055), _head(dy=-0.022, rot=4)] + T(10),     # release
        ],
        "hit": [
            [_lean(-9.0), _squash(0.055), _head(dx=0.032, rot=-8)] + T(-16),
        ],
        "faint": [
            [_squash(0.34), _lean(-5.0), _head(dx=0.055, dy=0.10, rot=-16)] + T(-26),
        ],
        "celebrate": [
            [_stretch(0.075), _head(dy=-0.030, rot=3)] + T(16),
            [_squash(0.070), _head(dy=0.028, rot=-3)] + T(-10),
        ],
        "sleep": [
            [_squash(0.100), _head(dx=0.020, dy=0.030, rot=-7)] + T(-14),
            [_squash(0.130), _head(dx=0.024, dy=0.042, rot=-8)] + T(-18),
        ],
    }
