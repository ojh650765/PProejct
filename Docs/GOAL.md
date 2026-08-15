# Poké Lab — Current Goal

Supersedes the original brief where they conflict. Everything not restated here still stands.

## The product

An **HD-2D** Pokémon fan game in Unity 6.3 / URP: **3D level geometry with pixel-art 2D sprites for
creatures and people**, in the style of Octopath Traveler and the "Pokémon HD-2D in Unreal Engine 5"
fan project.

One exceptionally polished vertical slice, not breadth:

```
explore → encounter → seamless battle transition → Poké Lab tactical prediction
        → animated battle → capture/victory → progression → seamless return
```

## What changed from the original brief

| | Original | Now |
| --- | --- | --- |
| Creature art | 3D modelled in Blender | **Official Gen 5 pixel sprites** |
| Level art | 3D | **3D — unchanged** |
| Characters/NPCs | 3D rigged | **Pixel sprites** |
| Battle camera | 10-shot orbiting cinematic rig | **Constrained** — billboards cannot be shot from behind |
| Creature FBX | The deliverable | **Retired** |

### Why

Scripted `bpy` modelling hit a hard quality ceiling. The user's requirement is *"난 포켓몬과 똑같은
걸 원해"* — it must look exactly like Pokémon. Official sprites satisfy that by definition; procedural
3D never could.

## Art source — settled

Official sprites from the PokeAPI sprite repository, for all twelve slice species:

- **Static front + back**, 96×96, indexed colour.
- **Gen 5 Black/White animated front + back** — 1,386 animation frames in total across the cast,
  20–110 frames per creature per view.

This removes the two problems that made the pivot look expensive:
- **Back views** were the one thing that could not be derived from a front image. They exist officially.
- **Animation** would have been thousands of hand-drawn frames. It exists officially.

Left/right are horizontal mirrors applied in the shader (`uv.x = 1 - uv.x`, never negative X scale —
that inverts winding and flips normal-map interpretation).

## Rendering requirements

- **Sprites render in the opaque queue** with `ZWrite On` + alpha clip, not transparent, so they do
  not pop through foliage cards. Alpha must be binary at the edges; keylines baked into the art.
- **Billboard to the camera in the forward pass, to the light in the shadow pass**, or cast shadows go
  razor-thin as the camera moves.
- **Synthetic sphere normals** so a flat quad takes directional light as a rounded volume.
- **`MixFog` is mandatory** — skipping it is the main cause of sprites looking pasted on.
- Sprites use point filtering; **foliage stays bilinear** (point-filtering an alpha-clip mask crawls) and
  **hard surfaces stay bilinear + aniso** (a point-filtered receding ground plane shimmers).
- Environment textures are **quantised in place at 2048², not downsampled** — HD-2D textures are
  low-colour-count, not low-resolution. Target 256 px/m in contact zones, 128 px/m elsewhere.
- Creatures use a **constant sprite resolution for every species**, not constant pixels-per-metre,
  because the camera deliberately gives a 0.3 m Pidgey and a 1.3 m Gastly the same screen area.
- Depth of field on in every grade — the tilt-shift diorama look is HD-2D's clearest signature.

## Camera — the open decision

Billboarded sprites cannot be shot from behind, and pixel art tolerates roughly a 6× change in
apparent size. The current rig varies subject size **12–16×** and includes two over-the-shoulder shots
and a 0.90-screen-fraction impact punch-in.

Either constrain the battle camera to an Octopath-style fixed angle with a shallow zoom range, or keep
3D battles and make only exploration HD-2D. **Not yet decided.**

## What is unaffected

Confirmed art-agnostic and requiring no changes: the `Core` contracts, the turn-based battle engine,
the ported prediction forest and tactical layer, the `BattleEvent` presentation stream, all UI, all
audio, the environment kit, the post-processing stack, and the VFX shaders.

## Unchanged from the original brief

The Poké Lab scanner as the signature feature; a real turn-based engine with the prediction model as a
tactical layer on top, never as the battle resolver; routes, town, cave, water, roaming wild creatures,
trainers, NPCs, weather, day/night, capturing, party management, progression; no abrupt cuts between
modes; continuous Play Mode screenshot sweeps hunting rendering and animation defects; and an
independent impartial judge agent comparing against commercial creature-collection games, with its
prompt not weakened.

## Legal note

The sprites are Nintendo/Game Freak copyright. The user has confirmed this direction; recorded here
because it becomes a real constraint if distribution is ever considered.
