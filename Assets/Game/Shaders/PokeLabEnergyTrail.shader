// -----------------------------------------------------------------------------
// PokeLab/VFX/EnergyTrail
//
// Beams, projectile trails, slashes, the capture beam, vine whips and thunder
// bolts. Built for Unity's Trail Renderer and Line Renderer UV layout, and for
// stretched-billboard particles: U runs along the length, V across it.
//
// The shape is authored in the shader rather than in a texture so one material
// serves every move type: a hot core with a soft falloff across V, a flow that
// scrolls along U, a head that is brighter than the tail, and an optional
// lightning-style lateral wobble for Electric.
//
// Vertex colour alpha tapers the trail, which is what the Trail Renderer's own
// colour-over-lifetime gradient drives, so a trail dies correctly for free.
// -----------------------------------------------------------------------------
Shader "PokeLab/VFX/EnergyTrail"
{
    Properties
    {
        [HDR] _CoreColor("Core Colour", Color) = (3.0,2.6,1.6,1)
        [HDR] _EdgeColor("Edge Colour", Color) = (1.2,0.5,0.15,1)
        _CoreWidth("Core Width", Range(0.01,1)) = 0.22
        _EdgeSoftness("Edge Softness", Range(0.01,1)) = 0.5

        [Header(Flow)][Space(4)]
        _FlowMap("Flow Noise", 2D) = "white" {}
        _FlowSpeed("Flow Speed", Float) = 2.4
        _FlowStrength("Flow Strength", Range(0,1)) = 0.55
        _FlowTiling("Flow Tiling", Vector) = (3,1,0,0)

        [Header(Shape)][Space(4)]
        _HeadBoost("Head Brightness", Range(0,4)) = 1.4
        _TailFade("Tail Fade Power", Range(0.2,6)) = 1.6
        _Wobble("Lightning Wobble", Range(0,1)) = 0
        _WobbleFrequency("Wobble Frequency", Float) = 9
        _WobbleSpeed("Wobble Speed", Float) = 14

        [Header(Fade)][Space(4)]
        _SoftFadeDistance("Soft Fade Distance (m)", Range(0,6)) = 0.4
        _Opacity("Opacity", Range(0,2)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+5"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "EnergyTrail"
            Tags { "LightMode" = "UniversalForward" }

            // Pure additive with a premultiplied result, so the alpha is applied
            // exactly once. SrcAlpha One would square it and crush the soft edges.
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex TrailVertex
            #pragma fragment TrailFragment
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _CoreColor;
                half4  _EdgeColor;
                half   _CoreWidth;
                half   _EdgeSoftness;
                float4 _FlowMap_ST;
                float  _FlowSpeed;
                half   _FlowStrength;
                float4 _FlowTiling;
                half   _HeadBoost;
                half   _TailFade;
                half   _Wobble;
                float  _WobbleFrequency;
                float  _WobbleSpeed;
                float  _SoftFadeDistance;
                half   _Opacity;
            CBUFFER_END

            TEXTURE2D(_FlowMap); SAMPLER(sampler_FlowMap);

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

            Varyings TrailVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.uv = input.uv;
                output.colour = input.colour;
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 TrailFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float t = _Time.y;

                float along = saturate(input.uv.x);
                float across = input.uv.y * 2.0 - 1.0;   // -1 at one edge, +1 at the other

                // Lightning wobble: displace the centreline laterally with two
                // decorrelated sines. At _Wobble = 0 this is exactly a straight beam.
                float wobble = (sin(along * _WobbleFrequency + t * _WobbleSpeed) * 0.6 +
                                sin(along * _WobbleFrequency * 2.7 - t * _WobbleSpeed * 1.4) * 0.4)
                               * _Wobble;
                across -= wobble;

                // Flow noise erodes the beam along its length so it is not a clean
                // gradient tube.
                float2 flowUV = float2(along * _FlowTiling.x - t * _FlowSpeed,
                                       input.uv.y * _FlowTiling.y);
                half flow = SAMPLE_TEXTURE2D(_FlowMap, sampler_FlowMap, flowUV).r;
                float erosion = lerp(1.0, flow * 1.6, _FlowStrength);

                // Cross-section profile: hot core, soft shoulders.
                float d = abs(across);
                float core = 1.0 - smoothstep(0.0, _CoreWidth, d);
                float shoulder = 1.0 - smoothstep(_CoreWidth, _CoreWidth + _EdgeSoftness, d);

                float profile = saturate(core + shoulder * 0.55) * erosion;

                // Head is brighter than tail. Trail Renderer puts U = 0 at the head.
                float head = pow(1.0 - along, _TailFade);
                profile *= lerp(1.0, head, 0.85);

                half3 colour = lerp(_EdgeColor.rgb, _CoreColor.rgb, core);
                colour *= input.colour.rgb;
                colour *= 1.0 + head * _HeadBoost;

                half alpha = saturate(profile * input.colour.a * _Opacity);

                float fragmentEyeDepth = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
                if (_SoftFadeDistance > 1e-4)
                    alpha *= PL_SoftFade(screenUV, fragmentEyeDepth, _SoftFadeDistance);

                colour = MixFogColor(colour, half3(0, 0, 0), input.fogFactor);
                colour += PL_Dither(svPosition, 1.0 / 255.0);

                return half4(colour * alpha, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
