"""Builds the dialogue-box portraits from the trainer sprites.

The box used to draw the speaker's overworld walking sprite: a 32-pixel figure seen from
the waist up in a frame 700 pixels tall, standing in the corner of it. It read as a map
marker that had wandered into the UI, not as the person talking.

These are built from `source_trainers/` instead -- the trainer sprites, which are drawn
facing the viewer at two to four times the detail and are the art the games themselves put
next to a line of dialogue.

Each one is cropped to the bust. The crop is measured from the figure's own opaque bounds
rather than from the canvas, because the sprites are not consistently placed inside their
frames -- Rowan sits low in a 64x128 sheet with his case beside him, the 80x80 trainers are
roughly centred, and a fixed rectangle would behead one and leave the other adrift. From
those bounds the head and torso are the top BUST_FRACTION of the figure, and the crop is
centred on the figure's own horizontal midpoint.

Scaled up with NEAREST. These are pixel drawings and the whole game is committed to that
look; a smooth upscale would make the portrait the one soft thing on the screen.

Run:  python Tools/Sprites/build_portraits.py
"""

import io
import json
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE = os.path.join(ROOT, "Tools", "Sprites", "source_trainers")
OUT = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Resources", "Portraits")

sys.path.insert(0, os.path.join(ROOT, "Tools", "Sprites"))
import unity_meta  # noqa: E402

# How much of the figure, measured down from the top of its own opaque bounds, is kept.
#
# All of it. A bust crop was tried and rejected: these sprites are drawn as whole standing
# figures, and the things that make them read as a person -- Rowan's stance, his hand on his
# hip, the case at his feet -- are all below the chest. Cropping to the head and shoulders
# throws away the half of the drawing that has the character in it.
BUST_FRACTION = 1.0

# Breathing room around the crop, as a fraction of the figure's width. Without it a head
# touching the top of its bounds ends up touching the top of the frame.
PAD_X = 0.14
PAD_TOP = 0.10
PAD_BOTTOM = 0.04

SCALE = 6

# Which speaker key takes which sprite.
#
# Keyed by the same role names PersonSpriteLibrary resolves to, so a speaker id already
# normalised for the world sprite finds its portrait without a second mapping. Roles with
# no trainer sprite are absent on purpose: DialoguePortraits returns null for them and the
# box falls back to the world sprite, which is worse but is not wrong.
CAST = {
    "professor": "professor.png",
    "rival": "rival.png",
    "player": "lucas.png",
    "player_f": "dawn.png",
    "youngster": "youngster.png",
    "lass": "lass.png",
    "hiker": "hiker.png",

    # The rest of the cast that actually speaks. Only five roles have lines in the book --
    # Linden, Kes, Bram, Sela and Odell -- so only those need a face, and each is mapped to
    # the nearest authored trainer class rather than to whatever was closest alphabetically.
    #
    # Bram is the Ruin Maniac: an older man in workwear, which is what "가서 얘기부터 듣고 오게"
    # sounds like. He was briefly the Roughneck, a bald tattooed biker, and a face that reads
    # as a thug turns his apology into a threat.
    #
    # Roles with no counterpart in the trainer classes are deliberately absent. The box falls
    # back to their walk sprite, which is worse than a portrait and much better than handing
    # somebody a face that belongs to a different person.
    "townsman": "townsman.png",       # Bram, at the gate
    "shopkeeper": "shopkeeper.png",   # Sela, at the market
    "gardener": "gardener.png",       # Odell, in the plots
}

# The same people seen from behind, for the battle arena.
#
# The person who throws the ball is looked at from behind, because the camera stands behind
# them -- and the sprites above are all drawn facing the viewer, so none of them can be used
# for it. What the games use is a separate set: `res/graphics/battle/trainer_backs/` in
# pokeplatinum, the sprite that winds up and throws at the start of every battle. That is the
# art these are cut from, and there is no other: no game in the series draws a trainer's back
# at full length, so these stop at the hip and TrainerView sizes them from the crown down
# rather than standing them on the ground.
BACKS = {
    "player_back": "lucas_back.png",
    "player_f_back": "dawn_back.png",
    "rival_back": "barry_back.png",
}

# The whole throw, not one pose from it.
#
# The strip holds five drawn frames -- wind-up, cock, release, follow-through, recover -- which
# is the animation the games play, and the first version of this kept only frame 0 because the
# battle drew a still. It does not have to: a trainer who appears, holds one pose and vanishes
# while a ball leaves their hand from nowhere is the throw not happening.
#
# Emitted as one horizontal sheet so the runtime can step through it with a UV offset, which is
# how every other sprite in this project is animated. Frames are laid out left to right in
# their drawn order.
THROW_FRAMES = 5


def bounds(image):
    """The figure's own opaque box, or the whole canvas if nothing is transparent."""
    alpha = image.getchannel("A")
    box = alpha.getbbox()
    return box if box else (0, 0, image.width, image.height)


def bust(image):
    """Crops to the head and torso, centred on the figure."""
    left, top, right, bottom = bounds(image)
    width = right - left
    height = bottom - top

    keep = max(1, int(round(height * BUST_FRACTION)))
    pad_x = int(round(width * PAD_X))
    pad_top = int(round(height * PAD_TOP))

    centre = (left + right) / 2.0
    half = width / 2.0 + pad_x

    crop = (
        int(round(centre - half)),
        top - pad_top,
        int(round(centre + half)),
        top + keep + int(round(height * PAD_BOTTOM)),
    )

    # Clamped rather than allowed to run off the canvas: PIL will happily crop outside the
    # image and fill with black, which on a portrait shows up as a hard bar down one side.
    crop = (
        max(0, crop[0]),
        max(0, crop[1]),
        min(image.width, crop[2]),
        min(image.height, crop[3]),
    )
    return image.crop(crop)


def strip_frame(image, index):
    """One frame of a vertical animation strip, found by the blank rows between poses.

    Measured rather than divided. The trainer-back strips are 80x404 for five poses, which is
    80.8 rows each -- not a whole number, and the figure moves up and down inside its cell as
    it winds up, so a fixed pitch clips a hand off one frame and leaves a shoulder in the next.
    The gaps between the poses are fully transparent, and those are unambiguous.
    """
    alpha = image.getchannel("A")
    width, height = image.size

    runs, start = [], None
    for y in range(height):
        row = alpha.crop((0, y, width, y + 1))
        occupied = row.getbbox() is not None
        if occupied and start is None:
            start = y
        elif not occupied and start is not None:
            runs.append((start, y))
            start = None
    if start is not None:
        runs.append((start, height))

    if not runs:
        return image
    top, bottom = runs[min(index, len(runs) - 1)]
    return image.crop((0, top, width, bottom))


TEMPLATE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "portrait_meta_template.yaml")


def write_meta(png_path, key):
    """Import settings that make the PNG a Sprite.

    unity_meta's template is for raw textures -- textureType 0, spriteMode 0 -- and a PNG
    imported that way is a Texture2D, not a Sprite. Resources.Load<Sprite> then returns null
    for a file that is sitting right there, the portrait lookup falls through to the walking
    sprite, and nothing anywhere reports a problem. That cost a full round of "it did not
    change".

    So these carry their own: sprite type, single mode, point filter and no compression,
    taken from a sprite the project already imports correctly.
    """
    meta = png_path + ".meta"
    guid = unity_meta.existing_guid(meta) or unity_meta.stable_guid(key)
    with io.open(TEMPLATE, encoding="utf-8") as fh:
        text = fh.read().replace("{guid}", guid)
    with io.open(meta, "w", encoding="utf-8", newline=chr(10)) as fh:
        fh.write(text)


def build(key, filename, frame=None):
    path = os.path.join(SOURCE, filename)
    if not os.path.exists(path):
        return None, f"{filename} is not in source_trainers/"

    image = Image.open(path).convert("RGBA")

    # Palette sprites carry their transparency as an index, and converting to RGBA keeps a
    # fully opaque background unless it is keyed out first. The corner pixel is background
    # in every sheet in this set.
    if Image.open(path).mode == "P":
        original = Image.open(path)
        if "transparency" not in original.info:
            key_colour = image.getpixel((0, 0))[:3]
            pixels = image.load()
            for y in range(image.height):
                for x in range(image.width):
                    r, g, b, a = pixels[x, y]
                    if (r, g, b) == key_colour:
                        pixels[x, y] = (r, g, b, 0)

    # A strip becomes a sheet: every pose cut out, cropped to a shared box, laid left to right.
    if frame == "sheet":
        return build_sheet(key, image)

    # A strip is cut to one pose before the bust crop, because bust() measures the figure's
    # own opaque bounds and those are the whole strip when five poses are still stacked in it.
    if frame is not None:
        image = strip_frame(image, frame)

    cropped = bust(image)
    scaled = cropped.resize(
        (cropped.width * SCALE, cropped.height * SCALE), Image.NEAREST)

    os.makedirs(OUT, exist_ok=True)
    out_path = os.path.join(OUT, key + ".png")
    scaled.save(out_path)
    write_meta(out_path, "portrait_" + key)
    return scaled.size, None


def build_sheet(key, image):
    """Every pose of a throw strip, side by side in one texture.

    The frames are cropped to a *shared* box rather than to each pose's own bounds. Cropping
    each one tightly would be smaller, but the figure would then jump around inside its cell
    from frame to frame -- the thing that reads as a throw is the arm moving while the body
    stays put, and per-frame crops destroy exactly that.
    """
    poses = [strip_frame(image, i) for i in range(THROW_FRAMES)]

    # The union of every pose's bounds, so nothing is clipped and every cell aligns.
    boxes = [bounds(p) for p in poses]
    left = min(b[0] for b in boxes)
    top = min(b[1] for b in boxes)
    right = max(b[2] for b in boxes)
    bottom = max(b[3] for b in boxes)

    pad_x = int(round((right - left) * PAD_X))
    pad_top = int(round((bottom - top) * PAD_TOP))
    box = (max(0, left - pad_x), max(0, top - pad_top),
           min(poses[0].width, right + pad_x), bottom)

    cell_w = box[2] - box[0]
    cell_h = box[3] - box[1]

    sheet = Image.new("RGBA", (cell_w * THROW_FRAMES, cell_h), (0, 0, 0, 0))
    for i, pose in enumerate(poses):
        sheet.paste(pose.crop(box), (i * cell_w, 0))

    scaled = sheet.resize((sheet.width * SCALE, sheet.height * SCALE), Image.NEAREST)

    os.makedirs(OUT, exist_ok=True)
    out_path = os.path.join(OUT, key + ".png")
    scaled.save(out_path)
    write_meta(out_path, "portrait_" + key)
    return scaled.size, None


def main():
    if not os.path.isdir(SOURCE):
        print(f"No {SOURCE}; nothing to build.")
        return 1

    jobs = [(key, filename, None) for key, filename in sorted(CAST.items())]
    jobs += [(key, filename, "sheet") for key, filename in sorted(BACKS.items())]

    built, problems = 0, []
    for key, filename, frame in jobs:
        size, problem = build(key, filename, frame)
        if problem:
            problems.append(problem)
            continue
        print(f"  {key:<14} {filename:<18} -> {size[0]}x{size[1]}")
        built += 1

    print(f"\n{built} portrait(s) in Assets/Game/Art/Sprites/Resources/Portraits/")
    for problem in problems:
        print(f"  missing: {problem}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
