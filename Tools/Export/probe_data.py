"""Recon over the source CSVs: ranges, join coverage, and null shapes."""
from __future__ import annotations

import pandas as pd

from common import (COMBATS_CSV, POKEROGUE_DIR, TYPE_INDEX, load_master,
                    load_stat_changes, normalise_species_key)


def main() -> None:
    master = load_master()
    print("master rows:", len(master), "id range:", master.KaggleID.min(), master.KaggleID.max())
    print("dup ids:", master.KaggleID.duplicated().sum())
    print("Type 1 nulls:", master["Type 1"].isna().sum(), "Type 2 nulls:", master["Type 2"].isna().sum())
    unknown = set(master["Type 1"].dropna().unique()) | set(master["Type 2"].dropna().unique())
    print("types not in enum:", sorted(unknown - set(TYPE_INDEX)))
    print("Legendary values:", master.Legendary.unique())
    print("Generation range:", master.Generation.min(), master.Generation.max())
    for col in ("height", "weight", "base_experience", "NationalDex"):
        print(f"  {col}: nulls={master[col].isna().sum()} min={master[col].min()} max={master[col].max()}")
    print("Abilities nulls:", master["Abilities"].isna().sum())
    print("Name_ko nulls:", master["Name_ko"].isna().sum())

    flags = pd.read_csv(POKEROGUE_DIR / "species_flags_and_costs.csv")
    keys = {normalise_species_key(k) for k in flags.species_key}
    master_keys = master["Name"].map(normalise_species_key)
    hit = master_keys.isin(keys)
    print(f"\npokerogue flags join: {hit.sum()}/{len(master)} matched")
    print("  unmatched sample:", master.loc[~hit, "Name"].head(15).tolist())

    changes = load_stat_changes()
    print("\nstat changes rows:", len(changes), "stats:", sorted(changes.stat.unique()))
    chg_keys = changes.name.map(normalise_species_key)
    known = set(master_keys)
    print("  change names unmatched:", sorted(set(chg_keys) - known))

    mods = pd.read_csv(POKEROGUE_DIR / "ability_type_modifiers.csv")
    print("\nability modifiers rows:", len(mods), "multipliers:", sorted(mods.multiplier.unique()))
    print("  types:", sorted(set(mods.attack_type) - set(TYPE_INDEX)), "(unknown)")

    combats = pd.read_csv(COMBATS_CSV).drop_duplicates()
    print("\ncombats rows (deduped):", len(combats))
    ids = pd.concat([combats.First_pokemon, combats.Second_pokemon])
    print("  id range:", ids.min(), ids.max())
    lo = combats[["First_pokemon", "Second_pokemon"]].min(axis=1)
    hi = combats[["First_pokemon", "Second_pokemon"]].max(axis=1)
    print("  unique unordered pairs:", len(set(zip(lo, hi))))
    print("  winner outside pair:", int((~combats.Winner.isin([*combats.First_pokemon, *combats.Second_pokemon])).sum()))
    bad = ((combats.Winner != combats.First_pokemon) & (combats.Winner != combats.Second_pokemon)).sum()
    print("  winner not one of the two:", int(bad))

    slice_ids = [1, 5, 10, 21, 25, 31, 47, 49, 66, 73, 81, 100]
    print("\nslice roster:")
    for sid in slice_ids:
        row = master.loc[master.KaggleID == sid].iloc[0]
        print(f"  {sid:3d} {row['Name']:<12} {row['Type 1']}/{row['Type 2']} "
              f"dex={row['NationalDex']} ko={row['Name_ko']} abil={row['Abilities']}")


if __name__ == "__main__":
    main()
