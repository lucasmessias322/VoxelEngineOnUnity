using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

public struct TerrainNoiseSampleState
{
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
    private const float WarpAxisSampleOffset = 100f;
    private const float LegacyFallbackScale = 0.05f;

    [BurstCompile]
    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, warpLayers, offsetX, offsetZ, biomeNoiseSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    [BurstCompile]
    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeTerrainSettings terrainSettings)
    {
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, warpLayers, offsetX, offsetZ, terrainSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        WarpLayer[] warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, warpLayers, offsetX, offsetZ, biomeNoiseSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    public static int SampleSurfaceHeight(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        WarpLayer[] warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        in BiomeTerrainSettings terrainSettings)
    {
        float terrainSignal = SampleTerrainSignal(worldX, worldZ, noiseLayers, warpLayers, offsetX, offsetZ, terrainSettings);
        return GetHeightFromTerrainSignal(terrainSignal, baseHeight, worldHeight);
    }

    [BurstCompile]
    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        ComputeWarpOffset(worldX, worldZ, warpLayers, out float warpX, out float warpZ);
        TerrainNoiseSampleState sampleState = default;

        for (int i = 0; i < noiseLayers.Length; i++)
            SampleNoiseLayer(worldX, worldZ, warpX, warpZ, noiseLayers[i], ref sampleState);

        return FinalizeTerrainSignal(worldX, worldZ, warpX, warpZ, offsetX, offsetZ, biomeNoiseSettings, ref sampleState);
    }

    [BurstCompile]
    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        float offsetX,
        float offsetZ,
        in BiomeTerrainSettings terrainSettings)
    {
        ComputeWarpOffset(worldX, worldZ, warpLayers, out float warpX, out float warpZ);
        TerrainNoiseSampleState sampleState = default;

        for (int i = 0; i < noiseLayers.Length; i++)
            SampleNoiseLayer(worldX, worldZ, warpX, warpZ, noiseLayers[i], ref sampleState);

        return FinalizeTerrainSignal(worldX, worldZ, warpX, warpZ, offsetX, offsetZ, terrainSettings, ref sampleState);
    }

    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        WarpLayer[] warpLayers,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        ComputeWarpOffset(worldX, worldZ, warpLayers, out float warpX, out float warpZ);
        TerrainNoiseSampleState sampleState = default;

        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
                SampleNoiseLayer(worldX, worldZ, warpX, warpZ, noiseLayers[i], ref sampleState);
        }

        return FinalizeTerrainSignal(worldX, worldZ, warpX, warpZ, offsetX, offsetZ, biomeNoiseSettings, ref sampleState);
    }

    public static float SampleTerrainSignal(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        WarpLayer[] warpLayers,
        float offsetX,
        float offsetZ,
        in BiomeTerrainSettings terrainSettings)
    {
        ComputeWarpOffset(worldX, worldZ, warpLayers, out float warpX, out float warpZ);
        TerrainNoiseSampleState sampleState = default;

        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
                SampleNoiseLayer(worldX, worldZ, warpX, warpZ, noiseLayers[i], ref sampleState);
        }

        return FinalizeTerrainSignal(worldX, worldZ, warpX, warpZ, offsetX, offsetZ, terrainSettings, ref sampleState);
    }

    [BurstCompile]
    public static int GetHeightFromTerrainSignal(float terrainSignal, int baseHeight, int worldHeight)
    {
        return math.clamp(baseHeight + (int)math.floor(terrainSignal), 1, worldHeight - 1);
    }

    [BurstCompile]
    private static void ComputeWarpOffset(
        int worldX,
        int worldZ,
        NativeArray<WarpLayer> warpLayers,
        out float warpX,
        out float warpZ)
    {
        warpX = 0f;
        warpZ = 0f;
        float sumWarpAmp = 0f;

        for (int i = 0; i < warpLayers.Length; i++)
            AccumulateWarpLayer(worldX, worldZ, warpLayers[i], ref warpX, ref warpZ, ref sumWarpAmp);

        ClampWarp(ref warpX, ref warpZ, sumWarpAmp);
    }

    private static void ComputeWarpOffset(
        int worldX,
        int worldZ,
        WarpLayer[] warpLayers,
        out float warpX,
        out float warpZ)
    {
        warpX = 0f;
        warpZ = 0f;
        float sumWarpAmp = 0f;

        if (warpLayers != null)
        {
            for (int i = 0; i < warpLayers.Length; i++)
                AccumulateWarpLayer(worldX, worldZ, warpLayers[i], ref warpX, ref warpZ, ref sumWarpAmp);
        }

        ClampWarp(ref warpX, ref warpZ, sumWarpAmp);
    }

    [BurstCompile]
    private static void AccumulateWarpLayer(
        int worldX,
        int worldZ,
        WarpLayer layer,
        ref float warpX,
        ref float warpZ,
        ref float sumWarpAmp)
    {
        if (!layer.enabled)
            return;

        float baseNx = worldX + layer.offset.x;
        float baseNz = worldZ + layer.offset.y;

        float sampleX = MyNoise.OctavePerlin(baseNx + WarpAxisSampleOffset, baseNz, layer);
        float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + WarpAxisSampleOffset, layer);

        warpX += (sampleX * 2f - 1f) * layer.amplitude;
        warpZ += (sampleZ * 2f - 1f) * layer.amplitude;
        sumWarpAmp += layer.amplitude;
    }

    [BurstCompile]
    private static void ClampWarp(ref float warpX, ref float warpZ, float sumWarpAmp)
    {
        if (sumWarpAmp <= 0f)
            return;

        float maxDisplacement = math.max(1f, sumWarpAmp);
        warpX = math.clamp(warpX, -maxDisplacement, maxDisplacement);
        warpZ = math.clamp(warpZ, -maxDisplacement, maxDisplacement);
    }

    [BurstCompile]
    private static void SampleNoiseLayer(
        int worldX,
        int worldZ,
        float warpX,
        float warpZ,
        NoiseLayer layer,
        ref TerrainNoiseSampleState sampleState)
    {
        if (!layer.enabled)
            return;

        sampleState.hasActiveLayers = true;

        float nx = (worldX + warpX) + layer.offset.x;
        float nz = (worldZ + warpZ) + layer.offset.y;

        float sample = MyNoise.OctavePerlin(nx, nz, layer);
        if (layer.redistributionModifier != 1f || layer.exponent != 1f)
            sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);

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
        float warpX,
        float warpZ,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings,
        ref TerrainNoiseSampleState sampleState)
    {
        BiomeTerrainSettings terrainSettings = BiomeUtility.BlendTerrainSettings(worldX, worldZ, biomeNoiseSettings);
        return FinalizeTerrainSignal(worldX, worldZ, warpX, warpZ, offsetX, offsetZ, terrainSettings, ref sampleState);
    }

    [BurstCompile]
    private static float FinalizeTerrainSignal(
        int worldX,
        int worldZ,
        float warpX,
        float warpZ,
        float offsetX,
        float offsetZ,
        in BiomeTerrainSettings terrainSettings,
        ref TerrainNoiseSampleState sampleState)
    {
        if (!sampleState.hasActiveLayers)
        {
            float nx = (worldX + warpX) * LegacyFallbackScale + offsetX;
            float nz = (worldZ + warpZ) * LegacyFallbackScale + offsetZ;
            sampleState.legacyNoiseTotal = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
            sampleState.legacyNoiseWeight = 1f;
            sampleState.hasTypedRoles = false;
        }

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
                terrainSettings)
            : MyNoise.ShapeLegacyTerrainSignal(
                MyNoise.GetLegacyCenteredNoise(sampleState.legacyNoiseTotal, sampleState.legacyNoiseWeight),
                terrainSettings);
    }
}
