using System;
using UnityEngine;

[Serializable]
public struct TreeSettings
{
    public int minHeight;
    public int maxHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int trunkClearance;
    public int minSpacing;
    public float density;
    public float noiseScale;
    public int seed;
}

[Serializable]
public struct OreSpawnSettings
{
    public bool enabled;
    public BlockType blockType;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int veinsPerChunk;
    [Min(1)] public int minVeinSize;
    [Min(1)] public int maxVeinSize;
    [Min(0)] public int minSurfaceDepth;
    public bool replaceStone;
    public bool replaceDeepslate;
}

[Serializable]
public struct SpaghettiCaveSettings
{
    public bool enabled;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int minSurfaceDepth;
    [Min(0)] public int entranceSurfaceDepth;
    [Range(-0.35f, 0.35f)] public float densityBias;
    public int seedOffset;

    public static SpaghettiCaveSettings Default => new SpaghettiCaveSettings
    {
        enabled = true,
        minY = 4,
        maxY = 320,
        minSurfaceDepth = 6,
        entranceSurfaceDepth = 0,
        densityBias = 0f,
        seedOffset = 48271
    };

    public bool LooksUninitialized =>
        !enabled &&
        minY == 0 &&
        maxY == 0 &&
        minSurfaceDepth == 0 &&
        entranceSurfaceDepth == 0 &&
        densityBias == 0f &&
        seedOffset == 0;

    public bool LooksLikeInitialSurfaceClosedDefault =>
        enabled &&
        minY == 4 &&
        maxY == 320 &&
        minSurfaceDepth == 6 &&
        entranceSurfaceDepth == 1 &&
        densityBias == 0f &&
        seedOffset == 48271;
}

public enum TreeLeafQualityMode : byte
{
    Medium = 0,
    High = 1,
    Ultra = 2
}

public enum WorldMaterialProfile : byte
{
    PcLit = 0,
    MobileUnlit = 1
}

public enum WorldTerrainMode : byte
{
    Normal = 0,
    Flat = 1
}
