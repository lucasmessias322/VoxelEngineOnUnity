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

    public static TerrainDensitySettings MinetestInspiredDefault => new TerrainDensitySettings
    {
        enabled = true,
        debugEnableDetailNoise = true,
        debugEnableOverhangNoise = true,
        debugEnableBiomeDensityMultipliers = true,
        verticalSampleStep = 2,
        solidThreshold = 0f,
        surfaceSearchHeight = 24f,
        baseSolidBias = 0.85f,
        detailScale = 42f,
        detailAmplitude = 6.5f,
        detailOctaves = 3,
        detailPersistence = 0.52f,
        detailLacunarity = 2.05f,
        detailVerticalScale = 0.78f,
        detailBandHeight = 16f,
        overhangScale = 24f,
        overhangAmplitude = 4.5f,
        overhangOctaves = 2,
        overhangPersistence = 0.55f,
        overhangLacunarity = 2.1f,
        overhangVerticalScale = 0.42f,
        overhangBandHeight = 18f,
        overhangBelowSurfaceAllowance = 10f,
        overhangThreshold = 0.58f
    };

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
        overhangThreshold == 0f;

    public TerrainDensitySettings Sanitized()
    {
        TerrainDensitySettings settings = LooksUninitialized ? MinetestInspiredDefault : this;
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
