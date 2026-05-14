using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private const string SteamEngineCoalResourcePath = "Itens/Item/Coal";

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

    private Item cachedSteamEngineCoalItem;

    private sealed class SteamEngineRuntimeState
    {
        public Item FuelItem;
        public int FuelAmount;
        public float BurnTimer;
        public float CurrentFuelDuration;
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
        return HasSteamEngineWaterSupply(steamEnginePos) ? 1f : 0f;
    }

    public bool IsSteamEngineGenerating(Vector3Int steamEnginePos)
    {
        return IsSteamEngineBlockInWorld(steamEnginePos) &&
               HasSteamEngineWaterSupply(steamEnginePos) &&
               steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) &&
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
            if (!IsSteamEngineBlockInWorld(steamEnginePos) || !HasSteamEngineWaterSupply(steamEnginePos))
                continue;

            SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);
            float remainingEngineCapacity = energyPerSecond * deltaTime;
            while (remainingRequestedEnergy > 0.0001f && remainingEngineCapacity > 0.0001f)
            {
                if (state.BurnTimer <= 0f && !TryStartSteamEngineFuelBurn(state))
                    break;

                float fuelEnergyAvailable = state.BurnTimer * energyPerSecond;
                float produceNow = Mathf.Min(remainingRequestedEnergy, remainingEngineCapacity, fuelEnergyAvailable);
                if (produceNow <= 0.0001f)
                    break;

                float burnSeconds = produceNow / energyPerSecond;
                state.BurnTimer = Mathf.Max(0f, state.BurnTimer - burnSeconds);
                remainingEngineCapacity -= produceNow;
                remainingRequestedEnergy -= produceNow;
                producedEnergy += produceNow;
            }

            if (remainingRequestedEnergy <= 0.0001f)
                break;
        }

        return producedEnergy;
    }

    private int CountReadySteamEngines(IReadOnlyList<Vector3Int> steamEnginePositions)
    {
        if (steamEnginePositions == null || steamEnginePositions.Count == 0)
            return 0;

        int count = 0;
        for (int i = 0; i < steamEnginePositions.Count; i++)
        {
            Vector3Int steamEnginePos = steamEnginePositions[i];
            if (!IsSteamEngineBlockInWorld(steamEnginePos) || !HasSteamEngineWaterSupply(steamEnginePos))
                continue;

            SteamEngineRuntimeState state = GetOrCreateSteamEngineState(steamEnginePos);

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
            if (!IsSteamEngineBlockInWorld(steamEnginePos) || !HasSteamEngineWaterSupply(steamEnginePos))
                continue;

            if (!steamEngineStates.TryGetValue(steamEnginePos, out SteamEngineRuntimeState state) || state == null)
                continue;

            availableEnergy += Mathf.Max(0f, state.BurnTimer) * energyPerSecond;
            if (HasUsableSteamEngineFuel(state))
                availableEnergy += Mathf.Max(0, state.FuelAmount) * fuelDuration * energyPerSecond;
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

    private bool HasSteamEngineWaterSupply(Vector3Int steamEnginePos)
    {
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
                    if (IsWaterPumpSupplying(neighbor))
                        return true;

                    continue;
                }

                if (FluidPipeUtility.IsFluidPipeBlock(neighborType))
                    EnqueueSteamEngineFluidPipe(neighbor);
            }
        }

        return false;
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
