# Art direction: HD-2D pivot — implementation direction

**Status:** direction document. Analysis only — no code written, no assets changed.
**Date:** 2026-08-16

## Settled by the user

> **레벨 오브젝트는 3d여야 함** — level objects must be 3D.

The split is fixed and is no longer an open question:

- **3D geometry:** terrain, cliffs, buildings, trees, props, water — the level.
- **2D pixel sprites:** creatures, the player, NPCs and trainers only.

That is the Octopath / HD-2D division. This document takes it as given and answers *how*.
Creature sprite production is out of scope here. What follows covers the three questions the
user raised, the camera specification the split forces, the environment treatment, and the
integration work that decides whether it looks like one image or two.

**Headline:** the environment side of this pivot is cheap and mostly already done — **~6-9 days**
of shader and texture work on assets that survive intact. The expensive and risky part is
**the battle camera**, which needs 8-12 days and loses 3 of its 10 shots outright. Section 9
records the costs this decision carries so they are visible rather than laundered.

---

## 1. The three questions, answered directly

### 1.1 "Flip the plane horizontally when moving left/right"

Works, and it is what every 2D-in-3D game does. But **do not use negative X scale.**

An odd number of negative scale axes mirrors the object into left-handed space, and two things
break: winding order inverts (backface culling culls the wrong face), and **normals and normal
maps flip** — a long-standing, still-open Unity behaviour tracked from 2019.4 through 2021.2 and
unchanged in URP. On a lit sprite this reads as the creature's shading snapping to a different
light source the instant it turns around.

That matters here specifically. `LightingDirector.cs` drives a directional key that moves through
the day, and section 5.4 puts *synthetic sphere normals* on the sprites so they take that light
like rounded volumes. A sprite that re-lights itself backwards when it turns is more obviously
broken than a sprite with no lighting at all.

**Correct mechanism: flip UVs in the shader** — `uv.x = 1 - uv.x` behind a `_FlipX` float.
Geometry, winding and normals untouched. Costs one shader property. `SpriteRenderer.flipX` is
documented as unsupported with normal-mapped sprite shaders, so it is not an option here.

A separately authored mirrored sprite is correct in every respect but doubles atlas footprint;
worth it only for asymmetry the player will notice (a scanner held in the right hand). All 12
creatures are bilaterally symmetric. Skip it.

**Q1 is solved in the shader and is not where the risk is.**

### 1.2 "Closer to or further from the camera — the representation gets ambiguous"

The instinct is right, but this is **two problems fused into one**, and only one of them is a
"which image" problem.

**Problem A — facing.** Genuinely a "which image" problem, with a standard answer:

| Set | Unique drawn | Supports |
|---|---|---|
| 4-direction | 3 (front, side, back); side mirrored | Camera at one fixed yaw. Classic Pokémon, RPG Maker. |
| **8-direction** | **5 (front, front-¾, side, back-¾, back); 3 mirrored** | **Camera at 45° steps, or free facing under a constrained camera. This is our target.** |
| 16-direction | 9 | Near-continuous facing. Nobody hand-draws this. |

Runtime lookup is trivial: signed angle between sprite forward and the camera's flattened
forward, divided by `360/N`, rounded, used as an atlas index. Unity's own `BillboardAsset` does
exactly this for SpeedTree.

**Problem B — apparent size.** *Not* a "which image" problem, and this is the one that drives the
camera specification in section 3. A creature approaching the camera doesn't need a different
picture; it needs the same picture drawn 8× larger, and that is what makes pixel art look broken
rather than intentional.

### 1.3 "Or would just swapping the sprite image work?"

**For facing: yes — that is exactly the mechanism, and it is the right one.**

**For near/far: no, and it cannot.** Swapping images changes *which* picture is drawn, not how
many screen pixels it is stretched across. The fix for depth is not an art fix at all — it is a
**camera constraint**. HD-2D looks good at varying depth because HD-2D cameras barely change the
subject's on-screen size. Octopath's tilt-shift wide-FOV setup deliberately flattens the viewpoint
while keeping depth cues; its battle camera pushes maybe 1.3× total.

**Our battle rig varies subject size by 12-16×.** That is the whole problem in one number, and it
is why section 3 exists.

---

## 2. Pixel-grid coherence — the numbers everything else hangs off

### 2.1 Creatures use constant *resolution*, not constant texel density

This is forced by our own architecture and it is worth stating plainly.
`ICreatureArtRegistry.GetDisplayHeight` plus `BattleCameraRig.SolveFov` deliberately make a 0.3 m
Pidgey and a 1.3 m Gastly occupy **the same screen area**. So they must have the **same source
resolution**, not the same pixels-per-metre. A constant px/m across a roster spanning 4.3× in
height would give Pidgey a 38 px sprite and Gastly a 166 px sprite for identical framing.

This is exactly what Gen 5 does: 96 × 96 for every species from Caterpie to Wailord.

| Asset | Source | Notes |
|---|---|---|
| **All 12 creatures** | **256 × 256** | Uniform. Zubat and Gastly simply use more of the frame. |
| **Player / NPCs / trainers** | **192 × 256** | Taller than wide; chibi proportions (3-4 heads), not the current 1:7.5 mannequins. |

### 2.2 The environment's density is set by the *overworld*, not by battle

Sprites and geometry share a frame, in focus, at the same distance, mostly in the overworld. In
a battle close-up the environment is behind the subject and inside the DOF blur, so its texel
resolution drops out of the comparison. Match the overworld.

A 1.7 m human at 256 px is **~150 px/m**. Round up for the surfaces characters actually touch:

| Zone | Target | Rationale |
|---|---|---|
| **Contact zone** — ground, paths, terrain and props under characters | **256 px/m** | In focus, adjacent to sprites, compared directly. 0.5 m modular grid = **128 px**, a clean power of two. |
| **Everything else** — walls, cliff faces, canopies, distant terrain | **128 px/m** | Far, DOF-blurred, or at a grazing angle. |
| **Hero close-range** — bridge planks, well rim, anything the camera pushes into | **256 px/m** | |

**The existing atlases already sit in the 100-256 px/m band.** Five atlases at 2048² on a 4 × 4
grid = 512² cells; a bark cell tiling twice up a 5 m trunk is ~205 px/m, a plaster cell tiling
every 2 m is 256 px/m. **Density is not the problem.** See 5.1 for what is.

---

## 3. Camera specification — the expensive part

`BattleCameraRig.cs` is not a generic follow camera. It solves placement per unit of creature
height, solves the lens so the subject fills a fixed screen fraction, then lets a deoccluder slide
the camera at runtime. It is good work and it is hostile to flat sprites. Here is the audit, which
is now a specification rather than an objection.

### 3.1 Shot-by-shot, from `ShotProfile.DefaultLibrary()`

| Shot | Fraction | Creature @1080p | Verdict |
|---|---|---|---|
| `WideEstablishing` | 0.42 of stage | ~82 px (Pikachu) | **Keep.** The HD-2D shot. |
| `PlayerOverShoulder` | 0.28 | 302 px far | **Cut.** Near creature shown *from behind*, cropped, foreground. A camera-facing billboard shows its front while its back is turned. No HD-2D game has this shot; that is not a coincidence. |
| `OpponentOverShoulder` | 0.28 | 302 px far | **Cut.** Same failure mirrored. |
| `AttackerCloseUp` | 0.68 | 734 px | **Keep, retuned.** Front-¾ available from the 8-set. Reduce fraction to ~0.45. |
| `TargetReaction` | 0.62 | 670 px | **Keep, retuned.** Reduce to ~0.45. |
| `ImpactPunchIn` | **0.90**, Dutch -6°, shake 1.6 | **972 px** | **Cut or gut.** ~970 px of flat cutout shaken hard. This is the shot that tells the player the creature is cardboard. Replace with a VFX-and-shake beat that does not push in. |
| `SendOut` | 0.45 | 486 px | **Keep.** |
| `Faint` | 0.55, `Up` = 0.06+0.20·h | 594 px | **Cut or reframe flat.** Camera is *below* mid-height looking up; a Y-billboard has no underside, a full billboard leans toward the lens as the creature dies. |
| `Capture` | 0.50 | 540 px | **Keep.** Mostly frames the ball, a real 3D object. |
| `Victory` | 0.50, `Up` = 0.20+0.90·h | 540 px | **Reframe.** Rising and looking down: the sprite either stays vertical while the ground rotates under a creature that never foreshortens, or tilts and lies down. |

**6 shots survive (2 retuned), 2 need reframing, 3 are cut.**

### 3.2 Two structural fixes required

**Yaw must become authored, not solved.** `ConfigureOcclusion` gives every camera a
`CinemachineDeoccluder` with `PreserveCameraHeight`, `MaximumEffort` 4, 0.25 s smoothing. When a
tree occludes, the camera slides laterally to an angle nobody chose — and a discrete 8-direction
sprite set needs predictable yaw or it pops, triggered by scenery. **Set `AvoidObstacles.Enabled =
false` on creature-facing shots and rely on the existing standoff floor and near-clip solve, which
already prevent intersection geometrically.** Keep the decollider (terrain) — vertical motion does
not change sprite direction.

**Blends must not sweep through unsupported yaw.** `ShotBlendRule.DefaultTable()` blends
shoulder-to-shoulder over 0.6 s across ~120° of yaw — three visible direction pops inside one
blend. With both shoulder shots cut this rule becomes dead; delete it. Remaining blends must be
checked to stay inside one 45° sector, or the direction lookup must be **hysteretic** (require
~55° of travel before switching index) so a blend that grazes a boundary does not flicker.

**Overworld:** `OverworldCameraRig.cs` is currently a free 360° yaw orbit with -25° to +60° pitch
and SphereCast pull-in. Constrain to **8 yaw steps with snapping**, pitch locked to a narrow band
(**35°-50°**, the HD-2D diorama band), and keep the distance pull-in — dolly does not break sprite
direction, only yaw does.

---

## 4. Render-stack recipe

Existing infrastructure covers almost all of this. `VolumeProfileFactory.cs` already wires
Tonemapping (Neutral), ColorAdjustments, Bloom, Vignette, ChromaticAberration, FilmGrain, Bokeh
DOF and CameraOnly MotionBlur, driven per-grade from `GradeLibrary.cs`.

| Setting | Now | Target | Why |
|---|---|---|---|
| **Depth of field** | **`false` in all 6 grades** (`GradeLibrary.cs` 87, 151, 214, 281, 345, 409) | **On.** Bokeh, focus at stage centre, aperture **f/2.8-4.0** | The single largest free win in the project. Tilt-shift diorama read is *most* of what makes HD-2D feel like HD-2D, and it is what lets non-integer sprite scaling pass unnoticed. |
| **FilmGrain** | on in several grades | **≤0.02 or off** | Per-pixel grain on a 4×-magnified sprite crawls and reads as video compression. |
| **Bloom** | 0.45-1.20 | keep, lean to the high end | Simulates CRT light bleed; core to the look. |
| **MSAA** | 4× | **keep 4×** | See 5.4 — it costs sprites nothing and keeps helping geometry. |
| **`_ShadeSteps`** (toon ramp) | 3 | **2-3, `_ShadeSoftness` ~0.02** | Flat banded colour is why Sugimori art reads as Sugimori art. Must be **the same value on sprites and environment** or they read as two renderers. |
| **Pixel Perfect Camera** | n/a | **do not use** | It is a 2D-renderer component assuming an orthographic camera on a fixed grid. Our camera is perspective with a per-shot runtime `SolveFov` under continuous Cinemachine damping. Accept non-integer scaling and bilinear magnification — which is what Octopath actually does. |

---

## 5. Environment treatment

### 5.1 Texture treatment — the atlases

**Do not re-author at lower resolution. Do not downsample. Quantise in place at 2048².**

This is the key non-obvious point: **HD-2D environment textures are not low-resolution, they are
low-colour-count.** Downsampling loses the crisp edges you want and destroys the normal maps;
re-authoring smaller throws away work for no gain. The "pixel" read comes from palette discipline
and flat shading, not from texel count.

The work, per atlas cell:

1. **Palette quantise to 12-24 colours per cell**, seeded from `Tools/Blender/ref_palettes.json`,
   with no dithering (dither reads as noise at these densities and fights the bloom).
2. **Flatten the value range.** Procedural noise variation inside a cell should collapse to 3-5
   discrete steps. This is what turns "a texture" into "painted pixel art".
3. **Add a keyline** where a cell has a natural boundary (plank edges, stone joints, roof tile
   rows) in `_OutlineColor` (0.08, 0.07, 0.12) so geometry and sprites share one line language.
4. **Leave the normal maps alone** except for a mild strength reduction. They carry the lighting
   response that keeps the geometry reading as 3D — which is the whole point of the settled split.

Scriptable with numpy/PIL over 5 atlases × 2 maps = 10 textures. **~1 day including per-family
palette tuning.**

**Filtering — and it does differ by family:**

| Family | Filter | Mips | Alpha | Why |
|---|---|---|---|---|
| **Character/creature sprites** | **Point** | **On** | Alpha clip, **alpha-to-coverage OFF** | Point is the pixel look. Mips are mandatory because the wide shot minifies to ~0.3× and would otherwise shimmer. A-to-C would soften the intended hard pixel edge. |
| **Foliage** | **Bilinear + aniso 4×** | On | Alpha clip, **alpha-to-coverage ON** | Point-filtering an alpha-clip mask gives jagged crawling leaf edges. A-to-C with the existing MSAA 4× gives hard-but-stable silhouettes. |
| **Hard surfaces** (Town, Terrain, Props) | **Bilinear + aniso 8×** | On | Opaque | A point-filtered ground plane receding to the horizon is a shimmering mess that mips cannot fix on the magnification side. Octopath does **not** point-filter its environments. |

### 5.2 Geometry survival by family

**Nothing needs remodelling. All 110 environment FBX survive as geometry.** The kit is already
silhouette-driven low-poly, which is exactly what HD-2D wants — `dressed_route.png` is close to a
diorama already.

| Family | Assets | Tris | Verdict |
|---|---|---|---|
| **Foliage** — Tree ×12, Bush, Fern, Flower, Grass, Lilypad, Moss, Reed, Vine | 34 | 10-1404 | **As-is.** Chunky canopies with vertex-colour wind are correct for HD-2D. Best-fitting family in the kit. |
| **Terrain** — Cliff ×6, Rock ×8, Cave ×6, Riverbank, Bridge, Stepping, Waterfall | 25 | 94-1934 | **As-is geometry, normals pass required.** See below. |
| **Town** — House ×3, Building, Market, Well, Lamp, Fence ×2, Path ×3, Bench, Barrel, Crate, Planter, Signpost | 21 | 108-4936 | **As-is.** Crisp architecture on a 0.5 m modular grid is the strongest HD-2D fit in the project. |
| **Props** | 5 | 1380-1962 | **As-is.** |
| **Characters** ×4 (+4 LODs, 5 `@Clip` FBX, Humanoid rig, Characters atlas) | 4 | 3000-3224 | **Thrown away** — these become sprites. ~12,500 tris plus the rig plus one 2048² atlas. Accepted cost; they were the weakest assets in the project. |

**The one real geometry-adjacent task: split normals.** The manifest states *"custom split normals
are authored; do not recalculate"*. Once textures go flat and quantised, smooth-shaded normals on
rock and cliff produce soft gradients that fight the flat texture and read as mush. You want
**harder facet breaks**, not softer — a normals-authoring pass in Blender on the Terrain family
(Cliff, Rock, Cave, Riverbank), not a remodel. **1-2 days.**

### 5.3 Shader path

| Shader | Lines | Verdict |
|---|---|---|
| `PokeLabTerrainBlend` | 539 | **Stay.** Retune only: `_ShadeSteps` → 2-3, `_ShadeSoftness` → ~0.02, kill specular. Four-layer world-XZ planar + triplanar rock is exactly right and needs no structural change. |
| `PokeLabFoliage` | 528 | **Stay.** Retune banding to match. Keep bilinear + alpha-to-coverage (5.1). Vertex-colour wind (R sway / G phase / B flutter) is a genuine HD-2D asset — Octopath-class foliage motion for free. |
| `PokeLabPropGroundBlend` | 507 | **Stay, and it becomes load-bearing.** Its contact blend — *"the lowest `_BlendHeight` metres tint toward the terrain colour so a boulder sinks into the grass instead of showing a hard intersection line"* — is the exact mechanism sprites need. See 5.4. |
| `PokeLabWater` | 383 | **Stay.** Retune only. Depth/opaque-texture requirements unchanged. Sorting against sprites needs the opaque-queue decision in 5.4 to work. |
| `PokeLabSky` | 232 | **Stay unchanged.** Driven by `LightingDirector`; dithered gradients still needed. |
| `PokeLabDecal` | 203 | **Stay, and promote.** It already lists *"the shadow blob under a creature"* as a supported case. That is the sprite-grounding solution, already written. |
| `PokeLabCreature` | 691 | **Fork, do not delete.** Creatures become sprites, so it stops being a creature shader — but its `ForwardLit` pass (toon ramp, rim, fog, shadow coord) and its `ShadowCaster` pass are the correct base for the new billboard shader. Keep it in service for any remaining 3D actor. |
| VFX set — Dissolve, EnergyTrail, ForceField, VfxGas, VfxParticle | — | **Stay unchanged.** Art-agnostic. |
| **`PokeLabSpriteBillboard`** | **new, ~350 lines** | **The only new shader required.** Fork of `PokeLabCreature`. Spec in 5.4. **2-3 days.** |

**Disable the outline pass on the sprite shader.** `PokeLabCreature`'s `Outline` pass is an
inverted-hull; on a quad it produces a rectangle border. **Bake a 1 px keyline into the sprite art
instead**, in `_OutlineColor`, so sprites and props share one line language.

### 5.4 Making 3D geometry and 2D sprites sit in one image

This is what decides whether the pivot reads as one picture or as stickers on a diorama.

**(a) Shadow casting — the most-reported HD-2D bug.**
A camera-facing quad casts a camera-facing silhouette, so the creature's shadow goes razor-thin and
visibly vanishes as the camera orbits perpendicular to the light. **Fix: billboard toward the
camera in `UniversalForward`, but billboard toward the *light* in `ShadowCaster`.** The
`ShadowCaster` pass already exists in `PokeLabCreature` (lines 485-556) and already computes
`lightDirWS` for `ApplyShadowBias`. This is roughly a ten-line change in a pass you already own.

**(b) Shadow receiving and self-shadowing.**
A flat quad has one constant world normal, so `NdotL` is uniform and the sprite either fully lights
or fully shades — and it receives its own shadow, which is why the standard complaint is that
billboard characters are *"almost always half in shadow"*. Two fixes, use both:

1. **Synthetic sphere normals.** Do not light with the billboard's flat normal. Derive
   `n.xy = uv*2-1`, `n.z = -sqrt(1 - |n.xy|²)`, rotate into world space by the billboard basis. A
   flat sprite then takes directional light like a rounded volume. Costs nothing and is the single
   biggest "not pasted on" win.
2. **Exclude the sprite from its own shadow receive** — bias `shadowCoord` toward the light, or
   put sprites on a layer excluded from the receive test.

**(c) Grounding.**
Use `PokeLabDecal`'s blob case. A box-projected decal conforms to the real terrain, which a flat
quad under the feet cannot. Pair it with a **contact-darkening gradient over the bottom ~8% of the
sprite**, ported directly from `PokeLabPropGroundBlend`'s contact blend — a mechanism that exists
in this project for precisely this reason, applied to boulders today and to sprites tomorrow.

**(d) Depth sorting and z-fighting.**
A vertical quad and a horizontal ground plane intersect in a line at the feet — guaranteed
z-fighting along the contact edge. Ranked fixes:

1. **Tilt the billboard back by the camera pitch** so the quad's bottom edge sits slightly nearer
   than its top. Standard HD-2D practice; also fixes the "razor-thin viewed from above" failure.
2. **Write sloped synthetic depth** — feet nearer than head — so sprite-vs-sprite and
   sprite-vs-prop sorting is correct too, not just sprite-vs-ground.
3. `Offset -1 -1` on the contact edge only, as a last resort.

**Render sprites in the opaque queue with `ZWrite On` and alpha clip — not the transparent
queue.** Transparent-queue objects sort per-object by camera distance and will pop in front of and
behind foliage cards unpredictably. Opaque + clip gives correct per-pixel depth against all
geometry for free. This is also why MSAA 4× stays: it anti-aliases geometry silhouettes, not
texture interiors, so it costs the pixel look nothing while continuing to help the environment.

**(e) Light and fog response — the anti-"pasted on" checklist.**
`LightingDirector.ApplyAmbientAndFog` already sets `RenderSettings.fog = true`,
`FogMode.ExponentialSquared`, Trilight ambient, per grade. The sprite shader must opt in to all of
it or it will float:

- `#pragma multi_compile_fog` + `ComputeFogFactor` + `MixFog`, exactly as `PokeLabCreature` does at
  lines 167 / 215 / 227. **Skipping fog is the number-one cause of sprites looking pasted on.**
- `SampleSH` with the synthetic normal so sprites pick up the same sky/equator/ground tint.
- **The same `_ShadeSteps` and `_ShadeColor` as the environment.** If the world bands at 3 steps
  and the sprite bands at 8, they read as two different renderers in one frame.
- The grade LUT and bloom are full-screen post, so they apply automatically — but confirm sprites
  render before the volume stack, which the opaque queue guarantees.

---

## 6. What is kept, reworked and thrown away

**Kept unchanged.** `Core/` contracts — `ICreatureArtRegistry.GetCreaturePrefab` returns a
`GameObject`, and a billboard prefab satisfies it; `GetPortrait` already returns a `Sprite`;
`GetDisplayHeight` is art-agnostic. **Zero contract changes.** `ICreatureView` holds
(anchors become quad offsets). `CreatureAnimation`'s 14 states hold. The battle engine, the
`BattleEvent` stream, the ported prediction forest, UI and audio are confirmed art-agnostic — the
engine is pure C#, never touches the scene, and presenters subscribe. All 110 environment FBX, all
5 atlases, the full post stack and `LightingDirector` are kept. Ten of eleven shaders are kept.

**Reworked.** `BattleCameraRig` (shot library, deoccluder policy, blend table) — 8-12 days.
`OverworldCameraRig` (yaw snapping, pitch band). `CreatureView.SwapModel` / `ResolveAnchors`.
Split normals on the Terrain family.

**Thrown away.** The 4 human character FBX + 4 LODs + 5 `@Clip` animation FBX + the Humanoid
rig + the Characters atlas. Three battle shots. `PokeLabCreature`'s outline pass, for sprites.

**The 12 rigged creature FBX and their 168 animation clips are out of scope here** — whether they
survive depends on how creature sprites get authored, which is not my call.

---

## 7. Ranked cheapest path

Do these in order. Each is independently shippable and each front-loads visible payoff.

| # | Task | Effort | Payoff |
|---|---|---|---|
| 1 | **Turn DOF on** in all 6 grades; drop FilmGrain to ~0 | **~1 hour** | Largest single visual change available. Diorama read, immediately. |
| 2 | **Retune the toon ramp** across all material families — `_ShadeSteps` 2-3, `_ShadeSoftness` 0.02, kill specular | **1 day** | Flat graphic read; the thing that makes it look drawn rather than moulded. |
| 3 | **Constrain the overworld camera** — 8 yaw steps, 35-50° pitch band | **2 days** | Unblocks all sprite work; makes the overworld read as HD-2D on its own. |
| 4 | **Palette-quantise the 5 atlases** in place at 2048² | **1 day** | Environment stops looking procedural. |
| 5 | **`PokeLabSpriteBillboard` shader** — light-facing shadow caster, sphere normals, `_FlipX`, direction UV, contact gradient, fog | **2-3 days** | The whole integration layer in one file. |
| 6 | **Blob shadows** via the existing `PokeLabDecal` | **0.5 day** | Grounding. |
| 7 | **Split-normals pass** on Terrain family | **1-2 days** | Rock and cliff stop reading as mush under flat textures. |
| 8 | **Battle camera rework** — cut 3 shots, reframe 2, retune 2, disable deoccluder on creature shots | **8-12 days** | The risky one. Do it last, with sprites already in hand to test against. |

**Environment and integration total: ~6-9 days.** Camera: 8-12 days on top. Creature and human
sprite authoring is separate and out of scope here.

---

## 8. Risks this decision carries

Recorded so they are visible, not to reopen the decision.

1. **The battle camera loses its three best shots.** Both over-the-shoulders and the impact
   punch-in are cut. Those are the shots that made the rig worth building. The battle will read
   closer to Gen 5's camera than to what `BattleCameraRig` was designed to deliver, and that is a
   real reduction in the product's differentiator.
2. **The 12-16× subject-size range does not go away, it gets constrained away.** Every retuned
   shot fraction in 3.1 is a reduction in how close the camera may get. Battle intimacy is the
   currency being spent.
3. **Sprite direction changes are cuts.** The project's non-negotiable is "no abrupt camera cuts;
   every transition is authored". A direction index flip is a small cut fired by geometry rather
   than intent. Hysteresis (3.2) mitigates it; nothing removes it.
4. **The seamless battle transition now has an art-identity seam** wherever a sprite creature and
   a 3D level element must cross-fade. `TransitionDirector` blends; a 2D↔3D identity change does
   not blend. Budget for hiding it behind `ScreenTransitionOverlay`.
5. **The reference project does not de-risk any of this.** NIONX's UE5 Kanto — the HD-2D piece
   this pivot is modelled on — used **community-ripped Gen 5 sprites** and **has no battle
   system**, no dialogue, and is a non-downloadable portfolio walkthrough. Its author's real
   achievement is the environment and lighting, learned from scratch in Blender. That half is
   genuinely transferable and section 7 is how to capture it. The battle-camera half is a problem
   that project never had to solve, so nothing in it is evidence that ours is solvable.

**The strongest single mitigation is ordering.** Items 1-4 in section 7 cost under three days,
touch no sprites, and are reversible. Ship them first and the pivot's visual thesis is testable
before any irreversible art or camera work begins.

---

## Sources

- [HD-2D — Wikipedia](https://en.wikipedia.org/wiki/HD-2D)
- [Triangle Strategy devs on how the game uses "accurate" HD-2D — Nintendo Everything](https://nintendoeverything.com/triangle-strategy-devs-on-how-the-game-uses-accurate-hd-2d/)
- [Perfecting Unity's Billboard Shader for HD-2D — TW0CATS Games](https://tw0catsgames.com/update/2023/11/06/perfecting-unitys_billboard_shader_for_hd2d_01.html)
- [How do games like Octopath Traveler handle the angle of shadows? — Unity Discussions](https://discussions.unity.com/t/how-do-games-like-octopath-traveler-handle-the-angle-of-shadows-and-is-this-even-possible-in-unity-urp/246459)
- [Fix shadows for billboarded Sprite3D — Godot PR #72638](https://github.com/godotengine/godot/pull/72638)
- [Unity Issue Tracker — URP/2D normal maps flipped when sprite scale is flipped](https://issuetracker.unity3d.com/issues/urp-2d-normals-maps-are-flipped-when-the-sprites-scale-is-flipped-from-slash-to-negative)
- [Unity Issue Tracker — negative scale transforms and backface culling](https://issuetracker.unity3d.com/issues/odd-number-of-negative-scales-causes-normals-of-mesh-drawn-using-graphics-dot-drawnmesh-to-flip)
- [ShaderLab command: Offset — Unity Manual](https://docs.unity3d.com/Manual//SL-Offset.html)
- [2.5D Games in Unity, Part 2 — Depth Shader — NotSlot](https://notslot.com/tutorials/2019/12/25d-game-in-unity-part-2)
- [Unity Scripting API — BillboardAsset](https://docs.unity3d.com/ScriptReference/BillboardAsset.html)
- [Introduction to the Pixel Perfect Camera in URP — Unity Manual](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/2d-pixelperfect.html)
- [Pokémon HD-2D in Unreal Engine 5 — Calm Walk Through Kanto (NIONX)](https://www.youtube.com/watch?v=aaK79jhjkgM)
- [This Pokemon HD-2D Remake in Unreal Engine 5 Looks So Cool — DSOGaming](https://www.dsogaming.com/videotrailer-news/this-pokemon-hd-2d-remake-in-unreal-engine-5-looks-so-cool/)
- [Pokémon's HD-2D Dream is One Fan's Unreal Engine 5 Kanto — TechEBlog](https://www.techeblog.com/pokemon-hd-2d-unreal-engine-5-black-and-white-remake/)
