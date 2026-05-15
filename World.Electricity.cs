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
    private readonly List<ElectricalNetworkRuntime> electricalConnectedNetworkBuffer = new List<ElectricalNetworkRuntime>(8);
    private readonly HashSet<ElectricalNetworkRuntime> electricalConnectedNetworkSet = new HashSet<ElectricalNetworkRuntime>();
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
    private bool electricalBlockIndexInitialized;
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
        public float storedBatteryEnergy;
        public float poweredEnergyPerSecond;
        public bool batteryVisualsDirty;
        public bool anyPoweredLastTick;
        public bool fullyPoweredLastTick;
        public int activePoweredBlockCount;
        public readonly List<Vector3Int> positions = new List<Vector3Int>(32);
        public readonly List<Vector3Int> batteryPositions = new List<Vector3Int>(8);
        public readonly List<Vector3Int> windMillPositions = new List<Vector3Int>(8);
        public readonly List<Vector3Int> steamEnginePositions = new List<Vector3Int>(8);
        public readonly List<Vector3Int> poweredBlockPositions = new List<Vector3Int>(8);
        public int consumerCursor;
        public float steamGenerationBudget;
        public float steamGenerationBudgetTime;
    }

    #region Electricity

    public bool HasElectricalEnergy(Vector3Int consumerPos, float amount)
    {
        if (!enableElectricitySystem)
            return true;

        amount = Mathf.Max(0f, amount);
        EnsureElectricitySimulationReady();

        if (!TryGetElectricalNetworkForPosition(consumerPos, out ElectricalNetworkRuntime network))
            return false;

        if (amount <= 0f)
            return true;

        float availableEnergy = GetAvailableElectricalEnergy(network);
        if (availableEnergy + 0.0001f >= amount)
            return true;

        return CanProduceSteamEnergyForImmediateDemand(network, amount - availableEnergy);
    }

    public bool TryConsumeElectricalEnergy(Vector3Int consumerPos, float amount)
    {
        if (!enableElectricitySystem)
            return true;

        amount = Mathf.Max(0f, amount);
        EnsureElectricitySimulationReady();

        if (!TryGetElectricalNetworkForPosition(consumerPos, out ElectricalNetworkRuntime network))
            return false;

        if (amount <= 0f)
            return true;

        float availableEnergy = GetAvailableElectricalEnergy(network);
        if (availableEnergy + 0.0001f >= amount)
        {
            ConsumeElectricalEnergy(network, amount);
            return true;
        }

        float missingEnergy = amount - availableEnergy;
        if (!CanProduceSteamEnergyForImmediateDemand(network, missingEnergy))
            return false;

        if (availableEnergy > 0.0001f)
            ConsumeElectricalEnergy(network, availableEnergy);

        float producedEnergy = TryProduceSteamEnergyForImmediateDemand(network, missingEnergy);
        return producedEnergy + 0.0001f >= missingEnergy;
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

    public float GetElectricalEnergyCharge01(Vector3Int consumerPos)
    {
        if (!enableElectricitySystem)
            return 1f;

        EnsureElectricitySimulationReady();

        if (!TryGetElectricalNetworkForPosition(consumerPos, out ElectricalNetworkRuntime network))
            return 0f;

        float capacity = GetElectricalEnergyCapacity(network);
        return capacity > 0f
            ? Mathf.Clamp01(GetAvailableElectricalEnergy(network) / capacity)
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
        EnsureElectricalBlockIndexBuilt();
        electricalNetworkByPosition.Clear();
        electricalNetworks.Clear();
        electricalBuildVisited.Clear();
        electricalExtraEdges.Clear();

        CleanupBatteryEnergyStorage();
        BuildElectricConnectorExtraEdges();
        BuildSteamEngineFrontExtraEdges();
        BuildWireGeometryExtraEdges();

        foreach (Vector3Int position in electricalBlockPositions)
        {
            if (!electricalBuildVisited.Contains(position))
                BuildElectricalNetwork(position);
        }

        RemoveStalePoweredElectricalBlocksAfterNetworkRebuild();
        RefreshElectricalNetworkBufferLookup();
        electricalNetworksDirty = false;
    }

    private bool TryGetElectricalNetworkForPosition(Vector3Int position, out ElectricalNetworkRuntime network)
    {
        if (electricalNetworkByPosition.TryGetValue(position, out network))
            return true;

        if (!IsElectricalBlock(GetBlockAt(position)))
            return false;

        electricalNetworksDirty = true;
        EnsureElectricityNetworksBuilt();
        return electricalNetworkByPosition.TryGetValue(position, out network);
    }

    private void EnsureElectricalBlockIndexBuilt()
    {
        if (electricalBlockIndexInitialized)
            return;

        electricalBlockPositions.Clear();
        foreach (KeyValuePair<Vector3Int, BlockType> pair in blockOverrides)
        {
            if (IsElectricalBlock(pair.Value))
                electricalBlockPositions.Add(pair.Key);
        }

        electricalBlockIndexInitialized = true;
    }

    private void RemoveStalePoweredElectricalBlocksAfterNetworkRebuild()
    {
        if (poweredElectricalBlockPositions.Count == 0)
            return;

        poweredElectricalRemovalBuffer.Clear();
        foreach (Vector3Int blockPos in poweredElectricalBlockPositions)
        {
            if (!electricalNetworkByPosition.ContainsKey(blockPos) ||
                !TryGetElectricalPoweredBlockConfig(GetBlockAt(blockPos), out _))
            {
                poweredElectricalRemovalBuffer.Add(blockPos);
            }
        }

        for (int i = 0; i < poweredElectricalRemovalBuffer.Count; i++)
            RemovePoweredElectricalBlockPosition(poweredElectricalRemovalBuffer[i]);

        poweredElectricalRemovalBuffer.Clear();
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

    private bool TryAddElectricalBlockIncrementally(Vector3Int position)
    {
        if (electricalNetworksDirty || !electricalBlockIndexInitialized)
            return false;

        BlockType blockType = GetBlockAt(position);
        if (!IsElectricalBlock(blockType))
            return true;

        if (electricalNetworkByPosition.ContainsKey(position))
            return true;

        if (ShouldForceFullElectricalRebuildForAddedBlock(position, blockType))
            return false;

        AddLocalWireGeometryExtraEdgesForAddedBlock(position);
        AddLocalSteamEngineFrontExtraEdgesForAddedBlock(position);
        CollectConnectedElectricalNetworks(position, blockType);

        ElectricalNetworkRuntime targetNetwork;
        if (electricalConnectedNetworkBuffer.Count == 0)
        {
            targetNetwork = new ElectricalNetworkRuntime
            {
                key = position
            };
            AddElectricalPositionToNetwork(targetNetwork, position, blockType);
            targetNetwork.directCapacity = ResolveElectricalDirectCapacity(targetNetwork);
            electricalNetworks.Add(targetNetwork);
        }
        else
        {
            targetNetwork = electricalConnectedNetworkBuffer[0];
            if (electricalConnectedNetworkBuffer.Count > 1)
                MergeElectricalNetworksInto(targetNetwork);

            AddElectricalPositionToNetwork(targetNetwork, position, blockType);
            targetNetwork.directCapacity = ResolveElectricalDirectCapacity(targetNetwork);
            targetNetwork.directEnergy = Mathf.Clamp(targetNetwork.directEnergy, 0f, targetNetwork.directCapacity);
        }

        electricalConnectedNetworkBuffer.Clear();
        electricalConnectedNetworkSet.Clear();
        return true;
    }

    private bool ShouldForceFullElectricalRebuildForAddedBlock(Vector3Int position, BlockType blockType)
    {
        if (blockType == BlockType.wire)
            return false;

        if (!TryGetBlockMapping(blockType, out BlockTextureMapping mapping) || !mapping.renderAsDynamicPrefab)
            return false;

        int horizontalRadius = Mathf.Max(
            1,
            Mathf.Max(mapping.dynamicOccupiedWidthBlocks, mapping.dynamicOccupiedLengthBlocks));
        int verticalRadius = Mathf.Max(1, mapping.dynamicOccupiedHeightBlocks);

        for (int yOffset = -1; yOffset <= verticalRadius + 1; yOffset++)
        {
            for (int zOffset = -horizontalRadius - 1; zOffset <= horizontalRadius + 1; zOffset++)
            {
                for (int xOffset = -horizontalRadius - 1; xOffset <= horizontalRadius + 1; xOffset++)
                {
                    Vector3Int candidate = position + new Vector3Int(xOffset, yOffset, zOffset);
                    if (candidate == position)
                        continue;

                    if (electricalBlockPositions.Contains(candidate))
                        return true;
                }
            }
        }

        return false;
    }

    private void AddLocalWireGeometryExtraEdgesForAddedBlock(Vector3Int position)
    {
        AddWireGeometryExtraEdgesForPosition(position);

        for (int yOffset = -1; yOffset <= 1; yOffset++)
        {
            for (int zOffset = -1; zOffset <= 1; zOffset++)
            {
                for (int xOffset = -1; xOffset <= 1; xOffset++)
                {
                    Vector3Int candidate = position + new Vector3Int(xOffset, yOffset, zOffset);
                    if (candidate == position || !electricalBlockPositions.Contains(candidate))
                        continue;

                    AddWireGeometryExtraEdgesForPosition(candidate);
                }
            }
        }
    }

    private void AddWireGeometryExtraEdgesForPosition(Vector3Int position)
    {
        if (!electricalBlockPositions.Contains(position) || GetBlockAt(position) != BlockType.wire)
            return;

        AddTopWireGeometryExtraEdges(position);
        AddWallWireGeometryExtraEdges(position);
    }

    private void AddLocalSteamEngineFrontExtraEdgesForAddedBlock(Vector3Int position)
    {
        AddSteamEngineFrontExtraEdgeForPosition(position);

        for (int i = 0; i < sixDirections.Length; i++)
        {
            Vector3Int candidate = position + sixDirections[i];
            if (electricalBlockPositions.Contains(candidate))
                AddSteamEngineFrontExtraEdgeForPosition(candidate);
        }
    }

    private void CollectConnectedElectricalNetworks(Vector3Int position, BlockType blockType)
    {
        electricalConnectedNetworkBuffer.Clear();
        electricalConnectedNetworkSet.Clear();

        for (int i = 0; i < sixDirections.Length; i++)
        {
            Vector3Int neighbor = position + sixDirections[i];
            if (!electricalBlockPositions.Contains(neighbor) ||
                !electricalNetworkByPosition.TryGetValue(neighbor, out ElectricalNetworkRuntime network))
            {
                continue;
            }

            if (!AreAdjacentElectricalBlocksConnected(blockType, GetBlockAt(neighbor)))
                continue;

            AddConnectedElectricalNetwork(network);
        }

        if (!electricalExtraEdges.TryGetValue(position, out List<Vector3Int> linkedPositions))
            return;

        for (int i = 0; i < linkedPositions.Count; i++)
        {
            Vector3Int linked = linkedPositions[i];
            if (electricalNetworkByPosition.TryGetValue(linked, out ElectricalNetworkRuntime network))
                AddConnectedElectricalNetwork(network);
        }
    }

    private void AddConnectedElectricalNetwork(ElectricalNetworkRuntime network)
    {
        if (network == null || !electricalConnectedNetworkSet.Add(network))
            return;

        electricalConnectedNetworkBuffer.Add(network);
    }

    private void MergeElectricalNetworksInto(ElectricalNetworkRuntime targetNetwork)
    {
        for (int i = 0; i < electricalConnectedNetworkBuffer.Count; i++)
        {
            ElectricalNetworkRuntime sourceNetwork = electricalConnectedNetworkBuffer[i];
            if (sourceNetwork == null || sourceNetwork == targetNetwork)
                continue;

            for (int j = 0; j < sourceNetwork.positions.Count; j++)
            {
                Vector3Int position = sourceNetwork.positions[j];
                electricalNetworkByPosition[position] = targetNetwork;
                targetNetwork.positions.Add(position);
            }

            targetNetwork.blockCount += sourceNetwork.blockCount;
            targetNetwork.solarPanelCount += sourceNetwork.solarPanelCount;
            targetNetwork.directEnergy += sourceNetwork.directEnergy;
            targetNetwork.storedBatteryEnergy += sourceNetwork.storedBatteryEnergy;
            targetNetwork.poweredEnergyPerSecond += sourceNetwork.poweredEnergyPerSecond;
            targetNetwork.batteryVisualsDirty |= sourceNetwork.batteryVisualsDirty;
            targetNetwork.activePoweredBlockCount += sourceNetwork.activePoweredBlockCount;

            if (CompareElectricalPositions(sourceNetwork.key, targetNetwork.key) < 0)
                targetNetwork.key = sourceNetwork.key;

            targetNetwork.batteryPositions.AddRange(sourceNetwork.batteryPositions);
            targetNetwork.windMillPositions.AddRange(sourceNetwork.windMillPositions);
            targetNetwork.steamEnginePositions.AddRange(sourceNetwork.steamEnginePositions);
            targetNetwork.poweredBlockPositions.AddRange(sourceNetwork.poweredBlockPositions);
            RefreshElectricalNetworkPoweredFlags(targetNetwork);
            electricalNetworks.Remove(sourceNetwork);
        }
    }

    private void AddElectricalPositionToNetwork(
        ElectricalNetworkRuntime network,
        Vector3Int position,
        BlockType blockType)
    {
        if (network == null)
            return;

        network.blockCount++;
        network.positions.Add(position);
        if (CompareElectricalPositions(position, network.key) < 0)
            network.key = position;

        electricalNetworkByPosition[position] = network;

        if (blockType == BlockType.SolarPanel)
        {
            network.solarPanelCount++;
        }
        else if (blockType == BlockType.windmill)
        {
            network.windMillPositions.Add(position);
        }
        else if (blockType == BlockType.SteamEngine)
        {
            network.steamEnginePositions.Add(position);
        }
        else if (BatteryBlockUtility.IsBatteryBlock(blockType))
        {
            network.batteryPositions.Add(position);
            if (batteryEnergyByPosition.TryGetValue(position, out float batteryEnergy))
                network.storedBatteryEnergy += Mathf.Clamp(batteryEnergy, 0f, GetBatteryCapacity());

            network.batteryVisualsDirty = true;
        }

        if (TryGetElectricalPoweredBlockConfig(blockType, out ElectricalPoweredBlockConfig config))
        {
            network.poweredBlockPositions.Add(position);
            network.poweredEnergyPerSecond += Mathf.Max(0f, config.energyPerSecond);
            if (poweredElectricalBlockPositions.Contains(position))
                network.activePoweredBlockCount++;
            else
                network.fullyPoweredLastTick = false;

            RefreshElectricalNetworkPoweredFlags(network);
        }
    }

    private static void RefreshElectricalNetworkPoweredFlags(ElectricalNetworkRuntime network)
    {
        if (network == null || network.poweredBlockPositions.Count == 0)
        {
            if (network != null)
            {
                network.activePoweredBlockCount = 0;
                network.anyPoweredLastTick = false;
                network.fullyPoweredLastTick = false;
            }

            return;
        }

        network.activePoweredBlockCount = Mathf.Clamp(
            network.activePoweredBlockCount,
            0,
            network.poweredBlockPositions.Count);
        network.anyPoweredLastTick = network.activePoweredBlockCount > 0;
        network.fullyPoweredLastTick = network.activePoweredBlockCount == network.poweredBlockPositions.Count;
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
            network.positions.Add(position);
            if (CompareElectricalPositions(position, network.key) < 0)
                network.key = position;

            electricalNetworkByPosition[position] = network;

            if (blockType == BlockType.SolarPanel)
                network.solarPanelCount++;
            else if (blockType == BlockType.windmill)
                network.windMillPositions.Add(position);
            else if (blockType == BlockType.SteamEngine)
                network.steamEnginePositions.Add(position);
            else if (BatteryBlockUtility.IsBatteryBlock(blockType))
            {
                network.batteryPositions.Add(position);
                if (batteryEnergyByPosition.TryGetValue(position, out float batteryEnergy))
                    network.storedBatteryEnergy += Mathf.Clamp(batteryEnergy, 0f, GetBatteryCapacity());
            }

            if (TryGetElectricalPoweredBlockConfig(blockType, out ElectricalPoweredBlockConfig config))
            {
                network.poweredBlockPositions.Add(position);
                network.poweredEnergyPerSecond += Mathf.Max(0f, config.energyPerSecond);
                if (poweredElectricalBlockPositions.Contains(position))
                    network.activePoweredBlockCount++;
            }

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

        network.batteryVisualsDirty = network.batteryPositions.Count > 0;
        RefreshElectricalNetworkPoweredFlags(network);
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

            if (!EletricConnectorWireSystem.IsValidWireConnectionPair(
                    GetBlockAt(connection.Start),
                    GetBlockAt(connection.End)))
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
            AddWireGeometryExtraEdgesForPosition(position);
    }

    private void BuildSteamEngineFrontExtraEdges()
    {
        foreach (Vector3Int position in electricalBlockPositions)
            AddSteamEngineFrontExtraEdgeForPosition(position);
    }

    private void AddSteamEngineFrontExtraEdgeForPosition(Vector3Int steamEnginePos)
    {
        if (!electricalBlockPositions.Contains(steamEnginePos) ||
            GetBlockAt(steamEnginePos) != BlockType.SteamEngine)
        {
            return;
        }

        AddAdjacentRoboticArmExtraEdgesForSteamEngine(steamEnginePos);

        Vector3Int frontPos = steamEnginePos + ResolveSteamEngineFrontStep(steamEnginePos);
        if (!TryResolveElectricalEndpointAt(frontPos, out Vector3Int endpointTarget))
            return;

        if (endpointTarget == steamEnginePos)
            return;

        AddElectricalExtraEdge(steamEnginePos, endpointTarget);
        AddElectricalExtraEdge(endpointTarget, steamEnginePos);
    }

    private void AddAdjacentRoboticArmExtraEdgesForSteamEngine(Vector3Int steamEnginePos)
    {
        for (int i = 0; i < sixDirections.Length; i++)
        {
            Vector3Int candidate = steamEnginePos + sixDirections[i];
            if (!electricalBlockPositions.Contains(candidate) ||
                GetBlockAt(candidate) != BlockType.RoboticArm)
            {
                continue;
            }

            AddElectricalExtraEdge(steamEnginePos, candidate);
            AddElectricalExtraEdge(candidate, steamEnginePos);
        }
    }

    private Vector3Int ResolveSteamEngineFrontStep(Vector3Int steamEnginePos)
    {
        BlockPlacementAxis placementAxis = GetPlacementAxisAt(steamEnginePos, BlockType.SteamEngine);
        switch (BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis))
        {
            case BlockPlacementAxis.X:
                return Vector3Int.right;

            case BlockPlacementAxis.XNegative:
                return Vector3Int.left;

            case BlockPlacementAxis.ZNegative:
                return Vector3Int.forward;

            default:
                return Vector3Int.back;
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
        RefillSteamEngineWaterForAllNetworks(deltaTime);

        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network == null)
                continue;

            steamEngineProducedEnergyThisTick.Clear();

            int activeWindMillCount = CountActiveWindMills(network);
            int readySteamEngineCount = CountReadySteamEngines(network.steamEnginePositions);
            network.directCapacity = ResolveElectricalDirectCapacity(network, activeWindMillCount, readySteamEngineCount);
            network.directEnergy = Mathf.Clamp(network.directEnergy, 0f, network.directCapacity);
            RefillSteamGenerationBudget(network, readySteamEngineCount);

            float passiveProducedEnergy =
                (network.solarPanelCount * solarRate * daylightMultiplier +
                 activeWindMillCount * windRate) * deltaTime;

            if (passiveProducedEnergy > 0f)
                AddProducedElectricalEnergy(network, passiveProducedEnergy);
            else
                network.directEnergy = 0f;

            float steamDemand = Mathf.Min(
                GetSteamEngineDemandForNetwork(network, deltaTime),
                GetSteamGenerationBudget(network));
            float steamProducedEnergy = TickSteamEnginesAndGetProducedEnergy(
                network.steamEnginePositions,
                steamDemand,
                deltaTime);
            if (steamProducedEnergy > 0f)
            {
                SpendSteamGenerationBudget(network, steamProducedEnergy);
                AddProducedElectricalEnergy(network, steamProducedEnergy);
            }

            float idleSteamDemand = Mathf.Min(
                GetSteamEngineIdleEnergyDemand(readySteamEngineCount, deltaTime),
                GetSteamGenerationBudget(network));
            float idleSteamProducedEnergy = TickSteamEnginesIdleAndGetProducedEnergy(
                network.steamEnginePositions,
                idleSteamDemand,
                deltaTime);
            if (idleSteamProducedEnergy > 0f)
            {
                SpendSteamGenerationBudget(network, idleSteamProducedEnergy);
                AddProducedElectricalEnergy(network, idleSteamProducedEnergy);
            }

            UpdatePoweredBlocksForNetwork(network, deltaTime);
        }

        electricalPoweredThisTick.Clear();
        SyncElectricalBatteryVisualStates();
        RefreshElectricalNetworkBufferLookup();
    }

    private void AddProducedElectricalEnergy(ElectricalNetworkRuntime network, float producedEnergy)
    {
        if (network == null || producedEnergy <= 0f)
            return;

        if (network.batteryPositions.Count > 0)
        {
            float remaining = ChargeElectricalBatteries(network, producedEnergy);
            network.directEnergy = Mathf.Clamp(network.directEnergy + remaining, 0f, network.directCapacity);
            return;
        }

        network.directEnergy = Mathf.Clamp(network.directEnergy + producedEnergy, 0f, network.directCapacity);
    }

    private float GetSteamEngineDemandForNetwork(ElectricalNetworkRuntime network, float deltaTime)
    {
        if (network == null || deltaTime <= 0f)
            return 0f;

        float continuousDemand = Mathf.Max(0f, network.poweredEnergyPerSecond) * Mathf.Max(0f, deltaTime);
        float availableForConsumers = GetAvailableElectricalEnergy(network);
        float unmetContinuousDemand = Mathf.Max(0f, continuousDemand - availableForConsumers);
        return unmetContinuousDemand + GetBatteryFreeCapacity(network);
    }

    private float GetSteamEngineIdleEnergyDemand(int readySteamEngineCount, float deltaTime)
    {
        if (readySteamEngineCount <= 0 || deltaTime <= 0f)
            return 0f;

        float idleMultiplier = Mathf.Clamp01(steamEngineIdleBurnMultiplier);
        if (idleMultiplier <= 0f)
            return 0f;

        return readySteamEngineCount *
               Mathf.Max(0f, steamEngineEnergyPerSecond) *
               idleMultiplier *
               deltaTime;
    }

    private bool CanProduceSteamEnergyForImmediateDemand(ElectricalNetworkRuntime network, float requestedEnergy)
    {
        if (network == null || requestedEnergy <= 0f)
            return true;

        int readySteamEngineCount = CountReadySteamEngines(network.steamEnginePositions);
        RefillSteamGenerationBudget(network, readySteamEngineCount);
        float availableSteamEnergy = Mathf.Min(
            GetAvailableSteamEngineEnergy(network.steamEnginePositions),
            GetSteamGenerationBudget(network));
        return availableSteamEnergy + 0.0001f >= requestedEnergy;
    }

    private float TryProduceSteamEnergyForImmediateDemand(ElectricalNetworkRuntime network, float requestedEnergy)
    {
        if (network == null || requestedEnergy <= 0f)
            return 0f;

        int readySteamEngineCount = CountReadySteamEngines(network.steamEnginePositions);
        if (readySteamEngineCount <= 0)
            return 0f;

        RefreshElectricalDirectCapacityForNetwork(network, readySteamEngineCount);

        float steamRate = readySteamEngineCount * Mathf.Max(0f, steamEngineEnergyPerSecond);
        if (steamRate <= 0f)
            return 0f;

        RefillSteamGenerationBudget(network, readySteamEngineCount);
        float producibleEnergy = Mathf.Min(requestedEnergy, GetSteamGenerationBudget(network));
        if (producibleEnergy <= 0.0001f)
            return 0f;

        float producedEnergy = TickSteamEnginesAndGetProducedEnergy(
            network.steamEnginePositions,
            producibleEnergy,
            Mathf.Max(0.0001f, producibleEnergy / steamRate));
        SpendSteamGenerationBudget(network, producedEnergy);
        return producedEnergy;
    }

    private void RefillSteamGenerationBudget(ElectricalNetworkRuntime network, int readySteamEngineCount)
    {
        if (network == null)
            return;

        float steamRate = Mathf.Max(0, readySteamEngineCount) * Mathf.Max(0f, steamEngineEnergyPerSecond);
        if (steamRate <= 0f)
        {
            network.steamGenerationBudget = 0f;
            network.steamGenerationBudgetTime = Time.time;
            return;
        }

        float now = Time.time;
        if (network.steamGenerationBudgetTime <= 0f)
        {
            network.steamGenerationBudgetTime = now;
            network.steamGenerationBudget = Mathf.Min(
                GetSteamGenerationBudgetCapacity(steamRate),
                network.steamGenerationBudget);
            return;
        }

        float elapsed = Mathf.Max(0f, now - network.steamGenerationBudgetTime);
        network.steamGenerationBudgetTime = now;
        network.steamGenerationBudget = Mathf.Min(
            GetSteamGenerationBudgetCapacity(steamRate),
            Mathf.Max(0f, network.steamGenerationBudget) + elapsed * steamRate);
    }

    private float GetSteamGenerationBudget(ElectricalNetworkRuntime network)
    {
        return network != null ? Mathf.Max(0f, network.steamGenerationBudget) : 0f;
    }

    private float GetSteamGenerationBudgetCapacity(float steamRate)
    {
        float bufferSeconds = Mathf.Max(Mathf.Max(0f, directSolarBufferSeconds), Mathf.Max(0.0001f, electricityTickInterval));
        return Mathf.Max(0f, steamRate) * bufferSeconds;
    }

    private void SpendSteamGenerationBudget(ElectricalNetworkRuntime network, float producedEnergy)
    {
        if (network == null || producedEnergy <= 0f)
            return;

        network.steamGenerationBudget = Mathf.Max(0f, network.steamGenerationBudget - producedEnergy);
    }

    private void RefreshElectricalDirectCapacityForNetwork(
        ElectricalNetworkRuntime network,
        int readySteamEngineCount)
    {
        if (network == null)
            return;

        network.directCapacity = ResolveElectricalDirectCapacity(
            network,
            CountActiveWindMills(network),
            readySteamEngineCount);
        network.directEnergy = Mathf.Clamp(network.directEnergy, 0f, network.directCapacity);
    }

    private void UpdatePoweredBlocksForNetwork(ElectricalNetworkRuntime network, float deltaTime)
    {
        if (network == null || network.poweredBlockPositions.Count == 0)
            return;

        float tickDuration = Mathf.Max(0f, deltaTime);
        float availableEnergy = GetAvailableElectricalEnergy(network);
        float totalEnergyRequired = Mathf.Max(0f, network.poweredEnergyPerSecond) * tickDuration;

        if (totalEnergyRequired <= 0.0001f)
        {
            if (availableEnergy > 0.0001f)
                SetElectricalNetworkPoweredState(network, true);
            else
                SetElectricalNetworkPoweredState(network, false);

            return;
        }

        if (availableEnergy + 0.0001f >= totalEnergyRequired)
        {
            ConsumeElectricalEnergy(network, totalEnergyRequired);
            SetElectricalNetworkPoweredState(network, true);
            return;
        }

        electricalConsumerDemandBuffer.Clear();
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

            electricalConsumerDemandBuffer.Add(new ElectricalConsumerDemand
            {
                position = blockPos,
                energyRequired = energyRequired
            });
        }

        if (electricalConsumerDemandBuffer.Count == 0)
        {
            ApplyPartialElectricalNetworkPoweredState(network);
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

        ApplyPartialElectricalNetworkPoweredState(network);
    }

    private void SetElectricalNetworkPoweredState(ElectricalNetworkRuntime network, bool powered)
    {
        if (network == null || network.poweredBlockPositions.Count == 0)
            return;

        if (powered)
        {
            if (network.fullyPoweredLastTick)
                return;

            for (int i = 0; i < network.poweredBlockPositions.Count; i++)
                AddOrRefreshPoweredElectricalBlockPosition(network.poweredBlockPositions[i]);

            network.activePoweredBlockCount = network.poweredBlockPositions.Count;
            network.anyPoweredLastTick = true;
            network.fullyPoweredLastTick = true;
            return;
        }

        if (!network.anyPoweredLastTick)
            return;

        for (int i = 0; i < network.poweredBlockPositions.Count; i++)
            RemovePoweredElectricalBlockPosition(network.poweredBlockPositions[i]);

        network.activePoweredBlockCount = 0;
        network.anyPoweredLastTick = false;
        network.fullyPoweredLastTick = false;
    }

    private void ApplyPartialElectricalNetworkPoweredState(ElectricalNetworkRuntime network)
    {
        if (network == null)
        {
            electricalPoweredThisTick.Clear();
            return;
        }

        if (network.anyPoweredLastTick)
        {
            for (int i = 0; i < network.poweredBlockPositions.Count; i++)
            {
                Vector3Int blockPos = network.poweredBlockPositions[i];
                if (poweredElectricalBlockPositions.Contains(blockPos) &&
                    !electricalPoweredThisTick.Contains(blockPos))
                {
                    RemovePoweredElectricalBlockPosition(blockPos);
                }
            }
        }

        foreach (Vector3Int blockPos in electricalPoweredThisTick)
            AddOrRefreshPoweredElectricalBlockPosition(blockPos);

        network.activePoweredBlockCount = electricalPoweredThisTick.Count;
        network.anyPoweredLastTick = network.activePoweredBlockCount > 0;
        network.fullyPoweredLastTick = network.activePoweredBlockCount == network.poweredBlockPositions.Count &&
                                       network.poweredBlockPositions.Count > 0;
        electricalPoweredThisTick.Clear();
    }

    private void AddOrRefreshPoweredElectricalBlockPosition(Vector3Int blockPos)
    {
        if (!TryGetElectricalPoweredBlockConfig(GetBlockAt(blockPos), out ElectricalPoweredBlockConfig config))
            return;

        bool wasPowered = poweredElectricalBlockPositions.Contains(blockPos);
        bool emissionChanged = false;
        bool shouldHaveEmission = LightUtils.HasBlockLight(config.poweredEmission);
        bool hadEmission = poweredElectricalEmissionByPosition.TryGetValue(blockPos, out ushort existingEmission);

        if (shouldHaveEmission)
        {
            if (!hadEmission || existingEmission != config.poweredEmission)
            {
                if (hadEmission)
                    RemovePoweredElectricalBlockLight(blockPos, existingEmission);

                poweredElectricalEmissionByPosition[blockPos] = config.poweredEmission;
                PropagatePoweredElectricalBlockLight(blockPos, config.poweredEmission);
                emissionChanged = true;
            }
        }
        else if (hadEmission)
        {
            poweredElectricalEmissionByPosition.Remove(blockPos);
            RemovePoweredElectricalBlockLight(blockPos, existingEmission);
            emissionChanged = true;
        }

        if (!wasPowered)
            poweredElectricalBlockPositions.Add(blockPos);

        if (!wasPowered || emissionChanged)
            SyncElectricalPoweredBlockVisualState(blockPos, true);
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

            SyncElectricalPoweredBlockVisualState(blockPos, true);
        }

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
            if (network == null || !network.batteryVisualsDirty)
                continue;

            SyncElectricalBatteryVisualStates(network);
            network.batteryVisualsDirty = false;
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
        ResetElectricalNetworkPoweredRuntimeState();
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

    private void ResetElectricalNetworkPoweredRuntimeState()
    {
        for (int i = 0; i < electricalNetworks.Count; i++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[i];
            if (network == null)
                continue;

            network.activePoweredBlockCount = 0;
            network.anyPoweredLastTick = false;
            network.fullyPoweredLastTick = false;
        }
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
        return ResolveElectricalDirectCapacity(
            network,
            CountActiveWindMills(network),
            CountReadySteamEngines(network != null ? network.steamEnginePositions : null));
    }

    private float ResolveElectricalDirectCapacity(ElectricalNetworkRuntime network, int activeWindMillCount)
    {
        return ResolveElectricalDirectCapacity(network, activeWindMillCount, CountReadySteamEngines(network != null ? network.steamEnginePositions : null));
    }

    private float ResolveElectricalDirectCapacity(
        ElectricalNetworkRuntime network,
        int activeWindMillCount,
        int readySteamEngineCount)
    {
        if (network == null)
            return 0f;

        float solarCapacityRate = network.solarPanelCount * Mathf.Max(0f, solarPanelEnergyPerSecond);
        float windCapacityRate = Mathf.Max(0, activeWindMillCount) * Mathf.Max(0f, windMillEnergyPerSecond);
        float steamCapacityRate = Mathf.Max(0, readySteamEngineCount) * Mathf.Max(0f, steamEngineEnergyPerSecond);
        return Mathf.Max(0f, (solarCapacityRate + windCapacityRate + steamCapacityRate) * directSolarBufferSeconds);
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
            network.storedBatteryEnergy += accepted;
            network.batteryVisualsDirty = true;
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
        return available + Mathf.Max(0f, network.storedBatteryEnergy);
    }

    private float GetElectricalEnergyCapacity(ElectricalNetworkRuntime network)
    {
        if (network == null)
            return 0f;

        float capacity = Mathf.Max(0f, network.directCapacity);
        return capacity + network.batteryPositions.Count * GetBatteryCapacity();
    }

    private float GetBatteryFreeCapacity(ElectricalNetworkRuntime network)
    {
        if (network == null || network.batteryPositions.Count == 0)
            return 0f;

        float freeCapacity = 0f;
        float capacity = GetBatteryCapacity();
        for (int i = 0; i < network.batteryPositions.Count; i++)
        {
            Vector3Int batteryPos = network.batteryPositions[i];
            if (!BatteryBlockUtility.IsBatteryBlock(GetBlockAt(batteryPos)))
                continue;

            float stored = batteryEnergyByPosition.TryGetValue(batteryPos, out float currentEnergy)
                ? Mathf.Clamp(currentEnergy, 0f, capacity)
                : 0f;
            freeCapacity += Mathf.Max(0f, capacity - stored);
        }

        return freeCapacity;
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
            network.storedBatteryEnergy = Mathf.Max(0f, network.storedBatteryEnergy - consumed);
            network.batteryVisualsDirty = true;

            if (stored <= 0.0001f)
                batteryEnergyByPosition.Remove(batteryPos);
            else
                batteryEnergyByPosition[batteryPos] = stored;
        }
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
        bool previousWasElectrical = IsElectricalBlock(previousType);
        bool newIsElectrical = IsElectricalBlock(newType);
        if (!previousWasElectrical && !newIsElectrical)
            return;

        UpdateElectricalBlockIndexForChange(worldPos, previousWasElectrical, newIsElectrical);
        bool handledIncrementally = !previousWasElectrical &&
                                    newIsElectrical &&
                                    TryAddElectricalBlockIncrementally(worldPos);
        if (!handledIncrementally)
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

    private void UpdateElectricalBlockIndexForChange(
        Vector3Int worldPos,
        bool previousWasElectrical,
        bool newIsElectrical)
    {
        if (!electricalBlockIndexInitialized)
            return;

        if (newIsElectrical)
            electricalBlockPositions.Add(worldPos);
        else if (previousWasElectrical)
            electricalBlockPositions.Remove(worldPos);
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
               blockType == BlockType.SteamEngine ||
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
