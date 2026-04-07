using System;
using Unity.Burst;
using Unity.Collections;
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
    // Multiplicadores que dizem como um bioma dobra o mesmo sinal de relevo.
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

[Serializable]
public struct CoastSurfaceThresholdSettings
{
    // Thresholds para ajustar costa e fundo submerso sem recompilar.
    public float beachSlopeSoft01;
    public float beachSlopeHard01;
    public int shoreBlendHeightMargin;
    public int underwaterSandMaxDepth;
    public int underwaterStoneStartDepth;
    public float underwaterSlopeStoneStart01;
    public float underwaterSlopeStoneFull01;
    public float beachThresholdBase;
    public float beachThresholdNoiseSpan;
    public float underwaterStoneThresholdBase;
    public float underwaterStoneThresholdNoiseSpan;
    public float shorePatchThreshold;

    public static CoastSurfaceThresholdSettings Default => new CoastSurfaceThresholdSettings
    {
        beachSlopeSoft01 = 0.30f,
        beachSlopeHard01 = 0.62f,
        shoreBlendHeightMargin = 5,
        underwaterSandMaxDepth = 7,
        underwaterStoneStartDepth = 11,
        underwaterSlopeStoneStart01 = 0.55f,
        underwaterSlopeStoneFull01 = 0.88f,
        beachThresholdBase = 0.48f,
        beachThresholdNoiseSpan = 0.18f,
        underwaterStoneThresholdBase = 0.56f,
        underwaterStoneThresholdNoiseSpan = 0.16f,
        shorePatchThreshold = 0.72f
    };

    public bool LooksUninitialized =>
        beachSlopeSoft01 == 0f &&
        beachSlopeHard01 == 0f &&
        shoreBlendHeightMargin == 0 &&
        underwaterSandMaxDepth == 0 &&
        underwaterStoneStartDepth == 0 &&
        underwaterSlopeStoneStart01 == 0f &&
        underwaterSlopeStoneFull01 == 0f &&
        beachThresholdBase == 0f &&
        beachThresholdNoiseSpan == 0f &&
        underwaterStoneThresholdBase == 0f &&
        underwaterStoneThresholdNoiseSpan == 0f &&
        shorePatchThreshold == 0f;

    public CoastSurfaceThresholdSettings Sanitized()
    {
        CoastSurfaceThresholdSettings settings = LooksUninitialized ? Default : this;
        settings.beachSlopeSoft01 = math.saturate(settings.beachSlopeSoft01);
        settings.beachSlopeHard01 = math.clamp(settings.beachSlopeHard01, settings.beachSlopeSoft01 + 0.01f, 1f);
        settings.shoreBlendHeightMargin = math.clamp(settings.shoreBlendHeightMargin, 2, 24);
        settings.underwaterSandMaxDepth = math.clamp(settings.underwaterSandMaxDepth, 0, 32);
        settings.underwaterStoneStartDepth = math.clamp(settings.underwaterStoneStartDepth, 1, 48);
        settings.underwaterSlopeStoneStart01 = math.saturate(settings.underwaterSlopeStoneStart01);
        settings.underwaterSlopeStoneFull01 = math.clamp(settings.underwaterSlopeStoneFull01, settings.underwaterSlopeStoneStart01 + 0.01f, 1f);
        settings.beachThresholdBase = math.saturate(settings.beachThresholdBase);
        settings.beachThresholdNoiseSpan = math.clamp(settings.beachThresholdNoiseSpan, 0f, 1f);
        settings.underwaterStoneThresholdBase = math.saturate(settings.underwaterStoneThresholdBase);
        settings.underwaterStoneThresholdNoiseSpan = math.clamp(settings.underwaterStoneThresholdNoiseSpan, 0f, 1f);
        settings.shorePatchThreshold = math.saturate(settings.shorePatchThreshold);
        return settings;
    }
}

[Serializable]
public struct BiomeDensityMultipliers
{
    // Multiplicadores aplicados por bioma sobre a configuracao base de densidade.
    public float solidThresholdMultiplier;
    public float surfaceSearchHeightMultiplier;
    public float baseSolidBiasMultiplier;

    public static BiomeDensityMultipliers Identity => new BiomeDensityMultipliers
    {
        solidThresholdMultiplier = 1f,
        surfaceSearchHeightMultiplier = 1f,
        baseSolidBiasMultiplier = 1f
    };

    public bool LooksUninitialized =>
        solidThresholdMultiplier == 0f &&
        surfaceSearchHeightMultiplier == 0f &&
        baseSolidBiasMultiplier == 0f;

    public BiomeDensityMultipliers Sanitized()
    {
        BiomeDensityMultipliers settings = LooksUninitialized ? Identity : this;
        settings.solidThresholdMultiplier = math.max(0f, settings.solidThresholdMultiplier);
        settings.surfaceSearchHeightMultiplier = math.max(0f, settings.surfaceSearchHeightMultiplier);
        settings.baseSolidBiasMultiplier = math.max(0f, settings.baseSolidBiasMultiplier);
        return settings;
    }
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
    // Snapshot compacto enviado para jobs Burst sem depender do MonoBehaviour World.
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
    public BiomeDensityMultipliers desertDensity;
    public BiomeDensityMultipliers savannaDensity;
    public BiomeDensityMultipliers meadowDensity;
    public BiomeDensityMultipliers taigaDensity;

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

    public CoastSurfaceThresholdSettings coastSurface;

    public TerrainSplineShaperSettings terrainShaper;
}

public struct VegetationBillboardRuleData
{
    public BiomeType biome;
    public BlockType groundBlock;
    public BlockType billboardBlock;
    public float weight;
    public float chanceMultiplier;
}

public static class VegetationBillboardUtility
{
    private const float MinHeightScale = 0.82f;
    private const float MaxHeightScale = 1.22f;
    private const float MinWidthScale = 0.84f;
    private const float MaxWidthScale = 1.12f;
    private const float BaseHalfWidth = 0.38f;
    private const float BaseYOffset = -0.02f;
    private const float YOffsetVariance = 0.012f;

    [BurstCompile]
    public static float ComputeEffectiveChance(
        int worldX,
        int worldZ,
        float baseChance,
        float noiseScale,
        float chanceMultiplier = 1f)
    {
        float safeScale = math.max(1e-4f, noiseScale);
        float scaledChance = math.max(0f, baseChance) * math.max(0f, chanceMultiplier);

        uint domainHash = Hash3D(worldX, 0, worldZ, 0x517CC1B7u);
        float jitterX = HashToSignedUnit(MixHash(domainHash ^ 0x91E10DA5u)) * 0.45f;
        float jitterZ = HashToSignedUnit(MixHash(domainHash ^ 0xC79E7B1Du)) * 0.45f;
        float2 p = new float2(worldX + jitterX, worldZ + jitterZ) * safeScale;

        // Rotacao + warp leve evita padroes em linha quando a chance e muito baixa.
        float2 rotatedA = new float2(
            p.x * 0.8660254f - p.y * 0.5f,
            p.x * 0.5f + p.y * 0.8660254f);
        float2 rotatedB = new float2(
            p.x * 0.5f + p.y * 0.8660254f,
            -p.x * 0.8660254f + p.y * 0.5f);
        float warp = noise.snoise(rotatedB * 0.71f + new float2(19.31f, -43.73f));

        float macro = noise.snoise(rotatedA * 0.62f + new float2(123.17f, -91.73f) + warp * 0.45f) * 0.5f + 0.5f;
        float detail = noise.snoise(rotatedB * 1.93f + new float2(-47.31f, 67.19f) + warp * 0.95f) * 0.5f + 0.5f;

        float patch = math.saturate(math.lerp(macro, detail, 0.35f));
        float cluster = patch * patch * (3f - 2f * patch);
        return math.saturate(scaledChance * math.lerp(0.18f, 1.9f, cluster));
    }

    [BurstCompile]
    public static uint ComputeVariantHash(int worldX, int worldY, int worldZ)
    {
        return Hash3D(worldX, worldY, worldZ, 0xA2C79E29u);
    }

    [BurstCompile]
    public static float ComputeJitterOffset(uint variantHash, int shift, float jitter)
    {
        uint jitterHash = MixHash(variantHash ^ (uint)(shift * 0x9E3779B9) ^ 0xB5297A4Du);
        return HashToSignedUnit(jitterHash) * math.max(0f, jitter);
    }

    [BurstCompile]
    public static float ComputeHeight(float baseHeight, uint variantHash)
    {
        float scale = math.lerp(MinHeightScale, MaxHeightScale, HashToUnit01(MixHash(variantHash ^ 0x1B56C4E9u)));
        return math.max(0.2f, baseHeight * scale);
    }

    [BurstCompile]
    public static float ComputeHalfWidth(uint variantHash)
    {
        float scale = math.lerp(MinWidthScale, MaxWidthScale, HashToUnit01(MixHash(variantHash ^ 0x8765ABCDu)));
        return BaseHalfWidth * scale;
    }

    [BurstCompile]
    public static float ComputeBaseYOffset(uint variantHash)
    {
        float centered = HashToSignedUnit(MixHash(variantHash ^ 0xDEADBEEFu));
        return BaseYOffset + centered * YOffsetVariance;
    }

    [BurstCompile]
    public static bool TryResolveBillboardRule(
        in BiomeNoiseSettings biomeNoiseSettings,
        NativeArray<VegetationBillboardRuleData> rules,
        int worldX,
        int worldY,
        int worldZ,
        BlockType groundBlock,
        float baseChance,
        float noiseScale,
        BlockType fallbackBillboardBlock,
        out BlockType billboardBlock,
        out uint variationHash)
    {
        variationHash = 0u;
        billboardBlock = fallbackBillboardBlock;

        BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, biomeNoiseSettings);
        float totalWeight = 0f;
        float weightedChanceMultiplier = 0f;
        bool hasBiomeRule = false;
        BlockType firstMatchingBlock = fallbackBillboardBlock;

        for (int i = 0; i < rules.Length; i++)
        {
            VegetationBillboardRuleData rule = rules[i];
            if (rule.biome != biome || rule.groundBlock != groundBlock || rule.billboardBlock == BlockType.Air)
                continue;

            float ruleWeight = rule.weight > 0f ? rule.weight : 1f;
            totalWeight += ruleWeight;
            weightedChanceMultiplier += ruleWeight * math.max(0f, rule.chanceMultiplier);

            if (!hasBiomeRule)
            {
                hasBiomeRule = true;
                firstMatchingBlock = rule.billboardBlock;
            }
        }

        if (!hasBiomeRule)
        {
            if (groundBlock != BlockType.Grass || fallbackBillboardBlock == BlockType.Air)
                return false;
        }

        float chanceMultiplier = hasBiomeRule
            ? weightedChanceMultiplier / math.max(1e-5f, totalWeight)
            : 1f;

        float effectiveChance = ComputeEffectiveChance(worldX, worldZ, baseChance, noiseScale, chanceMultiplier);
        uint chanceHash = Hash3D(worldX, worldY, worldZ, 0xD8163841u);
        float chanceRoll = HashToUnit01(chanceHash);
        float microVariance = math.lerp(0.85f, 1.15f, HashToUnit01(MixHash(chanceHash ^ 0x3C6EF372u)));
        effectiveChance = math.saturate(effectiveChance * microVariance);
        if (chanceRoll > effectiveChance)
            return false;

        variationHash = ComputeVariantHash(worldX, worldY, worldZ);
        if (!hasBiomeRule)
            return true;

        float pickTarget = HashToUnit01(MixHash(variationHash ^ 0x9E3779B9u)) * math.max(1e-5f, totalWeight);
        float accumulatedWeight = 0f;

        for (int i = 0; i < rules.Length; i++)
        {
            VegetationBillboardRuleData rule = rules[i];
            if (rule.biome != biome || rule.groundBlock != groundBlock || rule.billboardBlock == BlockType.Air)
                continue;

            accumulatedWeight += rule.weight > 0f ? rule.weight : 1f;
            if (pickTarget <= accumulatedWeight)
            {
                billboardBlock = rule.billboardBlock;
                return true;
            }
        }

        billboardBlock = firstMatchingBlock;
        return true;
    }

    public static bool TryResolveBillboardRule(
        in BiomeNoiseSettings biomeNoiseSettings,
        VegetationBillboardRuleData[] rules,
        int worldX,
        int worldY,
        int worldZ,
        BlockType groundBlock,
        float baseChance,
        float noiseScale,
        BlockType fallbackBillboardBlock,
        out BlockType billboardBlock,
        out uint variationHash)
    {
        variationHash = 0u;
        billboardBlock = fallbackBillboardBlock;

        BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, biomeNoiseSettings);
        float totalWeight = 0f;
        float weightedChanceMultiplier = 0f;
        bool hasBiomeRule = false;
        BlockType firstMatchingBlock = fallbackBillboardBlock;

        if (rules != null)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                VegetationBillboardRuleData rule = rules[i];
                if (rule.biome != biome || rule.groundBlock != groundBlock || rule.billboardBlock == BlockType.Air)
                    continue;

                float ruleWeight = rule.weight > 0f ? rule.weight : 1f;
                totalWeight += ruleWeight;
                weightedChanceMultiplier += ruleWeight * math.max(0f, rule.chanceMultiplier);

                if (!hasBiomeRule)
                {
                    hasBiomeRule = true;
                    firstMatchingBlock = rule.billboardBlock;
                }
            }
        }

        if (!hasBiomeRule)
        {
            if (groundBlock != BlockType.Grass || fallbackBillboardBlock == BlockType.Air)
                return false;
        }

        float chanceMultiplier = hasBiomeRule
            ? weightedChanceMultiplier / math.max(1e-5f, totalWeight)
            : 1f;

        float effectiveChance = ComputeEffectiveChance(worldX, worldZ, baseChance, noiseScale, chanceMultiplier);
        uint chanceHash = Hash3D(worldX, worldY, worldZ, 0xD8163841u);
        float chanceRoll = HashToUnit01(chanceHash);
        float microVariance = math.lerp(0.85f, 1.15f, HashToUnit01(MixHash(chanceHash ^ 0x3C6EF372u)));
        effectiveChance = math.saturate(effectiveChance * microVariance);
        if (chanceRoll > effectiveChance)
            return false;

        variationHash = ComputeVariantHash(worldX, worldY, worldZ);
        if (!hasBiomeRule)
            return true;

        float pickTarget = HashToUnit01(MixHash(variationHash ^ 0x9E3779B9u)) * math.max(1e-5f, totalWeight);
        float accumulatedWeight = 0f;

        for (int i = 0; i < rules.Length; i++)
        {
            VegetationBillboardRuleData rule = rules[i];
            if (rule.biome != biome || rule.groundBlock != groundBlock || rule.billboardBlock == BlockType.Air)
                continue;

            accumulatedWeight += rule.weight > 0f ? rule.weight : 1f;
            if (pickTarget <= accumulatedWeight)
            {
                billboardBlock = rule.billboardBlock;
                return true;
            }
        }

        billboardBlock = firstMatchingBlock;
        return true;
    }

    [BurstCompile]
    private static uint Hash3D(int x, int y, int z, uint salt)
    {
        unchecked
        {
            uint h = (uint)x * 0x9E3779B9u;
            h ^= (uint)y * 0x85EBCA6Bu;
            h ^= (uint)z * 0xC2B2AE35u;
            h ^= salt;
            return MixHash(h);
        }
    }

    [BurstCompile]
    private static uint MixHash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    [BurstCompile]
    private static float HashToUnit01(uint hash)
    {
        return (hash & 0x00FFFFFFu) / 16777215f;
    }

    [BurstCompile]
    private static float HashToSignedUnit(uint hash)
    {
        return HashToUnit01(hash) * 2f - 1f;
    }
}

public static class BiomeUtility
{
    [BurstCompile]
    public static BiomeClimateSample SampleClimate(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        // Clima e relevo sao amostrados separados para permitir transicoes suaves entre biomas.
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
        // Em vez de "trocar de preset" na fronteira do bioma, interpolamos todos os parametros.
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
    public static BiomeDensityMultipliers BlendDensityMultipliers(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        BiomeClimateSample climate = SampleClimate(worldX, worldZ, settings);
        return BlendDensityMultipliers(climate.temperature, climate.humidity, settings);
    }

    [BurstCompile]
    public static BiomeDensityMultipliers BlendDensityMultipliers(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        BiomeTerrainBlendWeights weights = GetTerrainBlendWeights(temperature, humidity, settings);

        return new BiomeDensityMultipliers
        {
            solidThresholdMultiplier =
                settings.desertDensity.solidThresholdMultiplier * weights.desert +
                settings.savannaDensity.solidThresholdMultiplier * weights.savanna +
                settings.meadowDensity.solidThresholdMultiplier * weights.meadow +
                settings.taigaDensity.solidThresholdMultiplier * weights.taiga,
            surfaceSearchHeightMultiplier =
                settings.desertDensity.surfaceSearchHeightMultiplier * weights.desert +
                settings.savannaDensity.surfaceSearchHeightMultiplier * weights.savanna +
                settings.meadowDensity.surfaceSearchHeightMultiplier * weights.meadow +
                settings.taigaDensity.surfaceSearchHeightMultiplier * weights.taiga,
            baseSolidBiasMultiplier =
                settings.desertDensity.baseSolidBiasMultiplier * weights.desert +
                settings.savannaDensity.baseSolidBiasMultiplier * weights.savanna +
                settings.meadowDensity.baseSolidBiasMultiplier * weights.meadow +
                settings.taigaDensity.baseSolidBiasMultiplier * weights.taiga
        };
    }

    [BurstCompile]
    public static BiomeTerrainBlendWeights GetTerrainBlendWeights(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        // Os pesos sempre somam 1 para que a transicao entre biomas seja continua.
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
