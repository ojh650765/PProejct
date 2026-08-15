// -----------------------------------------------------------------------------
// PokeLab/Foliage
//
// Grass, bushes, tree canopies and flowers. Two-sided, alpha clipped, wind
// animated, with translucency so backlit leaves glow the way they do in the
// reference games.
//
// Vertex colour convention, agreed with the environment artist:
//   R = sway mask     0 at the anchored base, 1 at the free tip
//   G = phase offset  decorrelates neighbouring cards so a hedge does not pulse
//   B = ambient occlusion (darkens the interior of a canopy)
//   A = unused
//
// The wind displacement lives in one function used by every pass, so the shadow
// a bush casts keeps up with the bush. Amplitude fades out with view distance:
// sub-pixel motion at range only produces shimmer, which the brief calls out.
// -----------------------------------------------------------------------------
Shader "PokeLab/Foliage"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Colour", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0,2)) = 0.7

        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.45

        [Header(Colour Variation)][Space(4)]
        _TipColor("Tip Colour", Color) = (0.72,0.88,0.42,1)
        _RootColor("Root Colour", Color) = (0.16,0.32,0.18,1)
        _GradientPower("Root to Tip Falloff", Range(0.2,4)) = 1
        _HueVariation("Per Instance Hue Variation", Color) = (0.85,1.0,0.7,1)
        _VariationStrength("Variation Strength", Range(0,1)) = 0.35

        [Header(Toon Ramp)][Space(4)]
        _ShadeColor("Shadow Tint", Color) = (0.38,0.48,0.55,1)
        _ShadeSteps("Band Count", Range(1,6)) = 2
        _ShadeSoftness("Band Softness", Range(0,0.6)) = 0.12
        _ShadeWrap("Light Wrap", Range(0,1)) = 0.45
        _ShadowStrength("Cast Shadow Depth", Range(0,1)) = 0.65
        _OcclusionStrength("Vertex Occlusion", Range(0,1)) = 0.8

        [Header(Translucency)][Space(4)]
        _TranslucencyColor("Translucency Colour", Color) = (0.55,0.95,0.30,1)
        _TranslucencyStrength("Translucency Strength", Range(0,4)) = 1.6
        _TranslucencyPower("Translucency Falloff", Range(1,16)) = 5

        [Header(Specular and Rim)][Space(4)]
        _SpecularStrength("Sheen Strength", Range(0,2)) = 0.35
        _SpecularSharpness("Sheen Sharpness", Range(1,128)) = 24
        _RimStrength("Rim Strength", Range(0,3)) = 0.5
        _RimPower("Rim Power", Range(0.5,12)) = 3

        [Header(Wind)][Space(4)]
        _SwayAmplitude("Sway Amplitude (m)", Range(0,1.5)) = 0.16
        _SwaySpeed("Sway Speed", Range(0,6)) = 0.9
        _FlutterAmplitude("Flutter Amplitude (m)", Range(0,0.5)) = 0.045
        _FlutterSpeed("Flutter Speed", Range(0,20)) = 6.5
        _GustStrength("Gust Strength", Range(0,3)) = 0.9
        _WindDistanceFade("Wind Fade Distance (m)", Float) = 45

        [Header(Normals)][Space(4)]
        _NormalSpherify("Spherify Normals", Range(0,1)) = 0.45

        [Header(Surface)][Space(4)]
        [Toggle] _AlphaToMask("Alpha To Coverage", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _BumpScale;
            half   _Cutoff;
            half4  _TipColor;
            half4  _RootColor;
            half   _GradientPower;
            half4  _HueVariation;
            half   _VariationStrength;
            half4  _ShadeColor;
            half   _ShadeSteps;
            half   _ShadeSoftness;
            half   _ShadeWrap;
            half   _ShadowStrength;
            half   _OcclusionStrength;
            half4  _TranslucencyColor;
            half   _TranslucencyStrength;
            half   _TranslucencyPower;
            half   _SpecularStrength;
            half   _SpecularSharpness;
            half   _RimStrength;
            half   _RimPower;
            half   _SwayAmplitude;
            half   _SwaySpeed;
            half   _FlutterAmplitude;
            half   _FlutterSpeed;
            half   _GustStrength;
            float  _WindDistanceFade;
            half   _NormalSpherify;
            half   _AlphaToMask;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

        #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

        // One wind function, used identically by the lit, shadow, depth and
        // depth-normals passes. If these ever disagree the shadow slides off the
        // plant, which is instantly obvious and hard to trace.
        float3 PokeLabFoliageDisplace(float3 positionWS, float4 vertexColour)
        {
            return PL_FoliageWind(positionWS, vertexColour,
                                  _SwayAmplitude, _SwaySpeed,
                                  _FlutterAmplitude, _FlutterSpeed,
                                  _GustStrength, _WindDistanceFade);
        }
        ENDHLSL

        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma vertex FoliageVertex
            #pragma fragment FoliageFragment
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
                // x = fog factor, y = per-instance variation. Packed to stay inside
                // the interpolator budget on the lowest target we compile for.
                float2 fogAndVariation : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings FoliageVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PokeLabFoliageDisplace(positionWS, input.colour);

                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = half3(normIn.normalWS);
                output.tangentWS = half4(normIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.colour = half4(input.colour);
                output.shadowCoord = TransformWorldToShadowCoord(positionWS);
                output.fogAndVariation = float2(ComputeFogFactor(output.positionCS.z),
                                                PL_InstanceSeed());
                return output;
            }

            half4 FoliageFragment(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = baseSample.a * _BaseColor.a;
                clip(alpha - _Cutoff);

                // Root-to-tip gradient. Cheap way to get depth into a flat card and
                // to stop a whole meadow reading as one shade of green.
                half gradient = pow(saturate(input.colour.r), _GradientPower);
                half3 tint = lerp(_RootColor.rgb, _TipColor.rgb, gradient);
                half3 albedo = baseSample.rgb * _BaseColor.rgb * tint;

                // Per-instance hue shift, so adjacent bushes are not clones.
                half variation = half(input.fogAndVariation.y);
                half3 varTint = lerp(half3(1, 1, 1), _HueVariation.rgb, variation);
                albedo = lerp(albedo, albedo * varTint, _VariationStrength);

                // --- Normals ---------------------------------------------------
                half3 geoNormalWS = normalize(input.normalWS);
                // Flip on backfaces, so the underside of a leaf lights rather than
                // going black. IS_FRONT_VFACE hides the D3D/GL semantic difference.
                geoNormalWS *= IS_FRONT_VFACE(facing, 1.0, -1.0);

                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w * cross(geoNormalWS, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangentWS, geoNormalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                // Spherify: bend the shading normal towards the canopy's outward
                // direction so a cluster of flat cards lights like a volume.
                float3 outward = normalize(input.positionWS -
                                           float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23) +
                                           float3(0, 1e-4, 0));
                normalWS = normalize(lerp(normalWS, half3(outward), _NormalSpherify));

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                half occlusion = lerp(1.0, input.colour.b, _OcclusionStrength);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(screenUV);
                    occlusion = min(occlusion, aoFactor.indirectAmbientOcclusion);
                #endif

                // --- Main light ------------------------------------------------
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

                // --- Translucency. The whole reason foliage reads as alive: light
                // coming from behind the leaf passes through it towards the eye. --
                half through = pow(saturate(dot(viewDirWS, -mainLight.direction)), _TranslucencyPower);
                // Thin parts transmit more. The sway mask doubles as a thickness
                // proxy: tips are thin, the woody base is not.
                half thickness = saturate(input.colour.r * 0.7 + 0.3);
                half3 translucency = _TranslucencyColor.rgb * mainLight.color * albedo *
                                     through * thickness * _TranslucencyStrength *
                                     mainLight.shadowAttenuation;
                colour += translucency;

                // --- Sheen -----------------------------------------------------
                half3 halfVec = SafeNormalize(mainLight.direction + viewDirWS);
                half sheen = pow(saturate(dot(normalWS, halfVec)), _SpecularSharpness);
                colour += mainLight.color * sheen * _SpecularStrength * ramp;

                // --- Additional lights -----------------------------------------
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

                // --- Ambient and rim -------------------------------------------
                colour += PL_Ambient(normalWS) * albedo * occlusion;

                half rim = PL_Rim(normalWS, viewDirWS, _RimPower, 0.2);
                half3 rimColour = _TipColor.rgb + _PL_RimTint.rgb * _PL_RimBoost;
                colour += rimColour * rim * (_RimStrength + _PL_RimBoost) * occlusion;

                colour = MixFog(colour, input.fogAndVariation.x);
                colour += PL_AdaptiveDither(svPosition, 1.0 / 255.0);

                return half4(colour, alpha);
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex FoliageShadowVertex
            #pragma fragment FoliageShadowFragment
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
                float2 uv         : TEXCOORD0;
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings FoliageShadowVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PokeLabFoliageDisplace(positionWS, input.colour);
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

            half4 FoliageShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex FoliageDepthVertex
            #pragma fragment FoliageDepthFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings FoliageDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PokeLabFoliageDisplace(positionWS, input.colour);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 FoliageDepthFragment(Varyings input) : SV_Target
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
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex FoliageDepthNormalsVertex
            #pragma fragment FoliageDepthNormalsFragment
            #pragma target 3.0
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
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings FoliageDepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PokeLabFoliageDisplace(positionWS, input.colour);
                output.positionCS = TransformWorldToHClip(positionWS);

                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = half3(NormalizeNormalPerVertex(normIn.normalWS));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 FoliageDepthNormalsFragment(Varyings input,
                                              FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                normalWS *= IS_FRONT_VFACE(facing, 1.0, -1.0);
                return half4(normalWS, 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
