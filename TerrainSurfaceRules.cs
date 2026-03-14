using Unity.Burst;
using Unity.Mathematics;

public struct TerrainSurfaceData
{
    public int surfaceHeight;
    public int surfaceLayerDepth;
    public BlockType surfaceBlock;
    public BlockType subsurfaceBlock;
    public bool isBeach;
    public bool isCliff;
    public bool isHighMountain;
    public float slope;
    public float slope01;
}

public static class TerrainSurfaceRules
{
    public const int BeachHeightMargin = 2;
    public const int DefaultSurfaceLayerDepth = 4;
    public const int StoneTransitionDepth = 50;
    public const int HighMountainHeightOffset = 70;

    private const float SlopeStoneStart01 = 0.24f;
    private const float SlopeStoneFull01 = 0.86f;
    private const int MaxSurfaceLayerDepth = 12;

    [BurstCompile]
    public static float GetSlopeFromNeighborHeights(
        int northHeight,
        int southHeight,
        int eastHeight,
        int westHeight,
        int northEastHeight,
        int northWestHeight,
        int southEastHeight,
        int southWestHeight)
    {
        float gradientX = (northEastHeight + 2f * eastHeight + southEastHeight) - (northWestHeight + 2f * westHeight + southWestHeight);
        float gradientZ = (southWestHeight + 2f * southHeight + southEastHeight) - (northWestHeight + 2f * northHeight + northEastHeight);
        return math.sqrt(gradientX * gradientX + gradientZ * gradientZ) * 0.125f;
    }

    [BurstCompile]
    public static float NormalizeSlope(float slope, int threshold)
    {
        float safeThreshold = math.max(1f, threshold);
        return math.saturate(slope / (safeThreshold * 2.5f));
    }

    [BurstCompile]
    public static bool IsSteepSlope(float slope, int threshold)
    {
        return slope >= math.max(1f, threshold);
    }

    [BurstCompile]
    public static TerrainSurfaceData EvaluateColumnSurface(
        int worldX,
        int worldZ,
        int surfaceHeight,
        float slope,
        float slope01,
        bool isCliff,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        bool isBeach = surfaceHeight <= seaLevel + BeachHeightMargin;
        bool isHighMountain = surfaceHeight >= baseHeight + HighMountainHeightOffset;
        float highMountain01 = math.saturate((surfaceHeight - (baseHeight + HighMountainHeightOffset - 12f)) / 20f);

        BiomeClimateSample climate = BiomeUtility.SampleClimate(worldX, worldZ, biomeNoiseSettings);
        BiomeTerrainSettings terrainSettings = BiomeUtility.BlendTerrainSettings(climate.temperature, climate.humidity, biomeNoiseSettings);
        BlockType biomeSurfaceBlock = BiomeUtility.GetSurfaceBlock(climate.biome, biomeNoiseSettings);
        BlockType biomeSubsurfaceBlock = BiomeUtility.GetSubsurfaceBlock(climate.biome, biomeNoiseSettings);

        BlockType surfaceBlock = isBeach ? BlockType.Sand : biomeSurfaceBlock;
        BlockType subsurfaceBlock = isBeach ? BlockType.Sand : biomeSubsurfaceBlock;

        float surfaceNoiseScale = math.max(0.018f, biomeNoiseSettings.coldSurfaceNoiseScale * 0.75f);
        float surfaceNoise = SampleSurfaceNoise01(worldX, worldZ, surfaceNoiseScale);
        float slopeStoneMask = math.saturate((slope01 - SlopeStoneStart01) / math.max(0.01f, SlopeStoneFull01 - SlopeStoneStart01));
        float exposedStoneMask = math.saturate(math.max(highMountain01, slopeStoneMask * 0.92f + highMountain01 * 0.25f));
        float stoneThreshold = 0.60f + (surfaceNoise - 0.5f) * 0.16f;
        int surfaceLayerDepth = ResolveSurfaceLayerDepth(terrainSettings, slope01, isBeach, highMountain01, exposedStoneMask);

        if (!isBeach)
        {
            if (exposedStoneMask >= stoneThreshold)
            {
                surfaceBlock = BlockType.Stone;
                subsurfaceBlock = BlockType.Stone;
                surfaceLayerDepth = 1;
            }
            else if (exposedStoneMask >= stoneThreshold - 0.22f)
            {
                subsurfaceBlock = BlockType.Stone;
                surfaceLayerDepth = math.min(surfaceLayerDepth, 2);
            }

            ApplyColdMountainSurface(
                worldX,
                worldZ,
                surfaceHeight,
                slope01,
                isHighMountain,
                baseHeight,
                climate.temperature,
                climate.humidity,
                biomeNoiseSettings,
                ref surfaceBlock,
                ref subsurfaceBlock);

            if (subsurfaceBlock == BlockType.Stone)
                surfaceLayerDepth = math.min(surfaceLayerDepth, surfaceBlock == BlockType.Stone ? 1 : 2);
        }

        return new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceLayerDepth = surfaceLayerDepth,
            surfaceBlock = surfaceBlock,
            subsurfaceBlock = subsurfaceBlock,
            isBeach = isBeach,
            isCliff = isCliff,
            isHighMountain = isHighMountain,
            slope = slope,
            slope01 = slope01
        };
    }

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, in TerrainSurfaceData surface)
    {
        if (y == surface.surfaceHeight)
            return surface.surfaceBlock;

        int surfaceLayerDepth = math.max(1, surface.surfaceLayerDepth);
        if (y > surface.surfaceHeight - surfaceLayerDepth)
            return surface.subsurfaceBlock;

        if (y <= 2)
            return BlockType.Bedrock;

        return y > surface.surfaceHeight - StoneTransitionDepth
            ? BlockType.Stone
            : BlockType.Deepslate;
    }

    [BurstCompile]
    private static int ResolveSurfaceLayerDepth(
        in BiomeTerrainSettings terrainSettings,
        float slope01,
        bool isBeach,
        float highMountain01,
        float exposedStoneMask)
    {
        float flatDepth = terrainSettings.surfaceDepth > 0f ? terrainSettings.surfaceDepth : DefaultSurfaceLayerDepth;
        float steepDepth = terrainSettings.steepSurfaceDepth > 0f ? terrainSettings.steepSurfaceDepth : math.min(flatDepth, 2f);
        float steepness = math.saturate(math.max(slope01, highMountain01 * 0.75f));
        float depth = math.lerp(flatDepth, steepDepth, steepness);
        depth = math.lerp(depth, math.max(1f, steepDepth - 0.5f), math.saturate(exposedStoneMask * 0.8f));

        if (isBeach)
            depth = math.max(depth, 4f);

        return math.clamp((int)math.round(depth), 1, MaxSurfaceLayerDepth);
    }

    [BurstCompile]
    private static void ApplyColdMountainSurface(
        int worldX,
        int worldZ,
        int surfaceHeight,
        float slope01,
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

        float stoneMask = math.saturate(coldMask * (stoneAltitudeMask * 0.85f + slope01 * 0.42f + (isHighMountain ? 0.18f : 0f)));
        float snowMask = math.saturate(coldMask * snowAltitudeMask * snowTemperatureMask * math.lerp(1f, 0.32f, slope01));
        float stoneThreshold = 0.54f + (surfaceNoise - 0.5f) * 0.18f;
        float snowThreshold = 0.50f + (surfaceNoise - 0.5f) * 0.22f;

        if (stoneMask >= stoneThreshold)
        {
            surfaceBlock = BlockType.Stone;
            subsurfaceBlock = BlockType.Stone;
        }

        if (slope01 <= 0.72f && snowMask >= snowThreshold)
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