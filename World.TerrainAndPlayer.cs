using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    #region Helpers: Padding, GetBlock, Procedural Fallback


    public NativeArray<byte> GetPaddedVoxelData(int chunkX, int chunkZ)
    {
        int sizeX = Chunk.SizeX;
        int sizeY = Chunk.SizeY;
        int sizeZ = Chunk.SizeZ;
        int border = GetMaxTreeCanopyRadiusForGeneration() + 1;
        int padX = sizeX + border;
        int padZ = sizeZ + border;

        NativeArray<byte> paddedData = new NativeArray<byte>(padX * sizeY * padZ, Allocator.TempJob);

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

                if (activeChunks.TryGetValue(new Vector2Int(currentCX, currentCZ), out Chunk c))
                {
                    if (c.hasVoxelData)
                    {
                        for (int y = 0; y < sizeY; y++)
                        {
                            int srcIdx = readX + readZ * sizeX + y * sizeX * sizeZ;
                            int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                            paddedData[dstIdx] = c.voxelData[srcIdx];
                        }
                        continue;
                    }
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

        if (worldPos.y < 0) return BlockType.Air;
        if (worldPos.y >= Chunk.SizeY) return BlockType.Air;
        if (worldPos.y <= 2) return BlockType.Bedrock;

        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.hasVoxelData)
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

        TerrainColumnContext columnContext = TerrainColumnSampler.SampleFromNoise(
            worldX,
            worldZ,
            noiseLayers,
            warpLayers,
            baseHeight,
            offsetX,
            offsetZ,
            Chunk.SizeY,
            CliffTreshold,
            seaLevel,
            GetBiomeNoiseSettings());

        if (worldPos.y > columnContext.surfaceHeight)
            return (worldPos.y <= seaLevel) ? BlockType.Water : BlockType.Air;

        return TerrainSurfaceRules.GetBlockTypeAtHeight(worldPos.y, columnContext.surface);
    }

    #endregion

    #region Noise & Height Helpers
    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        return TerrainHeightSampler.SampleSurfaceHeight(
            worldX,
            worldZ,
            noiseLayers,
            warpLayers,
            baseHeight,
            offsetX,
            offsetZ,
            Chunk.SizeY,
            GetBiomeNoiseSettings());
    }







    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
        return q;
    }


    #endregion

    #region Lighting System (Global BFS)

    private bool IsChunkLoaded(Vector2Int coord)
    {
        return activeChunks.TryGetValue(coord, out Chunk chunk) && chunk.hasVoxelData;
    }


    #endregion

    #region Player Actions

    public bool IsGrassBillboardSuppressed(Vector3Int billboardPos)
    {
        return suppressedGrassBillboards.Contains(billboardPos);
    }

    public void SuppressGrassBillboardAt(Vector3Int billboardPos)
    {
        if (billboardPos.y <= 0) return;
        if (!suppressedGrassBillboards.Add(billboardPos)) return;
        IndexSuppressedGrassBillboard(billboardPos);

        Vector2Int coord = GetChunkCoordFromWorldXZ(billboardPos.x, billboardPos.z);

        RequestChunkRebuild(coord);
    }


    public void SetBlockAt(Vector3Int worldPos, BlockType type)
    {
        if (worldPos.y <= 2)
        {
            Debug.Log("Tentativa de modificar Bedrock/abaixo ignorada: " + worldPos);
            return;
        }

        BlockType current = GetBlockAt(worldPos);
        if (current == BlockType.Bedrock)
        {
            Debug.Log("Tentativa de modificar Bedrock ignorada: " + worldPos);
            return;
        }

        if (current == type) return;

        // If this position gets occupied, it cannot host a billboard anymore.
        if (type != BlockType.Air)
            RemoveSuppressedGrassBillboard(worldPos);

        // If ground changes from grass to anything else, clear suppression above it.
        if (type != BlockType.Grass)
            RemoveSuppressedGrassBillboard(new Vector3Int(worldPos.x, worldPos.y + 1, worldPos.z));

        // Keep explicit Air overrides so broken procedural terrain stays removed.
        // Removing the key would make GetBlockAt() fall back to procedural data again.
        blockOverrides[worldPos] = type;

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, type);

        HashSet<Vector2Int> chunksToRebuild = new HashSet<Vector2Int>();
        chunksToRebuild.Add(chunkCoord);

        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);

        if (localX == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.left);
        if (localX == Chunk.SizeX - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.right);
        if (localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.down);
        if (localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.up);

        // Fora da altura simulada por chunk, mantemos apenas override:
        // evita custo de light propagation/rebuild de terrain data que nao cobre esse Y.
        if (worldPos.y >= Chunk.SizeY)
        {
            IndexHighOverride(worldPos, chunkCoord, type);

            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);

            if (worldPos.y == Chunk.SizeY || worldPos.y == Chunk.SizeY + 1)
            {
                foreach (Vector2Int coord in chunksToRebuild)
                    RequestChunkRebuild(coord);
            }
            return;
        }

        byte newEmission = GetBlockEmission(type);
        byte oldEmission = GetBlockEmission(current);
        byte newOpacity = GetBlockOpacity(type);
        byte oldOpacity = GetBlockOpacity(current);

        if (newEmission > 0)
        {
            PropagateLightGlobal(worldPos, newEmission);
        }
        else if (oldEmission > 0)
        {
            RemoveLightGlobal(worldPos);
        }
        else
        {
            bool becameOpaque = oldOpacity < 15 && newOpacity >= 15;
            bool becameTransparent = oldOpacity >= 15 && newOpacity < 15;

            if (becameOpaque)
            {
                RemoveLightGlobal(worldPos);
            }
            else if (becameTransparent)
            {
                RefillLightGlobal(worldPos);
            }
        }

        foreach (Vector2Int coord in chunksToRebuild)
        {
            RequestChunkRebuild(coord);
        }

        // Mudanca no topo do chunk pode expor/ocultar a face inferior de construcoes altas.
        if (worldPos.y >= Chunk.SizeY - 1)
        {
            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);
        }
    }

    private void ApplyBlockToLoadedChunkCache(Vector3Int worldPos, Vector2Int chunkCoord, BlockType type)
    {
        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk)) return;
        if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated) return;

        int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        int ly = worldPos.y;

        if (lx < 0 || lx >= Chunk.SizeX || lz < 0 || lz >= Chunk.SizeZ || ly < 0 || ly >= Chunk.SizeY)
            return;

        int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
        chunk.voxelData[idx] = (byte)type;
    }

    #endregion

}










