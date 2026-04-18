Shader "Voxel/URP/VoxelWaterURP"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _MainTexture("Water Still Spritesheet", 2D) = "white" {}
        [NoScaleOffset] _WaterFlowTexture("Water Flow Spritesheet (Optional)", 2D) = "white" {}

        [Header(Sprite Animation)]
        [Enum(water_still,0,water_flow,1)] _WaterMode("Water Mode", Float) = 0
        [ToggleUI] _UseSeparateFlowTexture("Use Separate Flow Texture", Float) = 0
        _FrameDuration("Frame Duration (Seconds)", Range(0.01, 1.0)) = 0.08
        _FlowUVSpeed("Flow UV Vertical Speed", Range(-4.0, 4.0)) = 0.22

        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Color("Water Tint", Color) = (0.0, 0.376, 1.0, 1.0)

        [Header(Water Motion)]
        [ToggleUI] _EnableWaterMotion("Enable Water Motion", Float) = 1
        _waveAmplitude("Wave Amplitude", Range(0.0, 0.4)) = 0.1
        _waveFrequency("Wave Frequency", Range(0.0, 6.0)) = 0.1
        _waveSpeed("Wave Speed", Range(0.0, 4.0)) = 0.1
        _WaveDirection("Wave Direction XZ", Vector) = (1, 0, 0, 0)

        [Header(Water Shading)]
        _alpha("Water Alpha", Range(0.0, 1.0)) = 1.0
        _Maxalpha("Max Alpha", Range(0.0, 1.0)) = 0.99
        _AmbientStrength("Ambient Strength", Range(0.0, 2.0)) = 0.2
        _DirectionalStrength("Main Light Strength", Range(0.0, 2.0)) = 0.45
        _AdditionalLightsStrength("Additional Lights Strength", Range(0.0, 2.0)) = 0.35
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.94
        _SpecularStrength("Specular Strength", Range(0.0, 4.0)) = 1.6
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        [ToggleUI] _CastShadows("Cast Shadows", Float) = 0.0
        _ShadowStrength("Shadow Strength", Range(0.0, 1.0)) = 0.85

        [Header(Camera Distance Opacity)]
        _DistanceAlphaNear("Near Opacity", Range(0.0, 1.0)) = 0.9
        _DistanceAlphaFar("Far Opacity", Range(0.0, 1.0)) = 0.0
        _DistanceFadeStart("Fade Start Distance", Range(0.0, 500.0)) = 0.0
        _DistanceFadeEnd("Fade End Distance", Range(0.01, 1000.0)) = 80.0

        [Header(Foam)]
        _TopStart("Top Foam Start", Range(0.0, 1.0)) = 0.35
        _Topend("Top Foam End", Range(0.0, 1.0)) = 0.48
        _foamAmount("Foam Amount", Range(0.0, 1.0)) = 0.1
        _FoamIntensity("Foam Intensity", Range(0.0, 2.0)) = 0.1

        [Header(Depth Fade Foam)]
        _DepthDistance("Depth Fade Distance", Range(0.01, 10.0)) = 1.0
        _DepthFoamColor("Depth Foam Color", Color) = (1, 1, 1, 1)
        _DepthFoamStrength("Depth Foam Strength", Range(0.0, 2.0)) = 1.0
        _DepthFoamAlpha("Depth Foam Alpha", Range(0.0, 1.0)) = 0.25

        [Header(Surface Highlight)]
        _SurfaceHighlightColor("Surface Highlight Color", Color) = (0.65, 0.88, 1.0, 1.0)
        _SurfaceHighlight("Surface Highlight Intensity", Range(0.0, 2.0)) = 0.22
        _SurfaceHighlightPower("Surface Highlight Power", Range(1.0, 8.0)) = 3.0

        [Header(Cutout)]
        [ToggleUI] _AlphaClip("Alpha Clip", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // Legacy properties kept for compatibility with existing material setup.
        [HideInInspector][NoScaleOffset] _Atlas("Atlas (Legacy Unused)", 2D) = "white" {}
        [HideInInspector] _AtlasSize("Atlas Size (Legacy Unused)", Vector) = (32, 32, 0, 0)
        [HideInInspector] _AtlasOriginTopLeft("Atlas Origin Top Left (Legacy Unused)", Float) = 0
        [HideInInspector] _PaddingUV("Atlas Padding (Legacy Unused)", Range(0.0, 0.01)) = 0.001

        [HideInInspector] _Cull("__cull", Float) = 0.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        [HideInInspector] _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0
        [HideInInspector] _QueueOffset("Queue Offset", Float) = 0.0
        [HideInInspector][NoScaleOffset] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _MainTex("Main Tex", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull [_Cull]

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _Color;
            float4 _DepthFoamColor;
            float4 _SurfaceHighlightColor;
            float4 _WaveDirection;
            float4 _AtlasSize;
            float _AtlasOriginTopLeft;
            float _PaddingUV;
            float _WaterMode;
            float _UseSeparateFlowTexture;
            float _FrameDuration;
            float _FlowUVSpeed;
            float _EnableWaterMotion;
            float _waveAmplitude;
            float _waveFrequency;
            float _waveSpeed;
            float _alpha;
            float _Maxalpha;
            float _AmbientStrength;
            float _DirectionalStrength;
            float _AdditionalLightsStrength;
            float _Smoothness;
            float _SpecularStrength;
            float _ReceiveShadows;
            float _CastShadows;
            float _ShadowStrength;
            float _DistanceAlphaNear;
            float _DistanceAlphaFar;
            float _DistanceFadeStart;
            float _DistanceFadeEnd;
            float _TopStart;
            float _Topend;
            float _foamAmount;
            float _FoamIntensity;
            float _DepthDistance;
            float _DepthFoamStrength;
            float _DepthFoamAlpha;
            float _SurfaceHighlight;
            float _SurfaceHighlightPower;
            float _AlphaClip;
            float _Cutoff;
        CBUFFER_END

        float4 _FogColorSurface;
        float _FogStart;
        float _FogEnd;

        TEXTURE2D(_MainTexture);
        SAMPLER(sampler_MainTexture);
        TEXTURE2D(_WaterFlowTexture);
        SAMPLER(sampler_WaterFlowTexture);
        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        float4 _MainTexture_TexelSize;
        float4 _WaterFlowTexture_TexelSize;
        float4 _MainTex_TexelSize;

        float3 _LightDirection;
        float3 _LightPosition;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
            float4 uv2 : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 localUV : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct SurfaceSample
        {
            half4 color;
            half alpha;
        };

        float2 RepeatTileUV(float2 uv)
        {
            return uv - floor(uv);
        }

        bool IsLikelyDefaultWhiteTexture(float4 texelSize)
        {
            return texelSize.z <= 2.5 && texelSize.w <= 2.5;
        }

        // Vertical spritesheet sampler:
        // - frame width is the texture width
        // - frame height equals frame width
        // - total frames = texture height / frame height
        half4 SampleAnimatedWaterSpritesheet(
            TEXTURE2D_PARAM(sheetTexture, sheetSampler),
            float2 localUV,
            float timeSeconds,
            float frameDuration,
            bool flowEnabled,
            float flowSpeed)
        {
            uint texWidthU = 1u;
            uint texHeightU = 1u;
            sheetTexture.GetDimensions(texWidthU, texHeightU);

            float textureWidth = max(1.0, (float)texWidthU);
            float textureHeight = max(1.0, (float)texHeightU);
            float2 texelSize = float2(rcp(textureWidth), rcp(textureHeight));

            float frameWidth = textureWidth;
            float frameHeight = frameWidth;
            float totalFrames = max(1.0, floor(textureHeight / max(frameHeight, 1.0)));

            float safeFrameDuration = max(frameDuration, 1e-4);
            float frameIndex = floor(max(timeSeconds, 0.0) / safeFrameDuration);
            float currentFrame = fmod(frameIndex, totalFrames);

            float2 tiledUV = RepeatTileUV(localUV);
            if (flowEnabled)
                tiledUV.y = frac(tiledUV.y + timeSeconds * flowSpeed);

            float frameSizeY = rcp(totalFrames);
            // Minecraft-like flipbooks are usually authored from top to bottom in the file.
            float frameOffsetY = (totalFrames - 1.0 - currentFrame) * frameSizeY;
            float2 frameMin = float2(0.5 * texelSize.x, frameOffsetY + 0.5 * texelSize.y);
            float2 frameMax = float2(1.0 - 0.5 * texelSize.x, frameOffsetY + frameSizeY - 0.5 * texelSize.y);
            float2 frameUV = lerp(frameMin, frameMax, tiledUV);

            return SAMPLE_TEXTURE2D(sheetTexture, sheetSampler, frameUV);
        }

        half4 SampleStillSheet(float2 localUV, float timeSeconds, bool flowEnabled, float flowSpeed)
        {
            bool useLegacyMainTex = IsLikelyDefaultWhiteTexture(_MainTexture_TexelSize) && !IsLikelyDefaultWhiteTexture(_MainTex_TexelSize);
            if (useLegacyMainTex)
            {
                return SampleAnimatedWaterSpritesheet(
                    TEXTURE2D_ARGS(_MainTex, sampler_MainTex),
                    localUV,
                    timeSeconds,
                    _FrameDuration,
                    flowEnabled,
                    flowSpeed);
            }

            return SampleAnimatedWaterSpritesheet(
                TEXTURE2D_ARGS(_MainTexture, sampler_MainTexture),
                localUV,
                timeSeconds,
                _FrameDuration,
                flowEnabled,
                flowSpeed);
        }

        half4 SampleSelectedWaterSheet(float2 localUV, float timeSeconds)
        {
            bool flowMode = _WaterMode > 0.5;
            bool hasFlowTexture = !IsLikelyDefaultWhiteTexture(_WaterFlowTexture_TexelSize);
            bool useFlowTexture = flowMode && (_UseSeparateFlowTexture > 0.5) && hasFlowTexture;

            if (useFlowTexture)
            {
                return SampleAnimatedWaterSpritesheet(
                    TEXTURE2D_ARGS(_WaterFlowTexture, sampler_WaterFlowTexture),
                    localUV,
                    timeSeconds,
                    _FrameDuration,
                    true,
                    _FlowUVSpeed);
            }

            return SampleStillSheet(localUV, timeSeconds, flowMode, _FlowUVSpeed);
        }

        float2 NormalizeDirection(float2 value, float2 fallback)
        {
            float lenSq = dot(value, value);
            if (lenSq < 1e-5)
                return normalize(fallback);

            return value * rsqrt(lenSq);
        }

        void ApplyWaterWaves(inout float3 positionWS, inout half3 normalWS)
        {
            if (_EnableWaterMotion < 0.5 || _waveAmplitude <= 0.0001 || _waveSpeed <= 0.0001)
                return;

            float2 waveDir = NormalizeDirection(_WaveDirection.xy, float2(1.0, 0.0));
            float2 wavePerp = float2(-waveDir.y, waveDir.x);
            float2 waveDiag = NormalizeDirection(waveDir + wavePerp, waveDir);

            float waveFreq = max(_waveFrequency, 0.001);
            float waveTime = _Time.y * _waveSpeed;
            float topMask = smoothstep(0.3, 0.95, normalWS.y);
            if (topMask <= 0.0001)
                return;

            float phase0 = dot(positionWS.xz, waveDir * waveFreq) + waveTime;
            float phase1 = dot(positionWS.xz, wavePerp * (waveFreq * 1.37)) - waveTime * 1.23;
            float phase2 = dot(positionWS.xz, waveDiag * (waveFreq * 0.79)) + waveTime * 1.71;

            float wave = sin(phase0) + sin(phase1) * 0.6 + sin(phase2) * 0.35;
            float amplitude = _waveAmplitude * topMask;
            positionWS.y += wave * amplitude;

            float2 slope =
                waveDir * cos(phase0) +
                wavePerp * (cos(phase1) * 0.6) +
                waveDiag * (cos(phase2) * 0.35);

            normalWS = normalize(normalWS + half3(-slope.x, 0.0, -slope.y) * (half)(amplitude * waveFreq * 0.6));
        }

        SurfaceSample SampleWaterSurface(float2 localUV)
        {
            half4 sheetSample = SampleSelectedWaterSheet(localUV, _Time.y);

            SurfaceSample surface;
            surface.color.rgb = sheetSample.rgb * _BaseColor.rgb * _Color.rgb;
            // Transparencia controlada apenas por parametros do material (sem usar alpha da textura).
            surface.alpha = saturate(_BaseColor.a * (half)_alpha);
            surface.alpha = min(surface.alpha, (half)_Maxalpha);
            surface.color.a = surface.alpha;
            return surface;
        }

        void ApplyVoxelAlphaClip(half alpha)
        {
            if (_AlphaClip > 0.5)
                clip(alpha - _Cutoff);
        }

        half3 ApplyVoxelFog(half3 color, float3 positionWS)
        {
            float fogRange = max(0.0001, _FogEnd - _FogStart);
            float cameraDistance = distance(GetCameraPositionWS(), positionWS);
            float fogFactor = saturate((cameraDistance - _FogStart) / fogRange);
            return lerp(color, _FogColorSurface.rgb, fogFactor);
        }

        half ComputeDepthFade01(float4 positionCS, float3 positionWS)
        {
            float2 screenUV = positionCS.xy / max(_ScaledScreenParams.xy, float2(1e-5, 1e-5));
            float rawSceneDepth = SampleSceneDepth(screenUV);
            float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
            float waterEyeDepth = -TransformWorldToView(positionWS).z;
            float depthDelta = max(0.0, sceneEyeDepth - waterEyeDepth);
            return saturate((half)(depthDelta / max(_DepthDistance, 1e-4)));
        }

        half ComputeDistanceOpacity(float3 positionWS)
        {
            float fadeRange = max(0.0001, _DistanceFadeEnd - _DistanceFadeStart);
            float cameraDistance = distance(GetCameraPositionWS(), positionWS);
            float fade01 = saturate((cameraDistance - _DistanceFadeStart) / fadeRange);
            fade01 = smoothstep(0.0, 1.0, fade01);
            return lerp((half)_DistanceAlphaNear, (half)_DistanceAlphaFar, (half)fade01);
        }

        half ComputeTopFoam(half3 normalWS)
        {
            half denom = max(0.0001h, (half)(_Topend - _TopStart));
            half topMask = saturate((normalWS.y - (half)_TopStart) / denom);
            return topMask * (half)_foamAmount;
        }

        half ComputeWrappedDiffuse(half3 normalWS, half3 lightDirectionWS)
        {
            const half wrap = 0.18h;
            half ndl = dot(normalWS, lightDirectionWS);
            return saturate((ndl + wrap) / (1.0h + wrap));
        }

        half3 ComputeWaterAmbientLighting(half3 normalWS)
        {
            half skyFactor = lerp(0.55h, 1.0h, saturate(normalWS.y * 0.5h + 0.5h));
            return skyFactor.xxx * (0.2h + (half)_AmbientStrength);
        }

        half3 ComputeWaterDynamicLighting(float3 positionWS, half3 normalWS)
        {
            half3 dynamicLighting = 0.0h;
            half receiveShadowStrength = (_ReceiveShadows > 0.5) ? (half)_ShadowStrength : 0.0h;

            Light mainLight = GetMainLight();
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
            #endif

            half mainShadow = lerp(1.0h, mainLight.shadowAttenuation, receiveShadowStrength);
            half mainDiffuse = ComputeWrappedDiffuse(normalWS, mainLight.direction);
            dynamicLighting += mainLight.color * mainDiffuse * mainLight.distanceAttenuation * mainShadow * (half)_DirectionalStrength;

            #if defined(_ADDITIONAL_LIGHTS)
                uint lightCount = (uint)GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < lightCount; ++lightIndex)
                {
                    Light addLight = GetAdditionalLight(lightIndex, positionWS);
                    half addDiffuse = ComputeWrappedDiffuse(normalWS, addLight.direction);
                    half addShadow = lerp(1.0h, addLight.shadowAttenuation, receiveShadowStrength);
                    dynamicLighting += addLight.color * addDiffuse * addLight.distanceAttenuation * addShadow * (half)_AdditionalLightsStrength;
                }
            #endif

            return dynamicLighting;
        }

        half ComputeWaterSpecularExponent()
        {
            return lerp(12.0h, 160.0h, saturate((half)_Smoothness));
        }

        half ComputeWaterSpecularTerm(half3 normalWS, half3 viewDirWS, half3 lightDirectionWS)
        {
            half ndl = saturate(dot(normalWS, lightDirectionWS));
            if (ndl <= 0.0001h)
                return 0.0h;

            half3 halfDir = SafeNormalize(lightDirectionWS + viewDirWS);
            half ndh = saturate(dot(normalWS, halfDir));
            half specExponent = ComputeWaterSpecularExponent();
            half normalization = (specExponent + 8.0h) * 0.125h;
            half spec = pow(ndh, specExponent) * normalization;
            half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), 5.0h);
            spec *= lerp(0.35h, 1.0h, fresnel);
            spec *= lerp(0.7h, 1.2h, saturate((half)_Smoothness));
            return spec * ndl * (half)_SpecularStrength;
        }

        half3 ComputeWaterSpecularLighting(float3 positionWS, half3 normalWS, half3 viewDirWS)
        {
            half3 specularLighting = 0.0h;
            half receiveShadowStrength = (_ReceiveShadows > 0.5) ? (half)_ShadowStrength : 0.0h;

            Light mainLight = GetMainLight();
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
            #endif

            half mainShadow = lerp(1.0h, mainLight.shadowAttenuation, receiveShadowStrength);
            half mainSpecular = ComputeWaterSpecularTerm(normalWS, viewDirWS, mainLight.direction);
            specularLighting += mainLight.color * mainSpecular * mainLight.distanceAttenuation * mainShadow * (half)_DirectionalStrength;

            #if defined(_ADDITIONAL_LIGHTS)
                uint lightCount = (uint)GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < lightCount; ++lightIndex)
                {
                    Light addLight = GetAdditionalLight(lightIndex, positionWS);
                    half addShadow = lerp(1.0h, addLight.shadowAttenuation, receiveShadowStrength);
                    half addSpecular = ComputeWaterSpecularTerm(normalWS, viewDirWS, addLight.direction);
                    specularLighting += addLight.color * addSpecular * addLight.distanceAttenuation * addShadow * (half)_AdditionalLightsStrength;
                }
            #endif

            return specularLighting * _SurfaceHighlightColor.rgb;
        }

        Varyings ForwardVertex(Attributes input)
        {
            Varyings output = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

            output.positionWS = positionInputs.positionWS;
            output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
            output.localUV = input.uv0;

            ApplyWaterWaves(output.positionWS, output.normalWS);

            output.positionCS = TransformWorldToHClip(output.positionWS);
            return output;
        }

        half4 ForwardFragment(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            half distanceOpacity = ComputeDistanceOpacity(input.positionWS);
            if (distanceOpacity <= 0.0001h)
                clip(-1.0h);

            SurfaceSample surface = SampleWaterSurface(input.localUV);
            ApplyVoxelAlphaClip(surface.alpha * distanceOpacity);

            half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
            half3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);
            half3 lighting = ComputeWaterAmbientLighting(normalWS);
            lighting += ComputeWaterDynamicLighting(input.positionWS, normalWS);
            half3 litSurface = surface.color.rgb * lighting;
            half3 specularLighting = ComputeWaterSpecularLighting(input.positionWS, normalWS, viewDirWS);

            half foam = ComputeTopFoam(normalWS);
            half3 foamColor = lerp(litSurface, half3(1.0h, 1.0h, 1.0h), saturate(foam * (half)_FoamIntensity));

            half depthFade = ComputeDepthFade01(input.positionCS, input.positionWS);
            half depthFoam = saturate((1.0h - depthFade) * (half)_DepthFoamStrength);

            half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), (half)_SurfaceHighlightPower);
            half3 highlight = _SurfaceHighlightColor.rgb * fresnel * (half)_SurfaceHighlight;

            half3 finalColor = litSurface + specularLighting + (foamColor - litSurface) * (half)0.45;
            finalColor = lerp(finalColor, _DepthFoamColor.rgb, depthFoam);
            finalColor += highlight;
            finalColor = ApplyVoxelFog(finalColor, input.positionWS);

            half finalAlpha = saturate(surface.alpha + foam * 0.05h + depthFoam * (half)_DepthFoamAlpha);
            finalAlpha *= distanceOpacity;
            finalAlpha = min(finalAlpha, (half)_Maxalpha);
            return half4(finalColor, finalAlpha);
        }

        Varyings PassThroughVertex(Attributes input)
        {
            Varyings output = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

            output.positionWS = positionInputs.positionWS;
            output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
            output.localUV = input.uv0;

            ApplyWaterWaves(output.positionWS, output.normalWS);

            output.positionCS = TransformWorldToHClip(output.positionWS);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
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
            #pragma target 4.5
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output = PassThroughVertex(input);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - output.positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_CastShadows < 0.5)
                    clip(-1.0h);

                half distanceOpacity = ComputeDistanceOpacity(input.positionWS);
                if (distanceOpacity <= 0.0001h)
                    clip(-1.0h);

                SurfaceSample surface = SampleWaterSurface(input.localUV);
                ApplyVoxelAlphaClip(surface.alpha * distanceOpacity);
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
            #pragma target 4.5
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing

            Varyings DepthOnlyVertex(Attributes input)
            {
                return PassThroughVertex(input);
            }

            half DepthOnlyFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half distanceOpacity = ComputeDistanceOpacity(input.positionWS);
                if (distanceOpacity <= 0.0001h)
                    clip(-1.0h);

                SurfaceSample surface = SampleWaterSurface(input.localUV);
                ApplyVoxelAlphaClip(surface.alpha * distanceOpacity);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing

            Varyings DepthNormalsVertex(Attributes input)
            {
                return PassThroughVertex(input);
            }

            half4 DepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half distanceOpacity = ComputeDistanceOpacity(input.positionWS);
                if (distanceOpacity <= 0.0001h)
                    clip(-1.0h);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                SurfaceSample surface = SampleWaterSurface(input.localUV);
                ApplyVoxelAlphaClip(surface.alpha * distanceOpacity);

                return half4(normalWS, 0.0h);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
