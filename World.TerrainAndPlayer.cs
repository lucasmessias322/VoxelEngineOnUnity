using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
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

        RequestChunkRebuild(coord, GetDirtySubchunkMaskForWorldY(billboardPos.y), false);
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

        EnsureTerrainOverrideIndexBuilt();
        IndexTerrainOverride(worldPos, chunkCoord);

        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, type);
        HandleLeafDecayBlockChange(worldPos, current, type, placedByPlayer);
        HandleWaterBlockChange(worldPos, current, type, placedByPlayer);

        int terrainDirtySubchunkMask = GetDirtySubchunkMaskForWorldY(worldPos.y);
        HashSet<Vector2Int> chunksToRebuild = new HashSet<Vector2Int>();
        chunksToRebuild.Add(chunkCoord);

        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);

        if (localX == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.left);
        if (localX == Chunk.SizeX - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.right);
        if (localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.down);
        if (localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.up);
        if (localX == 0 && localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.left + Vector2Int.down);
        if (localX == 0 && localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.left + Vector2Int.up);
        if (localX == Chunk.SizeX - 1 && localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.right + Vector2Int.down);
        if (localX == Chunk.SizeX - 1 && localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.right + Vector2Int.up);

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
                int topTerrainSubchunkMask = GetDirtySubchunkMaskForWorldY(Chunk.SizeY - 1);
                foreach (Vector2Int coord in chunksToRebuild)
                    RequestChunkRebuild(coord, topTerrainSubchunkMask);
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
            RequestChunkRebuild(coord, terrainDirtySubchunkMask);
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
        if (!CanChunkProvideVoxelSnapshot(chunk)) return;

        int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        int ly = worldPos.y;

        if (lx < 0 || lx >= Chunk.SizeX || lz < 0 || lz >= Chunk.SizeZ || ly < 0 || ly >= Chunk.SizeY)
            return;

        int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
        chunk.voxelData[idx] = (byte)type;
        chunk.hasVoxelSnapshot = true;
    }

    private void ProcessQueuedLeafDecay()
    {
        if (!enableLeafDecay || queuedLeafDecay.Count == 0)
            return;

        float now = Time.time;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
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

            if (!IsWorldPositionInsideSimulationDistance(candidate.position, simulationCenter))
            {
                TryQueueLeafDecay(candidate.position, Mathf.Max(leafDecayStepInterval, 0.5f));
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

    [Header("Fluid Simulation")]
    [SerializeField] private bool enableWaterSimulation = true;
    [SerializeField, Min(1)] private int waterUpdatesPerFrame = 48;
    [SerializeField, Min(0.01f)] private float waterTickInterval = 0.05f;
    [SerializeField, Min(1)] private int waterSlopeSearchDistance = 4;

    private readonly Queue<WaterUpdateCandidate> queuedWaterUpdates = new Queue<WaterUpdateCandidate>();
    private readonly HashSet<Vector3Int> queuedWaterUpdateSet = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> persistentWaterSources = new HashSet<Vector3Int>();

    private static readonly Vector3Int[] WaterNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    private static readonly Vector3Int[] HorizontalWaterOffsets =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.forward,
        Vector3Int.back
    };

    private const int NoWaterSlopeDropCost = 1000;

    private struct WaterUpdateCandidate
    {
        public Vector3Int position;
        public float nextUpdateTime;
    }

    private void ProcessQueuedWaterUpdates()
    {
        if (!enableWaterSimulation || queuedWaterUpdates.Count == 0)
            return;

        float now = Time.time;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int processed = 0;
        int attempts = queuedWaterUpdates.Count;
        int perFrameLimit = Mathf.Max(1, waterUpdatesPerFrame);

        while (processed < perFrameLimit && attempts-- > 0)
        {
            WaterUpdateCandidate candidate = queuedWaterUpdates.Dequeue();
            queuedWaterUpdateSet.Remove(candidate.position);

            if (candidate.nextUpdateTime > now)
            {
                RequeueWaterCandidate(candidate);
                continue;
            }

            if (!IsWorldPositionInsideSimulationDistance(candidate.position, simulationCenter))
            {
                TryQueueWaterUpdate(candidate.position, Mathf.Max(waterTickInterval, 0.25f));
                continue;
            }

            if (EvaluateWaterAt(candidate.position))
                processed++;
        }
    }

    private bool EvaluateWaterAt(Vector3Int worldPos)
    {
        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return false;

        BlockType current = GetBlockAt(worldPos);
        bool currentIsWater = FluidBlockUtility.IsWater(current);
        if (!currentIsWater && !CanWaterOccupy(current))
            return false;

        if (IsFixedWaterSource(worldPos))
            return SetWaterStateIfNeeded(worldPos, current, BlockType.Water);

        if (TryGetWaterStateFromAbove(worldPos, out BlockType fallingState))
            return SetWaterStateIfNeeded(worldPos, current, fallingState);

        if (ShouldBecomeInfiniteWaterSource(worldPos))
            return SetWaterStateIfNeeded(worldPos, current, BlockType.Water);

        if (TryGetHorizontalWaterState(worldPos, out BlockType flowingState))
            return SetWaterStateIfNeeded(worldPos, current, flowingState);

        if (!currentIsWater)
            return false;

        return SetWaterStateIfNeeded(worldPos, current, BlockType.Air);
    }

    private bool TryGetWaterStateFromAbove(Vector3Int worldPos, out BlockType state)
    {
        BlockType above = GetBlockAt(worldPos + Vector3Int.up);
        if (!FluidBlockUtility.IsWater(above))
        {
            state = BlockType.Air;
            return false;
        }

        int distance = FluidBlockUtility.GetWaterDistance(above);
        state = FluidBlockUtility.GetWaterBlockType(distance, true);
        return true;
    }

    private bool TryGetHorizontalWaterState(Vector3Int worldPos, out BlockType state)
    {
        int bestDistance = int.MaxValue;
        bool found = false;

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            Vector3Int sourcePos = worldPos + HorizontalWaterOffsets[i];
            if (!WouldWaterFlowFromTo(sourcePos, worldPos))
                continue;

            BlockType sourceType = GetBlockAt(sourcePos);
            int candidateDistance = FluidBlockUtility.GetWaterDistance(sourceType) + 1;
            if (candidateDistance > FluidBlockUtility.MaxWaterDistance)
                continue;

            if (candidateDistance < bestDistance)
            {
                bestDistance = candidateDistance;
                found = true;
            }
        }

        if (!found)
        {
            state = BlockType.Air;
            return false;
        }

        state = FluidBlockUtility.GetWaterBlockType(bestDistance, false);
        return true;
    }

    private bool WouldWaterFlowFromTo(Vector3Int sourcePos, Vector3Int targetPos)
    {
        BlockType sourceType = GetBlockAt(sourcePos);
        if (!FluidBlockUtility.IsWater(sourceType))
            return false;

        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType) && !FluidBlockUtility.IsWater(targetType))
            return false;

        if (CanWaterFlowDownInto(GetBlockAt(sourcePos + Vector3Int.down)))
            return false;

        int directionIndex = GetHorizontalDirectionIndex(targetPos - sourcePos);
        if (directionIndex < 0)
            return false;

        GetOptimalWaterFlowCosts(sourcePos, out int rightCost, out int leftCost, out int forwardCost, out int backCost, out int bestCost);
        if (bestCost == int.MaxValue)
            return false;

        int directionCost = directionIndex switch
        {
            0 => rightCost,
            1 => leftCost,
            2 => forwardCost,
            3 => backCost,
            _ => int.MaxValue
        };

        return directionCost == bestCost;
    }

    private void GetOptimalWaterFlowCosts(
        Vector3Int sourcePos,
        out int rightCost,
        out int leftCost,
        out int forwardCost,
        out int backCost,
        out int bestCost)
    {
        rightCost = GetFlowCostForDirection(sourcePos, 0);
        leftCost = GetFlowCostForDirection(sourcePos, 1);
        forwardCost = GetFlowCostForDirection(sourcePos, 2);
        backCost = GetFlowCostForDirection(sourcePos, 3);

        bestCost = Mathf.Min(rightCost, leftCost);
        bestCost = Mathf.Min(bestCost, forwardCost);
        bestCost = Mathf.Min(bestCost, backCost);
    }

    private int GetFlowCostForDirection(Vector3Int sourcePos, int directionIndex)
    {
        Vector3Int targetPos = sourcePos + HorizontalWaterOffsets[directionIndex];
        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType))
            return int.MaxValue;

        if (CanWaterFlowDownInto(GetBlockAt(targetPos + Vector3Int.down)))
            return 0;

        return GetWaterSlopeDistance(targetPos, 1, GetOppositeHorizontalDirection(directionIndex));
    }

    private int GetWaterSlopeDistance(Vector3Int origin, int depth, int blockedDirection)
    {
        int maxDepth = Mathf.Max(1, waterSlopeSearchDistance);
        if (depth >= maxDepth)
            return NoWaterSlopeDropCost;

        int bestDistance = NoWaterSlopeDropCost;

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (i == blockedDirection)
                continue;

            Vector3Int nextPos = origin + HorizontalWaterOffsets[i];
            BlockType nextType = GetBlockAt(nextPos);
            if (!CanWaterOccupy(nextType))
                continue;

            if (CanWaterFlowDownInto(GetBlockAt(nextPos + Vector3Int.down)))
                return depth;

            int candidate = GetWaterSlopeDistance(nextPos, depth + 1, GetOppositeHorizontalDirection(i));
            if (candidate < bestDistance)
                bestDistance = candidate;
        }

        return bestDistance;
    }

    private bool ShouldBecomeInfiniteWaterSource(Vector3Int worldPos)
    {
        BlockType below = GetBlockAt(worldPos + Vector3Int.down);
        bool supportedBelow = IsSolidBlock(below) || IsSourceWaterState(worldPos + Vector3Int.down);
        if (!supportedBelow)
            return false;

        int adjacentSources = 0;
        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (IsSourceWaterState(worldPos + HorizontalWaterOffsets[i]))
            {
                adjacentSources++;
                if (adjacentSources >= 2)
                    return true;
            }
        }

        return false;
    }

    private bool SetWaterStateIfNeeded(Vector3Int worldPos, BlockType current, BlockType desired)
    {
        if (current == desired)
            return false;

        if (desired == BlockType.Air && !FluidBlockUtility.IsWater(current))
            return false;

        SetBlockAt(worldPos, desired);
        return true;
    }

    private bool IsSourceWaterState(Vector3Int worldPos)
    {
        BlockType blockType = GetBlockAt(worldPos);
        return FluidBlockUtility.IsStillWater(blockType) && !FluidBlockUtility.IsFallingWater(blockType);
    }

    private bool IsFixedWaterSource(Vector3Int worldPos)
    {
        if (persistentWaterSources.Contains(worldPos))
            return true;

        if (blockOverrides.ContainsKey(worldPos))
            return false;

        return GetProceduralBlockFast(worldPos) == BlockType.Water;
    }

    private bool CanWaterOccupy(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return true;

        if (TorchPlacementUtility.IsTorchLike(blockType))
            return true;

        if (blockData != null)
        {
            BlockTextureMapping? mapping = blockData.GetMapping(blockType);
            if (mapping != null)
            {
                BlockTextureMapping value = mapping.Value;
                return !value.isSolid && !value.isLiquid;
            }
        }

        return !IsSolidBlock(blockType) && !IsLiquidBlock(blockType);
    }

    private bool CanWaterFlowDownInto(BlockType blockType)
    {
        if (FluidBlockUtility.IsWater(blockType))
            return false;

        return CanWaterOccupy(blockType);
    }

    private static int GetHorizontalDirectionIndex(Vector3Int delta)
    {
        if (delta == Vector3Int.right) return 0;
        if (delta == Vector3Int.left) return 1;
        if (delta == Vector3Int.forward) return 2;
        if (delta == Vector3Int.back) return 3;
        return -1;
    }

    private static int GetOppositeHorizontalDirection(int directionIndex)
    {
        return directionIndex switch
        {
            0 => 1,
            1 => 0,
            2 => 3,
            3 => 2,
            _ => -1
        };
    }

    private void HandleWaterBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType, bool placedByPlayer)
    {
        if (newType != BlockType.Water)
        {
            persistentWaterSources.Remove(worldPos);
        }
        else if (placedByPlayer)
        {
            persistentWaterSources.Add(worldPos);
        }

        if (!enableWaterSimulation)
            return;

        float delay = Mathf.Max(0f, waterTickInterval);
        TryQueueWaterUpdate(worldPos, delay);
        for (int i = 0; i < WaterNeighborOffsets.Length; i++)
            TryQueueWaterUpdate(worldPos + WaterNeighborOffsets[i], delay);
    }

    private void TryQueueWaterUpdate(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableWaterSimulation)
            return;

        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return;

        if (!queuedWaterUpdateSet.Add(worldPos))
            return;

        queuedWaterUpdates.Enqueue(new WaterUpdateCandidate
        {
            position = worldPos,
            nextUpdateTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private void RequeueWaterCandidate(WaterUpdateCandidate candidate)
    {
        if (!queuedWaterUpdateSet.Add(candidate.position))
            return;

        queuedWaterUpdates.Enqueue(candidate);
    }

    #endregion

}










