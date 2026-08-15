// -----------------------------------------------------------------------------
// PokeLab/TerrainBlend
//
// Four-layer stylised terrain for mesh terrain (not Unity's terrain system, so it
// works with whatever the environment artist exports from Blender).
//
//   Layer 0  grass   world XZ planar
//   Layer 1  dirt    world XZ planar
//   Layer 2  sand    world XZ planar
//   Layer 3  rock    triplanar, always
//
// Layers 0-2 project from world XZ rather than from mesh UVs, so terrain tiles
// seamlessly regardless of how the mesh was unwrapped. Rock is triplanar without
// a toggle: it is the layer that lands on cliffs, and a cliff sampled planar is
// the single most obvious "this is a game engine" artefact there is.
//
// Weights come from vertex colours (R grass, G dirt, B sand, A rock) optionally
// multiplied by a control map, then pushed through a height-aware blend so gravel
// pokes through grass instead of cross-fading with it. Slope steers weight into
// rock automatically, so an artist never has to hand-paint a cliff.
//
// Wetness darkens, glosses and flattens the surface. It is driven both by the
// _PL_Wetness global (rain, set by LightingDirector) and by proximity to the
// water plane, which is what puts puddles along the lake shore.
// -----------------------------------------------------------------------------
Shader "PokeLab/TerrainBlend"
{
    Properties
    {
        [Header(Layer 0 Grass)][Space(4)]
        _Layer0Map("Grass Albedo (A = height)", 2D) = "white" {}
        [Normal] _Layer0Normal("Grass Normal", 2D) = "bump" {}
        _Layer0Color("Grass Tint", Color) = (0.45,0.62,0.28,1)
        _Layer0Scale("Grass Scale (tiles per m)", Float) = 0.35
        _Layer0NormalScale("Grass Normal Scale", Range(0,2)) = 1

        [Header(Layer 1 Dirt)][Space(4)]
        _Layer1Map("Dirt Albedo (A = height)", 2D) = "white" {}
        [Normal] _Layer1Normal("Dirt Normal", 2D) = "bump" {}
        _Layer1Color("Dirt Tint", Color) = (0.42,0.31,0.21,1)
        _Layer1Scale("Dirt Scale", Float) = 0.5
        _Layer1NormalScale("Dirt Normal Scale", Range(0,2)) = 1

        [Header(Layer 2 Sand)][Space(4)]
        _Layer2Map("Sand Albedo (A = height)", 2D) = "white" {}
        [Normal] _Layer2Normal("Sand Normal", 2D) = "bump" {}
        _Layer2Color("Sand Tint", Color) = (0.82,0.74,0.55,1)
        _Layer2Scale("Sand Scale", Float) = 0.6
        _Layer2NormalScale("Sand Normal Scale", Range(0,2)) = 1

        [Header(Layer 3 Rock triplanar)][Space(4)]
        _Layer3Map("Rock Albedo (A = height)", 2D) = "white" {}
        [Normal] _Layer3Normal("Rock Normal", 2D) = "bump" {}
        _Layer3Color("Rock Tint", Color) = (0.46,0.45,0.48,1)
        _Layer3Scale("Rock Scale", Float) = 0.28
        _Layer3NormalScale("Rock Normal Scale", Range(0,2)) = 1
        _TriplanarSharpness("Triplanar Sharpness", Range(1,16)) = 5

        [Header(Blending)][Space(4)]
        _ControlMap("Control Map (RGBA weights, optional)", 2D) = "white" {}
        _UseControlMap("Use Control Map", Range(0,1)) = 0
        _HeightContrast("Height Blend Contrast", Range(0.02,1)) = 0.18
        _SlopeStart("Rock Slope Start (deg)", Range(0,90)) = 30
        _SlopeEnd("Rock Slope Full (deg)", Range(0,90)) = 52
        _MacroVariation("Macro Colour Variation", Range(0,1)) = 0.4
        _MacroScale("Macro Variation Scale (m)", Float) = 26

        [Header(Wetness)][Space(4)]
        _Wetness("Wetness", Range(0,1)) = 0
        _WaterLevel("Water Plane Height (m)", Float) = 0
        _ShoreWetHeight("Shore Wet Band (m)", Float) = 0.6
        _PuddleAmount("Puddle Amount", Range(0,1)) = 0.35
        _PuddleScale("Puddle Scale (m)", Float) = 3.5

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.42,0.50,0.68,1)
        _ShadeSteps("Band Count", Range(1,6)) = 3
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.15
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.2
        _ShadowStrength("Cast Shadow Depth", Range(0,1)) = 0.85
        _SpecularStrength("Specular Strength", Range(0,2)) = 0.15
        _SpecularSharpness("Specular Sharpness", Range(1,128)) = 20
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry-10"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _ControlMap_ST;
            half4  _Layer0Color; float _Layer0Scale; half _Layer0NormalScale;
            half4  _Layer1Color; float _Layer1Scale; half _Layer1NormalScale;
            half4  _Layer2Color; float _Layer2Scale; half _Layer2NormalScale;
            half4  _Layer3Color; float _Layer3Scale; half _Layer3NormalScale;
            half   _TriplanarSharpness;
            half   _UseControlMap;
            half   _HeightContrast;
            half   _SlopeStart;
            half   _SlopeEnd;
            half   _MacroVariation;
            float  _MacroScale;
            half   _Wetness;
            float  _WaterLevel;
            float  _ShoreWetHeight;
            half   _PuddleAmount;
            float  _PuddleScale;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half   _ShadowStrength;
            half   _SpecularStrength;
            half   _SpecularSharpness;
        CBUFFER_END

        TEXTURE2D(_Layer0Map);    SAMPLER(sampler_Layer0Map);
        TEXTURE2D(_Layer0Normal);
        TEXTURE2D(_Layer1Map);    TEXTURE2D(_Layer1Normal);
        TEXTURE2D(_Layer2Map);    TEXTURE2D(_Layer2Normal);
        TEXTURE2D(_Layer3Map);    TEXTURE2D(_Layer3Normal);
        TEXTURE2D(_ControlMap);   SAMPLER(sampler_ControlMap);

        #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"
        ENDHLSL

        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment TerrainFragment
            #pragma target 3.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                half4  tangentWS   : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                half4  colour      : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TerrainVertex(Attributes input)
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
                output.uv = TRANSFORM_TEX(input.uv, _ControlMap);
                output.colour = half4(input.colour);
                output.shadowCoord = GetShadowCoord(posIn);
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 TerrainFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float3 positionWS = input.positionWS;
                half3 geoNormalWS = normalize(input.normalWS);

                // --- Weights ----------------------------------------------------
                half4 weights = input.colour;
                half4 control = SAMPLE_TEXTURE2D(_ControlMap, sampler_ControlMap, input.uv);
                weights = lerp(weights, weights * control, _UseControlMap);

                // Slope pushes weight into rock. Working in cosine space avoids an
                // acos per pixel; the artist still authors in degrees.
                half cosStart = cos(radians(_SlopeStart));
                half cosEnd = cos(radians(_SlopeEnd));
                half slopeRock = 1.0 - smoothstep(cosEnd, cosStart, geoNormalWS.y);
                weights.a = max(weights.a, slopeRock);
                weights.rgb *= (1.0 - slopeRock);

                // Never leave every weight at zero: an unpainted mesh should read as
                // grass, not as black.
                half weightSum = dot(weights, half4(1, 1, 1, 1));
                weights = (weightSum < 1e-3) ? half4(1, 0, 0, 0) : weights / weightSum;

                // --- Planar layers 0-2 ------------------------------------------
                float2 uv0 = positionWS.xz * _Layer0Scale;
                float2 uv1 = positionWS.xz * _Layer1Scale;
                float2 uv2 = positionWS.xz * _Layer2Scale;

                half4 a0 = SAMPLE_TEXTURE2D(_Layer0Map, sampler_Layer0Map, uv0);
                half4 a1 = SAMPLE_TEXTURE2D(_Layer1Map, sampler_Layer0Map, uv1);
                half4 a2 = SAMPLE_TEXTURE2D(_Layer2Map, sampler_Layer0Map, uv2);

                // --- Triplanar rock ---------------------------------------------
                float3 triW = PL_TriplanarWeights(geoNormalWS, _TriplanarSharpness);
                half4 a3 = PL_TRIPLANAR_SAMPLE(_Layer3Map, sampler_Layer0Map,
                                               positionWS, triW, _Layer3Scale);

                // --- Height-aware blend -----------------------------------------
                half4 heights = half4(a0.a, a1.a, a2.a, a3.a);
                half4 w = half4(PL_HeightBlend4(float4(weights), float4(heights), _HeightContrast));

                half3 albedo = a0.rgb * _Layer0Color.rgb * w.x
                             + a1.rgb * _Layer1Color.rgb * w.y
                             + a2.rgb * _Layer2Color.rgb * w.z
                             + a3.rgb * _Layer3Color.rgb * w.w;

                // --- Normals. Tangent-space layers are blended before unpacking so
                // we pay for one matrix transform, not four. ---------------------
                half3 n0 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer0Normal, sampler_Layer0Map, uv0), _Layer0NormalScale);
                half3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer1Normal, sampler_Layer0Map, uv1), _Layer1NormalScale);
                half3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_Layer2Normal, sampler_Layer0Map, uv2), _Layer2NormalScale);
                half3 nRock = UnpackNormalScale(
                    PL_TRIPLANAR_SAMPLE(_Layer3Normal, sampler_Layer0Map, positionWS, triW, _Layer3Scale),
                    _Layer3NormalScale);

                half3 normalTS = normalize(n0 * w.x + n1 * w.y + n2 * w.z + nRock * w.w + half3(0, 0, 1e-4));

                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                // --- Macro variation. Large low-frequency colour drift stops the
                // tiling from reading, which is the other big giveaway on terrain.
                float macro = PL_Fbm(positionWS.xz / max(_MacroScale, 0.01));
                albedo *= lerp(1.0, 0.72 + macro * 0.56, _MacroVariation);

                // --- Wetness and puddles ----------------------------------------
                // Shore band: everything within _ShoreWetHeight above the water
                // plane is damp, which is what visually anchors the lake edge.
                float aboveWater = positionWS.y - _WaterLevel;
                half shore = 1.0 - saturate(aboveWater / max(_ShoreWetHeight, 1e-3));
                shore *= step(0.0, aboveWater);

                half wet = saturate(max(_Wetness + _PL_Wetness, shore));

                // Puddles only form on near-flat ground and in low noise pockets.
                float puddleNoise = PL_Fbm(positionWS.xz / max(_PuddleScale, 0.01));
                half flatness = smoothstep(0.86, 0.97, geoNormalWS.y);
                half puddle = smoothstep(0.52, 0.34, puddleNoise) * flatness *
                              saturate(_PuddleAmount * wet * 1.6);

                half smoothness = lerp(0.05, 0.45, wet);
                PL_ApplyWetness(albedo, smoothness, normalWS, geoNormalWS, wet * 0.7);

                // A puddle is a tiny water plane: flat normal, very smooth, darkened
                // substrate, and a sky-coloured fresnel on top.
                albedo = lerp(albedo, albedo * 0.4, puddle);
                normalWS = normalize(lerp(normalWS, geoNormalWS, puddle));
                smoothness = lerp(smoothness, 0.97, puddle);

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(positionWS));

                half occlusion = 1.0;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(screenUV);
                    occlusion = aoFactor.indirectAmbientOcclusion;
                #endif

                // --- Lighting ---------------------------------------------------
                half4 shadowMask = half4(1, 1, 1, 1);
                Light mainLight = GetMainLight(input.shadowCoord, positionWS, shadowMask);

                half atten = lerp(1.0, mainLight.shadowAttenuation, _ShadowStrength);
                // One derivative read for the whole fragment, taken here where the
                // control flow is still uniform. Passing it in keeps gradient
                // instructions out of the clustered light loop below, where they are
                // undefined and can fail to compile.
                float rampAA = PL_ToonRampWidth(normalWS, _ShadeSteps);

                half ramp = PL_ToonRamp(dot(normalWS, mainLight.direction),
                                        _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                        svPosition, rampAA) * atten;

                half3 lit = albedo * mainLight.color;
                half3 shaded = albedo * _ShadeColor.rgb * mainLight.color;
                half3 colour = lerp(shaded, lit, ramp);

                // The highlight follows the smoothness that wetness and puddles wrote,
                // so a dry path, a damp shore and a standing puddle form a continuum
                // rather than three separately tuned cases.
                half3 halfVec = SafeNormalize(mainLight.direction + viewDirWS);
                half specPower = lerp(_SpecularSharpness, 320.0, smoothness);
                half spec = pow(saturate(dot(normalWS, halfVec)), specPower);
                half specAmount = lerp(_SpecularStrength, 2.5, smoothness);
                colour += mainLight.color * spec * specAmount * ramp;

                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = positionWS;
                    inputData.normalizedScreenSpaceUV = screenUV;

                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light addLight = GetAdditionalLight(lightIndex, positionWS, shadowMask);
                        half addAtten = addLight.distanceAttenuation * addLight.shadowAttenuation;
                        half addRamp = PL_ToonRamp(dot(normalWS, addLight.direction),
                                                   _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                                   svPosition, rampAA);
                        colour += albedo * addLight.color * addRamp * addAtten;
                    LIGHT_LOOP_END
                }
                #endif

                half3 ambient = PL_Ambient(normalWS);
                colour += ambient * albedo * occlusion;

                // Puddles pick up the sky. Grazing angles reflect most, which is
                // what makes a wet path glow along its length at dawn and dusk.
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), 5.0);
                colour += ambient * fresnel * puddle * 2.2;

                colour = MixFog(colour, input.fogFactor);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, 1.0);
            }
            ENDHLSL
        }

        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex TerrainShadowVertex
            #pragma fragment TerrainShadowFragment
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings TerrainShadowVertex(Attributes input)
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
                return output;
            }

            half4 TerrainShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex TerrainDepthVertex
            #pragma fragment TerrainDepthFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TerrainDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 TerrainDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }

        // =====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex TerrainDepthNormalsVertex
            #pragma fragment TerrainDepthNormalsFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TerrainDepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = half3(NormalizeNormalPerVertex(normIn.normalWS));
                return output;
            }

            half4 TerrainDepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
