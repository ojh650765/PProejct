# Play probe: watching the game instead of guessing at it

Three things were wrong with how runs were being judged, and they are three different problems
with three different answers.

| The complaint | What it actually is | What answers it |
|---|---|---|
| "the agent doesn't know the right answer" | Not a capture problem at all. Nobody can judge a number without a reference. | `--baseline`: diff against a run you accepted |
| "the agent doesn't know what it captured or where it was looking" | The telemetry existed but lived in a different file from the pictures | Captions burned into the contact sheet |
| "the agent doesn't know how fast the NPCs move" | Stills two-thirds of a second apart cannot show motion, and only the player was tracked | `trace.csv`: one row per frame, every mover |

Worth saying plainly: **no amount of video fixes the first one.** A recording still needs
something to be compared against before "is this right?" has an answer. The baseline is that
something — it does not know what is good, but it knows what changed, and in practice almost
every regression is a change.

## Running one

Drop a request in `Temp/` with the editor open. It is picked up automatically.

```jsonc
// Temp/pokelab_play.json
{
  "scene": "Assets/Game/Scenes/Town.unity",
  "outputDir": "Captures/town",
  "settleSeconds": 2.5,
  "trace": true,          // one row per frame (default on)
  "traceActors": 8,       // NPCs to follow, nearest to the player first
  "legs": [
    { "label": "north",  "move": [0, 1], "seconds": 2.0, "shots": 3 },
    { "label": "talk",   "move": [0, 0], "seconds": 1.0, "interact": true },
    { "label": "impact", "move": [0, 1], "burstFps": 30, "burstSeconds": 1.5 }
  ]
}
```

A leg is **either** a few stills **or** a filmstrip, never both — a burst holds the same stick
for the same span, so taking the stills as well photographs the same seconds twice.

The result lands in `Temp/pokelab_play.result.json`, the frames and `trace.csv` in `outputDir`.

> The probe needs the **editor open**. It polls `EditorApplication.update` and enters Play mode,
> which `-batchmode -nographics` cannot do — and a screenshot taken without a framebuffer is
> black.

## Reading one

```bash
python Tools/play_report.py Captures/town
```

Writes `contact.png` (the stills, each captioned with its own time, beat, speeds and camera
pose), one `burst_*.png` per filmstrip, and `summary.json`. Prints something like:

```
== town ==
300 frames over 4.98s   fps mean 60  p05 60  worst 60
camera  boom 6.00-6.00m  fov 60  shake peak 0.312  settled in 183ms
hit-stop at 2.00s for 200ms (timeScale floor 0.15)

actor                      declared measured  moving%     gap
player                         2.20     2.20     100%    0.00
npc.Townsfolk                  1.40     1.40     100%    0.00
trainer.Youngster              3.40     0.00       0%    3.40  <-- disagrees
```

**Declared against measured** is the column worth understanding. Declared is what the mover
says it is doing — the NavMeshAgent's own velocity, which is also what drives the animator.
Measured is what actually happened to its transform between two frames. The trainer above is
the shape of a real bug: the agent reports 3.4 m/s, the animator plays a run cycle, and the
transform has not moved a millimetre. Either number alone looks fine. Only the pair shows it.

Measured speed is divided back by `timeScale` before the comparison, so a hit-stop does not
read as every actor on screen suddenly disagreeing with itself.

## Baselines

```bash
python Tools/play_report.py Captures/town --promote Captures/golden/town.json   # accept this
python Tools/play_report.py Captures/town --baseline Captures/golden/town.json  # check it
```

Only promote a run you have actually looked at, because from then on it is the definition of
correct. The diff walks every scalar and reports relative changes over `--tolerance` (10% by
default), ignoring anything whose magnitude is under 0.01 so that noise near zero stays quiet.

## What it is not

It cannot tell you whether something feels good. It can tell you the shake peaked at 0.31 and
was gone in 183 ms, that the hit-stop is 200 ms, that the NPC walks at 1.4 m/s and that none of
those were true last week. Taste stays yours.
