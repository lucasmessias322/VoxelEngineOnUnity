Shader "Voxel/URP/VoxelWaterURP"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2, 1.0)

        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0.0, 2.0)) = 1.0
        _NormalMapTiling("Normal Triplanar Tiling", Vector) = (1.8, 1.8, 0, 0)
        _TriplanarBlendSharpness("Triplanar Blend Sharpness", Range(1.0, 8.0)) = 4.0
        _WaterNormalStrengthA("Water Normal Strength A", Range(0.0, 2.0)) = 1.0
        _WaterNormalStrengthB("Water Normal Strength B", Range(0.0, 2.0)) = 1.0
        _WaterNormalScaleA("Water Normal Scale A", Range(0.1, 4.0)) = 1.0
        _WaterNormalScaleB("Water Normal Scale B", Range(0.1, 4.0)) = 2.2
        _WaterNormalSpeed("Water Normal Speed", Vector) = (0.06, 0.04, 0, 0)
        _WaterNormalSpeedBMultiplier("Water Normal Speed B Multiplier", Range(0.1, 4.0)) = 1.35
        _WaterNormalRotationA("Water Normal Rotation A", Range(-180.0, 180.0)) = 18.0
        _WaterNormalRotationB("Water Normal Rotation B", Range(-180.0, 180.0)) = -32.0
        _WaterNormalWarpScale("Water Normal Warp Scale", Range(0.01, 2.0)) = 0.2
        _WaterNormalWarpStrength("Water Normal Warp Strength", Range(0.0, 1.0)) = 0.3
        [ToggleUI] _UseWaterAlbedoTexture("Use Water Albedo Texture", Float) = 0.0
        _WaterTextureAlphaInfluence("Water Texture Alpha Influence", Range(0.0, 1.0)) = 0.0
        _ContactEdgeFadeDistance("Contact Edge Fade Distance", Range(0.01, 2.0)) = 0.35
        _ContactEdgeFadeExponent("Contact Edge Fade Exponent", Range(0.25, 8.0)) = 2.4
        _ContactEdgeMinAlpha("Contact Edge Min Alpha", Range(0.0, 1.0)) = 0.0
        _RefractionStrength("Refraction Strength", Range(0.0, 0.15)) = 0.025
        _RefractionBlend("Refraction Blend", Range(0.0, 1.0)) = 0.65
        [HDR] _DepthShallowColor("Shallow Color", Color) = (0.3, 0.82, 0.78, 1.0)
        [HDR] _DepthDeepColor("Deep Color", Color) = (0.08, 0.24, 0.42, 1.0)
        _DepthStartDistance("Depth Offset", Range(0.0, 8.0)) = 0.15
        _DepthColorDistance("Depth Range", Range(0.05, 32.0)) = 4.5
        _DepthAbsorptionStrength("Depth Curve", Range(0.25, 4.0)) = 1.2
        _DepthShallowAlpha("Shallow Opacity", Range(0.0, 1.0)) = 0.18
        _DepthDeepAlpha("Deep Opacity", Range(0.0, 1.0)) = 0.92
        _VoxelSkyLightMultiplier("Voxel Sky Light Multiplier", Range(0.0, 1.0)) = 1.0
        _VoxelLightStrength("Voxel Light Strength", Range(0.0, 2.0)) = 1.0
        _MinLight("Voxel Min Light", Range(0.0, 1.0)) = 0.0
        [HDR] _WaterNightColor("Water Night Visibility Color", Color) = (0.08, 0.32, 0.42, 1.0)
        _AmbientStrength("Water Night Visibility", Range(0.0, 1.0)) = 0.28

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [HideInInspector] _Surface("__surface", Float) = 1.0
        [HideInInspector] _Cull("__cull", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [ToggleUI] _UseAtlasForAlbedo("Use Atlas For Water Albedo", Float) = 0.0
        [HideInInspector][NoScaleOffset] _Atlas("Atlas", 2D) = "white" {}
        [HideInInspector] _AtlasSize("Atlas Size (Tiles XY)", Vector) = (32, 32, 0, 0)
        [HideInInspector] _AtlasOriginTopLeft("Atlas Origin Top Left", Float) = 0
        [HideInInspector] _PaddingUV("Atlas Padding", Range(0.0, 0.01)) = 0.0

        [HideInInspector] _MainTex("BaseMap (Legacy)", 2D) = "white" {}
        [HideInInspector] _Color("Color (Legacy)", Color) = (1, 1, 1, 1)
        [HideInInspector] _MainTexture("Water Still Spritesheet (Legacy)", 2D) = "white" {}
        [HideInInspector] _WaterFlowTexture("Water Flow Texture (Legacy)", 2D) = "white" {}
        [HideInInspector] _NormalMap("Normal Map (Legacy)", 2D) = "bump" {}
        [HideInInspector] _NormalTexture("Normal Texture (Legacy)", 2D) = "bump" {}
        [HideInInspector] _Normal("Normal (Legacy)", 2D) = "bump" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        LOD 300
        Blend One OneMinusSrcAlpha
        ZWrite [_ZWrite]
        Cull [_Cull]

        HLSLINCLUDE
        #define _SPECULAR_SETUP 1
        #define _NORMALMAP 1
        #define _SURFACE_TYPE_TRANSPARENT 1
        #define _ALPHAPREMULTIPLY_ON 1

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

        #if defined(LOD_FADE_CROSSFADE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
        #endif

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseMap_TexelSize;
            float4 _AtlasSize;
            half4 _BaseColor;
            half4 _SpecColor;
            float4 _NormalMapTiling;
            float4 _WaterNormalSpeed;
            half4 _DepthShallowColor;
            half4 _DepthDeepColor;
            half _AtlasOriginTopLeft;
            half _PaddingUV;
            half _Cutoff;
            half _Smoothness;
            half _UseAtlasForAlbedo;
            half _BumpScale;
            half _TriplanarBlendSharpness;
            half _WaterNormalStrengthA;
            half _WaterNormalStrengthB;
            half _WaterNormalScaleA;
            half _WaterNormalScaleB;
            half _WaterNormalSpeedBMultiplier;
            half _WaterNormalRotationA;
            half _WaterNormalRotationB;
            half _WaterNormalWarpScale;
            half _WaterNormalWarpStrength;
            half _UseWaterAlbedoTexture;
            half _WaterTextureAlphaInfluence;
            half _ContactEdgeFadeDistance;
            half _ContactEdgeFadeExponent;
            half _ContactEdgeMinAlpha;
            half _RefractionStrength;
            half _RefractionBlend;
            half _DepthStartDistance;
            half _DepthColorDistance;
            half _DepthAbsorptionStrength;
            half _DepthShallowAlpha;
            half _DepthDeepAlpha;
            half _VoxelSkyLightMultiplier;
            half _VoxelLightStrength;
            half _MinLight;
            half4 _WaterNightColor;
            half _AmbientStrength;
            half _Surface;
        CBUFFER_END

        TEXTURE2D(_MainTexture);
        SAMPLER(sampler_MainTexture);
        float4 _MainTexture_TexelSize;
        TEXTURE2D(_Atlas);
        SAMPLER(sampler_Atlas);

        float GetTriplanarNormalScale()
        {
            float2 tiling = abs(_NormalMapTiling.xy);
            return max(max(tiling.x, tiling.y), 0.0001);
        }

        float2 GetAtlasTileSize()
        {
            return 1.0 / max(_AtlasSize.xy, float2(1.0, 1.0));
        }

        float2 RepeatTileUV(float2 uv)
        {
            return uv - floor(uv);
        }

        float2 RotateUV(float2 uv, float angleRadians)
        {
            float sinAngle = sin(angleRadians);
            float cosAngle = cos(angleRadians);
            return float2(
                cosAngle * uv.x - sinAngle * uv.y,
                sinAngle * uv.x + cosAngle * uv.y);
        }

        half3 RotateTangentNormalXY(half3 normalTS, float angleRadians)
        {
            float2 rotatedXY = RotateUV(normalTS.xy, angleRadians);
            return normalize(half3((half)rotatedXY.x, (half)rotatedXY.y, normalTS.z));
        }

        float2 ResolveAtlasUV(float2 localUV, float2 atlasOrigin, float2 atlasSize)
        {
            float2 tileSize = max(atlasSize, float2(1e-5, 1e-5));
            float2 repeatedUV = RepeatTileUV(localUV);
            float2 normalizedPadding = saturate(_PaddingUV / max(tileSize, float2(1e-5, 1e-5)));
            repeatedUV = lerp(normalizedPadding, 1.0 - normalizedPadding, repeatedUV);

            float2 resolvedOrigin = atlasOrigin;
            if (_AtlasOriginTopLeft > 0.5h)
                resolvedOrigin.y = 1.0 - atlasOrigin.y - tileSize.y;

            return resolvedOrigin + repeatedUV * tileSize;
        }

        bool LooksLikeDefaultWhite(half4 sampleColor)
        {
            return all(abs(sampleColor - half4(1.0h, 1.0h, 1.0h, 1.0h)) < half4(0.0001h, 0.0001h, 0.0001h, 0.0001h));
        }

        half4 SampleWaterAlbedoAlpha(float2 localUV, float2 atlasOrigin, float2 atlasSize)
        {
            if (_UseWaterAlbedoTexture <= 0.5h)
                return half4(1.0h, 1.0h, 1.0h, 1.0h);

            if (_UseAtlasForAlbedo <= 0.5h)
            {
                float textureWidth = max(_MainTexture_TexelSize.z, 1.0);
                float textureHeight = max(_MainTexture_TexelSize.w, 1.0);
                float frameCount = max(floor(textureHeight / textureWidth + 0.5), 1.0);
                float frameSizeY = rcp(frameCount);
                float frameOffsetY = 1.0 - frameSizeY;
                float2 localMainUV = RepeatTileUV(localUV);
                float2 mainTextureUV = float2(localMainUV.x, frameOffsetY + localMainUV.y * frameSizeY);

                half4 mainTextureSample = SAMPLE_TEXTURE2D(_MainTexture, sampler_MainTexture, mainTextureUV);
                if (!LooksLikeDefaultWhite(mainTextureSample))
                    return mainTextureSample;

                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, localMainUV);
            }

            float2 resolvedAtlasSize = max(atlasSize, GetAtlasTileSize());
            float2 atlasUV = ResolveAtlasUV(localUV, atlasOrigin, resolvedAtlasSize);

            half4 atlasSample = SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, atlasUV);
            if (!LooksLikeDefaultWhite(atlasSample))
                return atlasSample;

            half4 mainSample = SAMPLE_TEXTURE2D(_MainTexture, sampler_MainTexture, atlasUV);
            if (!LooksLikeDefaultWhite(mainSample))
                return mainSample;

            return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, localUV);
        }

        float3 ResolveTriplanarSigns(half3 normalWS)
        {
            return float3(
                normalWS.x >= 0.0h ? 1.0 : -1.0,
                normalWS.y >= 0.0h ? 1.0 : -1.0,
                normalWS.z >= 0.0h ? 1.0 : -1.0);
        }

        half3 SampleWaterNormalMap(float2 uv, float rotationRadians)
        {
            half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, RotateUV(uv, rotationRadians)), _BumpScale);
            return RotateTangentNormalXY(normalTS, rotationRadians);
        }

        float3 GetWaterNormalWarp(float3 positionWS, float time)
        {
            float warpStrength = max((float)_WaterNormalWarpStrength, 0.0);
            if (warpStrength <= 0.0001)
                return float3(0.0, 0.0, 0.0);

            float warpScale = max((float)_WaterNormalWarpScale, 0.0001);
            float3 warpInput = positionWS * warpScale + float3(time * 0.11, time * 0.07, time * 0.09);

            float3 warpA = float3(
                sin(warpInput.y * 1.17 + warpInput.z * 1.31),
                sin(warpInput.z * 1.23 + warpInput.x * 1.41),
                sin(warpInput.x * 1.11 + warpInput.y * 1.53));

            float3 warpB = float3(
                cos(warpInput.z * 0.87 - warpInput.y * 1.09),
                cos(warpInput.x * 0.93 - warpInput.z * 1.03),
                cos(warpInput.y * 0.91 - warpInput.x * 1.13));

            return (warpA + warpB * 0.5) * warpStrength;
        }

        half3 SampleTriplanarNormalWS(float3 positionWS, half3 baseNormalWS, float tilingMultiplier, float rotationRadians)
        {
            half3 normalizedBaseNormal = NormalizeNormalPerPixel(baseNormalWS);
            if (_BumpScale <= 0.0001h)
                return normalizedBaseNormal;

            float3 normalSigns = ResolveTriplanarSigns(normalizedBaseNormal);
            half3 blendWeights = pow(saturate(abs(normalizedBaseNormal)), (half)_TriplanarBlendSharpness);
            half blendWeightSum = max(0.0001h, blendWeights.x + blendWeights.y + blendWeights.z);
            blendWeights /= blendWeightSum;

            float normalScale = GetTriplanarNormalScale() * max(tilingMultiplier, 0.0001);
            float2 uvX = positionWS.zy * float2(-normalSigns.x, 1.0) * normalScale;
            float2 uvY = positionWS.xz * float2(1.0, -normalSigns.y) * normalScale;
            float2 uvZ = positionWS.xy * float2(normalSigns.z, 1.0) * normalScale;

            float rotationX = rotationRadians;
            float rotationY = -rotationRadians * 0.82;
            float rotationZ = rotationRadians * 1.18;

            half3 normalX = SampleWaterNormalMap(uvX, rotationX);
            half3 normalY = SampleWaterNormalMap(uvY, rotationY);
            half3 normalZ = SampleWaterNormalMap(uvZ, rotationZ);

            half3 worldNormalX = half3((half)normalSigns.x * normalX.z, normalX.y, (half)(-normalSigns.x) * normalX.x);
            half3 worldNormalY = half3(normalY.x, (half)normalSigns.y * normalY.z, (half)(-normalSigns.y) * normalY.y);
            half3 worldNormalZ = half3((half)normalSigns.z * normalZ.x, normalZ.y, (half)normalSigns.z * normalZ.z);

            half3 detailNormal = normalize(worldNormalX * blendWeights.x + worldNormalY * blendWeights.y + worldNormalZ * blendWeights.z);
            half3 flatTriplanarNormal = normalize(half3(
                (half)normalSigns.x * blendWeights.x,
                (half)normalSigns.y * blendWeights.y,
                (half)normalSigns.z * blendWeights.z));

            return normalize(normalizedBaseNormal + (detailNormal - flatTriplanarNormal));
        }

        half3 SampleWaterAnimatedNormalWS(float3 positionWS, half3 baseNormalWS)
        {
            half3 normalizedBaseNormal = NormalizeNormalPerPixel(baseNormalWS);
            if (_BumpScale <= 0.0001h)
                return normalizedBaseNormal;

            float time = _Time.y;
            float3 scrollA = float3(_WaterNormalSpeed.x, 0.0, _WaterNormalSpeed.y) * time;
            float3 scrollB = float3(-_WaterNormalSpeed.x, 0.0, -_WaterNormalSpeed.y) * (time * _WaterNormalSpeedBMultiplier);
            float3 warp = GetWaterNormalWarp(positionWS, time);
            float rotationA = radians((float)_WaterNormalRotationA);
            float rotationB = radians((float)_WaterNormalRotationB);

            half3 layerA = SampleTriplanarNormalWS(positionWS + scrollA + warp, normalizedBaseNormal, _WaterNormalScaleA, rotationA);
            half3 layerB = SampleTriplanarNormalWS(positionWS + scrollB - warp * 0.85, normalizedBaseNormal, _WaterNormalScaleB, rotationB);

            layerA = normalize(normalizedBaseNormal + (layerA - normalizedBaseNormal) * _WaterNormalStrengthA);
            layerB = normalize(normalizedBaseNormal + (layerB - normalizedBaseNormal) * _WaterNormalStrengthB);

            return normalize(layerA + layerB - normalizedBaseNormal);
        }

        float GetWaterDepthDifference(float2 normalizedScreenSpaceUV, float3 positionWS)
        {
            float rawSceneDepth = SampleSceneDepth(normalizedScreenSpaceUV);
            float sceneEyeDepth = (unity_OrthoParams.w == 0.0) ? LinearEyeDepth(rawSceneDepth, _ZBufferParams) : LinearDepthToEyeDepth(rawSceneDepth);
            float surfaceEyeDepth = LinearEyeDepth(positionWS, GetWorldToViewMatrix());

            return max(sceneEyeDepth - surfaceEyeDepth, 0.0);
        }

        half GetWaterDepthFactor(float2 normalizedScreenSpaceUV, float3 positionWS)
        {
            float waterDepth = GetWaterDepthDifference(normalizedScreenSpaceUV, positionWS);
            float depthOffset = max((float)_DepthStartDistance, 0.0);
            float depthRange = max((float)_DepthColorDistance, 0.0001);
            float normalizedDepth = saturate(max(waterDepth - depthOffset, 0.0) / depthRange);
            float depthCurve = max((float)_DepthAbsorptionStrength, 0.0001);

            return (half)pow(normalizedDepth, depthCurve);
        }

        half GetWaterContactEdgeFade(float waterDepth)
        {
            float fadeDistance = max((float)_ContactEdgeFadeDistance, 0.0001);
            half edgeFade = (half)saturate(waterDepth / fadeDistance);
            return (half)pow(edgeFade, max((float)_ContactEdgeFadeExponent, 0.0001));
        }

        half GetWaterContactEdgeAlpha(float2 normalizedScreenSpaceUV, float3 positionWS)
        {
            float waterDepth = GetWaterDepthDifference(normalizedScreenSpaceUV, positionWS);
            return lerp(_ContactEdgeMinAlpha, 1.0h, GetWaterContactEdgeFade(waterDepth));
        }

        half GetWaterContactEdgeFade(float2 normalizedScreenSpaceUV, float3 positionWS)
        {
            float waterDepth = GetWaterDepthDifference(normalizedScreenSpaceUV, positionWS);
            return GetWaterContactEdgeFade(waterDepth);
        }

        half3 ResolveWaterContactEdgeNormalWS(half3 baseNormalWS, half3 detailNormalWS, half contactEdgeFade)
        {
            half3 normalizedBaseNormal = NormalizeNormalPerPixel(baseNormalWS);
            half3 normalizedDetailNormal = NormalizeNormalPerPixel(detailNormalWS);
            return normalize(lerp(normalizedBaseNormal, normalizedDetailNormal, contactEdgeFade));
        }

        half3 GetWaterDepthColor(half depthFactor)
        {
            return lerp(_DepthShallowColor.rgb, _DepthDeepColor.rgb, depthFactor);
        }

        half3 GetWaterDepthTint(half depthFactor)
        {
            return saturate(GetWaterDepthColor(depthFactor));
        }

        half3 GetWaterDepthEmission(half depthFactor)
        {
            return max(GetWaterDepthColor(depthFactor) - 1.0h, 0.0h);
        }

        half GetWaterDepthAlpha(half depthFactor)
        {
            return lerp(_DepthShallowAlpha, _DepthDeepAlpha, depthFactor);
        }

        void ApplyWaterDepthStyling(half depthFactor, half contactEdgeAlpha, half contactEdgeFade, inout SurfaceData surfaceData)
        {
            half3 depthTint = GetWaterDepthTint(depthFactor);
            half depthAlpha = GetWaterDepthAlpha(depthFactor);
            surfaceData.albedo *= depthTint;
            surfaceData.alpha *= depthAlpha * contactEdgeAlpha;
            surfaceData.smoothness *= contactEdgeFade;
            surfaceData.emission += GetWaterDepthEmission(depthFactor) * (depthAlpha * contactEdgeAlpha);
        }

        half3 SampleWaterRefractionDelta(float2 normalizedScreenSpaceUV, half3 normalWS, half depthFactor, half surfaceAlpha, half contactEdgeAlpha)
        {
            if (_RefractionStrength <= 0.0001h || _RefractionBlend <= 0.0001h)
                return half3(0.0h, 0.0h, 0.0h);

            float3 normalVS = normalize(mul((float3x3)GetWorldToViewMatrix(), normalWS));
            float2 distortion = normalVS.xy * (_RefractionStrength * lerp(0.35h, 1.0h, depthFactor));
            float2 sceneUV = normalizedScreenSpaceUV;
            float2 refractedUV = saturate(sceneUV + distortion);

            half3 baseSceneColor = SampleSceneColor(sceneUV);
            half3 refractedSceneColor = SampleSceneColor(refractedUV);
            half refractionVisibility = saturate((1.0h - surfaceAlpha) * _RefractionBlend) * contactEdgeAlpha;

            return (refractedSceneColor - baseSceneColor) * refractionVisibility;
        }

        half3 ResolveWaterBlockLightColor(half blockLight, half3 blockLightColor)
        {
            half hasColor = step(0.0001h, dot(blockLightColor, half3(1.0h, 1.0h, 1.0h)));
            return lerp(blockLight.xxx, saturate(blockLightColor), hasColor);
        }

        half3 ComputeWaterVoxelLightColor(half skyLight, half blockLight, half3 blockLightColor)
        {
            half skyVoxelLight = saturate(skyLight) * (half)_VoxelSkyLightMultiplier * (half)_VoxelLightStrength;
            half3 resolvedBlockLight = ResolveWaterBlockLightColor(blockLight, blockLightColor) * (half)_VoxelLightStrength;
            half minLight = (half)_MinLight;
            return saturate(max(half3(minLight, minLight, minLight), max(skyVoxelLight.xxx, resolvedBlockLight)));
        }

        half3 GetWaterMinimumVisibilityColor(half depthFactor)
        {
            half3 depthTint = GetWaterDepthTint(depthFactor);
            half3 nightTint = saturate(_WaterNightColor.rgb);
            return max(depthTint, nightTint);
        }

        void ApplyWaterMinimumVisibility(half depthFactor, half surfaceAlpha, inout half3 color)
        {
            half visibilityStrength = saturate((half)_AmbientStrength);
            // Keeps transparent water readable when night PBR and voxel light both collapse toward black.
            half3 visibilityFloor = GetWaterMinimumVisibilityColor(depthFactor) * (visibilityStrength * saturate(surfaceAlpha));
            color = max(color, visibilityFloor);
        }

        inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
        {
            half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
            outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
            outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
            outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);
            outSurfaceData.metallic = 0.0h;
            outSurfaceData.specular = _SpecColor.rgb;
            outSurfaceData.smoothness = _Smoothness;
            outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
            outSurfaceData.occlusion = 1.0h;
            outSurfaceData.emission = 0.0h;
            outSurfaceData.clearCoatMask = 0.0h;
            outSurfaceData.clearCoatSmoothness = 0.0h;
        }

        inline void InitializeTriplanarLitSurfaceData(float2 localUV, float2 atlasOrigin, float2 atlasSize, float3 positionWS, half3 baseNormalWS, out SurfaceData outSurfaceData, out half3 triplanarNormalWS)
        {
            half4 albedoAlpha = SampleWaterAlbedoAlpha(localUV, atlasOrigin, atlasSize);
            half resolvedAlpha = lerp(1.0h, albedoAlpha.a, saturate(_WaterTextureAlphaInfluence));
            outSurfaceData.alpha = Alpha(resolvedAlpha, _BaseColor, _Cutoff);
            outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
            outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);
            outSurfaceData.metallic = 0.0h;
            outSurfaceData.specular = _SpecColor.rgb;
            outSurfaceData.smoothness = _Smoothness;
            outSurfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
            outSurfaceData.occlusion = 1.0h;
            outSurfaceData.emission = 0.0h;
            outSurfaceData.clearCoatMask = 0.0h;
            outSurfaceData.clearCoatSmoothness = 0.0h;

            triplanarNormalWS = SampleWaterAnimatedNormalWS(positionWS, baseNormalWS);
        }

        struct ForwardAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 localUV : TEXCOORD0;
            float2 atlasOrigin : TEXCOORD1;
            float4 voxelExtra : TEXCOORD2;
            float4 voxelLightData : TEXCOORD3;
            half4 blockLightColor : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct ForwardVaryings
        {
            float2 localUV : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
            float4 atlasData : TEXCOORD10;

            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                half4 fogFactorAndVertexLight : TEXCOORD3;
            #else
                half fogFactor : TEXCOORD3;
            #endif

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD4;
            #endif

            DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);

            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD6;
            #endif

            half voxelSkyLight : TEXCOORD7;
            half voxelBlockLight : TEXCOORD8;
            half3 voxelBlockLightColor : TEXCOORD9;

            float4 positionCS : SV_POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float2 GetWaterNormalizedScreenSpaceUV(float4 positionCS)
        {
            #if defined(UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION)
                float2 preRotatedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                switch (UNITY_DISPLAY_ORIENTATION_PRETRANSFORM)
                {
                    default:
                    case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_0:
                        return preRotatedScreenSpaceUV;
                    case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_90:
                        return float2(1 - preRotatedScreenSpaceUV.y, preRotatedScreenSpaceUV.x);
                    case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_180:
                        return float2(1 - preRotatedScreenSpaceUV.x, 1 - preRotatedScreenSpaceUV.y);
                    case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_270:
                        return float2(preRotatedScreenSpaceUV.y, 1 - preRotatedScreenSpaceUV.x);
                }
            #else
                return GetNormalizedScreenSpaceUV(positionCS);
            #endif
        }

        void InitializeWaterInputData(ForwardVaryings input, half3 normalWS, out InputData inputData)
        {
            inputData = (InputData)0;
            inputData.positionWS = input.positionWS;

            #if defined(DEBUG_DISPLAY)
                inputData.positionCS = input.positionCS;
            #endif

            inputData.normalWS = NormalizeNormalPerPixel(normalWS);
            inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif

            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
                inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
            #else
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
            #endif
            inputData.normalizedScreenSpaceUV = GetWaterNormalizedScreenSpaceUV(input.positionCS);

            #if defined(DEBUG_DISPLAY)
                #if defined(LIGHTMAP_ON)
                    inputData.staticLightmapUV = input.staticLightmapUV;
                #else
                    inputData.vertexSH = input.vertexSH;
                #endif
                #if defined(USE_APV_PROBE_OCCLUSION)
                    inputData.probeOcclusion = input.probeOcclusion;
                #endif
            #endif
        }

        void InitializeWaterBakedGIData(ForwardVaryings input, inout InputData inputData)
        {
            #if defined(_SCREEN_SPACE_IRRADIANCE)
                inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy);
            #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                inputData.bakedGI = SAMPLE_GI(
                    input.vertexSH,
                    GetAbsolutePositionWS(inputData.positionWS),
                    inputData.normalWS,
                    inputData.viewDirectionWS,
                    input.positionCS.xy,
                    input.probeOcclusion,
                    inputData.shadowMask);
            #else
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
            #endif
        }

        ForwardVaryings WaterLitPassVertex(ForwardAttributes input)
        {
            ForwardVaryings output = (ForwardVaryings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

            half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
            half fogFactor = 0;

            #if !defined(_FOG_FRAGMENT)
                fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
            #endif

            output.localUV = input.localUV;
            output.positionWS = vertexInput.positionWS;
            output.normalWS = normalInput.normalWS;
            output.atlasData = float4(input.atlasOrigin, max(input.voxelLightData.xy, GetAtlasTileSize()));
            output.voxelSkyLight = saturate((half)input.voxelExtra.x);
            output.voxelBlockLight = saturate((half)input.voxelLightData.z);
            output.voxelBlockLightColor = saturate(input.blockLightColor.rgb);

            #if defined(LIGHTMAP_ON)
                output.staticLightmapUV = 0.0.xx;
            #endif

            OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
            #else
                output.fogFactor = fogFactor;
            #endif

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(vertexInput);
            #endif

            output.positionCS = vertexInput.positionCS;
            return output;
        }

        half4 WaterLitPassFragment(ForwardVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(input.positionCS);
            #endif

            SurfaceData surfaceData;
            half3 triplanarNormalWS;
            InitializeTriplanarLitSurfaceData(input.localUV, input.atlasData.xy, input.atlasData.zw, input.positionWS, input.normalWS, surfaceData, triplanarNormalWS);
            float2 normalizedScreenSpaceUV = GetWaterNormalizedScreenSpaceUV(input.positionCS);
            half contactEdgeAlpha = GetWaterContactEdgeAlpha(normalizedScreenSpaceUV, input.positionWS);
            half contactEdgeFade = GetWaterContactEdgeFade(normalizedScreenSpaceUV, input.positionWS);
            half3 resolvedNormalWS = ResolveWaterContactEdgeNormalWS(input.normalWS, triplanarNormalWS, contactEdgeFade);

            InputData inputData;
            InitializeWaterInputData(input, resolvedNormalWS, inputData);
            InitializeWaterBakedGIData(input, inputData);
            half depthFactor = GetWaterDepthFactor(normalizedScreenSpaceUV, input.positionWS);
            ApplyWaterDepthStyling(depthFactor, contactEdgeAlpha, contactEdgeFade, surfaceData);
            half3 voxelLightColor = ComputeWaterVoxelLightColor(input.voxelSkyLight, input.voxelBlockLight, input.voxelBlockLightColor);

            half4 color = UniversalFragmentPBR(inputData, surfaceData);
            color.rgb += SampleWaterRefractionDelta(normalizedScreenSpaceUV, resolvedNormalWS, depthFactor, surfaceData.alpha, contactEdgeAlpha);
            color.rgb *= voxelLightColor;
            ApplyWaterMinimumVisibility(depthFactor, surfaceData.alpha, color.rgb);
            color.rgb = MixFog(color.rgb, inputData.fogCoord);
            return color;
        }

        struct DepthNormalsAttributesCustom
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct DepthNormalsVaryingsCustom
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        DepthNormalsVaryingsCustom WaterDepthNormalsVertex(DepthNormalsAttributesCustom input)
        {
            DepthNormalsVaryingsCustom output = (DepthNormalsVaryingsCustom)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

            output.positionCS = vertexInput.positionCS;
            output.positionWS = vertexInput.positionWS;
            output.normalWS = normalInput.normalWS;
            return output;
        }

        void WaterDepthNormalsFragment(
            DepthNormalsVaryingsCustom input,
            out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
        )
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(input.positionCS);
            #endif

            float2 normalizedScreenSpaceUV = GetWaterNormalizedScreenSpaceUV(input.positionCS);
            half contactEdgeFade = GetWaterContactEdgeFade(normalizedScreenSpaceUV, input.positionWS);
            half3 detailNormalWS = SampleWaterAnimatedNormalWS(input.positionWS, input.normalWS);
            half3 normalWS = ResolveWaterContactEdgeNormalWS(input.normalWS, detailNormalWS, contactEdgeFade);
            outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0h);

            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex WaterLitPassVertex
            #pragma fragment WaterLitPassFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
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
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
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
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex WaterDepthNormalsVertex
            #pragma fragment WaterDepthNormalsFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }

            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex UniversalVertexMeta
            #pragma fragment UniversalFragmentMetaLit
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitMetaPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
