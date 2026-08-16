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

**The clone lives in a temp directory and is not durable.** It has already lost files once
— its venv's `python-dateutil` was gutted, which breaks `import pandas` and therefore every
script here; `pip install --force-reinstall python-dateutil six` inside that venv repairs
it. If the clone disappears entirely the export cannot run at all, and the artefacts under
`Assets/StreamingAssets/pokelab/` become unreproducible. Move it somewhere permanent, or
re-clone `pokemon_battle_prediction` and point `POKELAB_SOURCE_REPO` at it. Everything in
*this* folder is durable and version-controlled, including the 24-species supplement.

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
| `species.json` | 381 KB | 721 species as `Core/SpeciesData.cs`, plus model stat overrides and default-form notes |
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
| `species_supplement.csv` | The 24 species the upstream merge lost — see below. **Ours, not the source repo's** |
| `base_stat_changes_supplement.csv` | Corrections the upstream table could not cover, same schema, concatenated onto it |
| `moves_data.py` | The authored move pool and learnsets — **the one file here meant to be edited by hand** |
| `gen_reference.py` | Runs upstream `backend.py` to produce ground truth |
| `probe_model.py` | Prints the joblib bundle's shape; re-run first if the model is ever retrained |
| `probe_data.py` | CSV recon: ranges, join coverage, null shapes |
| `Verify/` | The C# comparison harness |

## The 24 species the upstream merge lost

`data/pokemon_master_corrected_with_abilities.csv` is 697 rows and skips 24 national dex
numbers. That is a **merge artefact, not a hole in the source**: `data/Pokemon.csv`, the
Kaggle sheet everything else is keyed against, carries all 24, and the KaggleID each one
would have had is simply unclaimed. Six lost their join on a special character in the name
(Nidoran♀, Nidoran♂, Farfetch'd, Mr. Mime, Mime Jr., Flabébé); seventeen exist only as
several forms and the merge could not pick one; Primeape has no visible reason.

`species_supplement.csv` re-attaches them. `common.py::load_master` concatenates it onto
the upstream CSV, sorts by KaggleID and raises on any duplicate KaggleID or NationalDex,
so **the export reproduces the full 721-row dex from scratch** — nothing here is a
hand-patch of `species.json`, and re-running the export cannot silently drop it.

**Ids are the Kaggle row number, same as every other species.** `SpeciesData.Id` is the
KaggleID: the 1-based row index of `data/Pokemon.csv`. It is *not* the national dex — only
two species have the two equal, and confusing them is the mistake in this project that
produces a plausible wrong answer rather than an error. Each supplemented species takes
the KaggleID of its own row in that sheet, so no id is invented and none can collide:
those 24 ids were unclaimed precisely because these 24 rows were dropped.

**Where the values came from.** Stats, typing, generation and the legendary flag are read
straight from `data/Pokemon.csv`, byte-for-byte the same source the other 697 use (checked:
those 697 agree with it on all eight columns). Height, weight, base experience and the
ability lists come from `data/pokeapi_battle_metadata/`, joined on the PokeAPI row that
`is_default = 1` marks; Korean names from `data/pokemon_names_by_country.csv`, keyed by
national dex. The same reconstruction reproduces all 697 upstream rows exactly, which is
how the join rule was confirmed rather than assumed.

**For a species with several forms, the stat line is the default form's.** PokeAPI's
`is_default` flag decides which — the form the games treat as the species' resting state.
That yields Deoxys-Normal, Wormadam-Plant, Giratina-Altered, Shaymin-Land,
Basculin-Red-Striped, Darmanitan-Standard, the three Incarnate genies, Keldeo-Ordinary,
Meloetta-Aria, Meowstic-Male, **Aegislash-Shield** (not Blade), Pumpkaboo/Gourgeist
Average Size, Zygarde-50% and Hoopa-Confined. `species.json` carries a top-level
`DefaultForms` block naming the form each row came from and listing the sibling forms that
existed within gen 1-6, so a stat line is never mistaken for the only one that exists.
`SpeciesRegistry` ignores the block; it is documentation that travels with the data.

## Decisions worth knowing

**Gen7+ stat corrections are applied to `species.json`, but the forest is fed the
originals.** The correction table updates 37 stats across 27 species so the game simulates
current values. The model was trained on the uncorrected gen6 numbers, so feeding it
corrected stats would silently diverge from `backend.py`. `species.json` therefore carries
a `ModelStatOverrides` array with the original stats for exactly those species, and
`SpeciesRegistry.ModelStats` is what the feature builder reads. The exporter raises if a
correction's stated gen6 value ever disagrees with the dex rather than guessing.

**The same rule applies to species the forest never saw.** All 24 supplemented species are
outside the training set — their KaggleIDs appear zero times in `final_combats.csv`. That
does not exempt them from the override table, because being outside the training set does
not put a species outside the *units* the forest learned in: every threshold on
`Attack_diff` was fitted against gen6 attack values, so a species scored with a gen7 number
sits at a different point on that axis than it would have during training. Uniformity of
the feature space is what the overrides are for. Two of the 24 need one:

- **Farfetch'd**, Attack 65 → 90 in gen7. This is the correction the upstream table always
  carried and could never apply, because its target was one of the 24 missing rows. The
  exporter used to report `34/35 applied`; it now reports `37/37`.
- **Aegislash**, Defense and Sp. Def 150 → 140 in gen8. The upstream table was derived by
  diffing the *697* against current values, so it could not have known about a species that
  was not in the 697. `base_stat_changes_supplement.csv` supplies it, in the same schema,
  concatenated by `common.py::load_stat_changes`.

**Known gap: Cryogonal.** Diffing the exported dex against a current PokeAPI snapshot
leaves exactly one species disagreeing after all corrections: Cryogonal ships Defense 30,
current is 50. The gen7 buff moved both HP (70→80) and Defense (30→50) and the upstream
table lists only the HP half. This is a pre-existing defect in one of the original 697, not
something the supplement introduced, so it is left alone rather than fixed in passing —
adding `Cryogonal,Defense,30,50,7` to `base_stat_changes_supplement.csv` is the one-line
fix if you want it.

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
