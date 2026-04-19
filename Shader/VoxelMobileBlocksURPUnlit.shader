Shader "Voxel/URP Mobile/Blocks Unlit AO"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _Atlas("Atlas", 2D) = "white" {}
        [NoScaleOffset] _GrassSideOverlay("Grass Side Overlay", 2D) = "black" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _GrassTint("Grass Tint", Color) = (0.38, 0.52, 0.25, 1)
        [HideInInspector] _FolliageTint("Folliage Tint", Color) = (0.38, 0.52, 0.25, 1)
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
        _AtlasSize("Atlas Size (Tiles XY)", Vector) = (32, 32, 0, 0)
        [Toggle] _AtlasOriginTopLeft("Atlas Origin Top Left", Float) = 0
        _PaddingUV("Atlas Padding", Range(0.0, 0.01)) = 0.001

        [Header(Voxel Light)]
        _MinLight("Minimum Light", Range(0.0, 1.0)) = 0.08
        _VoxelSkyLightMultiplier("Sky Light Multiplier", Range(0.0, 1.0)) = 1.0
        _VoxelLightStrength("Voxel Light Strength", Range(0.0, 2.0)) = 1.0
        _AOStrength("Vertex AO Strength", Range(0.0, 2.0)) = 1.0

        [Header(Face Shading)]
        _Top("Top Shade", Range(0.0, 2.0)) = 1.0
        _Sides("Side Shade", Range(0.0, 2.0)) = 0.82
        _Bottom("Bottom Shade", Range(0.0, 2.0)) = 0.62

        [Header(Cutout)]
        [ToggleUI] _AlphaClip("Alpha Clip", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [HideInInspector] _Cull("__cull", Float) = 2.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector][NoScaleOffset] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _MainTex("Main Tex", 2D) = "white" {}
        [HideInInspector] _Color("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        LOD 50
        Blend [_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
        ZWrite [_ZWrite]
        Cull [_Cull]

        HLSLINCLUDE
        #define VOXEL_MOBILE_BLOCKS 1
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
            float _MinLight;
            float _VoxelSkyLightMultiplier;
            float _VoxelLightStrength;
            float _AOStrength;
            float _Top;
            float _Sides;
            float _Bottom;
            float _AlphaClip;
            float _Cutoff;
        CBUFFER_END

        TEXTURE2D(_GrassSideOverlay);
        SAMPLER(sampler_GrassSideOverlay);

        #include "Assets/Scripts/ProceduralGen/Shader/VoxelMobileUnlitCommon.hlsl"

        half4 MobileForwardFragment(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            half4 surface = SampleVoxelAtlas(input.localUV, input.atlasData.xy, input.atlasData.zw);
            ApplyVoxelAlphaClip(surface.a);

            half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
            half3 baseAlbedo = surface.rgb;
            half3 tintColor = input.tintAndBlockLight.rgb;
            half3 albedo = baseAlbedo * tintColor;

            half tintMask = saturate(input.extra.y);
            half grassSideOverlayMask = saturate(input.extra.w);
            if (abs(normalWS.y) < 0.5h && tintMask > 0.0001h && grassSideOverlayMask > 0.0001h)
            {
                half4 sideOverlay = SAMPLE_TEXTURE2D(_GrassSideOverlay, sampler_GrassSideOverlay, RepeatTileUV(input.localUV));
                half overlayAlpha = saturate(sideOverlay.a) * tintMask * grassSideOverlayMask;
                half3 tintedOverlay = sideOverlay.rgb * tintColor;
                albedo = baseAlbedo * (1.0h - overlayAlpha) + tintedOverlay * overlayAlpha;
            }

            half3 voxelLight = ComputeVoxelLightColor(input.extra.x, input.tintAndBlockLight.a, input.blockLightColor);
            half ao = ComputeAO(input.extra.z);
            half faceShade = ComputeBlockFaceShade(normalWS);
            half3 finalColor = ApplyVoxelFog(albedo * voxelLight * ao * faceShade, input.positionWS);
            return half4(finalColor, surface.a);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex MobileForwardVertex
            #pragma fragment MobileForwardFragment
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex MobileDepthVertex
            #pragma fragment MobileDepthFragment
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
