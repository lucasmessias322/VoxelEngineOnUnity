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

    public static bool CaptureStableEntryIds(
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

        bool updatedAnyMapping = CaptureBlockMappingEntryIds(generator, blockData, legacyAtlasTiles, atlasOriginTopLeft);
        bool updatedAnyCuboid = CaptureMultiCuboidEntryIds(generator, blockData, legacyAtlasTiles, atlasOriginTopLeft);
        return updatedAnyMapping || updatedAnyCuboid;
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
            if (ApplyMapping(generator, blockData, legacyAtlasTiles, atlasOriginTopLeft, ref mapping))
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
                if (ApplyCuboid(generator, blockData, definition.blockType, c, legacyAtlasTiles, atlasOriginTopLeft, fallbackMapping, ref cuboid))
                    updated = true;

                definition.cuboids[c] = cuboid;
            }
        }

        return updated;
    }

    private static bool ApplyMapping(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        ref BlockTextureMapping mapping)
    {
        bool updated = false;
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Top), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Top);
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Bottom), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Bottom);
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Right), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Right);
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Left), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Left);
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Front), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Front);
        updated |= TryAssignMappingRect(generator, blockData, mapping.blockType, mapping.GetTileCoord(BlockFace.Back), legacyAtlasTiles, atlasOriginTopLeft, ref mapping, BlockFace.Back);
        return updated;
    }

    private static bool ApplyCuboid(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        BlockType blockType,
        int cuboidIndex,
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

            if (!TryResolveCuboidUvRect(
                    generator,
                    blockData,
                    blockType,
                    cuboidIndex,
                    face,
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

    private static bool CaptureBlockMappingEntryIds(
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
            for (int f = 0; f < SupportedFaces.Length; f++)
            {
                BlockFace face = SupportedFaces[f];
                if (blockData.TryGetTextureEntryId(mapping.blockType, face, out _))
                    continue;

                if (!generator.TryGetLegacyTileId(mapping.GetTileCoord(face), legacyAtlasTiles, atlasOriginTopLeft, out string entryId))
                    continue;

                if (blockData.SetTextureEntryId(mapping.blockType, face, entryId))
                    updated = true;
            }
        }

        return updated;
    }

    private static bool CaptureMultiCuboidEntryIds(
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
                for (int f = 0; f < SupportedFaces.Length; f++)
                {
                    BlockFace face = SupportedFaces[f];
                    if (!cuboid.HasTextureOverride(face) ||
                        blockData.TryGetCuboidTextureEntryId(definition.blockType, c, face, out _))
                    {
                        continue;
                    }

                    if (!generator.TryGetLegacyTileId(
                            cuboid.GetTileCoord(face, fallbackMapping),
                            legacyAtlasTiles,
                            atlasOriginTopLeft,
                            out string entryId))
                    {
                        continue;
                    }

                    if (blockData.SetCuboidTextureEntryId(definition.blockType, c, face, entryId))
                        updated = true;
                }
            }
        }

        return updated;
    }

    private static bool TryAssignMappingRect(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        BlockType blockType,
        Vector2Int tileCoord,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        ref BlockTextureMapping mapping,
        BlockFace face)
    {
        if (!TryResolveMappingUvRect(generator, blockData, blockType, face, tileCoord, legacyAtlasTiles, atlasOriginTopLeft, out Rect uvRect))
            return false;

        mapping.SetUvRectData(face, BlockAtlasUvUtility.RectToUvRectData(uvRect));
        return true;
    }

    private static bool TryResolveMappingUvRect(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        BlockType blockType,
        BlockFace face,
        Vector2Int tileCoord,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        out Rect uvRect)
    {
        uvRect = default;

        if (blockData != null &&
            blockData.TryGetTextureEntryId(blockType, face, out string entryId) &&
            generator.TryGetUv(entryId, out uvRect))
        {
            return true;
        }

        return generator.TryGetLegacyTileUv(tileCoord, legacyAtlasTiles, atlasOriginTopLeft, out uvRect);
    }

    private static bool TryResolveCuboidUvRect(
        TextureAtlasGenerator generator,
        BlockDataSO blockData,
        BlockType blockType,
        int cuboidIndex,
        BlockFace face,
        Vector2Int tileCoord,
        Vector2Int legacyAtlasTiles,
        bool atlasOriginTopLeft,
        out Rect uvRect)
    {
        uvRect = default;

        if (blockData != null &&
            blockData.TryGetCuboidTextureEntryId(blockType, cuboidIndex, face, out string entryId) &&
            generator.TryGetUv(entryId, out uvRect))
        {
            return true;
        }

        return generator.TryGetLegacyTileUv(tileCoord, legacyAtlasTiles, atlasOriginTopLeft, out uvRect);
    }
}
