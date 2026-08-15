"""Runs the upstream backend.py over a fixed pair list and records what it returns.

The output (reference_predictions.json) is the ground truth the C# port is checked
against, both by compare_csharp.py here and by the EditMode test in
Assets/Tests/PokeLab/. Regenerate it whenever the model or the dex changes.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from common import REPO

# backend.py is a package module, so the repo root has to be importable.
sys.path.insert(0, str(REPO))

from pokemon_battle_app.backend import PokemonBattleService  # noqa: E402

OUTPUT = Path(__file__).resolve().parent / "reference_predictions.json"

SLICE = [1, 5, 10, 21, 25, 31, 47, 49, 66, 73, 81, 100]

# Pairs chosen to exercise the parts of the algorithm that can silently go wrong:
# every slice species appears; Gastly (levitate) covers the ability-immunity warning and
# the Ground 0x -> -3.0 effect-score floor; Geodude/Pikachu covers a true type immunity;
# the extremes cover the log height/weight tails and stat corrections.
EXTRA = [
    (100, 81),   # Gastly vs Geodude - levitate blocks Ground, and Ghost/Normal immunity
    (81, 100),   # ... mirrored, to confirm order invariance end to end
    (31, 81),    # Pikachu vs Geodude - Electric into Ground is 0x
    (81, 31),
    (1, 1),      # same species - the 0.5 short circuit
    (73, 21),    # Fighting into Flying
    (100, 25),   # Ghost into Normal - a true zero both ways
    (47, 73),    # Poison/Flying vs Fighting
    (5, 10),     # the starter triangle
    (10, 1),
    (1, 5),
    (66, 31),    # Water vs Electric
    (49, 5),     # Grass/Poison vs Fire
    # KaggleID, not National Dex: 146 is Vaporeon, 445 is Bidoof, 400 is Sealeo and
    # 697 is Hydreigon. Chosen to reach outside the slice and across the log height and
    # weight tails.
    (146, 1),
    (445, 100),
    (400, 81),
    (697, 1),
    (1, 697),
]


def main() -> int:
    service = PokemonBattleService()
    print(f"model: {service.model_path.name}")
    print(f"selected_probability_model: {service.bundle.get('selected_probability_model')!r}")

    pairs: list[tuple[int, int]] = []
    # Round-robin over the slice so every one of the twelve is covered in both roles.
    for index, first in enumerate(SLICE):
        second = SLICE[(index + 1) % len(SLICE)]
        pairs.append((first, second))
    pairs.extend(EXTRA)

    seen: set[tuple[int, int]] = set()
    records = []
    for first, second in pairs:
        if (first, second) in seen:
            continue
        seen.add((first, second))
        prediction = service.predict(first, second)
        records.append({
            "first": first,
            "second": second,
            "firstName": prediction.first.name_en,
            "secondName": prediction.second.name_en,
            "firstProbability": prediction.first_probability,
            "typeEffectFirst": prediction.type_effect_first,
            "typeEffectSecond": prediction.type_effect_second,
            "abilityTypeEffectFirst": prediction.ability_type_effect_first,
            "abilityTypeEffectSecond": prediction.ability_type_effect_second,
            "directBattles": prediction.direct_battles,
            "directFirstWins": prediction.direct_first_wins,
            "directSecondWins": prediction.direct_second_wins,
            "evidence": [
                {
                    "feature": item.feature,
                    "value": item.value,
                    "probabilityPoints": item.probability_points,
                }
                for item in prediction.evidence
            ],
        })

    OUTPUT.write_text(json.dumps({"pairs": records}, indent=1), encoding="utf-8")
    print(f"wrote {len(records)} reference predictions -> {OUTPUT.name}")

    # A quick self-check of the property the whole readout depends on.
    worst = 0.0
    for first, second in list(seen):
        if (second, first) in seen and first != second:
            a = next(r for r in records if r["first"] == first and r["second"] == second)
            b = next(r for r in records if r["first"] == second and r["second"] == first)
            worst = max(worst, abs(a["firstProbability"] - (1.0 - b["firstProbability"])))
    print(f"order-invariance residual in Python itself: {worst:.3e}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
