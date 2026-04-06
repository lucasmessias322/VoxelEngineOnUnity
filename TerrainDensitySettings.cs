using System;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct TerrainDensitySettings
{
    public bool enabled;
    [HideInInspector] public bool debugEnableDetailNoise;
    [HideInInspector] public bool debugEnableOverhangNoise;
    [HideInInspector] public bool debugEnableBiomeDensityMultipliers;
    public int verticalSampleStep;
    public float solidThreshold;
    public float surfaceSearchHeight;
    public float baseSolidBias;
    public float detailScale;
    public float detailAmplitude;
    public int detailOctaves;
    public float detailPersistence;
    public float detailLacunarity;
    public float detailVerticalScale;
    public float detailBandHeight;
    public float overhangScale;
    public float overhangAmplitude;
    public int overhangOctaves;
    public float overhangPersistence;
    public float overhangLacunarity;
    public float overhangVerticalScale;
    public float overhangBandHeight;
    public float overhangBelowSurfaceAllowance;
    public float overhangThreshold;
    public float overhangIsoBandHeight;
    public float overhangPlacementScale;
    public int overhangPlacementOctaves;
    public float overhangPlacementPersistence;
    public float overhangPlacementLacunarity;
    public float overhangContinentalnessMin;
    public float overhangErosionMax;
    public float overhangRidgeMin;
    public float overhangPlacementSharpness;
    public float overhangCarveStrength;

    public static TerrainDensitySettings MinecraftLikeDefault => new TerrainDensitySettings
    {
        enabled = true,
        debugEnableDetailNoise = true,
        debugEnableOverhangNoise = true,
        debugEnableBiomeDensityMultipliers = true,
        verticalSampleStep = 1,
        solidThreshold = 0f,
        surfaceSearchHeight = 72f,
        baseSolidBias = 0f,
        detailScale = 64f,
        detailAmplitude = 9.5f,
        detailOctaves = 3,
        detailPersistence = 0.55f,
        detailLacunarity = 2f,
        detailVerticalScale = 1f,
        detailBandHeight = 48f,
        overhangScale = 48f,
        overhangAmplitude = 8f,
        overhangOctaves = 3,
        overhangPersistence = 0.5f,
        overhangLacunarity = 2f,
        overhangVerticalScale = 0.9f,
        overhangBandHeight = 56f,
        overhangBelowSurfaceAllowance = 28f,
        overhangThreshold = 0.62f,
        overhangIsoBandHeight = 24f,
        overhangPlacementScale = 480f,
        overhangPlacementOctaves = 2,
        overhangPlacementPersistence = 0.52f,
        overhangPlacementLacunarity = 2f,
        overhangContinentalnessMin = 0.42f,
        overhangErosionMax = 0.6f,
        overhangRidgeMin = 0.1f,
        overhangPlacementSharpness = 2.35f,
        overhangCarveStrength = 0.2f
    };

    public static TerrainDensitySettings MinetestInspiredDefault => MinecraftLikeDefault;

    public bool LooksUninitialized =>
        !enabled &&
        verticalSampleStep == 0 &&
        solidThreshold == 0f &&
        surfaceSearchHeight == 0f &&
        baseSolidBias == 0f &&
        detailScale == 0f &&
        detailAmplitude == 0f &&
        detailOctaves == 0 &&
        detailPersistence == 0f &&
        detailLacunarity == 0f &&
        detailVerticalScale == 0f &&
        detailBandHeight == 0f &&
        overhangScale == 0f &&
        overhangAmplitude == 0f &&
        overhangOctaves == 0 &&
        overhangPersistence == 0f &&
        overhangLacunarity == 0f &&
        overhangVerticalScale == 0f &&
        overhangBandHeight == 0f &&
        overhangBelowSurfaceAllowance == 0f &&
        overhangThreshold == 0f &&
        overhangIsoBandHeight == 0f &&
        overhangPlacementScale == 0f &&
        overhangPlacementOctaves == 0 &&
        overhangPlacementPersistence == 0f &&
        overhangPlacementLacunarity == 0f &&
        overhangContinentalnessMin == 0f &&
        overhangErosionMax == 0f &&
        overhangRidgeMin == 0f &&
        overhangPlacementSharpness == 0f &&
        overhangCarveStrength == 0f;

    public bool LooksLikeLegacyWithoutPlacementControls =>
        overhangIsoBandHeight == 0f &&
        overhangPlacementScale == 0f &&
        overhangPlacementOctaves == 0 &&
        overhangPlacementPersistence == 0f &&
        overhangPlacementLacunarity == 0f &&
        overhangContinentalnessMin == 0f &&
        overhangErosionMax == 0f &&
        overhangRidgeMin == 0f &&
        overhangPlacementSharpness == 0f &&
        overhangCarveStrength == 0f;

    public TerrainDensitySettings Sanitized()
    {
        TerrainDensitySettings settings = LooksUninitialized ? MinecraftLikeDefault : this;

        if (settings.LooksLikeLegacyWithoutPlacementControls)
        {
            TerrainDensitySettings defaults = MinecraftLikeDefault;
            settings.overhangIsoBandHeight = defaults.overhangIsoBandHeight;
            settings.overhangPlacementScale = defaults.overhangPlacementScale;
            settings.overhangPlacementOctaves = defaults.overhangPlacementOctaves;
            settings.overhangPlacementPersistence = defaults.overhangPlacementPersistence;
            settings.overhangPlacementLacunarity = defaults.overhangPlacementLacunarity;
            settings.overhangContinentalnessMin = defaults.overhangContinentalnessMin;
            settings.overhangErosionMax = defaults.overhangErosionMax;
            settings.overhangRidgeMin = defaults.overhangRidgeMin;
            settings.overhangPlacementSharpness = defaults.overhangPlacementSharpness;
            settings.overhangCarveStrength = defaults.overhangCarveStrength;
        }

        settings.verticalSampleStep = math.clamp(settings.verticalSampleStep, 1, 8);
        settings.solidThreshold = math.clamp(settings.solidThreshold, -12f, 12f);
        settings.surfaceSearchHeight = math.max(1f, settings.surfaceSearchHeight);
        settings.baseSolidBias = math.clamp(settings.baseSolidBias, -8f, 8f);
        settings.detailScale = math.max(0.001f, settings.detailScale);
        settings.detailAmplitude = math.max(0f, settings.detailAmplitude);
        settings.detailOctaves = math.max(1, settings.detailOctaves);
        settings.detailPersistence = math.clamp(settings.detailPersistence, 0f, 1f);
        settings.detailLacunarity = math.max(1f, settings.detailLacunarity);
        settings.detailVerticalScale = math.max(0.01f, settings.detailVerticalScale);
        settings.detailBandHeight = math.max(1f, settings.detailBandHeight);
        settings.overhangScale = math.max(0.001f, settings.overhangScale);
        settings.overhangAmplitude = math.max(0f, settings.overhangAmplitude);
        settings.overhangOctaves = math.max(1, settings.overhangOctaves);
        settings.overhangPersistence = math.clamp(settings.overhangPersistence, 0f, 1f);
        settings.overhangLacunarity = math.max(1f, settings.overhangLacunarity);
        settings.overhangVerticalScale = math.max(0.01f, settings.overhangVerticalScale);
        settings.overhangBandHeight = math.max(1f, settings.overhangBandHeight);
        settings.overhangBelowSurfaceAllowance = math.max(0f, settings.overhangBelowSurfaceAllowance);
        settings.overhangThreshold = math.clamp(settings.overhangThreshold, 0f, 1f);
        settings.overhangIsoBandHeight = math.max(1f, settings.overhangIsoBandHeight);
        settings.overhangPlacementScale = math.max(1f, settings.overhangPlacementScale);
        settings.overhangPlacementOctaves = math.clamp(settings.overhangPlacementOctaves, 1, 5);
        settings.overhangPlacementPersistence = math.clamp(settings.overhangPlacementPersistence, 0f, 1f);
        settings.overhangPlacementLacunarity = math.max(1f, settings.overhangPlacementLacunarity);
        settings.overhangContinentalnessMin = math.clamp(settings.overhangContinentalnessMin, 0f, 1f);
        settings.overhangErosionMax = math.clamp(settings.overhangErosionMax, 0f, 1f);
        settings.overhangRidgeMin = math.clamp(settings.overhangRidgeMin, -1f, 1f);
        settings.overhangPlacementSharpness = math.max(0.05f, settings.overhangPlacementSharpness);
        settings.overhangCarveStrength = math.clamp(settings.overhangCarveStrength, 0f, 1f);

        if (!settings.debugEnableDetailNoise)
            settings.detailAmplitude = 0f;

        if (!settings.debugEnableOverhangNoise)
            settings.overhangAmplitude = 0f;

        return settings;
    }
}

public enum TerrainDensityClassification : byte
{
    RequiresExactSample = 0,
    Solid = 1,
    Air = 2
}
