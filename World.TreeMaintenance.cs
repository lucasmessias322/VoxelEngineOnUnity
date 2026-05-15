using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly Queue<TreeCapitatorBreakCandidate> queuedTreeCapitatorBreaks = new Queue<TreeCapitatorBreakCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedTreeCapitatorSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Queue<Vector3Int> treeCapitatorSearchQueue = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> treeCapitatorVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly List<Vector3Int> treeCapitatorCollectedLogs = new List<Vector3Int>(64);
    private readonly HashSet<Vector3Int> playerPlacedLogs = new HashSet<Vector3Int>(InitialBlockEditCapacity);

    private readonly Queue<LeafDecayCandidate> queuedLeafDecay = new Queue<LeafDecayCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedLeafDecaySet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, float> leafDecayUnsupportedSince = new Dictionary<Vector3Int, float>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> persistentLeafBlocks = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Queue<LeafSupportSearchNode> leafSupportSearchQueue = new Queue<LeafSupportSearchNode>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> leafSupportVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Queue<LeafSupportScanCandidate> queuedLeafSupportScans = new Queue<LeafSupportScanCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedLeafSupportScanSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector2Int, int> leafDecayDirtyChunkMasks = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private float nextLeafDecayQueueWakeTime = float.PositiveInfinity;
    private float nextLeafDecayVisualFlushTime = float.PositiveInfinity;

    private static readonly Vector3Int[] TreeCapitatorNeighborOffsets = CreateTreeCapitatorNeighborOffsets();

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

    private struct TreeCapitatorBreakCandidate
    {
        public Vector3Int position;
        public BlockType expectedType;
        public bool shouldDrop;
        public Vector3 throwDirection;
    }

    private struct LeafSupportSearchNode
    {
        public Vector3Int position;
        public int distance;
    }

    private struct LeafSupportScanCandidate
    {
        public Vector3Int center;
        public int radius;
    }

    #region Tree Maintenance

    public bool TryQueueTreeCapitatorBreak(
        Vector3Int origin,
        BlockType brokenBlockType,
        bool shouldDrop,
        Vector3 throwDirection)
    {
        if (!enableTreeCapitator || !IsTreeLogBlock(brokenBlockType))
            return false;
        if (playerPlacedLogs.Contains(origin))
            return false;

        int connectedLogs = CollectTreeCapitatorLogs(origin, brokenBlockType);
        if (connectedLogs <= 1)
            return false;

        Vector3 normalizedThrowDirection = throwDirection.sqrMagnitude > 0.0001f
            ? throwDirection.normalized
            : Vector3.up;

        int queued = 0;
        for (int i = 0; i < treeCapitatorCollectedLogs.Count; i++)
        {
            Vector3Int logPos = treeCapitatorCollectedLogs[i];
            if (playerPlacedLogs.Contains(logPos))
                continue;

            if (!queuedTreeCapitatorSet.Add(logPos))
                continue;

            queuedTreeCapitatorBreaks.Enqueue(new TreeCapitatorBreakCandidate
            {
                position = logPos,
                expectedType = brokenBlockType,
                shouldDrop = shouldDrop,
                throwDirection = normalizedThrowDirection
            });
            queued++;
        }

        return queued > 0;
    }

    private int CollectTreeCapitatorLogs(Vector3Int origin, BlockType targetLogType)
    {
        treeCapitatorSearchQueue.Clear();
        treeCapitatorVisited.Clear();
        treeCapitatorCollectedLogs.Clear();

        int maxLogs = Mathf.Max(1, treeCapitatorMaxLogsPerTree);
        treeCapitatorSearchQueue.Enqueue(origin);
        treeCapitatorVisited.Add(origin);

        while (treeCapitatorSearchQueue.Count > 0 && treeCapitatorCollectedLogs.Count < maxLogs)
        {
            Vector3Int current = treeCapitatorSearchQueue.Dequeue();
            if (GetBlockAt(current) != targetLogType)
                continue;
            if (playerPlacedLogs.Contains(current))
                continue;

            treeCapitatorCollectedLogs.Add(current);

            for (int i = 0; i < TreeCapitatorNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current + TreeCapitatorNeighborOffsets[i];
                if (neighborPos.y <= 2 || neighborPos.y >= Chunk.SizeY)
                    continue;

                if (!treeCapitatorVisited.Add(neighborPos))
                    continue;

                if (GetBlockAt(neighborPos) != targetLogType)
                    continue;
                if (playerPlacedLogs.Contains(neighborPos))
                    continue;

                treeCapitatorSearchQueue.Enqueue(neighborPos);
            }
        }

        return treeCapitatorCollectedLogs.Count;
    }

    private void ProcessQueuedTreeCapitatorBreaks()
    {
        if (!enableTreeCapitator)
        {
            ClearQueuedTreeCapitatorBreaks();
            return;
        }

        if (queuedTreeCapitatorBreaks.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, treeCapitatorBreaksPerFrame);
        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = treeCapitatorTimeBudgetMS > 0f ? treeCapitatorTimeBudgetMS / 1000f : 0f;
        for (int i = 0; i < perFrameLimit && queuedTreeCapitatorBreaks.Count > 0; i++)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            TreeCapitatorBreakCandidate candidate = queuedTreeCapitatorBreaks.Dequeue();
            queuedTreeCapitatorSet.Remove(candidate.position);

            BlockType currentType = GetBlockAt(candidate.position);
            if (currentType != candidate.expectedType)
                continue;

            bool spawnedDrop = candidate.shouldDrop &&
                               BlockBreakDropResolver.TrySpawnDrop(this, candidate.position, currentType, candidate.throwDirection);
            SetBlockAt(candidate.position, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(candidate.position);

            if (candidate.shouldDrop && !spawnedDrop)
            {
                bool addedToInventory = PlayerInventory.Instance != null &&
                                        BlockBreakDropResolver.TryAddDropToInventory(PlayerInventory.Instance, this, currentType);
                if (!addedToInventory)
                    Debug.LogWarning($"[World] Falha ao gerar drop de {currentType} em {candidate.position}.");
            }
        }
    }

    private void ClearQueuedTreeCapitatorBreaks()
    {
        queuedTreeCapitatorBreaks.Clear();
        queuedTreeCapitatorSet.Clear();
    }

    private void ProcessQueuedLeafDecay()
    {
        if (!enableLeafDecay)
            return;

        float now = Time.time;
        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = leafDecayTimeBudgetMS > 0f ? leafDecayTimeBudgetMS / 1000f : 0f;

        ProcessQueuedLeafSupportScans(stepStartTime, timeBudgetSeconds);

        if (queuedLeafDecay.Count == 0)
        {
            FlushLeafDecayBlockRefreshes(false);
            return;
        }

        if (nextLeafDecayQueueWakeTime > now)
        {
            FlushLeafDecayBlockRefreshes(false);
            return;
        }

        nextLeafDecayQueueWakeTime = float.PositiveInfinity;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int perFrameChecks = Mathf.Max(1, leafDecayChecksPerFrame);
        int maxAttempts = Mathf.Max(perFrameChecks, leafDecayQueueScansPerFrame);
        int processed = 0;
        int attempts = Mathf.Min(queuedLeafDecay.Count, maxAttempts);
        bool hitTimeBudget = false;

        while (processed < perFrameChecks && attempts-- > 0 && queuedLeafDecay.Count > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
            {
                hitTimeBudget = true;
                break;
            }

            LeafDecayCandidate candidate = queuedLeafDecay.Dequeue();
            queuedLeafDecaySet.Remove(candidate.position);

            if (candidate.nextCheckTime > now)
            {
                RequeueLeafDecayCandidate(candidate);
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

        FlushLeafDecayBlockRefreshes(false);

        if (queuedLeafDecay.Count == 0)
            nextLeafDecayQueueWakeTime = float.PositiveInfinity;
        else if (hitTimeBudget || processed >= perFrameChecks)
            nextLeafDecayQueueWakeTime = Mathf.Min(nextLeafDecayQueueWakeTime, now);
        else if (attempts <= 0 && processed == 0)
            nextLeafDecayQueueWakeTime = Mathf.Min(
                nextLeafDecayQueueWakeTime,
                now + Mathf.Max(0.02f, leafDecayStepInterval * 0.25f));
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

        DecayLeafBlock(worldPos);
    }

    private void DecayLeafBlock(Vector3Int worldPos)
    {
        if (SetLeafDecayBlockToAir(worldPos))
            BlockBreakDropResolver.TrySpawnDrop(this, worldPos, BlockType.Leaves, Vector3.zero);
    }

    private bool SetLeafDecayBlockToAir(Vector3Int worldPos)
    {
        const BlockType previousType = BlockType.Leaves;
        const BlockType newType = BlockType.Air;

        if (worldPos.y <= 2 || GetBlockAt(worldPos) != previousType)
            return false;

        Vector2Int chunkCoord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        blockOverrides[worldPos] = newType;
        UpdateStoredPlacementAxis(worldPos, newType, BlockPlacementAxis.Y);

        EnsureTerrainOverrideIndexBuilt();
        IndexTerrainOverride(worldPos, chunkCoord);

        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, newType);
        HandleLeafDecayBlockChange(worldPos, previousType, newType, false);
        HandleSupportDependentBlockChange(worldPos, previousType, newType);

        torchFireParticleController?.NotifyBlockChanged(worldPos, previousType, newType);
        QueueLeafDecayBlockRefresh(worldPos, chunkCoord, previousType, newType);
        StoneCrusher.NotifyWorldBlockChanged(worldPos, previousType, newType);
        BlockChanged?.Invoke(worldPos, previousType, newType);
        return true;
    }

    private void QueueLeafDecayBlockRefresh(
        Vector3Int worldPos,
        Vector2Int chunkCoord,
        BlockType previousType,
        BlockType newType)
    {
        int dirtySubchunkMask = GetDirtySubchunkMaskForBlockChange(worldPos, previousType, newType);
        bool hadPendingVisualRefresh = leafDecayDirtyChunkMasks.Count > 0;
        int chunksToRebuildCount = CollectBlockEditRebuildChunks(worldPos, chunkCoord);
        for (int i = 0; i < chunksToRebuildCount; i++)
            AddDirtySubchunkMask(leafDecayDirtyChunkMasks, blockChangeChunksToRebuildBuffer[i], dirtySubchunkMask);

        if (!hadPendingVisualRefresh && leafDecayDirtyChunkMasks.Count > 0)
            nextLeafDecayVisualFlushTime = Time.time + Mathf.Max(0.02f, leafDecayVisualFlushIntervalSeconds);

        if (leafDecayRefreshBlockLighting)
        {
            QueueInteractiveBlockLightRefresh(
                worldPos,
                previousType,
                newType,
                Mathf.Max(0f, leafDecayRebuildDelaySeconds));
        }
    }

    private void FlushLeafDecayBlockRefreshes(bool force)
    {
        if (leafDecayDirtyChunkMasks.Count == 0)
        {
            nextLeafDecayVisualFlushTime = float.PositiveInfinity;
            return;
        }

        if (!force && Time.time < nextLeafDecayVisualFlushTime)
            return;

        float refreshDelay = Mathf.Max(0f, leafDecayRebuildDelaySeconds);
        bool smoothRefresh = refreshDelay > 0f;
        bool rebuildColliders = leafDecayRebuildColliders && IsSolidBlock(BlockType.Leaves);

        foreach (KeyValuePair<Vector2Int, int> kv in leafDecayDirtyChunkMasks)
        {
            if (smoothRefresh)
                RequestChunkRebuildDelayed(kv.Key, kv.Value, rebuildColliders, refreshDelay);
            else
                RequestChunkRebuild(kv.Key, kv.Value, rebuildColliders);
        }

        leafDecayDirtyChunkMasks.Clear();
        nextLeafDecayVisualFlushTime = float.PositiveInfinity;
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
            QueueLeafSupportScan(worldPos, Mathf.Max(1, leafDecaySupportDistance));

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

    private void QueueLeafSupportScan(Vector3Int center, int radius)
    {
        if (!queuedLeafSupportScanSet.Add(center))
            return;

        queuedLeafSupportScans.Enqueue(new LeafSupportScanCandidate
        {
            center = center,
            radius = radius
        });
    }

    private void ProcessQueuedLeafSupportScans(float stepStartTime, float timeBudgetSeconds)
    {
        if (queuedLeafSupportScans.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, leafDecaySupportScansPerFrame);
        for (int i = 0; i < perFrameLimit && queuedLeafSupportScans.Count > 0; i++)
        {
            if (i > 0 &&
                timeBudgetSeconds > 0f &&
                Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
            {
                break;
            }

            LeafSupportScanCandidate candidate = queuedLeafSupportScans.Dequeue();
            queuedLeafSupportScanSet.Remove(candidate.center);
            QueueNearbyLeavesForDecay(candidate.center, candidate.radius);
        }
    }

    private void TryQueueLeafDecay(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableLeafDecay || !queuedLeafDecaySet.Add(worldPos))
            return;

        RequeueLeafDecayCandidate(new LeafDecayCandidate
        {
            position = worldPos,
            nextCheckTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private void RequeueLeafDecayCandidate(LeafDecayCandidate candidate)
    {
        queuedLeafDecay.Enqueue(candidate);
        queuedLeafDecaySet.Add(candidate.position);
        nextLeafDecayQueueWakeTime = Mathf.Min(nextLeafDecayQueueWakeTime, candidate.nextCheckTime);
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
        return IsTreeLogBlock(blockType);
    }

    private static bool IsTreeLogBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log;
    }

    public int OakLeafSaplingDropOneIn => Mathf.Max(1, oakLeafSaplingDropOneIn);

    private void HandlePlayerPlacedLogBlockChange(
        Vector3Int worldPos,
        BlockType previousType,
        BlockType newType,
        bool placedByPlayer)
    {
        bool isLog = IsTreeLogBlock(newType);
        if (!isLog)
        {
            playerPlacedLogs.Remove(worldPos);
            return;
        }

        if (placedByPlayer)
            playerPlacedLogs.Add(worldPos);
    }

    private static Vector3Int[] CreateTreeCapitatorNeighborOffsets()
    {
        List<Vector3Int> offsets = new List<Vector3Int>(26);
        for (int y = -1; y <= 1; y++)
        {
            for (int z = -1; z <= 1; z++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0 && z == 0)
                        continue;

                    offsets.Add(new Vector3Int(x, y, z));
                }
            }
        }

        return offsets.ToArray();
    }

    #endregion
}
