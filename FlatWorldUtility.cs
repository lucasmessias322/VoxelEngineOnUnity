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
        BiomeNoiseSettings defaultBiomeSettings = default;
        return CreateColumnContext(
            worldX,
            worldZ,
            surfaceHeight,
            worldHeight,
            BiomeType.Meadow,
            in defaultBiomeSettings);
    }

    [BurstCompile]
    public static TerrainColumnContext CreateColumnContext(
        int worldX,
        int worldZ,
        int surfaceHeight,
        int worldHeight,
        BiomeType biome,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        surfaceHeight = math.clamp(surfaceHeight, 3, worldHeight - 1);
        biome = SanitizeBiome(biome);
        TerrainSurfaceData surfaceData = new TerrainSurfaceData
        {
            surfaceHeight = surfaceHeight,
            surfaceLayerDepth = SurfaceLayerDepth,
            waterDepth = 0,
            biome = biome,
            surfaceBlock = BiomeUtility.GetSurfaceBlock(biome, biomeNoiseSettings),
            subsurfaceBlock = BiomeUtility.GetSubsurfaceBlock(biome, biomeNoiseSettings),
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

    [BurstCompile]
    public static BlockType GetBlockTypeAtHeight(int y, in TerrainSurfaceData surface)
    {
        if (y <= 2)
            return BlockType.Bedrock;

        if (y > surface.surfaceHeight)
            return BlockType.Air;

        if (y == surface.surfaceHeight)
            return surface.surfaceBlock;

        int surfaceLayerDepth = math.max(1, surface.surfaceLayerDepth);
        if (y > surface.surfaceHeight - surfaceLayerDepth)
            return surface.subsurfaceBlock;

        return BlockType.Stone;
    }

    [BurstCompile]
    private static BiomeType SanitizeBiome(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
            case BiomeType.Savanna:
            case BiomeType.Meadow:
            case BiomeType.Taiga:
                return biome;
            default:
                return BiomeType.Meadow;
        }
    }
}
