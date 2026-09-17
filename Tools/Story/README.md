# Story authoring and playback checks

The Unity project remains the source of the game. Blender scripts create mesh assets;
the level builders place them, attach collisions, and bake navigation.

## Add an encounter

`new_episode.py` creates a matching actor binding, dialogue sequence and episode.
It previews the JSON by default. Use an existing scene and a sprite key from the person
registry. Positions are Unity world coordinates (X, Y, Z), on dry Ground.

```powershell
python Tools/Story/new_episode.py researcher_greeting --scene Interior_Lab --speaker Researcher --person professor --position -2 0.02 1 --requires story.pokedex --shot shot_lab_interior --line "Let us compare our observations."
```

Add `--write` to save that definition. Choose a unique actor name/episode ID. An existing
NPC's next conversation should extend its existing binding instead of duplicating the NPC.
The scaffold records its scene and brackets the episode with TakeControl/GiveControl. The runner sets the completion
flag only after successful completion and releases camera/player ownership on interruption.

Authoring files:

- `Assets/Game/Data/Story/Resources/episodes.json`: ordered beats, completion flags, chains.
- `Assets/Game/Data/Story/Resources/dialogue.json`: dialogue and choices.
- `Assets/Game/Data/Story/Resources/shots.json`: camera poses and moving timelines.
- `Assets/Game/Data/Story/interior_actors.json`: generated actor bindings; supports Town,
  Field, Route202 and the four interior scenes despite its original filename.

Run `python Tools/Story/validate_story.py` to check references and chain cycles.
In Unity, run **Tools > Poké Lab > Repair > Rebuild And Verify World** after actor or level
changes. For timeline edits, also run **Tools > Poké Lab > Rebuild > Sequencing Timelines
(from shots.json)**. Scene rebuilds replace generated scene objects; edit the source data
or builders instead of those generated objects.

## Movement and cameras

Ground is an explicit layer. Water beds and prop tops cannot generate walkable destinations.
Town, Field and Route202 share one baked navigation asset and world coordinates. Bridge decking is separate from its
non-walkable structure; a sloping river uses local water heights for its exclusions.

`ExitActor` routes use `>`-separated markers ending at the destination. Keep control until
the actor is outside the camera, then hide and place it at the last marker. The friend uses
7.5 m/s. Use authored road markers for the visible portion. Do not add a straight movement
fallback across cliffs. The runner reports a failed beat rather than granting story flags.

Camera moves share `CameraPath` easing and geometry clearance. Consecutive uses of the same
shot preserve its existing framing. The bag and Starly approach share a held player shot,
so they do not repeatedly orbit or restore and reapply a zoom. Long or obstructed shot
changes cut instead of blending through terrain. Starter selection finishes its case
reveal and releases its camera before it publishes the selection to the battle sequence.

## Verification

- **Repair > Play Test Story Sequences**: plays the nine current episodes, answers through
  their actual UI, battles, checks input release, and captures frames in `previews/story`.
- **Repair > Play Test All Building Doors**: checks reachable interaction positions and
  single-grant item collection, physically walks across the bridge and through nine doors,
  checks the live profile survives scene loads, and verifies the Center restores HP/PP.
- **Repair > Inspect Bridge Navigation**: samples the deck against geometry and path reachability.
- EditMode assembly `PokeLab.Overworld.Tests`: dry ground, prop, water and step regressions.

Reports go to `Temp`. The play probes use temporary in-memory profiles and do not save them.

## Current story boundary

A new game begins in `Interior_PlayerHome`, watching the red Gyarados lake broadcast on
the television, followed by Mom's departure dialogue. The repaired opening includes the
lake bag, Sinnoh starters, Starly encounter, Rowan's
invitation and the laboratory Pokédex scene. House 01 now leads to `Interior_PlayerHome`;
the other six house doors share `Interior_House`. The laboratory and Pokémon Center have
their own layouts.

After the Pokédex, the player visits Mom, receives a Journal, then receives Barry's Parcel
from his mother. These two scenes chain inside the player home. A dirt road from Town bends northwest to Route202. Town, Field and Route202 load additively;
crossing their outdoor boundaries keeps the same player and camera with no fade or warp.
The sign gives directions, and the capture lesson requires the completed home visit.
Interiors and the cave retain doorway transitions.

On Route 202 the assistant demonstrates weakening and catching Bidoof, gives five Poké Balls,
and leaves northward. The demonstration uses its own party, an automatic two-turn sequence,
and a guaranteed tutorial throw. The normal capture formula is unchanged. The demonstrated
Bidoof is not given to the player. Grass encounters unlock after the lesson; this first
route section currently has Starly and Bidoof. The player can return to Town.

Running remains available from the beginning; this chapter does not add a Running Shoes gate.
The Journal appears in the bag with completed story entries. Journal, Parcel and Poké Ball
gifts have individual receipt flags so an interrupted episode cannot duplicate its reward.

The remaining DP work includes the rest of Route 202's trainer roster and encounter species,
Jubilife and the parcel delivery, then the route toward Oreburgh. The existing cave is not
a complete DP chapter. These areas still need their maps, NPCs and encounter content.

## Rebuild and test the continuation

- **Tools > Poké Lab > Story > Rebuild Home And Route 202** builds the dedicated home,
  Town junction and route, including actor bindings. After outdoor geometry changes, also
  run **Story > Rebuild Connected Outdoor World** to rebuild all three outdoor scenes and
  their shared navigation.
- **Story > Play Test Home And Route 202** walks through doors, follows the proximity story
  triggers, checks repeated visits, in-memory save roundtrips, the walk to the north boundary
  and the isolated capture demonstration. Reports and frames
  go to `Temp/after_dex_play.json` and `previews/after_dex`. No player save is written.
- `Episode.Scene` optionally confines a chapter and its interrupted-chain resumption to its
  authored scene. `GiveItem.Target` optionally names a receipt flag for an idempotent gift.
- Beat `22` (`CaptureLesson`) waits for the actual battle presentation and successful capture
  before continuing to the reward. The stage request explicitly marks it as a demonstration.
- Actor bindings support `appearsOnFlag`, `leavesOnFlag` and `mentor` (the assistant's body
  and name follow the opposite player character choice).

The early plot reference is the [Diamond/Pearl walkthrough, part 1](https://bulbapedia.bulbagarden.net/wiki/Appendix%3ADiamond_and_Pearl_walkthrough/Section_1)
and [part 2](https://bulbapedia.bulbagarden.net/wiki/Walkthrough%3APok%C3%A9mon_Diamond_and_Pearl/Part_2).
Dialogue in the project is newly written; the maps still use the prototype's shared town/field layout.

Route202 preserves the outdoor follow camera and uses six combined grass meshes. Each visible patch sits
inside its encounter trigger; those triggers remain disabled until the capture lesson finishes.
Running is available from the beginning, with no Running Shoes unlock added.

## TV and presentation polish

- `Tools/Blender/environment/build_television.py` creates the low-poly CRT and cabinet,
  separate UV screen, FBX and `Tools/Blender/projects/Television.blend`. Run through Blender MCP.
- `Assets/Game/Art/Environment/Interior/Textures/TV_Broadcast.png` is the generated 4:3
  pixel-art lake broadcast image. Its screen material is unlit, point filtered and capped
  at 512 pixels in Unity. **Story > Rebuild Player Home Only** places the TV and viewing marker.
- **Story > Play Test TV Opening** plays the opening and walks outside; reports go to
  `Temp/story_sequences_play.json`. **Repair > Play Test Starter Sequence** checks the lake
  chain, including its first battle.
- `StageCreature` places Bidoof before the assistant's first line. It uses normal field
  scale and is hidden under the battle transition before the overworld becomes visible.
- Capture text observes the result only after the ball's final shake and click or breakout.
  The battle outro waits for the presentation queue so it cannot truncate that result.
- Dialogue uses a full-width translucent black band and a separate compact nameplate. The default rival display name is Barry / 용식;
  legacy internal IDs such as `npc_kes` remain stable for saved content.

`CreatureApproach.Speed` sets the flock's approach speed (4.2 m/s in the opening).
The staged bird and three companions reserve separate reachable positions around the
player and rival, advance together and brake before stopping. A missing path fails the
beat instead of relocating a bird across terrain. Field Starly uses a small speed-driven
step motion; this does not affect battle sprite movement. The starter play probe checks
that all four birds move and reach at least 3 m/s.

## Home startup and exits

The startup flow waits for the save session before deciding to play the opening.
Every new opening places the player at Spawn_WatchTV inside Interior_PlayerHome.
The TV scene never asks for a name; Rowan asks inside the laboratory before giving the Pokédex.
All four interior templates have a flush projecting entry floor with low side walls.
The rival mother approaches the player's near side and stops short instead of walking through a fixed marker.

Verify with **Tools > Poké Lab > Story > Play Test Home Startup**.
This checks actual startup without a probe warp, name entry, mother clearance, four exits, portraits and alpha fades.
