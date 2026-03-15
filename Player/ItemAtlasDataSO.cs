using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ItemAtlasDataSO", menuName = "ScriptableObjects/ItemAtlasDataSO", order = 2)]
public class ItemAtlasDataSO : ScriptableObject
{
    [Header("Atlas")]
    public Vector2Int atlasSize = new Vector2Int(4, 4);
    public Texture2D atlasTexture;
    public Material atlasMaterial;
    [Min(0)] public int paddingPixels = 0;
    [Min(0f)] public float fallbackUvInset = 0.001f;

    [Header("Mappings")]
    public List<ItemAtlasMapping> itemMappings = new List<ItemAtlasMapping>();

    [System.NonSerialized] private Dictionary<Item, ItemAtlasMapping> lookup;

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

    public bool TryGetMaterial(out Material material)
    {
        material = atlasMaterial;
        return material != null;
    }

    public bool TryGetTexture(out Texture2D texture)
    {
        texture = atlasTexture;
        if (texture != null)
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
        uvRect = default;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        return TryGetUvRect(mapping, out uvRect);
    }

    public bool TryGetAspect(Item item, out float aspect)
    {
        aspect = 1f;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        Vector2Int span = GetClampedSpan(mapping.tileSpan);
        aspect = Mathf.Max(0.01f, (float)span.x / Mathf.Max(1, span.y));
        return true;
    }

    public bool TryGetPixelRect(Item item, out Rect pixelRect)
    {
        pixelRect = default;
        if (!TryGetMapping(item, out ItemAtlasMapping mapping))
            return false;

        if (!TryGetTexture(out Texture2D texture) || texture == null)
            return false;

        return TryGetPixelRect(mapping, texture, out pixelRect);
    }

    private bool TryGetUvRect(ItemAtlasMapping mapping, out Rect uvRect)
    {
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
        Vector2 inset = CalculateUvInset(cellWidth, cellHeight);

        float minX = tile.x * cellWidth + inset.x;
        float minY = tile.y * cellHeight + inset.y;
        float maxX = (tile.x + span.x) * cellWidth - inset.x;
        float maxY = (tile.y + span.y) * cellHeight - inset.y;

        if (maxX <= minX || maxY <= minY)
            return false;

        uvRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    private bool TryGetPixelRect(ItemAtlasMapping mapping, Texture2D texture, out Rect pixelRect)
    {
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
        Vector2 inset = CalculatePixelInset(texture, cellWidth, cellHeight);

        float minX = tile.x * cellWidth + inset.x;
        float minY = tile.y * cellHeight + inset.y;
        float maxX = (tile.x + span.x) * cellWidth - inset.x;
        float maxY = (tile.y + span.y) * cellHeight - inset.y;

        if (maxX <= minX || maxY <= minY)
            return false;

        pixelRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    private Vector2 CalculateUvInset(float cellWidth, float cellHeight)
    {
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

    private Vector2 CalculatePixelInset(Texture2D texture, float cellWidth, float cellHeight)
    {
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
    public Vector2Int tile;
    public Vector2Int tileSpan;
}
