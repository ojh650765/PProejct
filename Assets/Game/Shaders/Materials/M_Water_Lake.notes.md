# M_Water_Lake

Why the values in `M_Water_Lake.mat` are what they are.

These notes lived as `#` comments inside the .mat itself until Unity refused to
parse the file: its YAML reader does not accept comments, and the material — with
every material that shares its shader — silently failed to load. They are here
instead because the reasoning is not decoration. Several of these numbers are tied
to the feature sizes the textures were authored at, so changing one without
rebuilding its map changes the size of everything in it.

## `m_TexEnvs`

Current: `m_TexEnvs: []`

No normal map is assigned, so the shader falls back to a flat "bump" and the
surface detail comes entirely from the procedural layers below. That is why
their scales matter so much here and why they were reading as a pattern
rather than as water.

## `_FoamDepth`

Current: `- _FoamDepth: 0.35`

Foam sized between the two failures. The shader default of 2.2 is a cell under
half a metre, which repeats ~70 times across a 32 m lake and reads as a white
lattice; dropping it to 0.42 overshot the other way into 2.4 m cotton-wool
blobs floating in open water. 1.1 puts the cell near 0.9 m. Crest foam is
nearly off because foam belongs at the shore, and the crest term was what put
it out in the middle.

## `_CausticStrength`

Current: `- _CausticStrength: 0.45`

Same correction. 1.6 gives a 0.6 m cell that tiles ~50 times across the lake
and weaves into a grid; 0.3 gave 3.3 m and turned the pattern into large soft
islands. 0.85 lands near 1.2 m, and the strength is down as well because these
only belong in water shallow enough to see the bed through.

## `_SparkleScale`

Current: `- _SparkleScale: 7`

22 is a sub-texel frequency at this camera distance and produces crawling
aliasing rather than glitter.
