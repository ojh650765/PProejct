"""Exports every runtime artefact the Poke Lab intelligence layer needs.

Writes into Assets/StreamingAssets/pokelab/:
    species.json    the dex, shaped exactly like Core/SpeciesData.cs
    typechart.json  18x18 multipliers + immunity abilities + defensive ability modifiers
    forest.bin      the 120-tree random forest, flattened little-endian
    combats.bin     head-to-head tallies keyed by the ordered species pair
    moves.json      the authored move pool and learnsets (see moves_data.py)

Run:  .venv/Scripts/python.exe Tools/Export/export_pokelab.py
See README.md in this folder.
"""
from __future__ import annotations

import json
import struct
from pathlib import Path

import numpy as np
import pandas as pd

import moves_data
from common import (COMBATS_CSV, ELEMENT_TYPES, OUTPUT_DIR, POKEROGUE_DIR,
                    STAT_COLUMNS, TYPE_INDEX, load_bundle, load_master,
                    normalise_species_key)

FOREST_MAGIC = b"PLFR"
COMBAT_MAGIC = b"PLCB"
FORMAT_VERSION = 1


# --- helpers ---------------------------------------------------------------------

def _type_id(value) -> int:
    """ElementType.None is -1; every real type maps through the frozen enum order."""
    if value is None or pd.isna(value) or not str(value).strip():
        return -1
    name = str(value).strip()
    if name not in TYPE_INDEX:
        raise KeyError(f"type '{name}' is not one of the 18 ElementType values")
    return TYPE_INDEX[name]


def _split(value) -> list[str]:
    if value is None or pd.isna(value):
        return []
    return [x.strip() for x in str(value).split(";") if x.strip()]


def _write_json(path: Path, payload: dict) -> None:
    # UTF-8 without BOM and without ASCII escaping: the Korean names must survive, and
    # UnityEngine.JsonUtility chokes on a BOM.
    text = json.dumps(payload, ensure_ascii=False, indent=1)
    path.write_text(text, encoding="utf-8")
    print(f"  {path.name}: {path.stat().st_size / 1024:.1f} KB")


# --- species ---------------------------------------------------------------------

def build_species(master: pd.DataFrame) -> tuple[list[dict], list[dict]]:
    """Returns (species records, model stat overrides).

    The gen7+ corrections are applied to SpeciesData.BaseStats because that is what the
    game should simulate with. The forest, however, was trained on the uncorrected gen6
    numbers, so feeding it corrected stats would silently diverge from backend.py. The
    override table carries the original values for exactly the rows that changed, and the
    oracle uses it when it builds a feature row.
    """
    flags = pd.read_csv(POKEROGUE_DIR / "species_flags_and_costs.csv")
    cost_by_key: dict[str, int] = {}
    for _, row in flags.iterrows():
        key = normalise_species_key(row["species_key"])
        cost = row["starter_cost"]
        if pd.notna(cost):
            cost_by_key[key] = int(cost)

    changes = pd.read_csv(POKEROGUE_DIR / "base_stat_changes_gen7plus.csv")
    change_by_key: dict[str, list[tuple[str, int, int]]] = {}
    for _, row in changes.iterrows():
        change_by_key.setdefault(normalise_species_key(row["name"]), []).append(
            (str(row["stat"]), int(row["gen6_value"]), int(row["current_value"]))
        )

    species: list[dict] = []
    overrides: list[dict] = []
    applied = 0
    for _, row in master.iterrows():
        key = normalise_species_key(row["Name"])
        stats = [int(row[column]) for column in STAT_COLUMNS]
        gen6 = list(stats)

        for stat_name, gen6_value, current_value in change_by_key.get(key, ()):
            index = STAT_COLUMNS.index(stat_name)
            if stats[index] != gen6_value:
                # The correction table disagrees with our dex; refuse to guess.
                raise ValueError(
                    f"{row['Name']} {stat_name}: expected gen6 {gen6_value}, dex has {stats[index]}"
                )
            stats[index] = current_value
            applied += 1

        species_id = int(row["KaggleID"])
        if stats != gen6:
            overrides.append({"Id": species_id, "ModelStats": gen6})

        species.append({
            "Id": species_id,
            "NationalDex": int(row["NationalDex"]),
            "NameEn": str(row["Name"]),
            "NameKo": str(row["Name_ko"]),
            "Type1": _type_id(row["Type 1"]),
            "Type2": _type_id(row.get("Type 2")),
            "BaseStats": stats,
            "Height": float(row["height"]),
            "Weight": float(row["weight"]),
            "BaseExperience": int(row["base_experience"]),
            "Generation": int(row["Generation"]),
            "Legendary": bool(row["Legendary"]),
            "Abilities": _split(row.get("Abilities")),
            "AbilitiesKo": _split(row.get("Abilities_ko")),
            "StarterCost": cost_by_key.get(key, 0),
            # Naming convention from Docs/CONTRACTS.md.
            "ArtKey": f"Creature_{species_id}_{row['Name']}",
        })

    expected = sum(len(v) for v in change_by_key.values())
    print(f"  gen7+ corrections: {applied}/{expected} applied "
          f"({len(overrides)} species carry a model-stat override)")
    return species, overrides


# --- type chart ------------------------------------------------------------------

def build_typechart(bundle: dict) -> dict:
    multipliers = bundle["type_multipliers"]
    immunity = bundle["immunity_abilities"]

    flat: list[float] = []
    for attack in ELEMENT_TYPES:
        row = multipliers.get(attack, {})
        for defend in ELEMENT_TYPES:
            flat.append(float(row.get(defend, 1.0)))

    immunity_rows = [
        {"Ability": ability, "AttackType": TYPE_INDEX[attack_type]}
        for attack_type, abilities in sorted(immunity.items())
        for ability in sorted(abilities)
    ]

    mods = pd.read_csv(POKEROGUE_DIR / "ability_type_modifiers.csv")
    modifier_rows = [
        {
            "Ability": str(row["ability_identifier"]).strip(),
            "AttackType": TYPE_INDEX[str(row["attack_type"]).strip()],
            "Multiplier": float(row["multiplier"]),
        }
        for _, row in mods.iterrows()
    ]

    return {
        "TypeNames": list(ELEMENT_TYPES),
        "Multipliers": flat,
        "ImmunityAbilities": immunity_rows,
        "AbilityModifiers": modifier_rows,
    }


# --- forest ----------------------------------------------------------------------

def _threshold_to_float32(threshold: np.ndarray) -> np.ndarray:
    """Round each float64 threshold DOWN to the nearest float32.

    sklearn compares a float32 feature against a float64 threshold. Naive rounding can
    move the split across a feature value and flip a decision. Taking the largest float32
    that is <= the float64 threshold makes the float32 comparison provably equivalent for
    float32 inputs: every float32 x with x <= t64 is also <= t32, and the next float32
    above t32 is strictly greater than t64.
    """
    down = threshold.astype(np.float32)
    # Where the cast rounded up, step one ULP toward -inf.
    overshoot = down.astype(np.float64) > threshold
    down[overshoot] = np.nextafter(down[overshoot], np.float32(-np.inf), dtype=np.float32)
    assert not np.any(down.astype(np.float64) > threshold)
    return down


def write_forest(bundle: dict, path: Path) -> None:
    forest = bundle["raw_model"]
    trees = [estimator.tree_ for estimator in forest.estimators_]
    if list(forest.classes_) != [0, 1]:
        raise ValueError(f"expected binary classes [0, 1], got {forest.classes_}")

    counts = [tree.node_count for tree in trees]
    offsets = np.zeros(len(trees) + 1, dtype=np.int64)
    offsets[1:] = np.cumsum(counts)
    total = int(offsets[-1])

    feature = np.empty(total, dtype=np.int16)
    threshold = np.empty(total, dtype=np.float64)
    left = np.empty(total, dtype=np.int32)
    right = np.empty(total, dtype=np.int32)
    positive = np.empty(total, dtype=np.float32)

    for index, tree in enumerate(trees):
        base = int(offsets[index])
        end = base + tree.node_count
        # sklearn marks leaves with feature == TREE_UNDEFINED (-2); keep it negative.
        feature[base:end] = tree.feature.astype(np.int16)
        threshold[base:end] = tree.threshold

        # Child indices become global so traversal needs no per-tree base.
        child_left = tree.children_left.astype(np.int64)
        child_right = tree.children_right.astype(np.int64)
        left[base:end] = np.where(child_left < 0, -1, child_left + base).astype(np.int32)
        right[base:end] = np.where(child_right < 0, -1, child_right + base).astype(np.int32)

        # tree_.value is already normalised per node in modern sklearn, and
        # DecisionTreeClassifier.predict_proba returns it verbatim. Column 1 is class 1.
        values = tree.value[:, 0, :]
        totals = values.sum(axis=1, keepdims=True)
        if not np.allclose(totals, 1.0, atol=1e-6):
            # Older sklearn stored raw weighted counts; normalise so either shape works.
            values = values / np.where(totals == 0, 1.0, totals)
        positive[base:end] = values[:, 1].astype(np.float32)

    threshold32 = _threshold_to_float32(threshold)

    with path.open("wb") as handle:
        handle.write(FOREST_MAGIC)
        handle.write(struct.pack("<IIII", FORMAT_VERSION, len(trees),
                                 int(forest.n_features_in_), total))
        handle.write(offsets.astype("<u4").tobytes())
        handle.write(feature.astype("<i2").tobytes())
        handle.write(threshold32.astype("<f4").tobytes())
        handle.write(left.astype("<i4").tobytes())
        handle.write(right.astype("<i4").tobytes())
        handle.write(positive.astype("<f4").tobytes())

    print(f"  {path.name}: {path.stat().st_size / 1024 / 1024:.2f} MB "
          f"({len(trees)} trees, {total} nodes)")


# --- combats ---------------------------------------------------------------------

def write_combats(path: Path) -> None:
    combats = pd.read_csv(COMBATS_CSV).drop_duplicates()
    first = combats["First_pokemon"].to_numpy()
    second = combats["Second_pokemon"].to_numpy()
    winner = combats["Winner"].to_numpy()

    lo = np.minimum(first, second)
    hi = np.maximum(first, second)
    if hi.max() > 0xFFFF:
        raise ValueError("species ids exceed uint16")

    tally: dict[int, list[int]] = {}
    for lo_id, hi_id, win_id in zip(lo, hi, winner):
        key = (int(lo_id) << 16) | int(hi_id)
        record = tally.setdefault(key, [0, 0])
        if win_id == lo_id:
            record[0] += 1
        elif win_id == hi_id:
            record[1] += 1

    keys = np.array(sorted(tally), dtype=np.uint32)
    lo_wins = np.array([tally[int(k)][0] for k in keys], dtype=np.uint16)
    hi_wins = np.array([tally[int(k)][1] for k in keys], dtype=np.uint16)
    if max(lo_wins.max(), hi_wins.max()) > 0xFFFF:
        raise ValueError("win tallies exceed uint16")

    with path.open("wb") as handle:
        handle.write(COMBAT_MAGIC)
        handle.write(struct.pack("<II", FORMAT_VERSION, len(keys)))
        handle.write(keys.astype("<u4").tobytes())
        handle.write(lo_wins.astype("<u2").tobytes())
        handle.write(hi_wins.astype("<u2").tobytes())

    print(f"  {path.name}: {path.stat().st_size / 1024:.1f} KB ({len(keys)} pairs)")


# --- entry point -----------------------------------------------------------------

def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    print(f"exporting to {OUTPUT_DIR}")

    master = load_master()
    bundle = load_bundle()

    species, overrides = build_species(master)
    _write_json(OUTPUT_DIR / "species.json",
                {"Species": species, "ModelStatOverrides": overrides})
    _write_json(OUTPUT_DIR / "typechart.json", build_typechart(bundle))
    _write_json(OUTPUT_DIR / "moves.json", moves_data.build(TYPE_INDEX))
    write_forest(bundle, OUTPUT_DIR / "forest.bin")
    write_combats(OUTPUT_DIR / "combats.bin")
    print("done")


if __name__ == "__main__":
    main()
