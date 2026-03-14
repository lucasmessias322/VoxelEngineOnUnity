using Unity.Burst;
using Unity.Mathematics;

public struct TerrainSurfaceData
{
    public int surfaceHeight;
    public BlockType surfaceBlock;
    public BlockType subsurfaceBlock;
    public bool isBeach;
    public bool isCliff;
    public bool isHighMountain;
}

public static class TerrainSurfaceRules
{
    public const int BeachHeightMargin = 2;
    public const int SurfaceLayerDepth = 4;
    public const int StoneTransitionDepth = 50;
    public const int HighMountainHeightOffset = 70;

    [BurstCompile]
    public static bool IsCliffFromNeighborHeights(
        int centerHeight,
        int northHeight,
        int southHeight,
        int eastHeight,
        int westHeight,
        int threshold)
    {
        int maxDiff = math.max(math.abs(centerHeight - northHeight), math.abs(centerHeight - southHeight));
        maxDiff = math.max(maxDiff, math.abs(centerHeight - eastHeight));
        maxDiff = math.max(maxDiff, math.abs(centerHeight - westHeight));
        return maxDiff >= threshold;
    }

    [BurstCompile]
    public static TerrainSurfaceData EvaluateColumnSurface(
        int worldX,
        int worldZ,
        int surfaceHeight,
        bool isCliff,
        int baseHeight,
        float seaLevel,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        bool isBeach = surfaceHeight <= seaLevel + BeachHeightMargin;
        bool isHighMountain = surfaceHeight >= baseHeight + HighMountainHeightOffset;

        BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, biomeNoiseSettings);
        BlockType biomeSurfaceBlock = BiomeUtility.GetSurfaceBlock(biome, biomeNoiseSettings);
        BlockType biomeSubsurfaceBlock = BiomeUtility.GetSubsurfaceBlock(biome, biomeNoiseSettings);

        BlockType surfaceBlock = (isHighMountain || isCliff)
            ? BlockType.Stone
            : (isBeach ? BlockType.Sand : biomeSurfaceBlock);

        BlockType subsurfaceBlock = (isHighMountain || isCliff)
            ? BlockType.Stone
            : (isBeach ? BlockType.Sand : biomeSubsurfaceBlock);

        return new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceBlock = surfaceBlock,
            subsurfaceBlock = subsurfaceBlock,
            isBeach = isBeach,
            isCliff = isCliff,
            isHighMountain = isHighMountain
        };
    }

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, in TerrainSurfaceData surface)
    {
        if (y == surface.surfaceHeight)
            return surface.surfaceBlock;

        if (y > surface.surfaceHeight - SurfaceLayerDepth)
            return surface.subsurfaceBlock;

        if (y <= 2)
            return BlockType.Bedrock;

        return y > surface.surfaceHeight - StoneTransitionDepth
            ? BlockType.Stone
            : BlockType.Deepslate;
    }
}
