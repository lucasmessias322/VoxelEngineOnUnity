#ifndef VOXEL_MOBILE_UNLIT_COMMON_INCLUDED
#define VOXEL_MOBILE_UNLIT_COMMON_INCLUDED

TEXTURE2D(_Atlas);
SAMPLER(sampler_Atlas);

float4 _FogColorSurface;
float _FogStart;
float _FogEnd;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    half4 blockLightColor : COLOR;
    float2 uv0 : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float4 uv2 : TEXCOORD2;
    float4 uv3 : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    float2 localUV : TEXCOORD2;
    float4 atlasData : TEXCOORD3;
    half4 extra : TEXCOORD4;
    half4 tintAndBlockLight : TEXCOORD5;
    half3 blockLightColor : TEXCOORD6;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct DepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 localUV : TEXCOORD0;
    float2 atlasOrigin : TEXCOORD1;
    float2 atlasSize : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct VoxelVertexData
{
    float3 positionWS;
    half3 normalWS;
    float2 localUV;
    float2 atlasOrigin;
    float2 atlasSize;
    half4 extra;
    half3 tintColor;
    half blockLight;
    half3 blockLightColor;
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
    float2 normalizedPadding = saturate(_PaddingUV / tileSize);
    repeatedUV = lerp(normalizedPadding, 1.0 - normalizedPadding, repeatedUV);

    float2 resolvedOrigin = atlasOrigin;
    if (_AtlasOriginTopLeft > 0.5)
        resolvedOrigin.y = 1.0 - atlasOrigin.y - tileSize.y;

    return resolvedOrigin + repeatedUV * tileSize;
}

half4 SampleVoxelAtlas(float2 localUV, float2 atlasOrigin, float2 atlasSize)
{
    float2 atlasUV = ResolveAtlasUV(localUV, atlasOrigin, atlasSize);
    return SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, atlasUV) * (half4)_BaseColor;
}

void ApplyVoxelAlphaClip(half alpha)
{
    if (_AlphaClip > 0.5)
        clip(alpha - (half)_Cutoff);
}

half3 ApplyVoxelFog(half3 color, float3 positionWS)
{
    float fogRange = max(0.0001, _FogEnd - _FogStart);
    float cameraDistance = distance(GetCameraPositionWS(), positionWS);
    half fogFactor = (half)saturate((cameraDistance - _FogStart) / fogRange);
    return lerp(color, (half3)_FogColorSurface.rgb, fogFactor);
}

half3 ResolveBlockLightColor(half blockLight, half3 blockLightColor)
{
    half hasColor = step(0.0001h, dot(blockLightColor, half3(1.0h, 1.0h, 1.0h)));
    return lerp(blockLight.xxx, saturate(blockLightColor), hasColor);
}

half3 ComputeVoxelLightColor(half skyLight, half blockLight, half3 blockLightColor)
{
    half skyVoxelLight = saturate(skyLight) * (half)_VoxelSkyLightMultiplier * (half)_VoxelLightStrength;
    half3 resolvedBlockLight = ResolveBlockLightColor(blockLight, blockLightColor) * (half)_VoxelLightStrength;
    half minLight = (half)_MinLight;
    return saturate(max(half3(minLight, minLight, minLight), max(skyVoxelLight.xxx, resolvedBlockLight)));
}

half ComputeAO(half ao)
{
    return lerp(1.0h, saturate(ao), (half)_AOStrength);
}

half ComputeBlockFaceShade(half3 normalWS)
{
    if (normalWS.y > 0.5h)
        return (half)_Top;

    if (normalWS.y < -0.5h)
        return (half)_Bottom;

    return (half)_Sides;
}

half ComputeLeafFaceShade(half3 normalWS)
{
    half yAbs = saturate(abs(normalWS.y));
    return lerp((half)_Sides, (half)_Top, yAbs);
}

half3 ResolveGrassTintByWorldPosition(float3 positionWS)
{
    float blendStrength = saturate(_BiomeTintBlendEnabled);
    if (blendStrength <= 0.0001)
        return (half3)_GrassTint.rgb;

    float2 uv = saturate((positionWS.xz - _BiomeTintOriginXZ.xy) * _BiomeTintInvSizeXZ.xy);
    half3 south = lerp((half3)_GrassTintCorner00.rgb, (half3)_GrassTintCorner10.rgb, (half)uv.x);
    half3 north = lerp((half3)_GrassTintCorner01.rgb, (half3)_GrassTintCorner11.rgb, (half)uv.x);
    half3 blended = lerp(south, north, (half)uv.y);
    return lerp((half3)_GrassTint.rgb, blended, (half)blendStrength);
}

half3 ResolveFoliageTintByWorldPosition(float3 positionWS)
{
    half3 baseTint = dot((half3)_FolliageTint.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h
        ? (half3)_FolliageTint.rgb
        : (half3)_GrassTint.rgb;

    float blendStrength = saturate(_BiomeTintBlendEnabled);
    if (blendStrength <= 0.0001)
        return baseTint;

    float2 uv = saturate((positionWS.xz - _BiomeTintOriginXZ.xy) * _BiomeTintInvSizeXZ.xy);
    half3 corner00 = dot((half3)_FolliageTintCorner00.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? (half3)_FolliageTintCorner00.rgb : (half3)_GrassTintCorner00.rgb;
    half3 corner10 = dot((half3)_FolliageTintCorner10.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? (half3)_FolliageTintCorner10.rgb : (half3)_GrassTintCorner10.rgb;
    half3 corner01 = dot((half3)_FolliageTintCorner01.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? (half3)_FolliageTintCorner01.rgb : (half3)_GrassTintCorner01.rgb;
    half3 corner11 = dot((half3)_FolliageTintCorner11.rgb, half3(1.0h, 1.0h, 1.0h)) > 0.0001h ? (half3)_FolliageTintCorner11.rgb : (half3)_GrassTintCorner11.rgb;
    half3 south = lerp(corner00, corner10, (half)uv.x);
    half3 north = lerp(corner01, corner11, (half)uv.x);
    half3 blended = lerp(south, north, (half)uv.y);
    return lerp(baseTint, blended, (half)blendStrength);
}

VoxelVertexData ResolveMeshVertexData(Attributes input)
{
    VoxelVertexData data;
    data.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    data.normalWS = TransformObjectToWorldNormal(input.normalOS);
    data.localUV = input.uv0;
    data.atlasOrigin = input.uv1;
    data.atlasSize = input.uv3.xy;
    data.blockLight = saturate((half)input.uv3.z);
    data.blockLightColor = saturate(input.blockLightColor.rgb);

    #if defined(VOXEL_MOBILE_BLOCKS)
        float packedExtraW = max(0.0, input.uv2.w);
        half decodedSubchunk = (half)floor(packedExtraW + 0.0001);
        half decodedOverlayMask = saturate((half)((packedExtraW - decodedSubchunk) * 4.0));
        data.extra = half4(saturate(input.uv2.xyz), decodedOverlayMask);
        half3 grassTint = ResolveGrassTintByWorldPosition(data.positionWS);
        data.tintColor = lerp(half3(1.0h, 1.0h, 1.0h), grassTint, saturate(data.extra.y));
    #else
        data.extra = half4(saturate(input.uv2.xyz), 0.0h);
        half3 foliageTint = ResolveFoliageTintByWorldPosition(data.positionWS);
        data.tintColor = lerp(half3(1.0h, 1.0h, 1.0h), foliageTint, saturate(data.extra.y));
    #endif

    return data;
}

Varyings MobileForwardVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VoxelVertexData data = ResolveMeshVertexData(input);
    output.positionWS = data.positionWS;
    output.normalWS = data.normalWS;
    output.localUV = data.localUV;
    output.atlasData = float4(data.atlasOrigin, data.atlasSize);
    output.extra = data.extra;
    output.tintAndBlockLight = half4(data.tintColor, data.blockLight);
    output.blockLightColor = data.blockLightColor;
    output.positionCS = TransformWorldToHClip(data.positionWS);
    return output;
}

DepthVaryings MobileDepthVertex(Attributes input)
{
    DepthVaryings output = (DepthVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VoxelVertexData data = ResolveMeshVertexData(input);
    output.localUV = data.localUV;
    output.atlasOrigin = data.atlasOrigin;
    output.atlasSize = data.atlasSize;
    output.positionCS = TransformWorldToHClip(data.positionWS);
    return output;
}

half4 MobileDepthFragment(DepthVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 surface = SampleVoxelAtlas(input.localUV, input.atlasOrigin, input.atlasSize);
    ApplyVoxelAlphaClip(surface.a);
    return 0;
}

#endif
