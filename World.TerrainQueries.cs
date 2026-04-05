using Unity.Collections;
using UnityEngine;

public partial class World : MonoBehaviour
{
    public NativeArray<byte> GetPaddedVoxelData(int chunkX, int chunkZ)
    {
        int sizeX = Chunk.SizeX;
        int sizeY = Chunk.SizeY;
        int sizeZ = Chunk.SizeZ;
        int border = GetMaxTreeCanopyRadiusForGeneration() + 1;
        int padX = sizeX + border;
        int padZ = sizeZ + border;
        NativeArray<byte> paddedData = new NativeArray<byte>(padX * sizeY * padZ, Allocator.Temp);

        for (int z = -border; z < sizeZ + border; z++)
        {
            for (int x = -border; x < sizeX + border; x++)
            {
                int currentCX = chunkX;
                int currentCZ = chunkZ;
                int readX = x;
                int readZ = z;

                if (x < 0) { currentCX--; readX = sizeX - 1; }
                else if (x >= sizeX) { currentCX++; readX = 0; }

                if (z < 0) { currentCZ--; readZ = sizeZ - 1; }
                else if (z >= sizeZ) { currentCZ++; readZ = 0; }

                if (activeChunks.TryGetValue(new Vector2Int(currentCX, currentCZ), out Chunk chunk) &&
                    CanChunkProvideVoxelSnapshot(chunk))
                {
                    for (int y = 0; y < sizeY; y++)
                    {
                        int srcIdx = readX + readZ * sizeX + y * sizeX * sizeZ;
                        int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                        paddedData[dstIdx] = chunk.voxelData[srcIdx];
                    }

                    continue;
                }

                for (int y = 0; y < sizeY; y++)
                {
                    int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                    paddedData[dstIdx] = 0;
                }
            }
        }

        return paddedData;
    }

    public BlockType GetBlockAt(Vector3Int worldPos)
    {
        if (blockOverrides.TryGetValue(worldPos, out BlockType overridden))
            return overridden;

        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return BlockType.Air;
        if (worldPos.y <= 2)
            return BlockType.Bedrock;

        int worldX = worldPos.x;
        int worldZ = worldPos.z;
        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ));

        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && CanChunkProvideVoxelSnapshot(chunk))
        {
            int lx = worldX - chunkCoord.x * Chunk.SizeX;
            int lz = worldZ - chunkCoord.y * Chunk.SizeZ;
            int ly = worldPos.y;

            if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
            {
                int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
                return (BlockType)chunk.voxelData[idx];
            }
        }

        return GetProceduralBlockFast(worldPos);
    }

    private BlockType GetProceduralBlockFast(Vector3Int worldPos)
    {
        int worldX = worldPos.x;
        int worldZ = worldPos.z;
        BiomeNoiseSettings biomeSettings = GetBiomeNoiseSettings();
        TerrainDensitySettings densitySettings = GetTerrainDensitySettings();

        if (densitySettings.enabled)
        {
            int baseSurfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(
                worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, Chunk.SizeY, biomeSettings);

            TerrainDensitySettings resolvedDensitySettings = TerrainDensitySampler.ResolveBiomeDensitySettings(
                worldX, worldZ, densitySettings, biomeSettings);

            int guaranteedSolidY = TerrainDensitySampler.GetGuaranteedSolidY(baseSurfaceHeight, resolvedDensitySettings);
            int densityTopY = TerrainDensitySampler.GetDensityBandTopY(baseSurfaceHeight, Chunk.SizeY, resolvedDensitySettings);

            if (worldPos.y > densityTopY)
                return worldPos.y <= seaLevel ? BlockType.Water : BlockType.Air;

            if (worldPos.y <= guaranteedSolidY)
            {
                TerrainColumnContext solidColumnContext = TerrainDensitySampler.SampleColumnContext(
                    worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ,
                    Chunk.SizeY, CliffTreshold, seaLevel, biomeSettings, densitySettings);

                return TerrainSurfaceRules.GetBlockTypeAtHeight(worldPos.y, solidColumnContext.surface);
            }

            if (!TerrainDensitySampler.IsSolidAt(
                worldX, worldPos.y, worldZ, baseSurfaceHeight, offsetX, offsetZ, resolvedDensitySettings))
            {
                return worldPos.y <= seaLevel ? BlockType.Water : BlockType.Air;
            }

            TerrainColumnContext densityColumnContext = TerrainDensitySampler.SampleColumnContext(
                worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ,
                Chunk.SizeY, CliffTreshold, seaLevel, biomeSettings, densitySettings);

            return TerrainSurfaceRules.GetBlockTypeAtHeight(worldPos.y, densityColumnContext.surface);
        }

        TerrainColumnContext columnContext = TerrainColumnSampler.SampleFromNoise(
            worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ,
            Chunk.SizeY, CliffTreshold, seaLevel, biomeSettings);

        if (worldPos.y > columnContext.surfaceHeight)
            return worldPos.y <= seaLevel ? BlockType.Water : BlockType.Air;

        return TerrainSurfaceRules.GetBlockTypeAtHeight(worldPos.y, columnContext.surface);
    }

    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        return TerrainDensitySampler.SampleSurfaceHeight(
            worldX,
            worldZ,
            noiseLayers,
            baseHeight,
            offsetX,
            offsetZ,
            Chunk.SizeY,
            GetBiomeNoiseSettings(),
            GetTerrainDensitySettings());
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0)))
            q--;

        return q;
    }

    private bool IsChunkLoaded(Vector2Int coord)
    {
        return activeChunks.TryGetValue(coord, out Chunk chunk) && CanChunkProvideVoxelSnapshot(chunk);
    }
}
