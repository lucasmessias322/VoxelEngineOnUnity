using UnityEngine;

public enum InventoryIconMode
{
    Auto = 0,
    ItemIconOnly = 1,
    IsometricBlockOnly = 2
}

[CreateAssetMenu(fileName = "NewItem", menuName = "Inventory/Item")]
public class Item : ScriptableObject
{
    [Header("Info")]
    public string itemName;
    public Sprite icon;
    [Tooltip("Auto usa bloco isometrico para itens mapeados como bloco. ItemIconOnly sempre usa o icone/atlas do item.")]
    public InventoryIconMode inventoryIconMode = InventoryIconMode.Auto;

    [Header("Stack")]
    [Min(1)] public int maxStack = 64;

    [Header("Tool")]
    public ToolType toolType = ToolType.None;
    [Min(1f)] public float toolEfficiency = 1f;

    [Header("Held Visual")]
    [Tooltip("Optional prefab shown in the player's hand when this item is selected.")]
    public GameObject heldPrefab;
    [Tooltip("Use these values instead of the default held-item transform.")]
    public bool overrideHeldTransform;
    public Vector3 heldLocalPosition = new Vector3(0.35f, -0.3f, 0.55f);
    public Vector3 heldLocalEulerAngles = new Vector3(20f, -25f, -8f);
    public Vector3 heldLocalScale = Vector3.one;
}

public static class ItemIconResolver
{
    public static Sprite ResolveForUI(Item item)
    {
        if (item == null)
            return null;

        if (item.inventoryIconMode == InventoryIconMode.ItemIconOnly)
            return ResolveFlatItemIcon(item);

        if (TryResolveBlockIcon(item, out Sprite blockIcon))
            return blockIcon;

        return ResolveFlatItemIcon(item);
    }

    private static bool TryResolveBlockIcon(Item item, out Sprite icon)
    {
        icon = null;
        if (item == null || item.inventoryIconMode == InventoryIconMode.ItemIconOnly)
            return false;

        PlayerInventory inventory = PlayerInventory.Instance;
        return inventory != null &&
               inventory.TryGetBlockForItem(item, out BlockType blockType) &&
               BlockItemIconCache.TryGetIcon(blockType, out icon) &&
               icon != null;
    }

    private static Sprite ResolveFlatItemIcon(Item item)
    {
        if (item == null)
            return null;

        if (ItemAtlasIconCache.TryGetIcon(item, out Sprite atlasIcon) && atlasIcon != null)
            return atlasIcon;

        return item.icon;
    }
}
