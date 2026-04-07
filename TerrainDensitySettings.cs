using System;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct TerrainDensitySettings
{
    public bool enabled;
    public int verticalSampleStep;
    public float solidThreshold;
    public float surfaceSearchHeight;
    public float baseSolidBias;

    public static TerrainDensitySettings MinecraftLikeDefault => new TerrainDensitySettings
    {
        enabled = true,
        verticalSampleStep = 1,
        solidThreshold = 0f,
        surfaceSearchHeight = 72f,
        baseSolidBias = 0f
    };

    public static TerrainDensitySettings MinetestInspiredDefault => MinecraftLikeDefault;

    public bool LooksUninitialized =>
        !enabled &&
        verticalSampleStep == 0 &&
        solidThreshold == 0f &&
        surfaceSearchHeight == 0f &&
        baseSolidBias == 0f;

    public TerrainDensitySettings Sanitized()
    {
        TerrainDensitySettings settings = LooksUninitialized ? MinecraftLikeDefault : this;

        settings.verticalSampleStep = math.clamp(settings.verticalSampleStep, 1, 8);
        settings.solidThreshold = math.clamp(settings.solidThreshold, -12f, 12f);
        settings.surfaceSearchHeight = math.max(1f, settings.surfaceSearchHeight);
        settings.baseSolidBias = math.clamp(settings.baseSolidBias, -8f, 8f);

        return settings;
    }
}

public enum TerrainDensityClassification : byte
{
    RequiresExactSample = 0,
    Solid = 1,
    Air = 2
}
