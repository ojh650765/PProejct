#!/bin/sh
# Downloads the Nintendo Switch Pokemon reference maps studied for the slice layout.
# Reference only, never shipped; the images are not committed.
DIR="$(dirname "$0")"
BASE="https://archives.bulbagarden.net/media/upload"
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
while read -r p; do
  [ -z "$p" ] && continue
  f="$DIR/$(basename "$p")"
  curl -s -L -A "$UA" -o "$f" "$BASE/$p"
  printf '%s %s\n' "$(basename "$p")" "$(stat -c%s "$f" 2>/dev/null)"
done <<'EOF'
3/3a/Sinnoh_Route_201_BDSP.png
a/a1/Sinnoh_Route_203_BDSP.png
6/6b/Sinnoh_Route_205_BDSP.png
9/9e/Sandgem_Town_BDSP.png
5/5c/Jubilife_City_BDSP.png
0/0d/Eterna_City_BDSP.png
EOF
