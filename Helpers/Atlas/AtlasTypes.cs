using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class AtlasTextureEntry
{
    public string id = "block/unknown";
    public Texture2D texture;
}

[Serializable]
public struct AtlasBuildSettings
{
    [Min(1)] public int initialSize;
    [Min(1)] public int maxSize;
    [Min(0)] public int paddingPixels;
    public bool generateMipmaps;
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
