// -----------------------------------------------------------------------------
// PokeLab/VFX/Particle
//
// The workhorse behind almost every effect in the game: sparks, flashes, rings,
// embers, pollen, splashes, dust. One shader, blend mode exposed as a material
// property, so the pooled VFX system can build additive, alpha and premultiplied
// variants from a single source without a keyword explosion.
//
// Features that matter more than they sound:
//   Soft particles   feathers the quad where it meets geometry, so a smoke puff
//                    on the ground stops showing a hard polygon edge.
//   Near fade        fades out as the quad approaches the near plane, so flying
//                    through an effect does not produce a full-screen flash.
//   Alpha erosion    dissolves the texture by threshold rather than fading it
//                    uniformly, so smoke breaks up as it dies instead of ghosting.
//   Distortion       optional refraction of the opaque texture, for heat haze.
//
// Vertex colour is the particle colour and alpha, as Shuriken supplies it.
// Requires Depth Texture on the URP renderer for soft particles, Opaque Texture
// for distortion. Both degrade to "off" rather than to an error.
// -----------------------------------------------------------------------------
Shader "PokeLab/VFX/Particle"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [HDR][MainColor] _BaseColor("Tint (HDR)", Color) = (1,1,1,1)
        [HDR] _EmissionColor("Emission Boost (HDR)", Color) = (0,0,0,0)

        _AlphaCutoff("Alpha Cutoff", Range(0,1)) = 0
        _AlphaErosion("Alpha Erosion Width", Range(0.001,1)) = 0.35

        [Header(Soft and Near Fade)][Space(4)]
        _SoftFadeDistance("Soft Fade Distance (m)", Range(0,6)) = 0.5
        _NearFadeStart("Near Fade Start (m)", Float) = 0.2
        _NearFadeEnd("Near Fade End (m)", Float) = 0.9

        [Header(Distortion)][Space(4)]
        [Normal] _DistortionMap("Distortion Normal", 2D) = "bump" {}
        _DistortionStrength("Distortion Strength", Range(0,0.2)) = 0
        _DistortionSpeed("Distortion Scroll", Vector) = (0.05,0.08,0,0)

        [Header(Scroll)][Space(4)]
        _ScrollSpeed("Base Map Scroll", Vector) = (0,0,0,0)

        [Header(Blending)][Space(4)]
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 1   // One (additive)
        [Enum(Off,0,On,1)] _ZWrite("ZWrite", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4    // LEqual
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0             // Off
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "VfxParticle"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex ParticleVertex
            #pragma fragment ParticleFragment
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            // Set by the VFX driver on additive materials. Soft particles are done
            // by hand below, so multi_compile_particles is deliberately absent.
            #pragma shader_feature_local_fragment _ADDITIVE_FOG

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _EmissionColor;
                half   _AlphaCutoff;
                half   _AlphaErosion;
                float  _SoftFadeDistance;
                float  _NearFadeStart;
                float  _NearFadeEnd;
                float4 _DistortionMap_ST;
                half   _DistortionStrength;
                float4 _DistortionSpeed;
                float4 _ScrollSpeed;
                half   _SrcBlend;
                half   _DstBlend;
                half   _ZWrite;
                half   _ZTest;
                half   _Cull;
            CBUFFER_END

            TEXTURE2D(_BaseMap);       SAMPLER(sampler_BaseMap);
            TEXTURE2D(_DistortionMap); SAMPLER(sampler_DistortionMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4  colour     : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ParticleVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.colour = input.colour;
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 ParticleFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float t = _Time.y;

                float2 uv = input.uv + _ScrollSpeed.xy * t;

                // --- Distortion. Also displaces the base sample, so a heat-haze
                // material can wobble its own texture rather than only the scene. --
                float2 distortion = float2(0, 0);
                if (_DistortionStrength > 1e-5)
                {
                    float2 duv = input.uv * _DistortionMap_ST.xy + _DistortionMap_ST.zw
                               + _DistortionSpeed.xy * t;
                    half3 dn = UnpackNormal(SAMPLE_TEXTURE2D(_DistortionMap, sampler_DistortionMap, duv));
                    distortion = dn.xy * _DistortionStrength;
                }

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv + distortion * 0.35);

                half4 colour = tex * _BaseColor * input.colour;

                // --- Alpha erosion. The particle's own alpha becomes a dissolve
                // threshold against the texture's alpha, so a puff eats itself away
                // from the thin edges inwards instead of fading as a whole. --------
                half threshold = 1.0 - (input.colour.a * _BaseColor.a);
                half eroded = saturate((tex.a - threshold) / max(_AlphaErosion, 1e-3));
                colour.a = tex.a * eroded;

                if (_AlphaCutoff > 1e-5)
                    clip(colour.a - _AlphaCutoff);

                // --- Soft particle fade -------------------------------------------
                float fragmentEyeDepth = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
                if (_SoftFadeDistance > 1e-4)
                    colour.a *= PL_SoftFade(screenUV, fragmentEyeDepth, _SoftFadeDistance);

                // --- Near fade ----------------------------------------------------
                float nearFade = saturate((fragmentEyeDepth - _NearFadeStart) /
                                          max(_NearFadeEnd - _NearFadeStart, 1e-3));
                colour.a *= nearFade;

                // --- Emission boost. Kept separate from the tint so the VFX driver
                // can push an effect into bloom without washing out its colour. ----
                colour.rgb += _EmissionColor.rgb * colour.a;

                // Refract the scene behind the particle. Only meaningful for the
                // alpha-blended heat-haze materials; additive ones leave it at zero.
                if (_DistortionStrength > 1e-5)
                {
                    half3 scene = half3(SampleSceneColor(screenUV + distortion));
                    colour.rgb = lerp(scene, colour.rgb, saturate(colour.a));
                }

                // Additive materials must fog towards black, not towards the fog
                // colour, or every effect turns into a bright patch of fog.
                #if defined(_ADDITIVE_FOG)
                    colour.rgb = MixFogColor(colour.rgb, half3(0, 0, 0), input.fogFactor);
                #else
                    colour.rgb = MixFog(colour.rgb, input.fogFactor);
                #endif

                colour.rgb += PL_Dither(svPosition, 1.0 / 255.0);
                return colour;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
