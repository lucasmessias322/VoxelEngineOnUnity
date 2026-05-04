using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class AtlasTextureEntry
{
    public string id = "block/unknown";
    public Texture2D texture;
    [Tooltip("Opcional. Preto = sem brilho; RGB define cor/intensidade emissiva no mesmo recorte da textura principal.")]
    public Texture2D emissionTexture;
    public bool useSourceRect;
    public RectInt sourceRect;
    public bool useUvSampling;
    public Vector4 sampledUvRect;
}

[Serializable]
public struct AtlasBuildSettings
{
    [Min(1)] public int initialSize;
    [Min(1)] public int maxSize;
    [Min(0)] public int paddingPixels;
    public bool generateMipmaps;
    public bool generateMipmapsPerTile;
    public TextureFormat format;
    public FilterMode filterMode;
    public TextureWrapMode wrapMode;

    public static AtlasBuildSettings Default
    {
        get
        {
            AtlasBuildSettings settings = new AtlasBuildSettings
            {
                initialSize = 512,
                maxSize = 4096,
                paddingPixels = 2,
                generateMipmaps = true,
                generateMipmapsPerTile = false,
                format = TextureFormat.RGBA32,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            return settings;
        }
    }

    public void Sanitize()
    {
        initialSize = Mathf.Max(1, initialSize);
        maxSize = Mathf.Max(initialSize, maxSize);
        paddingPixels = Mathf.Max(0, paddingPixels);
    }
}

public sealed class AtlasBuildResult
{
    public Texture2D atlasTexture;
    public int atlasWidth;
    public int atlasHeight;
    public Dictionary<string, Rect> uvMap = new Dictionary<string, Rect>(StringComparer.Ordinal);
    public Dictionary<string, RectInt> pixelRectMap = new Dictionary<string, RectInt>(StringComparer.Ordinal);
    public Dictionary<string, RectInt> paddedPixelRectMap = new Dictionary<string, RectInt>(StringComparer.Ordinal);

    public bool TryGetUv(string textureId, out Rect uvRect)
    {
        uvRect = default;
        return !string.IsNullOrEmpty(textureId) && uvMap.TryGetValue(textureId, out uvRect);
    }

    public bool TryGetUv(BlockType blockType, out Rect uvRect)
    {
        if (TryGetUv(blockType.ToString(), out uvRect))
            return true;

        return TryGetUv(AtlasKeyUtility.BuildMinecraftBlockKey(blockType), out uvRect);
    }
}

public static class AtlasKeyUtility
{
    public const string DefaultNamespace = "minecraft";
    public const string DefaultBlockPathPrefix = "block/";

    public static string BuildMinecraftBlockKey(
        BlockType blockType,
        string namespaceId = DefaultNamespace,
        string blockPathPrefix = DefaultBlockPathPrefix)
    {
        string safeNamespace = SanitizeNamespace(namespaceId);
        string safePrefix = SanitizePathPrefix(blockPathPrefix);
        string normalizedBlockName = NormalizePathSegment(blockType.ToString());
        return $"{safeNamespace}:{safePrefix}{normalizedBlockName}";
    }

    public static string SanitizeNamespace(string namespaceId)
    {
        string safe = string.IsNullOrWhiteSpace(namespaceId)
            ? DefaultNamespace
            : namespaceId.Trim().ToLowerInvariant();
        return safe.Replace(' ', '_');
    }

    public static string SanitizePathPrefix(string pathPrefix)
    {
        string safe = string.IsNullOrWhiteSpace(pathPrefix)
            ? DefaultBlockPathPrefix
            : pathPrefix.Trim().ToLowerInvariant().Replace('\\', '/');
        if (!safe.EndsWith("/"))
            safe += "/";
        return safe;
    }

    public static string NormalizePathSegment(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();
        return normalized.Replace(' ', '_');
    }
}

public static class BlockTextureEntryIdResolver
{
    public static bool TryGetCanonicalEntryId(BlockType blockType, BlockFace face, out string entryId)
    {
        entryId = string.Empty;

        switch (blockType)
        {
            case BlockType.Grass:
                entryId = face switch
                {
                    BlockFace.Top => "block/grass_block_top",
                    BlockFace.Bottom => "block/dirt",
                    _ => "block/grass_block_side"
                };
                return true;

            case BlockType.Dirt:
                entryId = "block/dirt";
                return true;

            case BlockType.Stone:
                entryId = "block/stone";
                return true;

            case BlockType.Sand:
                entryId = "block/sand";
                return true;

            case BlockType.Water:
            case BlockType.WaterFlow1:
            case BlockType.WaterFlow2:
            case BlockType.WaterFlow3:
            case BlockType.WaterFlow4:
            case BlockType.WaterFlow5:
            case BlockType.WaterFlow6:
            case BlockType.WaterFlow7:
            case BlockType.WaterFall0:
            case BlockType.WaterFall1:
            case BlockType.WaterFall2:
            case BlockType.WaterFall3:
            case BlockType.WaterFall4:
            case BlockType.WaterFall5:
            case BlockType.WaterFall6:
            case BlockType.WaterFall7:
                entryId = "block/images";
                return true;

            case BlockType.Bedrock:
                entryId = "block/bedrock_1";
                return true;

            case BlockType.Leaves:
                entryId = "block/oak_leaves";
                return true;

            case BlockType.Log:
                entryId = IsTopOrBottom(face) ? "block/oak_log_top" : "block/oak_log";
                return true;

            case BlockType.Deepslate:
                entryId = "block/cobbled_deepslate";
                return true;

            case BlockType.Snow:
                entryId = IsTopOrBottom(face) ? "block/snow" : "block/grass_block_snow";
                return true;

            case BlockType.glass:
                entryId = "block/glass_1";
                return true;

            case BlockType.glowstone:
                entryId = "block/glowstone";
                return true;

            case BlockType.batteryBlock:
                entryId = ResolveBatteryEntryId(face);
                return !string.IsNullOrEmpty(entryId);

            case BlockType.oak_planks:
            case BlockType.Oak_Leader:
            case BlockType.Oak_Ramp:
            case BlockType.Oak_VerticalRamp:
                entryId = "block/oak_planks";
                return true;

            case BlockType.short_grass4:
                entryId = "block/short_grass4";
                return true;

            case BlockType.birch_log:
                entryId = IsTopOrBottom(face) ? "block/birch_log_top" : "block/birch_log";
                return true;

            case BlockType.CoalOre:
                entryId = "block/coal_ore";
                return true;

            case BlockType.IronOre:
                entryId = "block/iron_ore";
                return true;

            case BlockType.GoldOre:
                entryId = "block/gold_ore";
                return true;

            case BlockType.DiamondOre:
                entryId = "block/deepslate_diamond_ore";
                return true;

            case BlockType.EmeraldOre:
                entryId = "block/deepslate_emerald_ore";
                return true;

            case BlockType.Cactus:
                entryId = face switch
                {
                    BlockFace.Top => "block/cactus_top",
                    BlockFace.Bottom => "block/cactus_bottom",
                    _ => "block/cactus_side"
                };
                return true;

            case BlockType.acacia_log:
                entryId = IsTopOrBottom(face) ? "block/acacia_log_top" : "block/acacia_log";
                return true;

            case BlockType.StoneFurnance:
                entryId = face switch
                {
                    BlockFace.Top => "block/furnace_top",
                    BlockFace.Bottom => "block/furnace_top",
                    BlockFace.Front => "block/furnace_front",
                    _ => "block/furnace_side"
                };
                return true;

            case BlockType.dead_bush:
                entryId = "block/dead_bush";
                return true;

            case BlockType.Copper_ore:
                entryId = "block/copper_ore";
                return true;

            default:
                return false;
        }
    }

    private static bool IsTopOrBottom(BlockFace face)
    {
        return face == BlockFace.Top || face == BlockFace.Bottom;
    }

    private static string ResolveBatteryEntryId(BlockFace face)
    {
        if (IsTopOrBottom(face))
            return "block/batterytop";

        return "block/batteryside";
    }
}
