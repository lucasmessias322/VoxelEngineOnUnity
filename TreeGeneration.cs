using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public enum TreeStyle : byte
{
    OakBroadleaf = 0,
    TaigaSpruce = 1,
    Cactus = 2,
    BirchBroadleaf = 3,
    SavannaAcacia = 4,
    FancyOak = 5
}

public struct TreeInstance
{
    public int worldX;
    public int worldZ;
    public int surfaceY;
    public int trunkHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int trunkClearance;
    public int spacingRadius;
    public TreeStyle treeStyle;
}

public struct TreeSpawnRuleData
{
    public BiomeType biome;
    public TreeStyle treeStyle;
    public TreeSettings settings;
}

public static class TreeGenerationMetrics
{
    public static int GetHorizontalReach(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetHorizontalReach(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetPlacementSpacingRadius(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight, settings.minSpacing);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight, int minSpacing)
    {
        int spacingFromConfig = math.max(1, (math.max(1, minSpacing) + 1) / 2);
        int horizontalReach = GetHorizontalReach(treeStyle, heightValue, canopyRadius, canopyHeight);
        return math.max(spacingFromConfig, horizontalReach);
    }

    public static int GetHorizontalReach(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight)
    {
        int safeHeight = math.max(1, heightValue);
        int safeRadius = math.max(0, canopyRadius);
        int safeCanopyHeight = math.max(1, canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return math.max(safeRadius + 2, 5);

            case TreeStyle.FancyOak:
                return GetFancyOakHorizontalReach(safeHeight, safeRadius, safeCanopyHeight);

            default:
                return safeRadius;
        }
    }

    public static int GetVerticalMargin(TreeStyle treeStyle, TreeSettings settings)
    {
        int safeMaxHeight = math.max(1, settings.maxHeight);
        int safeCanopyHeight = math.max(1, settings.canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return safeMaxHeight + math.max(4, safeCanopyHeight + 1);

            case TreeStyle.TaigaSpruce:
                return safeMaxHeight + math.max(3, safeCanopyHeight + 1);

            case TreeStyle.FancyOak:
                return safeMaxHeight + math.clamp(math.max(4, safeCanopyHeight), 4, 5) + 2;

            default:
                return safeMaxHeight + safeCanopyHeight + 2;
        }
    }

    private static int GetFancyOakHorizontalReach(int heightLimit, int canopyRadius, int canopyHeight)
    {
        int leafDistanceLimit = math.clamp(math.max(4, canopyHeight), 4, 5);
        float scaleWidth = math.max(1f, canopyRadius / 4f);
        float maxBranchReach = heightLimit * 0.25f * 1.328f * scaleWidth;
        return math.max(canopyRadius + 1, (int)math.ceil(maxBranchReach) + leafDistanceLimit);
    }
}
