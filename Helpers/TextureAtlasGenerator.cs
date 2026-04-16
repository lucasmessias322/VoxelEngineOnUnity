using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TextureAtlasGenerator : MonoBehaviour
{
    public enum BlockKeyMode
    {
        EnumName = 0,
        MinecraftResourceLocation = 1
    }

    public enum SavedAtlasMipMapFilter
    {
        Box = 0,
        Kaiser = 1
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

    [Header("Dynamic Atlas")]
    [Min(1)] public int initialAtlasSize = 512;
    [Min(1)] public int maxAtlasSize = 4096;
    [Min(0)] public int paddingPixels = 2;
    public bool generateMipmaps = true;
    public TextureFormat atlasFormat = TextureFormat.RGBA32;
    public FilterMode filterMode = FilterMode.Point;
    public TextureWrapMode wrapMode = TextureWrapMode.Clamp;

    [Header("Saved Atlas Mipmaps")]
    [Tooltip("Quando o atlas for salvo como asset, define o filtro usado na geracao dos mipmaps importados.")]
    public SavedAtlasMipMapFilter mipMapFiltering = SavedAtlasMipMapFilter.Box;
    [Tooltip("Preserva a cobertura do alpha nos mipmaps do atlas salvo.")]
    public bool preserveMipMapCoverage = true;
    [Range(0f, 1f)]
    [Tooltip("Alpha cutoff usado quando Preserve Coverage estiver ativo. Para materiais alpha-clip, o ideal e combinar com o cutoff do shader; foliage costuma funcionar melhor entre 0.4 e 0.6.")]
    public float mipMapAlphaCutoff = 0.5f;
    [Tooltip("Replica a borda no atlas salvo ao gerar os mipmaps importados.")]
    public bool replicateMipMapBorder = false;

    [Header("Runtime Mipmaps")]
    [Tooltip("Experimental: gera os mipmaps do atlas por tile para evitar bleeding entre blocos no atlas runtime.")]
    public bool generateMipmapsPerTile = false;

    [Header("Shader Sampling")]
    [Min(0f)]
    [Tooltip("Inset em pixels aplicado no sampling do atlas nos shaders voxel para reduzir bleeding e bordas pretas com mipmaps.")]
    public float shaderPaddingPixels = 1f;
    [Min(0f)]
    [Tooltip("Inset extra em pixels aplicado automaticamente a materiais com Alpha Clip, como folhas, para reduzir bleeding de tiles vizinhos nos mipmaps.")]
    public float alphaClipShaderPaddingPixels = 2f;

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

    private void OnValidate()
    {
        mipMapAlphaCutoff = Mathf.Clamp01(mipMapAlphaCutoff);
        shaderPaddingPixels = Mathf.Max(0f, shaderPaddingPixels);
        alphaClipShaderPaddingPixels = Mathf.Max(shaderPaddingPixels, alphaClipShaderPaddingPixels);
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
        return TryGetLegacyTileId(tileCoord, legacyGridSize, atlasOriginTopLeft, out string id) &&
               uvMap.TryGetValue(id, out uvRect);
    }

    public bool TryGetLegacyTileId(Vector2Int tileCoord, Vector2Int legacyGridSize, bool atlasOriginTopLeft, out string id)
    {
        id = string.Empty;
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

        string resolvedId = generatedLegacyEntryOrder[legacyIndex];
        if (string.IsNullOrWhiteSpace(resolvedId))
            return false;

        id = resolvedId.Trim();
        return true;
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
            generateMipmapsPerTile = generateMipmapsPerTile,
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

        Texture2D atlasToApply = generatedAtlas;
        if (saveToFile && generatedAtlas != null)
        {
            Texture2D importedAtlas = SaveAtlasToDisk(generatedAtlas);
#if UNITY_EDITOR
            if (!Application.isPlaying &&
                !generateMipmapsPerTile &&
                importedAtlas != null)
            {
                atlasToApply = importedAtlas;
            }
#endif
        }

        generatedAtlas = atlasToApply;
        ApplyAtlasToTargets(generatedAtlas);

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
        paddingPixels = 8;
        generateMipmaps = true;
        atlasFormat = TextureFormat.RGBA32;
        filterMode = FilterMode.Point;
        wrapMode = TextureWrapMode.Clamp;
        mipMapFiltering = SavedAtlasMipMapFilter.Box;
        preserveMipMapCoverage = true;
        mipMapAlphaCutoff = 0.5f;
        replicateMipMapBorder = false;
        generateMipmapsPerTile = false;
        shaderPaddingPixels = 1f;
        alphaClipShaderPaddingPixels = 2f;
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

    public float ComputeShaderPaddingUv(Texture texture)
    {
        return ComputeShaderPaddingUv(texture, null);
    }

    public float ComputeShaderPaddingUv(Texture texture, Material material)
    {
        if (texture == null)
            return 0f;

        int referenceSize = Mathf.Max(texture.width, texture.height);
        if (referenceSize <= 0)
            return 0f;

        float paddingPixels = Mathf.Max(0f, shaderPaddingPixels);
        if (UsesAlphaClip(material))
            paddingPixels = Mathf.Max(paddingPixels, alphaClipShaderPaddingPixels);

        return paddingPixels / referenceSize;
    }

    private void AssignTexture(Material material, Texture2D texture, string preferredProperty)
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

        if (material.HasProperty("_PaddingUV"))
            material.SetFloat("_PaddingUV", ComputeShaderPaddingUv(texture, material));

#if UNITY_EDITOR
        if (!Application.isPlaying)
            EditorUtility.SetDirty(material);
#endif
    }

    private Texture2D SaveAtlasToDisk(Texture2D atlas)
    {
        if (atlas == null || string.IsNullOrWhiteSpace(savePath))
            return atlas;

        string normalizedPath = savePath.Replace('\\', '/');
        string absolutePath = ResolveAbsoluteSavePath(normalizedPath);
        string directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        byte[] pngData = atlas.EncodeToPNG();
        File.WriteAllBytes(absolutePath, pngData);

#if UNITY_EDITOR
        string assetPath = TryGetAssetPath(absolutePath);
        if (!string.IsNullOrEmpty(assetPath))
        {
            AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ConfigureSavedAtlasImporter(assetPath);

            Texture2D importedAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (importedAtlas != null)
            {
                EditorUtility.SetDirty(this);
                Debug.Log($"TextureAtlasGenerator: atlas saved to '{assetPath}'.");
                return importedAtlas;
            }
        }
        else
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
#endif

        Debug.Log($"TextureAtlasGenerator: atlas saved to '{normalizedPath}'.");
        return atlas;
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

    private static bool UsesAlphaClip(Material material)
    {
        return material != null &&
               material.HasProperty("_AlphaClip") &&
               material.GetFloat("_AlphaClip") > 0.5f;
    }

    private static string ResolveAbsoluteSavePath(string normalizedPath)
    {
        if (Path.IsPathRooted(normalizedPath))
            return Path.GetFullPath(normalizedPath);

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        return Path.GetFullPath(Path.Combine(projectRoot, normalizedPath));
    }

#if UNITY_EDITOR
    private void ConfigureSavedAtlasImporter(string assetPath)
    {
        if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer))
            return;

        bool changed = false;

        if (importer.filterMode != filterMode)
        {
            importer.filterMode = filterMode;
            changed = true;
        }

        if (importer.wrapMode != wrapMode)
        {
            importer.wrapMode = wrapMode;
            changed = true;
        }

        if (importer.mipmapEnabled != generateMipmaps)
        {
            importer.mipmapEnabled = generateMipmaps;
            changed = true;
        }

        TextureImporterMipFilter targetMipFilter = mipMapFiltering == SavedAtlasMipMapFilter.Kaiser
            ? TextureImporterMipFilter.KaiserFilter
            : TextureImporterMipFilter.BoxFilter;
        if (importer.mipmapFilter != targetMipFilter)
        {
            importer.mipmapFilter = targetMipFilter;
            changed = true;
        }

        if (importer.mipMapsPreserveCoverage != preserveMipMapCoverage)
        {
            importer.mipMapsPreserveCoverage = preserveMipMapCoverage;
            changed = true;
        }

        float safeAlphaCutoff = Mathf.Clamp01(mipMapAlphaCutoff);
        if (!Mathf.Approximately(importer.alphaTestReferenceValue, safeAlphaCutoff))
        {
            importer.alphaTestReferenceValue = safeAlphaCutoff;
            changed = true;
        }

        if (importer.borderMipmap != replicateMipMapBorder)
        {
            importer.borderMipmap = replicateMipMapBorder;
            changed = true;
        }

        if (changed)
            importer.SaveAndReimport();
    }

    private static string TryGetAssetPath(string absolutePath)
    {
        string normalizedAbsolutePath = Path.GetFullPath(absolutePath).Replace('\\', '/');
        string normalizedAssetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
        if (!normalizedAbsolutePath.StartsWith(normalizedAssetsPath, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string relativePath = normalizedAbsolutePath.Substring(normalizedAssetsPath.Length).TrimStart('/');
        return string.IsNullOrEmpty(relativePath) ? "Assets" : $"Assets/{relativePath}";
    }
#endif
}
