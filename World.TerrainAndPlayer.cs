using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    #region Helpers: Padding, GetBlock, Procedural Fallback

    [Header("Leaf Decay")]
    [SerializeField] private bool enableLeafDecay = true;
    [SerializeField, Min(1)] private int leafDecaySupportDistance = 4;
    [SerializeField, Min(1)] private int leafDecayChecksPerFrame = 8;
    [SerializeField, Min(0.05f)] private float leafDecayStepInterval = 0.15f;
    [SerializeField, Min(0f)] private float leafDecayGraceSeconds = 1.2f;

    private readonly Queue<LeafDecayCandidate> queuedLeafDecay = new Queue<LeafDecayCandidate>();
    private readonly HashSet<Vector3Int> queuedLeafDecaySet = new HashSet<Vector3Int>();
    private readonly Dictionary<Vector3Int, float> leafDecayUnsupportedSince = new Dictionary<Vector3Int, float>();
    private readonly HashSet<Vector3Int> persistentLeafBlocks = new HashSet<Vector3Int>();
    private readonly Queue<LeafSupportSearchNode> leafSupportSearchQueue = new Queue<LeafSupportSearchNode>();
    private readonly HashSet<Vector3Int> leafSupportVisited = new HashSet<Vector3Int>();

    private static readonly Vector3Int[] LeafDecayNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0, -1)
    };

    private struct LeafDecayCandidate
    {
        public Vector3Int position;
        public float nextCheckTime;
    }

    private struct LeafSupportSearchNode
    {
        public Vector3Int position;
        public int distance;
    }


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


    public void SetBlockAt(Vector3Int worldPos, BlockType type, bool placedByPlayer = false)
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
        HandleLeafDecayBlockChange(worldPos, current, type, placedByPlayer);

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

    private void ProcessQueuedLeafDecay()
    {
        if (!enableLeafDecay || queuedLeafDecay.Count == 0)
            return;

        float now = Time.time;
        int processed = 0;
        int attempts = queuedLeafDecay.Count;

        while (processed < Mathf.Max(1, leafDecayChecksPerFrame) && attempts-- > 0)
        {
            LeafDecayCandidate candidate = queuedLeafDecay.Dequeue();
            queuedLeafDecaySet.Remove(candidate.position);

            if (candidate.nextCheckTime > now)
            {
                queuedLeafDecay.Enqueue(candidate);
                queuedLeafDecaySet.Add(candidate.position);
                continue;
            }

            EvaluateLeafDecay(candidate.position, now);
            processed++;
        }
    }

    private void EvaluateLeafDecay(Vector3Int worldPos, float now)
    {
        BlockType blockType = GetBlockAt(worldPos);
        if (blockType != BlockType.Leaves)
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            persistentLeafBlocks.Remove(worldPos);
            return;
        }

        if (persistentLeafBlocks.Contains(worldPos))
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            return;
        }

        if (HasLeafSupport(worldPos))
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            return;
        }

        if (!leafDecayUnsupportedSince.TryGetValue(worldPos, out float unsupportedSince))
        {
            leafDecayUnsupportedSince[worldPos] = now;
            TryQueueLeafDecay(worldPos, leafDecayStepInterval);
            return;
        }

        if (now - unsupportedSince < leafDecayGraceSeconds)
        {
            TryQueueLeafDecay(worldPos, leafDecayStepInterval);
            return;
        }

        SetBlockAt(worldPos, BlockType.Air);
    }

    private void HandleLeafDecayBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType, bool placedByPlayer)
    {
        if (!enableLeafDecay)
            return;

        bool wasLeaf = previousType == BlockType.Leaves;
        bool isLeaf = newType == BlockType.Leaves;
        bool wasLeafSupport = IsLeafSupportBlock(previousType);
        bool isLeafSupport = IsLeafSupportBlock(newType);

        if (isLeaf && placedByPlayer)
            persistentLeafBlocks.Add(worldPos);
        else if (!isLeaf || !placedByPlayer)
            persistentLeafBlocks.Remove(worldPos);

        if (!isLeaf)
            leafDecayUnsupportedSince.Remove(worldPos);

        if (wasLeafSupport != isLeafSupport)
            QueueNearbyLeavesForDecay(worldPos, Mathf.Max(1, leafDecaySupportDistance));

        if (wasLeaf != isLeaf)
        {
            QueueAdjacentLeavesForDecay(worldPos);

            if (isLeaf)
                TryQueueLeafDecay(worldPos, leafDecayStepInterval);
        }
    }

    private void QueueAdjacentLeavesForDecay(Vector3Int center)
    {
        for (int i = 0; i < LeafDecayNeighborOffsets.Length; i++)
        {
            Vector3Int neighborPos = center + LeafDecayNeighborOffsets[i];
            if (GetBlockAt(neighborPos) == BlockType.Leaves)
                TryQueueLeafDecay(neighborPos, leafDecayStepInterval);
        }
    }

    private void QueueNearbyLeavesForDecay(Vector3Int center, int radius)
    {
        int clampedRadius = Mathf.Max(1, radius);

        for (int dy = -clampedRadius; dy <= clampedRadius; dy++)
        {
            for (int dz = -clampedRadius; dz <= clampedRadius; dz++)
            {
                for (int dx = -clampedRadius; dx <= clampedRadius; dx++)
                {
                    Vector3Int pos = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                    if (GetBlockAt(pos) == BlockType.Leaves)
                        TryQueueLeafDecay(pos, leafDecayStepInterval);
                }
            }
        }
    }

    private void TryQueueLeafDecay(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableLeafDecay || !queuedLeafDecaySet.Add(worldPos))
            return;

        queuedLeafDecay.Enqueue(new LeafDecayCandidate
        {
            position = worldPos,
            nextCheckTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private bool HasLeafSupport(Vector3Int origin)
    {
        int maxDistance = Mathf.Max(1, leafDecaySupportDistance);

        leafSupportSearchQueue.Clear();
        leafSupportVisited.Clear();
        leafSupportVisited.Add(origin);
        leafSupportSearchQueue.Enqueue(new LeafSupportSearchNode
        {
            position = origin,
            distance = 0
        });

        while (leafSupportSearchQueue.Count > 0)
        {
            LeafSupportSearchNode current = leafSupportSearchQueue.Dequeue();
            if (current.distance >= maxDistance)
                continue;

            int nextDistance = current.distance + 1;
            for (int i = 0; i < LeafDecayNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current.position + LeafDecayNeighborOffsets[i];
                if (!leafSupportVisited.Add(neighborPos))
                    continue;

                BlockType neighborType = GetBlockAt(neighborPos);
                if (IsLeafSupportBlock(neighborType))
                    return true;

                if (neighborType == BlockType.Leaves)
                {
                    leafSupportSearchQueue.Enqueue(new LeafSupportSearchNode
                    {
                        position = neighborPos,
                        distance = nextDistance
                    });
                }
            }
        }

        return false;
    }

    private static bool IsLeafSupportBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log;
    }

    #endregion

}










