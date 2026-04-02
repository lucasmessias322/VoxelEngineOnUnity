using Unity.Burst;
using Unity.Collections;

public struct TerrainColumnContext
{
    // Snapshot completo de uma coluna apos olhar a altura central e os 8 vizinhos.
    public int worldX;
    public int worldZ;
    public int surfaceHeight;
    public int northHeight;
    public int southHeight;
    public int eastHeight;
    public int westHeight;
    public int northEastHeight;
    public int northWestHeight;
    public int southEastHeight;
    public int southWestHeight;
    public float slope;
    public float slope01;
    public TerrainSurfaceData surface;
}

public static class TerrainColumnSampler
{
    [BurstCompile]
    public static TerrainColumnContext CreateFromNeighborHeights(
        int worldX,
        int worldZ,
        int surfaceHeight,
        int northHeight,
        int southHeight,
        int eastHeight,
        int westHeight,
        int northEastHeight,
        int northWestHeight,
        int southEastHeight,
        int southWestHeight,
        int cliffThreshold,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        // A altura dos vizinhos vira um contexto semantico: slope, cliff e materiais da superficie.
        float slope = TerrainSurfaceRules.GetSlopeFromNeighborHeights(
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            northEastHeight,
            northWestHeight,
            southEastHeight,
            southWestHeight);
        float slope01 = TerrainSurfaceRules.NormalizeSlope(slope, cliffThreshold);
        bool isCliff = TerrainSurfaceRules.IsSteepSlope(slope, cliffThreshold);

        TerrainSurfaceData surfaceData = TerrainSurfaceRules.EvaluateColumnSurface(
            worldX,
            worldZ,
            surfaceHeight,
            slope,
            slope01,
            isCliff,
            baseHeight,
            seaLevel,
            biomeNoiseSettings);

        return new TerrainColumnContext
        {
            worldX = worldX,
            worldZ = worldZ,
            surfaceHeight = surfaceHeight,
            northHeight = northHeight,
            southHeight = southHeight,
            eastHeight = eastHeight,
            westHeight = westHeight,
            northEastHeight = northEastHeight,
            northWestHeight = northWestHeight,
            southEastHeight = southEastHeight,
            southWestHeight = southWestHeight,
            slope = slope,
            slope01 = slope01,
            surface = surfaceData
        };
    }

    [BurstCompile]
    public static TerrainColumnContext SampleFromNoise(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        int surfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int eastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int westHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northEastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northWestHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southEastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southWestHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);

        return CreateFromNeighborHeights(
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

    public static TerrainColumnContext SampleFromNoise(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        int surfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int eastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int westHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northEastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int northWestHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ + 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southEastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);
        int southWestHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ - 1, noiseLayers, baseHeight, offsetX, offsetZ, worldHeight, biomeNoiseSettings);

        return CreateFromNeighborHeights(
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
    public static bool TryCreateFromHeightCache(
        int worldX,
        int worldZ,
        int chunkCoordX,
        int chunkCoordZ,
        int sizeX,
        int sizeZ,
        int border,
        NativeArray<int> heightCache,
        int cliffThreshold,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings,
        out TerrainColumnContext columnContext)
    {
        int heightStride = sizeX + 2 * border;
        int heightDepth = sizeZ + 2 * border;
        int realLx = worldX - chunkCoordX * sizeX;
        int realLz = worldZ - chunkCoordZ * sizeZ;
        int cacheX = realLx + border;
        int cacheZ = realLz + border;

        if (cacheX < 0 || cacheX >= heightStride || cacheZ < 0 || cacheZ >= heightDepth)
        {
            columnContext = default;
            return false;
        }

        // As bordas do cache nao possuem os 8 vizinhos necessarios para slope/cliff.
        // Nesses casos o caller reamostra por ruido para nao criar costuras entre chunks.
        if (cacheX <= 0 || cacheX >= heightStride - 1 || cacheZ <= 0 || cacheZ >= heightDepth - 1)
        {
            columnContext = default;
            return false;
        }

        int centerIdx = cacheX + cacheZ * heightStride;
        int surfaceHeight = heightCache[centerIdx];
        int northHeight = heightCache[centerIdx + heightStride];
        int southHeight = heightCache[centerIdx - heightStride];
        int eastHeight = heightCache[centerIdx + 1];
        int westHeight = heightCache[centerIdx - 1];
        int northEastHeight = heightCache[centerIdx + 1 + heightStride];
        int northWestHeight = heightCache[centerIdx - 1 + heightStride];
        int southEastHeight = heightCache[centerIdx + 1 - heightStride];
        int southWestHeight = heightCache[centerIdx - 1 - heightStride];

        columnContext = CreateFromNeighborHeights(
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
        return true;
    }
}
