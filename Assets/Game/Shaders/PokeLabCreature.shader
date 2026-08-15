// -----------------------------------------------------------------------------
// PokeLab/Creature
//
// The single shader every creature, trainer and character in the game uses.
// Banded ramp diffuse, tinted shadow, stepped specular, shared rim, wrapped
// subsurface for fins and ears, three tint zones so one material definition can
// serve twelve species, plus an inverted-hull outline.
//
// URP correct: casts and receives shadows, writes depth and depth-normals, fogs,
// and keeps every per-material property inside UnityPerMaterial for the SRP
// batcher.
//
// Texture conventions expected from the Blender workers:
//   _BaseMap   RGB albedo, A alpha (only read when _ALPHATEST_ON)
//   _BumpMap   tangent-space normal
//   _MaskMap   R specular mask, G subsurface mask, B tint-zone B, A emission mask
//   vertex colour  R tint-zone A, G tint-zone C, B baked occlusion
// A missing _MaskMap samples as white, which is a sane fully-lit default.
// -----------------------------------------------------------------------------
Shader "PokeLab/Creature"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Colour", Color) = (1,1,1,1)

        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0,2)) = 1

        _MaskMap("Mask (R spec, G sss, B tintB, A emissive)", 2D) = "white" {}

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.42,0.47,0.66,1)
        // HD-2D ramp language. 3 bands with a 0.02 edge is a hard cel terminator,
        // not a soft one, and the same pair is authored on every family in this
        // folder -- creature, sprite, prop, terrain, foliage. If the world bands at
        // 3 steps and a creature bands at 8 they read as two renderers in one frame.
        _ShadeSteps("Band Count", Range(1,6)) = 3
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.02
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.25
        _ShadowStrength("Cast Shadow Depth", Range(0,1)) = 0.8
        _OcclusionStrength("Vertex Occlusion", Range(0,1)) = 0.7

        [Header(Specular)][Space(4)]
        _SpecularColor("Specular Colour", Color) = (1,1,1,1)
        // Specular is nearly off for the pixel look. A moving Blinn-Phong smear is
        // the strongest "this is a 3D model" tell there is, so what survives is a
        // small, fully stepped highlight that reads as a painted shape.
        _SpecularSharpness("Specular Sharpness", Range(1,128)) = 48
        _SpecularStrength("Specular Strength", Range(0,2)) = 0.08
        _SpecularStep("Specular Hardness", Range(0,1)) = 1.0

        [Header(Rim)][Space(4)]
        _RimColor("Rim Colour", Color) = (1,0.95,0.85,1)
        _RimPower("Rim Power", Range(0.5,12)) = 3
        _RimThreshold("Rim Threshold", Range(0,1)) = 0.25
        _RimStrength("Rim Strength", Range(0,3)) = 0.8
        _RimLightAlign("Rim Follows Light", Range(0,1)) = 0.6

        [Header(Subsurface)][Space(4)]
        _SssColor("Subsurface Colour", Color) = (1,0.45,0.35,1)
        _SssStrength("Subsurface Strength", Range(0,3)) = 0.7
        _SssPower("Subsurface Falloff", Range(1,16)) = 4

        [Header(Tint Zones)][Space(4)]
        _TintA("Tint A (vertex R)", Color) = (1,1,1,1)
        _TintB("Tint B (mask B)", Color) = (1,1,1,1)
        _TintC("Tint C (vertex G)", Color) = (1,1,1,1)

        [Header(Emission)][Space(4)]
        [HDR] _EmissionColor("Emission Colour", Color) = (0,0,0,1)
        _EmissionPulse("Emission Pulse Speed", Range(0,8)) = 0

        [Header(Outline)][Space(4)]
        _OutlineColor("Outline Colour", Color) = (0.08,0.07,0.12,1)
        _OutlineWidth("Outline Width (px at 1m)", Range(0,8)) = 1.4
        _OutlineFadeStart("Outline Fade Start (m)", Float) = 22
        _OutlineFadeEnd("Outline Fade End (m)", Float) = 40

        [Header(Battle Overrides)][Space(4)]
        [HDR] _FlashColor("Flash Colour", Color) = (1,1,1,1)
        _FlashAmount("Flash Amount", Range(0,1)) = 0
        _DissolveAmount("Dissolve Amount", Range(0,1)) = 0
        [HDR] _DissolveEdgeColor("Dissolve Edge Colour", Color) = (1.6,0.9,0.25,1)
        _DissolveEdgeWidth("Dissolve Edge Width", Range(0.001,0.35)) = 0.08
        _DissolveScale("Dissolve Noise Scale", Float) = 9

        [Header(Surface)][Space(4)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
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
            half   _BumpScale;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half   _ShadowStrength;
            half   _OcclusionStrength;
            half4  _SpecularColor;
            half   _SpecularSharpness;
            half   _SpecularStrength;
            half   _SpecularStep;
            half4  _RimColor;
            half   _RimPower;
            half   _RimThreshold;
            half   _RimStrength;
            half   _RimLightAlign;
            half4  _SssColor;
            half   _SssStrength;
            half   _SssPower;
            half4  _TintA;
            half4  _TintB;
            half4  _TintC;
            half4  _EmissionColor;
            half   _EmissionPulse;
            half4  _OutlineColor;
            half   _OutlineWidth;
            float  _OutlineFadeStart;
            float  _OutlineFadeEnd;
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
        TEXTURE2D(_BumpMap);   SAMPLER(sampler_BumpMap);
        TEXTURE2D(_MaskMap);   SAMPLER(sampler_MaskMap);
        ENDHLSL

        // =====================================================================
        // Outline. Drawn first, inverted hull, so it never occludes the creature.
        // =====================================================================
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            struct OutlineAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct OutlineVaryings
            {
                float4 positionCS : SV_POSITION;
                float  fogFactor  : TEXCOORD0;
                float  width      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            OutlineVaryings OutlineVertex(OutlineAttributes input)
            {
                OutlineVaryings output = (OutlineVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS);

                float dist = distance(posIn.positionWS, _WorldSpaceCameraPos);
                float fade = 1.0 - saturate((dist - _OutlineFadeStart) /
                                            max(_OutlineFadeEnd - _OutlineFadeStart, 0.001));

                // Widen in clip space and multiply by w so the outline holds a
                // constant pixel width instead of thinning out with distance.
                // _ScreenParams.xy is the render target size in pixels; the factor
                // of two converts NDC extent (-1..1) into pixels.
                float3 normalCS = TransformWorldToHClipDir(normIn.normalWS);
                float2 dirCS = normalize(normalCS.xy + float2(1e-6, 1e-6));
                float2 offset = dirCS * (_OutlineWidth * 2.0 / _ScreenParams.xy) * fade;

                float4 positionCS = posIn.positionCS;
                positionCS.xy += offset * positionCS.w;
                output.positionCS = positionCS;
                output.fogFactor = ComputeFogFactor(positionCS.z);
                output.width = _OutlineWidth * fade;
                return output;
            }

            half4 OutlineFragment(OutlineVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // A zero width outline would z-fight with the surface, so remove it
                // entirely rather than drawing a coincident hull.
                clip(input.width - 1e-3);
                half3 colour = _OutlineColor.rgb;
                colour = MixFog(colour, input.fogFactor);
                return half4(colour, 1.0);
            }
            ENDHLSL
        }

        // =====================================================================
        // Forward lit.
        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex CreatureVertex
            #pragma fragment CreatureFragment
            #pragma target 3.0

            #pragma shader_feature_local_fragment _ALPHATEST_ON

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
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                half3  normalWS    : TEXCOORD2;
                half4  tangentWS   : TEXCOORD3;   // w = bitangent sign
                float4 shadowCoord : TEXCOORD4;
                half4  colour      : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CreatureVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.normalWS = half3(normIn.normalWS);
                output.tangentWS = half4(normIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.colour = half4(input.colour);
                output.shadowCoord = GetShadowCoord(posIn);
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            // Banded, tinted diffuse response for one light.
            //
            // isKey selects between two energy conventions. The key light replaces
            // the surface colour, so its unlit region is tinted by _ShadeColor and
            // reads as a coloured shadow. Fill lights are purely additive, so their
            // unlit region contributes nothing and a torch can only ever brighten.
            half3 ShadeLight(Light light, half3 normalWS, half3 viewDirWS, half3 albedo,
                             half specMask, half sssMask, half isKey, float2 svPosition,
                             float rampAA, out half3 specularOut)
            {
                half atten = light.distanceAttenuation * light.shadowAttenuation;
                // _ShadowStrength lets the artist keep shadows readable rather than black.
                atten = lerp(1.0, atten, _ShadowStrength);

                half ndotl = dot(normalWS, light.direction);
                half ramp = PL_ToonRamp(ndotl, _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                        svPosition, rampAA);
                ramp *= atten;

                half3 lit = albedo * light.color;
                half3 shaded = albedo * _ShadeColor.rgb * light.color * isKey;
                half3 diffuse = lerp(shaded, lit, ramp);

                // Stepped Blinn-Phong. The step keeps the highlight reading as a
                // shape rather than as a smear, which is the toon look we want.
                half3 halfVec = SafeNormalize(light.direction + viewDirWS);
                half ndoth = saturate(dot(normalWS, halfVec));
                half spec = pow(ndoth, _SpecularSharpness);
                // Same reasoning as the ramp: no derivative reads in here, because
                // this function runs inside the clustered light loop. A sharper
                // highlight is a tighter one, so its screen-space width scales with
                // the exponent.
                half specAA = max(rampAA * _SpecularSharpness * 0.25, 0.01);
                half specStep = lerp(spec, smoothstep(0.5 - specAA, 0.5 + specAA, spec), _SpecularStep);
                specularOut = _SpecularColor.rgb * light.color * specStep *
                              _SpecularStrength * specMask * ramp;

                // Wrapped transmission for ears, fins and wings. Uses the inverted
                // normal so the light has to be behind the surface to show through.
                half backlight = saturate(dot(-normalWS, light.direction));
                half through = pow(saturate(dot(-viewDirWS, light.direction) * 0.5 + 0.5), _SssPower);
                half3 sss = _SssColor.rgb * light.color * (backlight * 0.35 + through * 0.65) *
                            _SssStrength * sssMask * atten;

                return diffuse + sss;
            }

            half4 CreatureFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);

                half alpha = baseSample.a * _BaseColor.a;
                #if defined(_ALPHATEST_ON)
                    clip(alpha - _Cutoff);
                #endif

                // --- Dissolve. Shared with the capture and faint effects so the
                // creature dissolves with the same signature as its VFX. -------
                half dissolveEdge = 0;
                if (_DissolveAmount > 0.001)
                {
                    float n = PL_Fbm(input.positionWS.xz * _DissolveScale +
                                     input.positionWS.y * _DissolveScale * 0.7);
                    // Bias by height above the pivot. Feet sit at the origin by the
                    // art contract, so this dissolves the creature from the ground up.
                    float localY = input.positionWS.y - UNITY_MATRIX_M._m13;
                    n = saturate(n * 0.7 + saturate(localY * 0.45) * 0.3);
                    float threshold = _DissolveAmount * 1.25;
                    float d = n - threshold;
                    clip(d + _DissolveEdgeWidth);
                    dissolveEdge = 1.0 - saturate(d / _DissolveEdgeWidth);
                }

                // --- Tint zones. Three independent masks so one material serves
                // a whole species line by swapping colours only. ---------------
                half3 albedo = baseSample.rgb * _BaseColor.rgb;
                albedo = lerp(albedo, albedo * _TintA.rgb, input.colour.r);
                albedo = lerp(albedo, albedo * _TintB.rgb, mask.b);
                albedo = lerp(albedo, albedo * _TintC.rgb, input.colour.g);

                // --- Normals ---------------------------------------------------
                half3 geoNormalWS = normalize(input.normalWS);
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                // --- Occlusion -------------------------------------------------
                half occlusion = lerp(1.0, input.colour.b, _OcclusionStrength);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(screenUV);
                    occlusion = min(occlusion, aoFactor.indirectAmbientOcclusion);
                #endif

                half specMask = mask.r;
                half sssMask = mask.g;

                // --- Main light ------------------------------------------------
                half4 shadowMask = half4(1, 1, 1, 1);
                Light mainLight = GetMainLight(input.shadowCoord, input.positionWS, shadowMask);

                // One derivative read for the whole fragment, taken here where the
                // control flow is still uniform.
                float rampAA = PL_ToonRampWidth(normalWS, _ShadeSteps);

                half3 specular = 0;
                half3 colour = ShadeLight(mainLight, normalWS, viewDirWS, albedo,
                                          specMask, sssMask, 1.0, svPosition, rampAA, specular);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    colour *= aoFactor.directAmbientOcclusion;
                #endif

                // --- Additional lights. Clustered-safe via the URP light loop. --
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalizedScreenSpaceUV = screenUV;

                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light addLight = GetAdditionalLight(lightIndex, input.positionWS, shadowMask);
                        half3 addSpec = 0;
                        colour += ShadeLight(addLight, normalWS, viewDirWS, albedo,
                                             specMask, sssMask, 0.0, svPosition, rampAA, addSpec);
                        specular += addSpec;
                    LIGHT_LOOP_END
                }
                #endif

                // --- Ambient ---------------------------------------------------
                half3 ambient = PL_Ambient(normalWS) * albedo * occlusion;
                colour += ambient;
                colour += specular;

                // --- Rim. Partly follows the key light so a backlit creature gets
                // a hot edge and a front-lit one stays clean. ------------------
                half rim = PL_Rim(normalWS, viewDirWS, _RimPower, _RimThreshold);
                half lightAlign = saturate(dot(normalWS, mainLight.direction) * 0.5 + 0.5);
                half rimWeight = lerp(1.0, 1.0 - lightAlign, _RimLightAlign);
                half3 rimColour = _RimColor.rgb + _PL_RimTint.rgb * _PL_RimBoost;
                colour += rimColour * rim * rimWeight * (_RimStrength + _PL_RimBoost) * occlusion;

                // --- Emission --------------------------------------------------
                half pulse = 1.0;
                if (_EmissionPulse > 0.001)
                    pulse = 0.65 + 0.35 * sin(_Time.y * _EmissionPulse);
                colour += _EmissionColor.rgb * mask.a * pulse;

                // Dissolve edge glows on top of everything so it survives the grade.
                colour += _DissolveEdgeColor.rgb * dissolveEdge;

                // Hit flash. Driven per-frame by BattleVfxPresenter.
                colour = lerp(colour, _FlashColor.rgb, saturate(_FlashAmount));

                colour = MixFog(colour, input.fogFactor);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, alpha);
            }
            ENDHLSL
        }

        // =====================================================================
        // Shadow caster.
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
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma target 3.0
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirWS));
                output.positionCS = ApplyShadowClamping(positionCS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 ShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                #if defined(_ALPHATEST_ON)
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        // Depth only. Required for depth prepass, SSAO and soft particles.
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma target 3.0
            #pragma shader_feature_local_fragment _ALPHATEST_ON
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

            Varyings DepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                #if defined(_ALPHATEST_ON)
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        // Depth normals. Feeds SSAO and any normal-aware post effect.
        // =====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertexPL
            #pragma fragment DepthNormalsFragmentPL
            #pragma target 3.0
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half4  tangentWS  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVertexPL(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = half3(NormalizeNormalPerVertex(normIn.normalWS));
                output.tangentWS = half4(normIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthNormalsFragmentPL(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                #if defined(_ALPHATEST_ON)
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif

                half3 geoNormalWS = normalize(input.normalWS);
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                return half4(NormalizeNormalPerPixel(normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
