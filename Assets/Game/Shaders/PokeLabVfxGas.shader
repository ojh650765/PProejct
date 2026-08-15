// -----------------------------------------------------------------------------
// PokeLab/VFX/Gas
//
// Smoke, steam, poison clouds, waterfall mist, campfire smoke and dust. The one
// thing that separates convincing smoke from a grey blob is that it is lit: it
// picks up the key light on one side and the sky on the other, and it changes
// colour through the day along with everything else.
//
// There is no real volume here. The trick is to synthesise a hemisphere normal
// from the quad's UV — a billboard shaded as if it were a sphere reads as a puff
// from every angle, and costs one normalize.
//
// Feeds off the same PL_Ambient gradient as the terrain and creatures, so a smoke
// column at dusk goes orange on the sun side and blue in shadow without anyone
// animating a gradient.
// -----------------------------------------------------------------------------
Shader "PokeLab/VFX/Gas"
{
    Properties
    {
        [MainTexture] _BaseMap("Smoke Map (A = density)", 2D) = "white" {}
        [MainColor]   _BaseColor("Tint", Color) = (1,1,1,1)
        _ShadeColor("Shade Colour", Color) = (0.28,0.30,0.38,1)

        [Header(Lighting)][Space(4)]
        _LightWrap("Light Wrap", Range(0,1)) = 0.6
        _LightStrength("Key Light Strength", Range(0,3)) = 1.1
        _AmbientStrength("Ambient Strength", Range(0,3)) = 1.0
        _Sphericity("Sphere Normal Strength", Range(0,1)) = 0.85
        _RimGlow("Backlit Rim Glow", Range(0,4)) = 0.9

        [Header(Density)][Space(4)]
        _Density("Density", Range(0,4)) = 1.2
        _AlphaErosion("Alpha Erosion Width", Range(0.001,1)) = 0.4
        _DetailMap("Detail Noise", 2D) = "gray" {}
        _DetailStrength("Detail Strength", Range(0,1)) = 0.35
        _DetailScroll("Detail Scroll", Vector) = (0.02,0.05,0,0)

        [Header(Fade)][Space(4)]
        _SoftFadeDistance("Soft Fade Distance (m)", Range(0,8)) = 1.2
        _NearFadeStart("Near Fade Start (m)", Float) = 0.3
        _NearFadeEnd("Near Fade End (m)", Float) = 1.2

        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
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
            Name "VfxGas"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex GasVertex
            #pragma fragment GasFragment
            #pragma target 3.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _ShadeColor;
                half   _LightWrap;
                half   _LightStrength;
                half   _AmbientStrength;
                half   _Sphericity;
                half   _RimGlow;
                half   _Density;
                half   _AlphaErosion;
                float4 _DetailMap_ST;
                half   _DetailStrength;
                float4 _DetailScroll;
                float  _SoftFadeDistance;
                float  _NearFadeStart;
                float  _NearFadeEnd;
                half   _Cull;
            CBUFFER_END

            TEXTURE2D(_BaseMap);   SAMPLER(sampler_BaseMap);
            TEXTURE2D(_DetailMap); SAMPLER(sampler_DetailMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float2 rawUv       : TEXCOORD1;
                half4  colour      : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                float  fogFactor   : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings GasVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.rawUv = input.uv;
                output.colour = input.colour;
                output.shadowCoord = TransformWorldToShadowCoord(posIn.positionWS);
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 GasFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float t = _Time.y;

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                // Detail noise breaks up the silhouette so several overlapping puffs
                // do not read as several identical stamps.
                half detail = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap,
                                               input.uv * _DetailMap_ST.xy + _DetailMap_ST.zw
                                               + _DetailScroll.xy * t).r;
                half density = tex.a * lerp(1.0, detail * 2.0, _DetailStrength);

                // Erosion against the particle's own alpha, so a dying puff breaks
                // apart at the thin edges instead of ghosting out uniformly.
                half threshold = 1.0 - input.colour.a;
                half alpha = saturate((density - threshold) / max(_AlphaErosion, 1e-3));
                alpha = saturate(alpha * _Density);
                if (alpha < 0.002) discard;

                // --- Synthetic sphere normal -----------------------------------
                // Map the quad UV to a hemisphere. Billboards face the camera, so a
                // camera-space normal rotated into world space is what we want.
                float2 c = input.rawUv * 2.0 - 1.0;
                float r2 = saturate(dot(c, c));
                float3 normalVS = normalize(float3(c, sqrt(max(1.0 - r2, 1e-4))));
                float3 flatVS = float3(0, 0, 1);
                normalVS = normalize(lerp(flatVS, normalVS, _Sphericity));

                // View to world. The inverse view matrix's upper 3x3 is a rotation.
                float3 normalWS = normalize(mul((float3x3)UNITY_MATRIX_I_V, normalVS));
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                // --- Lighting ---------------------------------------------------
                Light mainLight = GetMainLight(input.shadowCoord, input.positionWS, half4(1, 1, 1, 1));

                // Wrapped diffuse: smoke scatters, so it is never fully dark on the
                // shadow side and never has a hard terminator.
                half ndotl = dot(normalWS, mainLight.direction);
                half wrapped = saturate(lerp(saturate(ndotl), ndotl * 0.5 + 0.5, _LightWrap));
                wrapped *= lerp(0.4, 1.0, mainLight.shadowAttenuation);

                half3 tint = _BaseColor.rgb * input.colour.rgb * tex.rgb;
                half3 lit = tint * mainLight.color * _LightStrength;
                half3 shade = tint * _ShadeColor.rgb;
                half3 colour = lerp(shade, lit, wrapped);

                colour += PL_Ambient(normalWS) * tint * _AmbientStrength;

                // Backlit rim: light passing through the edge of a puff. This is the
                // detail that sells a smoke column against a low sun.
                half backlit = pow(saturate(dot(viewDirWS, -mainLight.direction)), 3.0);
                half edge = saturate(1.0 - alpha) * saturate(density * 2.0);
                colour += mainLight.color * tint * backlit * edge * _RimGlow;

                // --- Fades -------------------------------------------------------
                float fragmentEyeDepth = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
                if (_SoftFadeDistance > 1e-4)
                    alpha *= PL_SoftFade(screenUV, fragmentEyeDepth, _SoftFadeDistance);

                alpha *= saturate((fragmentEyeDepth - _NearFadeStart) /
                                  max(_NearFadeEnd - _NearFadeStart, 1e-3));

                colour = MixFog(colour, input.fogFactor);
                colour += PL_Dither(svPosition, 1.5 / 255.0);

                return half4(colour, saturate(alpha));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
