# M_Ground_TerrainBlend

Why the values in `M_Ground_TerrainBlend.mat` are what they are.

These notes lived as `#` comments inside the .mat itself until Unity refused to
parse the file: its YAML reader does not accept comments, and the material — with
every material that shares its shader — silently failed to load. They are here
instead because the reasoning is not decoration. Several of these numbers are tied
to the feature sizes the textures were authored at, so changing one without
rebuilding its map changes the size of everything in it.

## `m_TexEnvs`

Current: `m_TexEnvs:`

Four individually seamless tiling maps, built by
Tools/Blender/environment/terrain_layers.py. They are deliberately not atlas
cells: these layers sample an unbounded wrapping UV (planar world XZ for 0-2,
triplanar for 3), so an atlas cell's neighbours would bleed in on the first
repeat. Each BaseColor carries its height map in alpha, which is what
_HeightContrast below blends against; regenerate the pair together or the
blend silently loses its height.

## `_Layer0Scale`

Current: `- _Layer0Scale: 0.35`

Tiles per metre, so a tile of grass spans 2.86 m, dirt 2.0 m, sand 1.67 m and
rock 3.85 m. Each map was authored against its own figure -- grass tufts at
15 cm, gravel at 1-2 cm, sand ripples at 18 cm, rock slabs at 0.95 x 0.55 m --
so these are no longer free parameters, and changing one without rebuilding
its map changes the size of everything in it.

## `_Layer3Scale`

Current: `- _Layer3Scale: 0.15`

0.28 was a guess made against a white texture; 0.26 was the first real
figure and it read as a grid on the cliffs. 0.15 puts the tile at 6.67 m,
which is three repeats up a 20 m face instead of five, and less than one
whole tile on a 4 m retaining cliff -- no repeat there at all. The rock map
is 2048 rather than 1024 to pay for the larger tile, so texel density on a
cliff face is higher than it was before, not lower.

This only reduces the grid. It does not remove it, and it cannot: the
anti-tiling term below is PL_Fbm(positionWS.xz), which is constant along a
vertical, so it contributes nothing on a cliff face. See the note on
_MacroVariation.

## `_SlopeStart`

Current: `- _SlopeStart: 30`

Rock takes over between these two angles. The slice's ground runs to 85 deg in
places, and this is what puts stone on those faces without anyone painting them.

## `_MacroVariation`

Current: `- _MacroVariation: 0.55`

The shader's only anti-tiling term, and it is two dimensional: the
fragment shader evaluates PL_Fbm(positionWS.xz), and PL_Fbm takes a float2.
Every point on a vertical line therefore shares one value, so on a cliff
face this breaks up nothing at all -- and the rock layer is the one that is
sampled triplanar and repeats several times up a single wall. Raising these
numbers cannot fix that; it only makes the horizontal drift stronger. The
fix is a three dimensional macro in the shader.

## `_WaterLevel`

Current: `- _WaterLevel: -2.2`

The lake surface, so the shore damp band lands at the actual waterline.
