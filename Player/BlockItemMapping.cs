using System;
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

        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            if (blockItemMappings[i].blockType != blockType)
                continue;

            item = blockItemMappings[i].item;
            return item != null;
        }

        return false;
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

        return false;
    }
}
