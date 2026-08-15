// -----------------------------------------------------------------------------
// PokeLab/Water
//
// The hero surface. It gets close screen time at the lakeside, so it does the
// full set: gerstner-ish vertex waves with analytic normals, two scrolling normal
// layers, a depth-driven shallow-to-deep colour ramp, refraction through the
// opaque texture, depth-difference foam that hugs shorelines and rocks, caustics
// on the shallow bottom, sun sparkle, and a stylised sky fresnel.
//
// REQUIRES on the URP renderer asset:
//   Depth Texture  ON   (foam, refraction clamp, shallow ramp)
//   Opaque Texture ON   (refraction)
// Without them the shader still compiles and renders, but flat: the depth reads
// return the far plane, so it degrades to "deep water everywhere" rather than to
// a pink error material.
// -----------------------------------------------------------------------------
Shader "PokeLab/Water"
{
    Properties
    {
        [Header(Colour)][Space(4)]
        _ShallowColor("Shallow Colour", Color) = (0.32,0.78,0.76,1)
        _DeepColor("Deep Colour", Color) = (0.03,0.18,0.32,1)
        _DepthRange("Depth Fade Range (m)", Float) = 3.5
        _HorizonColor("Grazing Tint", Color) = (0.55,0.86,0.92,1)
        _Transparency("Surface Transparency", Range(0,1)) = 0.86

        [Header(Normals)][Space(4)]
        [Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalScaleA("Normal Layer A Tiling", Float) = 0.14
        _NormalScaleB("Normal Layer B Tiling", Float) = 0.37
        _NormalSpeedA("Normal Layer A Speed", Vector) = (0.021,0.013,0,0)
        _NormalSpeedB("Normal Layer B Speed", Vector) = (-0.015,0.024,0,0)
        _NormalStrength("Normal Strength", Range(0,3)) = 0.85

        [Header(Waves)][Space(4)]
        _WaveAmplitude("Wave Amplitude (m)", Range(0,1)) = 0.055
        _WaveLength("Wave Length (m)", Float) = 4.2
        _WaveSpeed("Wave Speed", Range(0,4)) = 0.65
        _WaveSteepness("Wave Steepness", Range(0,1)) = 0.35
        _WaveDirection("Primary Wave Direction", Vector) = (1,0.35,0,0)

        [Header(Refraction)][Space(4)]
        _RefractionStrength("Refraction Strength", Range(0,0.2)) = 0.035
        _RefractionTint("Underwater Tint", Color) = (0.62,0.92,0.88,1)

        [Header(Foam)][Space(4)]
        _FoamColor("Foam Colour", Color) = (1,1,1,1)
        _FoamDepth("Foam Depth (m)", Range(0.01,3)) = 0.42
        _FoamSharpness("Foam Sharpness", Range(0.2,6)) = 1.8
        _FoamScale("Foam Noise Scale", Float) = 2.2
        _FoamSpeed("Foam Scroll Speed", Range(0,2)) = 0.28
        _FoamCrest("Wave Crest Foam", Range(0,2)) = 0.55
        _ShoreBandSpeed("Shore Lapping Speed", Range(0,4)) = 0.9

        [Header(Caustics)][Space(4)]
        _CausticColor("Caustic Colour", Color) = (0.85,1.0,0.92,1)
        _CausticStrength("Caustic Strength", Range(0,4)) = 1.1
        _CausticScale("Caustic Scale", Float) = 1.6
        _CausticSpeed("Caustic Speed", Range(0,2)) = 0.32
        _CausticDepthRange("Caustic Depth Range (m)", Float) = 2.2

        [Header(Specular)][Space(4)]
        _SpecularColor("Specular Colour", Color) = (1,0.97,0.88,1)
        _SpecularSharpness("Specular Sharpness", Range(8,2048)) = 420
        _SpecularStrength("Specular Strength", Range(0,8)) = 2.6
        _SparkleScale("Sparkle Scale", Float) = 22
        _SparkleStrength("Sparkle Strength", Range(0,6)) = 1.6
        _SparkleThreshold("Sparkle Threshold", Range(0.5,0.999)) = 0.93

        [Header(Sky)][Space(4)]
        _FresnelPower("Fresnel Power", Range(0.5,8)) = 4.2
        _ReflectionStrength("Sky Reflection Strength", Range(0,3)) = 1.1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-100"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma target 3.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;
                half4  _DeepColor;
                float  _DepthRange;
                half4  _HorizonColor;
                half   _Transparency;
                float4 _NormalMap_ST;
                float  _NormalScaleA;
                float  _NormalScaleB;
                float4 _NormalSpeedA;
                float4 _NormalSpeedB;
                half   _NormalStrength;
                float  _WaveAmplitude;
                float  _WaveLength;
                float  _WaveSpeed;
                float  _WaveSteepness;
                float4 _WaveDirection;
                half   _RefractionStrength;
                half4  _RefractionTint;
                half4  _FoamColor;
                float  _FoamDepth;
                half   _FoamSharpness;
                float  _FoamScale;
                float  _FoamSpeed;
                half   _FoamCrest;
                float  _ShoreBandSpeed;
                half4  _CausticColor;
                half   _CausticStrength;
                float  _CausticScale;
                float  _CausticSpeed;
                float  _CausticDepthRange;
                half4  _SpecularColor;
                half   _SpecularSharpness;
                half   _SpecularStrength;
                float  _SparkleScale;
                half   _SparkleStrength;
                half   _SparkleThreshold;
                half   _FresnelPower;
                half   _ReflectionStrength;
            CBUFFER_END

            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                half3  waveNormal  : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
                half   crest       : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Three sine layers at decorrelated angles. Genuine Gerstner needs the
            // horizontal pinch too, which _WaveSteepness supplies below; the sum of
            // three is enough to stop the surface reading as one repeating swell.
            float PokeLabWaveHeight(float2 p, float t)
            {
                float2 d0 = normalize(_WaveDirection.xy + float2(1e-4, 0));
                float2 d1 = normalize(float2(-d0.y, d0.x) * 0.8 + d0 * 0.6);
                float2 d2 = normalize(d0 * 0.3 - float2(-d0.y, d0.x) * 1.1);

                float k0 = 6.2831853 / max(_WaveLength, 0.05);
                float k1 = k0 * 1.7;
                float k2 = k0 * 2.9;

                float h = 0;
                h += sin(dot(p, d0) * k0 + t * _WaveSpeed * 1.00) * 1.00;
                h += sin(dot(p, d1) * k1 + t * _WaveSpeed * 1.43) * 0.55;
                h += sin(dot(p, d2) * k2 + t * _WaveSpeed * 0.81) * 0.28;
                return h * _WaveAmplitude;
            }

            Varyings WaterVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float t = _Time.y;

                float h = PokeLabWaveHeight(positionWS.xz, t);
                positionWS.y += h;

                // Analytic-ish normal by central difference. One metre of epsilon
                // would smooth the wave away; 0.25 m tracks the shortest layer.
                const float e = 0.25;
                float hx = PokeLabWaveHeight(positionWS.xz + float2(e, 0), t);
                float hz = PokeLabWaveHeight(positionWS.xz + float2(0, e), t);
                float3 waveNormal = normalize(float3(-(hx - h) / e, 1.0, -(hz - h) / e));

                // Gerstner pinch: pull vertices towards the crest so peaks sharpen
                // and troughs widen instead of staying a pure sine.
                float2 slope = float2(hx - h, hz - h) / e;
                positionWS.xz -= slope * _WaveSteepness * _WaveLength * 0.12;

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.waveNormal = half3(waveNormal);
                output.shadowCoord = TransformWorldToShadowCoord(positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                // Normalised crest height, used to foam the tops of waves.
                output.crest = half(saturate(h / max(_WaveAmplitude * 1.6, 1e-4) * 0.5 + 0.5));
                return output;
            }

            // Two rotated noise layers multiplied together read as caustic filaments
            // far more convincingly than a single layer, for the same two samples.
            float PokeLabCaustic(float2 p, float t)
            {
                float a = PL_ValueNoise(p * _CausticScale + float2(t * _CausticSpeed, 0));
                float b = PL_ValueNoise(p * _CausticScale * 1.31 -
                                        float2(0, t * _CausticSpeed * 0.87) + 13.7);
                float c = 1.0 - abs(a - b) * 2.4;
                return pow(saturate(c), 6.0);
            }

            half4 WaterFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float3 positionWS = input.positionWS;
                float t = _Time.y;

                // --- Surface normal ---------------------------------------------
                float2 uvA = positionWS.xz * _NormalScaleA + _NormalSpeedA.xy * t;
                float2 uvB = positionWS.xz * _NormalScaleB + _NormalSpeedB.xy * t;

                half3 nA = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvA), _NormalStrength);
                half3 nB = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvB), _NormalStrength * 0.7);
                half3 detailTS = normalize(half3(nA.xy + nB.xy, nA.z * nB.z));

                // The wave normal is already world space and the plane is horizontal,
                // so tangent space here is simply (X, Z, Y) with Y up.
                half3 waveNormal = normalize(input.waveNormal);
                half3 normalWS = normalize(half3(waveNormal.x + detailTS.x,
                                                 waveNormal.y,
                                                 waveNormal.z + detailTS.y));

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(positionWS));

                // --- Depth reads -------------------------------------------------
                float fragmentEyeDepth = LinearEyeDepth(positionWS, GetWorldToViewMatrix());
                float sceneEyeDepth = PL_SceneEyeDepth(screenUV);
                float waterDepth = max(sceneEyeDepth - fragmentEyeDepth, 0.0);

                // --- Refraction. Offset the opaque sample by the surface normal,
                // then reject the sample if it pulled in geometry that is actually
                // in front of the water: that is what stops objects at the shoreline
                // smearing into the lake. -----------------------------------------
                float2 refractOffset = normalWS.xz * _RefractionStrength *
                                       saturate(waterDepth * 0.5);
                float2 refractUV = screenUV + refractOffset;

                float refractedSceneDepth = PL_SceneEyeDepth(refractUV);
                float refractedWaterDepth = refractedSceneDepth - fragmentEyeDepth;
                // Fall back to the unrefracted sample where the offset would bleed.
                refractUV = (refractedWaterDepth < 0.0) ? screenUV : refractUV;
                float usedDepth = (refractedWaterDepth < 0.0) ? waterDepth : refractedWaterDepth;

                half3 sceneColour = half3(SampleSceneColor(refractUV));

                // --- Depth colour ramp --------------------------------------------
                half depth01 = saturate(usedDepth / max(_DepthRange, 1e-3));
                // Dither before quantisation: a wide water gradient is exactly where
                // 8-bit banding shows, and it is called out in the QA brief.
                depth01 = saturate(depth01 + PL_Dither(svPosition, 2.5 / 255.0));
                half3 waterColour = lerp(_ShallowColor.rgb, _DeepColor.rgb, depth01);

                // --- Caustics on the shallow bottom, seen through the water --------
                half causticFade = 1.0 - saturate(usedDepth / max(_CausticDepthRange, 1e-3));
                float caustic = PokeLabCaustic(positionWS.xz, t) * causticFade;
                sceneColour *= _RefractionTint.rgb;
                sceneColour += _CausticColor.rgb * caustic * _CausticStrength;

                // Transmission: shallow water shows the bottom, deep water does not.
                half transmission = 1.0 - depth01;
                half3 colour = lerp(waterColour, lerp(waterColour, sceneColour, 0.85),
                                    transmission * _Transparency);

                // --- Foam ----------------------------------------------------------
                // Depth difference is what makes foam hug a shoreline and every rock
                // poking through, without anyone painting a mask.
                half shoreline = 1.0 - saturate(waterDepth / max(_FoamDepth, 1e-3));
                shoreline = pow(shoreline, _FoamSharpness);

                // Two scrolling noise layers give the band a moving, broken edge.
                float foamN = PL_Fbm(positionWS.xz * _FoamScale + float2(t, -t) * _FoamSpeed);
                float foamN2 = PL_Fbm(positionWS.xz * _FoamScale * 1.9 -
                                      float2(t * 0.6, t * 0.9) * _FoamSpeed);
                float foamNoise = saturate(foamN * 0.65 + foamN2 * 0.35);

                // A slow band travelling in from the shore reads as water lapping.
                float lap = sin(shoreline * 9.0 - t * _ShoreBandSpeed) * 0.5 + 0.5;

                half foam = saturate(shoreline * (foamNoise * 0.7 + 0.3) * (0.55 + 0.45 * lap));
                // Crest foam: whitecaps on the tops of the vertex waves.
                foam = max(foam, saturate((input.crest - 0.72) * 5.0) * foamNoise * _FoamCrest);
                foam = saturate(foam);

                // --- Lighting -------------------------------------------------------
                half4 shadowMask = half4(1, 1, 1, 1);
                Light mainLight = GetMainLight(input.shadowCoord, positionWS, shadowMask);
                half shadow = lerp(0.55, 1.0, mainLight.shadowAttenuation);

                // Sun glint. Very high exponent plus a sparkle mask so it reads as
                // broken glitter across the surface rather than one mirror blob.
                half3 halfVec = SafeNormalize(mainLight.direction + viewDirWS);
                half spec = pow(saturate(dot(normalWS, halfVec)), _SpecularSharpness);
                half3 sunTint = _SpecularColor.rgb + _PL_SunColor.rgb;

                float sparkleN = PL_ValueNoise(positionWS.xz * _SparkleScale + t * 0.7);
                float sparkleN2 = PL_ValueNoise(positionWS.xz * _SparkleScale * 1.6 - t * 0.53);
                half sparkleMask = step(_SparkleThreshold, sparkleN * sparkleN2 * 2.2);
                half glint = pow(saturate(dot(normalWS, halfVec)), 90.0) * sparkleMask;

                colour += mainLight.color * sunTint * spec * _SpecularStrength * shadow;
                colour += mainLight.color * sunTint * glint * _SparkleStrength * shadow;

                // --- Sky fresnel ----------------------------------------------------
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), _FresnelPower);
                half3 skyColour = PL_Ambient(reflect(-viewDirWS, normalWS));
                colour = lerp(colour, colour + skyColour * _ReflectionStrength, fresnel);
                colour += _HorizonColor.rgb * fresnel * 0.25;

                // Foam sits on top of everything, lit only by ambient plus the key,
                // so it stays white without blowing out under the battle grade.
                half3 foamColour = _FoamColor.rgb * (mainLight.color * shadow * 0.7 +
                                                     PL_Ambient(half3(0, 1, 0)) * 0.6);
                colour = lerp(colour, foamColour, foam);

                // --- Alpha. Opaque at the shore so the mesh edge never shows, and
                // opaque under foam so foam is not see-through. ---------------------
                half alpha = lerp(0.35, 1.0, saturate(waterDepth / max(_FoamDepth * 1.5, 1e-3)));
                alpha = max(alpha, foam);
                alpha = saturate(alpha);

                colour = MixFog(colour, input.fogFactor);
                colour += PL_AdaptiveDither(svPosition, 1.5 / 255.0);

                return half4(colour, alpha);
            }
            ENDHLSL
        }

        // No ShadowCaster, DepthOnly or DepthNormals pass by design. The surface
        // reads the depth and opaque textures it would otherwise have written to,
        // so contributing to either would make it refract and foam against itself.
    }

    FallBack "Universal Render Pipeline/Unlit"
}
