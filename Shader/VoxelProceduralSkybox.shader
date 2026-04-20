Shader "Voxel/URP/Procedural Voxel Skybox"
{
    Properties
    {
        [Header(Time Source)]
        [ToggleUI] _UseMainLightDirection("Use URP Main Directional Light", Float) = 1
        _SunDirection("Manual Sun Direction", Vector) = (0, 0, 0, 0)
        _MoonDirection("Manual Moon Direction", Vector) = (0, 0, 0, 0)

        [Header(Sky Colors)]
        [HDR] _DayZenithColor("Day Zenith", Color) = (0.18, 0.52, 1.0, 1)
        [HDR] _DayHorizonColor("Day Horizon", Color) = (0.70, 0.88, 1.0, 1)
        [HDR] _SunsetZenithColor("Sunset Zenith", Color) = (0.10, 0.22, 0.46, 1)
        [HDR] _SunsetHorizonColor("Sunset Horizon", Color) = (1.0, 0.45, 0.18, 1)
        [HDR] _NightZenithColor("Night Zenith", Color) = (0.015, 0.025, 0.075, 1)
        [HDR] _NightHorizonColor("Night Horizon", Color) = (0.055, 0.075, 0.145, 1)
        _HorizonPower("Horizon Blend Power", Range(0.25, 4.0)) = 1.25
        _SunsetGlow("Sunset Horizon Glow", Range(0.0, 2.0)) = 0.35
        _Exposure("Exposure", Range(0.0, 4.0)) = 1.0

        [Header(Sun)]
        [HDR] _SunColor("Sun Color", Color) = (1.0, 0.82, 0.28, 1)
        _SunSize("Sun Radius", Range(0.005, 0.18)) = 0.045
        _SunEdgeSoftness("Sun Edge Softness", Range(0.0001, 0.04)) = 0.003
        _SunGlowSize("Sun Glow Size", Range(0.0, 0.6)) = 0.18
        _SunGlowIntensity("Sun Glow Intensity", Range(0.0, 3.0)) = 0.45

        [Header(Moon)]
        [HDR] _MoonColor("Moon Color", Color) = (0.78, 0.88, 1.0, 1)
        _MoonSize("Moon Radius", Range(0.005, 0.18)) = 0.045
        _MoonEdgeSoftness("Moon Edge Softness", Range(0.0001, 0.04)) = 0.002
        _MoonGlowSize("Moon Glow Size", Range(0.0, 0.6)) = 0.12
        _MoonGlowIntensity("Moon Glow Intensity", Range(0.0, 3.0)) = 0.18
        _MoonPhaseOffset("Moon Side Shadow", Range(-1.0, 1.0)) = 0.32
        _MoonShadowDarkness("Moon Shadow Darkness", Range(0.0, 1.0)) = 0.72
        _MoonReliefStrength("Moon Relief Strength", Range(0.0, 2.0)) = 0.9

        [Header(Procedural Clouds)]
        _CloudCoverage("Cloud Coverage", Range(0.0, 1.0)) = 0.52
        _CloudSoftness("Cloud Softness", Range(0.001, 0.5)) = 0.16
        _CloudScale("Cloud Scale", Range(0.05, 4.0)) = 0.78
        _CloudSpeed("Cloud Speed", Range(0.0, 0.2)) = 0.015
        _CloudDirection("Cloud Direction XY", Vector) = (1.0, 0.15, 0, 0)
        _CloudLayerMix("Cloud Layer Mix", Range(0.0, 1.0)) = 0.65
        _CloudIntensity("Cloud Blend", Range(0.0, 1.0)) = 0.78
        [HDR] _CloudColor("Cloud Day Color", Color) = (1.0, 0.98, 0.92, 1)
        [HDR] _CloudEveningColor("Cloud Evening Color", Color) = (1.0, 0.56, 0.30, 1)
        [HDR] _CloudNightColor("Cloud Night Color", Color) = (0.12, 0.16, 0.28, 1)

        [Header(Stars)]
        _StarAmount("Star Amount", Range(0.0, 1.0)) = 0.55
        _StarBrightness("Star Brightness", Range(0.0, 4.0)) = 1.25
        _StarGrid("Star Grid", Range(32.0, 512.0)) = 220.0
        _StarSize("Star Size", Range(0.01, 0.45)) = 0.12
        _StarTwinkle("Star Twinkle", Range(0.0, 1.0)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            Name "VoxelProceduralSkybox"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define VOXEL_SKY_PI 3.14159265359
            #define VOXEL_SKY_INV_PI 0.31830988618
            #define VOXEL_SKY_INV_TAU 0.15915494309

            CBUFFER_START(UnityPerMaterial)
                float4 _SunDirection;
                float4 _MoonDirection;
                float4 _DayZenithColor;
                float4 _DayHorizonColor;
                float4 _SunsetZenithColor;
                float4 _SunsetHorizonColor;
                float4 _NightZenithColor;
                float4 _NightHorizonColor;
                float4 _SunColor;
                float4 _MoonColor;
                float4 _CloudDirection;
                float4 _CloudColor;
                float4 _CloudEveningColor;
                float4 _CloudNightColor;
                float _UseMainLightDirection;
                float _HorizonPower;
                float _SunsetGlow;
                float _Exposure;
                float _SunSize;
                float _SunEdgeSoftness;
                float _SunGlowSize;
                float _SunGlowIntensity;
                float _MoonSize;
                float _MoonEdgeSoftness;
                float _MoonGlowSize;
                float _MoonGlowIntensity;
                float _MoonPhaseOffset;
                float _MoonShadowDarkness;
                float _MoonReliefStrength;
                float _CloudCoverage;
                float _CloudSoftness;
                float _CloudScale;
                float _CloudSpeed;
                float _CloudLayerMix;
                float _CloudIntensity;
                float _StarAmount;
                float _StarBrightness;
                float _StarGrid;
                float _StarSize;
                float _StarTwinkle;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 skyDirection : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 NormalizeSafe3(float3 value, float3 fallback)
            {
                float lengthSq = dot(value, value);
                return lengthSq > 0.00001 ? value * rsqrt(lengthSq) : fallback;
            }

            float2 NormalizeSafe2(float2 value, float2 fallback)
            {
                float lengthSq = dot(value, value);
                return lengthSq > 0.00001 ? value * rsqrt(lengthSq) : fallback;
            }

            float Hash12(float2 p)
            {
                float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);

                float a = Hash12(i + float2(0.0, 0.0));
                float b = Hash12(i + float2(1.0, 0.0));
                float c = Hash12(i + float2(0.0, 1.0));
                float d = Hash12(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float Fbm(float2 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float2 shift = float2(17.31, 41.17);

                [unroll]
                for (int octave = 0; octave < 5; octave++)
                {
                    value += amplitude * ValueNoise(p);
                    p = p * 2.03 + shift;
                    amplitude *= 0.5;
                }

                return value;
            }

            float2 DirectionToEquirect(float3 direction)
            {
                float longitude = atan2(direction.x, direction.z) * VOXEL_SKY_INV_TAU + 0.5;
                float latitude = asin(clamp(direction.y, -1.0, 1.0)) * VOXEL_SKY_INV_PI + 0.5;
                return float2(longitude, latitude);
            }

            void CalculateTimeMasks(float sunHeight, out float dayMask, out float twilightMask, out float nightMask)
            {
                dayMask = smoothstep(0.02, 0.22, sunHeight);
                nightMask = 1.0 - smoothstep(-0.22, -0.05, sunHeight);
                twilightMask = saturate(1.0 - dayMask - nightMask);

                float total = max(dayMask + twilightMask + nightMask, 0.0001);
                dayMask /= total;
                twilightMask /= total;
                nightMask /= total;
            }

            void BuildBodyBasis(float3 centerDirection, out float3 right, out float3 up)
            {
                float3 referenceUp = abs(centerDirection.y) < 0.98 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
                right = NormalizeSafe3(cross(referenceUp, centerDirection), float3(1.0, 0.0, 0.0));
                up = NormalizeSafe3(cross(centerDirection, right), float3(0.0, 1.0, 0.0));
            }

            float DiscBodyMask(float3 rayDirection, float3 centerDirection, float radius, float softness, out float2 bodyCoordinates)
            {
                float3 right;
                float3 up;
                BuildBodyBasis(centerDirection, right, up);

                float facing = dot(rayDirection, centerDirection);
                float reciprocalFacing = rcp(max(facing, 0.0001));
                bodyCoordinates = float2(dot(rayDirection, right), dot(rayDirection, up)) * reciprocalFacing;

                float radialDistance = length(bodyCoordinates);
                float mask = 1.0 - smoothstep(radius, radius + max(softness, 0.0001), radialDistance);
                return mask * step(0.0, facing);
            }

            float DiscGlow(float3 rayDirection, float3 centerDirection, float radius, float glowSize)
            {
                float2 bodyCoordinates;
                DiscBodyMask(rayDirection, centerDirection, radius, 0.0001, bodyCoordinates);
                float radialDistance = length(bodyCoordinates);
                float glowRange = max(radius + glowSize, radius + 0.0001);
                float glow = 1.0 - smoothstep(radius, glowRange, radialDistance);
                return glow * glow * step(0.0, dot(rayDirection, centerDirection));
            }

            float SampleCloudMask(float3 rayDirection, float timeSeconds)
            {
                float horizonFade = smoothstep(-0.04, 0.14, rayDirection.y);
                float zenithFade = lerp(1.0, 0.72, smoothstep(0.88, 1.0, rayDirection.y));
                float skyFade = horizonFade * zenithFade;

                float denominator = max(rayDirection.y + 0.28, 0.08);
                float2 windDirection = NormalizeSafe2(_CloudDirection.xy, float2(1.0, 0.0));
                float2 cloudUv = (rayDirection.xz / denominator) * max(_CloudScale, 0.001);
                cloudUv += windDirection * (timeSeconds * _CloudSpeed);

                float largeClouds = Fbm(cloudUv);
                float fineClouds = Fbm(cloudUv * 3.1 + float2(71.2, -23.6) + timeSeconds * _CloudSpeed * 0.07);
                float clouds = lerp(largeClouds, largeClouds * 0.78 + fineClouds * 0.28, saturate(_CloudLayerMix));

                float threshold = lerp(0.82, 0.28, saturate(_CloudCoverage));
                return smoothstep(threshold, threshold + max(_CloudSoftness, 0.001), clouds) * skyFade;
            }

            float3 SampleStarLayer(float2 skyUv, float grid, float density, float baseSize, float brightnessScale, float twinkleAmount, float timeSeconds)
            {
                float2 starUv = skyUv * float2(grid, grid * 0.5);
                float2 cell = floor(starUv);
                float2 localCell = frac(starUv);

                float seed = Hash12(cell);
                float threshold = 1.0 - saturate(density) * 0.06;
                float starExists = step(threshold, seed);

                float2 starCenter = float2(Hash12(cell + 11.37), Hash12(cell + 47.21));
                float2 offset = localCell - starCenter;
                float distanceToCenter = length(offset);

                float brightnessSeed = Hash12(cell + 19.19);
                float sizeSeed = Hash12(cell + 5.73);
                float radius = max(baseSize, 0.005) * lerp(0.1, 0.28, sizeSeed * sizeSeed);
                float aa = max(fwidth(distanceToCenter), 0.0015);
                float core = 1.0 - smoothstep(radius * 0.2 - aa, radius * 0.95 + aa, distanceToCenter);
                float bloom = 1.0 - smoothstep(radius * 0.7, radius * (2.2 + sizeSeed * 1.2), distanceToCenter);
                bloom *= bloom * (0.08 + sizeSeed * 0.16);
                float rareBright = step(0.982, brightnessSeed);
                float rareHalo = 1.0 - smoothstep(radius * 0.9, radius * 4.2, distanceToCenter);
                rareHalo *= rareBright * 0.35;

                float twinklePhase = timeSeconds * (2.5 + brightnessSeed * 6.0) + seed * 31.7;
                float twinkle = lerp(1.0, 0.86 + 0.14 * sin(twinklePhase), saturate(twinkleAmount));
                float hdrBoost = lerp(1.4, 4.2, brightnessSeed * brightnessSeed * brightnessSeed);
                float star = starExists * (core * 2.35 + bloom + rareHalo) * brightnessScale * twinkle * hdrBoost;

                float3 coldStar = float3(0.72, 0.82, 1.0);
                float3 neutralStar = float3(0.95, 0.97, 1.0);
                float3 warmStar = float3(1.0, 0.92, 0.72);
                float warmMix = smoothstep(0.48, 0.92, brightnessSeed);
                float coldMix = 1.0 - smoothstep(0.16, 0.52, brightnessSeed);
                float3 starColor = lerp(neutralStar, warmStar, warmMix);
                starColor = lerp(starColor, coldStar, coldMix);
                return starColor * star;
            }

            float MoonReliefField(float2 surfaceUv)
            {
                float broadRelief = Fbm(surfaceUv * 12.0 + float2(6.7, -3.1));
                float fineRelief = Fbm(surfaceUv * 26.0 + float2(-14.2, 21.8));
                return broadRelief * 0.68 + fineRelief * 0.32;
            }

            float3 SampleStars(float3 rayDirection, float nightMask, float timeSeconds)
            {
                float horizonFade = smoothstep(0.0, 0.18, rayDirection.y);
                float polarFade = lerp(1.0, 0.72, smoothstep(0.96, 1.0, abs(rayDirection.y)));
                float2 skyUv = DirectionToEquirect(rayDirection);
                float grid = max(_StarGrid, 32.0);
                float baseSize = max(_StarSize, 0.01);

                float3 largeStars = SampleStarLayer(
                    skyUv,
                    grid,
                    saturate(_StarAmount),
                    baseSize,
                    _StarBrightness,
                    saturate(_StarTwinkle),
                    timeSeconds);

                float3 fineStars = SampleStarLayer(
                    skyUv + float2(0.173, 0.421),
                    max(grid * 2.1, 64.0),
                    saturate(_StarAmount) * 0.45,
                    baseSize * 0.28,
                    _StarBrightness * 0.18,
                    saturate(_StarTwinkle) * 0.4,
                    timeSeconds * 0.67 + 7.0);

                return (largeStars + fineStars) * nightMask * horizonFade * polarFade;
            }

            float3 ShadeMoon(float3 moonDirection, float3 sunDirection, float2 moonCoordinates)
            {
                float moonRadius = max(_MoonSize, 0.0001);
                float2 normalizedCoordinates = moonCoordinates / moonRadius;
                float radialSq = dot(normalizedCoordinates, normalizedCoordinates);
                float z = sqrt(saturate(1.0 - radialSq));
                float3 localNormal = float3(normalizedCoordinates, z);

                float3 right;
                float3 up;
                BuildBodyBasis(moonDirection, right, up);
                float3 surfaceNormal = NormalizeSafe3(
                    right * localNormal.x +
                    up * localNormal.y -
                    moonDirection * localNormal.z,
                    -moonDirection);

                float2 surfaceUv = float2(
                    atan2(localNormal.x, localNormal.z) * VOXEL_SKY_INV_TAU + 0.5,
                    asin(clamp(localNormal.y, -1.0, 1.0)) * VOXEL_SKY_INV_PI + 0.5);

                float maria = smoothstep(0.44, 0.76, Fbm(surfaceUv * 5.2 + float2(1.7, -2.3)));
                float highlands = Fbm(surfaceUv * 10.5 + float2(-3.4, 8.2));
                float craterLarge = smoothstep(0.57, 0.84, Fbm(surfaceUv * 17.0 + float2(9.1, 3.7)));
                float craterSmall = smoothstep(0.64, 0.9, Fbm(surfaceUv * 33.0 + float2(-12.6, 24.7)));

                float reliefBase = MoonReliefField(surfaceUv);
                float reliefDx = MoonReliefField(surfaceUv + float2(0.008, 0.0));
                float reliefDy = MoonReliefField(surfaceUv + float2(0.0, 0.008));
                float2 reliefGradient = float2(reliefDx - reliefBase, reliefDy - reliefBase);
                float reliefStrength = lerp(6.0, 10.0, saturate(craterSmall * 0.65 + maria * 0.35)) * max(_MoonReliefStrength, 0.0);
                float3 reliefNormal = NormalizeSafe3(
                    surfaceNormal -
                    right * reliefGradient.x * reliefStrength -
                    up * reliefGradient.y * reliefStrength,
                    surfaceNormal);

                float phaseAmount = abs(_MoonPhaseOffset);
                float phaseSign = _MoonPhaseOffset < 0.0 ? -1.0 : 1.0;
                float3 projectedLight = sunDirection - moonDirection * dot(sunDirection, moonDirection);
                float projectedLightLength = sqrt(max(dot(projectedLight, projectedLight), 0.0));
                float3 fallbackPhaseDirection = right * phaseSign;
                float3 phaseDirection = NormalizeSafe3(
                    lerp(fallbackPhaseDirection, projectedLight * rcp(max(projectedLightLength, 0.0001)), smoothstep(0.02, 0.24, projectedLightLength)),
                    fallbackPhaseDirection);
                float3 moonLightingDirection = NormalizeSafe3(sunDirection + phaseDirection * phaseAmount, sunDirection);

                float albedo = 0.91 - maria * 0.16 + highlands * 0.04 - craterLarge * 0.04 - craterSmall * 0.025;
                float viewFacing = saturate(dot(reliefNormal, -moonDirection));
                float phaseLight = dot(reliefNormal, moonLightingDirection);
                float phaseGradient = smoothstep(-0.32, 0.34, phaseLight);
                float directLight = pow(saturate(phaseLight * 0.82 + 0.18), 0.95);
                float reliefLight = saturate(phaseLight * 1.08 + 0.08);
                float earthshine = (1.0 - phaseGradient) * 0.07;
                float limbDarkening = lerp(0.72, 1.0, pow(viewFacing, 0.68));
                float craterOcclusion = 1.0 - craterLarge * 0.06 - craterSmall * 0.04;
                float shadowFloor = lerp(1.0, 0.12, saturate(_MoonShadowDarkness));

                float3 baseMoon = _MoonColor.rgb * albedo * limbDarkening;
                float3 litMoon = baseMoon * lerp(shadowFloor, 1.0, phaseGradient);
                litMoon *= 0.14 + directLight * 0.86;
                litMoon *= lerp(0.9, 1.08, reliefLight);
                litMoon *= craterOcclusion;
                litMoon += float3(0.07, 0.09, 0.14) * earthshine * (0.55 + maria * 0.45);

                float edgeHighlight = pow(1.0 - viewFacing, 3.4) * 0.02;
                litMoon += _MoonColor.rgb * edgeHighlight;

                return litMoon;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.skyDirection = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 rayDirection = NormalizeSafe3(input.skyDirection, float3(0.0, 1.0, 0.0));
                Light mainLight = GetMainLight();

                float3 manualSunDirection = _SunDirection.xyz;
                float manualSunValid = step(0.0001, dot(manualSunDirection, manualSunDirection));
                float3 mainSunDirection = NormalizeSafe3((float3)mainLight.direction, float3(0.0, 1.0, 0.0));
                float3 sunDirection = NormalizeSafe3(lerp(manualSunDirection, mainSunDirection, saturate(_UseMainLightDirection)), mainSunDirection);
                sunDirection = NormalizeSafe3(lerp(mainSunDirection, sunDirection, max(manualSunValid, saturate(_UseMainLightDirection))), float3(0.0, 1.0, 0.0));

                float moonManualValid = step(0.0001, dot(_MoonDirection.xyz, _MoonDirection.xyz));
                float3 moonDirection = NormalizeSafe3(lerp(-sunDirection, _MoonDirection.xyz, moonManualValid), -sunDirection);

                float dayMask;
                float twilightMask;
                float nightMask;
                CalculateTimeMasks(sunDirection.y, dayMask, twilightMask, nightMask);

                float verticalGradient = pow(saturate(rayDirection.y * 0.5 + 0.5), max(_HorizonPower, 0.001));
                float3 daySky = lerp(_DayHorizonColor.rgb, _DayZenithColor.rgb, verticalGradient);
                float3 twilightSky = lerp(_SunsetHorizonColor.rgb, _SunsetZenithColor.rgb, verticalGradient);
                float3 nightSky = lerp(_NightHorizonColor.rgb, _NightZenithColor.rgb, verticalGradient);
                float3 color = daySky * dayMask + twilightSky * twilightMask + nightSky * nightMask;

                float3 sunFlat = NormalizeSafe3(float3(sunDirection.x, 0.0, sunDirection.z), float3(0.0, 0.0, 1.0));
                float3 rayFlat = NormalizeSafe3(float3(rayDirection.x, 0.0, rayDirection.z), sunFlat);
                float warmHorizon = pow(saturate(dot(rayFlat, sunFlat)), 2.0);
                warmHorizon *= smoothstep(-0.12, 0.32, rayDirection.y) * (1.0 - smoothstep(0.48, 0.9, rayDirection.y));
                color += _SunsetHorizonColor.rgb * warmHorizon * twilightMask * _SunsetGlow;

                float timeSeconds = _Time.y;
                color += SampleStars(rayDirection, nightMask, timeSeconds);

                float sunVisibility = smoothstep(-0.055, 0.035, sunDirection.y);
                float moonVisibility = smoothstep(-0.045, 0.04, moonDirection.y) * saturate(nightMask + twilightMask * 0.35);

                float sunGlow = DiscGlow(rayDirection, sunDirection, _SunSize, _SunGlowSize) * sunVisibility * _SunGlowIntensity;
                float moonGlow = DiscGlow(rayDirection, moonDirection, _MoonSize, _MoonGlowSize) * moonVisibility * _MoonGlowIntensity;
                color += _SunColor.rgb * sunGlow;
                color += _MoonColor.rgb * moonGlow * 0.55;

                float cloudMask = SampleCloudMask(rayDirection, timeSeconds);
                float3 cloudColor = _CloudColor.rgb * dayMask + _CloudEveningColor.rgb * twilightMask + _CloudNightColor.rgb * nightMask;
                float cloudLight = dayMask + twilightMask * 0.78 + nightMask * 0.35;
                cloudColor *= cloudLight;
                color = lerp(color, cloudColor, cloudMask * saturate(_CloudIntensity));

                float2 sunCoordinates;
                float sunDisc = DiscBodyMask(rayDirection, sunDirection, _SunSize, _SunEdgeSoftness, sunCoordinates) * sunVisibility;
                float sunDistance = length(sunCoordinates) / max(_SunSize, 0.0001);
                float sunCore = 1.0 - smoothstep(0.0, 0.78, sunDistance);
                float3 sunDiscColor = lerp(_SunColor.rgb, float3(1.0, 0.97, 0.92), sunCore * 0.55);
                color += sunDiscColor * sunDisc * (1.0 - cloudMask * 0.35);

                float2 moonCoordinates;
                float moonDisc = DiscBodyMask(rayDirection, moonDirection, _MoonSize, _MoonEdgeSoftness, moonCoordinates) * moonVisibility;
                float3 moonSurface = ShadeMoon(moonDirection, sunDirection, moonCoordinates);
                color += moonSurface * moonDisc * (1.0 - cloudMask * 0.55);

                return half4(color * _Exposure, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
