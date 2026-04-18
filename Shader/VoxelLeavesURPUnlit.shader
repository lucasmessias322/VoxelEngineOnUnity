Shader "Voxel/URP/Voxel Leaves Unlit Lit"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _Atlas("Atlas", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _GrassTint("Grass Tint", Color) = (0.38, 0.52, 0.25, 1)
        _FolliageTint("Folliage Tint", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _BiomeTintBlendEnabled("Biome Tint Blend Enabled", Float) = 0
        [HideInInspector] _BiomeTintOriginXZ("Biome Tint Origin XZ", Vector) = (0, 0, 0, 0)
        [HideInInspector] _BiomeTintInvSizeXZ("Biome Tint Inv Size XZ", Vector) = (0.0625, 0.0625, 0, 0)
        [HideInInspector] _GrassTintCorner00("Grass Tint Corner 00", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner10("Grass Tint Corner 10", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner01("Grass Tint Corner 01", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _GrassTintCorner11("Grass Tint Corner 11", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _FolliageTintCorner00("Folliage Tint Corner 00", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _FolliageTintCorner10("Folliage Tint Corner 10", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _FolliageTintCorner01("Folliage Tint Corner 01", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _FolliageTintCorner11("Folliage Tint Corner 11", Color) = (0.38, 0.52, 0.25, 1)
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
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        [ToggleUI] _CastShadows("Cast Shadows", Float) = 1.0
        _ShadowStrength("Shadow Strength", Range(0.0, 1.0)) = 1.0
        _VoxelShadowBlend("Voxel Shadow Blend", Range(0.0, 1.0)) = 0.75
        _VoxelShadowLightThreshold("Voxel Shadow Light Threshold", Range(0.0, 1.0)) = 0.97
        _AOStrength("Vertex AO Strength", Range(0.0, 2.0)) = 1.0

        [Header(Face Shading)]
        _Top("Top Shade", Range(0.0, 2.0)) = 1.0
        _Sides("Side Shade", Range(0.0, 2.0)) = 0.82
        _Bottom("Bottom Shade", Range(0.0, 2.0)) = 0.62

        [Header(Cutout)]
        [ToggleUI] _AlphaClip("Alpha Clip", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [Header(Wind)]
        [ToggleUI] _EnableWind("Enable Wind", Float) = 1.0
        _WindScale("Wind Scale", Range(0.005, 0.5)) = 0.05
        _WindSpeed("Wind Speed", Range(0.0, 3.0)) = 0.1
        _WindStrength("Wind Strength", Range(0.0, 0.4)) = 0.06
        _WindDetailStrength("Wind Detail Strength", Range(0.0, 0.2)) = 0.025
        _WindVerticalStrength("Wind Vertical Strength", Range(0.0, 0.12)) = 0.012
        _WindGustFrequency("Wind Gust Frequency", Range(0.05, 2.0)) = 0.35
        _WindDirection("Wind Direction XZ", Vector) = (1, 0, 0, 0)

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
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
        #include "UnityIndirect.cginc"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _GrassTint;
            float4 _FolliageTint;
            float _BiomeTintBlendEnabled;
            float4 _BiomeTintOriginXZ;
            float4 _BiomeTintInvSizeXZ;
            float4 _GrassTintCorner00;
            float4 _GrassTintCorner10;
            float4 _GrassTintCorner01;
            float4 _GrassTintCorner11;
            float4 _FolliageTintCorner00;
            float4 _FolliageTintCorner10;
            float4 _FolliageTintCorner01;
            float4 _FolliageTintCorner11;
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
            float _ReceiveShadows;
            float _CastShadows;
            float _ShadowStrength;
            float _VoxelShadowBlend;
            float _VoxelShadowLightThreshold;
            float _AOStrength;
            float _Top;
            float _Sides;
            float _Bottom;
            float _AlphaClip;
            float _Cutoff;
            float _EnableWind;
            float _WindScale;
            float _WindSpeed;
            float _WindStrength;
            float _WindDetailStrength;
            float _WindVerticalStrength;
            float _WindGustFrequency;
            float4 _WindDirection;
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
            half3 extra : TEXCOORD5;
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
            float3 positionWS : TEXCOORD5;
            half blockLight : TEXCOORD6;
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
                resolvedOrigin.y = 1.0 - atlasOrigin.y - tileSize.y;

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
            half yAbs = saturate(abs(normalWS.y));
            return lerp((half)_Sides, (half)_Top, yAbs);
        }

        half ResolveVoxelLight01(half skyLight, half blockLight)
        {
            return max(saturate(blockLight), saturate(skyLight) * (half)_VoxelSkyLightMultiplier);
        }

        half ComputeWrappedDiffuse(half3 normalWS, half3 lightDirectionWS)
        {
            half wrap = saturate((half)_WrapLighting);
            // Folhagem e billboard devem receber luz nos dois lados para evitar manchas escuras.
            half ndl = abs(dot(normalWS, lightDirectionWS));
            return saturate((ndl + wrap) / (1.0h + wrap));
        }

        half3 ComputeMainDynamicLighting(float3 positionWS, half3 normalWS)
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

            return dynamicLighting;
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

        half ComputeMainLightShadowFactor(float3 positionWS)
        {
            if (_ReceiveShadows <= 0.5)
                return 1.0h;

            Light mainLight = GetMainLight();
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
            #endif

            return lerp(1.0h, mainLight.shadowAttenuation, (half)_ShadowStrength);
        }

        half ApplyRealtimeShadowToVoxelLight(half voxelLight, half packedLight01, float3 positionWS)
        {
            // Packed voxel light currently mixes skylight and block light, so low thresholds
            // will incorrectly shadow emissive lighting. Clamp to a safe range.
            half voxelShadowThreshold = max((half)_VoxelShadowLightThreshold, 0.97h);
            half shadowBlend = smoothstep(voxelShadowThreshold, 1.0h, saturate(packedLight01));
            shadowBlend *= (half)_VoxelShadowBlend;
            if (shadowBlend <= 0.0001h)
                return voxelLight;

            half mainShadow = ComputeMainLightShadowFactor(positionWS);
            return lerp(voxelLight, voxelLight * mainShadow, shadowBlend);
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
            out half3 extra,
            out half subchunkIndex,
            out half windBendMask)
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
            extra = half3(
                (half)ResolvePackedByte01(face.packed2, corner),
                (half)tint0,
                (half)ResolvePackedByte01(face.packed3, corner));
            subchunkIndex = (half)(localY / 16u);
            float verticalWeightU = abs(edgeU0.y) * safeSpanU;
            float verticalWeightV = abs(edgeV0.y) * safeSpanV;
            float verticalWeightSum = verticalWeightU + verticalWeightV;
            float verticalMask = verticalWeightSum > 1e-5
                ? (cornerUV01.x * verticalWeightU + cornerUV01.y * verticalWeightV) / verticalWeightSum
                : 1.0;
            verticalMask = saturate(verticalMask);
            verticalMask *= verticalMask;
            float faceMask = saturate(1.0 - abs(normalWS0.y));
            windBendMask = (half)lerp(1.0, verticalMask, faceMask);
        }

        half3 ResolveFolliageTintByWorldPosition(float3 positionWS)
        {
            float blendStrength = saturate(_BiomeTintBlendEnabled);
            half3 baseTint = dot(_FolliageTint.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h
                ? _FolliageTint.rgb
                : _GrassTint.rgb;
            if (blendStrength <= 0.0001)
                return baseTint;

            float2 uv = saturate((positionWS.xz - _BiomeTintOriginXZ.xy) * _BiomeTintInvSizeXZ.xy);
            half3 corner00 = dot(_FolliageTintCorner00.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? _FolliageTintCorner00.rgb : _GrassTintCorner00.rgb;
            half3 corner10 = dot(_FolliageTintCorner10.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? _FolliageTintCorner10.rgb : _GrassTintCorner10.rgb;
            half3 corner01 = dot(_FolliageTintCorner01.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? _FolliageTintCorner01.rgb : _GrassTintCorner01.rgb;
            half3 corner11 = dot(_FolliageTintCorner11.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? _FolliageTintCorner11.rgb : _GrassTintCorner11.rgb;
            half3 south = lerp(corner00, corner10, (half)uv.x);
            half3 north = lerp(corner01, corner11, (half)uv.x);
            half3 blended = lerp(south, north, (half)uv.y);
            return lerp(baseTint, blended, (half)blendStrength);
        }

        float Hash12(float2 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.yx + 33.33);
            return frac((p.x + p.y) * p.x);
        }

        void ApplyLeafWind(inout float3 positionWS, inout half3 normalWS, half windMask, half bendMask)
        {
            if (_EnableWind < 0.5 || windMask <= 0.0001h || bendMask <= 0.0001h)
                return;

            float2 windDir = _WindDirection.xy;
            float windDirLengthSq = dot(windDir, windDir);
            if (windDirLengthSq < 1e-5)
                windDir = float2(1.0, 0.0);
            else
                windDir *= rsqrt(windDirLengthSq);

            float windScale = max(_WindScale, 1e-4);
            float timeValue = _Time.y * max(_WindSpeed, 0.0);
            float2 samplePos = positionWS.xz * windScale;
            float randomPhase = Hash12(floor(samplePos * 3.0) + float2(17.0, 23.0)) * 6.2831853;

            float basePhase = dot(samplePos, windDir * 2.7) + randomPhase;
            float gustPhase = dot(samplePos, float2(-windDir.y, windDir.x) * 1.4) + timeValue * _WindGustFrequency;

            float baseSway = sin(basePhase + timeValue);
            float detailSway = sin(basePhase * 1.91 + timeValue * 1.73 + randomPhase * 0.5);
            float gust = saturate(sin(gustPhase) * 0.5 + 0.5);

            float swayAmplitude = _WindStrength * lerp(0.65, 1.25, gust);
            float detailAmplitude = _WindDetailStrength * (0.45 + 0.55 * gust);
            float verticalAmplitude = _WindVerticalStrength * (0.35 + 0.65 * gust);

            float2 detailDir = float2(-windDir.y, windDir.x);
            float2 offsetXZ = windDir * (baseSway * swayAmplitude) + detailDir * (detailSway * detailAmplitude);

            float finalMask = saturate((float)windMask * (float)bendMask);
            positionWS.xz += offsetXZ * finalMask;
            positionWS.y += (baseSway * detailSway) * verticalAmplitude * finalMask;

            normalWS = normalize(normalWS + half3(offsetXZ.x, 0.0, offsetXZ.y) * (half)(0.22 * finalMask));
        }

        void ResolveVoxelVertexData(Attributes input, out float3 positionWS, out half3 normalWS, out float2 localUV, out float2 atlasOrigin, out float2 atlasSize, out half3 extra, out half3 tintColor, out half subchunkIndex, out half blockLight)
        {
            positionWS = 0.0.xxx;
            normalWS = half3(0.0h, 1.0h, 0.0h);
            localUV = 0.0.xx;
            atlasOrigin = 0.0.xx;
            atlasSize = GetAtlasTileSize();
            extra = half3(1.0h, 0.0h, 1.0h);
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
                half windBendMask = 1.0h;
                ResolveCompactOpaqueFaceData(faceIndex, vertexIndex, chunkOriginWS, positionWS, normalWS, localUV, atlasOrigin, extra, subchunkIndex, windBendMask);
                atlasSize = GetAtlasTileSize();
                half3 foliageTint = ResolveFolliageTintByWorldPosition(positionWS);
                tintColor = lerp(half3(1.0h, 1.0h, 1.0h), foliageTint, saturate(extra.y));
                ApplyLeafWind(positionWS, normalWS, saturate(extra.y), windBendMask);
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
                extra = half3(
                    (half)ResolveProceduralCornerValue(face.cornerLight01, corner),
                    1.0h,
                    (half)ResolveProceduralCornerValue(face.cornerAo01, corner));
                half tintMask = saturate((half)face.edgeU.w);
                half3 foliageTint = ResolveFolliageTintByWorldPosition(positionWS);
                tintColor = lerp(half3(1.0h, 1.0h, 1.0h), foliageTint, tintMask);
                float edgeVerticalWeightU = abs(face.edgeU.y);
                float edgeVerticalWeightV = abs(face.edgeV.y);
                float edgeVerticalWeightSum = edgeVerticalWeightU + edgeVerticalWeightV;
                float verticalMask = edgeVerticalWeightSum > 1e-5
                    ? (cornerUV01.x * edgeVerticalWeightU + cornerUV01.y * edgeVerticalWeightV) / edgeVerticalWeightSum
                    : 1.0;
                verticalMask = saturate(verticalMask);
                verticalMask *= verticalMask;
                float faceMask = saturate(1.0 - abs(normalWS.y));
                half windBendMask = (half)lerp(1.0, verticalMask, faceMask);
                ApplyLeafWind(positionWS, normalWS, tintMask, windBendMask);
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
            extra = saturate(input.uv2.xyz);
            half3 foliageTint = ResolveFolliageTintByWorldPosition(positionWS);
            half tintMask = saturate(input.uv2.y);
            tintColor = lerp(half3(1.0h, 1.0h, 1.0h), foliageTint, tintMask);
            float verticalMask = saturate(localUV.y);
            verticalMask *= verticalMask;
            float faceMask = saturate(1.0 - abs(normalWS.y));
            half windBendMask = (half)lerp(1.0, verticalMask, faceMask);
            ApplyLeafWind(positionWS, normalWS, tintMask, windBendMask);
            subchunkIndex = (half)input.uv2.w;
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

            half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
            half3 albedo = surface.color.rgb * input.tintColor;

            if (_EnableRealisticShader <= 0.5)
            {
                half voxelLight01 = ResolveVoxelLight01(input.extra.x, input.blockLight);
                half simpleVoxelLight = saturate(max((half)_MinLight, voxelLight01 * (half)_VoxelLightStrength));
                half simpleAO = lerp(1.0h, saturate(input.extra.z), (half)_AOStrength);
                return half4(albedo * simpleVoxelLight * simpleAO, surface.alpha);
            }

            half ao = lerp(1.0h, saturate(input.extra.z), (half)_AOStrength);
            half faceShade = ComputeFaceShade(normalWS);
            half voxelLight01 = ResolveVoxelLight01(input.extra.x, input.blockLight);
            half voxelLight = max((half)_MinLight, voxelLight01 * (half)_VoxelLightStrength);
            half shadedVoxelLight = ApplyRealtimeShadowToVoxelLight(voxelLight, voxelLight01, input.positionWS);
            half environmentLight = saturate(shadedVoxelLight);
            half hemisphericAmbient = lerp(0.55h, 1.0h, saturate(normalWS.y * 0.5h + 0.5h)) * (half)_AmbientStrength;
            half3 mainDynamicLighting = ComputeMainDynamicLighting(input.positionWS, normalWS);
            half3 additionalDynamicLighting = ComputeAdditionalDynamicLighting(input.positionWS, input.positionCS, normalWS);

            half3 lighting = shadedVoxelLight.xxx * faceShade;
            lighting += hemisphericAmbient.xxx * faceShade * environmentLight;
            lighting += mainDynamicLighting * environmentLight;
            lighting += additionalDynamicLighting;
            lighting *= ao;

            half3 finalColor = ApplyVoxelFog(albedo * lighting, input.positionWS);
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
            half3 extra;
            half3 tintColor;
            ResolveVoxelVertexData(input, positionWS, normalWS, output.localUV, output.atlasOrigin, output.atlasSize, extra, tintColor, output.subchunkIndex, output.blockLight);

            output.positionCS = TransformWorldToHClip(positionWS);
            output.normalWS = normalWS;
            output.positionWS = positionWS;
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
            Cull Off

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
                half3 extra;
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
                output.positionWS = positionWS;
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
            Cull Off

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
            Cull Off

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
