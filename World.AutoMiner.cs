using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private const int AutoMinerWireframePositionCount = 16;

    private readonly List<Vector3Int> autoMinerMachinePositions = new List<Vector3Int>(64);
    private readonly Dictionary<Vector3Int, int> autoMinerScanOffsetByPosition =
        new Dictionary<Vector3Int, int>(InitialBlockEditCapacity);
    private readonly List<Vector3Int> autoMinerStateCleanupBuffer = new List<Vector3Int>(64);
    private readonly Dictionary<Vector3Int, LineRenderer> autoMinerAreaLinesByPosition =
        new Dictionary<Vector3Int, LineRenderer>(64);
    private readonly Dictionary<Vector3Int, AutoMinerLaserVisual> autoMinerLaserVisualsByPosition =
        new Dictionary<Vector3Int, AutoMinerLaserVisual>(64);
    private readonly HashSet<Vector3Int> autoMinerTouchedAreaLinePositions =
        new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> autoMinerTransportTubeQueue = new Queue<Vector3Int>(128);
    private readonly HashSet<Vector3Int> autoMinerTransportTubeVisited = new HashSet<Vector3Int>();
    private readonly List<Vector3Int> autoMinerAreaLineRemovalBuffer = new List<Vector3Int>(64);
    private readonly List<Vector3Int> autoMinerLaserRemovalBuffer = new List<Vector3Int>(64);

    private Transform autoMinerAreaLinesRoot;
    private Transform autoMinerLaserLinesRoot;
    private float nextAutoMinerTickTime;

    private sealed class AutoMinerLaserVisual
    {
        public LineRenderer line;
        public float hideTime;
    }

    private struct AutoMinerTransportTubeOutput
    {
        public Vector3Int tubePos;
        public Vector3Int exitDirection;
        public Vector3Int targetPos;
        public Vector3 dropPosition;
        public bool insertIntoChest;
    }

    #region Auto Miner

    private void ProcessAutoMinerMachines()
    {
        if (!enableAutoMinerMachines)
        {
            DestroyAutoMinerAreaVisuals();
            return;
        }

        float now = Time.time;
        if (now < nextAutoMinerTickTime)
            return;

        nextAutoMinerTickTime = now + Mathf.Max(0.05f, autoMinerTickInterval);

        autoMinerMachinePositions.Clear();
        if (blockOverrides.Count > 0)
        {
            foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
            {
                if (pair.Value == BlockType.AutoMiner)
                    autoMinerMachinePositions.Add(pair.Key);
            }
        }

        CleanupAutoMinerMachineState();
        SyncAutoMinerAreaVisuals();

        int processed = 0;
        int machineLimit = Mathf.Max(1, autoMinerMachinesPerTick);
        for (int i = 0; i < autoMinerMachinePositions.Count && processed < machineLimit; i++)
        {
            if (TryRunAutoMiner(autoMinerMachinePositions[i]))
                processed++;
        }
    }

    private bool TryRunAutoMiner(Vector3Int autoMinerPos)
    {
        if (GetBlockAt(autoMinerPos) != BlockType.AutoMiner)
            return false;

        bool minedAny = false;
        int blockLimit = Mathf.Max(1, autoMinerBlocksPerTick);
        for (int i = 0; i < blockLimit; i++)
        {
            if (!TryFindNextAutoMinerTarget(
                    autoMinerPos,
                    out Vector3Int targetPos,
                    out BlockType targetType,
                    out int nextScanOffset))
            {
                autoMinerScanOffsetByPosition[autoMinerPos] = 0;
                break;
            }

            if (!CanAutoMinerOutputDrop(autoMinerPos, targetType))
                break;

            if (!TryConsumeElectricalEnergy(autoMinerPos, Mathf.Max(0f, autoMinerEnergyPerBlock)))
                break;

            if (!MineAutoMinerTarget(autoMinerPos, targetPos, targetType))
                break;

            autoMinerScanOffsetByPosition[autoMinerPos] = nextScanOffset;
            minedAny = true;
        }

        return minedAny;
    }

    private bool TryFindNextAutoMinerTarget(
        Vector3Int autoMinerPos,
        out Vector3Int targetPos,
        out BlockType targetType,
        out int nextScanOffset)
    {
        targetPos = default;
        targetType = BlockType.Air;
        nextScanOffset = 0;

        int areaSize = GetAutoMinerAreaSize();
        int minY = GetAutoMinerMinimumY();
        int startY = Mathf.Min(autoMinerPos.y - 1, Chunk.SizeY - 1);
        if (startY < minY)
            return false;

        int layerCount = startY - minY + 1;
        int areaCellCount = areaSize * areaSize;
        int totalCellCount = areaCellCount * layerCount;
        if (totalCellCount <= 0)
            return false;

        int scanOffset = 0;
        if (autoMinerScanOffsetByPosition.TryGetValue(autoMinerPos, out int storedOffset))
            scanOffset = PositiveModulo(storedOffset, totalCellCount);

        GetAutoMinerAreaXZ(autoMinerPos, areaSize, out int minX, out int minZ);

        for (int step = 0; step < totalCellCount; step++)
        {
            int candidateOffset = (scanOffset + step) % totalCellCount;
            int layerIndex = candidateOffset / areaCellCount;
            int cellIndex = candidateOffset - layerIndex * areaCellCount;

            int x = minX + cellIndex % areaSize;
            int z = minZ + cellIndex / areaSize;
            int y = startY - layerIndex;

            if (autoMinerMineOnlyLoadedColumns && !IsWorldColumnLoaded(x, z))
                continue;

            Vector3Int candidatePos = new Vector3Int(x, y, z);
            BlockType candidateType;
            if (autoMinerMineOnlyLoadedColumns)
            {
                if (!TryGetLoadedBlockAt(candidatePos, out candidateType))
                    continue;
            }
            else
            {
                candidateType = GetBlockAt(candidatePos);
            }

            if (!CanAutoMinerMineBlock(candidateType))
                continue;

            targetPos = candidatePos;
            targetType = candidateType;
            nextScanOffset = (candidateOffset + 1) % totalCellCount;
            return true;
        }

        return false;
    }

    private bool MineAutoMinerTarget(Vector3Int autoMinerPos, Vector3Int targetPos, BlockType targetType)
    {
        FireAutoMinerLaser(autoMinerPos, targetPos);

        bool deliveredDrop = TryOutputAutoMinerDrop(autoMinerPos, targetType);
        if (!deliveredDrop)
        {
            Debug.LogWarning($"[World] AutoMiner falhou ao gerar drop de {targetType} em {targetPos}.");
            return false;
        }

        SetBlockAt(targetPos, BlockType.Air);
        InvalidateLoadedSubchunkCollidersAt(targetPos);
        return true;
    }

    private bool CanAutoMinerOutputDrop(Vector3Int autoMinerPos, BlockType targetType)
    {
        Vector3Int topPos = autoMinerPos + Vector3Int.up;
        BlockType topType = GetBlockAt(topPos);
        if (topType != BlockType.chest && !TransportTubeUtility.IsTransportTubeNetworkBlock(topType))
            return true;

        if (!TryResolveAutoMinerDrop(targetType, out Item outputItem, out int amount))
            return false;

        if (TransportTubeUtility.IsTransportTubeNetworkBlock(topType))
            return TryFindAutoMinerTransportTubeOutput(topPos, outputItem, amount, out _);

        ChestUIController chestUI = ChestUIController.EnsureInstance();
        return chestUI != null && chestUI.CanInsertItemStackIntoChest(topPos, outputItem, amount);
    }

    private bool TryOutputAutoMinerDrop(Vector3Int autoMinerPos, BlockType targetType)
    {
        if (!TryResolveAutoMinerDrop(targetType, out Item outputItem, out int amount))
            return false;

        int remaining = Mathf.Max(1, amount);
        Vector3Int topPos = autoMinerPos + Vector3Int.up;
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

        if (remaining <= 0)
            return true;

        Vector3 dropPosition = GetAutoMinerDropPosition(autoMinerPos);
        Vector3 throwDirection = Vector3.up * 0.65f;
        return ChestUIController.TrySpawnItemStack(outputItem, remaining, dropPosition, throwDirection);
    }

    private bool TryFindAutoMinerTransportTubeOutput(
        Vector3Int startTubePos,
        Item item,
        int amount,
        out AutoMinerTransportTubeOutput output)
    {
        output = default;
        if (item == null || amount <= 0 || !TransportTubeUtility.IsTransportTubeNetworkBlock(GetBlockAt(startTubePos)))
            return false;

        autoMinerTransportTubeQueue.Clear();
        autoMinerTransportTubeVisited.Clear();

        autoMinerTransportTubeQueue.Enqueue(startTubePos);
        autoMinerTransportTubeVisited.Add(startTubePos);

        bool hasStartFallbackOutput = false;
        AutoMinerTransportTubeOutput startFallbackOutput = default;
        int inspectedCount = 0;
        int searchLimit = Mathf.Max(16, autoMinerTransportTubeSearchLimit);

        while (autoMinerTransportTubeQueue.Count > 0 && inspectedCount < searchLimit)
        {
            Vector3Int tubePos = autoMinerTransportTubeQueue.Dequeue();
            inspectedCount++;

            bool isStartTube = tubePos == startTubePos;
            Vector3Int blockedOutputDirection = isStartTube ? Vector3Int.down : Vector3Int.zero;
            if (TryResolveAutoMinerTransportTubeOutputAt(
                    tubePos,
                    blockedOutputDirection,
                    item,
                    amount,
                    out AutoMinerTransportTubeOutput candidateOutput))
            {
                if (!isStartTube)
                {
                    output = candidateOutput;
                    return true;
                }

                if (!hasStartFallbackOutput)
                {
                    startFallbackOutput = candidateOutput;
                    hasStartFallbackOutput = true;
                }
            }

            QueueConnectedAutoMinerTransportTubes(tubePos);
        }

        if (!hasStartFallbackOutput)
            return false;

        output = startFallbackOutput;
        return true;
    }

    private void QueueConnectedAutoMinerTransportTubes(Vector3Int tubePos)
    {
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.left);
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.right);
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.back);
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.forward);
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.down);
        TryQueueConnectedAutoMinerTransportTube(tubePos, Vector3Int.up);
    }

    private void TryQueueConnectedAutoMinerTransportTube(Vector3Int tubePos, Vector3Int direction)
    {
        Vector3Int neighborPos = tubePos + direction;
        if (autoMinerTransportTubeVisited.Contains(neighborPos) ||
            !TransportTubeUtility.IsTransportTubeNetworkBlock(GetBlockAt(neighborPos)))
        {
            return;
        }

        autoMinerTransportTubeVisited.Add(neighborPos);
        autoMinerTransportTubeQueue.Enqueue(neighborPos);
    }

    private bool TryResolveAutoMinerTransportTubeOutputAt(
        Vector3Int tubePos,
        Vector3Int blockedOutputDirection,
        Item item,
        int amount,
        out AutoMinerTransportTubeOutput output)
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
            return TryResolveAutoMinerTransportTubeFallbackOutput(
                tubePos,
                fallbackAxis,
                blockedOutputDirection,
                item,
                amount,
                out output);
        }

        if (connectionCount != 1)
            return false;

        Vector3Int connectedDirection = GetSingleAutoMinerTransportTubeConnectionDirection(connectionMask);
        Vector3Int outputDirection = -connectedDirection;
        return TryBuildAutoMinerTransportTubeOutput(
            tubePos,
            outputDirection,
            blockedOutputDirection,
            item,
            amount,
            out output);
    }

    private bool TryResolveAutoMinerTransportTubeFallbackOutput(
        Vector3Int tubePos,
        BlockPlacementAxis fallbackAxis,
        Vector3Int blockedOutputDirection,
        Item item,
        int amount,
        out AutoMinerTransportTubeOutput output)
    {
        output = default;
        fallbackAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(fallbackAxis);

        if (fallbackAxis == BlockPlacementAxis.YNegative)
        {
            return TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.up, blockedOutputDirection, item, amount, out output) ||
                   TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.down, blockedOutputDirection, item, amount, out output);
        }

        if (fallbackAxis == BlockPlacementAxis.X || fallbackAxis == BlockPlacementAxis.XNegative)
        {
            return TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.right, blockedOutputDirection, item, amount, out output) ||
                   TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.left, blockedOutputDirection, item, amount, out output);
        }

        return TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.forward, blockedOutputDirection, item, amount, out output) ||
               TryBuildAutoMinerTransportTubeOutput(tubePos, Vector3Int.back, blockedOutputDirection, item, amount, out output);
    }

    private bool TryBuildAutoMinerTransportTubeOutput(
        Vector3Int tubePos,
        Vector3Int outputDirection,
        Vector3Int blockedOutputDirection,
        Item item,
        int amount,
        out AutoMinerTransportTubeOutput output)
    {
        output = default;
        if (outputDirection == Vector3Int.zero || outputDirection == blockedOutputDirection)
            return false;

        Vector3Int targetPos = tubePos + outputDirection;
        BlockType targetType = GetBlockAt(targetPos);
        if (targetType == BlockType.chest)
        {
            ChestUIController chestUI = ChestUIController.EnsureInstance();
            if (chestUI == null || !chestUI.CanInsertItemStackIntoChest(targetPos, item, amount))
                return false;

            output = new AutoMinerTransportTubeOutput
            {
                tubePos = tubePos,
                exitDirection = outputDirection,
                targetPos = targetPos,
                dropPosition = GetAutoMinerTransportTubeDropPosition(tubePos, targetPos, outputDirection, targetType),
                insertIntoChest = true
            };
            return true;
        }

        if (!CanAutoMinerTransportTubeDropInto(targetType))
            return false;

        output = new AutoMinerTransportTubeOutput
        {
            tubePos = tubePos,
            exitDirection = outputDirection,
            targetPos = targetPos,
            dropPosition = GetAutoMinerTransportTubeDropPosition(tubePos, targetPos, outputDirection, targetType),
            insertIntoChest = false
        };
        return true;
    }

    private bool TryDeliverAutoMinerTransportTubeOutput(AutoMinerTransportTubeOutput output, Item item, int amount)
    {
        int remaining = Mathf.Max(1, amount);
        if (output.insertIntoChest)
        {
            ChestUIController chestUI = ChestUIController.EnsureInstance();
            if (chestUI != null)
                remaining = chestUI.InsertItemStackIntoChest(output.targetPos, item, remaining);

            return remaining <= 0;
        }

        Vector3 throwDirection = GetAutoMinerTransportTubeThrowDirection(output.exitDirection);
        return ChestUIController.TrySpawnItemStack(item, remaining, output.dropPosition, throwDirection);
    }

    private bool CanAutoMinerTransportTubeDropInto(BlockType targetType)
    {
        if (targetType == BlockType.Air ||
            FluidBlockUtility.IsWater(targetType) ||
            ConveyorBeltUtility.IsConveyorBlock(targetType))
        {
            return true;
        }

        return !TransportTubeUtility.IsTransportTubeNetworkBlock(targetType) && !IsSolidBlock(targetType);
    }

    private Vector3 GetAutoMinerTransportTubeDropPosition(
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
               direction.normalized * (0.5f + Mathf.Max(0f, autoMinerTransportTubeExitOffset));
    }

    private static Vector3 GetAutoMinerTransportTubeThrowDirection(Vector3Int outputDirection)
    {
        Vector3 direction = new Vector3(outputDirection.x, outputDirection.y, outputDirection.z);
        if (direction.sqrMagnitude <= 0.0001f)
            return Vector3.up * 0.65f;

        Vector3 throwDirection = direction.normalized * 0.9f + Vector3.up * 0.35f;
        return throwDirection.sqrMagnitude > 0.0001f ? throwDirection : Vector3.up * 0.65f;
    }

    private static Vector3Int GetSingleAutoMinerTransportTubeConnectionDirection(byte connectionMask)
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

    private bool TryResolveAutoMinerDrop(BlockType targetType, out Item outputItem, out int amount)
    {
        outputItem = null;
        amount = 0;

        if (!BlockBreakDropResolver.TryResolveDrop(
                this,
                targetType,
                out Item dropItem,
                out BlockType dropBlockType,
                out int resolvedAmount))
        {
            return false;
        }

        if (!TryResolveAutoMinerDropItem(dropItem, dropBlockType, out outputItem))
            return false;

        amount = Mathf.Max(1, resolvedAmount);
        return true;
    }

    private static bool TryResolveAutoMinerDropItem(Item dropItem, BlockType dropBlockType, out Item outputItem)
    {
        if (dropItem != null)
        {
            outputItem = dropItem;
            return true;
        }

        return BlockItemCatalog.TryGetItemForBlock(dropBlockType, out outputItem);
    }

    private Vector3 GetAutoMinerDropPosition(Vector3Int autoMinerPos)
    {
        Vector3Int topPos = autoMinerPos + Vector3Int.up;
        if (ConveyorBeltUtility.IsConveyorBlock(GetBlockAt(topPos)))
        {
            return topPos + new Vector3(
                0.5f,
                Mathf.Max(0f, autoMinerDropConveyorTopOffset),
                0.5f);
        }

        if (GetBlockAt(topPos) == BlockType.chest)
        {
            return topPos + new Vector3(
                0.5f,
                1f + Mathf.Max(0f, autoMinerDropTopOffset),
                0.5f);
        }

        return autoMinerPos + new Vector3(
            0.5f,
            1f + Mathf.Max(0f, autoMinerDropTopOffset),
            0.5f);
    }

    private bool CanAutoMinerMineBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air ||
            blockType == BlockType.Bedrock ||
            FluidBlockUtility.IsWater(blockType))
        {
            return false;
        }

        if (MachineBlockUtility.IsMachineBlock(blockType) ||
            LeverUtility.IsLeverBlock(blockType) ||
            blockType == BlockType.wire ||
            blockType == BlockType.chest ||
            blockType == BlockType.Crafter ||
            blockType == BlockType.StoneFurnance ||
            blockType == BlockType.Anvil)
        {
            return false;
        }

        if (blockData != null)
        {
            BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
            if (mappingResult != null)
            {
                BlockTextureMapping mapping = mappingResult.Value;
                if (mapping.isEmpty || mapping.isLiquid || mapping.minecraftHardness < 0f)
                    return false;
            }
        }

        return true;
    }

    private void CleanupAutoMinerMachineState()
    {
        autoMinerStateCleanupBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, int> pair in autoMinerScanOffsetByPosition)
        {
            if (GetBlockAt(pair.Key) != BlockType.AutoMiner)
                autoMinerStateCleanupBuffer.Add(pair.Key);
        }

        for (int i = 0; i < autoMinerStateCleanupBuffer.Count; i++)
            autoMinerScanOffsetByPosition.Remove(autoMinerStateCleanupBuffer[i]);
    }

    private int GetAutoMinerAreaSize()
    {
        return Mathf.Max(1, autoMinerAreaSize);
    }

    private int GetAutoMinerMinimumY()
    {
        return Mathf.Clamp(autoMinerMinimumY, 3, Chunk.SizeY - 1);
    }

    private static void GetAutoMinerAreaXZ(Vector3Int autoMinerPos, int areaSize, out int minX, out int minZ)
    {
        int halfSize = areaSize / 2;
        minX = autoMinerPos.x - halfSize;
        minZ = autoMinerPos.z - halfSize;
    }

    private static int PositiveModulo(int value, int modulo)
    {
        if (modulo <= 0)
            return 0;

        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private void SyncAutoMinerAreaVisuals()
    {
        if (!showAutoMinerMiningArea)
        {
            ClearAutoMinerAreaLineInstances();
            return;
        }

        autoMinerTouchedAreaLinePositions.Clear();
        for (int i = 0; i < autoMinerMachinePositions.Count; i++)
        {
            Vector3Int autoMinerPos = autoMinerMachinePositions[i];
            if (GetBlockAt(autoMinerPos) != BlockType.AutoMiner)
                continue;

            autoMinerTouchedAreaLinePositions.Add(autoMinerPos);
            LineRenderer line = GetOrCreateAutoMinerAreaLine(autoMinerPos);
            UpdateAutoMinerAreaLine(line, autoMinerPos);
        }

        RemoveUntouchedAutoMinerAreaLines();
    }

    private void UpdateAutoMinerLaserVisuals()
    {
        if (autoMinerLaserVisualsByPosition.Count == 0)
            return;

        float now = Time.time;
        autoMinerLaserRemovalBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, AutoMinerLaserVisual> pair in autoMinerLaserVisualsByPosition)
        {
            AutoMinerLaserVisual visual = pair.Value;
            if (visual == null || visual.line == null || now >= visual.hideTime || GetBlockAt(pair.Key) != BlockType.AutoMiner)
                autoMinerLaserRemovalBuffer.Add(pair.Key);
        }

        for (int i = 0; i < autoMinerLaserRemovalBuffer.Count; i++)
        {
            Vector3Int autoMinerPos = autoMinerLaserRemovalBuffer[i];
            if (autoMinerLaserVisualsByPosition.TryGetValue(autoMinerPos, out AutoMinerLaserVisual visual))
                DestroyAutoMinerLaserVisual(visual);

            autoMinerLaserVisualsByPosition.Remove(autoMinerPos);
        }

        if (autoMinerLaserLinesRoot != null && autoMinerLaserLinesRoot.childCount == 0)
        {
            DestroyAutoMinerRuntimeObject(autoMinerLaserLinesRoot.gameObject);
            autoMinerLaserLinesRoot = null;
        }
    }

    private void FireAutoMinerLaser(Vector3Int autoMinerPos, Vector3Int targetPos)
    {
        if (!showAutoMinerMiningLaser)
            return;

        AutoMinerLaserVisual visual = GetOrCreateAutoMinerLaserVisual(autoMinerPos);
        if (visual == null || visual.line == null)
            return;

        LineRenderer line = visual.line;
        GetAutoMinerLaserLineStyle().Configure(
            line,
            "Runtime AutoMiner Laser Line Material",
            loop: false);

        Vector3 start = autoMinerPos + new Vector3(0.5f, 0f, 0.5f);
        Vector3 end = targetPos + Vector3.one * 0.5f;
        line.positionCount = 2;
        line.SetPosition(0, start);
        line.SetPosition(1, end);
        line.gameObject.SetActive(true);
        visual.hideTime = Time.time + Mathf.Max(0.02f, autoMinerLaserDurationSeconds);
    }

    private AutoMinerLaserVisual GetOrCreateAutoMinerLaserVisual(Vector3Int autoMinerPos)
    {
        if (autoMinerLaserVisualsByPosition.TryGetValue(autoMinerPos, out AutoMinerLaserVisual existing) &&
            existing != null &&
            existing.line != null)
        {
            return existing;
        }

        GameObject lineObject = new GameObject(
            $"AutoMinerLaser_{autoMinerPos.x}_{autoMinerPos.y}_{autoMinerPos.z}");
        lineObject.transform.SetParent(EnsureAutoMinerLaserLinesRoot(), false);

        AutoMinerLaserVisual visual = new AutoMinerLaserVisual
        {
            line = lineObject.AddComponent<LineRenderer>(),
            hideTime = 0f
        };
        autoMinerLaserVisualsByPosition[autoMinerPos] = visual;
        return visual;
    }

    private Transform EnsureAutoMinerLaserLinesRoot()
    {
        if (autoMinerLaserLinesRoot != null)
            return autoMinerLaserLinesRoot;

        Transform existing = transform.Find("AutoMinerLaserLines");
        if (existing != null)
        {
            autoMinerLaserLinesRoot = existing;
            return autoMinerLaserLinesRoot;
        }

        GameObject rootObject = new GameObject("AutoMinerLaserLines");
        rootObject.transform.SetParent(transform, false);
        rootObject.transform.localPosition = Vector3.zero;
        rootObject.transform.localRotation = Quaternion.identity;
        rootObject.transform.localScale = Vector3.one;
        autoMinerLaserLinesRoot = rootObject.transform;
        return autoMinerLaserLinesRoot;
    }

    private LineRenderer GetOrCreateAutoMinerAreaLine(Vector3Int autoMinerPos)
    {
        if (autoMinerAreaLinesByPosition.TryGetValue(autoMinerPos, out LineRenderer existing) &&
            existing != null)
        {
            return existing;
        }

        GameObject lineObject = new GameObject(
            $"AutoMinerArea_{autoMinerPos.x}_{autoMinerPos.y}_{autoMinerPos.z}");
        lineObject.transform.SetParent(EnsureAutoMinerAreaLinesRoot(), false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        ConfigureAutoMinerAreaLineRenderer(line);
        autoMinerAreaLinesByPosition[autoMinerPos] = line;
        return line;
    }

    private Transform EnsureAutoMinerAreaLinesRoot()
    {
        if (autoMinerAreaLinesRoot != null)
            return autoMinerAreaLinesRoot;

        Transform existing = transform.Find("AutoMinerAreaLines");
        if (existing != null)
        {
            autoMinerAreaLinesRoot = existing;
            return autoMinerAreaLinesRoot;
        }

        GameObject rootObject = new GameObject("AutoMinerAreaLines");
        rootObject.transform.SetParent(transform, false);
        rootObject.transform.localPosition = Vector3.zero;
        rootObject.transform.localRotation = Quaternion.identity;
        rootObject.transform.localScale = Vector3.one;
        autoMinerAreaLinesRoot = rootObject.transform;
        return autoMinerAreaLinesRoot;
    }

    private void ConfigureAutoMinerAreaLineRenderer(LineRenderer line)
    {
        if (line == null)
            return;

        GetAutoMinerAreaLineStyle().Configure(
            line,
            "Runtime AutoMiner Area Line Material",
            loop: false);
    }

    private VoxelLineRendererStyle GetAutoMinerAreaLineStyle()
    {
        if (autoMinerAreaLineStyle == null)
        {
            autoMinerAreaLineStyle = new VoxelLineRendererStyle
            {
                color = autoMinerAreaLineColor,
                width = Mathf.Max(0.001f, autoMinerAreaLineWidth),
                capVertices = 4,
                cornerVertices = 4
            };
        }

        return autoMinerAreaLineStyle;
    }

    private VoxelLineRendererStyle GetAutoMinerLaserLineStyle()
    {
        if (autoMinerLaserLineStyle == null)
        {
            autoMinerLaserLineStyle = new VoxelLineRendererStyle
            {
                color = autoMinerLaserColor,
                width = Mathf.Max(0.001f, autoMinerLaserWidth),
                capVertices = 4,
                cornerVertices = 4,
                enableEmission = true,
                emissionColor = autoMinerLaserColor,
                emissionIntensity = 1.6f
            };
        }

        return autoMinerLaserLineStyle;
    }

    private void UpdateAutoMinerAreaLine(LineRenderer line, Vector3Int autoMinerPos)
    {
        if (line == null)
            return;

        ConfigureAutoMinerAreaLineRenderer(line);

        int areaSize = GetAutoMinerAreaSize();
        GetAutoMinerAreaXZ(autoMinerPos, areaSize, out int minX, out int minZ);

        float topY = autoMinerPos.y + Mathf.Max(0f, autoMinerAreaLineTopOffset);
        float bottomY = GetAutoMinerMinimumY();
        float maxX = minX + areaSize;
        float maxZ = minZ + areaSize;

        Vector3 bottomA = new Vector3(minX, bottomY, minZ);
        Vector3 bottomB = new Vector3(maxX, bottomY, minZ);
        Vector3 bottomC = new Vector3(maxX, bottomY, maxZ);
        Vector3 bottomD = new Vector3(minX, bottomY, maxZ);
        Vector3 topA = new Vector3(minX, topY, minZ);
        Vector3 topB = new Vector3(maxX, topY, minZ);
        Vector3 topC = new Vector3(maxX, topY, maxZ);
        Vector3 topD = new Vector3(minX, topY, maxZ);

        line.positionCount = AutoMinerWireframePositionCount;
        line.SetPosition(0, bottomA);
        line.SetPosition(1, bottomB);
        line.SetPosition(2, bottomC);
        line.SetPosition(3, bottomD);
        line.SetPosition(4, bottomA);
        line.SetPosition(5, topA);
        line.SetPosition(6, topB);
        line.SetPosition(7, bottomB);
        line.SetPosition(8, topB);
        line.SetPosition(9, topC);
        line.SetPosition(10, bottomC);
        line.SetPosition(11, topC);
        line.SetPosition(12, topD);
        line.SetPosition(13, bottomD);
        line.SetPosition(14, topD);
        line.SetPosition(15, topA);
    }

    private void RemoveUntouchedAutoMinerAreaLines()
    {
        autoMinerAreaLineRemovalBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, LineRenderer> pair in autoMinerAreaLinesByPosition)
        {
            if (!autoMinerTouchedAreaLinePositions.Contains(pair.Key))
                autoMinerAreaLineRemovalBuffer.Add(pair.Key);
        }

        for (int i = 0; i < autoMinerAreaLineRemovalBuffer.Count; i++)
        {
            Vector3Int position = autoMinerAreaLineRemovalBuffer[i];
            if (autoMinerAreaLinesByPosition.TryGetValue(position, out LineRenderer line))
                DestroyAutoMinerAreaLine(line);

            autoMinerAreaLinesByPosition.Remove(position);
        }
    }

    private void ClearAutoMinerAreaLineInstances()
    {
        foreach (KeyValuePair<Vector3Int, LineRenderer> pair in autoMinerAreaLinesByPosition)
            DestroyAutoMinerAreaLine(pair.Value);

        autoMinerAreaLinesByPosition.Clear();
        autoMinerTouchedAreaLinePositions.Clear();
        autoMinerAreaLineRemovalBuffer.Clear();

        if (autoMinerAreaLinesRoot != null)
        {
            DestroyAutoMinerRuntimeObject(autoMinerAreaLinesRoot.gameObject);
            autoMinerAreaLinesRoot = null;
        }
    }

    private void ClearAutoMinerLaserLineInstances()
    {
        foreach (KeyValuePair<Vector3Int, AutoMinerLaserVisual> pair in autoMinerLaserVisualsByPosition)
            DestroyAutoMinerLaserVisual(pair.Value);

        autoMinerLaserVisualsByPosition.Clear();
        autoMinerLaserRemovalBuffer.Clear();

        if (autoMinerLaserLinesRoot != null)
        {
            DestroyAutoMinerRuntimeObject(autoMinerLaserLinesRoot.gameObject);
            autoMinerLaserLinesRoot = null;
        }
    }

    private static void DestroyAutoMinerAreaLine(LineRenderer line)
    {
        if (line == null)
            return;

        DestroyAutoMinerRuntimeObject(line.gameObject);
    }

    private static void DestroyAutoMinerLaserVisual(AutoMinerLaserVisual visual)
    {
        if (visual == null || visual.line == null)
            return;

        DestroyAutoMinerRuntimeObject(visual.line.gameObject);
    }

    private void DestroyAutoMinerAreaVisuals()
    {
        ClearAutoMinerAreaLineInstances();
        ClearAutoMinerLaserLineInstances();

        if (autoMinerAreaLinesRoot != null)
        {
            DestroyAutoMinerRuntimeObject(autoMinerAreaLinesRoot.gameObject);
            autoMinerAreaLinesRoot = null;
        }

        if (autoMinerLaserLinesRoot != null)
        {
            DestroyAutoMinerRuntimeObject(autoMinerLaserLinesRoot.gameObject);
            autoMinerLaserLinesRoot = null;
        }

        autoMinerAreaLineStyle?.DestroyRuntimeMaterial();
        autoMinerLaserLineStyle?.DestroyRuntimeMaterial();
    }

    private static void DestroyAutoMinerRuntimeObject(Object target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(target);
        else
            Object.DestroyImmediate(target);
    }

    #endregion
}
