using Unity.Burst;
using Unity.Mathematics;

public static class FlatWorldUtility
{
    public const int SurfaceLayerDepth = 1;

    [BurstCompile]
    public static TerrainColumnContext CreateColumnContext(
        int worldX,
        int worldZ,
        int surfaceHeight,
        int worldHeight)
    {
        surfaceHeight = math.clamp(surfaceHeight, 3, worldHeight - 1);
        TerrainSurfaceData surfaceData = new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceLayerDepth = SurfaceLayerDepth,
            waterDepth = 0,
            biome = BiomeType.Meadow,
            surfaceBlock = BlockType.Grass,
            subsurfaceBlock = BlockType.Stone,
            isBeach = false,
            isUnderwater = false,
            isCliff = false,
            isHighMountain = false,
            slope = 0f,
            slope01 = 0f
        };

        return new TerrainColumnContext
        {
            worldX = worldX,
            worldZ = worldZ,
            surfaceHeight = surfaceHeight,
            northHeight = surfaceHeight,
            southHeight = surfaceHeight,
            eastHeight = surfaceHeight,
            westHeight = surfaceHeight,
            northEastHeight = surfaceHeight,
            northWestHeight = surfaceHeight,
            southEastHeight = surfaceHeight,
            southWestHeight = surfaceHeight,
            slope = 0f,
            slope01 = 0f,
            surface = surfaceData
        };
    }

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, int surfaceHeight)
    {
        if (y <= 2)
            return BlockType.Bedrock;

        if (y == surfaceHeight)
            return BlockType.Grass;

        if (y > surfaceHeight - SurfaceLayerDepth)
            return BlockType.Stone;

        return BlockType.Stone;
    }
}
