using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct DensityLayer
{
    public bool enabled;
    public float horizontalScale;
    public float verticalScale;
    public float amplitude;
    public int octaves;
    public float persistence;
    public float lacunarity;
    public Vector3 offset;
    public float maxAmp;
    public float ridgeFactor;
}

[Serializable]
public struct TerrainDensitySettings
{
    public bool enabled;
    [Min(4)] public int shellDepth;
    [Min(2)] public int surfaceTransition;
    [Min(0)] public int overhangHeight;
    [Min(0)] public int deepSolidDepth;
    [Range(0f, 2.5f)] public float surfaceBias;
    [Range(0f, 2.5f)] public float featureStrength;
    [Range(0f, 2.5f)] public float mountainBoost;
    [Range(0f, 2.5f)] public float cliffBoost;
    [Range(-1f, 1f)] public float densityThreshold;
    [Min(0f)] public float mountainStartHeightOffset;
    [Min(1f)] public float mountainBlendRange;

    public static TerrainDensitySettings Default => new TerrainDensitySettings
    {
        enabled = true,
        shellDepth = 34,
        surfaceTransition = 14,
        overhangHeight = 42,
        deepSolidDepth = 12,
        surfaceBias = 1.35f,
        featureStrength = 1.15f,
        mountainBoost = 1.05f,
        cliffBoost = 1.1f,
        densityThreshold = 0.02f,
        mountainStartHeightOffset = 18f,
        mountainBlendRange = 56f
    };

    public static TerrainDensitySettings DisabledDefault
    {
        get
        {
            TerrainDensitySettings settings = Default;
            settings.enabled = false;
            return settings;
        }
    }

    public bool LooksUninitialized =>
        !enabled &&
        shellDepth == 0 &&
        surfaceTransition == 0 &&
        overhangHeight == 0 &&
        deepSolidDepth == 0 &&
        surfaceBias == 0f &&
        featureStrength == 0f &&
        mountainBoost == 0f &&
        cliffBoost == 0f &&
        densityThreshold == 0f &&
        mountainStartHeightOffset == 0f &&
        mountainBlendRange == 0f;
}

public static class TerrainDensitySampler
{
    private const float MinNoiseScale = 0.0001f;

    [BurstCompile]
    public static float ComputeFeatureMask(
        int surfaceHeight,
        int northHeight,
        int southHeight,
        int eastHeight,
        int westHeight,
        int baseHeight,
        int cliffThreshold,
        in TerrainDensitySettings densitySettings)
    {
        float mountainMask = math.saturate(
            (surfaceHeight - (baseHeight + densitySettings.mountainStartHeightOffset))
            / math.max(1f, densitySettings.mountainBlendRange));

        int maxDiff = math.max(math.abs(surfaceHeight - northHeight), math.abs(surfaceHeight - southHeight));
        maxDiff = math.max(maxDiff, math.abs(surfaceHeight - eastHeight));
        maxDiff = math.max(maxDiff, math.abs(surfaceHeight - westHeight));

        float safeCliffThreshold = math.max(1f, cliffThreshold);
        float cliffMask = math.saturate((maxDiff - safeCliffThreshold + 1f) / (safeCliffThreshold * 2f));

        return math.saturate(math.max(mountainMask, cliffMask * math.max(0f, densitySettings.cliffBoost)));
    }

    [BurstCompile]
    public static int GetEffectiveOverhangHeight(float featureMask, in TerrainDensitySettings densitySettings)
    {
        if (densitySettings.overhangHeight <= 0)
            return 0;

        float scaled = densitySettings.overhangHeight * math.lerp(0.35f, 1f, math.saturate(featureMask));
        return math.max(0, (int)math.ceil(scaled));
    }

    [BurstCompile]
    public static float SampleSignedDensity(
        int worldX,
        int worldY,
        int worldZ,
        int surfaceHeight,
        float featureMask,
        int baseHeight,
        in TerrainDensitySettings densitySettings,
        NativeArray<DensityLayer> densityLayers)
    {
        float compositeNoise = SampleCompositeDensity(worldX, worldY, worldZ, densityLayers);
        return ComposeSignedDensity(
            worldY,
            surfaceHeight,
            featureMask,
            baseHeight,
            densitySettings,
            compositeNoise);
    }

    public static float SampleSignedDensity(
        int worldX,
        int worldY,
        int worldZ,
        int surfaceHeight,
        float featureMask,
        int baseHeight,
        in TerrainDensitySettings densitySettings,
        DensityLayer[] densityLayers)
    {
        float compositeNoise = SampleCompositeDensity(worldX, worldY, worldZ, densityLayers);
        return ComposeSignedDensity(
            worldY,
            surfaceHeight,
            featureMask,
            baseHeight,
            densitySettings,
            compositeNoise);
    }

    [BurstCompile]
    public static float SampleCompositeDensity(int worldX, int worldY, int worldZ, NativeArray<DensityLayer> densityLayers)
    {
        float total = 0f;
        float weightSum = 0f;

        for (int i = 0; i < densityLayers.Length; i++)
        {
            DensityLayer layer = densityLayers[i];
            if (!layer.enabled)
                continue;

            float weight = math.max(0.0001f, layer.amplitude);
            total += SampleLayer(worldX, worldY, worldZ, layer) * weight;
            weightSum += weight;
        }

        if (weightSum <= 0f)
            return 0f;

        return math.clamp(total / weightSum, -1f, 1f);
    }

    public static float SampleCompositeDensity(int worldX, int worldY, int worldZ, DensityLayer[] densityLayers)
    {
        if (densityLayers == null || densityLayers.Length == 0)
            return 0f;

        float total = 0f;
        float weightSum = 0f;

        for (int i = 0; i < densityLayers.Length; i++)
        {
            DensityLayer layer = densityLayers[i];
            if (!layer.enabled)
                continue;

            float weight = math.max(0.0001f, layer.amplitude);
            total += SampleLayer(worldX, worldY, worldZ, layer) * weight;
            weightSum += weight;
        }

        if (weightSum <= 0f)
            return 0f;

        return math.clamp(total / weightSum, -1f, 1f);
    }

    [BurstCompile]
    private static float ComposeSignedDensity(
        int worldY,
        int surfaceHeight,
        float featureMask,
        int baseHeight,
        in TerrainDensitySettings densitySettings,
        float compositeNoise)
    {
        float feature = math.saturate(featureMask);
        float support = (surfaceHeight - worldY + 0.5f) / math.max(1f, densitySettings.surfaceTransition);
        float above01 = worldY > surfaceHeight
            ? math.saturate((worldY - surfaceHeight) / math.max(1f, densitySettings.overhangHeight))
            : 0f;
        float mountainMask = math.saturate(
            (surfaceHeight - (baseHeight + densitySettings.mountainStartHeightOffset))
            / math.max(1f, densitySettings.mountainBlendRange));

        float density = support * math.max(0.1f, densitySettings.surfaceBias);
        density += compositeNoise * densitySettings.featureStrength * feature;
        density += feature * 0.08f;
        density += mountainMask * densitySettings.mountainBoost * 0.22f;
        density -= above01 * math.lerp(1.2f, 0.82f, math.saturate(densitySettings.mountainBoost * 0.5f));
        return density;
    }

    [BurstCompile]
    private static float SampleLayer(int worldX, int worldY, int worldZ, DensityLayer layer)
    {
        float horizontalScale = math.max(MinNoiseScale, layer.horizontalScale);
        float verticalScale = math.max(MinNoiseScale, layer.verticalScale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);
        float ridgeFactor = math.max(1f, layer.ridgeFactor);

        float ox = worldX + layer.offset.x;
        float oy = worldY + layer.offset.y;
        float oz = worldZ + layer.offset.z;

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float3(
                (ox * frequency) / horizontalScale,
                (oy * frequency) / verticalScale,
                (oz * frequency) / horizontalScale)) * 0.5f + 0.5f;

            if (ridgeFactor > 1f)
            {
                sample = math.abs(1f - 2f * sample);
                sample = math.pow(sample, ridgeFactor);
            }

            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value01 = math.clamp(total / maxAmp, 0f, 1f);
        return value01 * 2f - 1f;
    }
}
