using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "BlockItemMappingSO", menuName = "ScriptableObjects/Block Item Mapping SO", order = 2)]
public class BlockItemMappingSO : ScriptableObject
{
    [System.Serializable]
    public struct BlockItemMapping
    {
        public BlockType blockType;
        public Item item;
    }

    [SerializeField] private BlockItemMapping[] blockItemMappings;

    public bool TryGetItemForBlock(BlockType blockType, out Item item)
    {
        item = null;
        if (blockItemMappings == null)
            return false;

        if (TryGetExplicitItemForBlock(blockType, out item))
            return true;

        BlockType fallbackBlockType = TorchPlacementUtility.GetInventoryDropBlockType(blockType);
        return fallbackBlockType != blockType &&
               TryGetExplicitItemForBlock(fallbackBlockType, out item);
    }

    public bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        blockType = BlockType.Air;
        if (item == null || blockItemMappings == null)
            return false;

        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            if (blockItemMappings[i].item != item)
                continue;

            blockType = blockItemMappings[i].blockType;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.itemName) &&
            item.itemName.Equals("torch", StringComparison.OrdinalIgnoreCase))
        {
            blockType = BlockType.glowstone;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.itemName) &&
            item.itemName.Equals("wire", StringComparison.OrdinalIgnoreCase))
        {
            blockType = BlockType.wire;
            return true;
        }

        return false;
    }

    public void AppendMappedItems(List<Item> output, bool includeDuplicates = false)
    {
        if (output == null || blockItemMappings == null)
            return;

        HashSet<Item> seen = includeDuplicates ? null : new HashSet<Item>(output);
        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            Item item = blockItemMappings[i].item;
            if (item == null)
                continue;

            if (seen != null && !seen.Add(item))
                continue;

            output.Add(item);
        }
    }

    private bool TryGetExplicitItemForBlock(BlockType blockType, out Item item)
    {
        item = null;
        if (blockItemMappings == null)
            return false;

        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            if (blockItemMappings[i].blockType != blockType)
                continue;

            item = blockItemMappings[i].item;
            return item != null;
        }

        return false;
    }
}
