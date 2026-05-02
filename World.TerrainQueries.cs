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
            return ResolveWaterStateForDebug(overridden);

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
                return ResolveWaterStateForDebug((BlockType)chunk.voxelData[idx]);
            }
        }

        return GetProceduralBlockFast(worldPos);
    }

    public bool TryGetLoadedBlockAt(Vector3Int worldPos, out BlockType blockType)
    {
        if (blockOverrides.TryGetValue(worldPos, out BlockType overridden))
        {
            blockType = ResolveWaterStateForDebug(overridden);
            return true;
        }

        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
        {
            blockType = BlockType.Air;
            return true;
        }

        if (worldPos.y <= 2)
        {
            blockType = BlockType.Bedrock;
            return true;
        }

        Vector2Int chunkCoord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && CanChunkProvideVoxelSnapshot(chunk))
        {
            int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
            int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
            int ly = worldPos.y;

            if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
            {
                int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
                blockType = ResolveWaterStateForDebug((BlockType)chunk.voxelData[idx]);
                return true;
            }
        }

        blockType = BlockType.Air;
        return false;
    }

    public bool IsWorldColumnLoaded(int worldX, int worldZ)
    {
        return IsChunkLoaded(GetChunkCoordFromWorldXZ(worldX, worldZ));
    }

    private BlockType GetProceduralBlockFast(Vector3Int worldPos)
    {
        int worldX = worldPos.x;
        int worldZ = worldPos.z;
        BiomeNoiseSettings biomeSettings = GetBiomeNoiseSettings();
        TerrainDensitySettings densitySettings = GetTerrainDensitySettings();

        if (IsFlatWorldMode())
        {
            TerrainColumnContext flatColumnContext = CreateFlatColumnContext(worldX, worldZ);
            if (worldPos.y > flatColumnContext.surfaceHeight)
                return BlockType.Air;

            return FlatWorldUtility.GetBlockTypeAtHeight(worldPos.y, flatColumnContext.surfaceHeight);
        }

        if (densitySettings.enabled)
        {
            int baseSurfaceHeight = TerrainHeightSampler.SampleSurfaceHeight(
                worldX, worldZ, noiseLayers, baseHeight, offsetX, offsetZ, Chunk.SizeY, biomeSettings);

            TerrainDensitySettings resolvedDensitySettings = TerrainDensitySampler.ResolveBiomeDensitySettings(
                worldX, worldZ, densitySettings, biomeSettings);

            int guaranteedSolidY = TerrainDensitySampler.GetGuaranteedSolidY(baseSurfaceHeight, resolvedDensitySettings);
            int densityTopY = TerrainDensitySampler.GetDensityBandTopY(baseSurfaceHeight, Chunk.SizeY, resolvedDensitySettings);

            if (worldPos.y > densityTopY)
                return GetProceduralSeaBlockOrAir(worldPos.y);

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
                return GetProceduralSeaBlockOrAir(worldPos.y);
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
            return GetProceduralSeaBlockOrAir(worldPos.y);

        return TerrainSurfaceRules.GetBlockTypeAtHeight(worldPos.y, columnContext.surface);
    }

    private TerrainColumnContext CreateFlatColumnContext(int worldX, int worldZ)
    {
        return FlatWorldUtility.CreateColumnContext(worldX, worldZ, GetResolvedFlatWorldHeight(), Chunk.SizeY);
    }

    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        if (IsFlatWorldMode())
            return GetResolvedFlatWorldHeight();

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
