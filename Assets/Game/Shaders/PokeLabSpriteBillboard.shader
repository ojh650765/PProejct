// -----------------------------------------------------------------------------
// PokeLab/SpriteBillboard
//
// The HD-2D integration shader: a quad carrying a pixel sprite, rendered so that
// it sits *inside* the 3D level rather than on top of it. Forked from
// PokeLab/Creature, which keeps the toon ramp, the ambient gradient, the rim and
// the battle overrides identical between the two, so a 3D actor and a sprite
// actor in the same frame band their light the same way.
//
// What is different from PokeLab/Creature, and why:
//
//   Billboard to the camera in UniversalForward, to the LIGHT in ShadowCaster.
//       A camera-facing quad casts a camera-facing silhouette, so the shadow goes
//       razor-thin and visibly vanishes as the camera orbits perpendicular to the
//       key. Yawing the shadow-caster geometry to the light instead keeps the
//       shadow the full width of the creature from every camera angle. This is the
//       single most-reported HD-2D billboard bug.
//
//   Synthetic sphere normals.
//       A flat quad has one constant world normal, so NdotL is uniform: the sprite
//       either fully lights or fully shades and reads as a sticker. Deriving
//       n.xy = uv*2-1 and n.z from them, then rotating into the billboard basis,
//       makes a flat sprite take the moving directional key like a rounded volume.
//       Costs nothing and is the biggest single "not pasted on" win.
//
//   Horizontal flip through the UV, never through negative X scale.
//       An odd number of negative scale axes mirrors the object into left-handed
//       space: winding inverts and normal-map interpretation flips. That is a
//       long-standing, still-open Unity behaviour. Under LightingDirector's moving
//       key a negative-scale sprite would re-light backwards the instant it turned
//       around, which is more obviously broken than no lighting at all.
//       _FlipX mirrors the sampled UV only. Geometry, winding and the synthetic
//       normal all stay in right-handed space.
//
//   Opaque queue, ZWrite On, alpha clip. Not the transparent queue.
//       Transparent objects sort per-object by camera distance and would pop in
//       front of and behind foliage cards unpredictably. Opaque + clip gives
//       correct per-pixel depth against all geometry for free, and it keeps
//       MSAA 4x useful: MSAA antialiases geometry silhouettes, not texture
//       interiors, so it costs the pixel look nothing.
//
//   Ground contact without z-fighting.
//       Three mechanisms, in the order the plan ranks them:
//         1. the quad tilts back by the camera's elevation (_PitchTilt), so its
//            bottom edge sits nearer the lens than its top;
//         2. _DepthSlope pushes the top edge further away in metres, which writes
//            a genuinely sloped depth buffer -- feet nearer than head -- so
//            sprite-vs-sprite and sprite-vs-prop sorting is right too, not just
//            sprite-vs-ground. This is geometric, so ForwardLit, DepthOnly,
//            DepthNormals and the depth prepass all agree by construction. No
//            SV_Depth is written anywhere: that would kill early-Z and would have
//            to be replicated bit-identically across four passes.
//         3. polygon offset, written as a literal `Offset 0, 0` in the three
//            depth-writing passes. Last resort only, and it must be changed in
//            all three at once or the prepass stops matching the colour pass.
//
//   Contact darkening, ported from PokeLabPropGroundBlend.
//       Its contact blend ("the lowest _BlendHeight metres tint toward the terrain
//       colour so a boulder sinks into the grass instead of showing a hard
//       intersection line") is exactly the mechanism a sprite needs at its base.
//       Here the height is a fraction of sprite height rather than metres, because
//       the sprite's own UV is the natural coordinate. Pair it with a
//       PokeLab/VFX/Decal blob under the feet: a box-projected decal conforms to
//       real terrain, which a flat quad never can.
//
//   No outline pass.
//       PokeLab/Creature's inverted-hull outline on a quad draws a rectangle
//       border. The keyline belongs in the sprite art -- bake 1 px in
//       _OutlineColor (0.08, 0.07, 0.12) so sprites and props share one line
//       language.
//
//   Fog is not optional.
//       multi_compile_fog + ComputeFogFactor + MixFog, exactly as
//       PokeLab/Creature does. Skipping fog is the number-one cause of sprites
//       looking pasted on, and LightingDirector.ApplyAmbientAndFog drives real
//       exponential-squared fog per grade.
//
// Mesh requirements
//   A single quad. UV0 must span 0..1 across it: UV0 drives the synthetic sphere
//   normal, the contact gradient and the depth slope, all of which are geometric
//   and must not follow the atlas cell. Object rotation is ignored (that is what
//   billboarding means); object scale on X and Y is honoured. Author the quad
//   with its pivot at the feet, per the art contract, or dial _VerticalOffset.
//
// Texture import settings (these are importer settings, not shader state, and the
// shader cannot enforce them -- see Assets/Game/Scripts/VFX/Editor/
// PokeLabTextureImportRules.cs, which applies them automatically by folder):
//   Filter Mode   Point. Point is the pixel look.
//   Mip Maps      ON. Mandatory. The wide establishing shot minifies a 256 px
//                 sprite to roughly 0.3x; without mips it shimmers.
//   Aniso         1 (a sprite is always seen face-on).
//   Alpha Is Transparency  ON, so the transparent border does not bleed dark.
//   Alpha-to-coverage  OFF for sprites. It would soften the intended hard pixel
//                 edge. Foliage wants the opposite; the importer rules encode it.
//   Compression   None or high-quality; block compression eats a 1 px keyline.
//   Atlas padding At least 8 px of gutter per cell if _AtlasColumns/_AtlasRows are
//                 used, or mip 2+ bleeds neighbouring directions into the frame.
// -----------------------------------------------------------------------------
Shader "PokeLab/SpriteBillboard"
{
    Properties
    {
        [MainTexture] _BaseMap("Sprite Atlas", 2D) = "white" {}
        [MainColor]   _BaseColor("Tint", Color) = (1,1,1,1)

        [Header(Sprite Frame)][Space(4)]
        _AtlasColumns("Atlas Columns", Float) = 1
        _AtlasRows("Atlas Rows", Float) = 1
        _AtlasIndex("Atlas Index (direction or frame)", Float) = 0
        // ToggleUI, not Toggle: this must never become a shader keyword. Flipping
        // is a per-instance value driven every frame from the facing solve, and a
        // keyword flip would be a variant change per creature per frame.
        [ToggleUI] _FlipX("Flip Horizontally", Float) = 0

        [Header(Billboard)][Space(4)]
        _PitchTilt("Tilt Back By Camera Pitch", Range(0,1)) = 0.35
        _DepthSlope("Depth Slope (m, head pushed back)", Range(0,1)) = 0.12
        _DepthBias("Depth Bias Toward Camera (m)", Range(0,0.2)) = 0.01
        _VerticalOffset("Vertical Offset (m)", Float) = 0

        [Header(Synthetic Normals)][Space(4)]
        _SphereNormal("Sphere Normal Amount", Range(0,1)) = 0.75
        _NormalCurvature("Sphere Normal Curvature", Range(0.1,1.5)) = 0.85
        _SelfShadowOffset("Self Shadow Escape (m)", Range(0,2)) = 0.35

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.42,0.47,0.66,1)
        // Must match every other PokeLab shader exactly: 3 bands, 0.02 edge.
        _ShadeSteps("Band Count", Range(1,6)) = 3
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.02
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.3
        _ShadowStrength("Cast Shadow Depth", Range(0,1)) = 0.7
        _AmbientOcclusion("Base Occlusion", Range(0,1)) = 0.3

        [Header(Rim)][Space(4)]
        _RimColor("Rim Colour", Color) = (1,0.95,0.85,1)
        _RimPower("Rim Power", Range(0.5,12)) = 3
        _RimThreshold("Rim Threshold", Range(0,1)) = 0.3
        _RimStrength("Rim Strength", Range(0,3)) = 0.5
        _RimLightAlign("Rim Follows Light", Range(0,1)) = 0.6

        [Header(Ground Contact)][Space(4)]
        _ContactColor("Contact Colour", Color) = (0.30,0.34,0.24,1)
        _ContactHeight("Contact Height (fraction of sprite)", Range(0,0.5)) = 0.08
        _ContactPower("Contact Falloff", Range(0.2,6)) = 2
        _ContactStrength("Contact Strength", Range(0,1)) = 0.55
        _ContactNoiseScale("Contact Noise Scale", Float) = 6
        _ContactNoiseAmount("Contact Noise", Range(0,1)) = 0.35

        [Header(Emission)][Space(4)]
        [HDR] _EmissionColor("Emission Colour", Color) = (0,0,0,1)
        _EmissionPulse("Emission Pulse Speed", Range(0,8)) = 0

        [Header(Battle Overrides)][Space(4)]
        [HDR] _FlashColor("Flash Colour", Color) = (1,1,1,1)
        _FlashAmount("Flash Amount", Range(0,1)) = 0
        _DissolveAmount("Dissolve Amount", Range(0,1)) = 0
        [HDR] _DissolveEdgeColor("Dissolve Edge Colour", Color) = (1.6,0.9,0.25,1)
        _DissolveEdgeWidth("Dissolve Edge Width", Range(0.001,0.35)) = 0.08
        _DissolveScale("Dissolve Noise Scale", Float) = 9

        [Header(Surface)][Space(4)]
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            // TransparentCutout + AlphaTest queue, matching PokeLab/Foliage. This
            // is still the opaque half of the frame -- front to back, ZWrite on,
            // drawn before anything in the transparent queue -- which is the whole
            // point: per-object transparent sorting would pop sprites through
            // foliage cards.
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
        }
        LOD 300

        // ---------------------------------------------------------------------
        // Shared declarations. Every pass includes this so the UnityPerMaterial
        // layout is byte-identical, which is what the SRP batcher requires.
        // ---------------------------------------------------------------------
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            float  _AtlasColumns;
            float  _AtlasRows;
            float  _AtlasIndex;
            half   _FlipX;
            half   _PitchTilt;
            float  _DepthSlope;
            float  _DepthBias;
            float  _VerticalOffset;
            half   _SphereNormal;
            half   _NormalCurvature;
            float  _SelfShadowOffset;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half   _ShadowStrength;
            half   _AmbientOcclusion;
            half4  _RimColor;
            half   _RimPower;
            half   _RimThreshold;
            half   _RimStrength;
            half   _RimLightAlign;
            half4  _ContactColor;
            half   _ContactHeight;
            half   _ContactPower;
            half   _ContactStrength;
            float  _ContactNoiseScale;
            half   _ContactNoiseAmount;
            half4  _EmissionColor;
            half   _EmissionPulse;
            half4  _FlashColor;
            half   _FlashAmount;
            half   _DissolveAmount;
            half4  _DissolveEdgeColor;
            half   _DissolveEdgeWidth;
            float  _DissolveScale;
            half   _Cutoff;
            half   _Cull;
        CBUFFER_END

        TEXTURE2D(_BaseMap);   SAMPLER(sampler_BaseMap);

        // -----------------------------------------------------------------
        // Billboard basis.
        //
        // Rebuilt from scratch every vertex, which is the whole point: the
        // object's rotation is deliberately discarded, its translation is the
        // quad centre and its X/Y scale is the quad size in metres.
        //
        // faceDirWS is the unit vector from the sprite towards whatever it
        // should present its face to: the camera in UniversalForward, the light
        // in ShadowCaster. Everything downstream is shared.
        // -----------------------------------------------------------------
        struct PLBillboard
        {
            float3 positionWS;
            float3 right;
            float3 up;
            float3 forward;   // the quad's normal, pointing back along faceDirWS
        };

        PLBillboard PL_Billboard(float3 positionOS, float2 spriteUV,
                                 float3 faceDirWS, float pitchTilt,
                                 float depthSlope, float depthBias,
                                 float verticalOffset)
        {
            PLBillboard b;

            float3 centreWS = float3(UNITY_MATRIX_M._m03,
                                     UNITY_MATRIX_M._m13,
                                     UNITY_MATRIX_M._m23);

            // Column lengths of the object-to-world matrix are the world scale.
            float scaleX = length(float3(UNITY_MATRIX_M._m00,
                                         UNITY_MATRIX_M._m10,
                                         UNITY_MATRIX_M._m20));
            float scaleY = length(float3(UNITY_MATRIX_M._m01,
                                         UNITY_MATRIX_M._m11,
                                         UNITY_MATRIX_M._m21));

            // Yaw only. A cylindrical base keeps the sprite standing upright no
            // matter where the viewer is; the pitch is added back in a controlled
            // amount below rather than taken wholesale.
            float2 flatDir = faceDirWS.xz;
            float flatLen = length(flatDir);
            float3 fwd = (flatLen > 1e-4)
                       ? float3(flatDir.x / flatLen, 0.0, flatDir.y / flatLen)
                       : float3(0.0, 0.0, 1.0);

            float3 worldUp = float3(0.0, 1.0, 0.0);
            float3 right = cross(worldUp, fwd);

            // Lean the top away from the viewer by a fraction of the viewer's
            // elevation. At 0 the quad is a pure Y-billboard and goes razor-thin
            // from above; at 1 it faces the viewer squarely and lies down flat on
            // a high shot. The HD-2D band is a third of the way in.
            float elevation = asin(clamp(faceDirWS.y, -1.0, 1.0));
            float tilt = max(elevation, 0.0) * saturate(pitchTilt);
            float st, ct;
            sincos(tilt, st, ct);

            b.right   = right;
            b.up      = worldUp * ct - fwd * st;
            b.forward = fwd * ct + worldUp * st;

            float3 positionWS = centreWS
                              + right * (positionOS.x * scaleX)
                              + b.up  * (positionOS.y * scaleY + verticalOffset);

            // Sloped depth: the head is pushed away from the viewer, the feet are
            // not. Written as geometry rather than as SV_Depth so every pass in
            // this shader agrees without having to duplicate the maths.
            positionWS -= b.forward * (depthSlope * saturate(spriteUV.y));
            positionWS += b.forward * depthBias;

            b.positionWS = positionWS;
            return b;
        }

        // Sprite atlas cell lookup plus the horizontal flip. Index 0 is the
        // top-left cell and runs left to right, which matches how sprite sheets
        // are authored.
        float2 PL_SpriteUV(float2 spriteUV)
        {
            float2 uv = spriteUV * _BaseMap_ST.xy + _BaseMap_ST.zw;
            uv.x = lerp(uv.x, 1.0 - uv.x, saturate(_FlipX));

            float cols = max(_AtlasColumns, 1.0);
            float rows = max(_AtlasRows, 1.0);
            float index = floor(_AtlasIndex + 0.5);
            float col = fmod(index, cols);
            float row = floor(index / cols);
            // Row 0 at the top of the sheet, so flip into UV space.
            float2 cell = float2(col, rows - 1.0 - row);
            return (uv + cell) / float2(cols, rows);
        }
        ENDHLSL

        // =====================================================================
        // Forward lit. Billboards to the camera.
        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            Blend One Zero
            // Polygon offset is the last-resort fix for a contact edge that still
            // z-fights after the pitch tilt and the depth slope. It is off because
            // it should not be needed and it costs correctness elsewhere -- an
            // offset sprite sorts wrong against other sprites. If the feet still
            // shimmer against the ground, change this line here and in DepthOnly
            // and DepthNormals *together*, to: Offset -1, -1
            Offset 0, 0

            HLSLPROGRAM
            #pragma vertex SpriteVertex
            #pragma fragment SpriteFragment
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float4 uv          : TEXCOORD0;   // xy atlas uv, zw raw quad uv
                float4 positionWS  : TEXCOORD1;   // xyz world, w fog factor
                half3  billRight   : TEXCOORD2;
                half3  billUp      : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SpriteVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 centreWS = float3(UNITY_MATRIX_M._m03,
                                         UNITY_MATRIX_M._m13,
                                         UNITY_MATRIX_M._m23);
                float3 toCamera = _WorldSpaceCameraPos - centreWS;
                float3 faceDir = SafeNormalize(toCamera);

                PLBillboard bill = PL_Billboard(input.positionOS.xyz, input.uv,
                                                faceDir, _PitchTilt,
                                                _DepthSlope, _DepthBias,
                                                _VerticalOffset);

                float4 positionCS = TransformWorldToHClip(bill.positionWS);

                output.positionCS = positionCS;
                output.uv = float4(PL_SpriteUV(input.uv), input.uv);
                output.positionWS = float4(bill.positionWS, ComputeFogFactor(positionCS.z));
                output.billRight = half3(bill.right);
                output.billUp = half3(bill.up);

                // Shadow receive is sampled from a point pushed towards the key,
                // because the shadow caster below is a *differently oriented* quad
                // and a sprite would otherwise sit permanently inside its own
                // shadow. This is the documented "billboards are almost always
                // half in shadow" failure.
                VertexPositionInputs shadowIn = (VertexPositionInputs)0;
                shadowIn.positionWS = bill.positionWS +
                                      normalize(_MainLightPosition.xyz + float3(1e-5, 1e-5, 1e-5)) *
                                      _SelfShadowOffset;
                shadowIn.positionCS = positionCS;
                output.shadowCoord = GetShadowCoord(shadowIn);

                return output;
            }

            // Banded, tinted diffuse for one light. Identical energy convention to
            // PokeLab/Creature so a sprite and a 3D prop under the same key land on
            // the same colour. Specular is deliberately absent: the pixel look does
            // not survive a moving highlight, and section 7 of the plan kills spec
            // across every family.
            half3 ShadeSpriteLight(Light light, half3 normalWS, half3 albedo,
                                   half isKey, float2 svPosition, float rampAA)
            {
                half atten = light.distanceAttenuation * light.shadowAttenuation;
                atten = lerp(1.0, atten, _ShadowStrength);

                half ndotl = dot(normalWS, light.direction);
                half ramp = PL_ToonRamp(ndotl, _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                        svPosition, rampAA);
                ramp *= atten;

                half3 lit = albedo * light.color;
                half3 shaded = albedo * _ShadeColor.rgb * light.color * isKey;
                return lerp(shaded, lit, ramp);
            }

            half4 SpriteFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 spriteUV = input.uv.zw;
                float3 positionWS = input.positionWS.xyz;

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv.xy);
                half alpha = baseSample.a * _BaseColor.a;
                // Always clipped. A sprite in the opaque queue is only correct
                // because its transparent border is discarded rather than blended.
                clip(alpha - _Cutoff);

                // --- Dissolve. Same signature as the creature and VFX shaders. --
                half dissolveEdge = 0;
                if (_DissolveAmount > 0.001)
                {
                    float n = PL_Fbm(positionWS.xz * _DissolveScale +
                                     positionWS.y * _DissolveScale * 0.7);
                    // Sprite UV height is the natural "up the body" coordinate here,
                    // so the dissolve climbs from the feet exactly as it does on a
                    // 3D creature whose feet sit at the pivot.
                    n = saturate(n * 0.7 + saturate(spriteUV.y) * 0.3);
                    float threshold = _DissolveAmount * 1.25;
                    float d = n - threshold;
                    clip(d + _DissolveEdgeWidth);
                    dissolveEdge = 1.0 - saturate(d / _DissolveEdgeWidth);
                }

                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                // --- Synthetic sphere normal --------------------------------
                // Built from the *raw* quad UV, never the flipped one: the flip
                // mirrors the drawn image, not the screen position of the surface,
                // so the shading volume must stay where it is.
                half3 right = normalize(input.billRight);
                half3 up = normalize(input.billUp);
                half3 flatNormal = normalize(cross(right, up));

                half2 sphere = half2(spriteUV * 2.0 - 1.0) * _NormalCurvature;
                half sphereLen2 = saturate(dot(sphere, sphere));
                half sphereZ = sqrt(max(1.0 - sphereLen2, 0.0));
                half3 curved = normalize(right * sphere.x + up * sphere.y + flatNormal * sphereZ);
                half3 normalWS = normalize(lerp(flatNormal, curved, saturate(_SphereNormal)));

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(positionWS));

                // --- Ground contact, ported from PokeLabPropGroundBlend --------
                // A noisy threshold rather than a clean gradient: a straight fade
                // still reads as a band, a broken one reads as grass growing over
                // the feet.
                float h = saturate(spriteUV.y / max(_ContactHeight, 1e-3));
                float contactNoise = PL_Fbm(positionWS.xz * _ContactNoiseScale);
                h = saturate(h + (contactNoise - 0.5) * _ContactNoiseAmount);
                half contact = pow(saturate(1.0 - h), _ContactPower) * _ContactStrength;

                albedo = lerp(albedo, _ContactColor.rgb, contact);
                // Shade the contact zone like ground rather than like a wall.
                normalWS = normalize(lerp(normalWS, half3(0, 1, 0), contact * 0.6));

                // --- Occlusion. No vertex colour on a quad, so the base darkening
                // is a constant scaled by the same contact gradient. -----------
                half occlusion = lerp(1.0, 1.0 - _AmbientOcclusion, contact);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(screenUV);
                    occlusion = min(occlusion, aoFactor.indirectAmbientOcclusion);
                #endif

                // --- Main light ------------------------------------------------
                half4 shadowMask = half4(1, 1, 1, 1);
                Light mainLight = GetMainLight(input.shadowCoord, positionWS, shadowMask);

                // One derivative read for the whole fragment, taken where control
                // flow is still uniform and outside the clustered light loop.
                float rampAA = PL_ToonRampWidth(normalWS, _ShadeSteps);

                half3 colour = ShadeSpriteLight(mainLight, normalWS, albedo,
                                                1.0, svPosition, rampAA);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    colour *= aoFactor.directAmbientOcclusion;
                #endif

                // --- Additional lights -----------------------------------------
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = positionWS;
                    inputData.normalizedScreenSpaceUV = screenUV;

                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light addLight = GetAdditionalLight(lightIndex, positionWS, shadowMask);
                        colour += ShadeSpriteLight(addLight, normalWS, albedo,
                                                   0.0, svPosition, rampAA);
                    LIGHT_LOOP_END
                }
                #endif

                // --- Ambient. Same gradient the environment samples, through the
                // synthetic normal, so sprites pick up sky and bounce tint. -----
                colour += PL_Ambient(normalWS) * albedo * occlusion;

                // --- Rim -------------------------------------------------------
                half rim = PL_Rim(normalWS, viewDirWS, _RimPower, _RimThreshold);
                half lightAlign = saturate(dot(normalWS, mainLight.direction) * 0.5 + 0.5);
                half rimWeight = lerp(1.0, 1.0 - lightAlign, _RimLightAlign);
                // _PL_RimBoost lands once, on the strength.
                //
                // It used to land twice — scaled into the rim colour as well — and the two
                // multiply, so the director's boost entered this term squared. It went unseen
                // while world sprites drew through URP/Unlit, which has no rim at all; the
                // moment they moved onto this shader every character in the game was washed
                // out. The same expression, and the same fix, as PokeLabFoliage.
                half3 rimColour = lerp(_RimColor.rgb, _PL_RimTint.rgb, saturate(_PL_RimBoost));
                colour += rimColour * rim * rimWeight * (_RimStrength + _PL_RimBoost) * occlusion;

                // --- Emission --------------------------------------------------
                half pulse = 1.0;
                if (_EmissionPulse > 0.001)
                    pulse = 0.65 + 0.35 * sin(_Time.y * _EmissionPulse);
                colour += _EmissionColor.rgb * pulse;

                colour += _DissolveEdgeColor.rgb * dissolveEdge;

                // Hit flash. Driven per-frame by BattleVfxPresenter, same property
                // name as PokeLab/Creature so the presenter needs no branch.
                colour = lerp(colour, _FlashColor.rgb, saturate(_FlashAmount));

                colour = MixFog(colour, input.positionWS.w);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, alpha);
            }
            ENDHLSL
        }

        // =====================================================================
        // Shadow caster. Billboards to the LIGHT, not to the camera.
        //
        // This is the pass that stops the creature's shadow going razor-thin and
        // vanishing as the camera orbits. Yaw only: leaning the quad towards the
        // sun would shorten the shadow, and a full-width shadow of the right
        // length is what grounds the sprite.
        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex SpriteShadowVertex
            #pragma fragment SpriteShadowFragment
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings SpriteShadowVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 centreWS = float3(UNITY_MATRIX_M._m03,
                                         UNITY_MATRIX_M._m13,
                                         UNITY_MATRIX_M._m23);

                // Both branches yield a unit vector pointing *towards* the light,
                // which is the convention URP's ShadowCasterPass uses for
                // _LightDirection and the one ApplyShadowBias expects.
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirWS = normalize(_LightPosition - centreWS);
                #else
                    float3 lightDirWS = _LightDirection;
                #endif

                // Face the light, and do not tilt: a shadow caster that leans is a
                // shadow that changes length with the camera.
                PLBillboard bill = PL_Billboard(input.positionOS.xyz, input.uv,
                                                lightDirWS, 0.0,
                                                0.0, 0.0, _VerticalOffset);

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(bill.positionWS, bill.forward, lightDirWS));
                output.positionCS = ApplyShadowClamping(positionCS);
                output.uv = PL_SpriteUV(input.uv);
                return output;
            }

            half4 SpriteShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        // Depth only. Must use the *camera* billboard and the same depth slope as
        // ForwardLit, or a depth prepass would disagree with the colour pass and
        // erase the sprite.
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]
            Offset 0, 0   // keep in step with ForwardLit

            HLSLPROGRAM
            #pragma vertex SpriteDepthVertex
            #pragma fragment SpriteDepthFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SpriteDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 centreWS = float3(UNITY_MATRIX_M._m03,
                                         UNITY_MATRIX_M._m13,
                                         UNITY_MATRIX_M._m23);
                float3 faceDir = SafeNormalize(_WorldSpaceCameraPos - centreWS);

                PLBillboard bill = PL_Billboard(input.positionOS.xyz, input.uv,
                                                faceDir, _PitchTilt,
                                                _DepthSlope, _DepthBias,
                                                _VerticalOffset);

                output.positionCS = TransformWorldToHClip(bill.positionWS);
                output.uv = PL_SpriteUV(input.uv);
                return output;
            }

            half4 SpriteDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        // Depth normals. Feeds SSAO and any normal-aware post effect. Emits the
        // synthetic sphere normal, not the quad's flat normal, so SSAO sees the
        // same rounded volume the lighting does.
        // =====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]
            Offset 0, 0   // keep in step with ForwardLit

            HLSLPROGRAM
            #pragma vertex SpriteDepthNormalsVertex
            #pragma fragment SpriteDepthNormalsFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv         : TEXCOORD0;   // xy atlas uv, zw raw quad uv
                half3  billRight  : TEXCOORD1;
                half3  billUp     : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SpriteDepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 centreWS = float3(UNITY_MATRIX_M._m03,
                                         UNITY_MATRIX_M._m13,
                                         UNITY_MATRIX_M._m23);
                float3 faceDir = SafeNormalize(_WorldSpaceCameraPos - centreWS);

                PLBillboard bill = PL_Billboard(input.positionOS.xyz, input.uv,
                                                faceDir, _PitchTilt,
                                                _DepthSlope, _DepthBias,
                                                _VerticalOffset);

                output.positionCS = TransformWorldToHClip(bill.positionWS);
                output.uv = float4(PL_SpriteUV(input.uv), input.uv);
                output.billRight = half3(bill.right);
                output.billUp = half3(bill.up);
                return output;
            }

            half4 SpriteDepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv.xy).a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 right = normalize(input.billRight);
                half3 up = normalize(input.billUp);
                half3 flatNormal = normalize(cross(right, up));

                float2 sphere = (input.uv.zw * 2.0 - 1.0) * _NormalCurvature;
                float sphereLen2 = saturate(dot(sphere, sphere));
                float sphereZ = sqrt(1.0 - sphereLen2);
                half3 curved = normalize(right * sphere.x + up * sphere.y + flatNormal * sphereZ);
                half3 normalWS = normalize(lerp(flatNormal, curved, saturate(_SphereNormal)));

                return half4(NormalizeNormalPerPixel(normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
