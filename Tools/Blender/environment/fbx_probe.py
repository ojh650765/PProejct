"""Read the parts of a binary FBX that decide how Unity scales and orients it.

Written because the integrator reported the whole kit arriving in Unity at 1/100
scale and lying on its back, and neither fault is visible from Blender -- both
live in the FBX's GlobalSettings block and in whether the exporter baked its
axis conversion into the vertex data.  This prints, per file:

    UnitScaleFactor / OriginalUnitScaleFactor   what the file says a unit is
    UpAxis / FrontAxis / CoordAxis (+ signs)    what the file says up is
    vertex extents                              what is actually in the file
    Model PreRotation / Lcl Scaling             any compensation baked on nodes

Usage:  python Tools/Blender/environment/fbx_probe.py <file.fbx> [more.fbx ...]
"""

from __future__ import annotations

import os
import struct
import sys
import zlib

_ARRAY_TYPES = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b"}
_SCALAR_TYPES = {"Y": "h", "C": "?", "I": "i", "F": "f", "D": "d", "L": "q"}


def _read_property(buf, pos):
    kind = chr(buf[pos])
    pos += 1
    if kind in _SCALAR_TYPES:
        fmt = _SCALAR_TYPES[kind]
        (value,) = struct.unpack_from("<" + fmt, buf, pos)
        return value, pos + struct.calcsize(fmt)
    if kind in _ARRAY_TYPES:
        length, encoding, comp_len = struct.unpack_from("<III", buf, pos)
        pos += 12
        raw = buf[pos:pos + comp_len]
        pos += comp_len
        if encoding == 1:
            raw = zlib.decompress(raw)
        fmt = _ARRAY_TYPES[kind]
        return list(struct.unpack_from("<%d%s" % (length, fmt), raw, 0)), pos
    if kind in ("S", "R"):
        (length,) = struct.unpack_from("<I", buf, pos)
        pos += 4
        raw = buf[pos:pos + length]
        return raw, pos + length
    raise ValueError("unknown FBX property type %r at %d" % (kind, pos - 1))


def _walk(buf, pos, end, visit):
    while pos < end:
        end_offset, num_props, _ = struct.unpack_from("<III", buf, pos)
        if end_offset == 0:
            return
        name_len = buf[pos + 12]
        name = buf[pos + 13:pos + 13 + name_len].decode("utf-8", "replace")
        p = pos + 13 + name_len
        props = []
        for _ in range(num_props):
            value, p = _read_property(buf, p)
            props.append(value)
        visit(name, props)
        if p < end_offset - 13:
            _walk(buf, p, end_offset, visit)
        pos = end_offset


def probe(path):
    with open(path, "rb") as h:
        buf = h.read()
    if not buf.startswith(b"Kaydara FBX Binary"):
        raise ValueError("%s is not a binary FBX" % path)
    want = {"UpAxis", "UpAxisSign", "FrontAxis", "FrontAxisSign", "CoordAxis",
            "CoordAxisSign", "UnitScaleFactor", "OriginalUnitScaleFactor",
            "PreRotation", "Lcl Scaling", "Lcl Rotation", "GeometricRotation"}
    found = {}
    verts = []

    def visit(name, props):
        if name == "Vertices" and props and isinstance(props[0], list):
            verts.append(props[0])
        if name == "P" and props:
            key = props[0].decode("utf-8", "replace") if \
                isinstance(props[0], bytes) else str(props[0])
            if key in want:
                found.setdefault(key, []).append(props[4:])

    _walk(buf, 27, len(buf), visit)

    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for arr in verts:
        for i in range(0, len(arr) - 2, 3):
            for a in range(3):
                v = arr[i + a]
                lo[a] = min(lo[a], v)
                hi[a] = max(hi[a], v)
    size = [round(hi[a] - lo[a], 4) for a in range(3)] if verts else None
    return {"path": path, "found": found, "size": size,
            "min": [round(v, 4) for v in lo] if verts else None,
            "max": [round(v, 4) for v in hi] if verts else None}


AXIS_NAME = {0: "X", 1: "Y", 2: "Z"}


def report(path):
    r = probe(path)
    f = r["found"]

    def one(key, default=None):
        vals = f.get(key)
        if not vals:
            return default
        v = vals[0]
        return v[0] if v else default

    up = one("UpAxis")
    ups = one("UpAxisSign", 1)
    front = one("FrontAxis")
    fronts = one("FrontAxisSign", 1)
    coord = one("CoordAxis")
    coords = one("CoordAxisSign", 1)
    unit = one("UnitScaleFactor")
    orig = one("OriginalUnitScaleFactor")
    print("%s" % os.path.basename(path))
    print("  UnitScaleFactor      %s   (OriginalUnitScaleFactor %s)"
          % (unit, orig))
    print("  Up %s%s  Front %s%s  Coord %s%s"
          % ("-" if ups == -1 else "+", AXIS_NAME.get(up, up),
             "-" if fronts == -1 else "+", AXIS_NAME.get(front, front),
             "-" if coords == -1 else "+", AXIS_NAME.get(coord, coord)))
    print("  vertex min %s" % (r["min"],))
    print("  vertex max %s" % (r["max"],))
    print("  vertex size %s" % (r["size"],))
    for key in ("PreRotation", "Lcl Rotation", "Lcl Scaling"):
        if key in f:
            uniq = sorted({tuple(round(x, 4) for x in v) for v in f[key]})
            print("  %-14s %s" % (key, uniq[:4]))
    print("")
    return r


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    for p in argv[1:]:
        report(p)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
