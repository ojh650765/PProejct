"""Throwaway calibration: back-view construction check."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import numpy as np
from PIL import Image, ImageDraw
import pixelize as P, palette as PAL, views as V

SRC = r"C:/Users/ojh65/AppData/Local/Temp/claude/C--PProejct/8fbd9adb-7cdd-4c1d-a6d3-ce52c84c54e8/scratchpad/repo/data/pokemon_images/002501.png"
OUT = os.path.join(os.path.dirname(__file__), "_verify")

BROWN = (145, 111, 63)
RECIPE = {
    "face_erase": [dict(cx=0.765, cy=0.420, rx=0.235, ry=0.165, soft=0.30)],
    "back_markings": [
        dict(kind="band", cy=0.638, half_h=0.026, cx=0.500, half_w=0.195, bow=0.030, colour=BROWN),
        dict(kind="band", cy=0.722, half_h=0.030, cx=0.520, half_w=0.215, bow=0.030, colour=BROWN),
    ],
}

src = P.load_rgba(SRC)
bbox = P.alpha_bbox(src)
back, bbox_b = V.build_back(src, bbox, RECIPE)

smask = src[..., 3] > 0.5
mats = PAL.segment_materials(src[..., :3], smask, n_clusters=9)

H, zoom = 77, 6
panels = []
for label, img, bb in (("front src", src, bbox), ("back src", back, bbox_b)):
    hi = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8), "RGBA")
    hi = hi.crop(bb).resize((int((bb[2]-bb[0]) * H / (bb[3]-bb[1]) * zoom * 0.42), int(H * zoom * 0.42)), Image.LANCZOS)
    panels.append((label + " (hi-res)", hi))
    small, mask = P.pixelize(img, H, bbox=bb)
    q, midx = PAL.quantise(small[..., :3], mask, mats)
    q = P.add_outline(q, mask, midx, mats)
    a = np.concatenate([q, mask[..., None].astype(np.float32)], 2)
    im = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8), "RGBA")
    panels.append((label.replace(" src", "") + f" {small.shape[1]}x{small.shape[0]}",
                   im.resize((im.width * zoom, im.height * zoom), Image.NEAREST)))

cw = max(p[1].width for p in panels) + 24
ch = max(p[1].height for p in panels) + 30
sheet = Image.new("RGB", (cw * len(panels), ch), (240, 240, 236))
d = ImageDraw.Draw(sheet)
for i, (label, im) in enumerate(panels):
    sheet.paste(im, (i * cw + (cw - im.width) // 2, 24), im if im.mode == "RGBA" else None)
    d.text((i * cw + 8, 6), label, fill=(10, 10, 10))
sheet.save(os.path.join(OUT, "calib_back.png"))
print(sheet.size)
