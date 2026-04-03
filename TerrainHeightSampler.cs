using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

public struct TerrainNoiseSampleState
{
    // Acumula cada "canal" de ruido separadamente para que o relevo final
    // possa combinar continentalness, erosao, morros e montanhas sem reamostrar.
    public bool hasActiveLayers;
    public bool hasTypedRoles;
    public float legacyNoiseTotal;
    public float legacyNoiseWeight;
    public float continentalTotal;
    public float continentalWeight;
    public float erosionTotal;
    public float erosionWeight;
    public float hillsTotal;
    public float hillsWeight;
    public float peaksValleysTotal;
    public float peaksValleysWeight;
    public float mountainTotal;
    public float mountainWeight;
}

public static class TerrainHeightSampler
{
    private const float LegacyFallbackScale = 0.05f;

    [BurstCompile]
    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        // Etapa 1 do pipeline de terreno: transforma o ruido em uma altura final de superficie.
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, offsetX, offsetZ, biomeNoiseSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        // Etapa 1 do pipeline de terreno: transforma o ruido em uma altura final de superficie.
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, offsetX, offsetZ, biomeNoiseSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    [BurstCompile]
    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        TerrainNoiseSampleState sampleState = default;

        for (int i = 0; i < noiseLayers.Length; i++)
            SampleNoiseLayer(worldX, worldZ, offsetX, offsetZ, noiseLayers[i], ref sampleState);

        return FinalizeTerrainSignal(worldX, worldZ, offsetX, offsetZ, biomeNoiseSettings, ref sampleState);
    }

    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        TerrainNoiseSampleState sampleState = default;

        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
                SampleNoiseLayer(worldX, worldZ, offsetX, offsetZ, noiseLayers[i], ref sampleState);
        }

        return FinalizeTerrainSignal(worldX, worldZ, offsetX, offsetZ, biomeNoiseSettings, ref sampleState);
    }

    [BurstCompile]
    public static int GetHeightFromTerrainSignal(float terrainSignal, int baseHeight, int worldHeight)
    {
        return math.clamp(baseHeight + (int)math.floor(terrainSignal), 1, worldHeight - 1);
    }

    [BurstCompile]
    private static void SampleNoiseLayer(
        int worldX,
        int worldZ,
        float offsetX,
        float offsetZ,
        NoiseLayer layer,
        ref TerrainNoiseSampleState sampleState)
    {
        if (!layer.enabled)
            return;

        sampleState.hasActiveLayers = true;

        // Offsets por camada permitem deslocar canais especificos sem mexer no seed global.
        float nx = (worldX + offsetX) + layer.offset.x;
        float nz = (worldZ + offsetZ) + layer.offset.y;

        float sample = MyNoise.OctavePerlin(nx, nz, layer);
        if (layer.redistributionModifier != 1f || layer.exponent != 1f)
            sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);

        // Layers sem role continuam alimentando o caminho legado; layers tipadas
        // entram no compositor mais moderno inspirado no terrain shaping do Minecraft.
        MyNoise.AccumulateLayerByRole(
            layer,
            sample,
            ref sampleState.hasTypedRoles,
            ref sampleState.legacyNoiseTotal,
            ref sampleState.legacyNoiseWeight,
            ref sampleState.continentalTotal,
            ref sampleState.continentalWeight,
            ref sampleState.erosionTotal,
            ref sampleState.erosionWeight,
            ref sampleState.hillsTotal,
            ref sampleState.hillsWeight,
            ref sampleState.peaksValleysTotal,
            ref sampleState.peaksValleysWeight,
            ref sampleState.mountainTotal,
            ref sampleState.mountainWeight);
    }

    [BurstCompile]
    private static float FinalizeTerrainSignal(
        int worldX,
        int worldZ,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings,
        ref TerrainNoiseSampleState sampleState)
    {
        if (!sampleState.hasActiveLayers)
        {
            // Compatibilidade com worlds/perfis antigos que nao possuem noise layers configuradas.
            float nx = worldX * LegacyFallbackScale + offsetX;
            float nz = worldZ * LegacyFallbackScale + offsetZ;
            sampleState.legacyNoiseTotal = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
            sampleState.legacyNoiseWeight = 1f;
            sampleState.hasTypedRoles = false;
        }

        BiomeTerrainSettings terrainSettings = BiomeUtility.BlendTerrainSettings(worldX, worldZ, biomeNoiseSettings);
        // Se houver canais tipados, usamos o compositor moderno; caso contrario,
        // apenas modelamos o ruido legado com os multiplicadores do bioma atual.
        return sampleState.hasTypedRoles
            ? MyNoise.ComposeMinecraftLikeTerrainSignal(
                sampleState.continentalTotal,
                sampleState.continentalWeight,
                sampleState.erosionTotal,
                sampleState.erosionWeight,
                sampleState.hillsTotal,
                sampleState.hillsWeight,
                sampleState.peaksValleysTotal,
                sampleState.peaksValleysWeight,
                sampleState.mountainTotal,
                sampleState.mountainWeight,
                sampleState.legacyNoiseTotal,
                sampleState.legacyNoiseWeight,
                terrainSettings,
                biomeNoiseSettings.terrainShaper)
            : MyNoise.ShapeLegacyTerrainSignal(
                MyNoise.GetLegacyCenteredNoise(sampleState.legacyNoiseTotal, sampleState.legacyNoiseWeight),
                terrainSettings);
    }
}

[Serializable]
public struct TerrainDensitySettings
{
    public bool enabled;
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
        return settings;
    }
}

public enum TerrainDensityClassification : byte
{
    RequiresExactSample = 0,
    Solid = 1,
    Air = 2
}

public static class TerrainDensitySampler
{
    private const float DetailOffsetX = 37.41f;
    private const float DetailOffsetY = 11.73f;
    private const float DetailOffsetZ = -29.58f;
    private const float OverhangOffsetX = -143.17f;
    private const float OverhangOffsetY = 53.21f;
    private const float OverhangOffsetZ = 87.61f;

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

        return SampleSurfaceHeightFromBaseSurface(
            worldX,
            worldZ,
            baseSurfaceHeight,
            offsetX,
            offsetZ,
            worldHeight,
            densitySettings);
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

        return SampleSurfaceHeightFromBaseSurface(
            worldX,
            worldZ,
            baseSurfaceHeight,
            offsetX,
            offsetZ,
            worldHeight,
            densitySettings);
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
