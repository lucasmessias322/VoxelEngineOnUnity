using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly Queue<WaterUpdateCandidate> queuedWaterUpdates = new Queue<WaterUpdateCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedWaterUpdateSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> persistentWaterSources = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> waterSlopeVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);

    private static readonly Vector3Int[] WaterNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    private static readonly Vector3Int[] HorizontalWaterOffsets =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.forward,
        Vector3Int.back
    };

    private const int NoWaterSlopeDropCost = 1000;

    private struct WaterUpdateCandidate
    {
        public Vector3Int position;
        public float nextUpdateTime;
    }

    #region Water Simulation

    private void ProcessQueuedWaterUpdates()
    {
        if (!enableWater || !enableWaterSimulation || queuedWaterUpdates.Count == 0)
            return;

        int queuedCount = queuedWaterUpdates.Count;
        float now = Time.time;
        float stepStartTime = Time.realtimeSinceStartup;
        float effectiveWaterBudgetMS = waterUpdateTimeBudgetMS + Mathf.Clamp((queuedCount - waterUpdatesPerFrame) * 0.03f, 0f, 3f);
        float timeBudgetSeconds = effectiveWaterBudgetMS > 0f ? effectiveWaterBudgetMS / 1000f : 0f;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int processed = 0;
        int attempts = queuedCount;
        int backlogBoost = Mathf.Clamp(queuedCount / 8, 0, 64);
        int perFrameLimit = Mathf.Clamp(Mathf.Max(8, waterUpdatesPerFrame) + backlogBoost, 8, 96);

        while (processed < perFrameLimit && attempts-- > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            WaterUpdateCandidate candidate = queuedWaterUpdates.Dequeue();
            queuedWaterUpdateSet.Remove(candidate.position);

            if (candidate.nextUpdateTime > now)
            {
                RequeueWaterCandidate(candidate);
                continue;
            }

            if (!IsWorldPositionInsideSimulationDistance(candidate.position, simulationCenter))
            {
                TryQueueWaterUpdate(candidate.position, Mathf.Max(waterTickInterval, 0.25f));
                continue;
            }

            if (EvaluateWaterAt(candidate.position))
                processed++;
        }
    }

    private bool EvaluateWaterAt(Vector3Int worldPos)
    {
        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return false;

        BlockType current = GetBlockAt(worldPos);
        bool currentIsWater = FluidBlockUtility.IsWater(current);
        if (!currentIsWater && !CanWaterOccupy(current))
            return false;

        BlockType desiredState = GetDesiredWaterState(worldPos, current, currentIsWater);
        return SetWaterStateIfNeeded(worldPos, current, desiredState);
    }

    private BlockType GetDesiredWaterState(Vector3Int worldPos, BlockType current, bool currentIsWater)
    {
        if (IsFixedWaterSource(worldPos))
            return BlockType.Water;

        if (TryGetWaterStateFromAbove(worldPos, out BlockType fallingState))
            return fallingState;

        if (ShouldBecomeInfiniteWaterSource(worldPos))
            return BlockType.Water;

        if (TryGetHorizontalWaterState(worldPos, out BlockType flowingState))
            return flowingState;

        return currentIsWater ? BlockType.Air : current;
    }

    private bool TryGetWaterStateFromAbove(Vector3Int worldPos, out BlockType state)
    {
        BlockType above = GetBlockAt(worldPos + Vector3Int.up);
        if (!FluidBlockUtility.IsWater(above))
        {
            state = BlockType.Air;
            return false;
        }

        // Em Minecraft, quando a agua cai para um nivel inferior ela volta a
        // se comportar como uma nova coluna de queda, podendo espalhar mais
        // alguns blocos quando encontra apoio novamente.
        state = FluidBlockUtility.GetWaterBlockType(0, true);
        return true;
    }

    private bool TryGetHorizontalWaterState(Vector3Int worldPos, out BlockType state)
    {
        int bestDistance = int.MaxValue;
        bool found = false;
        bool targetShouldFall = CanWaterFlowDownInto(GetBlockAt(worldPos + Vector3Int.down));

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            Vector3Int sourcePos = worldPos + HorizontalWaterOffsets[i];
            if (!TryGetHorizontalWaterContribution(sourcePos, worldPos, targetShouldFall, out int candidateDistance))
                continue;

            if (candidateDistance < bestDistance)
            {
                bestDistance = candidateDistance;
                found = true;
            }
        }

        if (!found)
        {
            state = BlockType.Air;
            return false;
        }

        if (targetShouldFall)
        {
            state = FluidBlockUtility.GetWaterBlockType(0, true);
            return true;
        }

        state = FluidBlockUtility.GetWaterBlockType(bestDistance, false);
        return true;
    }

    private bool TryGetHorizontalWaterContribution(
        Vector3Int sourcePos,
        Vector3Int targetPos,
        bool targetShouldFall,
        out int candidateDistance)
    {
        candidateDistance = int.MaxValue;

        BlockType sourceType = GetBlockAt(sourcePos);
        if (!CanWaterSpreadHorizontallyFrom(sourcePos, sourceType))
            return false;

        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType) && !FluidBlockUtility.IsWater(targetType))
            return false;

        int directionIndex = GetHorizontalDirectionIndex(targetPos - sourcePos);
        if (directionIndex < 0)
            return false;

        // Suspended sources/waterfalls still cannot branch sideways because
        // CanWaterSpreadHorizontallyFrom() rejects any source that can already
        // flow downward. This keeps side-placed tower water vertical, while
        // still allowing supported surface water to spill over edges.
        if (!IsOptimalWaterSpreadDirection(sourcePos, directionIndex))
            return false;

        candidateDistance = FluidBlockUtility.GetWaterDistance(sourceType) + 1;
        return candidateDistance <= FluidBlockUtility.MaxWaterDistance;
    }

    private bool CanWaterSpreadHorizontallyFrom(Vector3Int sourcePos, BlockType sourceType)
    {
        if (!FluidBlockUtility.IsWater(sourceType))
            return false;

        if (FluidBlockUtility.GetWaterDistance(sourceType) >= FluidBlockUtility.MaxWaterDistance)
            return false;

        BlockType below = GetBlockAt(sourcePos + Vector3Int.down);
        if (CanWaterFlowDownInto(below))
            return false;

        bool supportedBelow = IsSolidBlock(below) || IsSourceWaterState(sourcePos + Vector3Int.down);
        if (!supportedBelow && FluidBlockUtility.IsWater(below))
            return false;

        // Falling columns should only spill at the bottom-most blocked cell, not from
        // every segment in the column.
        if (FluidBlockUtility.IsFallingWater(sourceType) && FluidBlockUtility.IsWater(below))
            return false;

        return true;
    }

    private bool IsOptimalWaterSpreadDirection(Vector3Int sourcePos, int directionIndex)
    {
        GetOptimalWaterFlowCosts(sourcePos, out int rightCost, out int leftCost, out int forwardCost, out int backCost, out int bestCost);
        if (bestCost == int.MaxValue)
            return false;

        int directionCost = directionIndex switch
        {
            0 => rightCost,
            1 => leftCost,
            2 => forwardCost,
            3 => backCost,
            _ => int.MaxValue
        };

        return directionCost == bestCost;
    }

    private void GetOptimalWaterFlowCosts(
        Vector3Int sourcePos,
        out int rightCost,
        out int leftCost,
        out int forwardCost,
        out int backCost,
        out int bestCost)
    {
        rightCost = GetFlowCostForDirection(sourcePos, 0);
        leftCost = GetFlowCostForDirection(sourcePos, 1);
        forwardCost = GetFlowCostForDirection(sourcePos, 2);
        backCost = GetFlowCostForDirection(sourcePos, 3);

        bestCost = Mathf.Min(rightCost, leftCost);
        bestCost = Mathf.Min(bestCost, forwardCost);
        bestCost = Mathf.Min(bestCost, backCost);
    }

    private int GetFlowCostForDirection(Vector3Int sourcePos, int directionIndex)
    {
        Vector3Int targetPos = sourcePos + HorizontalWaterOffsets[directionIndex];
        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType))
            return int.MaxValue;

        if (CanWaterFlowDownInto(GetBlockAt(targetPos + Vector3Int.down)))
            return 0;

        waterSlopeVisited.Clear();
        waterSlopeVisited.Add(targetPos);
        int bestDistance = GetWaterSlopeDistance(targetPos, 1, GetOppositeHorizontalDirection(directionIndex));
        waterSlopeVisited.Clear();
        return bestDistance;
    }

    private int GetWaterSlopeDistance(Vector3Int origin, int depth, int blockedDirection)
    {
        int maxDepth = Mathf.Max(1, waterSlopeSearchDistance);
        if (depth >= maxDepth)
            return NoWaterSlopeDropCost;

        int bestDistance = NoWaterSlopeDropCost;

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (i == blockedDirection)
                continue;

            Vector3Int nextPos = origin + HorizontalWaterOffsets[i];
            if (!waterSlopeVisited.Add(nextPos))
                continue;

            BlockType nextType = GetBlockAt(nextPos);
            if (!CanWaterOccupy(nextType))
            {
                waterSlopeVisited.Remove(nextPos);
                continue;
            }

            if (CanWaterFlowDownInto(GetBlockAt(nextPos + Vector3Int.down)))
            {
                waterSlopeVisited.Remove(nextPos);
                return depth;
            }

            int candidate = GetWaterSlopeDistance(nextPos, depth + 1, GetOppositeHorizontalDirection(i));
            if (candidate < bestDistance)
                bestDistance = candidate;

            waterSlopeVisited.Remove(nextPos);
        }

        return bestDistance;
    }

    private bool ShouldBecomeInfiniteWaterSource(Vector3Int worldPos)
    {
        BlockType below = GetBlockAt(worldPos + Vector3Int.down);
        bool supportedBelow = IsSolidBlock(below) || IsSourceWaterState(worldPos + Vector3Int.down);
        if (!supportedBelow)
            return false;

        int adjacentSources = 0;
        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (IsSourceWaterState(worldPos + HorizontalWaterOffsets[i]))
            {
                adjacentSources++;
                if (adjacentSources >= 2)
                    return true;
            }
        }

        return false;
    }

    private bool SetWaterStateIfNeeded(Vector3Int worldPos, BlockType current, BlockType desired)
    {
        if (current == desired)
            return false;

        if (desired == BlockType.Air && !FluidBlockUtility.IsWater(current))
            return false;

        SetBlockAt(worldPos, desired);
        return true;
    }

    private bool IsSourceWaterState(Vector3Int worldPos)
    {
        BlockType blockType = GetBlockAt(worldPos);
        return FluidBlockUtility.IsStillWater(blockType) && !FluidBlockUtility.IsFallingWater(blockType);
    }

    private bool IsFixedWaterSource(Vector3Int worldPos)
    {
        if (!enableWater)
            return false;

        if (persistentWaterSources.Contains(worldPos))
            return true;

        if (blockOverrides.ContainsKey(worldPos))
            return false;

        return GetProceduralBlockFast(worldPos) == BlockType.Water;
    }

    private bool CanWaterOccupy(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return true;

        if (TorchPlacementUtility.IsTorchLike(blockType))
            return true;

        if (blockData != null)
        {
            BlockTextureMapping? mapping = blockData.GetMapping(blockType);
            if (mapping != null)
            {
                BlockTextureMapping value = mapping.Value;
                return !value.isSolid && !value.isLiquid;
            }
        }

        return !IsSolidBlock(blockType) && !IsLiquidBlock(blockType);
    }

    private bool CanWaterFlowDownInto(BlockType blockType)
    {
        if (FluidBlockUtility.IsWater(blockType))
            return false;

        return CanWaterOccupy(blockType);
    }

    private static int GetHorizontalDirectionIndex(Vector3Int delta)
    {
        if (delta == Vector3Int.right) return 0;
        if (delta == Vector3Int.left) return 1;
        if (delta == Vector3Int.forward) return 2;
        if (delta == Vector3Int.back) return 3;
        return -1;
    }

    private static int GetOppositeHorizontalDirection(int directionIndex)
    {
        return directionIndex switch
        {
            0 => 1,
            1 => 0,
            2 => 3,
            3 => 2,
            _ => -1
        };
    }

    private void HandleWaterBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType, bool placedByPlayer)
    {
        if (!FluidBlockUtility.IsWater(newType))
        {
            persistentWaterSources.Remove(worldPos);
        }
        else if (placedByPlayer)
        {
            persistentWaterSources.Add(worldPos);
        }

        if (!enableWater || !enableWaterSimulation)
            return;

        float delay = Mathf.Max(0f, waterTickInterval);
        bool isWaterNow = FluidBlockUtility.IsWater(newType);
        bool wasWater = FluidBlockUtility.IsWater(previousType);
        bool becameWater = isWaterNow && !wasWater;

        TryQueueWaterUpdate(worldPos, becameWater ? 0f : delay);

        if (isWaterNow)
        {
            // Waterfalls should extend downward immediately instead of waiting for
            // the generic neighbor delay, which removes the odd "hanging surface"
            // feeling when placing water high on a wall.
            TryQueueWaterUpdate(worldPos + Vector3Int.down, 0f);
            TryQueueWaterUpdate(worldPos + Vector3Int.up, delay);
            for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
                TryQueueWaterUpdate(worldPos + HorizontalWaterOffsets[i], delay);
            return;
        }

        for (int i = 0; i < WaterNeighborOffsets.Length; i++)
            TryQueueWaterUpdate(worldPos + WaterNeighborOffsets[i], delay);
    }

    private void TryQueueWaterUpdate(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableWater || !enableWaterSimulation)
            return;

        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return;

        if (!queuedWaterUpdateSet.Add(worldPos))
            return;

        queuedWaterUpdates.Enqueue(new WaterUpdateCandidate
        {
            position = worldPos,
            nextUpdateTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private void RequeueWaterCandidate(WaterUpdateCandidate candidate)
    {
        if (!queuedWaterUpdateSet.Add(candidate.position))
            return;

        queuedWaterUpdates.Enqueue(candidate);
    }

    #endregion
}
