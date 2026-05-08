using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly List<Vector3Int> treecutterMachinePositions = new List<Vector3Int>(64);
    private readonly Queue<TreecutterSearchNode> treecutterSearchQueue = new Queue<TreecutterSearchNode>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> treecutterVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly List<Vector3Int> treecutterCollectedLogs = new List<Vector3Int>(64);
    private readonly List<Vector3Int> treecutterCollectedLeaves = new List<Vector3Int>(128);
    private float nextTreecutterTickTime;

    private struct TreecutterSearchNode
    {
        public Vector3Int position;
        public int leafDistance;
    }

    #region Treecutter

    private void ProcessTreecutterMachines()
    {
        if (!enableTreecutterMachines)
            return;

        float now = Time.time;
        if (now < nextTreecutterTickTime)
            return;

        nextTreecutterTickTime = now + Mathf.Max(0.05f, treecutterTickInterval);
        if (blockOverrides.Count == 0)
            return;

        treecutterMachinePositions.Clear();
        foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
        {
            if (pair.Value == BlockType.Treecutter)
                treecutterMachinePositions.Add(pair.Key);
        }

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
            return false;

        if (!TryFindTreecutterTargetLog(treecutterPos, out Vector3Int targetLogPos))
            return false;

        CollectTreecutterTree(targetLogPos);
        if (treecutterCollectedLogs.Count == 0)
            return false;

        if (!TryConsumeElectricalEnergy(treecutterPos, Mathf.Max(0f, treecutterEnergyPerTree)))
            return false;

        BreakCollectedTreecutterTree(treecutterPos);
        return true;
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

    private void BreakCollectedTreecutterTree(Vector3Int treecutterPos)
    {
        Vector3 throwDirection = ((Vector3)ResolveTreecutterForwardStep(
            GetPlacementAxisAt(treecutterPos, BlockType.Treecutter)) + Vector3.up * 0.35f).normalized;

        for (int i = 0; i < treecutterCollectedLogs.Count; i++)
        {
            Vector3Int logPos = treecutterCollectedLogs[i];
            BlockType currentType = GetBlockAt(logPos);
            if (!IsTreeLogBlock(currentType))
                continue;

            bool spawnedDrop = BlockBreakDropResolver.TrySpawnDrop(this, logPos, currentType, throwDirection);
            SetBlockAt(logPos, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(logPos);

            if (!spawnedDrop)
            {
                bool addedToInventory = PlayerInventory.Instance != null &&
                                        BlockBreakDropResolver.TryAddDropToInventory(PlayerInventory.Instance, this, currentType);
                if (!addedToInventory)
                    Debug.LogWarning($"[World] Treecutter falhou ao gerar drop de {currentType} em {logPos}.");
            }
        }

        for (int i = 0; i < treecutterCollectedLeaves.Count; i++)
        {
            Vector3Int leafPos = treecutterCollectedLeaves[i];
            if (GetBlockAt(leafPos) != BlockType.Leaves)
                continue;

            SetBlockAt(leafPos, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(leafPos);
        }
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
