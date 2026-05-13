using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly List<Vector3Int> transportTubeFilterPositions = new List<Vector3Int>(64);
    private readonly Queue<Vector3Int> transportTubeRouteQueue = new Queue<Vector3Int>(128);
    private readonly HashSet<Vector3Int> transportTubeRouteVisited = new HashSet<Vector3Int>();
    private float nextTransportTubeFilterTickTime;

    private struct TransportTubeRouteOutput
    {
        public Vector3Int tubePos;
        public Vector3Int exitDirection;
        public Vector3Int targetPos;
        public Vector3 dropPosition;
        public bool insertIntoChest;
    }

    #region Transport Tubes

    private void ProcessTransportTubeFilters()
    {
        if (!enableTransportTubeFilters)
            return;

        float now = Time.time;
        if (now < nextTransportTubeFilterTickTime)
            return;

        nextTransportTubeFilterTickTime = now + Mathf.Max(0.05f, transportTubeFilterTickInterval);

        transportTubeFilterPositions.Clear();
        if (blockOverrides.Count > 0)
        {
            foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
            {
                if (pair.Value == BlockType.TransportTubeFilter)
                    transportTubeFilterPositions.Add(pair.Key);
            }
        }

        int processed = 0;
        int filterLimit = Mathf.Max(1, transportTubeFiltersPerTick);
        for (int i = 0; i < transportTubeFilterPositions.Count && processed < filterLimit; i++)
        {
            if (TryRunTransportTubeFilter(transportTubeFilterPositions[i]))
                processed++;
        }
    }

    private bool TryRunTransportTubeFilter(Vector3Int filterPos)
    {
        if (GetBlockAt(filterPos) != BlockType.TransportTubeFilter)
            return false;

        if (!TryFindTransportTubeFilterSourceChest(filterPos, out Vector3Int sourceChestPos))
            return false;

        ChestUIController chestUI = ChestUIController.EnsureInstance();
        if (chestUI == null)
            return false;

        int transferLimit = Mathf.Max(1, transportTubeFilterItemsPerTick);
        if (!chestUI.TryPeekItemStackFromChest(sourceChestPos, transferLimit, out Item item, out int amount) ||
            item == null ||
            amount <= 0)
        {
            return false;
        }

        if (!TryFindTransportTubeRouteOutput(
                filterPos,
                sourceChestPos,
                item,
                amount,
                out TransportTubeRouteOutput output))
        {
            return false;
        }

        if (!chestUI.TryTakeItemStackFromChest(sourceChestPos, amount, item, out Item takenItem, out int takenAmount) ||
            takenItem == null ||
            takenAmount <= 0)
        {
            return false;
        }

        if (TryDeliverTransportTubeOutput(output, takenItem, takenAmount))
            return true;

        chestUI.InsertItemStackIntoChest(sourceChestPos, takenItem, takenAmount);
        return false;
    }

    private bool TryFindTransportTubeFilterSourceChest(Vector3Int filterPos, out Vector3Int chestPos)
    {
        for (int i = 0; i < sixDirections.Length; i++)
        {
            Vector3Int candidatePos = filterPos + sixDirections[i];
            if (GetBlockAt(candidatePos) != BlockType.chest)
                continue;

            chestPos = candidatePos;
            return true;
        }

        chestPos = default;
        return false;
    }

    private bool TryResolveTransportTubeFilterBackToChestAxis(Vector3Int filterPos, out BlockPlacementAxis axis)
    {
        if (GetBlockAt(filterPos + Vector3Int.left) == BlockType.chest)
        {
            axis = BlockPlacementAxis.X;
            return true;
        }

        if (GetBlockAt(filterPos + Vector3Int.right) == BlockType.chest)
        {
            axis = BlockPlacementAxis.XNegative;
            return true;
        }

        if (GetBlockAt(filterPos + Vector3Int.back) == BlockType.chest)
        {
            axis = BlockPlacementAxis.Z;
            return true;
        }

        if (GetBlockAt(filterPos + Vector3Int.forward) == BlockType.chest)
        {
            axis = BlockPlacementAxis.ZNegative;
            return true;
        }

        if (GetBlockAt(filterPos + Vector3Int.down) == BlockType.chest)
        {
            axis = BlockPlacementAxis.Y;
            return true;
        }

        if (GetBlockAt(filterPos + Vector3Int.up) == BlockType.chest)
        {
            axis = BlockPlacementAxis.YNegative;
            return true;
        }

        axis = default;
        return false;
    }

    private bool TryFindTransportTubeRouteOutput(
        Vector3Int startPos,
        Vector3Int sourceChestPos,
        Item item,
        int amount,
        out TransportTubeRouteOutput output)
    {
        output = default;
        if (item == null ||
            amount <= 0 ||
            !TransportTubeUtility.IsTransportTubeNetworkBlock(GetBlockAt(startPos)))
        {
            return false;
        }

        transportTubeRouteQueue.Clear();
        transportTubeRouteVisited.Clear();

        transportTubeRouteQueue.Enqueue(startPos);
        transportTubeRouteVisited.Add(startPos);

        int inspectedCount = 0;
        int searchLimit = Mathf.Max(16, transportTubeFilterSearchLimit);
        while (transportTubeRouteQueue.Count > 0 && inspectedCount < searchLimit)
        {
            Vector3Int tubePos = transportTubeRouteQueue.Dequeue();
            inspectedCount++;

            if (TryResolveTransportTubeOutputAt(tubePos, sourceChestPos, item, amount, out output))
                return true;

            QueueConnectedTransportTubeNodes(tubePos);
        }

        return false;
    }

    private void QueueConnectedTransportTubeNodes(Vector3Int tubePos)
    {
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.left);
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.right);
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.back);
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.forward);
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.down);
        TryQueueConnectedTransportTubeNode(tubePos, Vector3Int.up);
    }

    private void TryQueueConnectedTransportTubeNode(Vector3Int tubePos, Vector3Int direction)
    {
        Vector3Int neighborPos = tubePos + direction;
        if (transportTubeRouteVisited.Contains(neighborPos) ||
            !TransportTubeUtility.IsTransportTubeNetworkBlock(GetBlockAt(neighborPos)))
        {
            return;
        }

        transportTubeRouteVisited.Add(neighborPos);
        transportTubeRouteQueue.Enqueue(neighborPos);
    }

    private bool TryResolveTransportTubeOutputAt(
        Vector3Int tubePos,
        Vector3Int sourceChestPos,
        Item item,
        int amount,
        out TransportTubeRouteOutput output)
    {
        output = default;
        BlockType tubeType = GetBlockAt(tubePos);
        if (!TransportTubeUtility.IsTransportTubeBlock(tubeType))
            return false;

        byte connectionMask = TransportTubeUtility.ResolveConnectionMask(this, tubePos);
        int connectionCount = TransportTubeUtility.CountConnections(connectionMask);

        if (connectionCount == 0)
        {
            BlockPlacementAxis fallbackAxis = GetPlacementAxisAt(tubePos, tubeType);
            return TryResolveTransportTubeFallbackOutput(
                tubePos,
                sourceChestPos,
                fallbackAxis,
                item,
                amount,
                out output);
        }

        if (connectionCount != 1)
            return false;

        Vector3Int connectedDirection = GetSingleTransportTubeConnectionDirection(connectionMask);
        return TryBuildTransportTubeOutput(
            tubePos,
            -connectedDirection,
            sourceChestPos,
            item,
            amount,
            out output);
    }

    private bool TryResolveTransportTubeFallbackOutput(
        Vector3Int tubePos,
        Vector3Int sourceChestPos,
        BlockPlacementAxis fallbackAxis,
        Item item,
        int amount,
        out TransportTubeRouteOutput output)
    {
        output = default;
        fallbackAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(fallbackAxis);

        if (fallbackAxis == BlockPlacementAxis.YNegative)
        {
            return TryBuildTransportTubeOutput(tubePos, Vector3Int.up, sourceChestPos, item, amount, out output) ||
                   TryBuildTransportTubeOutput(tubePos, Vector3Int.down, sourceChestPos, item, amount, out output);
        }

        if (fallbackAxis == BlockPlacementAxis.X || fallbackAxis == BlockPlacementAxis.XNegative)
        {
            return TryBuildTransportTubeOutput(tubePos, Vector3Int.right, sourceChestPos, item, amount, out output) ||
                   TryBuildTransportTubeOutput(tubePos, Vector3Int.left, sourceChestPos, item, amount, out output);
        }

        return TryBuildTransportTubeOutput(tubePos, Vector3Int.forward, sourceChestPos, item, amount, out output) ||
               TryBuildTransportTubeOutput(tubePos, Vector3Int.back, sourceChestPos, item, amount, out output);
    }

    private bool TryBuildTransportTubeOutput(
        Vector3Int tubePos,
        Vector3Int outputDirection,
        Vector3Int sourceChestPos,
        Item item,
        int amount,
        out TransportTubeRouteOutput output)
    {
        output = default;
        if (outputDirection == Vector3Int.zero)
            return false;

        Vector3Int targetPos = tubePos + outputDirection;
        if (targetPos == sourceChestPos)
            return false;

        BlockType targetType = GetBlockAt(targetPos);
        if (targetType == BlockType.chest)
        {
            ChestUIController chestUI = ChestUIController.EnsureInstance();
            if (chestUI == null || !chestUI.CanInsertItemStackIntoChest(targetPos, item, amount))
                return false;

            output = new TransportTubeRouteOutput
            {
                tubePos = tubePos,
                exitDirection = outputDirection,
                targetPos = targetPos,
                dropPosition = GetTransportTubeDropPosition(tubePos, targetPos, outputDirection, targetType),
                insertIntoChest = true
            };
            return true;
        }

        if (!CanTransportTubeDropInto(targetType))
            return false;

        output = new TransportTubeRouteOutput
        {
            tubePos = tubePos,
            exitDirection = outputDirection,
            targetPos = targetPos,
            dropPosition = GetTransportTubeDropPosition(tubePos, targetPos, outputDirection, targetType),
            insertIntoChest = false
        };
        return true;
    }

    private bool TryDeliverTransportTubeOutput(TransportTubeRouteOutput output, Item item, int amount)
    {
        int remaining = Mathf.Max(1, amount);
        if (output.insertIntoChest)
        {
            ChestUIController chestUI = ChestUIController.EnsureInstance();
            if (chestUI != null)
                remaining = chestUI.InsertItemStackIntoChest(output.targetPos, item, remaining);

            return remaining <= 0;
        }

        Vector3 throwDirection = GetTransportTubeThrowDirection(output.exitDirection);
        return ChestUIController.TrySpawnItemStack(item, remaining, output.dropPosition, throwDirection);
    }

    private bool CanTransportTubeDropInto(BlockType targetType)
    {
        if (targetType == BlockType.Air ||
            FluidBlockUtility.IsWater(targetType) ||
            ConveyorBeltUtility.IsConveyorBlock(targetType))
        {
            return true;
        }

        return !TransportTubeUtility.IsTransportTubeNetworkBlock(targetType) && !IsSolidBlock(targetType);
    }

    private Vector3 GetTransportTubeDropPosition(
        Vector3Int tubePos,
        Vector3Int targetPos,
        Vector3Int outputDirection,
        BlockType targetType)
    {
        if (ConveyorBeltUtility.IsConveyorBlock(targetType))
        {
            return targetPos + new Vector3(
                0.5f,
                Mathf.Max(0f, autoMinerDropConveyorTopOffset),
                0.5f);
        }

        Vector3 direction = new Vector3(outputDirection.x, outputDirection.y, outputDirection.z);
        return tubePos + Vector3.one * 0.5f +
               direction.normalized * (0.5f + Mathf.Max(0f, transportTubeExitOffset));
    }

    private static Vector3 GetTransportTubeThrowDirection(Vector3Int outputDirection)
    {
        Vector3 direction = new Vector3(outputDirection.x, outputDirection.y, outputDirection.z);
        if (direction.sqrMagnitude <= 0.0001f)
            return Vector3.up * 0.65f;

        Vector3 throwDirection = direction.normalized * 0.9f + Vector3.up * 0.35f;
        return throwDirection.sqrMagnitude > 0.0001f ? throwDirection : Vector3.up * 0.65f;
    }

    private static Vector3Int GetSingleTransportTubeConnectionDirection(byte connectionMask)
    {
        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest))
            return Vector3Int.left;

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast))
            return Vector3Int.right;

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectSouth))
            return Vector3Int.back;

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectNorth))
            return Vector3Int.forward;

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown))
            return Vector3Int.down;

        return Vector3Int.up;
    }

    #endregion
}
