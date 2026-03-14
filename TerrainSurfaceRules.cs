using Unity.Burst;
using Unity.Mathematics;

public struct TerrainSurfaceData
{
    public int surfaceHeight;
    public BlockType surfaceBlock;
    public BlockType subsurfaceBlock;
    public bool isBeach;
    public bool isCliff;
    public bool isHighMountain;
}

public static class TerrainSurfaceRules
{
    public const int BeachHeightMargin = 2;
    public const int SurfaceLayerDepth = 4;
    public const int StoneTransitionDepth = 50;
    public const int HighMountainHeightOffset = 70;

    [BurstCompile]
    public static bool IsCliffFromNeighborHeights(
        int centerHeight,
        int northHeight,
        int southHeight,
        int eastHeight,
        int westHeight,
        int threshold)
    {
        int maxDiff = math.max(math.abs(centerHeight - northHeight), math.abs(centerHeight - southHeight));
        maxDiff = math.max(maxDiff, math.abs(centerHeight - eastHeight));
        maxDiff = math.max(maxDiff, math.abs(centerHeight - westHeight));
        return maxDiff >= threshold;
    }

    [BurstCompile]
    public static TerrainSurfaceData EvaluateColumnSurface(
        int worldX,
        int worldZ,
        int surfaceHeight,
        bool isCliff,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        bool isBeach = surfaceHeight <= seaLevel + BeachHeightMargin;
        bool isHighMountain = surfaceHeight >= baseHeight + HighMountainHeightOffset;

        BiomeClimateSample climate = BiomeUtility.SampleClimate(worldX, worldZ, biomeNoiseSettings);
        BlockType biomeSurfaceBlock = BiomeUtility.GetSurfaceBlock(climate.biome, biomeNoiseSettings);
        BlockType biomeSubsurfaceBlock = BiomeUtility.GetSubsurfaceBlock(climate.biome, biomeNoiseSettings);

        BlockType surfaceBlock = isBeach ? BlockType.Sand : biomeSurfaceBlock;
        BlockType subsurfaceBlock = isBeach ? BlockType.Sand : biomeSubsurfaceBlock;

        if (!isBeach && (isHighMountain || isCliff))
        {
            surfaceBlock = BlockType.Stone;
            subsurfaceBlock = BlockType.Stone;
        }

        if (!isBeach)
        {
            ApplyColdMountainSurface(
                worldX,
                worldZ,
                surfaceHeight,
                isCliff,
                isHighMountain,
                baseHeight,
                climate.temperature,
                climate.humidity,
                biomeNoiseSettings,
                ref surfaceBlock,
                ref subsurfaceBlock);
        }

        return new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceBlock = surfaceBlock,
            subsurfaceBlock = subsurfaceBlock,
            isBeach = isBeach,
            isCliff = isCliff,
            isHighMountain = isHighMountain
        };
    }

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, in TerrainSurfaceData surface)
    {
        if (y == surface.surfaceHeight)
            return surface.surfaceBlock;

        if (y > surface.surfaceHeight - SurfaceLayerDepth)
            return surface.subsurfaceBlock;

        if (y <= 2)
            return BlockType.Bedrock;

        return y > surface.surfaceHeight - StoneTransitionDepth
            ? BlockType.Stone
            : BlockType.Deepslate;
    }

    [BurstCompile]
    private static void ApplyColdMountainSurface(
        int worldX,
        int worldZ,
        int surfaceHeight,
        bool isCliff,
        bool isHighMountain,
        int baseHeight,
        float temperature,
        float humidity,
        in BiomeNoiseSettings biomeNoiseSettings,
        ref BlockType surfaceBlock,
        ref BlockType subsurfaceBlock)
    {
        BiomeTerrainBlendWeights weights = BiomeUtility.GetTerrainBlendWeights(temperature, humidity, biomeNoiseSettings);
        float coldMask = weights.taiga;
        if (coldMask <= 1e-3f)
            return;

        float adjustedTemperature = BiomeUtility.GetAltitudeAdjustedTemperature(temperature, surfaceHeight, baseHeight, biomeNoiseSettings);
        float stoneAltitudeMask = math.saturate((surfaceHeight - (baseHeight + biomeNoiseSettings.coldStoneStartHeightOffset))
            / math.max(1f, biomeNoiseSettings.coldStoneBlendRange));
        float snowAltitudeMask = math.saturate((surfaceHeight - (baseHeight + biomeNoiseSettings.coldSnowStartHeightOffset))
            / math.max(1f, biomeNoiseSettings.coldSnowBlendRange));
        float snowTemperatureMask = math.saturate((biomeNoiseSettings.coldSnowTemperatureThreshold - adjustedTemperature) / 0.18f);
        float surfaceNoise = SampleSurfaceNoise01(worldX, worldZ, biomeNoiseSettings.coldSurfaceNoiseScale);

        float stoneMask = math.saturate(coldMask * (stoneAltitudeMask * 0.85f + (isCliff ? 0.35f : 0f) + (isHighMountain ? 0.18f : 0f)));
        float snowMask = math.saturate(coldMask * snowAltitudeMask * snowTemperatureMask * (isCliff ? 0.25f : 1f));
        float stoneThreshold = 0.54f + (surfaceNoise - 0.5f) * 0.18f;
        float snowThreshold = 0.50f + (surfaceNoise - 0.5f) * 0.22f;

        if (stoneMask >= stoneThreshold)
        {
            surfaceBlock = BlockType.Stone;
            subsurfaceBlock = BlockType.Stone;
        }

        if (!isCliff && snowMask >= snowThreshold)
            surfaceBlock = BlockType.Snow;
    }

    [BurstCompile]
    private static float SampleSurfaceNoise01(int worldX, int worldZ, float scale)
    {
        float safeScale = math.max(0.001f, scale);
        float nx = worldX * safeScale + 17.213f;
        float nz = worldZ * safeScale - 9.371f;
        return math.saturate(noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f);
    }
}
