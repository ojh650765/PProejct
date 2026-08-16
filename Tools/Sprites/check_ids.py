"""Re-derive the cast's GAME_ID -> dex mapping and fail if it drifted.

    python Tools/Sprites/check_ids.py            # offline checks
    python Tools/Sprites/check_ids.py --online   # also re-fetch and hash-compare

A wrong id mapping is the one defect in this pipeline that produces a
plausible result: the sprite loads, sits on the ground line, animates, and is
simply the wrong animal.  Nothing downstream can catch it, because every
downstream check only asks whether the pixels are intact.  So it is checked
here, against the same registry the running game reads.

Four independent things have to agree for a row to pass:

  1. the registry record for the GAME id exists, and its NationalDex, NameEn
     and Height match the cast row;
  2. the registry's ArtKey, which is written Creature_<gameid>_<Name>, binds
     that game id to that name a second time and from a different field;
  3. all five source files named for the row's DEX are staged; and
  4. (--online) each staged file is byte-identical to the upstream file at the
     dex-keyed path it was fetched from, which is what makes "front_43.png is
     Oddish" a fact about the artwork rather than about our own naming.
"""

from __future__ import annotations

import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import species as S
from fetch_source import BASE, FILES, fetch

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REGISTRY = os.path.join(ROOT, "Assets", "StreamingAssets", "pokelab", "species.json")


def load_registry() -> dict[int, dict]:
    with open(REGISTRY, encoding="utf-8") as fh:
        return {e["Id"]: e for e in json.load(fh)["Species"]}


def main(argv: list[str]) -> None:
    online = "--online" in argv
    reg = load_registry()
    bad = 0

    seen_gid: dict[int, str] = {}
    seen_dex: dict[int, str] = {}
    all_dex = {e["NationalDex"]: e["NameEn"] for e in reg.values()}

    print(f"{'game':>4} {'dex':>4} {'name':12} {'height':>6}  registry / artkey / files")
    for gid, dex, name, height in S.CAST:
        problems = []

        e = reg.get(gid)
        if e is None:
            problems.append(f"game id {gid} is not in the registry")
        else:
            if e["NationalDex"] != dex:
                problems.append(f"registry says dex {e['NationalDex']}, cast says {dex}")
            if e["NameEn"] != name:
                problems.append(f"registry says {e['NameEn']}, cast says {name}")
            if abs(e["Height"] / 10.0 - height) > 1e-9:
                problems.append(f"registry height {e['Height'] / 10.0}, cast {height}")
            if e.get("ArtKey") != f"Creature_{gid}_{name}":
                problems.append(f"ArtKey {e.get('ArtKey')!r} does not bind {gid} to {name}")

        if gid in seen_gid:
            problems.append(f"duplicate game id (also {seen_gid[gid]})")
        if dex in seen_dex:
            problems.append(f"duplicate dex (also {seen_dex[dex]})")
        seen_gid[gid], seen_dex[dex] = name, name

        for tpl in FILES:
            p = os.path.join(S.SOURCE_DIR, tpl.format(dex=dex))
            if not os.path.exists(p):
                problems.append(f"missing source file {os.path.basename(p)}")

        bad += bool(problems)
        status = "ok" if not problems else "FAIL: " + "; ".join(problems)
        print(f"{gid:>4} {dex:>4} {name:12} {height:>6.2f}  {status}")

    # The collision, quantified rather than asserted.
    same = [n for g, d, n, _ in S.CAST if g == d]
    cross = [(g, d, n, all_dex[g]) for g, d, n, _ in S.CAST
             if g != d and g in all_dex]
    print(f"\ngame id == dex for only {len(same)} of {len(S.CAST)}: {', '.join(same)}")
    print(f"{len(cross)} rows have a game id that is another species' dex "
          f"-- reading the wrong column would silently yield:")
    for g, d, n, other in cross:
        print(f"  game {g:>3} is {n:12} but dex {g:>3} is {other}")

    if online:
        print("\nre-fetching every staged file and comparing bytes:")
        for _, dex, name, _ in S.CAST:
            for tpl, remote in FILES.items():
                p = os.path.join(S.SOURCE_DIR, tpl.format(dex=dex))
                if not os.path.exists(p):
                    continue
                lh = hashlib.sha256(open(p, "rb").read()).hexdigest()
                try:
                    rh = hashlib.sha256(fetch(BASE + remote.format(dex=dex))).hexdigest()
                except OSError as exc:
                    print(f"  {name:12} {tpl.format(dex=dex):22} FETCH FAIL {exc}")
                    bad += 1
                    continue
                if lh != rh:
                    print(f"  {name:12} {tpl.format(dex=dex):22} DIFFERS FROM UPSTREAM")
                    bad += 1
        print("  every staged file matches its dex-keyed upstream path"
              if not bad else f"  {bad} mismatches")

    print(f"\n{len(S.CAST)} species checked, {bad} problems")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main(sys.argv[1:])
