using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TextureAtlasGenerator : MonoBehaviour
{
    public enum BlockKeyMode
    {
        EnumName = 0,
        MinecraftResourceLocation = 1
    }

    [Serializable]
    private struct AtlasUvEntry
    {
        public string id;
        public Rect uv;
    }

    [Header("Entries Database")]
    [Tooltip("Fonte principal dos AtlasTextureEntry. Quando atribuida, novas entradas sao salvas neste asset.")]
    public TextureAtlasDatabaseSO textureDatabase;

    [HideInInspector]
    public List<AtlasTextureEntry> textureEntries = new List<AtlasTextureEntry>();

    [Header("BlockType key mode")]
    public BlockKeyMode blockKeyMode = BlockKeyMode.MinecraftResourceLocation;
    public string resourceNamespace = AtlasKeyUtility.DefaultNamespace;
    public string blockPathPrefix = AtlasKeyUtility.DefaultBlockPathPrefix;

    [Header("Legacy entries (auto ID: block/<name>)")]
    public List<Texture2D> blockTextures = new List<Texture2D>();

    [Header("Dynamic Atlas")]
    [Min(1)] public int initialAtlasSize = 512;
    [Min(1)] public int maxAtlasSize = 4096;
    [Min(0)] public int paddingPixels = 2;
    public bool generateMipmaps = true;
    public TextureFormat atlasFormat = TextureFormat.RGBA32;
    public FilterMode filterMode = FilterMode.Point;
    public TextureWrapMode wrapMode = TextureWrapMode.Clamp;

    [Header("Runtime")]
    public bool generateOnStart = false;
    public Renderer targetRenderer;
    public List<Material> targetMaterials = new List<Material>();
    public string targetTextureProperty = "_Atlas";

    [Header("Options")]
    public bool saveToFile = false;
    public string savePath = "Assets/AtlasGerado.png";

    [SerializeField] private Texture2D generatedAtlas;
    [SerializeField] private List<AtlasUvEntry> generatedUvEntries = new List<AtlasUvEntry>();
    [SerializeField] private List<string> generatedLegacyEntryOrder = new List<string>();

    private readonly AtlasBuilder atlasBuilder = new AtlasBuilder();
    private readonly Dictionary<string, Rect> uvMap = new Dictionary<string, Rect>(StringComparer.Ordinal);
    private readonly Dictionary<BlockType, Rect> blockTypeUvMap = new Dictionary<BlockType, Rect>();

    public Texture2D GeneratedAtlas => generatedAtlas;
    public IReadOnlyDictionary<string, Rect> UvMap => uvMap;
    public IReadOnlyDictionary<BlockType, Rect> BlockTypeUvMap => blockTypeUvMap;
    public bool UsesTextureDatabase => textureDatabase != null;

    private void Start()
    {
        if (generateOnStart)
            GenerateAtlas();
    }

    public bool TryGetUv(string id, out Rect uvRect)
    {
        uvRect = default;
        return !string.IsNullOrWhiteSpace(id) && uvMap.TryGetValue(id.Trim(), out uvRect);
    }

    public bool TryGetUv(BlockType blockType, out Rect uvRect)
    {
        if (blockTypeUvMap.TryGetValue(blockType, out uvRect))
            return true;

        if (uvMap.TryGetValue(blockType.ToString(), out uvRect))
            return true;

        string minecraftLikeKey = ResolveBlockTypeAtlasKey(blockType);
        if (uvMap.TryGetValue(minecraftLikeKey, out uvRect))
            return true;

        uvRect = default;
        return false;
    }

    public bool TryGetLegacyTileUv(Vector2Int tileCoord, Vector2Int legacyGridSize, bool atlasOriginTopLeft, out Rect uvRect)
    {
        uvRect = default;
        if (generatedLegacyEntryOrder == null || generatedLegacyEntryOrder.Count == 0)
            return false;

        int safeTilesX = Mathf.Max(1, legacyGridSize.x);
        int safeTilesY = Mathf.Max(1, legacyGridSize.y);
        int tileX = Mathf.Clamp(tileCoord.x, 0, safeTilesX - 1);
        int tileY = Mathf.Clamp(tileCoord.y, 0, safeTilesY - 1);

        if (atlasOriginTopLeft)
            tileY = safeTilesY - 1 - tileY;

        int legacyIndex = tileX + tileY * safeTilesX;
        if (legacyIndex < 0 || legacyIndex >= generatedLegacyEntryOrder.Count)
            return false;

        string id = generatedLegacyEntryOrder[legacyIndex];
        return !string.IsNullOrWhiteSpace(id) && uvMap.TryGetValue(id, out uvRect);
    }

    public void GenerateAtlas()
    {
        List<string> legacyEntryOrder;
        List<AtlasTextureEntry> entries = BuildEntriesForGeneration(out legacyEntryOrder);
        if (entries.Count == 0)
        {
            Debug.LogError("TextureAtlasGenerator: no valid textures were provided.");
            return;
        }

        AtlasBuildSettings settings = new AtlasBuildSettings
        {
            initialSize = initialAtlasSize,
            maxSize = maxAtlasSize,
            paddingPixels = paddingPixels,
            generateMipmaps = generateMipmaps,
            format = atlasFormat,
            filterMode = filterMode,
            wrapMode = wrapMode
        };

        AtlasBuildResult result;
        try
        {
            result = atlasBuilder.Build(entries, settings);
        }
        catch (Exception ex)
        {
            Debug.LogError($"TextureAtlasGenerator: failed to build dynamic atlas. {ex.Message}");
            return;
        }

        generatedAtlas = result.atlasTexture;

        uvMap.Clear();
        blockTypeUvMap.Clear();
        generatedUvEntries.Clear();
        generatedLegacyEntryOrder.Clear();
        if (legacyEntryOrder != null)
        {
            for (int i = 0; i < legacyEntryOrder.Count; i++)
                generatedLegacyEntryOrder.Add(legacyEntryOrder[i]);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            AtlasTextureEntry entry = entries[i];
            if (entry == null || string.IsNullOrWhiteSpace(entry.id))
                continue;

            string id = entry.id.Trim();
            if (!result.uvMap.TryGetValue(id, out Rect uvRect))
                continue;

            uvMap[id] = uvRect;
            generatedUvEntries.Add(new AtlasUvEntry { id = id, uv = uvRect });
        }

        BuildBlockTypeLookupFromUvMap();

        ApplyAtlasToTargets(generatedAtlas);

        if (saveToFile && generatedAtlas != null)
            SaveAtlasToDisk(generatedAtlas);

        Debug.Log(
            $"TextureAtlasGenerator: built {generatedAtlas.width}x{generatedAtlas.height} atlas " +
            $"with {uvMap.Count} entries and {paddingPixels}px padding.");
    }

    private List<AtlasTextureEntry> BuildEntriesForGeneration(out List<string> legacyEntryOrder)
    {
        List<AtlasTextureEntry> entries = new List<AtlasTextureEntry>();
        legacyEntryOrder = new List<string>();
        HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
        bool preferTextureEntriesForLegacy = HasConfiguredTextureEntries();

        AppendTextureEntries(
            textureDatabase != null ? textureDatabase.entries : null,
            entries,
            legacyEntryOrder,
            usedIds,
            preferTextureEntriesForLegacy);
        AppendTextureEntries(
            textureEntries,
            entries,
            legacyEntryOrder,
            usedIds,
            preferTextureEntriesForLegacy);

        if (blockTextures != null)
        {
            for (int i = 0; i < blockTextures.Count; i++)
            {
                Texture2D texture = blockTextures[i];
                if (texture == null)
                    continue;

                string baseId = $"block/{texture.name.ToLowerInvariant()}";
                string id = baseId;
                int suffix = 1;
                while (!usedIds.Add(id))
                {
                    suffix++;
                    id = $"{baseId}_{suffix}";
                }

                entries.Add(new AtlasTextureEntry
                {
                    id = id,
                    texture = texture
                });

                if (!preferTextureEntriesForLegacy)
                    legacyEntryOrder.Add(id);
            }
        }

        return entries;
    }

    private void BuildBlockTypeLookupFromUvMap()
    {
        blockTypeUvMap.Clear();

        Array values = Enum.GetValues(typeof(BlockType));
        for (int i = 0; i < values.Length; i++)
        {
            BlockType blockType = (BlockType)values.GetValue(i);
            if (uvMap.TryGetValue(blockType.ToString(), out Rect uvRect))
            {
                blockTypeUvMap[blockType] = uvRect;
                continue;
            }

            string minecraftKey = ResolveBlockTypeAtlasKey(blockType);
            if (uvMap.TryGetValue(minecraftKey, out uvRect))
                blockTypeUvMap[blockType] = uvRect;
        }
    }

    public string ResolveBlockTypeAtlasKey(BlockType blockType)
    {
        if (blockKeyMode == BlockKeyMode.MinecraftResourceLocation)
        {
            return AtlasKeyUtility.BuildMinecraftBlockKey(
                blockType,
                resourceNamespace,
                blockPathPrefix);
        }

        return blockType.ToString();
    }

    [ContextMenu("Fill textureEntries From Legacy blockTextures")]
    public void FillTextureEntriesFromLegacy()
    {
        if (blockTextures == null || blockTextures.Count == 0)
        {
            Debug.LogWarning("TextureAtlasGenerator: legacy blockTextures is empty.");
            return;
        }

        List<AtlasTextureEntry> writableEntries = GetWritableTextureEntries();
        writableEntries.Clear();

        HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
        int addedCount = 0;

        for (int i = 0; i < blockTextures.Count; i++)
        {
            Texture2D texture = blockTextures[i];
            if (texture == null)
                continue;

            string baseId = $"block/{texture.name.ToLowerInvariant()}";
            string id = baseId;
            int suffix = 1;
            while (!usedIds.Add(id))
            {
                suffix++;
                id = $"{baseId}_{suffix}";
            }

            writableEntries.Add(new AtlasTextureEntry
            {
                id = id,
                texture = texture
            });
            addedCount++;
        }

#if UNITY_EDITOR
        if (textureDatabase != null)
            UnityEditor.EditorUtility.SetDirty(textureDatabase);
        UnityEditor.EditorUtility.SetDirty(this);
#endif

        string target = textureDatabase != null
            ? $"database '{textureDatabase.name}'"
            : "embedded textureEntries";
        Debug.Log($"TextureAtlasGenerator: migrated {addedCount} entries from legacy blockTextures to {target}.");
    }

    [ContextMenu("Move Embedded textureEntries To Database")]
    public void MoveEmbeddedTextureEntriesToDatabase()
    {
        if (textureDatabase == null)
        {
            Debug.LogWarning("TextureAtlasGenerator: assign a TextureAtlasDatabaseSO before moving embedded entries.");
            return;
        }

        if (textureEntries == null || textureEntries.Count == 0)
        {
            Debug.LogWarning("TextureAtlasGenerator: no embedded textureEntries were found to move.");
            return;
        }

        List<AtlasTextureEntry> databaseEntries = textureDatabase.GetOrCreateEntries();
        HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < databaseEntries.Count; i++)
        {
            AtlasTextureEntry existing = databaseEntries[i];
            if (existing == null || existing.texture == null)
                continue;

            string existingId = NormalizeEntryId(existing.id, existing.texture);
            if (!string.IsNullOrEmpty(existingId))
                usedIds.Add(existingId);
        }

        List<AtlasTextureEntry> remainingEmbeddedEntries = new List<AtlasTextureEntry>();
        int movedCount = 0;
        int skippedCount = 0;

        for (int i = 0; i < textureEntries.Count; i++)
        {
            AtlasTextureEntry copy = CreateNormalizedEntryCopy(textureEntries[i]);
            if (copy == null)
            {
                remainingEmbeddedEntries.Add(textureEntries[i]);
                continue;
            }

            if (!usedIds.Add(copy.id))
            {
                remainingEmbeddedEntries.Add(textureEntries[i]);
                skippedCount++;
                continue;
            }

            databaseEntries.Add(copy);
            movedCount++;
        }

        textureEntries = remainingEmbeddedEntries;

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(textureDatabase);
        UnityEditor.EditorUtility.SetDirty(this);
#endif

        Debug.Log(
            $"TextureAtlasGenerator: moved {movedCount} embedded entries to database '{textureDatabase.name}'. " +
            $"Skipped {skippedCount} duplicate entries.");
    }

    [ContextMenu("Apply Modern Minecraft Preset")]
    public void ApplyModernMinecraftPreset()
    {
        blockKeyMode = BlockKeyMode.MinecraftResourceLocation;
        resourceNamespace = AtlasKeyUtility.DefaultNamespace;
        blockPathPrefix = AtlasKeyUtility.DefaultBlockPathPrefix;
        initialAtlasSize = 1024;
        maxAtlasSize = 4096;
        paddingPixels = 2;
        generateMipmaps = true;
        atlasFormat = TextureFormat.RGBA32;
        filterMode = FilterMode.Point;
        wrapMode = TextureWrapMode.Clamp;
        generateOnStart = true;
        saveToFile = false;
        targetTextureProperty = "_Atlas";
    }

    private void ApplyAtlasToTargets(Texture2D atlas)
    {
        if (atlas == null)
            return;

        Renderer rendererToUse = targetRenderer != null ? targetRenderer : GetComponent<Renderer>();
        if (rendererToUse != null && rendererToUse.sharedMaterial != null)
            AssignTexture(rendererToUse.sharedMaterial, atlas, targetTextureProperty);

        if (targetMaterials == null)
            return;

        for (int i = 0; i < targetMaterials.Count; i++)
        {
            Material material = targetMaterials[i];
            if (material == null)
                continue;

            AssignTexture(material, atlas, targetTextureProperty);
        }
    }

    private static void AssignTexture(Material material, Texture2D texture, string preferredProperty)
    {
        if (material == null || texture == null)
            return;

        string safeProperty = string.IsNullOrWhiteSpace(preferredProperty) ? "_Atlas" : preferredProperty.Trim();

        if (material.HasProperty(safeProperty))
            material.SetTexture(safeProperty, texture);
        else if (material.HasProperty("_Atlas"))
            material.SetTexture("_Atlas", texture);
        else if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        else if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        else
            material.mainTexture = texture;
    }

    private void SaveAtlasToDisk(Texture2D atlas)
    {
        if (atlas == null || string.IsNullOrWhiteSpace(savePath))
            return;

        string normalizedPath = savePath.Replace('\\', '/');
        string directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        byte[] pngData = atlas.EncodeToPNG();
        File.WriteAllBytes(normalizedPath, pngData);

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif

        Debug.Log($"TextureAtlasGenerator: atlas saved to '{normalizedPath}'.");
    }

    public List<AtlasTextureEntry> GetWritableTextureEntries()
    {
        if (textureDatabase != null)
            return textureDatabase.GetOrCreateEntries();

        if (textureEntries == null)
            textureEntries = new List<AtlasTextureEntry>();

        return textureEntries;
    }

    public List<AtlasTextureEntry> GetAllTextureEntriesSnapshot()
    {
        List<AtlasTextureEntry> entries = new List<AtlasTextureEntry>();

        if (textureDatabase != null && textureDatabase.entries != null)
        {
            for (int i = 0; i < textureDatabase.entries.Count; i++)
                entries.Add(textureDatabase.entries[i]);
        }

        if (textureEntries != null)
        {
            for (int i = 0; i < textureEntries.Count; i++)
                entries.Add(textureEntries[i]);
        }

        return entries;
    }

    public bool HasConfiguredTextureEntries()
    {
        return HasValidTextureEntries(textureDatabase != null ? textureDatabase.entries : null) ||
               HasValidTextureEntries(textureEntries);
    }

    public bool HasEmbeddedTextureEntries()
    {
        return HasValidTextureEntries(textureEntries);
    }

    private void AppendTextureEntries(
        List<AtlasTextureEntry> sourceEntries,
        List<AtlasTextureEntry> targetEntries,
        List<string> legacyEntryOrder,
        HashSet<string> usedIds,
        bool appendToLegacyOrder)
    {
        if (sourceEntries == null)
            return;

        for (int i = 0; i < sourceEntries.Count; i++)
        {
            AtlasTextureEntry copy = CreateNormalizedEntryCopy(sourceEntries[i]);
            if (copy == null)
                continue;

            if (!usedIds.Add(copy.id))
            {
                Debug.LogWarning($"TextureAtlasGenerator: duplicate ID ignored: '{copy.id}'.");
                continue;
            }

            targetEntries.Add(copy);
            if (appendToLegacyOrder)
                legacyEntryOrder.Add(copy.id);
        }
    }

    private static AtlasTextureEntry CreateNormalizedEntryCopy(AtlasTextureEntry source)
    {
        if (source == null || source.texture == null)
            return null;

        string id = NormalizeEntryId(source.id, source.texture);
        if (string.IsNullOrEmpty(id))
            return null;

        return new AtlasTextureEntry
        {
            id = id,
            texture = source.texture,
            useSourceRect = source.useSourceRect,
            sourceRect = source.sourceRect,
            useUvSampling = source.useUvSampling,
            sampledUvRect = source.sampledUvRect
        };
    }

    private static string NormalizeEntryId(string id, Texture2D texture)
    {
        string safeId = (id ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(safeId) && texture != null)
            safeId = $"block/{texture.name.ToLowerInvariant()}";

        return safeId;
    }

    private static bool HasValidTextureEntries(List<AtlasTextureEntry> entries)
    {
        return entries != null && entries.Exists(entry => entry != null && entry.texture != null);
    }
}
