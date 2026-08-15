# Poké Lab export pipeline

Turns the upstream `pokemon_battle_prediction` repo — a Python project with a pickled
scikit-learn model — into five files Unity can read at runtime with no Python involved.

Everything under `Assets/StreamingAssets/pokelab/` is **generated**. Do not hand-edit it;
change the source or the scripts here and re-run.

## Prerequisites

The source repo, cloned read-only, with its virtualenv already built:

```
<source>/
  .venv/Scripts/python.exe          numpy 2.x, pandas 3.x, scikit-learn 1.9
  data/pokemon_master_corrected_with_abilities.csv
  data/pokerogue_derived/*.csv
  data/kaggle_pokedex_pokemon_data/final_combats.csv
  pokemon_battle_app/{domain,backend}.py
  battle_refinement_outputs/pokemon_battle_app_model.joblib
```

**The venv is not optional.** The bundle was pickled under numpy 1.x, and a system Python
on numpy 1.x cannot unpickle it. The venv also needs the legacy-pandas shim that
`common.py` installs, because the pickle references `pandas.core.indexes.numeric`, a
module pandas 2 removed.

`common.py` defaults `REPO` to the scratchpad clone. Point it somewhere else with:

```bash
export POKELAB_SOURCE_REPO=/path/to/pokemon_battle_prediction
```

## Re-running the export

```bash
cd Tools/Export
<source>/.venv/Scripts/python.exe export_pokelab.py
```

Writes into `Assets/StreamingAssets/pokelab/`:

| File | Size | Contents |
| --- | --- | --- |
| `species.json` | 363 KB | 697 species as `Core/SpeciesData.cs`, plus model stat overrides |
| `typechart.json` | 4.8 KB | 18×18 multipliers, immunity abilities, pokerogue defensive modifiers |
| `moves.json` | 23 KB | 32 authored moves and 12 learnsets |
| `forest.bin` | 4.55 MB | 120 trees / 264,814 nodes, little-endian |
| `combats.bin` | 275 KB | 35,140 head-to-head pairs |

The export is deterministic: the same inputs always produce byte-identical outputs.

## Verifying the C# port against Python

Two steps. First regenerate the ground truth by running the *upstream* `backend.py`:

```bash
<source>/.venv/Scripts/python.exe gen_reference.py     # -> reference_predictions.json
```

Then compile the C# numeric core outside Unity and compare:

```bash
cd Verify && dotnet run -c Release
```

The harness compiles the UnityEngine-free sources directly from `Assets/` — the same
files the player builds — loads the exported artefacts, and checks every reference pair,
plus order invariance, the tactical scenarios, and the performance budget. It exits
non-zero on any drift, so it can gate a re-export.

Current result: **max |C# − Python| = 2.9e-8** across 27 pairs, against a 1e-4 tolerance.
The residual is float32 storage of the probability, not a difference in the arithmetic.

The same reference values are pasted into `Assets/Tests/PokeLab/PokeLabOracleTests.cs`, so
the match is also checked from the Unity Test Runner. **If you regenerate
`reference_predictions.json`, update that array too** — print it with:

```bash
<source>/.venv/Scripts/python.exe -c "
import json
for r in json.load(open('reference_predictions.json',encoding='utf-8'))['pairs']:
    print(f\"            ({r['first']}, {r['second']}, {r['firstProbability']!r}),\")"
```

## Files

| Script | Role |
| --- | --- |
| `common.py` | Paths, the joblib loader, the pokerogue join rule, and the feature builder mirrored from `backend.py` |
| `export_pokelab.py` | The export itself |
| `moves_data.py` | The authored move pool and learnsets — **the one file here meant to be edited by hand** |
| `gen_reference.py` | Runs upstream `backend.py` to produce ground truth |
| `probe_model.py` | Prints the joblib bundle's shape; re-run first if the model is ever retrained |
| `probe_data.py` | CSV recon: ranges, join coverage, null shapes |
| `Verify/` | The C# comparison harness |

## Decisions worth knowing

**Gen7+ stat corrections are applied to `species.json`, but the forest is fed the
originals.** The correction table updates 34 stats across 25 species so the game simulates
current values. The model was trained on the uncorrected gen6 numbers, so feeding it
corrected stats would silently diverge from `backend.py`. `species.json` therefore carries
a `ModelStatOverrides` array with the original stats for exactly those species, and
`SpeciesRegistry.ModelStats` is what the feature builder reads.

One of the 35 corrections (`Farfetch'd`) has no target: that species is not among the 697
in the Kaggle dex. The exporter reports `34/35 applied`, which is correct, and raises if a
correction's stated gen6 value ever disagrees with the dex rather than guessing.

**Forest thresholds are rounded *down* to float32.** sklearn compares a float32 feature
against a float64 threshold. Naive rounding can move a split across a feature value and
flip a decision. Taking the largest float32 ≤ the float64 threshold makes the float32
comparison provably equivalent for float32 inputs, which is why the port matches to 1e-8
rather than merely "closely".

**`TypeAdvantage_diff` uses the ability-free type effect.** `backend.py` feeds the model
the raw type matchup and uses the ability-aware one only for the warning text. Getting
this backwards is the easiest way to break the port while still looking plausible.

**Combat tallies are keyed by the unordered pair.** `final_combats.csv` records both
orderings; the export collapses them and stores `(min id << 16) | max id` so the runtime
lookup is a binary search over a sorted `uint32[]`.
