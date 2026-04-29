using System;
using UnityEngine;

[Serializable]
public sealed class VoxelAStarPathSettings
{
    [Header("Search")]
    [Min(2)] public int searchRadius = 16;
    [Min(32)] public int maxVisitedNodes = 512;
    [Min(1)] public int verticalSearchRange = 8;
    [Min(4)] public int maxPathPoints = 96;
    public bool loadedChunksOnly = true;
    public bool allowPartialPath = true;
    public bool compressStraightSegments = true;
    public bool allowDiagonalMovement = true;
    public bool preventDiagonalCornerCutting = true;

    [Header("Mob Shape")]
    [Min(1)] public int bodyWidthBlocks = 1;
    [Min(1)] public int bodyDepthBlocks = 1;
    [Min(1)] public int mobHeightBlocks = 2;
    [Min(0)] public int stepHeightBlocks = 1;
    [Min(0)] public int maxFallBlocks = 3;
    public bool allowWater = false;
    public float waypointYOffset = 0.05f;

    public static VoxelAStarPathSettings Default => new VoxelAStarPathSettings();
}

public enum VoxelPathStatus
{
    Failed = 0,
    Partial = 1,
    Complete = 2
}

public enum VoxelAStarDebugCellState
{
    OutsideSearchBounds = 0,
    Unloaded = 1,
    Blocked = 2,
    Walkable = 3
}

public struct VoxelPathResult
{
    public VoxelPathStatus status;
    public int visitedNodes;
    public int pathPoints;

    public bool HasPath => status != VoxelPathStatus.Failed && pathPoints > 0;

    public static VoxelPathResult Failed(int visitedNodes)
    {
        return new VoxelPathResult
        {
            status = VoxelPathStatus.Failed,
            visitedNodes = visitedNodes,
            pathPoints = 0
        };
    }
}
