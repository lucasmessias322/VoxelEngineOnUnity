using UnityEngine;

public static class VoxelAtlasCompatibility
{
    private static readonly BlockFace[] SupportedFaces =
    {
        BlockFace.Top,
        BlockFace.Bottom,
        BlockFace.Right,
        BlockFace.Left,
        BlockFace.Front,
        BlockFace.Back
    };

    public static bool Apply(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft)
    {
        if (generator == null || blockData == null)
            return false;

        blockData.InitializeDictionary();

        if ((generator.GeneratedAtlas == null || generator.UvMap.Count == 0))
            generator.GenerateAtlas();

        if (generator.GeneratedAtlas == null || generator.UvMap.Count == 0)
            return false;

        bool updatedAnyMapping = ApplyBlockMappings(generator, blockData, legacyAtlasTiles, atlasOriginTopLeft);
        bool updatedAnyCuboid = ApplyMultiCuboidMappings(generator, blockData, legacyAtlasTiles, atlasOriginTopLeft);

        if (updatedAnyMapping || updatedAnyCuboid || blockData.mappings == null)
            blockData.InitializeDictionary();

        return true;
    }

    private static bool ApplyBlockMappings(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft)
    {
        if (blockData.blockTextures == null || blockData.blockTextures.Count == 0)
            return false;

        bool updated = false;
        for (int i = 0; i < blockData.blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockData.blockTextures[i];
            if (ApplyMapping(generator, legacyAtlasTiles, atlasOriginTopLeft, ref mapping))
                updated = true;

            blockData.blockTextures[i] = mapping;
        }

        return updated;
    }

    private static bool ApplyMultiCuboidMappings(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft)
    {
        if (blockData.multiCuboidShapes == null || blockData.multiCuboidShapes.Count == 0)
            return false;

        bool updated = false;
        for (int i = 0; i < blockData.multiCuboidShapes.Count; i++)
        {
            BlockMultiCuboidDefinition definition = blockData.multiCuboidShapes[i];
            if (definition == null || definition.cuboids == null || definition.cuboids.Count == 0)
                continue;

            BlockTextureMapping? mappingResult = blockData.GetMapping(definition.blockType);
            if (!mappingResult.HasValue)
                continue;

            BlockTextureMapping fallbackMapping = mappingResult.Value;
            for (int c = 0; c < definition.cuboids.Count; c++)
            {
                BlockModelCuboid cuboid = definition.cuboids[c];
                if (ApplyCuboid(generator, legacyAtlasTiles, atlasOriginTopLeft, fallbackMapping, ref cuboid))
                    updated = true;

                definition.cuboids[c] = cuboid;
            }
        }

        return updated;
    }

    private static bool ApplyMapping(
        TextureAtlasGenerator generator,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        ref BlockTextureMapping mapping)
    {
        bool updated = false;
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Top), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Top);
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Bottom), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Bottom);
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Right), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Right);
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Left), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Left);
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Front), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Front);
        updated |= TryAssignRect(generator, mapping.GetTileCoord(BlockFace.Back), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Back);
        return updated;
    }

    private static bool ApplyCuboid(
        TextureAtlasGenerator generator,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        BlockTextureMapping fallbackMapping,
        ref BlockModelCuboid cuboid)
    {
        bool updated = false;

        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            if (!cuboid.HasTextureOverride(face))
                continue;

            if (!generator.TryGetLegacyTileUv(
                    cuboid.GetTileCoord(face, fallbackMapping),
                    legacyAtlasTiles,
                    atlasOriginTopLeft,
                    out Rect uvRect))
            {
                continue;
            }

            cuboid.SetOverrideUvRectData(face, BlockAtlasUvUtility.RectToUvRectData(uvRect));
            updated = true;
        }

        return updated;
    }

    private static bool TryAssignRect(
        TextureAtlasGenerator generator,
        Vector2Int tileCoord,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        ref BlockTextureMapping mapping,
        BlockFace face)
    {
        if (!generator.TryGetLegacyTileUv(tileCoord, legacyAtlasTiles, atlasOriginTopLeft, out Rect uvRect))
            return false;

        mapping.SetUvRectData(face, BlockAtlasUvUtility.RectToUvRectData(uvRect));
        return true;
    }
}
