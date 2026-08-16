#ifndef POKELAB_COMMON_INCLUDED
#define POKELAB_COMMON_INCLUDED

// -----------------------------------------------------------------------------
// Poke Lab shared shader library.
//
// One file so every surface in the game speaks the same lighting and colour
// language: the same toon ramp, the same art-directed ambient gradient, the same
// dither, the same wind. Cohesion is the brief, and cohesion comes from sharing
// code rather than from matching values by eye across a dozen shaders.
//
// Every _PL_ global below is written by LightingDirector (PokeLab.Vfx). They all
// default to zero on purpose: a scene where the director has not booted falls back
// to plain Unity ambient and no wind rather than to black.
//
// Requires Core.hlsl to be included first. Everything else it needs it pulls in
// itself, deliberately: this file is included from shadow and depth passes as well
// as from lit ones, and those never include Lighting.hlsl.
// -----------------------------------------------------------------------------

// SampleSH lives here rather than in Lighting.hlsl. Including it directly is what
// lets PL_Ambient be defined in a ShadowCaster pass without dragging the whole
// lighting library in behind it. The header is include-guarded, so a lit pass that
// also includes Lighting.hlsl pays nothing.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"

// --- Director-driven globals (never per material, never inside UnityPerMaterial) --
float4 _PL_SkyColor;         // rgb: zenith ambient
float4 _PL_EquatorColor;     // rgb: horizon ambient
float4 _PL_GroundColor;      // rgb: bounce ambient
float  _PL_AmbientBlend;     // 0 = engine SH, 1 = the gradient above
float  _PL_AmbientBoost;     // additive multiplier, 0 = neutral
float4 _PL_RimTint;          // rgb: shared rim colour, a: unused
float  _PL_RimBoost;         // additive rim intensity, 0 = neutral
float4 _PL_WindParams;       // xy: normalised wind dir (XZ), z: strength, w: gust phase speed
float  _PL_WindGust;         // 0..1 extra amplitude during a gust event
float  _PL_Wetness;          // 0..1 global surface wetness (rain)
float4 _PL_SunColor;         // rgb: stylised sun tint used by sky and water sparkle
float  _PL_NightFactor;      // 0 by day, 1 at deep night. Drives dither strength.

// Things that push foliage aside. xyz: world position, w: radius in metres.
// Velocity is carried separately so grass can lean the way the walker is going
// rather than only radially outward, which is what makes it read as being pushed
// through rather than repelled by a force field.
#define PL_MAX_INTERACTORS 8
float4 _PL_Interactors[PL_MAX_INTERACTORS];
float4 _PL_InteractorVel[PL_MAX_INTERACTORS];   // xyz: velocity m/s, w: unused
float  _PL_InteractorCount;

// -----------------------------------------------------------------------------
// Dither. The QA brief calls out banding, so every gradient in this project runs
// through PL_Dither before it reaches an 8-bit buffer. Interleaved gradient noise
// is cheap, temporally stable under a static camera and does not tile visibly.
// -----------------------------------------------------------------------------
float PL_IGN(float2 pixel)
{
    const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixel, magic.xy)));
}

// amount is expressed in final colour units. 1/255 removes 8-bit banding,
// 2/255 is a good default for wide dark gradients such as the night sky.
float PL_Dither(float2 svPosition, float amount)
{
    return (PL_IGN(svPosition) - 0.5) * amount;
}

// Night grades quantise hardest, so scale the dither with _PL_NightFactor.
float PL_AdaptiveDither(float2 svPosition, float baseAmount)
{
    return PL_Dither(svPosition, baseAmount * (1.0 + 2.0 * saturate(_PL_NightFactor)));
}

// -----------------------------------------------------------------------------
// Hash and noise. Deliberately small: these run per pixel on foliage and water.
// -----------------------------------------------------------------------------
float PL_Hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    return frac(p * (p + p));
}

float PL_Hash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float PL_ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = PL_Hash21(i);
    float b = PL_Hash21(i + float2(1.0, 0.0));
    float c = PL_Hash21(i + float2(0.0, 1.0));
    float d = PL_Hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Four fixed octaves. Unrolled by hand so no compiler has an excuse to keep a loop.
float PL_Fbm(float2 p)
{
    float v = 0.0;
    v += 0.5000 * PL_ValueNoise(p);
    v += 0.2500 * PL_ValueNoise(p * 2.03 + 17.3);
    v += 0.1250 * PL_ValueNoise(p * 4.01 + 31.7);
    v += 0.0625 * PL_ValueNoise(p * 8.05 + 53.1);
    return v / 0.9375;
}

// A stable per-instance random from the object-to-world translation. Two objects
// at the same position get the same value, which is exactly what we want for
// batched foliage: variation follows placement, not draw order.
float PL_InstanceSeed()
{
    float3 t = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
    return PL_Hash21(t.xz + t.y * 0.37);
}

// -----------------------------------------------------------------------------
// Toon ramp.
//
// Quantises a lambert term into `bands` steps. The band edge is widened to at
// least one pixel of screen-space derivative so the terminator antialiases
// instead of crawling, which is the usual tell of a cheap toon shader.
//
// The derivative is taken once per fragment by PL_ToonRampWidth and passed in,
// rather than being read inside the ramp itself. That is not only cheaper: the
// ramp is called from inside URP's clustered light loop, and gradient
// instructions inside a loop that can break are undefined and can fail to compile
// outright on some backends.
// -----------------------------------------------------------------------------

/// Screen-space band width for a surface. Call once per fragment, outside any loop.
float PL_ToonRampWidth(float3 normalWS, float bands)
{
    // How far the shading normal turns across one pixel, scaled by how many bands
    // that turn has to cross. This is what actually drives terminator aliasing, and
    // it is the same for every light, so one read serves them all.
    return length(fwidth(normalWS)) * max(1.0, bands) + 1e-4;
}

float PL_ToonRamp(float ndotl, float bands, float softness, float wrap,
                  float2 svPosition, float edgeWidth)
{
    float w = lerp(saturate(ndotl), ndotl * 0.5 + 0.5, saturate(wrap));
    float b = max(1.0, bands);
    float scaled = w * b;

    float edge = max(softness, edgeWidth * 1.25);

    float lo = floor(scaled);
    float fr = frac(scaled);
    // Dither the fractional position rather than the result: it breaks the band
    // edge into noise instead of shifting the whole band's brightness.
    fr += PL_Dither(svPosition, edge * 0.5);
    float step01 = smoothstep(0.5 - edge * 0.5, 0.5 + edge * 0.5, fr);

    return saturate((lo + step01) / b);
}

// -----------------------------------------------------------------------------
// Art-directed ambient.
//
// Blends the engine's spherical harmonics with a three-colour sky/equator/ground
// gradient owned by LightingDirector. _PL_AmbientBlend = 0 means "use whatever
// Unity computed", so this is safe in an unlit test scene.
// Requires Lighting.hlsl for SampleSH.
// -----------------------------------------------------------------------------
half3 PL_Ambient(float3 normalWS)
{
    half3 sh = SampleSH(normalWS);

    float up = normalWS.y;
    half3 above = lerp(_PL_EquatorColor.rgb, _PL_SkyColor.rgb, saturate(up));
    half3 below = lerp(_PL_EquatorColor.rgb, _PL_GroundColor.rgb, saturate(-up));
    half3 grad = lerp(below, above, step(0.0, up));

    half3 amb = lerp(sh, grad, saturate(_PL_AmbientBlend));
    return amb * (1.0 + _PL_AmbientBoost);
}

// -----------------------------------------------------------------------------
// Rim. Shared so creatures, props and foliage all catch light at the same angle.
// -----------------------------------------------------------------------------
half PL_Rim(float3 normalWS, float3 viewDirWS, float power, float threshold)
{
    half f = 1.0 - saturate(dot(normalWS, viewDirWS));
    f = pow(max(f, 1e-4), max(power, 0.01));
    return smoothstep(threshold, 1.0, f);
}

// -----------------------------------------------------------------------------
// Triplanar. `sharpness` around 4 gives a tight blend without visible seams.
// -----------------------------------------------------------------------------
float3 PL_TriplanarWeights(float3 normalWS, float sharpness)
{
    float3 w = pow(abs(normalWS), max(sharpness, 1.0));
    return w / max(1e-4, w.x + w.y + w.z);
}

#define PL_TRIPLANAR_SAMPLE(tex, samp, posWS, w, scale) ( \
    SAMPLE_TEXTURE2D(tex, samp, (posWS).zy * (scale)) * (w).x + \
    SAMPLE_TEXTURE2D(tex, samp, (posWS).xz * (scale)) * (w).y + \
    SAMPLE_TEXTURE2D(tex, samp, (posWS).xy * (scale)) * (w).z )

#define PL_TRIPLANAR_SAMPLE_LOD(tex, samp, posWS, w, scale, lod) ( \
    SAMPLE_TEXTURE2D_LOD(tex, samp, (posWS).zy * (scale), lod) * (w).x + \
    SAMPLE_TEXTURE2D_LOD(tex, samp, (posWS).xz * (scale), lod) * (w).y + \
    SAMPLE_TEXTURE2D_LOD(tex, samp, (posWS).xy * (scale), lod) * (w).z )

// -----------------------------------------------------------------------------
// Height blending. Splat weights that respect a height map read far better than
// a linear lerp: gravel pokes through grass instead of dissolving into it.
// -----------------------------------------------------------------------------
float2 PL_HeightBlend2(float w0, float h0, float w1, float h1, float contrast)
{
    float d = max(contrast, 1e-3);
    float m0 = w0 * (h0 + 1.0);
    float m1 = w1 * (h1 + 1.0);
    float peak = max(m0, m1) - d;
    float b0 = max(m0 - peak, 0.0);
    float b1 = max(m1 - peak, 0.0);
    float sum = max(b0 + b1, 1e-5);
    return float2(b0, b1) / sum;
}

float4 PL_HeightBlend4(float4 weights, float4 heights, float contrast)
{
    float d = max(contrast, 1e-3);
    float4 m = weights * (heights + 1.0);
    float peak = max(max(m.x, m.y), max(m.z, m.w)) - d;
    float4 b = max(m - peak, 0.0);
    return b / max(dot(b, 1.0), 1e-5);
}

// -----------------------------------------------------------------------------
// Wetness. One function so puddles by the lake, rain on the route and a damp cave
// floor all darken and gloss identically.
// -----------------------------------------------------------------------------
void PL_ApplyWetness(inout half3 albedo, inout half smoothness, inout half3 normalWS,
                     half3 geoNormalWS, half wet)
{
    wet = saturate(wet);
    albedo *= lerp(1.0, 0.55, wet);
    smoothness = lerp(smoothness, 0.92, wet);
    // Water fills the microrelief, so the surface flattens towards the geometric normal.
    normalWS = normalize(lerp(normalWS, geoNormalWS, wet * 0.8));
}

// -----------------------------------------------------------------------------
// Foliage wind.
//
// Vertex-colour convention agreed with the environment artist:
//   R = sway mask   (0 at the anchored base, 1 at the free tip)
//   G = phase offset (per-leaf-cluster, decorrelates neighbouring cards)
//
// Three layers: a slow trunk-scale sway, a gust that travels across the world,
// and a fast flutter confined to the tips. Amplitude fades with view distance so
// distant foliage stops shimmering under minification.
// -----------------------------------------------------------------------------
float3 PL_FoliageWind(float3 positionWS, float4 vertexColour,
                      float swayAmplitude, float swaySpeed,
                      float flutterAmplitude, float flutterSpeed,
                      float gustStrength, float distanceFade)
{
    float mask = vertexColour.r;
    if (mask <= 0.001) return float3(0.0, 0.0, 0.0);

    float2 dir = _PL_WindParams.xy;
    // Fall back to a fixed diagonal when the director has not written a direction,
    // so foliage still moves in an isolated test scene.
    float dirLen = length(dir);
    dir = (dirLen > 1e-3) ? dir / dirLen : float2(0.7071, 0.7071);

    float strength = 1.0 + _PL_WindParams.z;
    float t = _Time.y;

    float phase = vertexColour.g * 6.2831853 + PL_InstanceSeed() * 6.2831853;
    float travel = dot(positionWS.xz, dir) * 0.12;

    // Layer 1: broad sway. Low frequency, moves the whole card.
    float sway = sin(t * swaySpeed + travel + phase);

    // Layer 2: gust. A slow noise field scrolling downwind; squared so calm is the
    // resting state and gusts read as events rather than as a constant wobble.
    float gustField = PL_ValueNoise(positionWS.xz * 0.035 - dir * t * 0.35);
    float gust = gustField * gustField * (gustStrength + _PL_WindGust);

    // Layer 3: flutter. Tip-only, higher frequency, cubed mask so it never reaches
    // the base of the plant.
    float tip = mask * mask * mask;
    float flutter = sin(t * flutterSpeed + phase * 3.1 + travel * 4.0) * tip;

    float amp = (sway * swayAmplitude * (1.0 + gust) + flutter * flutterAmplitude) * mask * strength;

    // Distance fade. Beyond distanceFade metres the motion is sub-pixel anyway and
    // only produces aliasing, so it is cheaper and cleaner to stop moving.
    float viewDist = distance(positionWS, _WorldSpaceCameraPos);
    float fade = 1.0 - saturate((viewDist - distanceFade) / max(distanceFade, 1.0));

    float3 offset = float3(dir.x, 0.0, dir.y) * amp;
    // A little vertical bob keeps the sway from looking like a pure shear.
    offset.y -= abs(amp) * 0.25;

    return offset * fade;
}


// -----------------------------------------------------------------------------
// Foliage pushed aside by something moving through it.
//
// Uses the same sway mask as the wind (vertex colour R), so a plant bends from
// its anchored base rather than sliding bodily sideways, and so anything the
// generator marked rigid — bark — is untouched by a walker as well as by wind.
//
// Two components, because either alone reads wrong. Radial-only looks like a
// force field pushing the player away; along-velocity only makes the grass lean
// before the player arrives and snap back oddly behind them. Together the blades
// splay outward *and* trail in the direction of travel, which is what walking
// through long grass actually looks like.
//
// The falloff is squared so the disturbance stays local: a wide soft gradient
// reads as the whole field breathing whenever the player moves.
// -----------------------------------------------------------------------------
float3 PL_FoliageInteraction(float3 positionWS, float4 vertexColour,
                             float strength, float flutter)
{
    float mask = vertexColour.r;
    if (mask <= 0.001) return float3(0.0, 0.0, 0.0);

    float3 total = float3(0.0, 0.0, 0.0);
    int count = min((int)_PL_InteractorCount, PL_MAX_INTERACTORS);

    for (int i = 0; i < count; i++)
    {
        float4 it = _PL_Interactors[i];
        float radius = max(it.w, 0.01);

        // Measured in the ground plane. Including height would make grass ignore a
        // walker on a slope above or below it, and the walker's feet are what
        // matters anyway.
        float2 away = positionWS.xz - it.xyz.xz;
        float dist = length(away);
        if (dist >= radius) continue;

        float falloff = 1.0 - dist / radius;
        falloff *= falloff;

        float2 radial = (dist > 1e-4) ? away / dist : float2(1.0, 0.0);
        float3 vel = _PL_InteractorVel[i].xyz;
        float speed = length(vel.xz);
        float2 travel = (speed > 1e-3) ? vel.xz / speed : float2(0.0, 0.0);

        // Leaning with the walker scales with how fast they are going; the radial
        // splay does not, so grass still parts around someone standing still in it.
        float2 dir = radial + travel * saturate(speed / 3.0) * 1.15;
        float len = length(dir);
        dir = (len > 1e-4) ? dir / len : radial;

        float bend = falloff * strength * mask;

        // Tips only, and only while something is actually moving: the flick of
        // blades springing past each other as you pass. Cubed mask keeps it off
        // the base, matching the wind's flutter layer.
        float tip = mask * mask * mask;
        float rattle = sin(_Time.y * 26.0 + positionWS.x * 5.3 + positionWS.z * 4.1)
                     * flutter * tip * falloff * saturate(speed / 2.0);

        total.xz += dir * bend + radial * rattle;
        // Trodden down as well as aside, or fast movement looks like the grass is
        // being blown rather than walked through.
        total.y -= bend * 0.35;
    }

    return total;
}

// -----------------------------------------------------------------------------
// Soft-particle depth fade, shared by every transparent VFX shader so smoke,
// water foam and impact flashes all feather against geometry by the same rule.
//
// Only compiled where the shader has already included DeclareDepthTexture.hlsl;
// callers signal that by defining PL_HAS_SCENE_DEPTH before including this file.
// screenUV must be the perspective-divided screen position (0..1).
// -----------------------------------------------------------------------------
#if defined(PL_HAS_SCENE_DEPTH)
float PL_SceneEyeDepth(float2 screenUV)
{
    float raw = SampleSceneDepth(screenUV);
    #if UNITY_REVERSED_Z
        return LinearEyeDepth(raw, _ZBufferParams);
    #else
        return LinearEyeDepth(lerp(UNITY_NEAR_CLIP_VALUE, 1.0, raw), _ZBufferParams);
    #endif
}

// Returns 0 where the surface is coincident with scene geometry, 1 when it is at
// least fadeDistance metres in front of it.
float PL_SoftFade(float2 screenUV, float fragmentEyeDepth, float fadeDistance)
{
    float sceneDepth = PL_SceneEyeDepth(screenUV);
    return saturate((sceneDepth - fragmentEyeDepth) / max(fadeDistance, 1e-3));
}
#endif

// -----------------------------------------------------------------------------
// Small colour helpers, used to keep VFX tinting consistent with the grade.
// -----------------------------------------------------------------------------
half3 PL_Saturate(half3 c, half amount)
{
    half l = dot(c, half3(0.2126, 0.7152, 0.0722));
    return lerp(half3(l, l, l), c, amount);
}

half PL_Luma(half3 c)
{
    return dot(c, half3(0.2126, 0.7152, 0.0722));
}

// Remaps 0..1 through a smooth S so hand-tuned masks stay controllable.
float PL_Contrast(float x, float amount)
{
    return saturate(lerp(x, smoothstep(0.0, 1.0, x), saturate(amount)));
}

#endif // POKELAB_COMMON_INCLUDED
