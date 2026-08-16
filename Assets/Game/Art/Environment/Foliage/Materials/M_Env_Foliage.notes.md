# M_Env_Foliage

Why the values in `M_Env_Foliage.mat` are what they are.

These notes lived as `#` comments inside the .mat itself until Unity refused to
parse the file: its YAML reader does not accept comments, and the material — with
every material that shares its shader — silently failed to load. They are here
instead because the reasoning is not decoration. Several of these numbers are tied
to the feature sizes the textures were authored at, so changing one without
rebuilding its map changes the size of everything in it.

## `_GradientPower`

Current: `- _GradientPower: 1`

Gradient and per-instance variation are kept near neutral, because this one
material shades the whole family -- bark, canopies, ferns and grass all come off
the same atlas. The shader multiplies albedo by lerp(_RootColor, _TipColor,
vertexColour.r), and vertexColour.r is the *sway mask*, which is 0 at any
anchored base. With the shader's authored default of (0.16, 0.32, 0.18) that
multiplied every tree trunk by dark green, which is exactly what it looked like.
The atlas already carries the real colours; the ramp's job here is a little
occlusion toward the ground, not hue.

## `_Cutoff`

Current: `- _Cutoff: 0.45`

The atlas is an RGB PNG with no alpha channel, so sampled alpha is always 1
and the clip is a no-op. Cutoff is left at its default rather than raised,
because raising it on alpha-free art would clip the entire canopy away the
moment someone exports an atlas that does carry alpha.

## `_AlphaToMask`

Current: `- _AlphaToMask: 0`

Off deliberately. Alpha-to-coverage costs an MSAA resolve and dithers edges
for a coverage value that is constant 1 on this art; it earns its keep only
on real cutout foliage.

## `_TranslucencyStrength`

Current: `- _TranslucencyStrength: 1.05`

Backlit-leaf glow, pulled back from the shader default: it multiplies albedo, so
on bark it reads as a dark green wash rather than as translucency.

## `_SwayAmplitude`

Current: `- _SwayAmplitude: 0.24`

Wind, tuned for grass that visibly moves rather than trembles. The three layers
are already the right shape -- a broad sway, a squared noise gust scrolling
downwind so calm is the resting state and a gust reads as a wave crossing the
field, and a tip-only flutter -- so this is amplitude, not new machinery.
Gust is the layer that carries the look and is raised the most.

## `_WindDistanceFade`

Current: `- _WindDistanceFade: 70`

Matched to InstancedFoliage's 70 m draw distance. Leaving it at 45 froze the
grass over the last 25 m of its own draw range, which reads as a band of dead
grass tracking the camera.
