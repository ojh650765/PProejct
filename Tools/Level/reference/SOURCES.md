# Reference — Nintendo Switch Pokémon maps studied for the slice redesign

The first layout was rejected ("레벨디자인 이상하자나… 닌텐도스위치 포켓몬 맵 검색해서 참고해서
만들어"). This is what was actually looked at, and what was taken from each.

Images are **not committed** — they are copyrighted screenshots. `fetch_refs.sh` re-downloads
them into this directory if you want to look again. Everything that mattered is written down
below and encoded in `Tools/Level/build_layout.py`.

## Sources

| # | What | URL |
|---|---|---|
| 1 | Sinnoh Route 201, Brilliant Diamond / Shining Pearl — in-game screenshot | <https://archives.bulbagarden.net/media/upload/3/3a/Sinnoh_Route_201_BDSP.png> (via <https://bulbapedia.bulbagarden.net/wiki/Sinnoh_Route_201>) |
| 2 | Sinnoh Route 205, BDSP — in-game screenshot at the route sign, river and bridge | <https://archives.bulbagarden.net/media/upload/1/1b/Sinnoh_Route_205_BDSP.png> (via <https://bulbapedia.bulbagarden.net/wiki/Sinnoh_Route_205>) |
| 3 | Wedgehurst, Sword / Shield — the official annotated town map | <https://archives.bulbagarden.net/media/upload/8/8d/Wedgehurst_SwSh.png> (via <https://bulbapedia.bulbagarden.net/wiki/Wedgehurst>) |
| 4 | Deertrack Path, Legends: Arceus — hillside, worn path, tall-grass bands | <https://archives.bulbagarden.net/media/upload/7/7b/Deertrack_Path_LA.png> (via <https://bulbapedia.bulbagarden.net/wiki/Obsidian_Fieldlands>) |
| 5 | Obsidian Fieldlands, Legends: Arceus — the in-game topographic map | <https://archives.bulbagarden.net/media/upload/3/31/Obsidian_Fieldlands_Map.png> |
| 6 | Lake Verity, Legends: Arceus — lake in a cliff bowl | <https://archives.bulbagarden.net/media/upload/4/40/Lake_Verity_LA.png> |
| 7 | Oreburrow Tunnel, Legends: Arceus — cave interior massing and lighting | <https://archives.bulbagarden.net/media/upload/6/69/Oreburrow_Tunnel_LA.png> |
| 8 | Rowan's Lab, BDSP — interior, but the clearest example of on-axis staging | <https://archives.bulbagarden.net/media/upload/4/41/Rowan_Lab_BDSP.png> |
| 9 | Sinnoh Route 201 page (layout description, T-junction, ledges) | <https://bulbapedia.bulbagarden.net/wiki/Sinnoh_Route_201> |
| 10 | Sinnoh Route 205 page (two segments, one-way ledges, walkway over the pond) | <https://bulbapedia.bulbagarden.net/wiki/Sinnoh_Route_205> |
| 11 | Jubilife City page (grid streets, buildings by cardinal position) | <https://bulbapedia.bulbagarden.net/wiki/Jubilife_City> |
| 12 | Pokémon Workshop, "Good practices in Level Design" | <https://pokemonworkshop.com/en/learn/good-practices-in-level-design> |
| 13 | "Great Level Design Without Clear Restrictions — Pokémon Red & Blue" | <https://usaagitsun.wordpress.com/2016/12/18/great-level-design-without-clear-restrictions-pokemon-red-blue/> |
| 14 | Analysis of the first routes, Gen I–IV (grass density near towns, teaching order) | <https://fantendo.fandom.com/wiki/User_blog:Shadow_Inferno/A_short_analysis_of_the_first_Routes_in_Pokemon_Games_(Gen_I-IV)> |

## What each one actually shows

### 1 — BDSP Route 201: what a road is

The dirt path is **one continuous ribbon** with a soft, scalloped, hand-drawn outline and a
darker rim where it meets grass. It is about three player-widths across and turns through a
generous rounded corner into a T-junction. It is emphatically **not** a field of square tiles,
and it has no visible seams.

Around it: an enormous amount of **plain, empty grass**. Tiny flowers appear at maybe one per
several square metres. The density lives entirely at the edges — a wall of overlapping tree
canopies, and a **tall-grass field that is one solid contiguous mass** with a hard silhouette,
sitting immediately against the path so you can choose to enter it or skirt it.

A low earth bank runs across the middle: terrain, not an invisible wall, is what divides the
space.

→ In this layout: `paths` as splines with `edgeBlend`; `foliageFields` as contiguous polygons;
`Field_Route_Meadow` at 0.16 tufts/m²; corridor edges massed with `tree_wall`.

### 2 — BDSP Route 205: how terrain gates

A river cuts straight across the route. There is **one wooden bridge**. A stone railing runs
along the near bank so the water reads as a boundary rather than a texture. The path **branches
at a T** just before the crossing, and the route sign stands in the fork. Cliffs of red rock
bound the far side; conifers stand in dense overlapping clumps, never evenly spaced.

Three distinct ground materials are visible in one frame — light grass, a darker grass band
along the water, and the dirt path.

→ In this layout: `Water_Stream` crosses `Path_RouteSpine` at right angles at (13.6, 18.2) with
`Env_Bridge_Wood` and a `conformExclusions` disc; the crossroads sits beside it with the
signpost; `Patch_Crossroads` is a worn clearing.

### 3 — SwSh Wedgehurst: how a town is composed

The single most useful reference. **One wide cobbled main street** enters over a bridge from the
south, runs north, and forks; a **narrower dirt lane** branches off it, so the street network has
a hierarchy rather than a grid. Every building sits on a **bounded plot** — low hedge or fence —
with its **front facade addressing the street** and a short paved forecourt joining it. The
Pokémon Centre sits at a fork. The **research lab is the landmark**: largest, most complex
silhouette, staged apart on the east side. Tree hedgerows line the town boundary and separate
plots. Between all of it: large areas of plain lawn. The town outline is an irregular stepped
polygon bounded by a stone retaining lip and water.

→ In this layout: `Path_TownMain` (5 m cobble) + `Path_TownLane_West` (2.6 m dirt) +
`Path_TownLane_Lab`; `Patch_Plaza` is a *widening of the street*, not a separate square; six
houses each with fence frontage and a plot divider; `Env_Building_PokeLab` on a 0.4 m raised
forecourt at the head of the plaza.

### 4 — Legends: Arceus, Deertrack Path: what a verge looks like

The path is a **worn scar in the grass** whose edges dissolve rather than end. Tall grass appears
as **discrete bands with a clear silhouette**, at the path edge and against rock. Cliffs
transition **cliff → large boulders → small boulders → grass**; that gradient is what makes a
rock face read as terrain instead of a wall. The path narrows into a **gap between two rock
masses** — a pinch that frames the next area. The middle of the frame is almost entirely empty.

→ In this layout: `Route_FootRocks` / `Gorge_Rocks` clumps at the cliff feet with a size
gradient; the gorge pinches to ~9 m between `Mass_MassifWest` and `Mass_MassifSpur`.

### 5 — Obsidian Fieldlands topographic map: how elevation structures space

Contour rings, not steps. A raised central mesa ringed by cliff bands, a river snaking round it,
lakes sitting in bowls. Elevation is the primary structuring device, and the slopes are *graded*
— which is exactly what the rejected eight-flat-decks layout had none of.

→ In this layout: the height field is a sum of `masses`, each with an `amount` and a `shoulder`;
a 1 m shoulder on a 2.4 m rise is the town's retaining cliff, a 14 m shoulder on a −4.6 m drop is
the lake's beach.

### 6 — Lake Verity: a lake is a bowl

Water in a basin ringed by cliff, with a graded shore, not a blue plane dropped on flat ground.

→ `Mass_LakeBowl`, and `Water_Lake.polygon` is traced from the −2.2 m isoline of the baked
height field so the waterline cannot disagree with the ground holding it.

### 7 — Oreburrow Tunnel: a cave is one big room

A wide, low chamber; the ceiling is a single unbroken mass; the floor undulates; the exit is a
bright arch that carries all the light. Very few props.

→ `terrain.caves[0]` describes a hollow to subtract from `Mass_MassifWest`, 6 m at the passage
opening to 18 m at the back, with `Amb_Cave_Mouth_Light` as the one shaft.

### 8 — Rowan's Lab: on-axis staging

The important figure is centred at the far end of the room and everything else is symmetric
about that axis.

→ The lab faces **222°** and the player spawns at **45°** to it — the fixed camera's own axis, so
the first frame of the game is the dome filling the top of the screen.

### 12–14 — written level-design sources

Confirmed the gating grammar: tall grass is the danger and it is *avoidable*; grass is
concentrated near the town and thins as you progress; one-way ledges create shortcuts you can
take downhill but must walk around going back; routes teach by making the safe surface (the road)
visually obvious.

→ `Ledge_Route_South` (1.35 m, one-way south-east, with a tall-grass pocket and an item ball
below it) and `Ledge_TownRim` (the upper half of a double ledge out of town).
