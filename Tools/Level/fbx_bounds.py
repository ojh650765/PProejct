"""Extract per-asset local-space bounding boxes from the environment kit's binary FBX files.

The environment manifest records triangles, pivots and wind flags but *not* dimensions,
and a layout cannot be authored without knowing how tall a tree is or how wide a house is.
This walks the binary FBX node tree, pulls every ``Geometry/Vertices`` double array, and
reports the min/max/size of the union in the file's own axis convention (Y up, as exported).

Usage:
    python Tools/Level/fbx_bounds.py > Tools/Level/asset_bounds.json
"""

from __future__ import annotations

import json
import os
import struct
import sys
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MANIFEST = os.path.join(
    REPO, "Assets", "Game", "Art", "Environment", "environment_manifest.json"
)

_ARRAY_TYPES = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b"}
_SCALAR_TYPES = {"Y": "h", "C": "?", "I": "i", "F": "f", "D": "d", "L": "q"}


def _read_property(buf: bytes, pos: int):
    """Read one FBX property. Returns (value, new_pos). Arrays come back as lists."""
    kind = chr(buf[pos])
    pos += 1

    if kind in _SCALAR_TYPES:
        fmt = _SCALAR_TYPES[kind]
        size = struct.calcsize(fmt)
        (value,) = struct.unpack_from("<" + fmt, buf, pos)
        return value, pos + size

    if kind in _ARRAY_TYPES:
        length, encoding, comp_len = struct.unpack_from("<III", buf, pos)
        pos += 12
        raw = buf[pos : pos + comp_len]
        pos += comp_len
        if encoding == 1:
            raw = zlib.decompress(raw)
        fmt = _ARRAY_TYPES[kind]
        return list(struct.unpack_from("<%d%s" % (length, fmt), raw, 0)), pos

    if kind in ("S", "R"):
        (length,) = struct.unpack_from("<I", buf, pos)
        pos += 4
        raw = buf[pos : pos + length]
        pos += length
        return raw, pos

    raise ValueError("unknown FBX property type %r at %d" % (kind, pos - 1))


def _walk(buf: bytes, pos: int, end: int, want: str, out: list) -> None:
    """Recursively collect the properties of every node named ``want``."""
    while pos < end:
        end_offset, num_props, _prop_len = struct.unpack_from("<III", buf, pos)
        name_len = buf[pos + 12]
        name = buf[pos + 13 : pos + 13 + name_len].decode("utf-8", "replace")
        pos_props = pos + 13 + name_len

        # A null record (all-zero header) terminates a nested list.
        if end_offset == 0:
            return

        p = pos_props
        props = []
        for _ in range(num_props):
            value, p = _read_property(buf, p)
            props.append(value)

        if name == want:
            out.extend(props)

        # Anything left between the property list and end_offset is nested nodes.
        if p < end_offset - 13:
            _walk(buf, p, end_offset, want, out)

        pos = end_offset


def fbx_bounds(path: str):
    """Return (min[3], max[3]) over every vertex in the file, or None if it has none."""
    with open(path, "rb") as handle:
        buf = handle.read()
    if not buf.startswith(b"Kaydara FBX Binary"):
        raise ValueError("%s is not a binary FBX" % path)

    arrays: list = []
    _walk(buf, 27, len(buf), "Vertices", arrays)

    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    found = False
    for arr in arrays:
        if not isinstance(arr, list) or len(arr) < 3:
            continue
        found = True
        for i in range(0, len(arr) - 2, 3):
            for axis in range(3):
                value = arr[i + axis]
                if value < lo[axis]:
                    lo[axis] = value
                if value > hi[axis]:
                    hi[axis] = value
    return (lo, hi) if found else None


def main() -> int:
    manifest = json.load(open(MANIFEST, encoding="utf-8"))
    result = {}
    for asset in manifest["assets"]:
        path = os.path.join(REPO, asset["path"].replace("/", os.sep))
        if not os.path.exists(path):
            print("MISSING %s" % path, file=sys.stderr)
            continue
        bounds = fbx_bounds(path)
        if bounds is None:
            print("NO VERTS %s" % asset["name"], file=sys.stderr)
            continue
        lo, hi = bounds
        result[asset["name"]] = {
            "family": asset["family"],
            "subfamily": asset["subfamily"],
            "path": asset["path"],
            "pivot": asset["pivot"],
            "min": [round(v, 4) for v in lo],
            "max": [round(v, 4) for v in hi],
            "size": [round(hi[i] - lo[i], 4) for i in range(3)],
        }
    json.dump(result, sys.stdout, indent=1)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
