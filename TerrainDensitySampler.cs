using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

public static class TerrainDensitySampler
{
    [BurstCompile]
    public static TerrainDensitySettings ResolveBiomeDensitySettings(
        int worldX,
        int worldZ,
        in TerrainDensitySettings densitySettings,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        return ApplyBiomeDensityMultipliers(
            densitySettings,
            BiomeUtility.BlendDensityMultipliers(worldX, worldZ, biomeNoiseSettings));
    }

    [BurstCompile]
    public static TerrainDensitySettings ResolveBiomeDensitySettings(
        float temperature,
        float humidity,
        in TerrainDensitySettings densitySettings,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        return ApplyBiomeDensityMultipliers(
            densitySettings,
            BiomeUtility.BlendDensityMultipliers(temperature, humidity, biomeNoiseSettings));
    }

    [BurstCompile]
    public static float SampleTerrainDensity(
        int worldX,
        int worldY,
        int worldZ,
        int baseSurfaceHeight,
        float offsetX,
        float offsetZ,
        in TerrainDensitySettings densitySettings)
    {
        return GetBaseDensity(worldY, baseSurfaceHeight, densitySettings);
    }

    [BurstCompile]
    public static TerrainDensityClassification ClassifyDensityWithoutNoise(
        int worldY,
        int baseSurfaceHeight,
        in TerrainDensitySettings densitySettings)
    {
        float baseDensity = GetBaseDensity(worldY, baseSurfaceHeight, densitySettings);
        return baseDensity > densitySettings.solidThreshold
            ? TerrainDensityClassification.Solid
            : TerrainDensityClassification.Air;
    }

    [BurstCompile]
    public static bool IsSolidAt(
        int worldX,
        int worldY,
        int worldZ,
        int baseSurfaceHeight,
        float offsetX,
        float offsetZ,
        in TerrainDensitySettings densitySettings)
    {
        return SampleTerrainDensity(worldX, worldY, worldZ, baseSurfaceHeight, offsetX, offsetZ, densitySettings)
            > densitySettings.solidThreshold;
    }

    [BurstCompile]
    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings,
        in TerrainDensitySettings densitySettings)
    {
        int baseSurfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(
            worldX,
            worldZ,
            noiseLayers,
            baseHeight,
            offsetX,
            offsetZ,
            worldHeight,
            biomeNoiseSettings);

        TerrainDensitySettings resolvedDensitySettings = ResolveBiomeDensitySettings(
            worldX,
            worldZ,
            densitySettings,
            biomeNoiseSettings);

        return SampleSurfaceHeightFromBaseSurface(
            worldX,
            worldZ,
            baseSurfaceHeight,
            offsetX,
            offsetZ,
            worldHeight,
            resolvedDensitySettings);
    }

    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings,
        in TerrainDensitySettings densitySettings)
    {
        int baseSurfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(
            worldX,
            worldZ,
            noiseLayers,
            baseHeight,
            offsetX,
            offsetZ,
            worldHeight,
            biomeNoiseSettings);

        TerrainDensitySettings resolvedDensitySettings = ResolveBiomeDensitySettings(
            worldX,
            worldZ,
            densitySettings,
            biomeNoiseSettings);

        return SampleSurfaceHeightFromBaseSurface(
            worldX,
            worldZ,
            baseSurfaceHeight,
            offsetX,
            offsetZ,
            worldHeight,
            resolvedDensitySettings);
    }

    [BurstCompile]
    public static TerrainColumnContext SampleColumnContext(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings,
        in TerrainDensitySettings densitySettings)
    {
        int surfaceHeight = SampleSurfaceHeight(worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northHeight = SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southHeight = SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int eastHeight = SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int westHeight = SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northEastHeight = SampleSurfaceHeight(worldX + 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northWestHeight = SampleSurfaceHeight(worldX - 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southEastHeight = SampleSurfaceHeight(worldX + 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southWestHeight = SampleSurfaceHeight(worldX - 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);

        return TerrainColumnSampler.CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            northEastHeight,
            northWestHeight,
            southEastHeight,
            southWestHeight,
            cliffThreshold,
            baseHeight,
            seaLevel,
            biomeNoiseSettings);
    }

    public static TerrainColumnContext SampleColumnContext(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings,
        in TerrainDensitySettings densitySettings)
    {
        int surfaceHeight = SampleSurfaceHeight(worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northHeight = SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southHeight = SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int eastHeight = SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int westHeight = SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northEastHeight = SampleSurfaceHeight(worldX + 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int northWestHeight = SampleSurfaceHeight(worldX - 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southEastHeight = SampleSurfaceHeight(worldX + 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);
        int southWestHeight = SampleSurfaceHeight(worldX - 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings, densitySettings);

        return TerrainColumnSampler.CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            northEastHeight,
            northWestHeight,
            southEastHeight,
            southWestHeight,
            cliffThreshold,
            baseHeight,
            seaLevel,
            biomeNoiseSettings);
    }

    [BurstCompile]
    private static int SampleSurfaceHeightFromBaseSurface(
        int worldX,
        int worldZ,
        int baseSurfaceHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in TerrainDensitySettings densitySettings)
    {
        if (!densitySettings.enabled)
            return baseSurfaceHeight;

        int searchTop = GetDensityBandTopY(baseSurfaceHeight, worldHeight, densitySettings);
        int searchBottom = GetGuaranteedSolidY(baseSurfaceHeight, densitySettings);
        int sampleStep = math.max(1, densitySettings.verticalSampleStep);

        for (int sampleY = searchTop; sampleY >= searchBottom; sampleY -= sampleStep)
        {
            TerrainDensityClassification classification = ClassifyDensityWithoutNoise(sampleY, baseSurfaceHeight, densitySettings);
            if (classification == TerrainDensityClassification.Air)
                continue;

            int refineTop = math.min(searchTop, sampleY + sampleStep - 1);
            for (int y = refineTop; y >= sampleY; y--)
            {
                TerrainDensityClassification refineClassification = ClassifyDensityWithoutNoise(y, baseSurfaceHeight, densitySettings);
                if (refineClassification == TerrainDensityClassification.Air)
                    continue;
                if (refineClassification == TerrainDensityClassification.Solid
                    || SampleTerrainDensity(worldX, y, worldZ, baseSurfaceHeight, offsetX, offsetZ, densitySettings) > densitySettings.solidThreshold)
                    return y;
            }

            if (classification == TerrainDensityClassification.Solid
                || SampleTerrainDensity(worldX, sampleY, worldZ, baseSurfaceHeight, offsetX, offsetZ, densitySettings) > densitySettings.solidThreshold)
                return sampleY;
        }

        return math.max(2, searchBottom);
    }

    [BurstCompile]
    private static int GetMaxSurfaceRise(in TerrainDensitySettings densitySettings)
    {
        if (!densitySettings.enabled)
            return 0;

        return math.max(4, (int)math.ceil(densitySettings.surfaceSearchHeight));
    }

    [BurstCompile]
    public static int GetDensityBandTopY(int baseSurfaceHeight, int worldHeight, in TerrainDensitySettings densitySettings)
    {
        int top = baseSurfaceHeight + GetMaxSurfaceRise(densitySettings);
        return math.clamp(top, 3, worldHeight - 1);
    }

    [BurstCompile]
    public static int GetGuaranteedSolidY(int baseSurfaceHeight, in TerrainDensitySettings densitySettings)
    {
        float guaranteedSolidY = baseSurfaceHeight
            + densitySettings.baseSolidBias
            - densitySettings.solidThreshold;

        return math.max(3, (int)math.floor(guaranteedSolidY));
    }

    [BurstCompile]
    private static float GetBaseDensity(int worldY, int baseSurfaceHeight, in TerrainDensitySettings densitySettings)
    {
        return (baseSurfaceHeight + densitySettings.baseSolidBias) - worldY;
    }

    [BurstCompile]
    private static TerrainDensitySettings ApplyBiomeDensityMultipliers(
        in TerrainDensitySettings densitySettings,
        in BiomeDensityMultipliers densityMultipliers)
    {
        TerrainDensitySettings resolvedSettings = densitySettings.Sanitized();
        BiomeDensityMultipliers safeMultipliers = densityMultipliers.Sanitized();

        resolvedSettings.solidThreshold *= safeMultipliers.solidThresholdMultiplier;
        resolvedSettings.surfaceSearchHeight *= safeMultipliers.surfaceSearchHeightMultiplier;
        resolvedSettings.baseSolidBias *= safeMultipliers.baseSolidBiasMultiplier;

        return resolvedSettings.Sanitized();
    }
}
