Shader "Voxel/URP/Voxel Blocks Unlit Lit"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _Atlas("Atlas", 2D) = "white" {}
        [NoScaleOffset] _GrassSideOverlay("Grass Side Overlay", 2D) = "black" {}
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "black" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _GrassTint("Grass Tint", Color) = (0.38, 0.52, 0.25, 1)
        [HDR] _VoxelEmissionTint("Emission Tint", Color) = (1, 1, 1, 1)
        _VoxelEmissionStrength("Emission Strength", Range(0.0, 8.0)) = 0.0
        [HideInInspector] _BiomeTintBlendEnabled("Biome Tint Blend Enabled", Float) = 0
        [HideInInspector] _BiomeTintOriginXZ("Biome Tint Origin XZ", Vector) = (0, 0, 0, 0)
        [HideInInspector] _BiomeTintInvSizeXZ("Biome Tint Inv Size XZ", Vector) = (0.0625, 0.0625, 0, 0)
        [HideInInspector] _GrassTintCorner00("Grass Tint Corner 00", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner10("Grass Tint Corner 10", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner01("Grass Tint Corner 01", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner11("Grass Tint Corner 11", Color) = (0.38, 0.52, 0.25, 1)
        _AtlasSize("Atlas Size (Tiles XY)", Vector) = (9, 10, 0, 0)
        [Toggle] _AtlasOriginTopLeft("Atlas Origin Top Left", Float) = 1
        _PaddingUV("Atlas Padding", Range(0.0, 0.01)) = 0.002
        [HideInInspector] _EnableRealisticShader("Enable Realistic Shader", Float) = 1

        [Header(Lighting)]
        _MinLight("Minimum Light", Range(0.0, 1.0)) = 0.08
        _VoxelSkyLightMultiplier("Sky Light Multiplier", Range(0.0, 1.0)) = 1.0
        _VoxelLightStrength("Voxel Light Strength", Range(0.0, 2.0)) = 1.0
        _AmbientStrength("Ambient Strength", Range(0.0, 2.0)) = 0.2
        _DirectionalStrength("Main Light Strength", Range(0.0, 2.0)) = 0.45
        _AdditionalLightsStrength("Additional Lights Strength", Range(0.0, 2.0)) = 0.35
        _WrapLighting("Wrap Lighting", Range(0.0, 1.0)) = 0.18
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.12
        _SpecularStrength("Specular Strength", Range(0.0, 2.0)) = 0.08
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        [ToggleUI] _CastShadows("Cast Shadows", Float) = 1.0
        _ShadowStrength("Shadow Strength", Range(0.0, 1.0)) = 1.0
        _RealtimeShadowFillStrength("Realtime Shadow Fill Strength", Range(0.0, 1.0)) = 0.85
        _VoxelShadowBlend("Voxel Shadow Blend", Range(0.0, 1.0)) = 0.85
        _VoxelShadowLightThreshold("Voxel Shadow Light Threshold", Range(0.0, 1.0)) = 0.97
        _AOStrength("Vertex AO Strength", Range(0.0, 2.0)) = 1.0

        [Header(Face Shading)]
        _Top("Top Shade", Range(0.0, 2.0)) = 1.0
        _Sides("Side Shade", Range(0.0, 2.0)) = 0.82
        _Bottom("Bottom Shade", Range(0.0, 2.0)) = 0.62

        [Header(Cutout)]
        [ToggleUI] _AlphaClip("Alpha Clip", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [HideInInspector] _UseVertexPulling("Use Vertex Pulling", Float) = 0
        [HideInInspector] _UseCompactOpaqueFaces("Use Compact Opaque Faces", Float) = 0
        [HideInInspector] _UseIndirectVertexPulling("Use Indirect Vertex Pulling", Float) = 0
        [HideInInspector] _UseBatchedOpaqueSections("Use Batched Opaque Sections", Float) = 0
        [HideInInspector] _UseSectionVisibilityMask("Use Section Visibility Mask", Float) = 0
        [HideInInspector] _SectionVisibility0("Section Visibility 0", Vector) = (1, 1, 1, 1)
        [HideInInspector] _SectionVisibility1("Section Visibility 1", Vector) = (1, 1, 1, 1)
        [HideInInspector] _SectionVisibility2("Section Visibility 2", Vector) = (1, 1, 1, 1)
        [HideInInspector] _SectionVisibility3("Section Visibility 3", Vector) = (1, 1, 1, 1)
        [HideInInspector] _SectionVisibility4("Section Visibility 4", Vector) = (1, 1, 1, 1)
        [HideInInspector] _SectionVisibility5("Section Visibility 5", Vector) = (1, 1, 1, 1)
        [HideInInspector] _Surface("__surface", Float) = 0.0
        [HideInInspector] _CompactChunkWorldOrigin("Compact Chunk World Origin", Vector) = (0, 0, 0, 0)
        [HideInInspector] _Blend("__blend", Float) = 0.0
        [HideInInspector] _Cull("__cull", Float) = 2.0
        [HideInInspector] _BlendOp("__blendop", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        [HideInInspector] _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0
        [HideInInspector] _QueueOffset("Queue Offset", Float) = 0.0

        [HideInInspector][NoScaleOffset] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _MainTex("Main Tex", 2D) = "white" {}
        [HideInInspector] _Color("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        LOD 100
        Blend [_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
        ZWrite [_ZWrite]
        Cull [_Cull]

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
        #include "UnityIndirect.cginc"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _GrassTint;
            float4 _VoxelEmissionTint;
            float _VoxelEmissionStrength;
            float _BiomeTintBlendEnabled;
            float4 _BiomeTintOriginXZ;
            float4 _BiomeTintInvSizeXZ;
            float4 _GrassTintCorner00;
            float4 _GrassTintCorner10;
            float4 _GrassTintCorner01;
            float4 _GrassTintCorner11;
            float4 _AtlasSize;
            float _AtlasOriginTopLeft;
            float _PaddingUV;
            float _EnableRealisticShader;
            float _MinLight;
            float _VoxelSkyLightMultiplier;
            float _VoxelLightStrength;
            float _AmbientStrength;
            float _DirectionalStrength;
            float _AdditionalLightsStrength;
            float _WrapLighting;
            float _Smoothness;
            float _SpecularStrength;
            float _ReceiveShadows;
            float _CastShadows;
            float _ShadowStrength;
            float _RealtimeShadowFillStrength;
            float _VoxelShadowBlend;
            float _VoxelShadowLightThreshold;
            float _AOStrength;
            float _Top;
            float _Sides;
            float _Bottom;
            float _AlphaClip;
            float _Cutoff;
            float _UseVertexPulling;
            float _UseCompactOpaqueFaces;
            float _UseIndirectVertexPulling;
            float _UseBatchedOpaqueSections;
            float _UseSectionVisibilityMask;
            float4 _SectionVisibility0;
            float4 _SectionVisibility1;
            float4 _SectionVisibility2;
            float4 _SectionVisibility3;
            float4 _SectionVisibility4;
            float4 _SectionVisibility5;
            float _PulledOpaqueFaceBaseIndex;
            float4 _CompactChunkWorldOrigin;
        CBUFFER_END

        // Global fog parameters driven at runtime by FogControl.
        float4 _FogColorSurface;
        float _FogStart;
        float _FogEnd;

        #include "Assets/Scripts/ProceduralGen/Shader/VoxelBreakInteraction.hlsl"

        TEXTURE2D(_Atlas);
        SAMPLER(sampler_Atlas);
        TEXTURE2D(_GrassSideOverlay);
        SAMPLER(sampler_GrassSideOverlay);
        TEXTURE2D(_EmissionMap);
        SAMPLER(sampler_EmissionMap);

        struct PulledOpaqueFace
        {
            float4 originWS;
            float4 edgeU;
            float4 edgeV;
            float4 normalWS;
            float4 uvBounds;
            float4 atlasAndFlags;
            float4 cornerLight01;
            float4 cornerAo01;
        };

        struct CompactOpaqueFace
        {
            uint packed0;
            uint packed1;
            uint packed2;
            uint packed3;
        };

        struct OpaqueGpuSection
        {
            float4 worldOriginAndVoxelOffset;
            float4 lightOffsetAndFaceRange;
        };

        struct OpaqueGpuBlockMapping
        {
            float4 topBottomAtlas;
            float4 sideAtlasAndFlags;
        };

        StructuredBuffer<PulledOpaqueFace> _PulledOpaqueFaces;
        StructuredBuffer<CompactOpaqueFace> _CompactOpaqueFaces;
        StructuredBuffer<OpaqueGpuSection> _OpaqueGpuSections;
        StructuredBuffer<OpaqueGpuBlockMapping> _OpaqueBlockMappings;

        float3 _LightDirection;
        float3 _LightPosition;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
            float4 uv2 : TEXCOORD2;
            float4 uv3 : TEXCOORD3;
            uint proceduralVertexID : SV_VertexID;
            uint proceduralInstanceID : SV_InstanceID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 localUV : TEXCOORD2;
            float2 atlasOrigin : TEXCOORD3;
            float2 atlasSize : TEXCOORD4;
            half4 extra : TEXCOORD5;
            half3 tintColor : TEXCOORD6;
            half subchunkIndex : TEXCOORD7;
            half blockLight : TEXCOORD8;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct PassThroughVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 localUV : TEXCOORD0;
            float2 atlasOrigin : TEXCOORD1;
            float2 atlasSize : TEXCOORD2;
            half3 normalWS : TEXCOORD3;
            half subchunkIndex : TEXCOORD4;
            half blockLight : TEXCOORD5;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct SurfaceSample
        {
            half4 color;
            half alpha;
        };

        float2 GetAtlasTileSize()
        {
            return 1.0 / max(_AtlasSize.xy, float2(1.0, 1.0));
        }

        float2 RepeatTileUV(float2 uv)
        {
            return uv - floor(uv);
        }

        float2 ResolveAtlasUV(float2 localUV, float2 atlasOrigin, float2 atlasSize)
        {
            float2 tileSize = max(atlasSize, float2(1e-5, 1e-5));
            float2 repeatedUV = RepeatTileUV(localUV);
            float2 normalizedPadding = saturate(_PaddingUV / max(tileSize, float2(1e-5, 1e-5)));
            repeatedUV = lerp(normalizedPadding, 1.0 - normalizedPadding, repeatedUV);

            float2 resolvedOrigin = atlasOrigin;
            if (_AtlasOriginTopLeft > 0.5)
                resolvedOrigin.y = 1.0 - atlasOrigin.y - atlasSize.y;

            return resolvedOrigin + repeatedUV * tileSize;
        }

        SurfaceSample SampleVoxelSurface(float2 localUV, float2 atlasOrigin, float2 atlasSize)
        {
            float2 atlasUV = ResolveAtlasUV(localUV, atlasOrigin, atlasSize);

            SurfaceSample surface;
            surface.color = SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, atlasUV) * _BaseColor;
            surface.alpha = surface.color.a;
            return surface;
        }

        half4 SampleGrassSideOverlay(float2 localUV)
        {
            float2 repeatedUV = RepeatTileUV(localUV);
            return SAMPLE_TEXTURE2D(_GrassSideOverlay, sampler_GrassSideOverlay, repeatedUV);
        }

        half3 SampleVoxelEmission(float2 localUV, float2 atlasOrigin, float2 atlasSize)
        {
            if (_VoxelEmissionStrength <= 0.0001)
                return 0.0h;

            float2 atlasUV = ResolveAtlasUV(localUV, atlasOrigin, atlasSize);
            half3 emissionMap = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, atlasUV).rgb;
            half3 emissionTint = half3(_VoxelEmissionTint.rgb);
            return emissionMap * emissionTint * (half)_VoxelEmissionStrength;
        }

        void ApplyVoxelAlphaClip(half alpha)
        {
            if (_AlphaClip > 0.5)
                clip(alpha - _Cutoff);
        }

        half ReadSectionVisibilityValue(float4 values, int componentIndex)
        {
            if (componentIndex <= 0)
                return (half)values.x;
            if (componentIndex == 1)
                return (half)values.y;
            if (componentIndex == 2)
                return (half)values.z;

            return (half)values.w;
        }

        half SampleSectionVisibility(float subchunkIndex)
        {
            if (_UseSectionVisibilityMask < 0.5)
                return 1.0h;

            int index = clamp((int)round(subchunkIndex), 0, 23);
            int componentIndex = index & 3;

            if (index < 4)
                return ReadSectionVisibilityValue(_SectionVisibility0, componentIndex);
            if (index < 8)
                return ReadSectionVisibilityValue(_SectionVisibility1, componentIndex);
            if (index < 12)
                return ReadSectionVisibilityValue(_SectionVisibility2, componentIndex);
            if (index < 16)
                return ReadSectionVisibilityValue(_SectionVisibility3, componentIndex);
            if (index < 20)
                return ReadSectionVisibilityValue(_SectionVisibility4, componentIndex);

            return ReadSectionVisibilityValue(_SectionVisibility5, componentIndex);
        }

        void ApplySectionVisibilityMask(float subchunkIndex)
        {
            if (_UseSectionVisibilityMask > 0.5)
                clip(SampleSectionVisibility(subchunkIndex) - 0.5h);
        }

        half ComputeFaceShade(half3 normalWS)
        {
            if (normalWS.y > 0.5h)
                return (half)_Top;

            if (normalWS.y < -0.5h)
                return (half)_Bottom;

            return (half)_Sides;
        }

        half ResolveVoxelLight01(half skyLight, half blockLight)
        {
            return max(saturate(blockLight), saturate(skyLight) * (half)_VoxelSkyLightMultiplier);
        }

        half ComputeVoxelDirectLightVisibility(half skyLight)
        {
            return saturate(skyLight * 64.0h);
        }

        half ComputeWrappedDiffuse(half3 normalWS, half3 lightDirectionWS)
        {
            half wrap = saturate((half)_WrapLighting);
            half ndl = dot(normalWS, lightDirectionWS);
            return saturate((ndl + wrap) / (1.0h + wrap));
        }

        half3 ComputeMainDynamicLighting(float3 positionWS, half3 normalWS, out half mainShadow)
        {
            half3 dynamicLighting = 0.0h;
            half receiveShadowStrength = (_ReceiveShadows > 0.5) ? (half)_ShadowStrength : 0.0h;

            Light mainLight = GetMainLight();
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
            #endif

            mainShadow = lerp(1.0h, mainLight.shadowAttenuation, receiveShadowStrength);
            half mainDiffuse = ComputeWrappedDiffuse(normalWS, mainLight.direction);
            dynamicLighting += mainLight.color * mainDiffuse * mainLight.distanceAttenuation * mainShadow * (half)_DirectionalStrength;

            return dynamicLighting;
        }

        half ComputeRealtimeShadowFill(half mainShadow)
        {
            return lerp(1.0h, mainShadow, saturate((half)_RealtimeShadowFillStrength));
        }

        half ComputeVoxelLightWithRealtimeShadow(half skyLight, half blockLight, half realtimeShadowFill)
        {
            half skyVoxelLight = saturate(skyLight) * (half)_VoxelSkyLightMultiplier * (half)_VoxelLightStrength * realtimeShadowFill;
            half blockVoxelLight = saturate(blockLight) * (half)_VoxelLightStrength;
            return max((half)_MinLight, max(skyVoxelLight, blockVoxelLight));
        }

        half ComputeSkyAmbientLight(half skyLight, half realtimeShadowFill)
        {
            half skyVoxelLight = saturate(skyLight) * (half)_VoxelSkyLightMultiplier * (half)_VoxelLightStrength;
            return saturate(skyVoxelLight * realtimeShadowFill);
        }

        half3 ComputeAdditionalDynamicLighting(float3 positionWS, float4 positionCS, half3 normalWS)
        {
            half3 dynamicLighting = 0.0h;
            half receiveShadowStrength = (_ReceiveShadows > 0.5) ? (half)_ShadowStrength : 0.0h;

            #if defined(_ADDITIONAL_LIGHTS)
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                uint lightCount = (uint)GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light addLight = GetAdditionalLight(lightIndex, positionWS);
                    half addDiffuse = ComputeWrappedDiffuse(normalWS, addLight.direction);
                    half addShadow = lerp(1.0h, addLight.shadowAttenuation, receiveShadowStrength);
                    dynamicLighting += addLight.color * addDiffuse * addLight.distanceAttenuation * addShadow * (half)_AdditionalLightsStrength;
                LIGHT_LOOP_END
            #endif

            return dynamicLighting;
        }

        half ComputeSpecularExponent()
        {
            return lerp(6.0h, 64.0h, saturate((half)_Smoothness));
        }

        half ComputeSpecularTerm(half3 normalWS, half3 viewDirWS, half3 lightDirectionWS)
        {
            half ndl = saturate(dot(normalWS, lightDirectionWS));
            if (ndl <= 0.0001h)
                return 0.0h;

            half3 halfDir = SafeNormalize(lightDirectionWS + viewDirWS);
            half ndh = saturate(dot(normalWS, halfDir));
            half specExponent = ComputeSpecularExponent();
            half normalization = (specExponent + 8.0h) * 0.125h;
            half spec = pow(ndh, specExponent) * normalization;
            spec *= lerp(0.55h, 1.0h, saturate((half)_Smoothness));
            return spec * ndl * (half)_SpecularStrength;
        }

        half3 ComputeSpecularLighting(float3 positionWS, float4 positionCS, half3 normalWS, half3 viewDirWS)
        {
            half3 specularLighting = 0.0h;
            half receiveShadowStrength = (_ReceiveShadows > 0.5) ? (half)_ShadowStrength : 0.0h;

            Light mainLight = GetMainLight();
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
            #endif

            half mainShadow = lerp(1.0h, mainLight.shadowAttenuation, receiveShadowStrength);
            half mainSpecular = ComputeSpecularTerm(normalWS, viewDirWS, mainLight.direction);
            specularLighting += mainLight.color * mainSpecular * mainLight.distanceAttenuation * mainShadow * (half)_DirectionalStrength;

            #if defined(_ADDITIONAL_LIGHTS)
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                uint lightCount = (uint)GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light addLight = GetAdditionalLight(lightIndex, positionWS);
                    half addShadow = lerp(1.0h, addLight.shadowAttenuation, receiveShadowStrength);
                    half addSpecular = ComputeSpecularTerm(normalWS, viewDirWS, addLight.direction);
                    specularLighting += addLight.color * addSpecular * addLight.distanceAttenuation * addShadow * (half)_AdditionalLightsStrength;
                LIGHT_LOOP_END
            #endif

            return specularLighting;
        }

        half3 ApplyVoxelFog(half3 color, float3 positionWS)
        {
            float fogRange = max(0.0001, _FogEnd - _FogStart);
            float cameraDistance = distance(GetCameraPositionWS(), positionWS);
            float fogFactor = saturate((cameraDistance - _FogStart) / fogRange);
            return lerp(color, _FogColorSurface.rgb, fogFactor);
        }

        uint ResolveProceduralCornerIndex(uint vertexId, uint flags)
        {
            bool flipped = (flags & 1u) != 0u;
            bool reversed = (flags & 2u) != 0u;
            uint vid = vertexId % 6u;

            if (!reversed)
            {
                if (!flipped)
                {
                    switch (vid)
                    {
                        case 0u: return 0u;
                        case 1u: return 1u;
                        case 2u: return 2u;
                        case 3u: return 0u;
                        case 4u: return 2u;
                        default: return 3u;
                    }
                }

                switch (vid)
                {
                    case 0u: return 0u;
                    case 1u: return 1u;
                    case 2u: return 3u;
                    case 3u: return 1u;
                    case 4u: return 2u;
                    default: return 3u;
                }
            }

            if (!flipped)
            {
                switch (vid)
                {
                    case 0u: return 0u;
                    case 1u: return 3u;
                    case 2u: return 2u;
                    case 3u: return 0u;
                    case 4u: return 2u;
                    default: return 1u;
                }
            }

            switch (vid)
            {
                case 0u: return 0u;
                case 1u: return 3u;
                case 2u: return 1u;
                case 3u: return 1u;
                case 4u: return 3u;
                default: return 2u;
            }

            return 0u;
        }

        float2 ResolveProceduralCornerUV01(uint corner)
        {
            switch (corner)
            {
                case 0u: return float2(0.0, 0.0);
                case 1u: return float2(1.0, 0.0);
                case 2u: return float2(1.0, 1.0);
                default: return float2(0.0, 1.0);
            }
        }

        float ResolveProceduralCornerValue(float4 values, uint corner)
        {
            switch (corner)
            {
                case 0u: return values.x;
                case 1u: return values.y;
                case 2u: return values.z;
                default: return values.w;
            }
        }

        uint ExtractPackedBits(uint value, uint shift, uint mask)
        {
            return (value >> shift) & mask;
        }

        float ResolvePackedByte01(uint packedValue, uint corner)
        {
            uint shift = corner * 8u;
            return ((packedValue >> shift) & 0xFFu) / 255.0;
        }

        void ResolveCompactOpaqueFaceData(
            uint faceIndex,
            uint vertexIndex,
            float3 chunkOriginWS,
            out float3 positionWS,
            out half3 normalWS,
            out float2 localUV,
            out float2 atlasOrigin,
            out half4 extra,
            out half subchunkIndex)
        {
            CompactOpaqueFace face = _CompactOpaqueFaces[faceIndex];
            uint localX = ExtractPackedBits(face.packed0, 0u, 0xFu);
            uint localY = ExtractPackedBits(face.packed0, 4u, 0x1FFu);
            uint localZ = ExtractPackedBits(face.packed0, 13u, 0xFu);
            uint faceDir = ExtractPackedBits(face.packed0, 17u, 0x7u);
            uint tileX = ExtractPackedBits(face.packed0, 20u, 0x1Fu);
            uint tileY = ExtractPackedBits(face.packed0, 25u, 0x1Fu);
            uint spanU = ExtractPackedBits(face.packed1, 0u, 0x1FFu);
            uint spanV = ExtractPackedBits(face.packed1, 9u, 0x1FFu);
            float tint0 = ExtractPackedBits(face.packed0, 30u, 0x1u) != 0u ? 1.0 : 0.0;
            bool flipped = ExtractPackedBits(face.packed0, 31u, 0x1u) != 0u;

            bool reversed = faceDir == 1u || faceDir == 3u || faceDir == 5u;
            uint compactFlags = (flipped ? 1u : 0u) | (reversed ? 2u : 0u);
            uint corner = ResolveProceduralCornerIndex(vertexIndex, compactFlags);
            float2 cornerUV01 = ResolveProceduralCornerUV01(corner);

            float3 originWS0;
            float3 edgeU0;
            float3 edgeV0;
            float3 normalWS0;
            float2 atlas0 = float2(tileX, tileY) * GetAtlasTileSize();

            switch (faceDir)
            {
                case 0u:
                    originWS0 = chunkOriginWS + float3(localX + 1u, localY, localZ);
                    edgeU0 = float3(0.0, 1.0, 0.0);
                    edgeV0 = float3(0.0, 0.0, 1.0);
                    normalWS0 = float3(1.0, 0.0, 0.0);
                    break;
                case 1u:
                    originWS0 = chunkOriginWS + float3(localX, localY, localZ);
                    edgeU0 = float3(0.0, 1.0, 0.0);
                    edgeV0 = float3(0.0, 0.0, 1.0);
                    normalWS0 = float3(-1.0, 0.0, 0.0);
                    break;
                case 2u:
                    originWS0 = chunkOriginWS + float3(localX, localY + 1u, localZ);
                    edgeU0 = float3(0.0, 0.0, 1.0);
                    edgeV0 = float3(1.0, 0.0, 0.0);
                    normalWS0 = float3(0.0, 1.0, 0.0);
                    break;
                case 3u:
                    originWS0 = chunkOriginWS + float3(localX, localY, localZ);
                    edgeU0 = float3(0.0, 0.0, 1.0);
                    edgeV0 = float3(1.0, 0.0, 0.0);
                    normalWS0 = float3(0.0, -1.0, 0.0);
                    break;
                case 4u:
                    originWS0 = chunkOriginWS + float3(localX, localY, localZ + 1u);
                    edgeU0 = float3(1.0, 0.0, 0.0);
                    edgeV0 = float3(0.0, 1.0, 0.0);
                    normalWS0 = float3(0.0, 0.0, 1.0);
                    break;
                default:
                    originWS0 = chunkOriginWS + float3(localX, localY, localZ);
                    edgeU0 = float3(1.0, 0.0, 0.0);
                    edgeV0 = float3(0.0, 1.0, 0.0);
                    normalWS0 = float3(0.0, 0.0, -1.0);
                    break;
            }

            float safeSpanU = max(1.0, (float)spanU);
            float safeSpanV = max(1.0, (float)spanV);

            positionWS = originWS0 + edgeU0 * (cornerUV01.x * safeSpanU) + edgeV0 * (cornerUV01.y * safeSpanV);
            normalWS = normalize(normalWS0);
            localUV = (faceDir == 0u || faceDir == 1u || faceDir == 2u || faceDir == 3u)
                ? float2(cornerUV01.y * safeSpanV, cornerUV01.x * safeSpanU)
                : float2(cornerUV01.x * safeSpanU, cornerUV01.y * safeSpanV);
            atlasOrigin = atlas0;
            extra = half4(
                (half)ResolvePackedByte01(face.packed2, corner),
                (half)tint0,
                (half)ResolvePackedByte01(face.packed3, corner),
                0.0h);
            subchunkIndex = (half)(localY / 16u);
        }

        half3 ResolveGrassTintByWorldPosition(float3 positionWS)
        {
            float blendStrength = saturate(_BiomeTintBlendEnabled);
            if (blendStrength <= 0.0001)
                return _GrassTint.rgb;

            float2 uv = saturate((positionWS.xz - _BiomeTintOriginXZ.xy) * _BiomeTintInvSizeXZ.xy);
            half3 south = lerp(_GrassTintCorner00.rgb, _GrassTintCorner10.rgb, (half)uv.x);
            half3 north = lerp(_GrassTintCorner01.rgb, _GrassTintCorner11.rgb, (half)uv.x);
            half3 blended = lerp(south, north, (half)uv.y);
            return lerp(_GrassTint.rgb, blended, (half)blendStrength);
        }

        void ResolveVoxelVertexData(Attributes input, out float3 positionWS, out half3 normalWS, out float2 localUV, out float2 atlasOrigin, out float2 atlasSize, out half4 extra, out half3 tintColor, out half subchunkIndex, out half blockLight)
        {
            positionWS = 0.0.xxx;
            normalWS = half3(0.0h, 1.0h, 0.0h);
            localUV = 0.0.xx;
            atlasOrigin = 0.0.xx;
            atlasSize = GetAtlasTileSize();
            extra = half4(1.0h, 0.0h, 1.0h, 0.0h);
            tintColor = half3(1.0h, 1.0h, 1.0h);
            subchunkIndex = 0.0h;
            blockLight = 0.0h;

            if (_UseCompactOpaqueFaces > 0.5)
            {
                uint faceBaseIndex = (uint)max(0.0, _PulledOpaqueFaceBaseIndex);
                uint faceIndex = faceBaseIndex + input.proceduralInstanceID;
                uint vertexIndex = input.proceduralVertexID;
                float3 chunkOriginWS = _CompactChunkWorldOrigin.xyz;
                if (_UseIndirectVertexPulling > 0.5)
                {
                    InitIndirectDrawArgs(0);
                    faceIndex = GetIndirectInstanceID_Base(input.proceduralInstanceID);
                    vertexIndex = GetIndirectVertexID(input.proceduralVertexID);

                    if (_UseBatchedOpaqueSections > 0.5)
                    {
                        OpaqueGpuSection section = _OpaqueGpuSections[GetCommandID(0)];
                        faceBaseIndex += (uint)max(0.0, section.lightOffsetAndFaceRange.x);
                        chunkOriginWS = section.worldOriginAndVoxelOffset.xyz;
                    }
                }

                faceIndex += faceBaseIndex;
                ResolveCompactOpaqueFaceData(faceIndex, vertexIndex, chunkOriginWS, positionWS, normalWS, localUV, atlasOrigin, extra, subchunkIndex);
                atlasSize = GetAtlasTileSize();
                half3 grassTint = ResolveGrassTintByWorldPosition(positionWS);
                tintColor = lerp(half3(1.0h, 1.0h, 1.0h), grassTint, saturate(extra.y));
                ApplyVoxelBreakInteraction(positionWS);
                return;
            }

            if (_UseVertexPulling > 0.5)
            {
                uint faceIndex = (uint)max(0.0, _PulledOpaqueFaceBaseIndex) + input.proceduralInstanceID;
                uint vertexIndex = input.proceduralVertexID;
                if (_UseIndirectVertexPulling > 0.5)
                {
                    InitIndirectDrawArgs(0);
                    faceIndex = (uint)max(0.0, _PulledOpaqueFaceBaseIndex) + GetIndirectInstanceID_Base(input.proceduralInstanceID);
                    vertexIndex = GetIndirectVertexID(input.proceduralVertexID);
                }

                PulledOpaqueFace face = _PulledOpaqueFaces[faceIndex];
                uint flags = (uint)round(face.originWS.w);
                uint corner = ResolveProceduralCornerIndex(vertexIndex, flags);
                float2 cornerUV01 = ResolveProceduralCornerUV01(corner);

                positionWS = face.originWS.xyz + face.edgeU.xyz * cornerUV01.x + face.edgeV.xyz * cornerUV01.y;
                normalWS = normalize(face.normalWS.xyz);
                localUV = face.uvBounds.xy + face.uvBounds.zw * cornerUV01.x + face.atlasAndFlags.xy * cornerUV01.y;
                atlasOrigin = face.atlasAndFlags.zw;
                atlasSize = GetAtlasTileSize();
                half tintMask = saturate((half)face.edgeU.w);
                extra = half4(
                    (half)ResolveProceduralCornerValue(face.cornerLight01, corner),
                    tintMask,
                    (half)ResolveProceduralCornerValue(face.cornerAo01, corner),
                    0.0h);
                half3 grassTint = ResolveGrassTintByWorldPosition(positionWS);
                tintColor = lerp(half3(1.0h, 1.0h, 1.0h), grassTint, tintMask);
                ApplyVoxelBreakInteraction(positionWS);
                return;
            }

            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

            positionWS = positionInputs.positionWS;
            normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
            localUV = input.uv0;
            atlasOrigin = input.uv1;
            atlasSize = input.uv3.xy;
            blockLight = saturate((half)input.uv3.z);
            float packedExtraW = max(0.0, input.uv2.w);
            float decodedSubchunk = floor(packedExtraW + 0.0001);
            float decodedOverlayMask = saturate((packedExtraW - decodedSubchunk) * 4.0);
            extra = half4(saturate(input.uv2.xyz), (half)decodedOverlayMask);
            half3 grassTint = ResolveGrassTintByWorldPosition(positionWS);
            tintColor = lerp(half3(1.0h, 1.0h, 1.0h), grassTint, saturate(input.uv2.y));
            subchunkIndex = (half)decodedSubchunk;
        }

        Varyings ForwardVertex(Attributes input)
        {
            Varyings output = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            ResolveVoxelVertexData(input, output.positionWS, output.normalWS, output.localUV, output.atlasOrigin, output.atlasSize, output.extra, output.tintColor, output.subchunkIndex, output.blockLight);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            return output;
        }

        half4 ForwardFragment(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            SurfaceSample surface = SampleVoxelSurface(input.localUV, input.atlasOrigin, input.atlasSize);
            ApplyVoxelAlphaClip(surface.alpha);
            ApplySectionVisibilityMask(input.subchunkIndex);

            half tintMask = saturate(input.extra.y);
            half grassSideOverlayMask = saturate(input.extra.w);
            half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
            half3 baseAlbedo = surface.color.rgb;
            half3 albedo = baseAlbedo * input.tintColor;

            // Apply side overlay tint only on faces explicitly flagged as grass-block sides.
            if (abs(normalWS.y) < 0.5h && tintMask > 0.0001h && grassSideOverlayMask > 0.0001h)
            {
                half4 sideOverlay = SampleGrassSideOverlay(input.localUV);
                half overlayAlpha = saturate(sideOverlay.a) * tintMask * grassSideOverlayMask;
                half3 tintedOverlay = sideOverlay.rgb * input.tintColor;
                albedo = baseAlbedo * (1.0h - overlayAlpha) + tintedOverlay * overlayAlpha;
            }

            if (_EnableRealisticShader <= 0.5)
            {
                half voxelLight01 = ResolveVoxelLight01(input.extra.x, input.blockLight);
                half simpleVoxelLight = saturate(max((half)_MinLight, voxelLight01 * (half)_VoxelLightStrength));
                half simpleAO = lerp(1.0h, saturate(input.extra.z), (half)_AOStrength);
                half3 emission = SampleVoxelEmission(input.localUV, input.atlasOrigin, input.atlasSize);
                half3 simpleColor = ApplyVoxelFog(albedo * simpleVoxelLight * simpleAO + emission, input.positionWS);
                return half4(simpleColor, surface.alpha);
            }

            half3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);
            half ao = lerp(1.0h, saturate(input.extra.z), (half)_AOStrength);
            half faceShade = ComputeFaceShade(normalWS);
            half skyLight01 = saturate(input.extra.x);
            half hemisphericAmbient = lerp(0.55h, 1.0h, saturate(normalWS.y * 0.5h + 0.5h)) * (half)_AmbientStrength;
            half mainShadow;
            half3 mainDynamicLighting = ComputeMainDynamicLighting(input.positionWS, normalWS, mainShadow);
            half3 additionalDynamicLighting = ComputeAdditionalDynamicLighting(input.positionWS, input.positionCS, normalWS);
            half realtimeShadowFill = ComputeRealtimeShadowFill(mainShadow);
            half directLightVisibility = ComputeVoxelDirectLightVisibility(skyLight01);
            half shadedVoxelLight = ComputeVoxelLightWithRealtimeShadow(skyLight01, input.blockLight, realtimeShadowFill);
            half environmentLight = ComputeSkyAmbientLight(skyLight01, realtimeShadowFill);

            half3 lighting = shadedVoxelLight.xxx * faceShade;
            lighting += hemisphericAmbient.xxx * faceShade * environmentLight;
            lighting += mainDynamicLighting * directLightVisibility;
            lighting += additionalDynamicLighting;
            lighting *= ao;
            half3 specularLighting = ComputeSpecularLighting(input.positionWS, input.positionCS, normalWS, viewDirWS) * directLightVisibility * lerp(1.0h, ao, 0.35h);

            half3 emission = SampleVoxelEmission(input.localUV, input.atlasOrigin, input.atlasSize);
            half3 finalColor = ApplyVoxelFog(albedo * lighting + specularLighting + emission, input.positionWS);
            return half4(finalColor, surface.alpha);
        }

        PassThroughVaryings PassThroughVertex(Attributes input)
        {
            PassThroughVaryings output = (PassThroughVaryings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            float3 positionWS;
            half3 normalWS;
            half4 extra;
            half3 tintColor;
            ResolveVoxelVertexData(input, positionWS, normalWS, output.localUV, output.atlasOrigin, output.atlasSize, extra, tintColor, output.subchunkIndex, output.blockLight);

            output.positionCS = TransformWorldToHClip(positionWS);
            output.normalWS = normalWS;
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
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

            PassThroughVaryings ShadowPassVertex(Attributes input)
            {
                PassThroughVaryings output = (PassThroughVaryings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS;
                half3 normalWS;
                half4 extra;
                half3 tintColor;
                ResolveVoxelVertexData(input, positionWS, normalWS, output.localUV, output.atlasOrigin, output.atlasSize, extra, tintColor, output.subchunkIndex, output.blockLight);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);
                output.normalWS = normalWS;
                return output;
            }

            half4 ShadowPassFragment(PassThroughVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_CastShadows < 0.5)
                    clip(-1.0h);

                SurfaceSample surface = SampleVoxelSurface(input.localUV, input.atlasOrigin, input.atlasSize);
                ApplyVoxelAlphaClip(surface.alpha);
                ApplySectionVisibilityMask(input.subchunkIndex);
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

            PassThroughVaryings DepthOnlyVertex(Attributes input)
            {
                return PassThroughVertex(input);
            }

            half DepthOnlyFragment(PassThroughVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                SurfaceSample surface = SampleVoxelSurface(input.localUV, input.atlasOrigin, input.atlasSize);
                ApplyVoxelAlphaClip(surface.alpha);
                ApplySectionVisibilityMask(input.subchunkIndex);
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

            PassThroughVaryings DepthNormalsVertex(Attributes input)
            {
                return PassThroughVertex(input);
            }

            half4 DepthNormalsFragment(PassThroughVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                SurfaceSample surface = SampleVoxelSurface(input.localUV, input.atlasOrigin, input.atlasSize);
                ApplyVoxelAlphaClip(surface.alpha);
                ApplySectionVisibilityMask(input.subchunkIndex);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                return half4(normalWS, 0.0h);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
