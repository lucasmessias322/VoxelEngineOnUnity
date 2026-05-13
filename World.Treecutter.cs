using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly List<Vector3Int> treecutterMachinePositions = new List<Vector3Int>(64);
    private readonly Queue<TreecutterSearchNode> treecutterSearchQueue = new Queue<TreecutterSearchNode>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> treecutterVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly List<Vector3Int> treecutterCollectedLogs = new List<Vector3Int>(64);
    private readonly List<Vector3Int> treecutterCollectedLeaves = new List<Vector3Int>(128);
    private readonly Dictionary<Vector3Int, TreecutterBreakState> treecutterBreakStates = new Dictionary<Vector3Int, TreecutterBreakState>(64);
    private readonly List<Vector3Int> treecutterBreakStateCleanupBuffer = new List<Vector3Int>(64);
    private int treecutterPendingOakSaplingDrops;
    private float nextTreecutterTickTime;

    private struct TreecutterSearchNode
    {
        public Vector3Int position;
        public int leafDistance;
    }

    private struct TreecutterBreakState
    {
        public Vector3Int targetLogPos;
        public BlockType targetLogType;
        public float startTime;
        public float duration;
    }

    #region Treecutter

    private void ProcessTreecutterMachines()
    {
        if (!enableTreecutterMachines)
        {
            ClearTreecutterBreakStates();
            return;
        }

        float now = Time.time;
        UpdateTreecutterBreakCrackVisual(now);

        if (now < nextTreecutterTickTime)
            return;

        nextTreecutterTickTime = now + Mathf.Max(0.05f, treecutterTickInterval);
        if (blockOverrides.Count == 0)
        {
            ClearTreecutterBreakStates();
            return;
        }

        treecutterMachinePositions.Clear();
        foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
        {
            if (pair.Value == BlockType.Treecutter)
                treecutterMachinePositions.Add(pair.Key);
        }

        CleanupTreecutterBreakStates();

        int processed = 0;
        int limit = Mathf.Max(1, treecutterMachinesPerTick);
        for (int i = 0; i < treecutterMachinePositions.Count && processed < limit; i++)
        {
            if (TryRunTreecutter(treecutterMachinePositions[i]))
                processed++;
        }
    }

    private bool TryRunTreecutter(Vector3Int treecutterPos)
    {
        if (GetBlockAt(treecutterPos) != BlockType.Treecutter)
        {
            CancelTreecutterBreakState(treecutterPos);
            return false;
        }

        if (treecutterBreakStates.TryGetValue(treecutterPos, out TreecutterBreakState activeState))
            return UpdateTreecutterBreakState(treecutterPos, activeState);

        if (!TryFindTreecutterTargetLog(treecutterPos, out Vector3Int targetLogPos))
            return false;

        BlockType targetLogType = GetBlockAt(targetLogPos);
        if (!IsTreeLogBlock(targetLogType) || playerPlacedLogs.Contains(targetLogPos))
            return false;

        if (!HasElectricalEnergy(treecutterPos, Mathf.Max(0f, treecutterEnergyPerTree)))
            return false;

        CollectTreecutterTree(targetLogPos);
        if (treecutterCollectedLogs.Count == 0)
            return false;

        treecutterPendingOakSaplingDrops = ResolveTreecutterOakSaplingDropCount();
        bool canOutputDrops = CanTreecutterOutputCollectedDrops(treecutterPos);
        treecutterPendingOakSaplingDrops = 0;
        if (!canOutputDrops)
            return false;

        StartTreecutterBreakState(treecutterPos, targetLogPos, targetLogType);
        return true;
    }

    private void StartTreecutterBreakState(Vector3Int treecutterPos, Vector3Int targetLogPos, BlockType targetLogType)
    {
        TreecutterBreakState state = new TreecutterBreakState
        {
            targetLogPos = targetLogPos,
            targetLogType = targetLogType,
            startTime = Time.time,
            duration = Mathf.Max(0.05f, treecutterBreakDurationSeconds)
        };

        treecutterBreakStates[treecutterPos] = state;
        UpdateTreecutterBreakCrackVisual(targetLogPos, targetLogType, 0f);
    }

    private bool UpdateTreecutterBreakState(Vector3Int treecutterPos, TreecutterBreakState state)
    {
        if (!IsTreecutterBreakStateValid(state))
        {
            CancelTreecutterBreakState(treecutterPos);
            return false;
        }

        float progress01 = GetTreecutterBreakProgress01(state, Time.time);
        UpdateTreecutterBreakCrackVisual(state.targetLogPos, state.targetLogType, progress01);
        if (progress01 < 1f)
            return true;

        CollectTreecutterTree(state.targetLogPos);
        if (treecutterCollectedLogs.Count == 0)
        {
            CancelTreecutterBreakState(treecutterPos);
            return false;
        }

        bool shouldReplant = TryResolveTreecutterReplant(out Vector3Int replantPos, out BlockType replantSaplingType);
        treecutterPendingOakSaplingDrops = ResolveTreecutterOakSaplingDropCount();
        if (!CanTreecutterOutputCollectedDrops(treecutterPos))
        {
            treecutterPendingOakSaplingDrops = 0;
            return true;
        }

        if (!TryConsumeElectricalEnergy(treecutterPos, Mathf.Max(0f, treecutterEnergyPerTree)))
        {
            treecutterPendingOakSaplingDrops = 0;
            return true;
        }

        BreakCollectedTreecutterTree(treecutterPos, shouldReplant, replantPos, replantSaplingType);
        CancelTreecutterBreakState(treecutterPos);
        return true;
    }

    private bool IsTreecutterBreakStateValid(TreecutterBreakState state)
    {
        return IsTreeLogBlock(state.targetLogType) &&
               GetBlockAt(state.targetLogPos) == state.targetLogType &&
               !playerPlacedLogs.Contains(state.targetLogPos);
    }

    private static float GetTreecutterBreakProgress01(TreecutterBreakState state, float now)
    {
        return Mathf.Clamp01((now - state.startTime) / Mathf.Max(0.05f, state.duration));
    }

    private void CleanupTreecutterBreakStates()
    {
        if (treecutterBreakStates.Count == 0)
            return;

        treecutterBreakStateCleanupBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, TreecutterBreakState> pair in treecutterBreakStates)
        {
            if (GetBlockAt(pair.Key) != BlockType.Treecutter || !IsTreecutterBreakStateValid(pair.Value))
                treecutterBreakStateCleanupBuffer.Add(pair.Key);
        }

        for (int i = 0; i < treecutterBreakStateCleanupBuffer.Count; i++)
            treecutterBreakStates.Remove(treecutterBreakStateCleanupBuffer[i]);

        if (treecutterBreakStates.Count == 0)
            HideTreecutterBreakCrackVisual();
    }

    private void CancelTreecutterBreakState(Vector3Int treecutterPos)
    {
        if (treecutterBreakStates.Remove(treecutterPos) && treecutterBreakStates.Count == 0)
            HideTreecutterBreakCrackVisual();
    }

    private void ClearTreecutterBreakStates()
    {
        treecutterBreakStates.Clear();
        treecutterPendingOakSaplingDrops = 0;
        HideTreecutterBreakCrackVisual();
    }

    private bool TryFindTreecutterTargetLog(Vector3Int treecutterPos, out Vector3Int targetLogPos)
    {
        targetLogPos = default;

        Vector3Int forwardStep = ResolveTreecutterForwardStep(
            GetPlacementAxisAt(treecutterPos, BlockType.Treecutter));
        int maxDistance = Mathf.Max(1, treecutterFrontSearchDistanceBlocks);
        int below = Mathf.Max(0, treecutterFrontVerticalSearchBelow);
        int above = Mathf.Max(0, treecutterFrontVerticalSearchAbove);

        for (int distance = 1; distance <= maxDistance; distance++)
        {
            Vector3Int basePos = treecutterPos + forwardStep * distance;
            for (int yOffset = -below; yOffset <= above; yOffset++)
            {
                Vector3Int candidate = new Vector3Int(basePos.x, basePos.y + yOffset, basePos.z);
                if (candidate.y <= 2 || candidate.y >= Chunk.SizeY)
                    continue;

                BlockType blockType = GetBlockAt(candidate);
                if (!IsTreeLogBlock(blockType) || playerPlacedLogs.Contains(candidate))
                    continue;

                targetLogPos = candidate;
                return true;
            }
        }

        return false;
    }

    private void CollectTreecutterTree(Vector3Int origin)
    {
        treecutterSearchQueue.Clear();
        treecutterVisited.Clear();
        treecutterCollectedLogs.Clear();
        treecutterCollectedLeaves.Clear();
        treecutterPendingOakSaplingDrops = 0;

        BlockType trunkType = GetBlockAt(origin);
        if (!IsTreeLogBlock(trunkType) || playerPlacedLogs.Contains(origin))
            return;

        int maxLogs = Mathf.Max(1, treecutterMaxLogsPerTree);
        treecutterSearchQueue.Enqueue(new TreecutterSearchNode
        {
            position = origin,
            leafDistance = 0
        });
        treecutterVisited.Add(origin);

        while (treecutterSearchQueue.Count > 0 && treecutterCollectedLogs.Count < maxLogs)
        {
            TreecutterSearchNode current = treecutterSearchQueue.Dequeue();
            if (GetBlockAt(current.position) != trunkType)
                continue;
            if (playerPlacedLogs.Contains(current.position))
                continue;

            treecutterCollectedLogs.Add(current.position);

            for (int i = 0; i < TreeCapitatorNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current.position + TreeCapitatorNeighborOffsets[i];
                if (neighborPos.y <= 2 || neighborPos.y >= Chunk.SizeY)
                    continue;
                if (!treecutterVisited.Add(neighborPos))
                    continue;
                if (GetBlockAt(neighborPos) != trunkType || playerPlacedLogs.Contains(neighborPos))
                    continue;

                treecutterSearchQueue.Enqueue(new TreecutterSearchNode
                {
                    position = neighborPos,
                    leafDistance = 0
                });
            }
        }

        CollectTreecutterLeavesFromLogs();
    }

    private void CollectTreecutterLeavesFromLogs()
    {
        int maxLeafDistance = Mathf.Max(1, treecutterLeafSearchDistance);
        int maxLeaves = Mathf.Max(1, treecutterMaxLeavesPerTree);

        treecutterSearchQueue.Clear();
        treecutterVisited.Clear();
        for (int i = 0; i < treecutterCollectedLogs.Count; i++)
        {
            Vector3Int logPos = treecutterCollectedLogs[i];
            treecutterVisited.Add(logPos);
            treecutterSearchQueue.Enqueue(new TreecutterSearchNode
            {
                position = logPos,
                leafDistance = 0
            });
        }

        while (treecutterSearchQueue.Count > 0 && treecutterCollectedLeaves.Count < maxLeaves)
        {
            TreecutterSearchNode current = treecutterSearchQueue.Dequeue();
            if (current.leafDistance >= maxLeafDistance)
                continue;

            for (int i = 0; i < TreeCapitatorNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current.position + TreeCapitatorNeighborOffsets[i];
                if (neighborPos.y <= 2 || neighborPos.y >= Chunk.SizeY)
                    continue;
                if (!treecutterVisited.Add(neighborPos))
                    continue;

                BlockType neighborType = GetBlockAt(neighborPos);
                if (neighborType == BlockType.Leaves)
                {
                    treecutterCollectedLeaves.Add(neighborPos);
                    treecutterSearchQueue.Enqueue(new TreecutterSearchNode
                    {
                        position = neighborPos,
                        leafDistance = current.leafDistance + 1
                    });
                }
            }
        }
    }

    private void BreakCollectedTreecutterTree(
        Vector3Int treecutterPos,
        bool shouldReplant,
        Vector3Int replantPos,
        BlockType replantSaplingType)
    {
        for (int i = 0; i < treecutterCollectedLogs.Count; i++)
        {
            Vector3Int logPos = treecutterCollectedLogs[i];
            BlockType currentType = GetBlockAt(logPos);
            if (!IsTreeLogBlock(currentType))
                continue;

            bool outputDrop = TryOutputTreecutterDrop(treecutterPos, currentType);
            SetBlockAt(logPos, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(logPos);

            if (!outputDrop)
                Debug.LogWarning($"[World] Treecutter falhou ao gerar drop de {currentType} em {logPos}.");
        }

        for (int i = 0; i < treecutterCollectedLeaves.Count; i++)
        {
            Vector3Int leafPos = treecutterCollectedLeaves[i];
            if (GetBlockAt(leafPos) != BlockType.Leaves)
                continue;

            SetBlockAt(leafPos, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(leafPos);
        }

        if (shouldReplant && !TryReplantTreecutterSapling(replantPos, replantSaplingType))
            Debug.LogWarning($"[World] Treecutter falhou ao replantar {replantSaplingType} em {replantPos}.");

        if (treecutterPendingOakSaplingDrops > 0)
        {
            bool outputSaplings = TryOutputTreecutterDrop(
                treecutterPos,
                BlockType.oakTreeSapling,
                treecutterPendingOakSaplingDrops);
            if (!outputSaplings)
                Debug.LogWarning($"[World] Treecutter falhou ao gerar drop de {BlockType.oakTreeSapling} x{treecutterPendingOakSaplingDrops}.");
        }

        treecutterPendingOakSaplingDrops = 0;
    }

    private bool TryResolveTreecutterReplant(out Vector3Int saplingPos, out BlockType saplingType)
    {
        saplingPos = default;
        saplingType = BlockType.Air;

        if (treecutterCollectedLogs.Count == 0)
            return false;

        BlockType trunkType = GetBlockAt(treecutterCollectedLogs[0]);
        if (!TryResolveTreecutterSaplingType(trunkType, out saplingType))
            return false;

        bool hasCandidate = false;
        Vector3Int bestCandidate = default;
        for (int i = 0; i < treecutterCollectedLogs.Count; i++)
        {
            Vector3Int candidate = treecutterCollectedLogs[i];
            if (!CanTreecutterReplantAt(candidate, saplingType))
                continue;

            if (!hasCandidate || candidate.y < bestCandidate.y)
            {
                bestCandidate = candidate;
                hasCandidate = true;
            }
        }

        if (!hasCandidate)
            return false;

        saplingPos = bestCandidate;
        return true;
    }

    private static bool TryResolveTreecutterSaplingType(BlockType trunkType, out BlockType saplingType)
    {
        switch (trunkType)
        {
            case BlockType.Log:
                saplingType = BlockType.oakTreeSapling;
                return true;

            case BlockType.birch_log:
                saplingType = BlockType.birchTreeSapling;
                return true;

            default:
                saplingType = BlockType.Air;
                return false;
        }
    }

    private bool CanTreecutterReplantAt(Vector3Int saplingPos, BlockType saplingType)
    {
        if (!SaplingBlockUtility.IsSapling(saplingType))
            return false;

        if (blockData != null && blockData.GetMapping(saplingType) == null)
            return false;

        if (saplingPos.y <= 2 || saplingPos.y >= Chunk.SizeY)
            return false;

        BlockType currentType = GetBlockAt(saplingPos);
        if (currentType != BlockType.Air && !IsTreeLogBlock(currentType))
            return false;

        return SaplingBlockUtility.CanPlantOn(GetBlockAt(saplingPos + Vector3Int.down));
    }

    private bool TryReplantTreecutterSapling(Vector3Int saplingPos, BlockType saplingType)
    {
        if (!CanTreecutterReplantAt(saplingPos, saplingType))
            return false;

        if (GetBlockAt(saplingPos) != BlockType.Air)
            return false;

        SetBlockAt(saplingPos, saplingType);
        InvalidateLoadedSubchunkCollidersAt(saplingPos);
        return GetBlockAt(saplingPos) == saplingType;
    }

    private bool CanTreecutterOutputCollectedDrops(Vector3Int treecutterPos)
    {
        for (int i = 0; i < treecutterCollectedLogs.Count; i++)
        {
            BlockType currentType = GetBlockAt(treecutterCollectedLogs[i]);
            if (!IsTreeLogBlock(currentType))
                continue;

            if (!CanTreecutterOutputDrop(treecutterPos, currentType))
                return false;
        }

        if (treecutterPendingOakSaplingDrops > 0 &&
            !CanTreecutterOutputDrop(treecutterPos, BlockType.oakTreeSapling, treecutterPendingOakSaplingDrops))
        {
            return false;
        }

        return true;
    }

    private bool CanTreecutterOutputDrop(Vector3Int treecutterPos, BlockType targetType, int amountOverride = 0)
    {
        Vector3Int topPos = treecutterPos + Vector3Int.up;
        BlockType topType = GetBlockAt(topPos);
        if (topType != BlockType.chest && !TransportTubeUtility.IsTransportTubeNetworkBlock(topType))
            return true;

        if (!TryResolveAutoMinerDrop(targetType, out Item outputItem, out int amount))
            return false;

        if (amountOverride > 0)
            amount = amountOverride;

        if (TransportTubeUtility.IsTransportTubeNetworkBlock(topType))
            return TryFindAutoMinerTransportTubeOutput(topPos, outputItem, amount, out _);

        ChestUIController chestUI = ChestUIController.EnsureInstance();
        return chestUI != null && chestUI.CanInsertItemStackIntoChest(topPos, outputItem, amount);
    }

    private bool TryOutputTreecutterDrop(Vector3Int treecutterPos, BlockType targetType, int amountOverride = 0)
    {
        if (!TryResolveAutoMinerDrop(targetType, out Item outputItem, out int amount))
            return false;

        if (amountOverride > 0)
            amount = amountOverride;

        int remaining = Mathf.Max(1, amount);
        Vector3Int topPos = treecutterPos + Vector3Int.up;
        BlockType topType = GetBlockAt(topPos);
        if (topType == BlockType.chest)
        {
            ChestUIController chestUI = ChestUIController.EnsureInstance();
            if (chestUI != null)
                remaining = chestUI.InsertItemStackIntoChest(topPos, outputItem, remaining);

            return remaining <= 0;
        }

        if (TransportTubeUtility.IsTransportTubeNetworkBlock(topType))
        {
            if (!TryFindAutoMinerTransportTubeOutput(topPos, outputItem, remaining, out AutoMinerTransportTubeOutput tubeOutput))
                return false;

            return TryDeliverAutoMinerTransportTubeOutput(tubeOutput, outputItem, remaining);
        }

        Vector3 dropPosition = GetTreecutterDropPosition(treecutterPos);
        Vector3 throwDirection = Vector3.up * 0.65f;
        return ChestUIController.TrySpawnItemStack(outputItem, remaining, dropPosition, throwDirection);
    }

    private int ResolveTreecutterOakSaplingDropCount()
    {
        if (treecutterCollectedLogs.Count == 0 || GetBlockAt(treecutterCollectedLogs[0]) != BlockType.Log)
            return 0;

        return BlockBreakDropResolver.RollOakLeafSaplingDropCount(this, treecutterCollectedLeaves.Count);
    }

    private Vector3 GetTreecutterDropPosition(Vector3Int treecutterPos)
    {
        Vector3Int topPos = treecutterPos + Vector3Int.up;
        if (ConveyorBeltUtility.IsConveyorBlock(GetBlockAt(topPos)))
        {
            return topPos + new Vector3(
                0.5f,
                Mathf.Max(0f, autoMinerDropConveyorTopOffset),
                0.5f);
        }

        return treecutterPos + new Vector3(
            0.5f,
            1f + Mathf.Max(0f, autoMinerDropTopOffset),
            0.5f);
    }

    private static Vector3Int ResolveTreecutterForwardStep(BlockPlacementAxis placementAxis)
    {
        switch (BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis))
        {
            case BlockPlacementAxis.X:
                return Vector3Int.right;

            case BlockPlacementAxis.XNegative:
                return Vector3Int.left;

            case BlockPlacementAxis.ZNegative:
                return Vector3Int.back;

            default:
                return Vector3Int.forward;
        }
    }

    #endregion
}
