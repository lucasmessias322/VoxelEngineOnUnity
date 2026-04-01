using System;
using Unity.Burst;
using Unity.Mathematics;

public enum BiomeType : byte
{
    Desert = 0,
    Savanna = 1,
    Meadow = 2,
    Taiga = 3
}

[Serializable]
public struct BiomeTerrainSettings
{
    public float reliefMultiplier;
    public float hillsMultiplier;
    public float mountainMultiplier;
    public float erosionBias;
    public float erosionPower;
    public float flattenStrength;
    public float heightOffset;
    public float surfaceDepth;
    public float steepSurfaceDepth;

    public static BiomeTerrainSettings DesertDefault => new BiomeTerrainSettings
    {
        reliefMultiplier = 0.72f,
        hillsMultiplier = 0.30f,
        mountainMultiplier = 0.55f,
        erosionBias = 0.22f,
        erosionPower = 0.80f,
        flattenStrength = 0.50f,
        heightOffset = -1.25f,
        surfaceDepth = 6.5f,
        steepSurfaceDepth = 2.5f
    };

    public static BiomeTerrainSettings SavannaDefault => new BiomeTerrainSettings
    {
        reliefMultiplier = 0.90f,
        hillsMultiplier = 0.62f,
        mountainMultiplier = 0.78f,
        erosionBias = 0.08f,
        erosionPower = 0.95f,
        flattenStrength = 0.22f,
        heightOffset = 0.35f,
        surfaceDepth = 4.75f,
        steepSurfaceDepth = 2.5f
    };

    public static BiomeTerrainSettings MeadowDefault => new BiomeTerrainSettings
    {
        reliefMultiplier = 0.82f,
        hillsMultiplier = 0.45f,
        mountainMultiplier = 0.65f,
        erosionBias = 0.12f,
        erosionPower = 0.90f,
        flattenStrength = 0.35f,
        heightOffset = 0f,
        surfaceDepth = 5.5f,
        steepSurfaceDepth = 3f
    };

    public static BiomeTerrainSettings TaigaDefault => new BiomeTerrainSettings
    {
        reliefMultiplier = 1.16f,
        hillsMultiplier = 1.05f,
        mountainMultiplier = 1.32f,
        erosionBias = -0.12f,
        erosionPower = 1.18f,
        flattenStrength = 0.06f,
        heightOffset = 1.4f,
        surfaceDepth = 6.25f,
        steepSurfaceDepth = 2.25f
    };
}

public struct BiomeClimateSample
{
    public float temperature;
    public float humidity;
    public BiomeType biome;
}

public struct BiomeTerrainBlendWeights
{
    public float desert;
    public float savanna;
    public float meadow;
    public float taiga;
}

public struct BiomeNoiseSettings
{
    public float temperatureScale;
    public float humidityScale;
    public float2 temperatureOffset;
    public float2 humidityOffset;
    public float terrainBlendRange;
    public float desertMinTemperature;
    public float desertMaxHumidity;
    public float savannaMinTemperature;
    public float taigaMaxTemperature;
    public float taigaMinHumidity;

    public BiomeTerrainSettings desertTerrain;
    public BiomeTerrainSettings savannaTerrain;
    public BiomeTerrainSettings meadowTerrain;
    public BiomeTerrainSettings taigaTerrain;

    public float altitudeTemperatureFalloff;
    public float coldStoneStartHeightOffset;
    public float coldStoneBlendRange;
    public float coldSnowStartHeightOffset;
    public float coldSnowBlendRange;
    public float coldSnowTemperatureThreshold;
    public float coldSurfaceNoiseScale;

    public BlockType desertSurfaceBlock;
    public BlockType desertSubsurfaceBlock;
    public BlockType savannaSurfaceBlock;
    public BlockType savannaSubsurfaceBlock;
    public BlockType meadowSurfaceBlock;
    public BlockType meadowSubsurfaceBlock;
    public BlockType taigaSurfaceBlock;
    public BlockType taigaSubsurfaceBlock;

    public TerrainSplineShaperSettings terrainShaper;
}

public static class BiomeUtility
{
    [BurstCompile]
    public static BiomeClimateSample SampleClimate(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        float temperature = SampleTemperature(worldX, worldZ, settings);
        float humidity = SampleHumidity(worldX, worldZ, settings);
        return new BiomeClimateSample
        {
            temperature = temperature,
            humidity = humidity,
            biome = GetBiomeType(temperature, humidity, settings)
        };
    }

    [BurstCompile]
    public static float SampleTemperature(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        return Sample01(worldX, worldZ, settings.temperatureScale, settings.temperatureOffset);
    }

    [BurstCompile]
    public static float SampleHumidity(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        return Sample01(worldX, worldZ, settings.humidityScale, settings.humidityOffset);
    }

    [BurstCompile]
    public static BiomeType GetBiomeType(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        return SampleClimate(worldX, worldZ, settings).biome;
    }

    [BurstCompile]
    public static BiomeType GetBiomeType(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        if (temperature >= settings.desertMinTemperature && humidity <= settings.desertMaxHumidity)
            return BiomeType.Desert;

        if (temperature <= settings.taigaMaxTemperature && humidity >= settings.taigaMinHumidity)
            return BiomeType.Taiga;

        if (temperature >= settings.savannaMinTemperature)
            return BiomeType.Savanna;

        return BiomeType.Meadow;
    }

    [BurstCompile]
    public static BlockType GetSurfaceBlock(BiomeType biome)
    {
        return GetDefaultSurfaceBlock(biome);
    }

    [BurstCompile]
    public static BlockType GetSurfaceBlock(BiomeType biome, in BiomeNoiseSettings settings)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return UseConfiguredOrDefault(settings.desertSurfaceBlock, GetDefaultSurfaceBlock(BiomeType.Desert));
            case BiomeType.Taiga:
                return UseConfiguredOrDefault(settings.taigaSurfaceBlock, GetDefaultSurfaceBlock(BiomeType.Taiga));
            case BiomeType.Savanna:
                return UseConfiguredOrDefault(settings.savannaSurfaceBlock, GetDefaultSurfaceBlock(BiomeType.Savanna));
            case BiomeType.Meadow:
            default:
                return UseConfiguredOrDefault(settings.meadowSurfaceBlock, GetDefaultSurfaceBlock(BiomeType.Meadow));
        }
    }

    [BurstCompile]
    public static BlockType GetSubsurfaceBlock(BiomeType biome)
    {
        return GetDefaultSubsurfaceBlock(biome);
    }

    [BurstCompile]
    public static BlockType GetSubsurfaceBlock(BiomeType biome, in BiomeNoiseSettings settings)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return UseConfiguredOrDefault(settings.desertSubsurfaceBlock, GetDefaultSubsurfaceBlock(BiomeType.Desert));
            case BiomeType.Taiga:
                return UseConfiguredOrDefault(settings.taigaSubsurfaceBlock, GetDefaultSubsurfaceBlock(BiomeType.Taiga));
            case BiomeType.Savanna:
                return UseConfiguredOrDefault(settings.savannaSubsurfaceBlock, GetDefaultSubsurfaceBlock(BiomeType.Savanna));
            case BiomeType.Meadow:
            default:
                return UseConfiguredOrDefault(settings.meadowSubsurfaceBlock, GetDefaultSubsurfaceBlock(BiomeType.Meadow));
        }
    }

    [BurstCompile]
    public static BlockType GetDefaultSurfaceBlock(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return BlockType.Sand;
            case BiomeType.Taiga:
            case BiomeType.Savanna:
            case BiomeType.Meadow:
            default:
                return BlockType.Grass;
        }
    }

    [BurstCompile]
    public static BlockType GetDefaultSubsurfaceBlock(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return BlockType.Sand;
            case BiomeType.Taiga:
                return BlockType.Dirt;
            case BiomeType.Savanna:
            case BiomeType.Meadow:
            default:
                return BlockType.Dirt;
        }
    }

    [BurstCompile]
    public static BiomeTerrainSettings GetTerrainSettings(BiomeType biome, in BiomeNoiseSettings settings)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return settings.desertTerrain;
            case BiomeType.Taiga:
                return settings.taigaTerrain;
            case BiomeType.Savanna:
                return settings.savannaTerrain;
            case BiomeType.Meadow:
            default:
                return settings.meadowTerrain;
        }
    }

    [BurstCompile]
    public static BiomeTerrainSettings BlendTerrainSettings(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        BiomeClimateSample climate = SampleClimate(worldX, worldZ, settings);
        return BlendTerrainSettings(climate.temperature, climate.humidity, settings);
    }

    [BurstCompile]
    public static BiomeTerrainSettings BlendTerrainSettings(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        BiomeTerrainBlendWeights weights = GetTerrainBlendWeights(temperature, humidity, settings);

        return new BiomeTerrainSettings
        {
            reliefMultiplier =
                settings.desertTerrain.reliefMultiplier * weights.desert +
                settings.savannaTerrain.reliefMultiplier * weights.savanna +
                settings.meadowTerrain.reliefMultiplier * weights.meadow +
                settings.taigaTerrain.reliefMultiplier * weights.taiga,
            hillsMultiplier =
                settings.desertTerrain.hillsMultiplier * weights.desert +
                settings.savannaTerrain.hillsMultiplier * weights.savanna +
                settings.meadowTerrain.hillsMultiplier * weights.meadow +
                settings.taigaTerrain.hillsMultiplier * weights.taiga,
            mountainMultiplier =
                settings.desertTerrain.mountainMultiplier * weights.desert +
                settings.savannaTerrain.mountainMultiplier * weights.savanna +
                settings.meadowTerrain.mountainMultiplier * weights.meadow +
                settings.taigaTerrain.mountainMultiplier * weights.taiga,
            erosionBias =
                settings.desertTerrain.erosionBias * weights.desert +
                settings.savannaTerrain.erosionBias * weights.savanna +
                settings.meadowTerrain.erosionBias * weights.meadow +
                settings.taigaTerrain.erosionBias * weights.taiga,
            erosionPower =
                settings.desertTerrain.erosionPower * weights.desert +
                settings.savannaTerrain.erosionPower * weights.savanna +
                settings.meadowTerrain.erosionPower * weights.meadow +
                settings.taigaTerrain.erosionPower * weights.taiga,
            flattenStrength =
                settings.desertTerrain.flattenStrength * weights.desert +
                settings.savannaTerrain.flattenStrength * weights.savanna +
                settings.meadowTerrain.flattenStrength * weights.meadow +
                settings.taigaTerrain.flattenStrength * weights.taiga,
            heightOffset =
                settings.desertTerrain.heightOffset * weights.desert +
                settings.savannaTerrain.heightOffset * weights.savanna +
                settings.meadowTerrain.heightOffset * weights.meadow +
                settings.taigaTerrain.heightOffset * weights.taiga,
            surfaceDepth =
                settings.desertTerrain.surfaceDepth * weights.desert +
                settings.savannaTerrain.surfaceDepth * weights.savanna +
                settings.meadowTerrain.surfaceDepth * weights.meadow +
                settings.taigaTerrain.surfaceDepth * weights.taiga,
            steepSurfaceDepth =
                settings.desertTerrain.steepSurfaceDepth * weights.desert +
                settings.savannaTerrain.steepSurfaceDepth * weights.savanna +
                settings.meadowTerrain.steepSurfaceDepth * weights.meadow +
                settings.taigaTerrain.steepSurfaceDepth * weights.taiga
        };
    }

    [BurstCompile]
    public static BiomeTerrainBlendWeights GetTerrainBlendWeights(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        float blend = math.max(0.01f, settings.terrainBlendRange);
        float warm = Smooth01(temperature, settings.savannaMinTemperature - blend, settings.savannaMinTemperature + blend);
        float desertHot = Smooth01(temperature, settings.desertMinTemperature - blend, settings.desertMinTemperature + blend);
        float cold = 1f - Smooth01(temperature, settings.taigaMaxTemperature - blend, settings.taigaMaxTemperature + blend);
        float dry = 1f - Smooth01(humidity, settings.desertMaxHumidity - blend, settings.desertMaxHumidity + blend);
        float humid = Smooth01(humidity, settings.taigaMinHumidity - blend, settings.taigaMinHumidity + blend);

        float desert = desertHot * dry;
        float taiga = cold * humid;
        float savannaHumidity = 1f - Smooth01(humidity, settings.taigaMinHumidity - blend * 0.5f, settings.taigaMinHumidity + blend * 1.5f);
        float savanna = warm * savannaHumidity * (1f - desert) * (1f - taiga * 0.65f);
        float meadow = math.max(0.0001f, 1f - desert - savanna - taiga);

        float weightSum = desert + savanna + meadow + taiga;
        if (weightSum <= 1e-5f)
        {
            return new BiomeTerrainBlendWeights
            {
                meadow = 1f
            };
        }

        float invWeightSum = 1f / weightSum;
        return new BiomeTerrainBlendWeights
        {
            desert = desert * invWeightSum,
            savanna = savanna * invWeightSum,
            meadow = meadow * invWeightSum,
            taiga = taiga * invWeightSum
        };
    }

    [BurstCompile]
    public static float GetAltitudeAdjustedTemperature(float temperature, int surfaceHeight, int baseHeight, in BiomeNoiseSettings settings)
    {
        float altitudeDelta = math.max(0f, surfaceHeight - baseHeight);
        return temperature - altitudeDelta * math.max(0f, settings.altitudeTemperatureFalloff);
    }

    [BurstCompile]
    private static BlockType UseConfiguredOrDefault(BlockType configured, BlockType fallback)
    {
        return configured == BlockType.Air ? fallback : configured;
    }

    [BurstCompile]
    private static float Smooth01(float value, float min, float max)
    {
        return math.smoothstep(min, max, value);
    }

    [BurstCompile]
    private static float Sample01(int worldX, int worldZ, float scale, float2 offset)
    {
        float safeScale = math.max(0.0001f, scale);
        float nx = worldX * safeScale + offset.x;
        float nz = worldZ * safeScale + offset.y;
        return math.saturate(noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f);
    }
}
