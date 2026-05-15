using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private const string SteamEngineCoalResourcePath = "Itens/Item/Coal";
    private const float DefaultSteamEngineWaterPerEnergy = 0.02777778f;
    private const float DefaultSteamEngineWaterCapacity = 5f;
    private const float DefaultWaterPumpWaterPerSecond = 1f;

    private static readonly Vector3Int[] steamEngineFluidDirections =
    {
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.back,
        Vector3Int.forward,
        Vector3Int.down,
        Vector3Int.up
    };

    private readonly Dictionary<Vector3Int, SteamEngineRuntimeState> steamEngineStates =
        new Dictionary<Vector3Int, SteamEngineRuntimeState>(InitialBlockEditCapacity);
    private readonly Queue<Vector3Int> steamEngineFluidSearchQueue = new Queue<Vector3Int>(128);
    private readonly HashSet<Vector3Int> steamEngineFluidSearchVisited =
        new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly List<Vector3Int> steamEngineWaterPumpSearchResults = new List<Vector3Int>(8);
    private readonly Dictionary<Vector3Int, int> steamEngineWaterPumpConsumerCounts =
        new Dictionary<Vector3Int, int>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, float> steamEngineProducedEnergyThisTick =
        new Dictionary<Vector3Int, float>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> steamEngineWaterRefillVisited =
        new HashSet<Vector3Int>(InitialBlockEditCapacity);

    private Item cachedSteamEngineCoalItem;

    private sealed class SteamEngineRuntimeState
    {
        public Item FuelItem;
        public int FuelAmount;
        public float BurnTimer;
        public float CurrentFuelDuration;
        public float WaterAmount;
    }

    public bool IsSteamEngineBlockInWorld(Vector3Int steamEnginePos)
    {
        return GetBlockAt(steamEnginePos) == BlockType.SteamEngine;
    }

    public bool CanUseSteamEngineFuel(Item item)
    {
        if (item == null)
            return false;

        Item coalItem = ResolveSteamEngineCoalItem();
        if (coalItem != null)
            return item == coalItem;

        return string.Equals(item.name, "Coal", System.StringComparison.OrdinalIgnoreCase);
    }

    public Item GetSteamEngineFuelItem(Vector3Int steamEnginePos)
    {
        return steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state)
            ? state.FuelItem
            : null;
    }

    public int GetSteamEngineFuelAmount(Vector3Int steamEnginePos)
    {
        return steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state)
            ? Mathf.Max(0, state.FuelAmount)
            : 0;
    }

    public float GetSteamEngineFuel01(Vector3Int steamEnginePos)
    {
        if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) ||
            state.CurrentFuelDuration <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(state.BurnTimer / state.CurrentFuelDuration);
    }

    public float GetSteamEngineWater01(Vector3Int steamEnginePos)
    {
        if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) || state == null)
            return HasSteamEngineWaterSupply(steamEnginePos) ? 1f : 0f;

        float capacity = GetSteamEngineWaterCapacity();
        return capacity > 0f ? Mathf.Clamp01(state.WaterAmount / capacity) : 0f;
    }

    public bool IsSteamEngineGenerating(Vector3Int steamEnginePos)
    {
        return IsSteamEngineBlockInWorld(steamEnginePos) &&
               steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) &&
               HasSteamEngineUsableWater(state) &&
               state.BurnTimer > 0f;
    }

    public int InsertSteamEngineFuel(Vector3Int steamEnginePos, Item item, int amount)
    {
        return InsertSteamEngineFuel(steamEnginePos, item, amount, int.MaxValue);
    }

    public int InsertSteamEngineFuel(Vector3Int steamEnginePos, Item item, int amount, int maxFuelAmount)
    {
        if (!IsSteamEngineBlockInWorld(steamEnginePos) ||
            !CanUseSteamEngineFuel(item) ||
            amount <= 0 ||
            maxFuelAmount <= 0)
        {
            return amount;
        }

        SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
        int stackLimit = Mathf.Min(Mathf.Max(1, item.maxStack), Mathf.Max(0, maxFuelAmount));
        if (state.FuelItem == null || state.FuelAmount <= 0)
        {
            int moved = Mathf.Min(stackLimit, amount);
            state.FuelItem = item;
            state.FuelAmount = moved;
            return amount - moved;
        }

        if (state.FuelItem != item)
            return amount;

        int freeSpace = Mathf.Max(0, stackLimit - state.FuelAmount);
        int addNow = Mathf.Min(freeSpace, amount);
        if (addNow <= 0)
            return amount;

        state.FuelAmount += addNow;
        return amount - addNow;
    }

    public int GetInsertableSteamEngineFuelCount(Vector3Int steamEnginePos, Item item, int amount, int maxFuelAmount)
    {
        if (!IsSteamEngineBlockInWorld(steamEnginePos) ||
            !CanUseSteamEngineFuel(item) ||
            amount <= 0 ||
            maxFuelAmount <= 0)
        {
            return 0;
        }

        int stackLimit = Mathf.Min(Mathf.Max(1, item.maxStack), Mathf.Max(0, maxFuelAmount));
        if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) ||
            state == null ||
            state.FuelItem == null ||
            state.FuelAmount <= 0)
        {
            return Mathf.Min(stackLimit, amount);
        }

        if (state.FuelItem != item)
            return 0;

        int freeSpace = Mathf.Max(0, stackLimit - state.FuelAmount);
        return Mathf.Min(freeSpace, amount);
    }

    public void SetSteamEngineFuelContents(Vector3Int steamEnginePos, Item item, int amount)
    {
        if (!IsSteamEngineBlockInWorld(steamEnginePos))
            return;

        SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
        if (item == null || amount <= 0 || !CanUseSteamEngineFuel(item))
        {
            state.FuelItem = null;
            state.FuelAmount = 0;
            return;
        }

        state.FuelItem = item;
        state.FuelAmount = Mathf.Clamp(amount, 0, Mathf.Max(1, item.maxStack));
    }

    public int RemoveSteamEngineFuel(Vector3Int steamEnginePos, int amount)
    {
        if (amount <= 0 ||
            !steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) ||
            state.FuelItem == null ||
            state.FuelAmount <= 0)
        {
            return 0;
        }

        int removed = Mathf.Min(amount, state.FuelAmount);
        state.FuelAmount -= removed;
        if (state.FuelAmount <= 0)
        {
            state.FuelItem = null;
            state.FuelAmount = 0;
        }

        return removed;
    }

    private float TickSteamEnginesAndGetProducedEnergy(
        IReadOnlyList<Vector3Int> steamEnginePositions,
        float requestedEnergy,
        float deltaTime)
    {
        if (steamEnginePositions == null ||
            steamEnginePositions.Count == 0 ||
            requestedEnergy <= 0f ||
            deltaTime <= 0f)
        {
            return 0f;
        }

        float producedEnergy = 0f;
        float remainingRequestedEnergy = requestedEnergy;
        float energyPerSecond = Mathf.Max(0f, steamEngineEnergyPerSecond);
        if (energyPerSecond <= 0f)
            return 0f;

        for (int i = 0; i < steamEnginePositions.Count; i++)
        {
            Vector3Int steamEnginePos = steamEnginePositions[i];
            if (!IsSteamEngineBlockInWorld(steamEnginePos))
                continue;

            SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
            float remainingEngineCapacity = energyPerSecond * deltaTime;
            while (remainingRequestedEnergy > 0.0001f && remainingEngineCapacity > 0.0001f)
            {
                if (!HasSteamEngineUsableWater(state))
                    break;

                if (state.BurnTimer <= 0f && !TryStartSteamEngineFuelBurn(state))
                    break;

                float fuelEnergyAvailable = state.BurnTimer * energyPerSecond;
                float waterEnergyAvailable = GetSteamEngineWaterEnergyAvailable(state);
                float produceNow = Mathf.Min(remainingRequestedEnergy, remainingEngineCapacity, fuelEnergyAvailable, waterEnergyAvailable);
                if (produceNow <= 0.0001f)
                    break;

                float burnSeconds = produceNow / energyPerSecond;
                state.BurnTimer = Mathf.Max(0f, state.BurnTimer - burnSeconds);
                ConsumeSteamEngineWaterForEnergy(state, produceNow);
                remainingEngineCapacity -= produceNow;
                remainingRequestedEnergy -= produceNow;
                producedEnergy += produceNow;
                AddSteamEngineProducedEnergyThisTick(steamEnginePos, produceNow);
            }

            if (remainingRequestedEnergy <= 0.0001f)
                break;
        }

        return producedEnergy;
    }

    private float TickSteamEnginesIdleAndGetProducedEnergy(
        IReadOnlyList<Vector3Int> steamEnginePositions,
        float requestedEnergy,
        float deltaTime)
    {
        if (steamEnginePositions == null ||
            steamEnginePositions.Count == 0 ||
            requestedEnergy <= 0f ||
            deltaTime <= 0f)
        {
            return 0f;
        }

        float idleMultiplier = Mathf.Clamp01(steamEngineIdleBurnMultiplier);
        float energyPerSecond = Mathf.Max(0f, steamEngineEnergyPerSecond);
        if (idleMultiplier <= 0f || energyPerSecond <= 0f)
            return 0f;

        float producedEnergy = 0f;
        float remainingRequestedEnergyBudget = requestedEnergy;
        float requestedEnergyPerEngine = energyPerSecond * idleMultiplier * deltaTime;
        for (int i = 0; i < steamEnginePositions.Count; i++)
        {
            if (remainingRequestedEnergyBudget <= 0.0001f)
                break;

            Vector3Int steamEnginePos = steamEnginePositions[i];
            if (!IsSteamEngineBlockInWorld(steamEnginePos))
                continue;

            SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
            if (!HasSteamEngineUsableWater(state))
                continue;

            steamEngineProducedEnergyThisTick.TryGetValue(steamEnginePos, out float alreadyProducedEnergy);
            float remainingRequestedEnergy = Mathf.Min(
                Mathf.Max(0f, requestedEnergyPerEngine - alreadyProducedEnergy),
                remainingRequestedEnergyBudget);
            if (remainingRequestedEnergy <= 0.0001f)
                continue;

            float remainingEngineCapacity = energyPerSecond * deltaTime;
            while (remainingRequestedEnergy > 0.0001f && remainingEngineCapacity > 0.0001f)
            {
                if (!HasSteamEngineUsableWater(state))
                    break;

                if (state.BurnTimer <= 0f && !TryStartSteamEngineFuelBurn(state))
                    break;

                float fuelEnergyAvailable = state.BurnTimer * energyPerSecond;
                float waterEnergyAvailable = GetSteamEngineWaterEnergyAvailable(state);
                float produceNow = Mathf.Min(remainingRequestedEnergy, remainingEngineCapacity, fuelEnergyAvailable, waterEnergyAvailable);
                if (produceNow <= 0.0001f)
                    break;

                float burnSeconds = produceNow / energyPerSecond;
                state.BurnTimer = Mathf.Max(0f, state.BurnTimer - burnSeconds);
                ConsumeSteamEngineWaterForEnergy(state, produceNow);
                remainingRequestedEnergy -= produceNow;
                remainingRequestedEnergyBudget -= produceNow;
                remainingEngineCapacity -= produceNow;
                producedEnergy += produceNow;
                AddSteamEngineProducedEnergyThisTick(steamEnginePos, produceNow);
            }
        }

        return producedEnergy;
    }

    private void AddSteamEngineProducedEnergyThisTick(Vector3Int steamEnginePos, float producedEnergy)
    {
        if (producedEnergy <= 0f)
            return;

        steamEngineProducedEnergyThisTick.TryGetValue(steamEnginePos, out float previousEnergy);
        steamEngineProducedEnergyThisTick[steamEnginePos] = previousEnergy + producedEnergy;
    }

    private int CountReadySteamEngines(IReadOnlyList<Vector3Int> steamEnginePositions)
    {
        if (steamEnginePositions == null || steamEnginePositions.Count == 0)
            return 0;

        int count = 0;
        for (int i = 0; i < steamEnginePositions.Count; i++)
        {
            Vector3Int steamEnginePos = steamEnginePositions[i];
            if (!IsSteamEngineBlockInWorld(steamEnginePos))
                continue;

            SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
            if (!HasSteamEngineUsableWater(state))
                continue;

            if (state.BurnTimer > 0f || HasUsableSteamEngineFuel(state))
                count++;
        }

        return count;
    }

    private float GetAvailableSteamEngineEnergy(IReadOnlyList<Vector3Int> steamEnginePositions)
    {
        if (steamEnginePositions == null || steamEnginePositions.Count == 0)
            return 0f;

        float energyPerSecond = Mathf.Max(0f, steamEngineEnergyPerSecond);
        if (energyPerSecond <= 0f)
            return 0f;

        float availableEnergy = 0f;
        float fuelDuration = Mathf.Max(0.1f, steamEngineCoalBurnDuration);
        for (int i = 0; i < steamEnginePositions.Count; i++)
        {
            Vector3Int steamEnginePos = steamEnginePositions[i];
            if (!IsSteamEngineBlockInWorld(steamEnginePos))
                continue;

            if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) || state == null)
                continue;

            float waterLimitedEnergy = GetSteamEngineWaterEnergyAvailable(state);
            if (waterLimitedEnergy <= 0.0001f)
                continue;

            float fuelLimitedEnergy = Mathf.Max(0f, state.BurnTimer) * energyPerSecond;
            if (HasUsableSteamEngineFuel(state))
                fuelLimitedEnergy += Mathf.Max(0, state.FuelAmount) * fuelDuration * energyPerSecond;

            availableEnergy += Mathf.Min(waterLimitedEnergy, fuelLimitedEnergy);
        }

        return availableEnergy;
    }

    private bool TryStartSteamEngineFuelBurn(SteamEngineRuntimeState state)
    {
        if (!HasUsableSteamEngineFuel(state))
            return false;

        state.FuelAmount--;
        if (state.FuelAmount <= 0)
        {
            state.FuelItem = null;
            state.FuelAmount = 0;
        }

        state.CurrentFuelDuration = Mathf.Max(0.1f, steamEngineCoalBurnDuration);
        state.BurnTimer = state.CurrentFuelDuration;
        return true;
    }

    private bool HasUsableSteamEngineFuel(SteamEngineRuntimeState state)
    {
        return state != null &&
               state.FuelItem != null &&
               state.FuelAmount > 0 &&
               CanUseSteamEngineFuel(state.FuelItem);
    }

    private SteamEngineRuntimeState GetOrCreateSteamEngineState(Vector3Int steamEnginePos)
    {
        if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) || state == null)
        {
            state = new SteamEngineRuntimeState();
            steamEngineStates[steamEnginePos] = state;
        }

        return state;
    }

    private void RefillSteamEngineWaterForAllNetworks(float deltaTime)
    {
        if (electricalNetworks == null || electricalNetworks.Count == 0 || deltaTime <= 0f)
            return;

        float capacity = GetSteamEngineWaterCapacity();
        if (capacity <= 0f)
            return;

        steamEngineWaterPumpConsumerCounts.Clear();
        steamEngineWaterRefillVisited.Clear();

        for (int networkIndex = 0; networkIndex < electricalNetworks.Count; networkIndex++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[networkIndex];
            if (network == null)
                continue;

            IReadOnlyList<Vector3Int> steamEnginePositions = network.steamEnginePositions;
            if (steamEnginePositions == null)
                continue;

            for (int i = 0; i < steamEnginePositions.Count; i++)
            {
                Vector3Int steamEnginePos = steamEnginePositions[i];
                if (!steamEngineWaterRefillVisited.Add(steamEnginePos) ||
                    !IsSteamEngineBlockInWorld(steamEnginePos))
                {
                    continue;
                }

                SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
                if (state.WaterAmount >= capacity - 0.0001f)
                    continue;

                if (!TryCollectSteamEngineWaterPumps(steamEnginePos, steamEngineWaterPumpSearchResults))
                    continue;

                for (int pumpIndex = 0; pumpIndex < steamEngineWaterPumpSearchResults.Count; pumpIndex++)
                {
                    Vector3Int pumpPos = steamEngineWaterPumpSearchResults[pumpIndex];
                    steamEngineWaterPumpConsumerCounts.TryGetValue(pumpPos, out int consumerCount);
                    steamEngineWaterPumpConsumerCounts[pumpPos] = consumerCount + 1;
                }
            }
        }

        if (steamEngineWaterPumpConsumerCounts.Count == 0)
            return;

        steamEngineWaterRefillVisited.Clear();

        for (int networkIndex = 0; networkIndex < electricalNetworks.Count; networkIndex++)
        {
            ElectricalNetworkRuntime network = electricalNetworks[networkIndex];
            if (network == null)
                continue;

            IReadOnlyList<Vector3Int> steamEnginePositions = network.steamEnginePositions;
            if (steamEnginePositions == null)
                continue;

            for (int i = 0; i < steamEnginePositions.Count; i++)
            {
                Vector3Int steamEnginePos = steamEnginePositions[i];
                if (!steamEngineWaterRefillVisited.Add(steamEnginePos) ||
                    !IsSteamEngineBlockInWorld(steamEnginePos))
                {
                    continue;
                }

                SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
                float waterNeeded = capacity - state.WaterAmount;
                if (waterNeeded <= 0.0001f)
                    continue;

                if (!TryCollectSteamEngineWaterPumps(steamEnginePos, steamEngineWaterPumpSearchResults))
                    continue;

                float refillAmount = 0f;
                for (int pumpIndex = 0; pumpIndex < steamEngineWaterPumpSearchResults.Count; pumpIndex++)
                {
                    Vector3Int pumpPos = steamEngineWaterPumpSearchResults[pumpIndex];
                    if (!steamEngineWaterPumpConsumerCounts.TryGetValue(pumpPos, out int consumerCount) || consumerCount <= 0)
                        continue;

                    refillAmount += GetWaterPumpFlowPerSecond(pumpPos) * deltaTime / consumerCount;
                }

                if (refillAmount > 0f)
                    state.WaterAmount = Mathf.Min(capacity, state.WaterAmount + Mathf.Min(waterNeeded, refillAmount));
            }
        }
    }

    private bool HasSteamEngineWaterSupply(Vector3Int steamEnginePos)
    {
        return TryCollectSteamEngineWaterPumps(steamEnginePos, steamEngineWaterPumpSearchResults);
    }

    private bool TryCollectSteamEngineWaterPumps(Vector3Int steamEnginePos, List<Vector3Int> waterPumpPositions)
    {
        if (waterPumpPositions == null)
            return false;

        waterPumpPositions.Clear();
        if (!IsSteamEngineBlockInWorld(steamEnginePos))
            return false;

        steamEngineFluidSearchQueue.Clear();
        steamEngineFluidSearchVisited.Clear();

        for (int i = 0; i < steamEngineFluidDirections.Length; i++)
        {
            Vector3Int direction = steamEngineFluidDirections[i];
            Vector3Int neighbor = steamEnginePos + direction;
            if (!FluidPipeUtility.IsFluidPipeBlock(GetBlockAt(neighbor)))
                continue;

            if (!FluidPipeUtility.CanConnect(this, neighbor, -direction))
                continue;

            EnqueueSteamEngineFluidPipe(neighbor);
        }

        while (steamEngineFluidSearchQueue.Count > 0)
        {
            Vector3Int pipePos = steamEngineFluidSearchQueue.Dequeue();
            for (int i = 0; i < steamEngineFluidDirections.Length; i++)
            {
                Vector3Int direction = steamEngineFluidDirections[i];
                if (!FluidPipeUtility.CanConnect(this, pipePos, direction))
                    continue;

                Vector3Int neighbor = pipePos + direction;
                BlockType neighborType = GetBlockAt(neighbor);
                if (neighborType == BlockType.WaterPump)
                {
                    if (IsWaterPumpSupplying(neighbor) && !waterPumpPositions.Contains(neighbor))
                        waterPumpPositions.Add(neighbor);

                    continue;
                }

                if (FluidPipeUtility.IsFluidPipeBlock(neighborType))
                    EnqueueSteamEngineFluidPipe(neighbor);
            }
        }

        return waterPumpPositions.Count > 0;
    }

    private void EnqueueSteamEngineFluidPipe(Vector3Int pipePos)
    {
        if (!steamEngineFluidSearchVisited.Add(pipePos))
            return;

        steamEngineFluidSearchQueue.Enqueue(pipePos);
    }

    public bool IsWaterPumpSupplying(Vector3Int pumpPos)
    {
        return GetBlockAt(pumpPos) == BlockType.WaterPump &&
               CountWaterPumpSourceBlocks(pumpPos) >= Mathf.Max(1, waterPumpRequiredWaterBlocks);
    }

    private float GetWaterPumpFlowPerSecond(Vector3Int pumpPos)
    {
        return IsWaterPumpSupplying(pumpPos) ? GetWaterPumpWaterPerSecond() : 0f;
    }

    private float GetSteamEngineWaterCapacity()
    {
        return steamEngineWaterCapacity > 0f
            ? Mathf.Max(0.1f, steamEngineWaterCapacity)
            : DefaultSteamEngineWaterCapacity;
    }

    private float GetSteamEngineWaterEnergyAvailable(SteamEngineRuntimeState state)
    {
        if (state == null)
            return 0f;

        return Mathf.Max(0f, state.WaterAmount) / GetSteamEngineWaterPerEnergy();
    }

    private bool HasSteamEngineUsableWater(SteamEngineRuntimeState state)
    {
        return state != null && state.WaterAmount > 0.0001f;
    }

    private void ConsumeSteamEngineWaterForEnergy(SteamEngineRuntimeState state, float producedEnergy)
    {
        if (state == null || producedEnergy <= 0f)
            return;

        state.WaterAmount = Mathf.Max(
            0f,
            state.WaterAmount - producedEnergy * GetSteamEngineWaterPerEnergy());
    }

    private float GetSteamEngineWaterPerEnergy()
    {
        return steamEngineWaterPerEnergy > 0f
            ? Mathf.Max(0.0001f, steamEngineWaterPerEnergy)
            : DefaultSteamEngineWaterPerEnergy;
    }

    private float GetWaterPumpWaterPerSecond()
    {
        return waterPumpWaterPerSecond > 0f
            ? Mathf.Max(0.0001f, waterPumpWaterPerSecond)
            : DefaultWaterPumpWaterPerSecond;
    }

    public int CountWaterPumpSourceBlocks(Vector3Int pumpPos)
    {
        int radius = Mathf.Max(1, waterPumpWaterSearchHorizontalRadius);
        int belowBlocks = Mathf.Max(0, waterPumpWaterSearchBelowBlocks);
        int count = 0;

        for (int yOffset = -belowBlocks; yOffset <= 0; yOffset++)
        {
            for (int zOffset = -radius; zOffset <= radius; zOffset++)
            {
                for (int xOffset = -radius; xOffset <= radius; xOffset++)
                {
                    if (xOffset == 0 && yOffset == 0 && zOffset == 0)
                        continue;

                    Vector3Int candidate = pumpPos + new Vector3Int(xOffset, yOffset, zOffset);
                    if (FluidBlockUtility.IsWater(GetBlockAt(candidate)))
                        count++;
                }
            }
        }

        return count;
    }

    private Item ResolveSteamEngineCoalItem()
    {
        if (cachedSteamEngineCoalItem != null)
            return cachedSteamEngineCoalItem;

        cachedSteamEngineCoalItem = Resources.Load<Item>(SteamEngineCoalResourcePath);
        return cachedSteamEngineCoalItem;
    }

    private void HandleSteamEngineBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (previousType != BlockType.SteamEngine || newType == BlockType.SteamEngine)
            return;

        if (steamEngineStates.TryGetValue(worldPos, out SteamEngineRuntimeState state) &&
            state != null &&
            state.FuelItem != null &&
            state.FuelAmount > 0)
        {
            ChestUIController.TrySpawnItemStack(
                state.FuelItem,
                state.FuelAmount,
                worldPos + Vector3.one * 0.5f + Vector3.up * 0.15f,
                Vector3.up * 0.9f);
        }

        steamEngineStates.Remove(worldPos);
    }
}
