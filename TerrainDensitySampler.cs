using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

public static class TerrainDensitySampler
{
    private const float DetailOffsetX = 37.41f;
    private const float DetailOffsetY = 11.73f;
    private const float DetailOffsetZ = -29.58f;
    private const float OverhangOffsetX = -143.17f;
    private const float OverhangOffsetY = 53.21f;
    private const float OverhangOffsetZ = 87.61f;

    [BurstCompile]
    public static TerrainDensitySettings ResolveBiomeDensitySettings(
        int worldX,
        int worldZ,
        in TerrainDensitySettings densitySettings,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        if (!densitySettings.debugEnableBiomeDensityMultipliers)
            return densitySettings.Sanitized();

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
        if (!densitySettings.debugEnableBiomeDensityMultipliers)
            return densitySettings.Sanitized();

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
        float baseDensity = GetBaseDensity(worldY, baseSurfaceHeight, densitySettings);
        if (!densitySettings.enabled)
            return baseDensity;

        float sampleX = worldX + offsetX;
        float sampleY = worldY;
        float sampleZ = worldZ + offsetZ;

        float detailMask = GetDetailMask(worldY, baseSurfaceHeight, densitySettings);
        float detailNoise = 0f;
        if (detailMask > 1e-5f && densitySettings.detailAmplitude > 1e-5f)
        {
            detailNoise = SampleFractalSimplex3D(
                sampleX + DetailOffsetX,
                sampleY + DetailOffsetY,
                sampleZ + DetailOffsetZ,
                densitySettings.detailScale,
                densitySettings.detailVerticalScale,
                densitySettings.detailOctaves,
                densitySettings.detailPersistence,
                densitySettings.detailLacunarity);
        }

        float overhangMask = GetOverhangMask(worldY, baseSurfaceHeight, densitySettings);
        float overhangNoise = 0f;
        if (overhangMask > 1e-5f && densitySettings.overhangAmplitude > 1e-5f)
        {
            overhangNoise = SampleRidgedSimplex3D(
                sampleX + OverhangOffsetX,
                sampleY + OverhangOffsetY,
                sampleZ + OverhangOffsetZ,
                densitySettings.overhangScale,
                densitySettings.overhangVerticalScale,
                densitySettings.overhangOctaves,
                densitySettings.overhangPersistence,
                densitySettings.overhangLacunarity);
        }

        float detailDensity = detailNoise * densitySettings.detailAmplitude * detailMask;
        float overhangDensity = ((overhangNoise - densitySettings.overhangThreshold) * 2f) * densitySettings.overhangAmplitude * overhangMask;
        return baseDensity + detailDensity + overhangDensity;
    }

    [BurstCompile]
    public static TerrainDensityClassification ClassifyDensityWithoutNoise(
        int worldY,
        int baseSurfaceHeight,
        in TerrainDensitySettings densitySettings)
    {
        float baseDensity = GetBaseDensity(worldY, baseSurfaceHeight, densitySettings);
        if (!densitySettings.enabled)
            return baseDensity > densitySettings.solidThreshold ? TerrainDensityClassification.Solid : TerrainDensityClassification.Air;

        float detailMask = GetDetailMask(worldY, baseSurfaceHeight, densitySettings);
        float overhangMask = GetOverhangMask(worldY, baseSurfaceHeight, densitySettings);

        float maxDensity = baseDensity;
        if (detailMask > 1e-5f && densitySettings.detailAmplitude > 1e-5f)
            maxDensity += densitySettings.detailAmplitude * detailMask;
        if (overhangMask > 1e-5f && densitySettings.overhangAmplitude > 1e-5f)
            maxDensity += GetMaxOverhangDensityContribution(overhangMask, densitySettings);
        if (maxDensity <= densitySettings.solidThreshold)
            return TerrainDensityClassification.Air;

        float minDensity = baseDensity;
        if (detailMask > 1e-5f && densitySettings.detailAmplitude > 1e-5f)
            minDensity -= densitySettings.detailAmplitude * detailMask;
        if (overhangMask > 1e-5f && densitySettings.overhangAmplitude > 1e-5f)
            minDensity += GetMinOverhangDensityContribution(overhangMask, densitySettings);
        if (minDensity > densitySettings.solidThreshold)
            return TerrainDensityClassification.Solid;

        return TerrainDensityClassification.RequiresExactSample;
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
        TerrainDensityClassification classification = ClassifyDensityWithoutNoise(worldY, baseSurfaceHeight, densitySettings);
        if (classification == TerrainDensityClassification.Solid)
            return true;
        if (classification == TerrainDensityClassification.Air)
            return false;

        return SampleTerrainDensity(worldX, worldY, worldZ, baseSurfaceHeight, offsetX, offsetZ, densitySettings) > densitySettings.solidThreshold;
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

        float rise = math.max(
            densitySettings.surfaceSearchHeight,
            math.max(densitySettings.detailBandHeight, densitySettings.overhangBandHeight)
                + densitySettings.overhangAmplitude
                + math.max(4f, densitySettings.baseSolidBias + densitySettings.overhangBelowSurfaceAllowance * 0.25f));
        return math.max(4, (int)math.ceil(rise));
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
            - densitySettings.solidThreshold
            - densitySettings.detailAmplitude
            - densitySettings.overhangAmplitude;

        return math.max(3, (int)math.floor(guaranteedSolidY));
    }

    [BurstCompile]
    private static float GetBaseDensity(int worldY, int baseSurfaceHeight, in TerrainDensitySettings densitySettings)
    {
        return (baseSurfaceHeight + densitySettings.baseSolidBias) - worldY;
    }

    [BurstCompile]
    private static float GetDetailMask(int worldY, int baseSurfaceHeight, in TerrainDensitySettings densitySettings)
    {
        return math.saturate(1f - math.abs(worldY - baseSurfaceHeight) / densitySettings.detailBandHeight);
    }

    [BurstCompile]
    private static float GetOverhangMask(int worldY, int baseSurfaceHeight, in TerrainDensitySettings densitySettings)
    {
        float overhangBaseY = baseSurfaceHeight - densitySettings.overhangBelowSurfaceAllowance;
        float overhangBelowMask = densitySettings.overhangBelowSurfaceAllowance > 0f
            ? math.saturate((worldY - overhangBaseY) / densitySettings.overhangBelowSurfaceAllowance)
            : 1f;
        float overhangAboveMask = math.saturate(1f - math.max(0f, worldY - baseSurfaceHeight) / densitySettings.overhangBandHeight);
        return overhangBelowMask * overhangAboveMask;
    }

    [BurstCompile]
    private static float GetMaxOverhangDensityContribution(float overhangMask, in TerrainDensitySettings densitySettings)
    {
        return ((1f - densitySettings.overhangThreshold) * 2f) * densitySettings.overhangAmplitude * overhangMask;
    }

    [BurstCompile]
    private static float GetMinOverhangDensityContribution(float overhangMask, in TerrainDensitySettings densitySettings)
    {
        return ((0f - densitySettings.overhangThreshold) * 2f) * densitySettings.overhangAmplitude * overhangMask;
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
        resolvedSettings.detailScale *= safeMultipliers.detailScaleMultiplier;
        resolvedSettings.detailAmplitude *= safeMultipliers.detailAmplitudeMultiplier;
        resolvedSettings.detailPersistence *= safeMultipliers.detailPersistenceMultiplier;
        resolvedSettings.detailLacunarity *= safeMultipliers.detailLacunarityMultiplier;
        resolvedSettings.detailVerticalScale *= safeMultipliers.detailVerticalScaleMultiplier;
        resolvedSettings.detailBandHeight *= safeMultipliers.detailBandHeightMultiplier;
        resolvedSettings.overhangScale *= safeMultipliers.overhangScaleMultiplier;
        resolvedSettings.overhangAmplitude *= safeMultipliers.overhangAmplitudeMultiplier;
        resolvedSettings.overhangPersistence *= safeMultipliers.overhangPersistenceMultiplier;
        resolvedSettings.overhangLacunarity *= safeMultipliers.overhangLacunarityMultiplier;
        resolvedSettings.overhangVerticalScale *= safeMultipliers.overhangVerticalScaleMultiplier;
        resolvedSettings.overhangBandHeight *= safeMultipliers.overhangBandHeightMultiplier;
        resolvedSettings.overhangBelowSurfaceAllowance *= safeMultipliers.overhangBelowSurfaceAllowanceMultiplier;
        resolvedSettings.overhangThreshold *= safeMultipliers.overhangThresholdMultiplier;

        return resolvedSettings.Sanitized();
    }

    [BurstCompile]
    private static float SampleFractalSimplex3D(
        float x,
        float y,
        float z,
        float scale,
        float verticalScale,
        int octaves,
        float persistence,
        float lacunarity)
    {
        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float amplitudeSum = 0f;
        float safeScale = math.max(0.001f, scale);
        float safeVerticalScale = math.max(0.01f, verticalScale);
        int safeOctaves = math.max(1, octaves);

        for (int i = 0; i < safeOctaves; i++)
        {
            float3 samplePoint = new float3(
                (x * frequency) / safeScale,
                (y * frequency) / (safeScale * safeVerticalScale),
                (z * frequency) / safeScale);

            total += noise.snoise(samplePoint) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= math.clamp(persistence, 0f, 1f);
            frequency *= math.max(1f, lacunarity);
        }

        if (amplitudeSum <= 1e-5f)
            return 0f;

        return math.clamp(total / amplitudeSum, -1f, 1f);
    }

    [BurstCompile]
    private static float SampleRidgedSimplex3D(
        float x,
        float y,
        float z,
        float scale,
        float verticalScale,
        int octaves,
        float persistence,
        float lacunarity)
    {
        float simplex = SampleFractalSimplex3D(x, y, z, scale, verticalScale, octaves, persistence, lacunarity);
        return 1f - math.abs(simplex);
    }
}
