"""The authored move pool and learnsets for the vertical slice.

Hand-written rather than scraped: the slice needs 32 moves that are canonical in power,
accuracy and PP, and that between them give all twelve slice species real coverage and a
reason to switch. Values follow the mainline games (gen 6 numbers where they moved).

Emitted as moves.json by export_pokelab.py. Field names match Core/SpeciesData.cs
(MoveData) exactly, because UnityEngine.JsonUtility binds by field name.
"""
from __future__ import annotations

# Mirrors of the frozen Core enums. Duplicated deliberately: these integers are the
# serialized contract, so they should break loudly here if Core ever renumbers.
PHYSICAL, SPECIAL, STATUS = 0, 1, 2

NO_STATUS, BURN, FREEZE, PARALYSIS, POISON, BAD_POISON, SLEEP = 0, 1, 2, 3, 4, 5, 6

VOL_NONE, VOL_CONFUSED, VOL_FLINCHED = 0, 1, 2

HP, ATTACK, DEFENSE, SP_ATTACK, SP_DEFENSE, SPEED, ACCURACY, EVASION = range(8)


def _move(move_id, name_en, name_ko, type_name, category, power=0, accuracy=100,
          pp=15, priority=0, crit=0, status=NO_STATUS, volatile=VOL_NONE,
          effect_chance=0, stat_changes=(), targets_self=False, drain=0.0,
          recoil=0.0, heal=0.0, min_hits=0, max_hits=0, vfx=None, anim=None,
          projectile=False, contact=True) -> dict:
    return {
        "Id": move_id,
        "NameEn": name_en,
        "NameKo": name_ko,
        "_Type": type_name,  # resolved to the ElementType int by build()
        "Category": category,
        "Power": power,
        "Accuracy": accuracy,
        "PowerPoints": pp,
        "Priority": priority,
        "CritStageBonus": crit,
        "InflictsStatus": status,
        "InflictsVolatile": volatile,
        "EffectChance": effect_chance,
        "StatChanges": [{"Stat": stat, "Stages": stages} for stat, stages in stat_changes],
        "TargetsSelf": targets_self,
        "DrainRatio": drain,
        "RecoilRatio": recoil,
        "HealRatio": heal,
        "MinHits": min_hits,
        "MaxHits": max_hits,
        "VfxKey": vfx or f"vfx_{move_id.replace('-', '_')}",
        "AnimationKey": anim or ("Attack_Special" if category == SPECIAL
                                 else "Attack_Status" if category == STATUS
                                 else "Attack_Physical"),
        "IsProjectile": projectile,
        "MakesContact": contact,
    }


# --- The pool: 32 moves ----------------------------------------------------------

MOVES = [
    # Normal (5)
    _move("tackle", "Tackle", "몸통박치기", "Normal", PHYSICAL, power=40, pp=35),
    _move("quick-attack", "Quick Attack", "전광석화", "Normal", PHYSICAL, power=40, pp=30,
          priority=1, anim="Attack_Dash"),
    _move("hyper-fang", "Hyper Fang", "이빨아파", "Normal", PHYSICAL, power=80, accuracy=90,
          pp=15, volatile=VOL_FLINCHED, effect_chance=10, anim="Attack_Bite"),
    _move("growl", "Growl", "울음소리", "Normal", STATUS, pp=40,
          stat_changes=((ATTACK, -1),), contact=False, anim="Cast_Debuff"),
    _move("tail-whip", "Tail Whip", "꼬리흔들기", "Normal", STATUS, pp=30,
          stat_changes=((DEFENSE, -1),), contact=False, anim="Cast_Debuff"),

    # Grass (5)
    _move("vine-whip", "Vine Whip", "덩굴채찍", "Grass", PHYSICAL, power=45, pp=25),
    _move("razor-leaf", "Razor Leaf", "잎날가르기", "Grass", PHYSICAL, power=55, accuracy=95,
          pp=25, crit=1, projectile=True, contact=False),
    _move("absorb", "Absorb", "흡수", "Grass", SPECIAL, power=20, pp=25, drain=0.5,
          projectile=True, contact=False),
    _move("sleep-powder", "Sleep Powder", "수면가루", "Grass", STATUS, accuracy=75, pp=15,
          status=SLEEP, effect_chance=100, projectile=True, contact=False, anim="Cast_Powder"),
    _move("stun-spore", "Stun Spore", "저리가루", "Grass", STATUS, accuracy=75, pp=30,
          status=PARALYSIS, effect_chance=100, projectile=True, contact=False, anim="Cast_Powder"),

    # Poison (3)
    _move("poison-powder", "Poison Powder", "독가루", "Poison", STATUS, accuracy=75, pp=35,
          status=POISON, effect_chance=100, projectile=True, contact=False, anim="Cast_Powder"),
    _move("poison-sting", "Poison Sting", "독침", "Poison", PHYSICAL, power=15, pp=35,
          status=POISON, effect_chance=30, projectile=True, contact=False),
    _move("sludge", "Sludge", "오물", "Poison", SPECIAL, power=65, pp=20,
          status=POISON, effect_chance=30, projectile=True, contact=False),

    # Fire (2)
    _move("ember", "Ember", "불꽃세례", "Fire", SPECIAL, power=40, pp=25,
          status=BURN, effect_chance=10, projectile=True, contact=False),
    _move("flamethrower", "Flamethrower", "화염방사", "Fire", SPECIAL, power=90, pp=15,
          status=BURN, effect_chance=10, projectile=True, contact=False, anim="Attack_Beam"),

    # Water (3)
    _move("water-gun", "Water Gun", "물대포", "Water", SPECIAL, power=40, pp=25,
          projectile=True, contact=False),
    _move("bubble-beam", "Bubble Beam", "거품광선", "Water", SPECIAL, power=65, pp=20,
          effect_chance=10, stat_changes=((SPEED, -1),), projectile=True, contact=False,
          anim="Attack_Beam"),
    _move("withdraw", "Withdraw", "껍질에숨기", "Water", STATUS, pp=40,
          stat_changes=((DEFENSE, 1),), targets_self=True, contact=False, anim="Cast_Buff"),

    # Electric (3)
    _move("thunder-shock", "Thunder Shock", "전기충격", "Electric", SPECIAL, power=40, pp=30,
          status=PARALYSIS, effect_chance=10, projectile=True, contact=False),
    _move("thunderbolt", "Thunderbolt", "10만볼트", "Electric", SPECIAL, power=90, pp=15,
          status=PARALYSIS, effect_chance=10, projectile=True, contact=False, anim="Attack_Beam"),
    _move("thunder-wave", "Thunder Wave", "전기자석파", "Electric", STATUS, accuracy=90, pp=20,
          status=PARALYSIS, effect_chance=100, projectile=True, contact=False, anim="Cast_Debuff"),

    # Flying (2)
    _move("gust", "Gust", "바람일으키기", "Flying", SPECIAL, power=40, pp=35,
          projectile=True, contact=False),
    _move("wing-attack", "Wing Attack", "날개치기", "Flying", PHYSICAL, power=60, pp=35,
          anim="Attack_Dash"),

    # Fighting (2)
    _move("karate-chop", "Karate Chop", "태권당수", "Fighting", PHYSICAL, power=50, pp=25, crit=1),
    # Canonically deals damage equal to the user's level. The engine has no hook for
    # fixed damage, so the slice ships it as a reliable mid-power hit.
    _move("seismic-toss", "Seismic Toss", "지구던지기", "Fighting", PHYSICAL, power=60, pp=20,
          anim="Attack_Grapple"),

    # Rock / Ground (3)
    _move("rock-throw", "Rock Throw", "돌떨어뜨리기", "Rock", PHYSICAL, power=50, accuracy=90,
          pp=15, projectile=True, contact=False),
    _move("rock-blast", "Rock Blast", "락블레스트", "Rock", PHYSICAL, power=25, accuracy=90,
          pp=10, min_hits=2, max_hits=5, projectile=True, contact=False),
    _move("mud-slap", "Mud-Slap", "진흙뿌리기", "Ground", SPECIAL, power=20, pp=10,
          effect_chance=100, stat_changes=((ACCURACY, -1),), projectile=True, contact=False),

    # Ghost (3)
    _move("lick", "Lick", "핥기", "Ghost", PHYSICAL, power=30, pp=30,
          status=PARALYSIS, effect_chance=30),
    _move("confuse-ray", "Confuse Ray", "이상한빛", "Ghost", STATUS, pp=10,
          volatile=VOL_CONFUSED, effect_chance=100, projectile=True, contact=False,
          anim="Cast_Debuff"),
    _move("hypnosis", "Hypnosis", "최면술", "Psychic", STATUS, accuracy=60, pp=20,
          status=SLEEP, effect_chance=100, contact=False, anim="Cast_Debuff"),

    # Dark (1)
    _move("bite", "Bite", "물기", "Dark", PHYSICAL, power=60, pp=25,
          volatile=VOL_FLINCHED, effect_chance=30, anim="Attack_Bite"),
]


# --- Learnsets -------------------------------------------------------------------
# (level, move id), ordered by level. IMoveRegistry.MovesFor returns everything learned
# at or below the requested level; the battle layer keeps the last four.

LEARNSETS: dict[int, tuple[tuple[int, str], ...]] = {
    1: (   # Bulbasaur - Grass/Poison
        (1, "tackle"), (1, "growl"), (3, "vine-whip"), (7, "absorb"),
        (13, "poison-powder"), (15, "sleep-powder"), (20, "razor-leaf"),
    ),
    5: (   # Charmander - Fire
        (1, "tackle"), (1, "growl"), (4, "ember"), (17, "quick-attack"),
        (28, "flamethrower"),
    ),
    10: (  # Squirtle - Water
        (1, "tackle"), (1, "tail-whip"), (4, "water-gun"), (8, "withdraw"),
        (13, "bubble-beam"), (20, "bite"),
    ),
    21: (  # Pidgey - Normal/Flying
        (1, "tackle"), (5, "growl"), (9, "gust"), (13, "quick-attack"),
        (25, "wing-attack"),
    ),
    25: (  # Rattata - Normal
        (1, "tackle"), (1, "tail-whip"), (4, "quick-attack"), (7, "bite"),
        (13, "hyper-fang"),
    ),
    31: (  # Pikachu - Electric
        (1, "thunder-shock"), (1, "growl"), (5, "tail-whip"), (10, "quick-attack"),
        (18, "thunder-wave"), (26, "thunderbolt"),
    ),
    47: (  # Zubat - Poison/Flying
        (1, "tackle"), (9, "gust"), (13, "bite"), (17, "wing-attack"),
        (21, "confuse-ray"), (25, "poison-sting"),
    ),
    49: (  # Oddish - Grass/Poison
        (1, "absorb"), (5, "sleep-powder"), (9, "stun-spore"), (13, "poison-powder"),
        (15, "razor-leaf"), (21, "vine-whip"),
    ),
    66: (  # Poliwag - Water
        (1, "tackle"), (1, "water-gun"), (5, "hypnosis"), (13, "bubble-beam"),
        (16, "withdraw"),
    ),
    73: (  # Machop - Fighting. Rock Throw stands in for the Rock Tomb TM so Machop has
           # an answer to the Flying line.
        (1, "tackle"), (1, "growl"), (7, "karate-chop"), (13, "seismic-toss"),
        (19, "rock-throw"),
    ),
    81: (  # Geodude - Rock/Ground
        (1, "tackle"), (4, "mud-slap"), (8, "rock-throw"), (16, "rock-blast"),
    ),
    100: ( # Gastly - Ghost/Poison
        (1, "lick"), (1, "hypnosis"), (19, "confuse-ray"), (25, "sludge"),
    ),
}


def build(type_index: dict[str, int]) -> dict:
    """Resolves type names to ElementType ints and packs the JSON payload."""
    known = {move["Id"] for move in MOVES}
    moves = []
    for move in MOVES:
        record = dict(move)
        record["Type"] = type_index[record.pop("_Type")]
        moves.append(record)

    learnsets = []
    for species_id, entries in sorted(LEARNSETS.items()):
        for level, move_id in entries:
            if move_id not in known:
                raise KeyError(f"learnset for species {species_id} references unknown move '{move_id}'")
        learnsets.append({
            "SpeciesId": species_id,
            "Entries": [{"Level": level, "MoveId": move_id} for level, move_id in entries],
        })

    print(f"  moves: {len(moves)} across {len(learnsets)} learnsets")
    return {"Moves": moves, "Learnsets": learnsets}
