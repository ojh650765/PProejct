// -----------------------------------------------------------------------------
// PokeLab/VFX/ForceField
//
// Protect, Light Screen, Reflect, the capture ball's containment bubble, and the
// shield flash when an ability blocks a move.
//
// Three cues stacked, all cheap:
//   Fresnel      the shell is invisible face-on and solid at grazing angles, so
//                it reads as a surface without hiding the creature inside it.
//   Intersection where the shell cuts through geometry it glows, using the depth
//                difference. This is what makes it feel like it is really there.
//   Pattern      a scrolling hex lattice, brightest where the shell was recently
//                struck (_ImpactPoint / _ImpactTime, written by the VFX driver).
// -----------------------------------------------------------------------------
Shader "PokeLab/VFX/ForceField"
{
    Properties
    {
        [HDR] _BaseColor("Shell Colour", Color) = (0.35,0.75,1.0,1)
        [HDR] _RimColor("Rim Colour", Color) = (0.6,0.95,1.4,1)
        _FresnelPower("Fresnel Power", Range(0.3,10)) = 2.6
        _BaseAlpha("Base Alpha", Range(0,1)) = 0.08
        _RimAlpha("Rim Alpha", Range(0,2)) = 0.9

        [Header(Pattern)][Space(4)]
        [HDR] _PatternColor("Pattern Colour", Color) = (0.7,1.0,1.4,1)
        _PatternScale("Hex Scale", Float) = 8
        _PatternWidth("Hex Line Width", Range(0.005,0.3)) = 0.045
        _PatternStrength("Pattern Strength", Range(0,3)) = 0.8
        _PatternScroll("Pattern Scroll", Vector) = (0.03,0.05,0,0)

        [Header(Intersection)][Space(4)]
        [HDR] _IntersectColor("Intersection Colour", Color) = (1.4,1.9,2.4,1)
        _IntersectDepth("Intersection Depth (m)", Range(0.01,2)) = 0.28
        _IntersectStrength("Intersection Strength", Range(0,4)) = 1.6

        [Header(Impact Ripple)][Space(4)]
        _ImpactPoint("Impact Point (object space)", Vector) = (0,0,0,0)
        _ImpactTime("Seconds Since Impact", Float) = 999
        _ImpactSpeed("Ripple Speed", Float) = 3.5
        _ImpactWidth("Ripple Width", Range(0.02,2)) = 0.35
        [HDR] _ImpactColor("Ripple Colour", Color) = (2.2,2.6,3.0,1)

        [Header(Dissolve)][Space(4)]
        _Opacity("Global Opacity", Range(0,1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+10"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForceField"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShieldVertex
            #pragma fragment ShieldFragment
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _BaseColor;
                half4  _RimColor;
                half   _FresnelPower;
                half   _BaseAlpha;
                half   _RimAlpha;
                half4  _PatternColor;
                float  _PatternScale;
                half   _PatternWidth;
                half   _PatternStrength;
                float4 _PatternScroll;
                half4  _IntersectColor;
                float  _IntersectDepth;
                half   _IntersectStrength;
                float4 _ImpactPoint;
                float  _ImpactTime;
                float  _ImpactSpeed;
                float  _ImpactWidth;
                half4  _ImpactColor;
                half   _Opacity;
            CBUFFER_END

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
                float3 positionOS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half3  normalWS   : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShieldVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normIn = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.positionOS = input.positionOS.xyz;
                output.normalWS = half3(normIn.normalWS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            // Distance to the nearest edge of a hex cell, in cell units.
            // Standard axial hex fold: two offset rectangular lattices.
            float HexEdgeDistance(float2 p)
            {
                const float2 s = float2(1.0, 1.7320508);   // 1, sqrt(3)
                float2 a = fmod(p, s) - s * 0.5;
                float2 b = fmod(p + s * 0.5, s) - s * 0.5;
                float2 g = dot(a, a) < dot(b, b) ? a : b;

                // Distance to the hexagon boundary from its centre.
                float2 q = abs(g);
                return 0.5 - max(dot(q, float2(0.8660254, 0.5)), q.x);
            }

            half4 ShieldFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float t = _Time.y;

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                // A shell is drawn two-sided; flip the normal so the far side
                // produces the same fresnel as the near side instead of inverting.
                normalWS *= sign(dot(normalWS, viewDirWS) + 1e-4);

                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), _FresnelPower);

                half3 colour = _BaseColor.rgb * _BaseAlpha;
                half alpha = _BaseAlpha;

                colour += _RimColor.rgb * fresnel * _RimAlpha;
                alpha += fresnel * _RimAlpha;

                // --- Hex lattice -------------------------------------------------
                float2 patternUV = input.uv * _PatternScale + _PatternScroll.xy * t;
                float edge = HexEdgeDistance(patternUV);
                float aa = fwidth(edge) + 1e-4;
                half hex = 1.0 - smoothstep(_PatternWidth - aa, _PatternWidth + aa, edge);
                colour += _PatternColor.rgb * hex * _PatternStrength;
                alpha += hex * _PatternStrength * 0.35;

                // --- Intersection glow -------------------------------------------
                float fragmentEyeDepth = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
                float sceneEyeDepth = PL_SceneEyeDepth(screenUV);
                half intersect = 1.0 - saturate((sceneEyeDepth - fragmentEyeDepth) /
                                                max(_IntersectDepth, 1e-3));
                intersect = pow(saturate(intersect), 2.0);
                colour += _IntersectColor.rgb * intersect * _IntersectStrength;
                alpha += intersect * _IntersectStrength * 0.5;

                // --- Impact ripple -----------------------------------------------
                // A ring expanding from the point of impact across the shell surface.
                // _ImpactTime is set to 0 by the VFX driver on a block and left to run.
                if (_ImpactTime < 3.0)
                {
                    float d = distance(input.positionOS, _ImpactPoint.xyz);
                    float ringRadius = _ImpactTime * _ImpactSpeed;
                    float ring = 1.0 - saturate(abs(d - ringRadius) / max(_ImpactWidth, 1e-3));
                    ring = pow(ring, 2.0) * saturate(1.0 - _ImpactTime / 3.0);
                    colour += _ImpactColor.rgb * ring;
                    alpha += ring;
                }

                alpha = saturate(alpha) * _Opacity;
                colour *= _Opacity;

                // Additive-ish blending, so fog towards black.
                colour = MixFogColor(colour, half3(0, 0, 0), input.fogFactor);
                colour += PL_Dither(svPosition, 1.0 / 255.0);

                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
