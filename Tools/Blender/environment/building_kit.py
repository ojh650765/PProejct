"""Compact Sinnoh-inspired building kit. Metres, street facing Blender -Y.

Every generator returns the real shell opening used for Unity's entrance anchor.
The entrance aisle stays free of posts; thresholds are below a character step.
"""
import townlib as TL

CREAM, BLUE, RED, WOOD = 0, 1, 2, 3
ROOF_RED, ROOF_BLUE, ROOF_GREEN = 4, 5, 6
GLASS, DOOR, PAVING, STONE, TRIM = 8, 9, 10, 11, 15


def building(bm, width, depth, eave, roof, plaster=CREAM, upper=False,
             centre=False):
    poly = [(-width/2, -depth/2), (width/2, -depth/2),
            (width/2, depth/2), (-width/2, depth/2)]
    thickness = .24
    shell = TL.Shell(bm, poly, 0, eave, thickness, plaster, plaster)
    doorway = shell.add_opening(0, 1.9 if centre else 1.3, 2.18, 0, .5, 'door')
    windows = [shell.add_opening(0, .78 if not centre else 1.2, .94, 1.08,
                                s, 'window') for s in (.19, .81)]
    windows += [shell.add_opening(side, .85, .94, 1.08, s, 'window')
                for side in (1, 3) for s in (.3, .7)]
    windows += [shell.add_opening(2, .85, .94, 1.08, s, 'window')
                for s in (.25, .75)]
    if upper:
        windows += [shell.add_opening(side, .8, .9, 3.25, s, 'window')
                    for side in (0, 2) for s in (.25, .75)]
    shell.build()
    assert not shell.assert_openings(), shell.assert_openings()
    # A shallow foundation, with no waist-high solid spanning the doorway.
    TL.solid_prism(bm, [(x*1.018, y*1.02) for x,y in poly], -.025, .075, STONE)
    TL.gable_roof(bm, poly, eave, 1.15 if upper else 1.35, .32, .14,
                  roof, TRIM, ridge_along_x=True, gable_mat=plaster)
    TL.corner_posts(bm, poly, .08, eave-.08, .1, TRIM)
    if upper:
        for face in range(4):
            TL.beams_around(bm, shell, face, 2.75, .16, .07, TRIM)
    for opening in windows:
        TL.window_furniture(bm, opening, TRIM, GLASS, thickness,
                            mullion=True, shutters=False)
    TL.door_furniture(bm, doorway, DOOR, TRIM, PAVING, thickness)
    # Clear central canopy. No post or sign intrudes into the approach aisle.
    aw = 2.5 if centre else 1.95
    fy = -depth/2
    TL.solid_from_quad(bm, [(-aw/2, fy+.04, 2.65), (aw/2, fy+.04, 2.65),
                           (aw/2, fy-.80, 2.45), (-aw/2, fy-.80, 2.45)],
                       .10, roof, up=(0,0,1))
    TL.solid_box(bm, (0, fy-.79, 2.39), (aw, .10, .13), TRIM)
    TL.solid_box(bm, (0, fy-.37, .015),
                 (doorway.width+.36, .85, .07), PAVING)
    return doorway


def cottage(bm, rng):
    return building(bm, 4.5, 4.0, 2.65, ROOF_RED)


def townhouse(bm, rng):
    return building(bm, 4.2, 4.4, 4.5, ROOF_BLUE, BLUE, upper=True)


def farmhouse(bm, rng):
    return building(bm, 5.8, 3.6, 2.6, ROOF_GREEN)


def pokemon_centre(bm, rng):
    opening = building(bm, 7.2, 5.4, 3.25, ROOF_RED, centre=True)
    # Reuse the kit's recognisable red-and-white ball, above the entrance.
    from gen_town import poke_ball_sign
    poke_ball_sign(bm, (0, -3.54, 3.07), .48, .14, RED, TRIM, 13)
    return opening
