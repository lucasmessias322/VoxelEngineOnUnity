using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "BlockItemMappingSO", menuName = "ScriptableObjects/Block Item Mapping SO", order = 2)]
public class BlockItemMappingSO : ScriptableObject
{
    public bool TryGetItemForBlock(BlockType blockType, out Item item)
    {
        return BlockItemCatalog.TryGetItemForBlock(blockType, out item);
    }

    public bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        return BlockItemCatalog.TryGetBlockForItem(item, out blockType);
    }

    public void AppendMappedItems(List<Item> output, bool includeDuplicates = false)
    {
        BlockItemCatalog.AppendBlockItems(output, includeDuplicates);
    }
}

public static class BlockItemCatalog
{
    public const string DefaultBlockItemsResourcePath = "Itens/Blocks";

    private static bool isInitialized;
    private static Item[] cachedBlockItems = System.Array.Empty<Item>();
    private static readonly Dictionary<BlockType, Item> itemByBlockType = new Dictionary<BlockType, Item>();

    public static void ClearCache()
    {
        isInitialized = false;
        cachedBlockItems = System.Array.Empty<Item>();
        itemByBlockType.Clear();
    }

    public static bool TryGetItemForBlock(BlockType blockType, out Item item)
    {
        EnsureInitialized();

        if (TryGetFallbackInventoryBlockType(blockType, out BlockType fallbackBlockType) &&
            itemByBlockType.TryGetValue(fallbackBlockType, out item) &&
            item != null)
        {
            return true;
        }

        if (itemByBlockType.TryGetValue(blockType, out item) && item != null)
            return true;

        item = null;
        return false;
    }

    public static bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        if (item != null && item.TryGetBlockType(out blockType))
            return true;

        blockType = BlockType.Air;
        return false;
    }

    public static void AppendBlockItems(List<Item> output, bool includeDuplicates = false)
    {
        if (output == null)
            return;

        EnsureInitialized();

        HashSet<Item> seen = includeDuplicates ? null : new HashSet<Item>(output);
        for (int i = 0; i < cachedBlockItems.Length; i++)
        {
            Item item = cachedBlockItems[i];
            if (item == null)
                continue;

            if (seen != null && !seen.Add(item))
                continue;

            output.Add(item);
        }
    }

    private static void EnsureInitialized()
    {
        if (isInitialized)
            return;

        isInitialized = true;
        itemByBlockType.Clear();

        Item[] loadedItems = Resources.LoadAll<Item>(DefaultBlockItemsResourcePath);
        if (loadedItems == null || loadedItems.Length == 0)
        {
            cachedBlockItems = System.Array.Empty<Item>();
            return;
        }

        List<Item> validBlockItems = new List<Item>(loadedItems.Length);
        for (int i = 0; i < loadedItems.Length; i++)
        {
            Item item = loadedItems[i];
            if (item == null || !item.TryGetBlockType(out BlockType blockType))
                continue;

            if (itemByBlockType.TryGetValue(blockType, out Item existingItem) && existingItem != item)
            {
                if (ShouldPreferDuplicateBlockItem(blockType, existingItem, item))
                {
                    int existingIndex = validBlockItems.IndexOf(existingItem);
                    if (existingIndex >= 0)
                        validBlockItems[existingIndex] = item;

                    itemByBlockType[blockType] = item;
                    continue;
                }

                Debug.LogWarning($"[BlockItemCatalog] BlockType '{blockType}' esta duplicado entre '{existingItem.name}' e '{item.name}'. O primeiro item carregado sera mantido.");
                continue;
            }

            itemByBlockType[blockType] = item;
            validBlockItems.Add(item);
        }

        cachedBlockItems = validBlockItems.ToArray();
    }

    private static bool ShouldPreferDuplicateBlockItem(BlockType blockType, Item existingItem, Item candidateItem)
    {
        return blockType == BlockType.TransportTube &&
               IsPreferredTransportTubeItem(candidateItem) &&
               !IsPreferredTransportTubeItem(existingItem);
    }

    private static bool IsPreferredTransportTubeItem(Item item)
    {
        return item != null &&
               (string.Equals(item.name, "TransportTube", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.itemName, "TransportTube", System.StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetFallbackInventoryBlockType(BlockType blockType, out BlockType fallbackBlockType)
    {
        fallbackBlockType = BatteryBlockUtility.GetInventoryDropBlockType(blockType);
        if (fallbackBlockType != blockType)
            return true;

        fallbackBlockType = TransportTubeUtility.GetInventoryDropBlockType(blockType);
        if (fallbackBlockType != blockType)
            return true;

        fallbackBlockType = TorchPlacementUtility.GetInventoryDropBlockType(blockType);
        if (fallbackBlockType != blockType)
            return true;

        fallbackBlockType = LeverUtility.GetInventoryDropBlockType(blockType);
        return fallbackBlockType != blockType;
    }
}
