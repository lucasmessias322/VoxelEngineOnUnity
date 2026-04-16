using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ItemAtlasDataSO", menuName = "ScriptableObjects/ItemAtlasDataSO", order = 2)]
public class ItemAtlasDataSO : ScriptableObject
{
    private const int MinimumRuntimePaddingPixels = 2;

    [Header("Atlas")]
    public Vector2Int atlasSize = new Vector2Int(4, 4);
    public Texture2D atlasTexture;
    public Material atlasMaterial;
    [Tooltip("Database opcional para resolver entryIds como item/stick e reconstruir o atlas em runtime.")]
    public TextureAtlasDatabaseSO textureDatabase;
    [Min(0)] public int paddingPixels = 0;
    [Min(0f)] public float fallbackUvInset = 0.001f;

    [Header("Mappings")]
    public List<ItemAtlasMapping> itemMappings = new List<ItemAtlasMapping>();

    [System.NonSerialized] private Dictionary<Item, ItemAtlasMapping> lookup;
    [System.NonSerialized] private AtlasBuildResult runtimeAtlasResult;
    [System.NonSerialized] private bool runtimeAtlasBuildAttempted;
    [System.NonSerialized] private Texture2D appliedRuntimeAtlasTexture;

    public void InitializeLookup()
    {
        lookup = new Dictionary<Item, ItemAtlasMapping>();

        if (itemMappings == null)
            return;

        for (int i = 0; i < itemMappings.Count; i++)
        {
            ItemAtlasMapping mapping = itemMappings[i];
            if (mapping.item == null)
                continue;

            lookup[mapping.item] = mapping;
        }
    }

    public bool TryGetMapping(Item item, out ItemAtlasMapping mapping)
    {
        mapping = default;
        if (item == null)
            return false;

        if (lookup == null)
            InitializeLookup();

        return lookup != null && lookup.TryGetValue(item, out mapping);
    }

    public bool HasMapping(Item item)
    {
        return TryGetMapping(item, out _);
    }

    public bool TryGetTextureEntryId(Item item, out string entryId)
    {
        entryId = string.Empty;
        return TryGetMapping(item, out ItemAtlasMapping mapping) &&
               TryResolveEntryId(mapping, out entryId);
    }

    public bool TryGetMaterial(out Material material)
    {
        material = atlasMaterial;
        if (material == null)
            return false;

        if (TryGetResolvedAtlasTexture(out Texture2D resolvedTexture) && resolvedTexture != null)
            ApplyResolvedAtlasTexture(material, resolvedTexture);

        return material != null;
    }

    public bool TryGetTexture(out Texture2D texture)
    {
        if (TryGetResolvedAtlasTexture(out texture) && texture != null)
            return true;

        if (atlasMaterial == null)
            return false;

        Texture candidate = null;
        if (atlasMaterial.HasProperty("_BaseMap"))
            candidate = atlasMaterial.GetTexture("_BaseMap");
        if (candidate == null && atlasMaterial.HasProperty("_MainTex"))
            candidate = atlasMaterial.GetTexture("_MainTex");
        if (candidate == null)
            candidate = atlasMaterial.mainTexture;

        texture = candidate as Texture2D;
        return texture != null;
    }

    public bool TryGetUvRect(Item item, out Rect uvRect)
    {
        return TryGetUvRect(item, out uvRect, applyInset: true);
    }

    public bool TryGetUvRect(Item item, out Rect uvRect, bool applyInset)
    {
        uvRect = default;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        return TryGetUvRect(mapping, out uvRect, applyInset);
    }

    public bool TryGetAspect(Item item, out float aspect)
    {
        aspect = 1f;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        if (TryGetResolvedPixelRect(mapping, out Rect resolvedPixelRect, applyInset: false))
        {
            aspect = Mathf.Max(0.01f, resolvedPixelRect.width / Mathf.Max(1f, resolvedPixelRect.height));
            return true;
        }

        if (TryGetResolvedUvRect(mapping, out Rect resolvedUvRect, applyInset: false))
        {
            aspect = Mathf.Max(0.01f, resolvedUvRect.width / Mathf.Max(1e-5f, resolvedUvRect.height));
            return true;
        }

        Vector2Int span = GetClampedSpan(mapping.tileSpan);
        aspect = Mathf.Max(0.01f, (float)span.x / Mathf.Max(1, span.y));
        return true;
    }

    public bool TryGetPixelRect(Item item, out Rect pixelRect)
    {
        return TryGetPixelRect(item, out pixelRect, applyInset: true);
    }

    public bool TryGetPixelRect(Item item, out Rect pixelRect, bool applyInset)
    {
        pixelRect = default;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        if (!TryGetTexture(out Texture2D texture) || texture == null)
            return false;

        return TryGetPixelRect(mapping, texture, out pixelRect, applyInset);
    }

    private bool TryGetUvRect(ItemAtlasMapping mapping, out Rect uvRect, bool applyInset)
    {
        if (TryGetResolvedUvRect(mapping, out uvRect, applyInset))
            return true;

        uvRect = default;

        int tilesX = Mathf.Max(1, atlasSize.x);
        int tilesY = Mathf.Max(1, atlasSize.y);
        Vector2Int tile = new Vector2Int(
            Mathf.Clamp(mapping.tile.x, 0, tilesX - 1),
            Mathf.Clamp(mapping.tile.y, 0, tilesY - 1));

        Vector2Int span = GetClampedSpan(mapping.tileSpan);
        span.x = Mathf.Min(span.x, tilesX - tile.x);
        span.y = Mathf.Min(span.y, tilesY - tile.y);

        float cellWidth = 1f / tilesX;
        float cellHeight = 1f / tilesY;
        Vector2 inset = CalculateUvInset(cellWidth, cellHeight, applyInset);

        float minX = tile.x * cellWidth + inset.x;
        float minY = tile.y * cellHeight + inset.y;
        float maxX = (tile.x + span.x) * cellWidth - inset.x;
        float maxY = (tile.y + span.y) * cellHeight - inset.y;

        if (maxX <= minX || maxY <= minY)
            return false;

        uvRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    private bool TryGetPixelRect(ItemAtlasMapping mapping, Texture2D texture, out Rect pixelRect, bool applyInset)
    {
        if (TryGetResolvedPixelRect(mapping, out pixelRect, applyInset))
            return true;

        pixelRect = default;
        if (texture == null)
            return false;

        int tilesX = Mathf.Max(1, atlasSize.x);
        int tilesY = Mathf.Max(1, atlasSize.y);
        Vector2Int tile = new Vector2Int(
            Mathf.Clamp(mapping.tile.x, 0, tilesX - 1),
            Mathf.Clamp(mapping.tile.y, 0, tilesY - 1));

        Vector2Int span = GetClampedSpan(mapping.tileSpan);
        span.x = Mathf.Min(span.x, tilesX - tile.x);
        span.y = Mathf.Min(span.y, tilesY - tile.y);

        float cellWidth = (float)texture.width / tilesX;
        float cellHeight = (float)texture.height / tilesY;
        Vector2 inset = CalculatePixelInset(texture, cellWidth, cellHeight, applyInset);

        float minX = tile.x * cellWidth + inset.x;
        float minY = tile.y * cellHeight + inset.y;
        float maxX = (tile.x + span.x) * cellWidth - inset.x;
        float maxY = (tile.y + span.y) * cellHeight - inset.y;

        if (maxX <= minX || maxY <= minY)
            return false;

        pixelRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    private bool TryGetResolvedUvRect(ItemAtlasMapping mapping, out Rect uvRect, bool applyInset)
    {
        uvRect = default;
        if (!TryResolveEntryId(mapping, out string entryId))
            return false;

        if (!EnsureRuntimeAtlasBuilt() ||
            runtimeAtlasResult == null ||
            !runtimeAtlasResult.TryGetUv(entryId, out Rect resolvedUvRect))
        {
            return false;
        }

        if (applyInset)
        {
            Vector2 inset = CalculateResolvedUvInset(resolvedUvRect);
            float minX = resolvedUvRect.xMin + inset.x;
            float minY = resolvedUvRect.yMin + inset.y;
            float maxX = resolvedUvRect.xMax - inset.x;
            float maxY = resolvedUvRect.yMax - inset.y;
            if (maxX <= minX || maxY <= minY)
                return false;

            uvRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        uvRect = resolvedUvRect;
        return true;
    }

    private bool TryGetResolvedPixelRect(ItemAtlasMapping mapping, out Rect pixelRect, bool applyInset)
    {
        pixelRect = default;
        if (!TryResolveEntryId(mapping, out string entryId))
            return false;

        if (!EnsureRuntimeAtlasBuilt() || runtimeAtlasResult == null)
            return false;

        if (runtimeAtlasResult.pixelRectMap != null &&
            runtimeAtlasResult.pixelRectMap.TryGetValue(entryId, out RectInt pixelRectInt))
        {
            Rect resolvedPixelRect = Rect.MinMaxRect(
                pixelRectInt.xMin,
                pixelRectInt.yMin,
                pixelRectInt.xMax,
                pixelRectInt.yMax);

            if (!applyInset)
            {
                pixelRect = resolvedPixelRect;
                return true;
            }

            if (!TryGetResolvedAtlasTexture(out Texture2D atlas) || atlas == null)
                return false;

            Vector2 inset = CalculateResolvedPixelInset(atlas, resolvedPixelRect.width, resolvedPixelRect.height);
            float minX = resolvedPixelRect.xMin + inset.x;
            float minY = resolvedPixelRect.yMin + inset.y;
            float maxX = resolvedPixelRect.xMax - inset.x;
            float maxY = resolvedPixelRect.yMax - inset.y;
            if (maxX <= minX || maxY <= minY)
                return false;

            pixelRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        if (!TryGetResolvedAtlasTexture(out Texture2D fallbackTexture) || fallbackTexture == null)
            return false;

        if (!runtimeAtlasResult.TryGetUv(entryId, out Rect uvRect))
            return false;

        float minXFromUv = uvRect.xMin * fallbackTexture.width;
        float minYFromUv = uvRect.yMin * fallbackTexture.height;
        float maxXFromUv = uvRect.xMax * fallbackTexture.width;
        float maxYFromUv = uvRect.yMax * fallbackTexture.height;

        if (applyInset)
        {
            Vector2 inset = CalculateResolvedPixelInset(
                fallbackTexture,
                maxXFromUv - minXFromUv,
                maxYFromUv - minYFromUv);
            minXFromUv += inset.x;
            minYFromUv += inset.y;
            maxXFromUv -= inset.x;
            maxYFromUv -= inset.y;
        }

        if (maxXFromUv <= minXFromUv || maxYFromUv <= minYFromUv)
            return false;

        pixelRect = Rect.MinMaxRect(minXFromUv, minYFromUv, maxXFromUv, maxYFromUv);
        return true;
    }

    private bool TryGetResolvedAtlasTexture(out Texture2D texture)
    {
        if (EnsureRuntimeAtlasBuilt() && runtimeAtlasResult != null && runtimeAtlasResult.atlasTexture != null)
        {
            texture = runtimeAtlasResult.atlasTexture;
            return true;
        }

        texture = atlasTexture;
        if (texture != null)
            return true;

        return false;
    }

    private bool EnsureRuntimeAtlasBuilt()
    {
        if (runtimeAtlasResult != null && runtimeAtlasResult.atlasTexture != null)
        {
            if (atlasMaterial != null)
                ApplyResolvedAtlasTexture(atlasMaterial, runtimeAtlasResult.atlasTexture);

            return true;
        }

        if (runtimeAtlasBuildAttempted)
            return false;

        runtimeAtlasBuildAttempted = true;
        if (textureDatabase == null || !textureDatabase.HasValidEntries())
            return false;

        List<AtlasTextureEntry> entries = textureDatabase.GetOrCreateEntries();
        if (entries == null || entries.Count == 0)
            return false;

        try
        {
            AtlasBuilder atlasBuilder = new AtlasBuilder();
            runtimeAtlasResult = atlasBuilder.Build(entries, BuildRuntimeAtlasSettings());
            if (runtimeAtlasResult == null || runtimeAtlasResult.atlasTexture == null)
                return false;

            if (atlasMaterial != null)
                ApplyResolvedAtlasTexture(atlasMaterial, runtimeAtlasResult.atlasTexture);

            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"ItemAtlasDataSO: falha ao construir atlas runtime por entryId. {ex.Message}", this);
            runtimeAtlasResult = null;
            return false;
        }
    }

    private AtlasBuildSettings BuildRuntimeAtlasSettings()
    {
        AtlasBuildSettings settings = AtlasBuildSettings.Default;
        settings.paddingPixels = Mathf.Max(MinimumRuntimePaddingPixels, paddingPixels);
        settings.wrapMode = TextureWrapMode.Clamp;

        Texture2D referenceTexture = atlasTexture;
        if (referenceTexture == null && atlasMaterial != null)
        {
            Texture candidate = null;
            if (atlasMaterial.HasProperty("_BaseMap"))
                candidate = atlasMaterial.GetTexture("_BaseMap");
            if (candidate == null && atlasMaterial.HasProperty("_MainTex"))
                candidate = atlasMaterial.GetTexture("_MainTex");
            if (candidate == null)
                candidate = atlasMaterial.mainTexture;

            referenceTexture = candidate as Texture2D;
        }

        if (referenceTexture != null)
        {
            int referenceSize = Mathf.NextPowerOfTwo(Mathf.Max(referenceTexture.width, referenceTexture.height));
            settings.initialSize = Mathf.Max(1, referenceSize);
            settings.maxSize = Mathf.Max(settings.initialSize, referenceSize);
            settings.generateMipmaps = referenceTexture.mipmapCount > 1;
            settings.filterMode = referenceTexture.filterMode;
        }

        settings.generateMipmapsPerTile = settings.generateMipmaps;

        return settings;
    }

    private void ApplyResolvedAtlasTexture(Material material, Texture2D texture)
    {
        if (material == null || texture == null || appliedRuntimeAtlasTexture == texture || !Application.isPlaying)
            return;

        if (material.HasProperty("_Atlas"))
            material.SetTexture("_Atlas", texture);
        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        else
            material.mainTexture = texture;

        appliedRuntimeAtlasTexture = texture;
    }

    private static bool TryResolveEntryId(ItemAtlasMapping mapping, out string entryId)
    {
        entryId = ItemAtlasEntryIdUtility.SanitizeEntryId(mapping.entryId);
        if (!string.IsNullOrEmpty(entryId))
            return true;

        return ItemAtlasEntryIdUtility.TryBuildDefaultEntryId(mapping.item, out entryId);
    }

    private Vector2 CalculateUvInset(float cellWidth, float cellHeight, bool applyInset)
    {
        if (!applyInset)
            return Vector2.zero;

        if (TryGetTexture(out Texture2D texture) && texture != null && paddingPixels > 0)
        {
            return new Vector2(
                paddingPixels / Mathf.Max(1f, texture.width),
                paddingPixels / Mathf.Max(1f, texture.height));
        }

        float safeInset = Mathf.Max(0f, fallbackUvInset);
        return new Vector2(
            Mathf.Min(safeInset, cellWidth * 0.49f),
            Mathf.Min(safeInset, cellHeight * 0.49f));
    }

    private Vector2 CalculatePixelInset(Texture2D texture, float cellWidth, float cellHeight, bool applyInset)
    {
        if (!applyInset)
            return Vector2.zero;

        if (texture != null && paddingPixels > 0)
        {
            return new Vector2(
                Mathf.Min(paddingPixels, cellWidth * 0.49f),
                Mathf.Min(paddingPixels, cellHeight * 0.49f));
        }

        float safeInsetX = Mathf.Max(0f, fallbackUvInset) * Mathf.Max(1f, texture != null ? texture.width : 0f);
        float safeInsetY = Mathf.Max(0f, fallbackUvInset) * Mathf.Max(1f, texture != null ? texture.height : 0f);
        return new Vector2(
            Mathf.Min(safeInsetX, cellWidth * 0.49f),
            Mathf.Min(safeInsetY, cellHeight * 0.49f));
    }

    private Vector2 CalculateResolvedUvInset(Rect uvRect)
    {
        float safeInset = Mathf.Max(0f, fallbackUvInset);
        return new Vector2(
            Mathf.Min(safeInset, uvRect.width * 0.49f),
            Mathf.Min(safeInset, uvRect.height * 0.49f));
    }

    private Vector2 CalculateResolvedPixelInset(Texture2D texture, float width, float height)
    {
        float safeInsetX = Mathf.Max(0f, fallbackUvInset) * Mathf.Max(1f, texture != null ? texture.width : 0f);
        float safeInsetY = Mathf.Max(0f, fallbackUvInset) * Mathf.Max(1f, texture != null ? texture.height : 0f);
        return new Vector2(
            Mathf.Min(safeInsetX, width * 0.49f),
            Mathf.Min(safeInsetY, height * 0.49f));
    }

    private static Vector2Int GetClampedSpan(Vector2Int requestedSpan)
    {
        return new Vector2Int(
            Mathf.Max(1, requestedSpan.x),
            Mathf.Max(1, requestedSpan.y));
    }
}

[System.Serializable]
public struct ItemAtlasMapping
{
    public Item item;
    [Tooltip("ID logico da textura dentro do atlas. Ex.: item/stick. Quando vazio, tile/tileSpan continuam como fallback legado.")]
    public string entryId;
    public Vector2Int tile;
    public Vector2Int tileSpan;
}

public static class ItemAtlasEntryIdUtility
{
    public const string DefaultItemPathPrefix = "item/";

    public static string SanitizeEntryId(string entryId)
    {
        return string.IsNullOrWhiteSpace(entryId) ? string.Empty : entryId.Trim();
    }

    public static bool TryBuildDefaultEntryId(Item item, out string entryId)
    {
        entryId = string.Empty;
        if (item == null)
            return false;

        string candidate = NormalizePathSegment(item.name);
        if (string.IsNullOrEmpty(candidate))
            candidate = NormalizePathSegment(item.itemName);

        if (string.IsNullOrEmpty(candidate))
            return false;

        entryId = DefaultItemPathPrefix + candidate;
        return true;
    }

    private static string NormalizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim().ToLowerInvariant().Replace('\\', '/');
        System.Text.StringBuilder builder = new System.Text.StringBuilder(normalized.Length);
        bool previousWasSeparator = false;

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            bool isAllowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (isAllowed)
            {
                builder.Append(c);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
                continue;

            builder.Append('_');
            previousWasSeparator = true;
        }

        return builder.ToString().Trim('_');
    }
}
