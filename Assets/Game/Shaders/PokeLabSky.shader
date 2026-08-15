// -----------------------------------------------------------------------------
// PokeLab/Sky
//
// Procedural stylised skybox, driven entirely by LightingDirector so the sky, the
// ambient gradient, the fog and the volume grade all move together through the
// day. Assign as RenderSettings.skybox (Lighting window > Environment).
//
// Four-stop vertical gradient, a sun disc with a wide bloom-friendly glow, two
// scrolling cloud bands, and stars that fade in with night.
//
// Banding is the whole reason this is a shader rather than a gradient texture: a
// sky is the widest smooth gradient on screen and quantises visibly in an 8-bit
// buffer. Every gradient here is dithered, harder at night where the steps are
// closest together.
// -----------------------------------------------------------------------------
Shader "PokeLab/Sky"
{
    Properties
    {
        [Header(Gradient)][Space(4)]
        [HDR] _ZenithColor("Zenith", Color) = (0.16,0.36,0.72,1)
        [HDR] _MidColor("Mid Sky", Color) = (0.42,0.66,0.92,1)
        [HDR] _HorizonColor("Horizon", Color) = (0.82,0.88,0.94,1)
        [HDR] _GroundColor("Below Horizon", Color) = (0.24,0.26,0.28,1)
        _HorizonSharpness("Horizon Sharpness", Range(0.5,12)) = 3.2
        _MidPoint("Mid Sky Height", Range(0.02,0.98)) = 0.28
        _Exposure("Exposure", Range(0,4)) = 1

        [Header(Sun)][Space(4)]
        [HDR] _SunColor("Sun Colour", Color) = (4,3.4,2.4,1)
        _SunSize("Sun Size", Range(0.001,0.2)) = 0.028
        _SunSoftness("Sun Edge Softness", Range(0.0005,0.1)) = 0.006
        [HDR] _SunGlowColor("Sun Glow Colour", Color) = (1.4,0.9,0.55,1)
        _SunGlowSize("Sun Glow Size", Range(1,64)) = 9
        _SunGlowStrength("Sun Glow Strength", Range(0,4)) = 1.1

        [Header(Clouds)][Space(4)]
        [HDR] _CloudColor("Cloud Colour", Color) = (1,0.98,0.95,1)
        [HDR] _CloudShadeColor("Cloud Shade Colour", Color) = (0.55,0.60,0.72,1)
        _CloudCoverage("Coverage", Range(0,1)) = 0.42
        _CloudSharpness("Sharpness", Range(0.5,12)) = 3
        _CloudScale("Scale", Float) = 2.4
        _CloudSpeed("Drift Speed", Range(0,0.2)) = 0.012
        _CloudHeightFade("Height Fade", Range(0.01,1)) = 0.12

        [Header(Stars)][Space(4)]
        [HDR] _StarColor("Star Colour", Color) = (1,1,1,1)
        _StarDensity("Star Density", Float) = 320
        _StarThreshold("Star Threshold", Range(0.9,0.9999)) = 0.985
        _StarTwinkle("Star Twinkle Speed", Range(0,6)) = 1.6
        _StarStrength("Star Strength", Range(0,4)) = 0

        [Header(Dither)][Space(4)]
        _DitherStrength("Dither Strength (1/255 units)", Range(0,6)) = 2.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "Queue" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            // No LightMode tag: the skybox is drawn by the pipeline's skybox pass
            // from RenderSettings.skybox, not by the object renderer, and tagging it
            // would make it a candidate for the forward opaque pass as well.
            Name "Sky"

            HLSLPROGRAM
            #pragma vertex SkyVertex
            #pragma fragment SkyFragment
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Game/Shaders/Library/PokeLabCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _ZenithColor;
                half4  _MidColor;
                half4  _HorizonColor;
                half4  _GroundColor;
                half   _HorizonSharpness;
                half   _MidPoint;
                half   _Exposure;
                half4  _SunColor;
                half   _SunSize;
                half   _SunSoftness;
                half4  _SunGlowColor;
                half   _SunGlowSize;
                half   _SunGlowStrength;
                half4  _CloudColor;
                half4  _CloudShadeColor;
                half   _CloudCoverage;
                half   _CloudSharpness;
                float  _CloudScale;
                float  _CloudSpeed;
                half   _CloudHeightFade;
                half4  _StarColor;
                float  _StarDensity;
                half   _StarThreshold;
                half   _StarTwinkle;
                half   _StarStrength;
                half   _DitherStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDirWS  : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SkyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Unity's skybox mesh is a unit shape centred on the camera, so the
                // object-space position is already the view direction.
                output.viewDirWS = input.positionOS.xyz;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 SkyFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 svPosition = input.positionCS.xy;
                float3 dir = normalize(input.viewDirWS);
                float h = dir.y;

                // --- Vertical gradient ------------------------------------------
                // Three stops above the horizon, one below. The horizon stop is
                // sharpened separately so a hazy band can sit tight to the skyline
                // without dragging the whole sky pale.
                float above = saturate(h);
                float horizonBlend = pow(1.0 - above, _HorizonSharpness);
                float midBlend = smoothstep(0.0, _MidPoint, above);
                float zenithBlend = smoothstep(_MidPoint, 1.0, above);

                half3 sky = lerp(_MidColor.rgb, _ZenithColor.rgb, zenithBlend);
                sky = lerp(sky, _MidColor.rgb, 1.0 - midBlend);
                sky = lerp(sky, _HorizonColor.rgb, horizonBlend);

                half3 colour = lerp(_GroundColor.rgb, sky, saturate(h * 12.0 + 0.5));

                // --- Sun ---------------------------------------------------------
                float3 sunDir = normalize(_MainLightPosition.xyz);
                float sunDot = dot(dir, sunDir);
                float sunAngle = 1.0 - sunDot;

                // Wide glow first, so the disc sits inside it rather than on top.
                float glow = pow(saturate(sunDot), _SunGlowSize * 8.0);
                // A second, much wider term keeps a warm wash across the whole sky
                // near the horizon at dawn and dusk.
                float wash = pow(saturate(sunDot * 0.5 + 0.5), 4.0) * saturate(1.0 - abs(sunDir.y) * 1.4);
                colour += _SunGlowColor.rgb * (glow + wash * 0.35) * _SunGlowStrength;

                float disc = 1.0 - smoothstep(_SunSize, _SunSize + _SunSoftness, sunAngle);
                colour += _SunColor.rgb * disc;

                // --- Clouds ------------------------------------------------------
                // Projected onto a plane above the camera. Cheap, and the stretch
                // towards the horizon is exactly the perspective we want.
                float planeFade = saturate((h - 0.02) / max(_CloudHeightFade, 1e-3));
                if (planeFade > 0.001)
                {
                    float2 cloudUV = dir.xz / max(h + 0.12, 0.02) * _CloudScale;
                    cloudUV += _Time.y * _CloudSpeed * float2(1.0, 0.35);

                    float n = PL_Fbm(cloudUV);
                    float n2 = PL_Fbm(cloudUV * 2.7 - _Time.y * _CloudSpeed * 1.9);
                    float density = saturate(n * 0.7 + n2 * 0.3);

                    float cover = smoothstep(1.0 - _CloudCoverage, 1.0 - _CloudCoverage + 0.22, density);
                    cover = pow(cover, _CloudSharpness * 0.35);
                    cover *= planeFade;

                    // Light the cloud from the sun direction using the density
                    // gradient as a stand-in normal. Enough to give shape.
                    float lightSide = saturate(dot(normalize(float3(dir.x, 0.35, dir.z)), sunDir) * 0.5 + 0.5);
                    half3 cloud = lerp(_CloudShadeColor.rgb, _CloudColor.rgb, lightSide);
                    cloud += _SunGlowColor.rgb * pow(lightSide, 6.0) * 0.6;

                    colour = lerp(colour, cloud, saturate(cover));
                }

                // --- Stars -------------------------------------------------------
                if (_StarStrength > 0.001 && h > 0.0)
                {
                    // Quantise the direction into cells and pick one bright point per
                    // cell, so stars are points rather than a noise field.
                    float2 cell = floor(dir.xz / max(abs(h) + 0.35, 0.05) * _StarDensity);
                    float r = PL_Hash21(cell);
                    float star = step(_StarThreshold, r);
                    float twinkle = 0.6 + 0.4 * sin(_Time.y * _StarTwinkle + r * 62.8);
                    // Fade out near the horizon where haze would swallow them.
                    float starFade = smoothstep(0.02, 0.35, h);
                    colour += _StarColor.rgb * star * twinkle * starFade * _StarStrength;
                }

                colour *= _Exposure;

                // The sky is the widest gradient on screen. Dither hard.
                colour += PL_Dither(svPosition, _DitherStrength / 255.0);

                return half4(max(colour, 0.0), 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
