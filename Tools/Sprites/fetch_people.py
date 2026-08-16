"""Stage official Gen 4 (Platinum) overworld sprites into source_overworld/.

    python Tools/Sprites/fetch_people.py            # everything people.py names
    python Tools/Sprites/fetch_people.py --check    # availability only, no writes
    python Tools/Sprites/fetch_people.py --verify   # staged bytes vs upstream
    python Tools/Sprites/fetch_people.py --list     # every character upstream offers

This is the people-side counterpart to fetch_source.py and it follows the same
rule: it copies bytes and never touches a pixel.  Decoding, aligning and
packing all belong to extract_people.py.

Upstream is the `pret/pokeplatinum` decompilation.  Unlike the DP and HGSS
decompilations -- which keep their overworld art inside NARC/NSBTX containers
that cannot be read without building the ROM -- pokeplatinum checks its field
sprites in as plain indexed PNGs.  That is the only reason this cast exists in
a form a script can fetch, and it is worth recording: `pret/pokediamond` and
`pret/pokeheartgold` were both checked first and both keep `files/data/mmodel/`
as opaque `.NSBTX` blobs.

Each file arrives as a 32 px wide vertical strip of 32x32 cells: sixteen for an
NPC, thirty-two for a player character.  Nothing is repacked here.
"""

from __future__ import annotations

import hashlib
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import people as P

BASE = ("https://raw.githubusercontent.com/pret/pokeplatinum/main/"
        "res/graphics/field_sprites/")

# The two folders the human cast is drawn from. A character is looked up in
# each in turn, so the cast table only has to name the file.
SUBDIRS = ("npc", "player", "objects")

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read()


def fetch_sheet(source: str) -> tuple[bytes, str]:
    """The strip PNG for one character, and the upstream path it came from."""
    problems = []
    for sub in SUBDIRS:
        rel = f"{sub}/{source}.png"
        try:
            data = fetch(BASE + rel)
        except urllib.error.HTTPError as exc:
            problems.append(f"{rel}: HTTP {exc.code}")
            continue
        except OSError as exc:
            problems.append(f"{rel}: {exc}")
            continue
        # A 404 from raw.githubusercontent is an HTML page, so the magic bytes
        # are checked rather than the status alone.
        if not data.startswith(PNG_MAGIC):
            problems.append(f"{rel}: not a PNG ({len(data)} bytes)")
            continue
        return data, rel
    raise FileNotFoundError("; ".join(problems))


def stage(check_only: bool = False) -> int:
    os.makedirs(P.SOURCE_DIR, exist_ok=True)
    bad = 0
    wanted = ([(k, s) for k, s, _, _, _ in P.CAST]
              + [(k, s) for k, s, _, _, _ in P.PROPS])
    for key, source in wanted:
        path = P.sheet_path(source)
        try:
            data, rel = fetch_sheet(source)
        except FileNotFoundError as exc:
            print(f"{key:12} {source:20} FAIL {exc}")
            bad += 1
            continue

        if check_only:
            print(f"{key:12} {source:20} available ({len(data)} bytes) <- {rel}")
            continue

        # Never silently replace a file that is already staged and different.
        # The whole cast is verified artwork and must not move underfoot.
        if os.path.exists(path):
            if open(path, "rb").read() != data:
                print(f"{key:12} {source:20} already staged with DIFFERENT "
                      f"bytes, kept")
                bad += 1
                continue
            print(f"{key:12} {source:20} unchanged")
            continue

        with open(path, "wb") as fh:
            fh.write(data)
        print(f"{key:12} {source:20} staged {len(data)} bytes <- {rel}")
    return bad


def verify() -> int:
    """Prove the staged files are still byte-identical to upstream."""
    bad = 0
    for key, source, *_ in list(P.CAST) + list(P.PROPS):
        path = P.sheet_path(source)
        if not os.path.exists(path):
            print(f"{key:12} {source:20} NOT STAGED")
            bad += 1
            continue
        local = hashlib.sha256(open(path, "rb").read()).hexdigest()
        try:
            remote = hashlib.sha256(fetch_sheet(source)[0]).hexdigest()
        except (OSError, FileNotFoundError) as exc:
            print(f"{key:12} {source:20} FETCH FAIL {exc}")
            bad += 1
            continue
        same = local == remote
        bad += not same
        print(f"{key:12} {source:20} {'identical' if same else 'DIFFERENT'}  "
              f"{local[:16]}")
    return bad


def list_upstream() -> int:
    """Every character the upstream folder offers, for widening the cast."""
    import json
    url = ("https://api.github.com/repos/pret/pokeplatinum/git/trees/"
           "HEAD?recursive=1")
    req = urllib.request.Request(url, headers={"User-Agent": "pokelab",
                                               "Accept": "application/vnd.github+json"})
    tree = json.load(urllib.request.urlopen(req, timeout=60))["tree"]
    have = {c[1] for c in P.CAST} | {p[1] for p in P.PROPS}
    for sub in SUBDIRS:
        prefix = f"res/graphics/field_sprites/{sub}/"
        names = sorted(t["path"][len(prefix):-4] for t in tree
                       if t["path"].startswith(prefix) and t["path"].endswith(".png"))
        print(f"\n{sub}/  ({len(names)})")
        for n in names:
            print(f"  {'*' if n in have else ' '} {n}")
    print("\n* = already in the cast")
    return 0


def main(argv: list[str]) -> None:
    if argv and argv[0] == "--verify":
        sys.exit(1 if verify() else 0)
    if argv and argv[0] == "--list":
        sys.exit(list_upstream())
    check_only = bool(argv) and argv[0] == "--check"
    bad = stage(check_only)
    print(f"\n{len(P.CAST)} characters + {len(P.PROPS)} props, {bad} problems")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main(sys.argv[1:])
