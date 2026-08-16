"""Stage official Gen 5 (Black/White) sprite files into Tools/Sprites/source/.

    python Tools/Sprites/fetch_source.py <dex> [<dex> ...]
    python Tools/Sprites/fetch_source.py --cast          # everything species.py names
    python Tools/Sprites/fetch_source.py --check <dex>   # availability only, no writes

This is the acquisition step that was previously done by hand: extract.py's
manifest already names the upstream ("Official Gen 5 (Black/White) sprites via
the PokeAPI sprite repository, staged in Tools/Sprites/source/"), but nothing
in the tree fetched them, so widening the cast meant sourcing files manually.

It is deliberately not a converter.  It copies bytes from the same upstream
paths the existing twelve came from -- verified byte-identical, see
--verify-existing -- and writes them under the filename convention species.py
already reads.  Nothing here decodes, rescales or re-encodes an image; that is
extract.py's job and it is the only thing allowed to touch the pixels.

Files staged per species, all keyed by NATIONAL DEX (never the game id):

    anim_front_<dex>.gif   animated/<dex>.gif
    anim_back_<dex>.gif    animated/back/<dex>.gif
    front_<dex>.png        <dex>.png
    back_<dex>.png         back/<dex>.png
    shiny_front_<dex>.png  shiny/<dex>.png
"""

from __future__ import annotations

import hashlib
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import species as S

BASE = ("https://raw.githubusercontent.com/PokeAPI/sprites/master/"
        "sprites/pokemon/versions/generation-v/black-white/")

# local filename template -> upstream path template, both keyed by dex
FILES = {
    "anim_front_{dex}.gif": "animated/{dex}.gif",
    "anim_back_{dex}.gif": "animated/back/{dex}.gif",
    "front_{dex}.png": "{dex}.png",
    "back_{dex}.png": "back/{dex}.png",
    "shiny_front_{dex}.png": "shiny/{dex}.png",
}

# The two formats extract.py can decode. A 404 from raw.githubusercontent is an
# HTML error page, so the magic bytes are checked rather than the status alone.
MAGIC = {".gif": b"GIF8", ".png": b"\x89PNG\r\n\x1a\n"}


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read()


def stage_one(dex: int, check_only: bool = False) -> tuple[int, list[str]]:
    """Stage every file for one dex. Returns (count_written, problems)."""
    written, problems = 0, []
    for local_tpl, remote_tpl in FILES.items():
        local = local_tpl.format(dex=dex)
        url = BASE + remote_tpl.format(dex=dex)
        path = os.path.join(S.SOURCE_DIR, local)
        try:
            data = fetch(url)
        except urllib.error.HTTPError as exc:
            problems.append(f"{local}: HTTP {exc.code}")
            continue
        except OSError as exc:
            problems.append(f"{local}: {exc}")
            continue

        magic = MAGIC[os.path.splitext(local)[1]]
        if not data.startswith(magic):
            problems.append(f"{local}: not a {magic[:4]!r} file ({len(data)} bytes)")
            continue

        if check_only:
            written += 1
            continue

        # Never silently replace a file that is already staged and different --
        # the existing twelve are verified artwork and must not move underfoot.
        if os.path.exists(path):
            old = open(path, "rb").read()
            if old != data:
                problems.append(f"{local}: already staged with different bytes, kept")
                continue
        os.makedirs(S.SOURCE_DIR, exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
        written += 1
    return written, problems


def verify_existing() -> int:
    """Prove the upstream is the same artwork the staged cast came from."""
    bad = 0
    for _, dex, name, _ in S.CAST:
        for local_tpl, remote_tpl in FILES.items():
            local = local_tpl.format(dex=dex)
            path = os.path.join(S.SOURCE_DIR, local)
            if not os.path.exists(path):
                continue
            lh = hashlib.sha256(open(path, "rb").read()).hexdigest()
            try:
                rh = hashlib.sha256(fetch(BASE + remote_tpl.format(dex=dex))).hexdigest()
            except OSError as exc:
                print(f"  {name:12} {local:22} FETCH FAIL {exc}")
                bad += 1
                continue
            same = lh == rh
            bad += not same
            print(f"  {name:12} {local:22} {'identical' if same else 'DIFFERENT'}")
    return bad


def main(argv: list[str]) -> None:
    if argv and argv[0] == "--verify-existing":
        sys.exit(1 if verify_existing() else 0)
    check_only = bool(argv) and argv[0] == "--check"
    if check_only:
        argv = argv[1:]
    if argv and argv[0] == "--cast":
        dexes = [c[1] for c in S.CAST]
    else:
        dexes = [int(a) for a in argv]
    if not dexes:
        print(__doc__)
        sys.exit(2)

    total, failed = 0, []
    for dex in dexes:
        n, problems = stage_one(dex, check_only)
        total += n
        for p in problems:
            failed.append((dex, p))
        print(f"dex {dex:4d}: {n}/{len(FILES)} files" +
              ("" if not problems else "  <- " + "; ".join(problems)))
    print(f"\n{total} files {'available' if check_only else 'staged'} in {S.SOURCE_DIR}")
    if failed:
        print(f"{len(failed)} problems:")
        for dex, p in failed:
            print(f"  dex {dex}: {p}")
        sys.exit(1)


if __name__ == "__main__":
    main(sys.argv[1:])
