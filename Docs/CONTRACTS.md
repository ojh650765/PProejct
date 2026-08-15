# Poké Lab — Integration Contract

Read this before writing a line of code. It is the only coordination mechanism between
eight parallel workers. Violating the ownership table causes merge conflicts that cost
more than the feature you were building.

## The product

A 3D creature-adventure vertical slice in Unity 6.3 / URP. One loop, polished end to end:

```
explore → encounter → seamless battle transition → Poké Lab tactical readout
        → animated battle → capture or victory → progression → seamless return
```

Depth of content is explicitly *not* the goal. Polish is. One route, one town, one cave,
one water body, a handful of creatures, and a dozen moves — all of them finished.

## Source material

`https://github.com/falcons-eyes/pokemon_battle_prediction` (GPL-3.0) provides the data
and the intelligence, not the gameplay:

- `data/pokemon_master_corrected_with_abilities.csv` — 697 species: stats, both types,
  height, weight, base experience, PokeAPI ability identifiers, Korean names.
- `pokemon_battle_app/domain.py` — the 18×18 type chart and the ability immunity table.
- `data/pokerogue_derived/` — defensive ability type modifiers, evolution edges,
  starter costs, gen7+ base stat corrections.
- `battle_refinement_outputs/pokemon_battle_app_model.joblib` — a 120-tree
  `RandomForestClassifier` over 11 features, 93.5% GroupKFold accuracy, uncalibrated
  (`selected_probability_model == "보정 전"`).
- `pokemon_battle_app/backend.py` — symmetric probability and the neutral-substitution
  explanation algorithm. **Port this logic exactly.**

**The forest is a tactical advisor, never the battle engine.** Battles are resolved by a
real turn-based simulation with damage rolls, accuracy, crits and RNG. The forest answers
"who tends to win this species matchup and why", which is what the scanner displays.

## Ownership table

A worker writes **only** inside its own paths. Never edit another worker's files, and
never edit `Core/`.

| Worker | Owns | Assembly |
| --- | --- | --- |
| Intelligence | `Assets/Game/Scripts/PokeLab/`, `Assets/StreamingAssets/pokelab/`, `Tools/Export/` | `PokeLab.Intelligence` |
| Battle | `Assets/Game/Scripts/Battle/`, `Assets/Tests/Battle/` | `PokeLab.Battle` |
| Overworld | `Assets/Game/Scripts/Overworld/` | `PokeLab.Overworld` |
| UI | `Assets/Game/Scripts/UI/`, `Assets/Game/UI/` | `PokeLab.UI` |
| Cinematics | `Assets/Game/Scripts/Cinematics/` | `PokeLab.Cinematics` |
| VFX & Shaders | `Assets/Game/Scripts/VFX/`, `Assets/Game/Shaders/`, `Assets/Game/VFX/` | `PokeLab.Vfx` |
| Audio | `Assets/Game/Scripts/Audio/` | `PokeLab.Audio` |
| Blender | `Tools/Blender/`, `Assets/Game/Art/` | — (assets only) |
| **Integrator (master)** | `Core/`, `Assets/Game/Scenes/`, `Assets/Game/Prefabs/`, `ProjectSettings/` | `PokeLab.Core` |

### Files no worker may touch

- `Assets/Game/Scripts/Core/**` — frozen contract. Need a change? Report it; the
  integrator applies it and tells everyone.
- `*.unity`, `*.prefab`, and serialized `*.asset` — the integrator wires these through
  Unity MCP. Ship prefab-ready components, not scene edits.
- `ProjectSettings/**`, `Packages/manifest.json`.

Exception: the Blender worker writes `.fbx`/`.png` under `Assets/Game/Art/` and the
`.meta` files Unity generates for them. It still authors no prefabs.

## The seam: `BattleEvent`

`Core/BattleEvents.cs` is why this parallelises. The battle engine is pure C#, never
touches the scene, and emits an ordered event stream. Cinematics, VFX, UI and Audio each
subscribe independently and play their own response.

```csharp
public sealed class BattlePresenter : IBattleEventListener
{
    public void OnBattleEvent(BattleEvent evt)
    {
        switch (evt)
        {
            case MoveExecutedEvent move: PlayAttack(move); break;
            case DamageDealtEvent hit:   PlayImpact(hit);  break;
        }
    }
}
```

Rules:

1. Presentation is **advisory**. State already changed inside the engine. Dropping an
   event must never desync anything.
2. Never mutate `CreatureInstance` outside the engine.
3. `SuggestedDuration` is a floor, not a deadline. A presenter may stretch a beat; it may
   not cut one short.
4. Need a new signal? Ask the integrator to add an event. Do not smuggle state through
   `MessageEvent.Text`.

## Cross-system dependencies

Everything resolves through `ServiceHub` at runtime, so no assembly references another
worker's assembly:

```csharp
var oracle = ServiceHub.Get<IPokeLabOracle>();
var art    = ServiceHub.Get<ICreatureArtRegistry>();
```

Registration happens once in the boot scene, owned by the integrator. If you need a
service that does not exist yet, code against the interface and guard with
`ServiceHub.TryGet` so your system degrades instead of throwing during partial integration.

## Art conventions

Agreed up front so models, animation, cameras and VFX line up without rework.

- **Units** — 1 Unity unit = 1 metre. Creature display heights come from
  `ICreatureArtRegistry.GetDisplayHeight`, never hardcoded.
- **Orientation** — models face **+Z**, feet at the origin, Y up. Blender exports with
  `-Y forward, Z up` so the FBX lands correctly.
- **Required anchors** — every creature rig exposes empties named `Anchor_Head`,
  `Anchor_Body`, `Anchor_Muzzle`. Health bars, hit VFX and projectiles attach to these;
  a missing anchor breaks battle framing.
- **Required clips** — one per `CreatureAnimation` value. A stub clip is acceptable
  early; a missing clip is not.
- **Scale** — authored at true size. A 0.3 m creature and a 6 m creature must both frame
  correctly through the shared camera rig.
- **Naming** — `Creature_<SpeciesId>_<NameEn>` for prefabs and clips.
- **Materials** — target the shared stylised toon shader the VFX worker owns. Ship models
  with sensible UVs and vertex colours; do not author bespoke shaders per creature.

## Definition of done

Compiling is not done. Committing is not done. A worker is done when its deliverable is
committed, self-reviewed, and the report lists: what was implemented, files changed,
commit SHA, Unity-side setup the integrator must perform, known limitations, and risks.

The *project* is done when the loop above runs in Play Mode and survives visual review
against commercial creature-collection games.

## Non-negotiables

- No blocky primitives, no obviously procedural placeholder art in anything the player sees.
- No abrupt camera cuts between modes. Every transition is authored.
- Determinism in the battle engine: same seed plus same actions equals the same event stream.
- The scanner reads as a trainer's field device, not as debug UI.
