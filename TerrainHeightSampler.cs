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

        float nx = (worldX + offsetX) + layer.offset.x;
        float nz = (worldZ + offsetZ) + layer.offset.y;

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
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings,
        ref TerrainNoiseSampleState sampleState)
    {
        if (!sampleState.hasActiveLayers)
        {
            float nx = worldX * LegacyFallbackScale + offsetX;
            float nz = worldZ * LegacyFallbackScale + offsetZ;
            sampleState.legacyNoiseTotal = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
            sampleState.legacyNoiseWeight = 1f;
            sampleState.hasTypedRoles = false;
        }

        BiomeTerrainSettings terrainSettings = BiomeUtility.BlendTerrainSettings(worldX, worldZ, biomeNoiseSettings);
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
