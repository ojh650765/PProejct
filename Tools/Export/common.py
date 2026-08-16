"""Shared plumbing for the Poke Lab export pipeline.

Everything that both the exporter and the Python-vs-C# comparison harness need lives
here so the two can never drift apart on how a feature row is built.
"""
from __future__ import annotations

import importlib
import math
import os
import sys
import types
import unicodedata
from pathlib import Path

import pandas as pd

# --- Locations -------------------------------------------------------------------

# The source clone is read-only and lives outside the Unity project. Override with
# POKELAB_SOURCE_REPO when re-running the export from a different checkout.
_DEFAULT_REPO = Path(
    r"C:/Users/ojh65/AppData/Local/Temp/claude/C--PProejct"
    r"/8fbd9adb-7cdd-4c1d-a6d3-ce52c84c54e8/scratchpad/repo"
)
REPO = Path(os.environ.get("POKELAB_SOURCE_REPO", _DEFAULT_REPO)).resolve()

# Tools/Export/common.py -> repo root is two levels up.
PROJECT_ROOT = Path(__file__).resolve().parents[2]
OUTPUT_DIR = PROJECT_ROOT / "Assets" / "StreamingAssets" / "pokelab"
TOOLS_DIR = Path(__file__).resolve().parent

MASTER_CSV = REPO / "data" / "pokemon_master_corrected_with_abilities.csv"

# Ours, not the source repo's. See README.md, "The 24 species the upstream merge lost".
SPECIES_SUPPLEMENT_CSV = TOOLS_DIR / "species_supplement.csv"
STAT_CHANGES_SUPPLEMENT_CSV = TOOLS_DIR / "base_stat_changes_supplement.csv"
COMBATS_CSV = REPO / "data" / "kaggle_pokedex_pokemon_data" / "final_combats.csv"
POKEROGUE_DIR = REPO / "data" / "pokerogue_derived"
MODEL_PATH = REPO / "battle_refinement_outputs" / "pokemon_battle_app_model.joblib"

# --- Model contract --------------------------------------------------------------

STAT_COLUMNS = ("HP", "Attack", "Defense", "Sp. Atk", "Sp. Def", "Speed")

FEATURE_COLUMNS = (
    "HP_diff", "Attack_diff", "Defense_diff", "Sp_Atk_diff", "Sp_Def_diff",
    "Speed_diff", "LogHeight_diff", "LogWeight_diff", "PhysicalPressure_diff",
    "SpecialPressure_diff", "TypeAdvantage_diff",
)

# ElementType integer values from Core/Types.cs. The serialized data contract depends
# on these, so they are duplicated here deliberately rather than inferred.
ELEMENT_TYPES = (
    "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison",
    "Ground", "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark",
    "Steel", "Fairy",
)
TYPE_INDEX = {name: index for index, name in enumerate(ELEMENT_TYPES)}


def _install_legacy_pandas_alias() -> None:
    """The bundle was pickled under pandas 1.x, which had a module we no longer ship."""
    module_name = "pandas.core.indexes.numeric"
    try:
        importlib.import_module(module_name)
        return
    except ModuleNotFoundError:
        pass
    module = types.ModuleType(module_name)
    module.Int64Index = pd.Index
    module.UInt64Index = pd.Index
    module.Float64Index = pd.Index
    sys.modules[module_name] = module


def load_bundle():
    """Loads the joblib bundle, tolerating the pandas/sklearn version skew."""
    import warnings

    import joblib

    _install_legacy_pandas_alias()
    if not MODEL_PATH.exists():
        raise FileNotFoundError(f"model bundle missing: {MODEL_PATH}")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        return joblib.load(MODEL_PATH)


def load_master() -> pd.DataFrame:
    """The dex, keyed by KaggleID exactly as backend.py keys it.

    The upstream CSV is 697 rows and skips 24 national dex numbers. That is a merge
    artefact, not a hole in the source: data/Pokemon.csv carries all 24, and the KaggleID
    each one would have had is still unclaimed. species_supplement.csv re-attaches them
    under exactly those ids, so the dex runs 1-721 with no gap and no id is invented.

    backend.py still sees only its own 697, which is what we want: reference_predictions
    .json must keep describing the model as it was trained, not as the game ships it.
    """
    master = pd.read_csv(MASTER_CSV)
    master["KaggleID"] = master["KaggleID"].astype(int)

    supplement = pd.read_csv(SPECIES_SUPPLEMENT_CSV)
    supplement["KaggleID"] = supplement["KaggleID"].astype(int)

    clash = sorted(set(master["KaggleID"]) & set(supplement["KaggleID"]))
    if clash:
        raise ValueError(f"species_supplement.csv reuses upstream KaggleIDs: {clash}")

    merged = pd.concat([master, supplement], ignore_index=True)
    merged = merged.sort_values("KaggleID", kind="stable").reset_index(drop=True)

    for column in ("KaggleID", "NationalDex"):
        duplicated = merged.loc[merged[column].duplicated(), column].tolist()
        if duplicated:
            raise ValueError(f"duplicate {column} in the merged dex: {sorted(duplicated)}")
    return merged


def load_stat_changes() -> pd.DataFrame:
    """The gen7+ base-stat corrections, upstream table plus our own.

    The upstream table was derived by diffing the *697* against current values, so it can
    say nothing about the 24 the merge had already dropped. The supplement carries the
    corrections that only apply to those, in the same schema, and is concatenated rather
    than merged so the exporter's "refuse to guess" guard still covers both.
    """
    upstream = pd.read_csv(POKEROGUE_DIR / "base_stat_changes_gen7plus.csv")
    extra = pd.read_csv(STAT_CHANGES_SUPPLEMENT_CSV)
    return pd.concat([upstream, extra], ignore_index=True)


# --- PokeRogue join --------------------------------------------------------------

def normalise_species_key(name: str) -> str:
    """The join rule documented in data/pokerogue_derived/README.md.

    Strip spaces, periods and hyphens; map the gender glyphs to F/M; fold combining
    accents; upper-case. Applied to both sides so "Mr. Mime" and "MR_MIME" meet in the
    middle, and so does "Flab\u00e9b\u00e9" with FLABEBE. Underscores are stripped too because the
    pokerogue keys use them as word separators.
    """
    if name is None:
        return ""
    text = unicodedata.normalize("NFKC", str(name)).strip()
    text = text.replace("\u2640", "F").replace("\u2642", "M")
    text = "".join(c for c in unicodedata.normalize("NFD", text) if not unicodedata.combining(c))
    for token in (" ", ".", "-", "_", "'", "\u2019", ":"):
        text = text.replace(token, "")
    return text.upper()


# --- Feature construction (mirrors backend.py exactly) ---------------------------

def effect_score(effect: float) -> float:
    """backend.py::_effect_score - a hard floor for immunity, log2 otherwise."""
    return -3.0 if effect == 0 else float(math.log2(effect))


def _clean(value) -> str | None:
    if value is None or pd.isna(value) or not str(value).strip():
        return None
    return str(value).strip()


def ability_set(row) -> set[str]:
    value = row.get("Abilities", "")
    return {x for x in str(value).split(";") if x and x != "nan"}


def best_effect(attacker, defender, type_multipliers, immunity_abilities,
                use_ability: bool = False) -> tuple[float, tuple[str, ...]]:
    """backend.py::_best_effect - max over the attacker's types, product over the
    defender's. Ability immunity zeroes a type outright when requested."""
    effects: list[float] = []
    blocked: list[str] = []
    for attack_type in (attacker["Type 1"], attacker.get("Type 2")):
        attack_type = _clean(attack_type)
        if attack_type is None:
            continue
        effect = 1.0
        for defend_type in (defender["Type 1"], defender.get("Type 2")):
            defend_type = _clean(defend_type)
            if defend_type is not None:
                effect *= type_multipliers.get(attack_type, {}).get(defend_type, 1.0)
        if use_ability and (ability_set(defender) & immunity_abilities.get(attack_type, set())):
            effect = 0.0
            blocked.append(attack_type)
        effects.append(float(effect))
    return (max(effects) if effects else 1.0), tuple(blocked)


def feature_values(first, second, type_multipliers, immunity_abilities) -> dict:
    """backend.py::_feature_frame, minus the DataFrame packaging."""
    values: dict[str, float] = {}
    for column in STAT_COLUMNS:
        key = column.replace(". ", "_") + "_diff"
        values[key] = float(first[column]) - float(second[column])
    values["LogHeight_diff"] = math.log1p(float(first["height"])) - math.log1p(float(second["height"]))
    values["LogWeight_diff"] = math.log1p(float(first["weight"])) - math.log1p(float(second["weight"]))
    values["PhysicalPressure_diff"] = (
        float(first["Attack"]) / (float(second["Defense"]) + 1)
        - float(second["Attack"]) / (float(first["Defense"]) + 1)
    )
    values["SpecialPressure_diff"] = (
        float(first["Sp. Atk"]) / (float(second["Sp. Def"]) + 1)
        - float(second["Sp. Atk"]) / (float(first["Sp. Def"]) + 1)
    )
    type_first, _ = best_effect(first, second, type_multipliers, immunity_abilities, False)
    type_second, _ = best_effect(second, first, type_multipliers, immunity_abilities, False)
    values["TypeAdvantage_diff"] = effect_score(type_first) - effect_score(type_second)
    return values
