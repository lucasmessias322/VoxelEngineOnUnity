using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public partial class World
{
    private readonly HashSet<Vector3Int> electricalBlockPositions = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> electricalBuildVisited = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Queue<Vector3Int> electricalBuildQueue = new Queue<Vector3Int>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, ElectricalNetworkRuntime> electricalNetworkByPosition = new Dictionary<Vector3Int, ElectricalNetworkRuntime>(InitialBlockEditCapacity);
    private readonly List<ElectricalNetworkRuntime> electricalNetworks = new List<ElectricalNetworkRuntime>(64);
    private readonly Dictionary<Vector3Int, float> batteryEnergyByPosition = new Dictionary<Vector3Int, float>(InitialBlockEditCapacity);
    private readonly List<Vector3Int> electricityCleanupBuffer = new List<Vector3Int>(128);
    private readonly List<EletricWireConnectionSnapshot> eletricConnectorConnectionBuffer = new List<EletricWireConnectionSnapshot>(64);
    private readonly Dictionary<Vector3Int, List<Vector3Int>> electricalExtraEdges = new Dictionary<Vector3Int, List<Vector3Int>>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, float> electricalDirectEnergyByNetworkKey = new Dictionary<Vector3Int, float>(64);
    private readonly HashSet<Vector3Int> poweredElectricalBlockPositions = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, ushort> poweredElectricalEmissionByPosition = new Dictionary<Vector3Int, ushort>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> electricalPoweredThisTick = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly List<Vector3Int> poweredElectricalRemovalBuffer = new List<Vector3Int>(128);
    private readonly List<Vector3Int> poweredElectricalApplyBuffer = new List<Vector3Int>(128);
    private readonly List<ElectricalConsumerDemand> electricalConsumerDemandBuffer = new List<ElectricalConsumerDemand>(128);
    private readonly Dictionary<Vector3Int, byte> electricalTextureVisualStateByPosition = new Dictionary<Vector3Int, byte>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector2Int, Dictionary<Vector3Int, byte>> electricalTextureVisualStateByChunk = new Dictionary<Vector2Int, Dictionary<Vector3Int, byte>>(64);
    private readonly List<Vector3Int> electricalTextureVisualCleanupBuffer = new List<Vector3Int>(128);

    private bool electricalNetworksDirty = true;
    private float lastElectricityTickTime;
    private float nextElectricityTickTime;
    private VoxelProceduralSkyController cachedElectricitySkyController;

    private struct ElectricalPoweredBlockConfig
    {
        public float energyPerSecond;
        public ushort poweredEmission;
    }

    private struct ElectricalConsumerDemand
    {
        public Vector3Int position;
        public float energyRequired;
    }

    private sealed class ElectricalNetworkRuntime
    {
        public int blockCount;
        public int solarPanelCount;
        public Vector3Int key;
        public float directEnergy;
        public float directCapacity;
        public readonly List<Vector3Int> batteryPositions = new List<Vector3Int>(8);
        public readonly List<Vector3Int> windMillPositions = new List<Vector3Int>(8);
        public readonly List<Vector3Int> poweredBlockPositions = new List<Vector3Int>(8);
        public int consumerCursor;
    }

    #region Electricity

    public bool HasElectricalEnergy(Vector3Int consumerPos, float amount)
    {
        if (!enableElectricitySystem)
            return true;

        amount = Mathf.Max(0f, amount);
        EnsureElectricitySimulationReady();

        if (!electricalNetworkByPosition.TryGetValue(consumerPos, out ElectricalNetworkRuntime network))
            return false;

        if (amount <= 0f)
            return true;

        return GetAvailableElectricalEnergy(network) + 0.0001f >= amount;
    }

    public bool TryConsumeElectricalEnergy(Vector3Int consumerPos, float amount)
    {
        if (!enableElectricitySystem)
            return true;

        amount = Mathf.Max(0f, amount);
        EnsureElectricitySimulationReady();

        if (!electricalNetworkByPosition.TryGetValue(consumerPos, out ElectricalNetworkRuntime network))
            return false;

        if (amount <= 0f)
            return true;

        if (GetAvailableElectricalEnergy(network) + 0.0001f < amount)
            return false;

        ConsumeElectricalEnergy(network, amount);
        BalanceElectricalBatteries(network);
        SyncElectricalBatteryVisualStates(network);
        return true;
    }

    public float GetBatteryElectricalEnergy(Vector3Int batteryPos)
    {
        if (!BatteryBlockUtility.IsBatteryBlock(GetBlockAt(batteryPos)))
            return 0f;

        return batteryEnergyByPosition.TryGetValue(batteryPos, out float energy)
            ? Mathf.Clamp(energy, 0f, GetBatteryCapacity())
            : 0f;
    }

    public float GetBatteryElectricalCharge01(Vector3Int batteryPos)
    {
        return GetBatteryCapacity() > 0f
            ? Mathf.Clamp01(GetBatteryElectricalEnergy(batteryPos) / GetBatteryCapacity())
            : 0f;
    }

    public bool IsElectricalEndpointPowered(Vector3Int worldPos)
    {
        if (!enableElectricitySystem)
            return false;

        EnsureElectricitySimulationReady();
        return poweredElectricalBlockPositions.Contains(worldPos);
    }

    public void NotifyElectricConnectorConnectionsChanged()
    {
        electricalNetworksDirty = true;
    }

    private void ProcessElectricitySimulation()
    {
        if (!enableElectricitySystem)
        {
            ClearPoweredElectricalBlockEffects();
            return;
        }

        float now = Time.time;
        if (!electricalNetworksDirty && now < nextElectricityTickTime)
            return;

        EnsureElectricityNetworksBuilt();

        float tickInterval = Mathf.Max(0.05f, electricityTickInterval);
        float deltaTime = lastElectricityTickTime > 0f
            ? Mathf.Clamp(now - lastElectricityTickTime, 0f, tickInterval * 4f)
            : tickInterval;

        lastElectricityTickTime = now;
        nextElectricityTickTime = now + tickInterval;

        if (deltaTime <= 0f)
            return;

        TickElectricalNetworks(deltaTime);
    }

    private void EnsureElectricitySimulationReady()
    {
        if (!enableElectricitySystem)
            return;

        EnsureElectricityNetworksBuilt();

        if (lastElectricityTickTime <= 0f || Time.time >= nextElectricityTickTime)
            ProcessElectricitySimulation();
    }

    private void EnsureElectricityNetworksBuilt()
    {
        if (!electricalNetworksDirty)
            return;

        CaptureCurrentElectricalNetworkBuffers();
        electricalBlockPositions.Clear();
        electricalNetworkByPosition.Clear();
        electricalNetworks.Clear();
        electricalBuildVisited.Clear();
        electricalExtraEdges.Clear();

        foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
        {
            if (IsElectricalBlock(pair.Value))
                electricalBlockPositions.Add(pair.Key);
        }

        CleanupBatteryEnergyStorage();
        BuildElectricConnectorExtraEdges();
        BuildWireGeometryExtraEdges();

        foreach (Vector3Int position in electricalBlockPositions)
        {
            if (!electricalBuildVisited.Contains(position))
                BuildElectricalNetwork(position);
        }

        RefreshElectricalNetworkBufferLookup();
        electricalNetworksDirty = false;
    }

    private void CaptureCurrentElectricalNetworkBuffers()
    {
        electricalDirectEnergyByNetworkKey.Clear();
        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network != null)
                electricalDirectEnergyByNetworkKey[network.key] = network.directEnergy;
        }
    }

    private void RefreshElectricalNetworkBufferLookup()
    {
        electricalDirectEnergyByNetworkKey.Clear();
        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network != null)
                electricalDirectEnergyByNetworkKey[network.key] = network.directEnergy;
        }
    }

    private void BuildElectricalNetwork(Vector3Int start)
    {
        ElectricalNetworkRuntime network = new ElectricalNetworkRuntime
        {
            key = start
        };

        electricalBuildVisited.Add(start);
        electricalBuildQueue.Enqueue(start);

        while (electricalBuildQueue.Count > 0)
        {
            Vector3Int position = electricalBuildQueue.Dequeue();
            BlockType blockType = GetBlockAt(position);

            network.blockCount++;
            if (CompareElectricalPositions(position, network.key) < 0)
                network.key = position;

            electricalNetworkByPosition[position] = network;

            if (blockType == BlockType.SolarPanel)
                network.solarPanelCount++;
            else if (blockType == BlockType.windmill)
                network.windMillPositions.Add(position);
            else if (BatteryBlockUtility.IsBatteryBlock(blockType))
                network.batteryPositions.Add(position);

            if (TryGetElectricalPoweredBlockConfig(blockType, out _))
                network.poweredBlockPositions.Add(position);

            for (int i = 0; i < sixDirections.Length; i++)
            {
                Vector3Int neighbor = position + sixDirections[i];
                if (!electricalBlockPositions.Contains(neighbor) || electricalBuildVisited.Contains(neighbor))
                    continue;

                BlockType neighborType = GetBlockAt(neighbor);
                if (!AreAdjacentElectricalBlocksConnected(blockType, neighborType))
                    continue;

                electricalBuildVisited.Add(neighbor);
                electricalBuildQueue.Enqueue(neighbor);
            }

            if (!electricalExtraEdges.TryGetValue(position, out List<Vector3Int> linkedPositions))
                continue;

            for (int i = 0; i < linkedPositions.Count; i++)
            {
                Vector3Int linked = linkedPositions[i];
                if (!electricalBlockPositions.Contains(linked) || electricalBuildVisited.Contains(linked))
                    continue;

                electricalBuildVisited.Add(linked);
                electricalBuildQueue.Enqueue(linked);
            }
        }

        network.directCapacity = ResolveElectricalDirectCapacity(network);
        if (electricalDirectEnergyByNetworkKey.TryGetValue(network.key, out float storedDirectEnergy))
            network.directEnergy = Mathf.Clamp(storedDirectEnergy, 0f, network.directCapacity);

        electricalNetworks.Add(network);
    }

    private void BuildElectricConnectorExtraEdges()
    {
        eletricConnectorConnectionBuffer.Clear();
        EletricConnectorWireSystem.CopyActiveConnections(eletricConnectorConnectionBuffer);

        for (int i = 0; i < eletricConnectorConnectionBuffer.Count; i++)
        {
            EletricWireConnectionSnapshot connection = eletricConnectorConnectionBuffer[i];
            if (!electricalBlockPositions.Contains(connection.Start) ||
                !electricalBlockPositions.Contains(connection.End))
            {
                continue;
            }

            AddElectricalExtraEdge(connection.Start, connection.End);
            AddElectricalExtraEdge(connection.End, connection.Start);
        }
    }

    private void BuildWireGeometryExtraEdges()
    {
        foreach (Vector3Int position in electricalBlockPositions)
        {
            if (GetBlockAt(position) != BlockType.wire)
                continue;

            AddTopWireGeometryExtraEdges(position);
            AddWallWireGeometryExtraEdges(position);
        }
    }

    private void AddTopWireGeometryExtraEdges(Vector3Int wirePos)
    {
        if (!TryGetElectricalWireState(
                wirePos,
                out bool hasTop,
                out _,
                out _)
            || !hasTop)
        {
            return;
        }

        AddTopWireStepExtraEdge(wirePos, Vector3Int.right);
        AddTopWireStepExtraEdge(wirePos, Vector3Int.left);
        AddTopWireStepExtraEdge(wirePos, Vector3Int.forward);
        AddTopWireStepExtraEdge(wirePos, Vector3Int.back);
    }

    private void AddTopWireStepExtraEdge(Vector3Int wirePos, Vector3Int horizontalDirection)
    {
        Vector3Int neighbor = wirePos + horizontalDirection;
        if (TryResolveElectricalTopWireOrEndpoint(neighbor, out Vector3Int sameLevelTarget))
        {
            AddElectricalExtraEdge(wirePos, sameLevelTarget);
            AddElectricalExtraEdge(sameLevelTarget, wirePos);
            return;
        }

        bool adjacentIsSolid = IsWireSupportBlock(neighbor);
        Vector3Int steppedTarget = adjacentIsSolid
            ? neighbor + Vector3Int.up
            : neighbor + Vector3Int.down;

        if (!TryResolveElectricalTopWireOrEndpoint(steppedTarget, out Vector3Int resolvedTarget))
            return;

        AddElectricalExtraEdge(wirePos, resolvedTarget);
        AddElectricalExtraEdge(resolvedTarget, wirePos);
    }

    private void AddWallWireGeometryExtraEdges(Vector3Int wirePos)
    {
        if (!TryGetElectricalWireState(
                wirePos,
                out _,
                out BlockPlacementAxis wallSurfaceAxis,
                out int wallAttachmentSide)
            || wallSurfaceAxis == BlockPlacementAxis.Y ||
               wallAttachmentSide == 0)
        {
            return;
        }

        Vector3Int supportOffset = GetWireAttachmentOffset(wallSurfaceAxis, wallAttachmentSide);
        Vector3Int supportPos = wirePos + supportOffset;
        if (TryResolveElectricalEndpointAt(supportPos, out Vector3Int supportEndpoint))
        {
            AddElectricalExtraEdge(wirePos, supportEndpoint);
            AddElectricalExtraEdge(supportEndpoint, wirePos);
        }

        AddWallWireEndpointContinuationEdges(wirePos, wallSurfaceAxis, supportOffset);

        Vector3Int supportTop = wirePos + Vector3Int.up + supportOffset;
        if (!TryResolveElectricalTopWireOrEndpoint(supportTop, out Vector3Int supportTopTarget))
            return;

        AddElectricalExtraEdge(wirePos, supportTopTarget);
        AddElectricalExtraEdge(supportTopTarget, wirePos);
    }

    private void AddWallWireEndpointContinuationEdges(
        Vector3Int wirePos,
        BlockPlacementAxis wallSurfaceAxis,
        Vector3Int supportOffset)
    {
        AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.up, supportOffset);
        AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.down, supportOffset);

        if (wallSurfaceAxis == BlockPlacementAxis.X)
        {
            AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.forward, supportOffset);
            AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.back, supportOffset);
        }
        else if (wallSurfaceAxis == BlockPlacementAxis.Z)
        {
            AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.right, supportOffset);
            AddWallWireEndpointContinuationEdge(wirePos, Vector3Int.left, supportOffset);
        }
    }

    private void AddWallWireEndpointContinuationEdge(
        Vector3Int wirePos,
        Vector3Int surfaceStep,
        Vector3Int supportOffset)
    {
        Vector3Int endpointPos = wirePos + surfaceStep + supportOffset;
        if (!TryResolveElectricalEndpointAt(endpointPos, out Vector3Int endpointTarget))
            return;

        AddElectricalExtraEdge(wirePos, endpointTarget);
        AddElectricalExtraEdge(endpointTarget, wirePos);
    }

    private void AddElectricalExtraEdge(Vector3Int from, Vector3Int to)
    {
        if (!electricalExtraEdges.TryGetValue(from, out List<Vector3Int> edges))
        {
            edges = new List<Vector3Int>(2);
            electricalExtraEdges[from] = edges;
        }

        if (!edges.Contains(to))
            edges.Add(to);
    }

    private bool TryGetElectricalWireState(
        Vector3Int wirePos,
        out bool hasTop,
        out BlockPlacementAxis wallSurfaceAxis,
        out int wallAttachmentSide)
    {
        hasTop = false;
        wallSurfaceAxis = BlockPlacementAxis.Y;
        wallAttachmentSide = 0;

        if (GetBlockAt(wirePos) != BlockType.wire)
            return false;

        byte rawValue = blockPlacementAxes.TryGetValue(wirePos, out BlockPlacementAxis storedAxis)
            ? (byte)storedAxis
            : (byte)BlockPlacementAxis.Y;
        rawValue = ResolveStoredWirePlacementRaw(wirePos, rawValue);

        hasTop = WirePlacementUtility.HasTop(rawValue);
        if (!WirePlacementUtility.TryGetWall(rawValue, out wallSurfaceAxis, out wallAttachmentSide))
            return true;

        if (HasWireSupportOnSide(wirePos, wallSurfaceAxis, wallAttachmentSide))
            return true;

        wallSurfaceAxis = BlockPlacementAxis.Y;
        wallAttachmentSide = 0;
        return true;
    }

    private bool IsElectricalTopWireOrEndpoint(Vector3Int position)
    {
        return TryResolveElectricalTopWireOrEndpoint(position, out _);
    }

    private bool TryResolveElectricalTopWireOrEndpoint(Vector3Int position, out Vector3Int resolvedPosition)
    {
        resolvedPosition = position;
        if (!electricalBlockPositions.Contains(position))
        {
            if (TryResolveElectricalEndpointAt(position, out resolvedPosition))
                return true;

            return false;
        }

        BlockType blockType = GetBlockAt(position);
        if (IsElectricalEndpointBlock(blockType))
            return true;

        return blockType == BlockType.wire &&
               TryGetElectricalWireState(position, out bool hasTop, out _, out _) &&
               hasTop;
    }

    private bool TryResolveElectricalEndpointAt(Vector3Int position, out Vector3Int endpointPosition)
    {
        endpointPosition = position;
        if (electricalBlockPositions.Contains(position) && IsElectricalEndpointBlock(GetBlockAt(position)))
            return true;

        if (!TryResolveDynamicBlockFootprintAt(position, out Vector3Int origin, out BlockType blockType))
            return false;

        if (!electricalBlockPositions.Contains(origin) || !IsElectricalEndpointBlock(blockType))
            return false;

        endpointPosition = origin;
        return true;
    }

    private static Vector3Int GetWireAttachmentOffset(BlockPlacementAxis wallSurfaceAxis, int wallAttachmentSide)
    {
        if (wallSurfaceAxis == BlockPlacementAxis.X)
            return wallAttachmentSide < 0 ? Vector3Int.left : Vector3Int.right;

        if (wallSurfaceAxis == BlockPlacementAxis.Z)
            return wallAttachmentSide < 0 ? Vector3Int.back : Vector3Int.forward;

        return Vector3Int.zero;
    }

    private void TickElectricalNetworks(float deltaTime)
    {
        float daylightMultiplier = GetSolarPanelDaylightMultiplier();
        float solarRate = Mathf.Max(0f, solarPanelEnergyPerSecond);
        float windRate = Mathf.Max(0f, windMillEnergyPerSecond);
        electricalPoweredThisTick.Clear();

        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network == null)
                continue;

            int activeWindMillCount = CountActiveWindMills(network);
            network.directCapacity = ResolveElectricalDirectCapacity(network, activeWindMillCount);
            network.directEnergy = Mathf.Clamp(network.directEnergy, 0f, network.directCapacity);

            float productionRate =
                network.solarPanelCount * solarRate * daylightMultiplier +
                activeWindMillCount * windRate;

            if (productionRate <= 0f)
            {
                network.directEnergy = 0f;
            }
            else
            {
                float producedEnergy = productionRate * deltaTime;
                if (producedEnergy > 0f)
                {
                    if (network.batteryPositions.Count > 0)
                    {
                        float remaining = ChargeElectricalBatteries(network, producedEnergy);
                        network.directEnergy = Mathf.Clamp(network.directEnergy + remaining, 0f, network.directCapacity);
                    }
                    else
                    {
                        network.directEnergy = Mathf.Clamp(network.directEnergy + producedEnergy, 0f, network.directCapacity);
                    }
                }
            }

            UpdatePoweredBlocksForNetwork(network, deltaTime);
            BalanceElectricalBatteries(network);
        }

        ApplyPoweredElectricalBlockChanges();
        SyncElectricalBatteryVisualStates();
        RefreshElectricalNetworkBufferLookup();
    }

    private void UpdatePoweredBlocksForNetwork(ElectricalNetworkRuntime network, float deltaTime)
    {
        if (network == null || network.poweredBlockPositions.Count == 0)
            return;

        electricalConsumerDemandBuffer.Clear();
        float tickDuration = Mathf.Max(0f, deltaTime);
        float availableEnergy = GetAvailableElectricalEnergy(network);
        float totalEnergyRequired = 0f;

        for (int i = 0; i < network.poweredBlockPositions.Count; i++)
        {
            Vector3Int blockPos = network.poweredBlockPositions[i];
            BlockType blockType = GetBlockAt(blockPos);
            if (!TryGetElectricalPoweredBlockConfig(blockType, out ElectricalPoweredBlockConfig config))
                continue;

            float energyRequired = Mathf.Max(0f, config.energyPerSecond) * tickDuration;
            if (energyRequired <= 0.0001f)
            {
                if (availableEnergy > 0.0001f)
                    electricalPoweredThisTick.Add(blockPos);
                continue;
            }

            totalEnergyRequired += energyRequired;
            electricalConsumerDemandBuffer.Add(new ElectricalConsumerDemand
            {
                position = blockPos,
                energyRequired = energyRequired
            });
        }

        if (totalEnergyRequired <= 0.0001f || electricalConsumerDemandBuffer.Count == 0)
            return;

        if (availableEnergy + 0.0001f >= totalEnergyRequired)
        {
            ConsumeElectricalEnergy(network, totalEnergyRequired);
            for (int i = 0; i < electricalConsumerDemandBuffer.Count; i++)
                electricalPoweredThisTick.Add(electricalConsumerDemandBuffer[i].position);
            return;
        }

        float spentEnergy = 0f;
        int demandCount = electricalConsumerDemandBuffer.Count;
        int startIndex = demandCount > 0 ? network.consumerCursor % demandCount : 0;
        if (startIndex < 0)
            startIndex += demandCount;

        for (int step = 0; step < demandCount; step++)
        {
            int index = (startIndex + step) % demandCount;
            ElectricalConsumerDemand demand = electricalConsumerDemandBuffer[index];
            if (spentEnergy + demand.energyRequired > availableEnergy + 0.0001f)
                continue;

            spentEnergy += demand.energyRequired;
            electricalPoweredThisTick.Add(demand.position);
        }

        network.consumerCursor = demandCount > 0 ? (startIndex + 1) % demandCount : 0;
        if (spentEnergy > 0.0001f)
            ConsumeElectricalEnergy(network, spentEnergy);
    }

    private void ApplyPoweredElectricalBlockChanges()
    {
        poweredElectricalRemovalBuffer.Clear();
        poweredElectricalApplyBuffer.Clear();

        foreach (Vector3Int blockPos in poweredElectricalBlockPositions)
        {
            if (!electricalPoweredThisTick.Contains(blockPos) ||
                !TryGetElectricalPoweredBlockConfig(GetBlockAt(blockPos), out ElectricalPoweredBlockConfig config) ||
                (poweredElectricalEmissionByPosition.TryGetValue(blockPos, out ushort existingEmission) &&
                 existingEmission != config.poweredEmission))
            {
                poweredElectricalRemovalBuffer.Add(blockPos);
            }
        }

        foreach (Vector3Int blockPos in electricalPoweredThisTick)
        {
            if (!TryGetElectricalPoweredBlockConfig(GetBlockAt(blockPos), out ElectricalPoweredBlockConfig config))
                continue;

            if (!poweredElectricalBlockPositions.Contains(blockPos))
            {
                poweredElectricalApplyBuffer.Add(blockPos);
                continue;
            }

            bool currentlyHasEmission = poweredElectricalEmissionByPosition.TryGetValue(blockPos, out ushort existingEmission);
            if (currentlyHasEmission != LightUtils.HasBlockLight(config.poweredEmission) ||
                (currentlyHasEmission && existingEmission != config.poweredEmission))
            {
                poweredElectricalApplyBuffer.Add(blockPos);
            }
        }

        for (int i = 0; i < poweredElectricalRemovalBuffer.Count; i++)
        {
            RemovePoweredElectricalBlockPosition(poweredElectricalRemovalBuffer[i]);
        }

        for (int i = 0; i < poweredElectricalApplyBuffer.Count; i++)
        {
            Vector3Int blockPos = poweredElectricalApplyBuffer[i];
            if (!TryGetElectricalPoweredBlockConfig(GetBlockAt(blockPos), out ElectricalPoweredBlockConfig config))
                continue;

            poweredElectricalBlockPositions.Add(blockPos);

            if (LightUtils.HasBlockLight(config.poweredEmission))
            {
                poweredElectricalEmissionByPosition[blockPos] = config.poweredEmission;
                PropagatePoweredElectricalBlockLight(blockPos, config.poweredEmission);
            }
            else
            {
                poweredElectricalEmissionByPosition.Remove(blockPos);
            }
        }

        SyncElectricalPoweredBlockVisualStates();
        electricalPoweredThisTick.Clear();
    }

    private void SyncElectricalPoweredBlockVisualStates()
    {
        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network == null)
                continue;

            for (int j = 0; j < network.poweredBlockPositions.Count; j++)
            {
                Vector3Int blockPos = network.poweredBlockPositions[j];
                SyncElectricalPoweredBlockVisualState(blockPos, electricalPoweredThisTick.Contains(blockPos));
            }
        }
    }

    private void SyncElectricalBatteryVisualStates()
    {
        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            SyncElectricalBatteryVisualStates(network);
        }
    }

    private void SyncElectricalBatteryVisualStates(ElectricalNetworkRuntime network)
    {
        if (network == null)
            return;

        for (int i = 0; i < network.batteryPositions.Count; i++)
            SyncElectricalBatteryVisualState(network.batteryPositions[i]);
    }

    private void SyncElectricalBatteryVisualState(Vector3Int batteryPos)
    {
        BlockType currentType = GetBlockAt(batteryPos);
        if (!BatteryBlockUtility.IsBatteryBlock(currentType))
            return;

        if (currentType != BlockType.batteryBlock)
        {
            SetElectricalVisualBlockType(batteryPos, currentType, BlockType.batteryBlock);
            currentType = BlockType.batteryBlock;
        }

        SetElectricalTextureVisualState(
            batteryPos,
            BatteryBlockUtility.GetChargeVisualState(GetBatteryElectricalCharge01(batteryPos)));
    }

    private void SyncElectricalPoweredBlockVisualState(Vector3Int worldPos, bool powered)
    {
        BlockType currentType = GetBlockAt(worldPos);
        BlockVisualStateCondition state = powered && HasBlockVisualStateTexture(currentType, BlockVisualStateCondition.ElectricalPowered)
            ? BlockVisualStateCondition.ElectricalPowered
            : BlockVisualStateCondition.None;
        SetElectricalTextureVisualState(worldPos, state);
    }

    private void SetElectricalVisualBlockType(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        BlockPlacementAxis placementAxis = GetStoredPlacementAxis(worldPos, previousType);

        blockOverrides[worldPos] = newType;
        UpdateStoredPlacementAxis(worldPos, newType, placementAxis);

        EnsureTerrainOverrideIndexBuilt();
        IndexTerrainOverride(worldPos, chunkCoord);
        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, newType);
        if (IsElectricalTextureOnlyVisualTransition(previousType, newType))
        {
            RequestElectricalTextureOnlyVisualRefresh(worldPos, chunkCoord, newType);
            return;
        }

        RequestBlockEditRefresh(worldPos, chunkCoord, previousType, newType);
        BlockChanged?.Invoke(worldPos, previousType, newType);
    }

    private void RequestElectricalTextureOnlyVisualRefresh(Vector3Int worldPos, Vector2Int chunkCoord, BlockType visualType)
    {
        if (worldPos.y >= Chunk.SizeY)
        {
            IndexHighOverride(worldPos, chunkCoord, visualType);
            RequestHighBuildMeshRebuild(chunkCoord);
            return;
        }

        int dirtySubchunkMask = GetDirtySubchunkMaskForWorldY(worldPos.y);
        RequestChunkRebuildDelayed(
            chunkCoord,
            dirtySubchunkMask,
            rebuildColliders: false,
            delaySeconds: Mathf.Max(0f, electricalVisualRefreshDelaySeconds));
    }

    private static bool IsElectricalTextureOnlyVisualTransition(BlockType previousType, BlockType newType)
    {
        if (previousType == newType)
            return false;

        return BatteryBlockUtility.IsBatteryBlock(previousType) &&
               BatteryBlockUtility.IsBatteryBlock(newType);
    }

    private void SetElectricalTextureVisualState(Vector3Int worldPos, BlockVisualStateCondition state)
    {
        if (!BlockVisualStateUtility.IsValidState(state))
        {
            RemoveElectricalTextureVisualState(worldPos, requestRefresh: true);
            return;
        }

        byte stateValue = (byte)state;
        if (electricalTextureVisualStateByPosition.TryGetValue(worldPos, out byte currentState) &&
            currentState == stateValue)
        {
            return;
        }

        electricalTextureVisualStateByPosition[worldPos] = stateValue;

        Vector2Int chunkCoord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        if (!electricalTextureVisualStateByChunk.TryGetValue(chunkCoord, out Dictionary<Vector3Int, byte> chunkStates))
        {
            chunkStates = new Dictionary<Vector3Int, byte>();
            electricalTextureVisualStateByChunk[chunkCoord] = chunkStates;
        }

        chunkStates[worldPos] = stateValue;
        RequestElectricalTextureVisualRefresh(worldPos, chunkCoord);
    }

    private void RemoveElectricalTextureVisualState(Vector3Int worldPos, bool requestRefresh)
    {
        if (!electricalTextureVisualStateByPosition.Remove(worldPos))
            return;

        Vector2Int chunkCoord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        if (electricalTextureVisualStateByChunk.TryGetValue(chunkCoord, out Dictionary<Vector3Int, byte> chunkStates))
        {
            chunkStates.Remove(worldPos);
            if (chunkStates.Count == 0)
                electricalTextureVisualStateByChunk.Remove(chunkCoord);
        }

        if (requestRefresh)
            RequestElectricalTextureVisualRefresh(worldPos, chunkCoord);
    }

    private void ClearElectricalTextureVisualStates()
    {
        if (electricalTextureVisualStateByPosition.Count == 0)
            return;

        electricalTextureVisualCleanupBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, byte> pair in electricalTextureVisualStateByPosition)
            electricalTextureVisualCleanupBuffer.Add(pair.Key);

        electricalTextureVisualStateByPosition.Clear();
        electricalTextureVisualStateByChunk.Clear();

        for (int i = 0; i < electricalTextureVisualCleanupBuffer.Count; i++)
        {
            Vector3Int visualPos = electricalTextureVisualCleanupBuffer[i];
            RequestElectricalTextureVisualRefresh(visualPos, GetChunkCoordFromWorldXZ(visualPos.x, visualPos.z));
        }

        electricalTextureVisualCleanupBuffer.Clear();
    }

    private void RequestElectricalTextureVisualRefresh(Vector3Int worldPos, Vector2Int chunkCoord)
    {
        if (worldPos.y >= Chunk.SizeY)
        {
            RequestHighBuildMeshRebuild(chunkCoord);
            return;
        }

        RequestChunkRebuildDelayed(
            chunkCoord,
            GetDirtySubchunkMaskForWorldY(worldPos.y),
            rebuildColliders: false,
            delaySeconds: Mathf.Max(0f, electricalVisualRefreshDelaySeconds));
    }

    private bool HasBlockVisualStateTexture(BlockType blockType, BlockVisualStateCondition state)
    {
        if (!BlockVisualStateUtility.IsValidState(state))
            return false;

        EnsureNativeGenerationCaches();
        int blockTypeCount = cachedNativeBlockMappings.IsCreated ? cachedNativeBlockMappings.Length : 0;
        int index = BlockVisualStateUtility.GetTextureMappingIndex(blockType, state, blockTypeCount);
        return cachedNativeBlockVisualStateTextures.IsCreated &&
               (uint)index < (uint)cachedNativeBlockVisualStateTextures.Length &&
               cachedNativeBlockVisualStateTextures[index].HasAnyFace;
    }

    private bool TryGetElectricalVisualStateUvRectData(
        Vector3Int worldPos,
        BlockType blockType,
        BlockFace face,
        out Vector4 uvRectData)
    {
        uvRectData = default;
        if (!electricalTextureVisualStateByPosition.TryGetValue(worldPos, out byte visualState))
            return false;

        EnsureNativeGenerationCaches();
        int blockTypeCount = cachedNativeBlockMappings.IsCreated ? cachedNativeBlockMappings.Length : 0;
        int index = BlockVisualStateUtility.GetTextureMappingIndex((int)blockType, visualState, blockTypeCount);
        if (!cachedNativeBlockVisualStateTextures.IsCreated ||
            (uint)index >= (uint)cachedNativeBlockVisualStateTextures.Length)
        {
            return false;
        }

        return cachedNativeBlockVisualStateTextures[index].TryGetUvRectData(face, out uvRectData);
    }

    private NativeArray<byte> BuildElectricalVisualStateMaskForMesh(Vector2Int chunkCoord, int borderSize, int voxelDataLength)
    {
        if (voxelDataLength <= 0 ||
            !electricalTextureVisualStateByChunk.TryGetValue(chunkCoord, out Dictionary<Vector3Int, byte> visualStates) ||
            visualStates.Count == 0)
        {
            return new NativeArray<byte>(0, Allocator.Persistent);
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> stateMask = new NativeArray<byte>(voxelDataLength, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        int chunkMinX = chunkCoord.x * Chunk.SizeX;
        int chunkMinZ = chunkCoord.y * Chunk.SizeZ;
        foreach (KeyValuePair<Vector3Int, byte> pair in visualStates)
        {
            Vector3Int visualPos = pair.Key;
            int localX = visualPos.x - chunkMinX;
            int localZ = visualPos.z - chunkMinZ;
            int localY = visualPos.y;
            if (localX < 0 || localX >= Chunk.SizeX ||
                localY < 0 || localY >= Chunk.SizeY ||
                localZ < 0 || localZ >= Chunk.SizeZ)
            {
                continue;
            }

            int idx = (localX + borderSize) + localY * voxelSizeX + (localZ + borderSize) * voxelPlaneSize;
            if ((uint)idx < (uint)stateMask.Length)
                stateMask[idx] = pair.Value;
        }

        return stateMask;
    }

    private void ClearPoweredElectricalBlockEffects()
    {
        if (poweredElectricalBlockPositions.Count == 0)
        {
            electricalPoweredThisTick.Clear();
            ClearElectricalTextureVisualStates();
            return;
        }

        poweredElectricalRemovalBuffer.Clear();
        foreach (Vector3Int blockPos in poweredElectricalBlockPositions)
            poweredElectricalRemovalBuffer.Add(blockPos);

        for (int i = 0; i < poweredElectricalRemovalBuffer.Count; i++)
            RemovePoweredElectricalBlockPosition(poweredElectricalRemovalBuffer[i]);

        electricalPoweredThisTick.Clear();
        ClearElectricalTextureVisualStates();
    }

    private void RemovePoweredElectricalBlockPosition(Vector3Int blockPos)
    {
        electricalPoweredThisTick.Remove(blockPos);
        if (!poweredElectricalBlockPositions.Remove(blockPos))
            return;

        if (electricalTextureVisualStateByPosition.TryGetValue(blockPos, out byte visualState) &&
            visualState == (byte)BlockVisualStateCondition.ElectricalPowered)
        {
            RemoveElectricalTextureVisualState(blockPos, requestRefresh: true);
        }

        if (poweredElectricalEmissionByPosition.TryGetValue(blockPos, out ushort poweredEmission))
        {
            poweredElectricalEmissionByPosition.Remove(blockPos);
            RemovePoweredElectricalBlockLight(blockPos, poweredEmission);
        }
    }

    private void PropagatePoweredElectricalBlockLight(Vector3Int blockPos, ushort emission)
    {
        if (!enableVoxelLighting || !LightUtils.HasBlockLight(emission))
            return;

        PropagateLightGlobal(blockPos, emission);
    }

    private void RemovePoweredElectricalBlockLight(Vector3Int blockPos, ushort emission)
    {
        if (!LightUtils.HasBlockLight(emission))
            return;

        RemoveLightGlobal(blockPos, emission);
    }

    public bool CanWindMillGenerateAt(Vector3Int windMillPos)
    {
        return GetWindMillGroundClearanceBlocks(windMillPos) >= GetWindMillMinimumGroundClearanceBlocks();
    }

    public int GetWindMillGroundClearanceBlocks(Vector3Int windMillPos)
    {
        return windMillPos.y - GetTerrainSurfaceHeightAt(windMillPos.x, windMillPos.z);
    }

    private int CountActiveWindMills(ElectricalNetworkRuntime network)
    {
        if (network == null)
            return 0;

        int count = 0;
        for (int i = 0; i < network.windMillPositions.Count; i++)
        {
            Vector3Int windMillPos = network.windMillPositions[i];
            if (GetBlockAt(windMillPos) == BlockType.windmill && CanWindMillGenerateAt(windMillPos))
                count++;
        }

        return count;
    }

    private float ResolveElectricalDirectCapacity(ElectricalNetworkRuntime network)
    {
        return ResolveElectricalDirectCapacity(network, CountActiveWindMills(network));
    }

    private float ResolveElectricalDirectCapacity(ElectricalNetworkRuntime network, int activeWindMillCount)
    {
        if (network == null)
            return 0f;

        float solarCapacityRate = network.solarPanelCount * Mathf.Max(0f, solarPanelEnergyPerSecond);
        float windCapacityRate = Mathf.Max(0, activeWindMillCount) * Mathf.Max(0f, windMillEnergyPerSecond);
        return Mathf.Max(0f, (solarCapacityRate + windCapacityRate) * directSolarBufferSeconds);
    }

    private int GetWindMillMinimumGroundClearanceBlocks()
    {
        return Mathf.Max(0, windMillMinimumGroundClearanceBlocks);
    }

    private float ChargeElectricalBatteries(ElectricalNetworkRuntime network, float energy)
    {
        float remaining = Mathf.Max(0f, energy);
        float capacity = GetBatteryCapacity();
        if (capacity <= 0f)
            return remaining;

        for (int i = 0; i < network.batteryPositions.Count && remaining > 0.0001f; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            float stored = batteryEnergyByPosition.TryGetValue(batteryPos, out float currentEnergy)
                ? Mathf.Clamp(currentEnergy, 0f, capacity)
                : 0f;

            float accepted = Mathf.Min(capacity - stored, remaining);
            if (accepted <= 0f)
                continue;

            batteryEnergyByPosition[batteryPos] = stored + accepted;
            remaining -= accepted;
        }

        return remaining;
    }

    private void BalanceElectricalBatteries(ElectricalNetworkRuntime network)
    {
        if (network == null || network.batteryPositions.Count <= 1)
            return;

        float capacity = GetBatteryCapacity();
        if (capacity <= 0f)
            return;

        float totalStored = 0f;
        for (int i = 0; i < network.batteryPositions.Count; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            if (!batteryEnergyByPosition.TryGetValue(batteryPos, out float stored))
                continue;

            totalStored += Mathf.Clamp(stored, 0f, capacity);
        }

        float targetStored = Mathf.Clamp(totalStored / network.batteryPositions.Count, 0f, capacity);
        for (int i = 0; i < network.batteryPositions.Count; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            if (targetStored <= 0.0001f)
            {
                batteryEnergyByPosition.Remove(batteryPos);
                continue;
            }

            batteryEnergyByPosition[batteryPos] = targetStored;
        }
    }

    private float GetAvailableElectricalEnergy(ElectricalNetworkRuntime network)
    {
        if (network == null)
            return 0f;

        float available = Mathf.Max(0f, network.directEnergy);
        float capacity = GetBatteryCapacity();
        for (int i = 0; i < network.batteryPositions.Count; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            if (batteryEnergyByPosition.TryGetValue(batteryPos, out float energy))
                available += Mathf.Clamp(energy, 0f, capacity);
        }

        return available;
    }

    private void ConsumeElectricalEnergy(ElectricalNetworkRuntime network, float amount)
    {
        float remaining = Mathf.Max(0f, amount);
        if (network.directEnergy > 0f)
        {
            float consumedDirect = Mathf.Min(network.directEnergy, remaining);
            network.directEnergy -= consumedDirect;
            remaining -= consumedDirect;
        }

        float capacity = GetBatteryCapacity();
        for (int i = 0; i < network.batteryPositions.Count && remaining > 0.0001f; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            if (!batteryEnergyByPosition.TryGetValue(batteryPos, out float stored))
                continue;

            stored = Mathf.Clamp(stored, 0f, capacity);
            float consumed = Mathf.Min(stored, remaining);
            stored -= consumed;
            remaining -= consumed;

            if (stored <= 0.0001f)
                batteryEnergyByPosition.Remove(batteryPos);
            else
                batteryEnergyByPosition[batteryPos] = stored;
        }

        RefreshElectricalNetworkBufferLookup();
    }

    private void CleanupBatteryEnergyStorage()
    {
        electricityCleanupBuffer.Clear();
        foreach (KeyValuePair<Vector3Int, float> pair in batteryEnergyByPosition)
        {
            if (!BatteryBlockUtility.IsBatteryBlock(GetBlockAt(pair.Key)))
                electricityCleanupBuffer.Add(pair.Key);
        }

        for (int i = 0; i < electricityCleanupBuffer.Count; i++)
            batteryEnergyByPosition.Remove(electricityCleanupBuffer[i]);
    }

    private void HandleElectricityBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!IsElectricalBlock(previousType) && !IsElectricalBlock(newType))
            return;

        electricalNetworksDirty = true;
        RemoveElectricalTextureVisualState(worldPos, requestRefresh: false);

        if (BatteryBlockUtility.IsBatteryBlock(previousType) &&
            !BatteryBlockUtility.IsBatteryBlock(newType))
        {
            batteryEnergyByPosition.Remove(worldPos);
        }

        if (previousType != newType)
            RemovePoweredElectricalBlockPosition(worldPos);
    }

    private float GetBatteryCapacity()
    {
        return Mathf.Max(1f, batteryCapacity);
    }

    private float GetSolarPanelDaylightMultiplier()
    {
        if (cachedElectricitySkyController == null)
            cachedElectricitySkyController = FindAnyObjectByType<VoxelProceduralSkyController>();

        if (cachedElectricitySkyController != null)
            return cachedElectricitySkyController.CurrentPhase == VoxelProceduralSkyController.DayPhase.Day ? 1f : 0f;

        Light sun = RenderSettings.sun;
        if (sun == null)
            return 1f;

        Vector3 sunDirection = -sun.transform.forward;
        bool sunAboveHorizon = Vector3.Dot(sunDirection, Vector3.up) > 0.05f;
        return sun.isActiveAndEnabled && sun.intensity > 0.05f && sunAboveHorizon ? 1f : 0f;
    }

    public bool IsElectricalWireEndpointBlockType(BlockType blockType)
    {
        return LeverUtility.IsLeverBlock(blockType) ||
               blockType == BlockType.EletricConnector ||
               IsLegacyElectricalEndpointBlock(blockType) ||
               IsConfiguredElectricalEndpointBlock(blockType);
    }

    private bool TryGetElectricalPoweredBlockConfig(BlockType blockType, out ElectricalPoweredBlockConfig config)
    {
        config = default;
        if (blockType == BlockType.RoboticArm)
        {
            config.energyPerSecond = Mathf.Max(0f, roboticArmIdleEnergyPerSecond);
            return config.energyPerSecond > 0f;
        }

        if (!TryGetBlockMapping(blockType, out BlockTextureMapping mapping) || !mapping.isElectricalEndpoint)
            return false;

        bool hasPoweredVisualState = HasBlockVisualStateTexture(blockType, BlockVisualStateCondition.ElectricalPowered);
        config.energyPerSecond = Mathf.Max(0f, mapping.poweredElectricalEnergyPerSecond);
        config.poweredEmission = LightUtils.PackEmission(mapping.poweredLightEmission, mapping.poweredLightColor);
        if (config.energyPerSecond <= 0f && LightUtils.HasBlockLight(config.poweredEmission))
            config.energyPerSecond = Mathf.Max(0f, defaultPoweredLightEnergyPerSecond);

        return config.energyPerSecond > 0f ||
               LightUtils.HasBlockLight(config.poweredEmission) ||
               hasPoweredVisualState;
    }

    private bool TryGetBlockMapping(BlockType blockType, out BlockTextureMapping mapping)
    {
        mapping = default;
        BlockTextureMapping? blockMapping = blockData != null ? blockData.GetMapping(blockType) : null;
        if (blockMapping == null)
            return false;

        mapping = blockMapping.Value;
        return true;
    }

    private bool IsConfiguredElectricalEndpointBlock(BlockType blockType)
    {
        return TryGetBlockMapping(blockType, out BlockTextureMapping mapping) && mapping.isElectricalEndpoint;
    }

    private bool IsElectricalBlock(BlockType blockType)
    {
        if (blockType == BlockType.LeverOff)
            return false;

        return blockType == BlockType.wire ||
               IsElectricalWireEndpointBlockType(blockType);
    }

    private bool IsElectricalEndpointBlock(BlockType blockType)
    {
        return IsElectricalWireEndpointBlockType(blockType);
    }

    private bool IsElectricalTransportBlock(BlockType blockType)
    {
        return blockType == BlockType.wire ||
               blockType == BlockType.EletricConnector ||
               blockType == BlockType.LeverOn;
    }

    private bool AreAdjacentElectricalBlocksConnected(BlockType left, BlockType right)
    {
        return IsElectricalTransportBlock(left) ||
               IsElectricalTransportBlock(right) ||
               (BatteryBlockUtility.IsBatteryBlock(left) && BatteryBlockUtility.IsBatteryBlock(right));
    }

    private static bool IsLegacyElectricalEndpointBlock(BlockType blockType)
    {
        return blockType == BlockType.SolarPanel ||
               blockType == BlockType.windmill ||
               BatteryBlockUtility.IsBatteryBlock(blockType) ||
               blockType == BlockType.ledWhiteBlock ||
               blockType == BlockType.RoboticArm ||
               blockType == BlockType.Treecutter ||
               blockType == BlockType.AutoMiner ||
               blockType == BlockType.StoneCrusher ||
               blockType == BlockType.MagneticSeparator;
    }

    private static int CompareElectricalPositions(Vector3Int left, Vector3Int right)
    {
        int x = left.x.CompareTo(right.x);
        if (x != 0)
            return x;

        int y = left.y.CompareTo(right.y);
        if (y != 0)
            return y;

        return left.z.CompareTo(right.z);
    }

    #endregion
}
