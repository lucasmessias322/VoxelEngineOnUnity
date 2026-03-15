using System.Collections.Generic;
using UnityEngine;

public static class ItemAtlasIconCache
{
    private static readonly Dictionary<Item, Sprite> Cache = new Dictionary<Item, Sprite>();

    private static ItemAtlasDataSO cachedAtlasData;
    private static Texture2D cachedTexture;

    public static bool TryGetIcon(Item item, out Sprite icon)
    {
        icon = null;
        if (item == null)
            return false;

        if (!TryResolveAtlas(out ItemAtlasDataSO atlasData, out Texture2D atlasTexture))
            return false;

        if (atlasData != cachedAtlasData || atlasTexture != cachedTexture)
            ClearCache();

        if (Cache.TryGetValue(item, out Sprite cachedIcon) && cachedIcon != null)
        {
            icon = cachedIcon;
            return true;
        }

        if (!atlasData.TryGetPixelRect(item, out Rect pixelRect))
            return false;

        icon = Sprite.Create(
            atlasTexture,
            pixelRect,
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect);

        icon.name = item.name + "_AtlasIcon";
        Cache[item] = icon;
        cachedAtlasData = atlasData;
        cachedTexture = atlasTexture;
        return true;
    }

    private static bool TryResolveAtlas(out ItemAtlasDataSO atlasData, out Texture2D atlasTexture)
    {
        atlasData = null;
        atlasTexture = null;

        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory == null || !inventory.TryGetItemAtlasData(out atlasData) || atlasData == null)
            return false;

        if (!atlasData.TryGetTexture(out atlasTexture) || atlasTexture == null)
            return false;

        return true;
    }

    private static void ClearCache()
    {
        foreach (KeyValuePair<Item, Sprite> pair in Cache)
        {
            if (pair.Value != null)
                Object.Destroy(pair.Value);
        }

        Cache.Clear();
        cachedAtlasData = null;
        cachedTexture = null;
    }
}
