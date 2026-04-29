using System.Collections.Generic;
using UnityEngine;

public sealed class VoxelAStarPathfinder
{
    private static readonly Vector3Int[] HorizontalDirections =
    {
        new Vector3Int(1, 0, 0),
        new Vector3Int(-1, 0, 0),
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0, -1),
        new Vector3Int(1, 0, 1),
        new Vector3Int(1, 0, -1),
        new Vector3Int(-1, 0, 1),
        new Vector3Int(-1, 0, -1)
    };

    private struct NodeRecord
    {
        public Vector3Int position;
        public int gCost;
        public int hCost;
        public int parentIndex;
        public bool closed;
    }

    private struct HeapItem
    {
        public int nodeIndex;
        public int fCost;
        public int hCost;
        public int gCost;
    }

    private sealed class MinHeap
    {
        private readonly List<HeapItem> items = new List<HeapItem>(512);

        public int Count => items.Count;

        public void Clear()
        {
            items.Clear();
        }

        public void Push(HeapItem item)
        {
            items.Add(item);

            int childIndex = items.Count - 1;
            while (childIndex > 0)
            {
                int parentIndex = (childIndex - 1) >> 1;
                if (HasHigherPriority(items[parentIndex], item))
                    break;

                items[childIndex] = items[parentIndex];
                childIndex = parentIndex;
            }

            items[childIndex] = item;
        }

        public HeapItem Pop()
        {
            HeapItem root = items[0];
            int lastIndex = items.Count - 1;
            HeapItem last = items[lastIndex];
            items.RemoveAt(lastIndex);

            if (lastIndex == 0)
                return root;

            int parentIndex = 0;
            while (true)
            {
                int leftChild = parentIndex * 2 + 1;
                if (leftChild >= lastIndex)
                    break;

                int rightChild = leftChild + 1;
                int bestChild = rightChild < lastIndex && HasHigherPriority(items[rightChild], items[leftChild])
                    ? rightChild
                    : leftChild;

                if (HasHigherPriority(last, items[bestChild]))
                    break;

                items[parentIndex] = items[bestChild];
                parentIndex = bestChild;
            }

            items[parentIndex] = last;
            return root;
        }

        private static bool HasHigherPriority(HeapItem a, HeapItem b)
        {
            if (a.fCost != b.fCost)
                return a.fCost < b.fCost;

            if (a.hCost != b.hCost)
                return a.hCost < b.hCost;

            return a.gCost > b.gCost;
        }
    }

    private readonly Dictionary<Vector3Int, int> nodeIndices = new Dictionary<Vector3Int, int>(512);
    private readonly List<NodeRecord> nodes = new List<NodeRecord>(512);
    private readonly MinHeap openHeap = new MinHeap();
    private readonly List<Vector3Int> cellPath = new List<Vector3Int>(96);

    public VoxelPathResult TryFindPath(
        World world,
        Vector3 startWorldPosition,
        Vector3 targetWorldPosition,
        VoxelAStarPathSettings settings,
        List<Vector3> outputPath)
    {
        if (outputPath != null)
            outputPath.Clear();

        if (world == null || outputPath == null)
            return VoxelPathResult.Failed(0);

        settings ??= VoxelAStarPathSettings.Default;

        int maxVisitedNodes = Mathf.Max(32, settings.maxVisitedNodes);
        Vector3Int rawStart = WorldToFeetCell(startWorldPosition);
        Vector3Int rawTarget = WorldToFeetCell(targetWorldPosition);

        int startVerticalProbe = Mathf.Max(3, settings.maxFallBlocks + settings.stepHeightBlocks + 1);
        if (!TryFindNearestWalkable(world, rawStart, rawStart, settings, 1, startVerticalProbe, out Vector3Int start))
            return VoxelPathResult.Failed(0);

        if (!TryFindNearestWalkable(world, rawTarget, start, settings, 2, Mathf.Max(2, settings.verticalSearchRange), out Vector3Int target))
        {
            if (!settings.allowPartialPath)
                return VoxelPathResult.Failed(0);

            target = rawTarget;
        }

        if (start == target)
        {
            outputPath.Add(CellToWorldWaypoint(start, settings));
            return new VoxelPathResult
            {
                status = VoxelPathStatus.Complete,
                visitedNodes = 1,
                pathPoints = outputPath.Count
            };
        }

        ClearSearch();

        int startIndex = AddNode(start, 0, GetHeuristicCost(start, target), -1);
        PushOpen(startIndex);

        int visitedNodes = 0;
        int bestIndex = startIndex;
        int bestHeuristic = nodes[startIndex].hCost;

        while (openHeap.Count > 0 && visitedNodes < maxVisitedNodes)
        {
            HeapItem currentItem = openHeap.Pop();
            NodeRecord current = nodes[currentItem.nodeIndex];

            if (current.closed || currentItem.gCost != current.gCost)
                continue;

            current.closed = true;
            nodes[currentItem.nodeIndex] = current;
            visitedNodes++;

            if (current.hCost < bestHeuristic)
            {
                bestHeuristic = current.hCost;
                bestIndex = currentItem.nodeIndex;
            }

            if (current.position == target)
            {
                BuildOutputPath(currentItem.nodeIndex, settings, outputPath);
                return new VoxelPathResult
                {
                    status = VoxelPathStatus.Complete,
                    visitedNodes = visitedNodes,
                    pathPoints = outputPath.Count
                };
            }

            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                if (!TryGetWalkableNeighbor(
                        world,
                        current.position,
                        HorizontalDirections[i],
                        start,
                        settings,
                        out Vector3Int neighbor,
                        out int stepCost))
                {
                    continue;
                }

                int tentativeGCost = current.gCost + stepCost;
                if (nodeIndices.TryGetValue(neighbor, out int neighborIndex))
                {
                    NodeRecord neighborRecord = nodes[neighborIndex];
                    if (neighborRecord.closed || tentativeGCost >= neighborRecord.gCost)
                        continue;

                    neighborRecord.gCost = tentativeGCost;
                    neighborRecord.parentIndex = currentItem.nodeIndex;
                    nodes[neighborIndex] = neighborRecord;
                    PushOpen(neighborIndex);
                    continue;
                }

                if (nodes.Count >= maxVisitedNodes)
                    continue;

                int hCost = GetHeuristicCost(neighbor, target);
                neighborIndex = AddNode(neighbor, tentativeGCost, hCost, currentItem.nodeIndex);
                PushOpen(neighborIndex);
            }
        }

        if (!settings.allowPartialPath || bestIndex == startIndex)
            return VoxelPathResult.Failed(visitedNodes);

        BuildOutputPath(bestIndex, settings, outputPath);
        return new VoxelPathResult
        {
            status = VoxelPathStatus.Partial,
            visitedNodes = visitedNodes,
            pathPoints = outputPath.Count
        };
    }

    public static Vector3Int WorldToFeetCell(Vector3 worldPosition)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPosition.x),
            Mathf.FloorToInt(worldPosition.y + 0.05f),
            Mathf.FloorToInt(worldPosition.z));
    }

    public VoxelAStarDebugCellState GetDebugCellState(
        World world,
        Vector3Int feet,
        Vector3Int searchOrigin,
        VoxelAStarPathSettings settings)
    {
        if (world == null)
            return VoxelAStarDebugCellState.Unloaded;

        settings ??= VoxelAStarPathSettings.Default;

        if (!IsInsideSearchBounds(feet, searchOrigin, settings))
            return VoxelAStarDebugCellState.OutsideSearchBounds;

        return GetStandableCellState(world, feet, settings);
    }

    public bool IsStandable(World world, Vector3Int feet, VoxelAStarPathSettings settings)
    {
        return GetStandableCellState(world, feet, settings) == VoxelAStarDebugCellState.Walkable;
    }

    public bool IsBodyClear(World world, Vector3Int feet, VoxelAStarPathSettings settings)
    {
        settings ??= VoxelAStarPathSettings.Default;
        return GetBodyClearState(world, feet, settings, feet.y, GetBodyTopY(feet, settings)) == VoxelAStarDebugCellState.Walkable;
    }

    public VoxelAStarDebugCellState GetStandableCellState(World world, Vector3Int feet, VoxelAStarPathSettings settings)
    {
        if (world == null)
            return VoxelAStarDebugCellState.Unloaded;

        settings ??= VoxelAStarPathSettings.Default;
        GetFootprintBounds(feet, settings, out int minX, out int maxX, out int minZ, out int maxZ);

        int supportY = feet.y - 1;
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                if (!TryGetBlock(world, new Vector3Int(x, supportY, z), settings, out BlockType supportBlock))
                    return VoxelAStarDebugCellState.Unloaded;

                if (!world.IsSolidBlock(supportBlock) || world.IsLiquidBlock(supportBlock))
                    return VoxelAStarDebugCellState.Blocked;
            }
        }

        return GetBodyClearState(world, feet, settings, feet.y, GetBodyTopY(feet, settings));
    }

    private VoxelAStarDebugCellState GetBodyClearState(
        World world,
        Vector3Int feet,
        VoxelAStarPathSettings settings,
        int minY,
        int maxY)
    {
        if (world == null)
            return VoxelAStarDebugCellState.Unloaded;

        settings ??= VoxelAStarPathSettings.Default;
        GetFootprintBounds(feet, settings, out int minX, out int maxX, out int minZ, out int maxZ);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (!TryGetBlock(world, new Vector3Int(x, y, z), settings, out BlockType bodyBlock))
                        return VoxelAStarDebugCellState.Unloaded;

                    if (world.IsSolidBlock(bodyBlock))
                        return VoxelAStarDebugCellState.Blocked;

                    if (!settings.allowWater && world.IsLiquidBlock(bodyBlock))
                        return VoxelAStarDebugCellState.Blocked;
                }
            }
        }

        return VoxelAStarDebugCellState.Walkable;
    }

    private void ClearSearch()
    {
        nodeIndices.Clear();
        nodes.Clear();
        openHeap.Clear();
        cellPath.Clear();
    }

    private int AddNode(Vector3Int position, int gCost, int hCost, int parentIndex)
    {
        int index = nodes.Count;
        nodes.Add(new NodeRecord
        {
            position = position,
            gCost = gCost,
            hCost = hCost,
            parentIndex = parentIndex,
            closed = false
        });
        nodeIndices.Add(position, index);
        return index;
    }

    private void PushOpen(int nodeIndex)
    {
        NodeRecord node = nodes[nodeIndex];
        openHeap.Push(new HeapItem
        {
            nodeIndex = nodeIndex,
            fCost = node.gCost + node.hCost,
            hCost = node.hCost,
            gCost = node.gCost
        });
    }

    private bool TryGetWalkableNeighbor(
        World world,
        Vector3Int current,
        Vector3Int direction,
        Vector3Int searchOrigin,
        VoxelAStarPathSettings settings,
        out Vector3Int neighbor,
        out int stepCost)
    {
        if (IsDiagonal(direction))
        {
            if (!settings.allowDiagonalMovement)
            {
                neighbor = default;
                stepCost = 0;
                return false;
            }

            if (!TryResolveWalkableNeighbor(world, current, direction, searchOrigin, settings, out neighbor, out stepCost))
                return false;

            if (!settings.preventDiagonalCornerCutting)
                return true;

            Vector3Int xSide = new Vector3Int(direction.x, 0, 0);
            Vector3Int zSide = new Vector3Int(0, 0, direction.z);

            if (!TryResolveWalkableNeighbor(world, current, xSide, searchOrigin, settings, out _, out _))
                return false;

            if (!TryResolveWalkableNeighbor(world, current, zSide, searchOrigin, settings, out _, out _))
                return false;

            return true;
        }

        return TryResolveWalkableNeighbor(world, current, direction, searchOrigin, settings, out neighbor, out stepCost);
    }

    private bool TryResolveWalkableNeighbor(
        World world,
        Vector3Int current,
        Vector3Int direction,
        Vector3Int searchOrigin,
        VoxelAStarPathSettings settings,
        out Vector3Int neighbor,
        out int stepCost)
    {
        int moveCost = IsDiagonal(direction) ? 14 : 10;
        int stepHeight = Mathf.Max(0, settings.stepHeightBlocks);
        for (int up = 0; up <= stepHeight; up++)
        {
            Vector3Int candidate = new Vector3Int(current.x + direction.x, current.y + up, current.z + direction.z);
            if (!IsWalkable(world, candidate, searchOrigin, settings))
                continue;

            neighbor = candidate;
            stepCost = moveCost + up * 6;
            return true;
        }

        int maxFall = Mathf.Max(0, settings.maxFallBlocks);
        for (int fall = 1; fall <= maxFall; fall++)
        {
            Vector3Int candidate = new Vector3Int(current.x + direction.x, current.y - fall, current.z + direction.z);
            if (!IsWalkable(world, candidate, searchOrigin, settings))
                continue;

            if (!HasPassableColumn(world, candidate, current.y + Mathf.Max(1, settings.mobHeightBlocks) - 1, settings))
                continue;

            neighbor = candidate;
            stepCost = moveCost + fall * 2;
            return true;
        }

        neighbor = default;
        stepCost = 0;
        return false;
    }

    private static bool IsDiagonal(Vector3Int direction)
    {
        return direction.x != 0 && direction.z != 0;
    }

    private bool TryFindNearestWalkable(
        World world,
        Vector3Int center,
        Vector3Int searchOrigin,
        VoxelAStarPathSettings settings,
        int horizontalRadius,
        int verticalRadius,
        out Vector3Int walkable)
    {
        horizontalRadius = Mathf.Max(0, horizontalRadius);
        verticalRadius = Mathf.Max(0, verticalRadius);

        for (int radius = 0; radius <= horizontalRadius; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                        continue;

                    for (int dy = 0; dy <= verticalRadius; dy++)
                    {
                        if (TryCandidate(center.x + dx, center.y + dy, center.z + dz, world, searchOrigin, settings, out walkable))
                            return true;

                        if (dy > 0 && TryCandidate(center.x + dx, center.y - dy, center.z + dz, world, searchOrigin, settings, out walkable))
                            return true;
                    }
                }
            }
        }

        walkable = default;
        return false;
    }

    private bool TryCandidate(
        int x,
        int y,
        int z,
        World world,
        Vector3Int searchOrigin,
        VoxelAStarPathSettings settings,
        out Vector3Int walkable)
    {
        Vector3Int candidate = new Vector3Int(x, y, z);
        if (IsWalkable(world, candidate, searchOrigin, settings))
        {
            walkable = candidate;
            return true;
        }

        walkable = default;
        return false;
    }

    private bool IsWalkable(World world, Vector3Int feet, Vector3Int searchOrigin, VoxelAStarPathSettings settings)
    {
        return GetDebugCellState(world, feet, searchOrigin, settings) == VoxelAStarDebugCellState.Walkable;
    }

    private bool HasPassableColumn(World world, Vector3Int feet, int topYInclusive, VoxelAStarPathSettings settings)
    {
        return GetBodyClearState(world, feet, settings, feet.y, topYInclusive) == VoxelAStarDebugCellState.Walkable;
    }

    private static int GetBodyTopY(Vector3Int feet, VoxelAStarPathSettings settings)
    {
        return feet.y + Mathf.Max(1, settings.mobHeightBlocks) - 1;
    }

    private static void GetFootprintBounds(
        Vector3Int feet,
        VoxelAStarPathSettings settings,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        int width = Mathf.Max(1, settings.bodyWidthBlocks);
        int depth = Mathf.Max(1, settings.bodyDepthBlocks);

        minX = feet.x - (width - 1) / 2;
        maxX = feet.x + width / 2;
        minZ = feet.z - (depth - 1) / 2;
        maxZ = feet.z + depth / 2;
    }

    private bool TryGetBlock(World world, Vector3Int position, VoxelAStarPathSettings settings, out BlockType blockType)
    {
        if (settings.loadedChunksOnly)
            return world.TryGetLoadedBlockAt(position, out blockType);

        blockType = world.GetBlockAt(position);
        return true;
    }

    private bool IsInsideSearchBounds(Vector3Int position, Vector3Int origin, VoxelAStarPathSettings settings)
    {
        int radius = Mathf.Max(1, settings.searchRadius);
        int verticalRange = Mathf.Max(1, settings.verticalSearchRange);

        return Mathf.Abs(position.x - origin.x) <= radius &&
               Mathf.Abs(position.z - origin.z) <= radius &&
               Mathf.Abs(position.y - origin.y) <= verticalRange;
    }

    private int GetHeuristicCost(Vector3Int from, Vector3Int to)
    {
        int dx = Mathf.Abs(from.x - to.x);
        int dz = Mathf.Abs(from.z - to.z);
        int diagonal = Mathf.Min(dx, dz);
        int straight = Mathf.Max(dx, dz) - diagonal;
        int vertical = Mathf.Abs(from.y - to.y);
        return diagonal * 14 + straight * 10 + vertical * 6;
    }

    private void BuildOutputPath(int endIndex, VoxelAStarPathSettings settings, List<Vector3> outputPath)
    {
        cellPath.Clear();

        int currentIndex = endIndex;
        int guard = 0;
        int guardLimit = nodes.Count + 1;
        while (currentIndex >= 0 && guard++ < guardLimit)
        {
            NodeRecord node = nodes[currentIndex];
            cellPath.Add(node.position);
            currentIndex = node.parentIndex;
        }

        if (cellPath.Count == 0)
            return;

        if (!settings.compressStraightSegments || cellPath.Count <= 2)
        {
            for (int i = cellPath.Count - 1; i >= 0; i--)
                outputPath.Add(CellToWorldWaypoint(cellPath[i], settings));

            return;
        }

        Vector3Int previousDirection = Vector3Int.zero;
        for (int i = cellPath.Count - 1; i >= 0; i--)
        {
            Vector3Int current = cellPath[i];
            bool shouldKeep = i == cellPath.Count - 1 || i == 0;

            if (!shouldKeep)
            {
                Vector3Int next = cellPath[i - 1];
                Vector3Int direction = NormalizeStep(next - current);
                shouldKeep = direction != previousDirection;
                previousDirection = direction;
            }
            else if (i > 0)
            {
                previousDirection = NormalizeStep(cellPath[i - 1] - current);
            }

            if (shouldKeep)
                outputPath.Add(CellToWorldWaypoint(current, settings));
        }
    }

    private static Vector3Int NormalizeStep(Vector3Int value)
    {
        return new Vector3Int(
            value.x == 0 ? 0 : (value.x > 0 ? 1 : -1),
            value.y == 0 ? 0 : (value.y > 0 ? 1 : -1),
            value.z == 0 ? 0 : (value.z > 0 ? 1 : -1));
    }

    private static Vector3 CellToWorldWaypoint(Vector3Int cell, VoxelAStarPathSettings settings)
    {
        return new Vector3(cell.x + 0.5f, cell.y + settings.waypointYOffset, cell.z + 0.5f);
    }
}
