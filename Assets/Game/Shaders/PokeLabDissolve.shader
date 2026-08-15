// -----------------------------------------------------------------------------
// PokeLab/Dissolve
//
// A standalone lit dissolve for any mesh that is not a creature: props being
// cleared, the capture ball's shell opening, a trainer sprite leaving. Creatures
// use the dissolve built into PokeLab/Creature so they keep their tint zones and
// outline while dissolving.
//
// Two dissolve drivers, combined:
//   Noise     the usual eroding edge, driven by a world-space fbm so adjacent
//             props do not dissolve in lockstep.
//   Direction a plane sweep along _DissolveDirection, so a capture can be
//             authored as "absorb from the feet up" rather than as a fade.
//
// The edge is emissive and unaffected by lighting, so it survives the battle
// grade and blooms the way it should.
// -----------------------------------------------------------------------------
Shader "PokeLab/Dissolve"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Colour", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0,2)) = 1

        [Header(Dissolve)][Space(4)]
        _DissolveAmount("Dissolve Amount", Range(0,1)) = 0
        _NoiseScale("Noise Scale", Float) = 7
        _NoiseWeight("Noise vs Direction", Range(0,1)) = 0.6
        _DissolveDirection("Sweep Direction (object space)", Vector) = (0,1,0,0)
        _DissolveSpan("Sweep Span (m)", Float) = 2.2
        [HDR] _EdgeColor("Edge Colour", Color) = (2.4,1.3,0.35,1)
        _EdgeWidth("Edge Width", Range(0.001,0.5)) = 0.09
        [HDR] _EdgeColorInner("Inner Edge Colour", Color) = (3.0,2.4,1.4,1)

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.42,0.48,0.66,1)
        // Shared HD-2D ramp language: 3 bands, 0.02 edge. See PokeLabCreature.
        _ShadeSteps("Band Count", Range(1,6)) = 3
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.02
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.25
        _RimColor("Rim Colour", Color) = (1,0.95,0.85,1)
        _RimStrength("Rim Strength", Range(0,3)) = 0.6
        _RimPower("Rim Power", Range(0.5,12)) = 3

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "AlphaTest"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _BumpScale;
            half   _DissolveAmount;
            float  _NoiseScale;
            half   _NoiseWeight;
            float4 _DissolveDirection;
            float  _DissolveSpan;
            half4  _EdgeColor;
            half   _EdgeWidth;
            half4  _EdgeColorInner;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half4  _RimColor;
            half   _RimStrength;
            half   _RimPower;
            half   _Cull;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

        // Returns the dissolve field in 0..1. Zero dissolves first.
        float PokeLabDissolveField(float3 positionOS, float3 positionWS)
        {
            float noise = PL_Fbm(positionWS.xz * _NoiseScale + positionWS.y * _NoiseScale * 0.6);

            float3 dir = normalize(_DissolveDirection.xyz + float3(0, 1e-5, 0));
            float sweep = saturate(dot(positionOS, dir) / max(_DissolveSpan, 1e-3) + 0.5);

            return saturate(lerp(sweep, noise, _NoiseWeight));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DissolveVertex
            #pragma fragment DissolveFragment
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
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
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 positionOS  : TEXCOORD2;
                half3  normalWS    : TEXCOORD3;
                half4  tangentWS   : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DissolveVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.positionOS = input.positionOS.xyz;
                output.normalWS = half3(normIn.normalWS);
                output.tangentWS = half4(normIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.shadowCoord = GetShadowCoord(posIn);
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 DissolveFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;

                float field = PokeLabDissolveField(input.positionOS, input.positionWS);
                float threshold = _DissolveAmount * (1.0 + _EdgeWidth * 2.0);
                float d = field - threshold;
                clip(d + _EdgeWidth);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                half3 geoNormalWS = normalize(input.normalWS);
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                Light mainLight = GetMainLight(input.shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                float rampAA = PL_ToonRampWidth(normalWS, _ShadeSteps);
                half ramp = PL_ToonRamp(dot(normalWS, mainLight.direction),
                                        _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                        svPosition, rampAA) *
                            mainLight.shadowAttenuation;

                half3 colour = lerp(albedo * _ShadeColor.rgb * mainLight.color,
                                    albedo * mainLight.color, ramp);
                colour += PL_Ambient(normalWS) * albedo;

                half rim = PL_Rim(normalWS, viewDirWS, _RimPower, 0.22);
                colour += _RimColor.rgb * rim * _RimStrength;

                // Two-stop emissive edge: an inner white-hot line inside a wider
                // coloured band. One stop reads flat; two reads like it is burning.
                half edge01 = 1.0 - saturate(d / max(_EdgeWidth, 1e-4));
                half inner = saturate((edge01 - 0.6) * 2.5);
                colour += _EdgeColor.rgb * edge01 * edge01;
                colour += _EdgeColorInner.rgb * inner;

                colour = MixFog(colour, input.fogFactor);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DissolveShadowVertex
            #pragma fragment DissolveShadowFragment
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
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DissolveShadowVertex(Attributes input)
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
                output.positionWS = positionWS;
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 DissolveShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                // The shadow has to dissolve with the mesh, or a fully dissolved
                // prop leaves a solid silhouette on the ground.
                float field = PokeLabDissolveField(input.positionOS, input.positionWS);
                clip(field - _DissolveAmount * (1.0 + _EdgeWidth * 2.0) + _EdgeWidth);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DissolveDepthVertex
            #pragma fragment DissolveDepthFragment
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
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DissolveDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 DissolveDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float field = PokeLabDissolveField(input.positionOS, input.positionWS);
                clip(field - _DissolveAmount * (1.0 + _EdgeWidth * 2.0) + _EdgeWidth);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
