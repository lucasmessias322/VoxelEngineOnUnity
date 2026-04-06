using Unity.Burst;
using Unity.Mathematics;

public struct TerrainSurfaceData
{
    // Decisao final de materiais da coluna depois de considerar bioma, praia, altitude e inclinacao.
    public int surfaceHeight;
    public int surfaceLayerDepth;
    public int waterDepth;
    public BiomeType biome;
    public BlockType surfaceBlock;
    public BlockType subsurfaceBlock;
    public bool isBeach;
    public bool isUnderwater;
    public bool isCliff;
    public bool isHighMountain;
    public float slope;
    public float slope01;
}

public static class TerrainSurfaceRules
{
    public const int BeachHeightMargin = 2;
    public const int DefaultSurfaceLayerDepth = 4;
    public const int StoneTransitionDepth = 100;
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
        // Usa um gradiente estilo Sobel para medir o quanto a coluna "puxa" para um lado.
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
        // Etapa 3 do pipeline: converte relevo + clima em blocos visiveis para o topo da coluna.
        CoastSurfaceThresholdSettings coastSurface = biomeNoiseSettings.coastSurface;
        if (coastSurface.LooksUninitialized)
            coastSurface = CoastSurfaceThresholdSettings.Default;

        bool isUnderwater = surfaceHeight < seaLevel;
        int waterDepth = math.max(0, (int)math.ceil(seaLevel - surfaceHeight));
        bool isHighMountain = surfaceHeight >= baseHeight + HighMountainHeightOffset;
        float highMountain01 = math.saturate((surfaceHeight - (baseHeight + HighMountainHeightOffset - 12f)) / 20f);

        BiomeClimateSample climate = BiomeUtility.SampleClimate(worldX, worldZ, biomeNoiseSettings);
        BiomeTerrainSettings terrainSettings = BiomeUtility.BlendTerrainSettings(climate.temperature, climate.humidity, biomeNoiseSettings);
        BlockType biomeSurfaceBlock = BiomeUtility.GetSurfaceBlock(climate.biome, biomeNoiseSettings);
        BlockType biomeSubsurfaceBlock = BiomeUtility.GetSubsurfaceBlock(climate.biome, biomeNoiseSettings);

        BlockType surfaceBlock = biomeSurfaceBlock;
        BlockType subsurfaceBlock = biomeSubsurfaceBlock;

        float surfaceNoiseScale = math.max(0.018f, biomeNoiseSettings.coldSurfaceNoiseScale * 0.75f);
        float surfaceNoise = SampleSurfaceNoise01(worldX, worldZ, surfaceNoiseScale);
        float coastalNoise = SampleSurfaceNoise01(
            worldX + 131,
            worldZ - 173,
            math.max(0.010f, surfaceNoiseScale * 0.58f));
        float shoreHeightMask = math.saturate(
            (seaLevel + BeachHeightMargin + 1f - surfaceHeight)
            / math.max(1f, BeachHeightMargin + 2f));
        float gentleBeachSlopeMask = 1f - math.saturate(
            (slope01 - coastSurface.beachSlopeSoft01)
            / math.max(0.01f, coastSurface.beachSlopeHard01 - coastSurface.beachSlopeSoft01));
        float beachMask = math.saturate(shoreHeightMask * gentleBeachSlopeMask);
        float beachThreshold = coastSurface.beachThresholdBase + (coastalNoise - 0.5f) * coastSurface.beachThresholdNoiseSpan;
        bool isBeach = !isUnderwater && beachMask >= beachThreshold;

        float slopeStoneMask = math.saturate((slope01 - SlopeStoneStart01) / math.max(0.01f, SlopeStoneFull01 - SlopeStoneStart01));
        // Mistura altitude alta e inclinacao para decidir quando a rocha deve "aflorar".
        float exposedStoneMask = math.saturate(math.max(highMountain01, slopeStoneMask * 0.92f + highMountain01 * 0.25f));
        float stoneThreshold = 0.60f + (surfaceNoise - 0.5f) * 0.16f;
        int surfaceLayerDepth = ResolveSurfaceLayerDepth(terrainSettings, slope01, isBeach, highMountain01, exposedStoneMask);

        if (isUnderwater)
        {
            float deepWaterStoneMask = math.saturate(
                (waterDepth - coastSurface.underwaterStoneStartDepth)
                / math.max(1f, coastSurface.underwaterStoneStartDepth));
            float underwaterSlopeStoneMask = math.saturate(
                (slope01 - coastSurface.underwaterSlopeStoneStart01)
                / math.max(0.01f, coastSurface.underwaterSlopeStoneFull01 - coastSurface.underwaterSlopeStoneStart01));
            float underwaterStoneMask = math.saturate(math.max(deepWaterStoneMask, underwaterSlopeStoneMask));
            float underwaterStoneThreshold = coastSurface.underwaterStoneThresholdBase + (coastalNoise - 0.5f) * coastSurface.underwaterStoneThresholdNoiseSpan;
            bool preferStoneFloor = underwaterStoneMask >= underwaterStoneThreshold && waterDepth > 1;

            if (preferStoneFloor)
            {
                surfaceBlock = BlockType.Stone;
                subsurfaceBlock = BlockType.Stone;
                surfaceLayerDepth = 1;
            }
            else
            {
                surfaceBlock = BlockType.Sand;
                subsurfaceBlock = BlockType.Sand;
                int sandDepth = 4 + math.max(0, math.min(3, waterDepth / 3));
                if (waterDepth <= coastSurface.underwaterSandMaxDepth)
                    sandDepth++;

                surfaceLayerDepth = math.max(surfaceLayerDepth, sandDepth);
            }

            isBeach = false;
        }
        else if (isBeach)
        {
            surfaceBlock = BlockType.Sand;
            subsurfaceBlock = BlockType.Sand;
            surfaceLayerDepth = math.max(surfaceLayerDepth, 4);
        }
        else
        {
            float shoreTransitionMask = math.saturate(
                (seaLevel + coastSurface.shoreBlendHeightMargin - surfaceHeight)
                / math.max(1f, coastSurface.shoreBlendHeightMargin - BeachHeightMargin + 1f));
            float shorePatchMask = shoreTransitionMask
                * (1f - slope01)
                * math.saturate(0.58f + (coastalNoise - 0.5f) * 0.9f);
            if (shorePatchMask >= coastSurface.shorePatchThreshold && surfaceBlock == BlockType.Grass)
            {
                subsurfaceBlock = BlockType.Sand;
                surfaceLayerDepth = math.max(surfaceLayerDepth, 3);
            }
        }

        if (!isBeach && !isUnderwater)
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

        surfaceLayerDepth = math.clamp(surfaceLayerDepth, 1, MaxSurfaceLayerDepth);

        return new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceLayerDepth = surfaceLayerDepth,
            waterDepth = waterDepth,
            biome = climate.biome,
            surfaceBlock = surfaceBlock,
            subsurfaceBlock = subsurfaceBlock,
            isBeach = isBeach,
            isUnderwater = isUnderwater,
            isCliff = isCliff,
            isHighMountain = isHighMountain,
            slope = slope,
            slope01 = slope01
        };
    }

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, in TerrainSurfaceData surface)
    {
        // Materializa a coluna virtual: topo, subsolo, pedra e deepslate.
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
        // O bioma frio ainda pode variar com altitude: pedra exposta e neve nao dependem so do bioma base.
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
