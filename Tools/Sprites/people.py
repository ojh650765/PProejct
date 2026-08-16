"""Cast table and canvas policy for the human cast.

This is the people-side companion to species.py.  It is a *second* table, not a
restructuring of the first: the creature cast and the human cast come from
different games, are drawn at different pixel densities, and share only the
column count of the packed sheet.  species.py is imported for that one shared
constant and for nothing else.

Where the creatures came from
-----------------------------
The 53 creatures are official Gen 5 (Black/White) **battle** sprites: 96x96
canvases, one looping idle per view, front and back only.

Where the people come from
--------------------------
The people are official Gen 4 (Platinum) **overworld** sprites, taken from the
`pret/pokeplatinum` decompilation, which checks its field sprites into the
repository as plain indexed PNGs under `res/graphics/field_sprites/`.  That is
the distinction that matters and it is not a small one: `source_trainers/`
already held 80x80 *battle* portraits of fifteen protagonists and trainer
classes, and a battle portrait is a completely different asset from an
overworld sprite.  It is drawn at a different size, from a different angle, in
a different pose, with only one view, and it cannot be walked around a map.

Every character below is traced -- that is, taken pixel-for-pixel -- from that
source.  Nothing in this cast is drawn by hand.  See `origin` on each row.

The sheet the source gives us
-----------------------------
One PNG per character, 32 px wide and 32*N tall: a vertical strip of 32x32
cells.  Sixteen cells for an NPC, thirty-two for a player character.  The
ordering was not documented anywhere reachable, so it was read off a contact
sheet and then confirmed numerically against every file in the cast:

    cells  0.. 3   back   (walking away from the lens)
    cells  4.. 7   front  (walking toward the lens)
    cells  8..11   side   (walking toward screen-left)
    cells 12..15   side   -- an exact horizontal mirror of 8..11, byte for byte
    cells 16..31   the same four groups again, for the run cycle (players only)

and within each group of four:

    +0   standing pose        <- byte-identical to +2 in every file checked
    +1   step, one leg leading
    +2   standing pose again
    +3   step, other leg leading

So a walk is the four-beat `0 1 0 3`, and an idle is cell `+0` held.  Cells
12..15 are dropped on the way in: they are a stored mirror, and the runtime
already mirrors by flipping UVs.  Keeping them would double the texture for no
new artwork.

Canvas policy
-------------
The full derivation, including the number that does not work out cleanly, is
in the module constants below.
"""

from __future__ import annotations

import os

import species as _species

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE_DIR = os.path.join(HERE, "source_overworld")

# --------------------------------------------------------------------------
# canvas policy
# --------------------------------------------------------------------------
#
# 32x32 is the size the source is already in, and it stays that size.  The rule
# extract.py states -- official pixel art is never rescaled -- is not relaxed
# here, and the temptation to relax it is real, so it is worth writing down why
# rescaling would buy nothing.
#
# The creature pipeline pins 1 world metre to 96 texels (species.PPU).  A human
# is about 1.7 m, so at that density a person would have to be 163 texels tall.
# The source figure is 23.  Reaching 163 means a 7x nearest-neighbour blow-up.
#
# That blow-up is pointless.  A 7x-magnified sprite drawn 1.7 m tall and a
# native sprite drawn 1.7 m tall rasterise to *the same pixels on screen* --
# the only difference is that the first wastes 49x the texture memory and
# invites someone downstream to filter it.  So the art stays native and the
# scale is carried by PPU instead, which is where scale belongs.
CELL = 32
COLS = _species.COLS  # 8 -- the one thing the two casts share

# Ground contact row, in every cell of every character.
#
# Measured, not chosen: every 16-cell NPC sheet in this cast has its standing
# frames bottoming out on row 29 exactly, and its stepping frames on row 30.
# The source is already registered to a common floor, because the game draws
# these from a fixed object origin.  So the correct alignment is the identity,
# and extract_people.py asserts that the translation it computes really is
# zero rather than quietly translating.
#
# The 1 px the step frames hang below is the walk's own bob, authored into the
# artwork.  It is preserved, not flattened -- flattening it would make the
# character glide instead of walk.  Two rows of slack remain below it.
GROUND_ROW = 29

# Texels per world metre, for people only.
#
# This is the one number in the project where the two source sets genuinely
# cannot be reconciled, so it is worth being exact about what was chosen and
# what it costs.
#
# Gen 5 battle art and Gen 4 field art are drawn at densities that differ by
# about 7:1.  There is no version of this project in which they match, because
# they were never meant to appear together: in the games these came from, the
# 96 px Pikachu is only ever seen in a battle scene and the 32 px townsfolk are
# only ever seen on a map.
#
# 96/7 is chosen over a rounder number (14 was the alternative) because an
# exact 7:1 ratio locks the two sets to a common screen texel grid: whatever
# integer magnification the creatures are drawn at, the people land on an
# integer magnification too, and neither set shimmers as the camera moves.  A
# ratio of 96/14 = 6.857 would put them permanently out of phase.
#
# What it costs, stated plainly: a person's texels are 7x the width of a
# creature's.  A player standing beside a Pikachu is visibly blockier than the
# Pikachu is.  That is inherent in the source material and no canvas policy
# fixes it; the only real fix is to redraw the overworld creatures from Gen 4
# field art (pokeplatinum ships those too, at this same density) and that is
# the creature pipeline's decision to make, not this one's.
PPU = 96.0 / 7.0  # 13.7143 texels per metre

# What that lands the cast at, in metres, measured from the standing pose:
#
#     child        21 px -> 1.53      townswoman   24 px -> 1.75
#     player       23 px -> 1.68      townsman     25 px -> 1.82
#     elder_man    23 px -> 1.68      hiker        26 px -> 1.90
#
# A 1.68 m teenage protagonist, adults from 1.75 to 1.82, a stooped elder at
# 1.68 and a heavy-set hiker at 1.90.  The spread is authored into the source
# artwork, not applied here -- every character is placed on the same canvas and
# its height is whatever it was drawn as.

# --------------------------------------------------------------------------
# the source sheet's frame layout
# --------------------------------------------------------------------------

# Cell index each direction's group starts at, within a 16-cell block.
GROUP = {"back": 0, "front": 4, "side": 8}

# The mirrored group that is deliberately not imported.
MIRROR_GROUP = 12

# Cells per direction group.
GROUP_LEN = 4

# Offsets within a group, in play order, for each clip.
CLIP_OFFSETS = {
    "idle": (0,),
    "walk": (0, 1, 0, 3),
}

# Milliseconds per step of the walk.
#
# Derived rather than sourced, and flagged as such.  The frame-sequence files
# that ship beside the artwork (`frame_sequences/generic_walk.bin`) turned out
# to hold a cell *remap* table -- a permutation of 0..15 -- and no timing at
# all, so there is no authored duration to read.  133 ms is the DS's own
# cadence: the field engine runs at 59.83 Hz and advances the walk pose every
# eighth frame, which is 8 / 59.83 = 133.7 ms.  The full four-beat cycle is
# therefore 535 ms.
#
# This is the one number in the human pipeline that is a reconstruction. It is
# a single constant so that it can be retuned against the real thing later
# without touching anything else.
WALK_STEP_MS = 133

# The run cycle is the same four beats at double rate; DPPt runs one tile per
# eight frames rather than sixteen.
RUN_STEP_MS = 67

# --------------------------------------------------------------------------
# states the source does not contain
# --------------------------------------------------------------------------
#
# A Gen 4 overworld sheet holds a standing pose and a four-beat walk.  That is
# all it holds.  There is no talking pose, no gesture, no presenting pose --
# not because they were missed, but because DPPt does not animate an NPC while
# it speaks: the text box carries the beat, and the only in-place motion the
# engine has is a whole-sprite hop for the "!" reaction.
#
# The opening needs the professor to hold the screen while he talks, so
# something has to move.  Three ways to get it were tried and two were thrown
# away; what is recorded here is the one that survived.
#
#   REJECTED -- re-sequencing the walk.  Playing stand/step slowly in place is
#   made of official cells, but the step frames drop the whole figure one pixel
#   and put the feet on row 30, so a talking professor would rhythmically sink
#   into the floor.
#
#   REJECTED -- a composed "settle": the body translated down one pixel with
#   the feet held on row 29.  Clean on the professor, whose lab coat covers the
#   seam, and visibly wrong on most of the cast: the composite necessarily
#   deletes one source row, and no character in this cast has a duplicated row
#   anywhere to delete safely (checked, all 16, rows 18-29).  On the youngster,
#   the lass and the child it shortens the legs by a pixel and it shows at 18x.
#
#   REJECTED -- drawing a raised arm.  Authored against the professor's own
#   14-colour palette at his own outline weight, rendered at 20x, and looked
#   at: it read as a slab floating beside him, not as an arm.  One hand-drawn
#   cell among two hundred traced ones is worse than none.
#
# What ships instead is a recipe, in the same form and the same vocabulary
# extract.py already uses for the creatures' PROCEDURAL_STATES: a runtime
# transform of frames that exist, specified in whole pixels, rather than
# artwork that does not.  Nothing here is a texture; all of it is a note to
# whoever wires the billboard.
PROCEDURAL_STATES = {
    "Talk": dict(
        base="idle", recipe=(
            "Hold the idle cell. Translate the whole sprite up 1 px on a 0.45 s "
            "square wave -- up for 0.15 s, down for 0.30 s -- snapped to whole "
            "pixels. This is Gen 4's own in-place vocabulary (its '!' reaction "
            "is a whole-sprite hop) scaled down to a breath. Translating the "
            "WHOLE sprite is the point: it keeps the figure rigid, so nothing "
            "is deleted, stretched or redrawn, and the 1 px ground break reads "
            "as emphasis rather than as a character sinking."),
        note="Needed by the opening. No artwork exists for it and none is faked."),
    "TalkEmphasis": dict(
        base="idle", recipe=(
            "As Talk, but 2 px and a 0.3 s wave, for a line that lands. Use "
            "sparingly -- at 2 px this reads as a bounce.")),
    "Present": dict(
        base="idle", recipe=(
            "Hold the idle cell facing the player. The gesture is carried by "
            "the prop, not the sprite: place PROPS['briefcase'] on the ground "
            "in front of the character and open it. This is how DPPt itself "
            "stages the starter choice -- the case is a field object with its "
            "own sprite, never part of the professor's sheet -- so it is the "
            "one beat that needs no new character art at all."),
        note="Pairs with PROPS['briefcase'] and PROPS['player_takes_ball']."),
    "Turn": dict(
        base="idle", recipe=(
            "Snap between the idle cells of the two facings, no blend, one "
            "frame of the intermediate facing if turning 180 degrees. Sprite "
            "games turn by swapping the image; interpolating rotates a "
            "billboard and reads as the character sliding.")),
}

# --------------------------------------------------------------------------
# props
# --------------------------------------------------------------------------
#
# Field objects, not characters: one cell, no directions, no walk.  They are
# here because two beats of the opening are carried by a prop in the original
# games rather than by a character pose, and staging them that way is both
# more faithful and entirely traced.
#
# (key, source basename, cells, display name, note)
PROPS = [
    ("briefcase", "briefcase", 1, "Starter Briefcase",
     "The case the starters come out of, as a field object on the same 32x32 "
     "cell and the same ground row as the cast. 14 px tall = 1.02 m of "
     "footprint at this PPU, which is right for a case on the floor."),
    ("player_takes_ball", "player_m_holding_pokeball", 4, "Player Takes Ball",
     "Four official cells of the player raising a ball overhead. Front view "
     "only -- it is a scripted beat, always played to camera. This is real "
     "gesture artwork, which is exactly what the professor has none of."),
]

PROPS_BY_KEY = {p[0]: p for p in PROPS}

# Which way the imported side group faces.
#
# Read off a contact sheet at 12x: in cells 8..11 the face, and the leading eye,
# sit on the LEFT of the cell -- the character is walking toward screen-left.
#
# Expressed the way the runtime enum talks about it, that is the view in which
# the subject's LEFT flank is toward the lens.  (Subject facing -X with the
# camera on -Z: the subject's right vector points to +Z, away from the lens, so
# what the lens sees is the left flank.)  Both phrasings are recorded in the
# emitted manifest, because CreatureBillboard's own two descriptions of this
# disagree with each other -- see the note in extract_people.py.
SIDE_WALKS_TOWARD = "screen_left"
SIDE_FLANK_TO_LENS = "left"

# --------------------------------------------------------------------------
# the cast
# --------------------------------------------------------------------------
#
# (key, source basename, role, display name, note)
#
# `source` is the file under res/graphics/field_sprites/ in pret/pokeplatinum,
# staged into source_overworld/ by fetch_people.py.  Every one of them is
# official artwork traced pixel-for-pixel; none is drawn here.
#
# The residents are chosen against the brief that a town is not a roster of
# trainer classes.  None of the eight wears a class silhouette: no cap-and-
# shorts, no rucksack, no fishing rod.  They are an elderly couple, a
# shopkeeper, a gardener, a labourer, a middle-aged pair and a child.
CAST = [
    # --- the player ------------------------------------------------------
    ("player", "player_m", "player", "Player",
     "Platinum male protagonist. 32 cells: walk and run."),
    ("player_f", "player_f", "player", "Player (female)",
     "Platinum female protagonist. Built so a gender select is possible; the "
     "opening does not require her. 32 cells: walk and run."),

    # --- the opening's two speaking parts --------------------------------
    # These two carry the first five minutes, so they are built before the
    # generic residents and both are chosen for silhouette rather than
    # convenience.
    ("professor", "prof_oak", "professor", "Professor Oak",
     "Swept grey hair, white lab coat over a mauve shirt. The white coat mass "
     "is the read: no other character in the cast has it, so he is legible "
     "from the three-quarter camera before any dialogue starts. Kanto's "
     "professor, which is also the right one for a cast of 53 Kanto species."),
    ("rival", "barry", "rival", "Rival",
     "Blond spiked hair, orange-striped shirt, green scarf. The Diamond/Pearl "
     "rival, whose role in the opening is exactly the one being written -- he "
     "takes the starter that beats yours and fights you on the spot. Chosen "
     "over reusing the female protagonist because he has to read as a "
     "different person from the player at a glance, not as the other slot."),

    # --- town residents --------------------------------------------------
    ("elder_man", "old_man", "resident", "Old Man",
     "Bald, grey beard, long green coat. Stooped -- shortest adult in the cast."),
    ("elder_woman", "old_woman", "resident", "Old Woman",
     "Grey bun, spectacles, apron."),
    ("shopkeeper", "cashier_f", "resident", "Shopkeeper",
     "Shop apron over a plain top. The market stallholder."),
    ("gardener", "rancher", "resident", "Gardener",
     "Wide brimmed hat and dungarees. Reads as someone who works outdoors "
     "rather than someone who battles."),
    ("labourer", "worker", "resident", "Labourer",
     "Hard hat and work overalls."),
    ("townsman", "middle_aged_man", "resident", "Townsman",
     "Middle-aged, plain shirt, no class marker. Tallest ordinary adult."),
    ("townswoman", "middle_aged_woman", "resident", "Townswoman",
     "Middle-aged, dark dress."),
    ("child", "twin", "resident", "Child",
     "Small girl with red hair ties. 21 px -- the shortest of the cast."),

    # --- overworld trainers ----------------------------------------------
    # These four are the classes slice_layout.json's gameplay.trainers names.
    # They ARE meant to read as trainers, which is exactly why none of them is
    # reused above.
    ("youngster", "youngster", "trainer", "Youngster",
     "Cap and shorts. The canonical route trainer."),
    ("lass", "lass", "trainer", "Lass",
     "Olive bob, orange skirt."),
    ("fisher", "fisherman", "trainer", "Fisher",
     "Red cap and vest."),
    ("hiker", "hiker", "trainer", "Hiker",
     "Broad hat, heavy pack, stocky. Tallest character in the cast at 26 px."),
]

BY_KEY = {c[0]: c for c in CAST}

# --------------------------------------------------------------------------
# where the level expects each of them
# --------------------------------------------------------------------------
#
# Assets/Game/Data/Levels/slice_layout.json places six NPCs and four trainers
# and gives them ids but no art.  This is the binding, kept here so the level
# worker and this pipeline have one shared answer rather than two guesses.
#
# Two of the level's NPC objects are named after trainer classes (NPC_Fisher,
# NPC_Hiker) but carry displayName "Townsfolk", so they are given resident art:
# a town's fisherman is a man who fishes, not a Fisher who wants to battle you.
# The Fisher and Hiker *trainers* are separate objects and keep the class art.
PLACEMENT = {
    # NPCs -- keyed by npcId in slice_layout.json gameplay.npcs
    "npc_market_01": "shopkeeper",
    "npc_rival_01": "rival",
    "npc_gate_01": "townsman",
    "npc_garden_01": "gardener",
    "npc_fisher_01": "elder_man",
    "npc_hiker_01": "labourer",
    # NOTE: there is no NPC object for the professor. slice_layout.json places
    # six NPCs and none of them is him, even though the layout has a lab
    # (Town_Lab_01 at -1.453, 2.728, -5.691) and the opening turns on him
    # standing in it. The sprite is built and bound here; the level still needs
    # an NPC object added for him, and that is not this pipeline's file to
    # edit. Same for the briefcase, which wants to be a field object on the lab
    # floor in front of him.
    # Trainers -- keyed by trainerId in slice_layout.json gameplay.trainers
    "trainer_route_01": "youngster",
    "trainer_route_02": "lass",
    "trainer_lake_01": "fisher",
    "trainer_cave_01": "hiker",
}

# elder_woman, townswoman and child are built but not placed: the slice only
# names six NPCs, and a town needs more faces than it has scripted roles.


def sheet_path(source: str) -> str:
    return os.path.join(SOURCE_DIR, f"{source}.png")
