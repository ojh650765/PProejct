// -----------------------------------------------------------------------------
// PokeLab/VFX/Decal
//
// Screen-space box-projected decals: scorch marks, craters, ice patches, poison
// pools, the shadow blob under a creature, and the ground ring a status aura
// leaves behind.
//
// Applied by rendering a unit cube (Unity's built-in Cube mesh, scaled to the
// decal volume). For every pixel the cube covers, the scene depth is read, the
// world position reconstructed, transformed into the cube's local space, and
// discarded if it falls outside the unit box. What survives is the decal
// projected onto whatever geometry is actually there.
//
// This is deliberately not URP's DecalProjector: that needs the Decal renderer
// feature enabled and a DBuffer-compatible shader, which is another integration
// step and another way for the whole thing to fail silently. This works on any
// URP renderer that has the depth texture on, which we need anyway.
//
// REQUIRES Depth Texture ON. Without it every decal covers its whole box.
// -----------------------------------------------------------------------------
Shader "PokeLab/VFX/Decal"
{
    Properties
    {
        [MainTexture] _BaseMap("Decal Map", 2D) = "white" {}
        [HDR][MainColor] _BaseColor("Tint", Color) = (1,1,1,1)
        [HDR] _EmissionColor("Emission", Color) = (0,0,0,0)

        _Opacity("Opacity", Range(0,1)) = 1
        _EdgeFade("Box Edge Fade", Range(0,0.5)) = 0.12
        _NormalCutoff("Surface Angle Cutoff", Range(-1,1)) = 0.15
        _NormalFade("Surface Angle Fade", Range(0.01,1)) = 0.35

        [Header(Animation)][Space(4)]
        _ScrollSpeed("Scroll Speed", Vector) = (0,0,0,0)
        _PulseSpeed("Pulse Speed", Range(0,8)) = 0
        _PulseDepth("Pulse Depth", Range(0,1)) = 0.3

        [Header(Dissolve)][Space(4)]
        _DissolveAmount("Dissolve Amount", Range(0,1)) = 0
        _DissolveScale("Dissolve Noise Scale", Float) = 5

        [Header(Blending)][Space(4)]
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 10
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-50"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Decal"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            // Front faces are culled and the depth test disabled so the decal still
            // resolves when the camera is inside its volume, which happens constantly
            // for a ground decal the player walks over.
            Cull Front
            ZTest Always

            HLSLPROGRAM
            #pragma vertex DecalVertex
            #pragma fragment DecalFragment
            #pragma target 3.5
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            #define PL_HAS_SCENE_DEPTH 1
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _EmissionColor;
                half   _Opacity;
                half   _EdgeFade;
                half   _NormalCutoff;
                half   _NormalFade;
                float4 _ScrollSpeed;
                half   _PulseSpeed;
                half   _PulseDepth;
                half   _DissolveAmount;
                float  _DissolveScale;
                half   _SrcBlend;
                half   _DstBlend;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewRayWS  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DecalVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posIn.positionCS;
                // Ray from the camera through this vertex. Interpolated, it gives a
                // per-pixel view ray to walk out to the reconstructed depth.
                output.viewRayWS = posIn.positionWS - _WorldSpaceCameraPos;
                return output;
            }

            half4 DecalFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float t = _Time.y;

                // --- Reconstruct the world position of whatever is behind us -----
                float sceneEyeDepth = PL_SceneEyeDepth(screenUV);
                float3 ray = input.viewRayWS;
                // Eye depth is measured along the view forward axis, not along the
                // ray, so scale the ray by its own view-space depth before walking
                // it out. Transform the direction only: no translation involved.
                float rayEyeDepth = -mul(GetWorldToViewMatrix(), float4(ray, 0.0)).z;
                float3 positionWS = _WorldSpaceCameraPos + ray * (sceneEyeDepth / max(rayEyeDepth, 1e-4));

                // --- Into the decal box ------------------------------------------
                float3 positionOS = TransformWorldToObject(positionWS);
                // Unity's built-in Cube spans -0.5..0.5, so the unit box test is on
                // 0.5 rather than on 1.
                float3 outside = abs(positionOS) - 0.5;
                clip(-max(max(outside.x, outside.y), outside.z));

                float2 uv = positionOS.xz + 0.5;
                uv = TRANSFORM_TEX(uv, _BaseMap) + _ScrollSpeed.xy * t;

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
                half4 colour = tex * _BaseColor;

                // --- Box edge fade. Without it the decal ends on a hard rectangle.
                float3 edge = saturate((0.5 - abs(positionOS)) / max(_EdgeFade, 1e-3));
                colour.a *= edge.x * edge.z * saturate(edge.y * 3.0);

                // --- Surface angle rejection --------------------------------------
                // A ground decal must not smear up the side of a wall it happens to
                // touch. The depth-normals buffer tells us which way the surface faces.
                float3 surfaceNormalWS = SampleSceneNormals(screenUV);
                float3 decalUpWS = normalize(mul((float3x3)UNITY_MATRIX_M, float3(0, 1, 0)));
                half facing = dot(surfaceNormalWS, decalUpWS);
                half angleMask = saturate((facing - _NormalCutoff) / max(_NormalFade, 1e-3));
                // When no DepthNormals prepass is running the normals texture reads
                // back black. Detect that and skip the rejection rather than making
                // every decal vanish for a reason nobody would think to look for.
                half normalsAvailable = step(0.1, dot(surfaceNormalWS, surfaceNormalWS));
                colour.a *= lerp(1.0, angleMask, normalsAvailable);

                // --- Animation ----------------------------------------------------
                if (_PulseSpeed > 1e-4)
                    colour.a *= 1.0 - _PulseDepth * (0.5 + 0.5 * sin(t * _PulseSpeed));

                if (_DissolveAmount > 1e-3)
                {
                    float n = PL_Fbm(positionWS.xz * _DissolveScale);
                    colour.a *= saturate((n - _DissolveAmount * 1.2) * 6.0);
                }

                colour.a *= _Opacity;
                colour.rgb += _EmissionColor.rgb * colour.a;

                colour.rgb += PL_Dither(svPosition, 1.0 / 255.0);
                return colour;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
