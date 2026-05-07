using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly Queue<Vector3Int> queuedSupportDependentBlockChecks = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedSupportDependentBlockCheckSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Queue<Vector3Int> supportDependencySearchQueue = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> supportDependencyVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);

    private static readonly Vector3Int[] SupportDependencyNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    #region Support Dependent Blocks

    public bool CanBlockStayAt(Vector3Int worldPos, BlockType blockType)
    {
        if (!DoesBlockBreakWithoutSupport(blockType))
            return true;

        return HasStableSupportForSupportDependentBlock(worldPos, blockType);
    }

    private void ProcessQueuedSupportDependentBlockChecks()
    {
        if (queuedSupportDependentBlockChecks.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, supportDependentBlockChecksPerFrame);
        int processed = 0;
        int attempts = queuedSupportDependentBlockChecks.Count;

        while (processed < perFrameLimit && attempts-- > 0)
        {
            Vector3Int worldPos = queuedSupportDependentBlockChecks.Dequeue();
            queuedSupportDependentBlockCheckSet.Remove(worldPos);
            EvaluateSupportDependentBlock(worldPos);
            processed++;
        }
    }

    private void EvaluateSupportDependentBlock(Vector3Int worldPos)
    {
        BlockType blockType = GetBlockAt(worldPos);
        if (!DoesBlockBreakWithoutSupport(blockType))
            return;

        if (CanBlockStayAt(worldPos, blockType))
            return;

        BlockBreakDropResolver.TrySpawnDrop(this, worldPos, blockType, Vector3.up);
        SetBlockAt(worldPos, BlockType.Air);
    }

    private void HandleSupportDependentBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (DoesBlockBreakWithoutSupport(newType))
            TryQueueSupportDependentBlockCheck(worldPos);

        if (previousType == newType)
            return;

        QueueAdjacentSupportDependentBlockChecks(worldPos);
    }

    private void QueueAdjacentSupportDependentBlockChecks(Vector3Int center)
    {
        for (int i = 0; i < SupportDependencyNeighborOffsets.Length; i++)
        {
            Vector3Int neighborPos = center + SupportDependencyNeighborOffsets[i];
            if (DoesBlockBreakWithoutSupport(GetBlockAt(neighborPos)))
                TryQueueSupportDependentBlockCheck(neighborPos);
        }
    }

    private void TryQueueSupportDependentBlockCheck(Vector3Int worldPos)
    {
        if (worldPos.y <= 2)
            return;

        if (queuedSupportDependentBlockCheckSet.Add(worldPos))
            queuedSupportDependentBlockChecks.Enqueue(worldPos);
    }

    private bool HasStableSupportForSupportDependentBlock(Vector3Int origin, BlockType originType)
    {
        int searchLimit = Mathf.Max(16, supportDependencySearchLimit);
        supportDependencySearchQueue.Clear();
        supportDependencyVisited.Clear();
        supportDependencyVisited.Add(origin);
        supportDependencySearchQueue.Enqueue(origin);

        while (supportDependencySearchQueue.Count > 0)
        {
            Vector3Int current = supportDependencySearchQueue.Dequeue();
            for (int i = 0; i < SupportDependencyNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current + SupportDependencyNeighborOffsets[i];
                if (supportDependencyVisited.Contains(neighborPos))
                    continue;

                BlockType neighborType = neighborPos == origin ? originType : GetBlockAt(neighborPos);
                if (IsStableSupportBlock(neighborType))
                    return true;

                if (!DoesBlockBreakWithoutSupport(neighborType))
                    continue;

                if (supportDependencyVisited.Count >= searchLimit)
                    return true;

                supportDependencyVisited.Add(neighborPos);
                supportDependencySearchQueue.Enqueue(neighborPos);
            }
        }

        return false;
    }

    private bool IsStableSupportBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        if (DoesBlockBreakWithoutSupport(blockType))
            return false;

        if (blockData != null)
        {
            BlockTextureMapping? mapping = blockData.GetMapping(blockType);
            if (mapping != null)
            {
                BlockTextureMapping value = mapping.Value;
                return value.isSolid && !value.isEmpty && !value.isLiquid;
            }
        }

        return IsSolidBlock(blockType) && !IsLiquidBlock(blockType);
    }

    private bool DoesBlockBreakWithoutSupport(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType) || blockData == null)
            return false;

        BlockTextureMapping? mapping = blockData.GetMapping(blockType);
        return mapping != null && mapping.Value.breaksWithoutSupport;
    }

    #endregion
}
