using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class AtlasRegistry : MonoBehaviour
{
    [Serializable]
    public sealed class AtlasGroupConfig
    {
        public string atlasId = "blocks";
        [Min(1)] public int initialAtlasSize = 512;
        [Min(1)] public int maxAtlasSize = 4096;
        [Min(0)] public int paddingPixels = 2;
        public bool generateMipmaps = true;
        public bool generateMipmapsPerTile = false;
        public TextureFormat atlasFormat = TextureFormat.RGBA32;
        public FilterMode filterMode = FilterMode.Point;
        public TextureWrapMode wrapMode = TextureWrapMode.Clamp;
        public string targetTextureProperty = "_Atlas";
        public List<Material> targetMaterials = new List<Material>();
        public List<AtlasTextureEntry> entries = new List<AtlasTextureEntry>();

        public AtlasBuildSettings ToBuildSettings()
        {
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
            settings.Sanitize();
            return settings;
        }
    }

    [SerializeField] private bool generateOnStart = true;
    [SerializeField] private List<AtlasGroupConfig> atlasGroups = new List<AtlasGroupConfig>();

    private readonly AtlasBuilder atlasBuilder = new AtlasBuilder();
    private readonly Dictionary<string, AtlasBuildResult> builtAtlases = new Dictionary<string, AtlasBuildResult>(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Texture2D>> runtimeEntriesByAtlas =
        new Dictionary<string, Dictionary<string, Texture2D>>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, AtlasBuildResult> BuiltAtlases => builtAtlases;

    private void Start()
    {
        if (generateOnStart)
            RebuildAllAtlases();
    }

    public void RebuildAllAtlases()
    {
        for (int i = 0; i < atlasGroups.Count; i++)
            RebuildGroup(atlasGroups[i]);
    }

    public bool RebuildAtlas(string atlasId)
    {
        if (string.IsNullOrWhiteSpace(atlasId))
            return false;

        AtlasGroupConfig group = FindGroupById(atlasId);
        if (group == null)
        {
            Debug.LogWarning($"AtlasRegistry: grupo '{atlasId}' nao encontrado.");
            return false;
        }

        return RebuildGroup(group);
    }

    public bool RegisterRuntimeTexture(string atlasId, string textureId, Texture2D texture, bool rebuildNow = true)
    {
        if (string.IsNullOrWhiteSpace(atlasId) || string.IsNullOrWhiteSpace(textureId) || texture == null)
            return false;

        if (!runtimeEntriesByAtlas.TryGetValue(atlasId, out Dictionary<string, Texture2D> runtimeEntries))
        {
            runtimeEntries = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            runtimeEntriesByAtlas[atlasId] = runtimeEntries;
        }

        runtimeEntries[textureId.Trim()] = texture;
        return !rebuildNow || RebuildAtlas(atlasId);
    }

    public bool UnregisterRuntimeTexture(string atlasId, string textureId, bool rebuildNow = true)
    {
        if (string.IsNullOrWhiteSpace(atlasId) || string.IsNullOrWhiteSpace(textureId))
            return false;

        if (!runtimeEntriesByAtlas.TryGetValue(atlasId, out Dictionary<string, Texture2D> runtimeEntries))
            return false;

        bool removed = runtimeEntries.Remove(textureId.Trim());
        if (!removed)
            return false;

        if (runtimeEntries.Count == 0)
            runtimeEntriesByAtlas.Remove(atlasId);

        return !rebuildNow || RebuildAtlas(atlasId);
    }

    public bool TryGetAtlasTexture(string atlasId, out Texture2D atlasTexture)
    {
        atlasTexture = null;
        if (string.IsNullOrWhiteSpace(atlasId))
            return false;

        if (!builtAtlases.TryGetValue(atlasId, out AtlasBuildResult result) || result == null)
            return false;

        atlasTexture = result.atlasTexture;
        return atlasTexture != null;
    }

    public bool TryGetUv(string atlasId, string textureId, out Rect uvRect)
    {
        uvRect = default;
        if (string.IsNullOrWhiteSpace(atlasId) || string.IsNullOrWhiteSpace(textureId))
            return false;

        if (!builtAtlases.TryGetValue(atlasId, out AtlasBuildResult result) || result == null)
            return false;

        return result.TryGetUv(textureId.Trim(), out uvRect);
    }

    public bool TryGetUv(string atlasId, BlockType blockType, out Rect uvRect)
    {
        if (TryGetUv(atlasId, blockType.ToString(), out uvRect))
            return true;

        string minecraftLikeKey = AtlasKeyUtility.BuildMinecraftBlockKey(blockType);
        return TryGetUv(atlasId, minecraftLikeKey, out uvRect);
    }

    public bool TryGetResult(string atlasId, out AtlasBuildResult result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(atlasId))
            return false;

        return builtAtlases.TryGetValue(atlasId, out result) && result != null;
    }

    private bool RebuildGroup(AtlasGroupConfig group)
    {
        if (group == null)
            return false;

        string atlasId = (group.atlasId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(atlasId))
        {
            Debug.LogWarning("AtlasRegistry: grupo ignorado porque atlasId esta vazio.");
            return false;
        }

        List<AtlasTextureEntry> mergedEntries = MergeGroupEntries(group);
        if (mergedEntries.Count == 0)
        {
            Debug.LogWarning($"AtlasRegistry: grupo '{atlasId}' nao possui texturas para empacotar.");
            builtAtlases.Remove(atlasId);
            return false;
        }

        try
        {
            AtlasBuildSettings settings = group.ToBuildSettings();
            AtlasBuildResult result = atlasBuilder.Build(mergedEntries, settings);
            builtAtlases[atlasId] = result;
            ApplyAtlasToMaterials(group, result.atlasTexture);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"AtlasRegistry: falha ao gerar atlas '{atlasId}'. {ex.Message}");
            return false;
        }
    }

    private List<AtlasTextureEntry> MergeGroupEntries(AtlasGroupConfig group)
    {
        Dictionary<string, AtlasTextureEntry> merged = new Dictionary<string, AtlasTextureEntry>(StringComparer.Ordinal);

        if (group.entries != null)
        {
            for (int i = 0; i < group.entries.Count; i++)
            {
                AtlasTextureEntry entry = group.entries[i];
                if (entry == null || entry.texture == null)
                    continue;

                string id = (entry.id ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(id))
                    continue;

                merged[id] = new AtlasTextureEntry
                {
                    id = id,
                    texture = entry.texture
                };
            }
        }

        string atlasId = (group.atlasId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(atlasId) &&
            runtimeEntriesByAtlas.TryGetValue(atlasId, out Dictionary<string, Texture2D> runtimeEntries))
        {
            foreach (KeyValuePair<string, Texture2D> pair in runtimeEntries)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                    continue;

                string id = pair.Key.Trim();
                merged[id] = new AtlasTextureEntry
                {
                    id = id,
                    texture = pair.Value
                };
            }
        }

        List<AtlasTextureEntry> list = new List<AtlasTextureEntry>(merged.Count);
        foreach (KeyValuePair<string, AtlasTextureEntry> pair in merged)
            list.Add(pair.Value);

        return list;
    }

    private static void ApplyAtlasToMaterials(AtlasGroupConfig group, Texture2D atlasTexture)
    {
        if (group == null || atlasTexture == null || group.targetMaterials == null)
            return;

        string preferredProperty = string.IsNullOrWhiteSpace(group.targetTextureProperty)
            ? "_Atlas"
            : group.targetTextureProperty;

        for (int i = 0; i < group.targetMaterials.Count; i++)
        {
            Material material = group.targetMaterials[i];
            if (material == null)
                continue;

            if (material.HasProperty(preferredProperty))
            {
                material.SetTexture(preferredProperty, atlasTexture);
            }
            else if (material.HasProperty("_Atlas"))
            {
                material.SetTexture("_Atlas", atlasTexture);
            }
            else if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", atlasTexture);
            }
            else if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", atlasTexture);
            }
            else
            {
                material.mainTexture = atlasTexture;
            }
        }
    }

    private AtlasGroupConfig FindGroupById(string atlasId)
    {
        if (atlasGroups == null)
            return null;

        for (int i = 0; i < atlasGroups.Count; i++)
        {
            AtlasGroupConfig group = atlasGroups[i];
            if (group == null)
                continue;

            if (string.Equals(group.atlasId, atlasId, StringComparison.Ordinal))
                return group;
        }

        return null;
    }
}
