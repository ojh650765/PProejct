// -----------------------------------------------------------------------------
// PokeLab/PropGroundBlend
//
// Rocks, trunks, fences, crates. Same toon lighting language as the creature and
// terrain shaders, plus the one thing that stops props looking pasted on: a
// ground blend.
//
// Two blends, both driven from the object pivot so they need no per-prop setup:
//   Contact blend  the lowest _BlendHeight metres of the prop tint towards the
//                  terrain colour, so a boulder sinks into the grass instead of
//                  showing a hard intersection line.
//   Moss blend     upward-facing surfaces pick up the same terrain colour, more
//                  strongly near the base, which reads as growth creeping up.
//
// Vertex colour alpha, if authored, multiplies the contact blend so an artist can
// hand-paint where a prop meets the ground. Unpainted meshes have A = 1 and get
// the automatic height blend, which is the behaviour we want by default.
// -----------------------------------------------------------------------------
Shader "PokeLab/PropGroundBlend"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Colour", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0,2)) = 1

        [Header(Ground Blend)][Space(4)]
        _GroundColor("Ground Colour", Color) = (0.34,0.42,0.22,1)
        _BlendHeight("Contact Blend Height (m)", Float) = 0.35
        _BlendPower("Contact Blend Falloff", Range(0.2,6)) = 2
        _BlendStrength("Contact Blend Strength", Range(0,1)) = 0.75
        _BlendNoiseScale("Contact Blend Noise Scale", Float) = 6
        _BlendNoiseAmount("Contact Blend Noise", Range(0,1)) = 0.5

        [Header(Moss)][Space(4)]
        _MossColor("Moss Colour", Color) = (0.30,0.45,0.20,1)
        _MossStrength("Moss Strength", Range(0,1)) = 0.35
        _MossHeight("Moss Reach (m)", Float) = 1.6
        _MossSharpness("Moss Slope Sharpness", Range(0.2,8)) = 2.5

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.40,0.48,0.66,1)
        _ShadeSteps("Band Count", Range(1,6)) = 3
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.1
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.2
        _ShadowStrength("Cast Shadow Depth", Range(0,1)) = 0.85
        _OcclusionStrength("Vertex Occlusion", Range(0,1)) = 0.6

        [Header(Highlights)][Space(4)]
        _SpecularStrength("Specular Strength", Range(0,2)) = 0.25
        _SpecularSharpness("Specular Sharpness", Range(1,128)) = 26
        _RimColor("Rim Colour", Color) = (1,0.95,0.85,1)
        _RimStrength("Rim Strength", Range(0,3)) = 0.45
        _RimPower("Rim Power", Range(0.5,12)) = 3.5

        [Header(Wetness)][Space(4)]
        _Wetness("Wetness", Range(0,1)) = 0

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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _BumpScale;
            half4  _GroundColor;
            float  _BlendHeight;
            half   _BlendPower;
            half   _BlendStrength;
            float  _BlendNoiseScale;
            half   _BlendNoiseAmount;
            half4  _MossColor;
            half   _MossStrength;
            float  _MossHeight;
            half   _MossSharpness;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half   _ShadowStrength;
            half   _OcclusionStrength;
            half   _SpecularStrength;
            half   _SpecularSharpness;
            half4  _RimColor;
            half   _RimStrength;
            half   _RimPower;
            half   _Wetness;
            half   _Cutoff;
            half   _Cull;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma vertex PropVertex
            #pragma fragment PropFragment
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
                // x = fog factor, y = metres above the object pivot. Packed to stay
                // inside the interpolator budget on the lowest target we compile for.
                float2 fogAndHeight : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings PropVertex(Attributes input)
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
                // Prop pivots sit on the ground by the art contract, so pivot height
                // is the ground plane and no terrain sampling is needed.
                output.fogAndHeight = float2(ComputeFogFactor(posIn.positionCS.z),
                                             posIn.positionWS.y - UNITY_MATRIX_M._m13);
                return output;
            }

            half4 PropFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = baseSample.a * _BaseColor.a;
                #if defined(_ALPHATEST_ON)
                    clip(alpha - _Cutoff);
                #endif

                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                half3 geoNormalWS = normalize(input.normalWS);
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                // --- Contact blend ---------------------------------------------
                // A noisy threshold rather than a clean gradient: a straight fade
                // still reads as a band, a broken one reads as debris and grass
                // growing over the base.
                float heightAbovePivot = input.fogAndHeight.y;
                float h = saturate(heightAbovePivot / max(_BlendHeight, 1e-3));
                float blendNoise = PL_Fbm(input.positionWS.xz * _BlendNoiseScale);
                h = saturate(h + (blendNoise - 0.5) * _BlendNoiseAmount);
                half contact = pow(1.0 - h, _BlendPower) * _BlendStrength;
                // Painted alpha gates the blend where the artist wants it gone.
                contact *= input.colour.a;

                albedo = lerp(albedo, _GroundColor.rgb, contact);
                // Blend the normal towards straight up as well, so the contact zone
                // shades like ground rather than like the side of a rock.
                normalWS = normalize(lerp(normalWS, half3(0, 1, 0), contact * 0.6));

                // --- Moss ------------------------------------------------------
                half upFacing = pow(saturate(geoNormalWS.y), _MossSharpness);
                half mossReach = 1.0 - saturate(heightAbovePivot / max(_MossHeight, 1e-3));
                half moss = upFacing * mossReach * _MossStrength *
                            saturate(blendNoise * 1.6);
                albedo = lerp(albedo, _MossColor.rgb, moss);

                // --- Wetness ---------------------------------------------------
                half wet = saturate(_Wetness + _PL_Wetness);
                half smoothness = 0.1;
                PL_ApplyWetness(albedo, smoothness, normalWS, geoNormalWS, wet);

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                half occlusion = lerp(1.0, input.colour.b, _OcclusionStrength);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(screenUV);
                    occlusion = min(occlusion, aoFactor.indirectAmbientOcclusion);
                #endif

                half4 shadowMask = half4(1, 1, 1, 1);
                Light mainLight = GetMainLight(input.shadowCoord, input.positionWS, shadowMask);

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

                // Wetness raises smoothness inside PL_ApplyWetness; the highlight
                // follows it rather than following the wetness value directly, so a
                // material that is authored glossy already behaves consistently.
                half3 halfVec = SafeNormalize(mainLight.direction + viewDirWS);
                half specPower = lerp(_SpecularSharpness, 200.0, smoothness);
                half spec = pow(saturate(dot(normalWS, halfVec)), specPower);
                colour += mainLight.color * spec * lerp(_SpecularStrength, 1.8, smoothness) * ramp;

                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalizedScreenSpaceUV = screenUV;

                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light addLight = GetAdditionalLight(lightIndex, input.positionWS, shadowMask);
                        half addAtten = addLight.distanceAttenuation * addLight.shadowAttenuation;
                        half addRamp = PL_ToonRamp(dot(normalWS, addLight.direction),
                                                   _ShadeSteps, _ShadeSoftness, _ShadeWrap,
                                                   svPosition, rampAA);
                        colour += albedo * addLight.color * addRamp * addAtten;
                    LIGHT_LOOP_END
                }
                #endif

                colour += PL_Ambient(normalWS) * albedo * occlusion;

                half rim = PL_Rim(normalWS, viewDirWS, _RimPower, 0.22);
                half3 rimColour = _RimColor.rgb + _PL_RimTint.rgb * _PL_RimBoost;
                colour += rimColour * rim * (_RimStrength + _PL_RimBoost) * occlusion;

                colour = MixFog(colour, input.fogFactor);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, alpha);
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
            #pragma vertex PropShadowVertex
            #pragma fragment PropShadowFragment
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

            Varyings PropShadowVertex(Attributes input)
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

            half4 PropShadowFragment(Varyings input) : SV_Target
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

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex PropDepthVertex
            #pragma fragment PropDepthFragment
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

            Varyings PropDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 PropDepthFragment(Varyings input) : SV_Target
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

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex PropDepthNormalsVertex
            #pragma fragment PropDepthNormalsFragment
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

            Varyings PropDepthNormalsVertex(Attributes input)
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

            half4 PropDepthNormalsFragment(Varyings input) : SV_Target
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
