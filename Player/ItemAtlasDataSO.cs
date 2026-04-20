using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ItemAtlasDataSO", menuName = "ScriptableObjects/ItemAtlasDataSO", order = 2)]
public class ItemAtlasDataSO : ScriptableObject
{
    private const int MinimumRuntimePaddingPixels = 2;

    [Header("Atlas")]
    public Texture2D atlasTexture;
    public Material atlasMaterial;
    [Tooltip("Database usada para resolver entryIds como item/stick e reconstruir o atlas em runtime.")]
    public TextureAtlasDatabaseSO textureDatabase;
    [Min(0)] public int paddingPixels = 0;
    [Min(0f)] public float fallbackUvInset = 0.001f;

    [Header("Mappings")]
    public List<ItemAtlasMapping> itemMappings = new List<ItemAtlasMapping>();

    [System.NonSerialized] private Dictionary<Item, ItemAtlasMapping> lookup;
    [System.NonSerialized] private AtlasBuildResult runtimeAtlasResult;
    [System.NonSerialized] private bool runtimeAtlasBuildAttempted;
    [System.NonSerialized] private Texture2D appliedRuntimeAtlasTexture;

    private void OnEnable()
    {
        InitializeLookup();
        ResetRuntimeAtlasState();
    }

    private void OnValidate()
    {
        InitializeLookup();
        ResetRuntimeAtlasState();
    }

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

    public bool HasMapping(Item item)
    {
        return TryGetTextureEntryId(item, out _);
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

        return true;
    }

    public bool TryGetTexture(out Texture2D texture)
    {
        if (TryGetResolvedAtlasTexture(out texture) && texture != null)
            return true;

        texture = ResolveTextureFromMaterial(atlasMaterial);
        return texture != null;
    }

    public bool TryGetUvRect(Item item, out Rect uvRect)
    {
        return TryGetUvRect(item, out uvRect, applyInset: true);
    }

    public bool TryGetUvRect(Item item, out Rect uvRect, bool applyInset)
    {
        uvRect = default;
        return TryGetTextureEntryId(item, out string entryId) &&
               TryGetResolvedUvRect(entryId, out uvRect, applyInset);
    }

    public bool TryGetAspect(Item item, out float aspect)
    {
        aspect = 1f;
        if (TryGetPixelRect(item, out Rect resolvedPixelRect, applyInset: false))
        {
            aspect = Mathf.Max(0.01f, resolvedPixelRect.width / Mathf.Max(1f, resolvedPixelRect.height));
            return true;
        }

        if (TryGetUvRect(item, out Rect resolvedUvRect, applyInset: false))
        {
            aspect = Mathf.Max(0.01f, resolvedUvRect.width / Mathf.Max(1e-5f, resolvedUvRect.height));
            return true;
        }

        return false;
    }

    public bool TryGetPixelRect(Item item, out Rect pixelRect)
    {
        return TryGetPixelRect(item, out pixelRect, applyInset: true);
    }

    public bool TryGetPixelRect(Item item, out Rect pixelRect, bool applyInset)
    {
        pixelRect = default;
        return TryGetTextureEntryId(item, out string entryId) &&
               TryGetResolvedPixelRect(entryId, out pixelRect, applyInset);
    }

    private bool TryGetMapping(Item item, out ItemAtlasMapping mapping)
    {
        mapping = default;
        if (item == null)
            return false;

        if (lookup == null)
            InitializeLookup();

        return lookup != null && lookup.TryGetValue(item, out mapping);
    }

    private bool TryGetResolvedUvRect(string entryId, out Rect uvRect, bool applyInset)
    {
        uvRect = default;
        if (string.IsNullOrEmpty(entryId))
            return false;

        if (!EnsureRuntimeAtlasBuilt() ||
            runtimeAtlasResult == null ||
            !runtimeAtlasResult.TryGetUv(entryId, out Rect resolvedUvRect))
        {
            return false;
        }

        if (!applyInset)
        {
            uvRect = resolvedUvRect;
            return true;
        }

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

    private bool TryGetResolvedPixelRect(string entryId, out Rect pixelRect, bool applyInset)
    {
        pixelRect = default;
        if (string.IsNullOrEmpty(entryId))
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
        if (EnsureRuntimeAtlasBuilt() &&
            runtimeAtlasResult != null &&
            runtimeAtlasResult.atlasTexture != null)
        {
            texture = runtimeAtlasResult.atlasTexture;
            return true;
        }

        texture = atlasTexture;
        if (texture != null)
            return true;

        texture = ResolveTextureFromMaterial(atlasMaterial);
        return texture != null;
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

        Texture2D referenceTexture = atlasTexture != null
            ? atlasTexture
            : ResolveTextureFromMaterial(atlasMaterial);

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

    private void ResetRuntimeAtlasState()
    {
        runtimeAtlasResult = null;
        runtimeAtlasBuildAttempted = false;
        appliedRuntimeAtlasTexture = null;
    }

    private static bool TryResolveEntryId(ItemAtlasMapping mapping, out string entryId)
    {
        entryId = ItemAtlasEntryIdUtility.SanitizeEntryId(mapping.entryId);
        if (!string.IsNullOrEmpty(entryId))
            return true;

        return ItemAtlasEntryIdUtility.TryBuildDefaultEntryId(mapping.item, out entryId);
    }

    private static Texture2D ResolveTextureFromMaterial(Material material)
    {
        if (material == null)
            return null;

        Texture candidate = null;
        if (material.HasProperty("_BaseMap"))
            candidate = material.GetTexture("_BaseMap");
        if (candidate == null && material.HasProperty("_MainTex"))
            candidate = material.GetTexture("_MainTex");
        if (candidate == null)
            candidate = material.mainTexture;

        return candidate as Texture2D;
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
}

[System.Serializable]
public struct ItemAtlasMapping
{
    public Item item;
    [Tooltip("ID logico da textura dentro do atlas. Ex.: item/stick.")]
    public string entryId;
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
