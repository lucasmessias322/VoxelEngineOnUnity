using Unity.Burst;
using Unity.Collections;

public struct TerrainColumnContext
{
    public int worldX;
    public int worldZ;
    public int surfaceHeight;
    public int northHeight;
    public int southHeight;
    public int eastHeight;
    public int westHeight;
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
        int cliffThreshold,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        bool isCliff = TerrainSurfaceRules.IsCliffFromNeighborHeights(
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            cliffThreshold);

        TerrainSurfaceData surfaceData = TerrainSurfaceRules.EvaluateColumnSurface(
            worldX,
            worldZ,
            surfaceHeight,
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
            surface = surfaceData
        };
    }

    [BurstCompile]
    public static TerrainColumnContext SampleFromNoise(
        int worldX,
        int worldZ,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        int surfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int northHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int southHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int eastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int westHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);

        return CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            cliffThreshold,
            baseHeight,
            seaLevel,
            biomeNoiseSettings);
    }

    public static TerrainColumnContext SampleFromNoise(
        int worldX,
        int worldZ,
        NoiseLayer[] noiseLayers,
        WarpLayer[] warpLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        int worldHeight,
        int cliffThreshold,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        int surfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int northHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ + 1, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int southHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX, worldZ - 1, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int eastHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX + 1, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);
        int westHeight = TerrainHeightSampler.SampleSurfaceHeight(worldX - 1, worldZ, noiseLayers, warpLayers, baseHeight, offsetX, offsetZ, worldHeight);

        return CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
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

        int centerIdx = cacheX + cacheZ * heightStride;
        int surfaceHeight = heightCache[centerIdx];
        int northHeight = cacheZ + 1 < heightDepth ? heightCache[centerIdx + heightStride] : surfaceHeight;
        int southHeight = cacheZ > 0 ? heightCache[centerIdx - heightStride] : surfaceHeight;
        int eastHeight = cacheX + 1 < heightStride ? heightCache[centerIdx + 1] : surfaceHeight;
        int westHeight = cacheX > 0 ? heightCache[centerIdx - 1] : surfaceHeight;

        columnContext = CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            northHeight,
            southHeight,
            eastHeight,
            westHeight,
            cliffThreshold,
            baseHeight,
            seaLevel,
            biomeNoiseSettings);
        return true;
    }
}
