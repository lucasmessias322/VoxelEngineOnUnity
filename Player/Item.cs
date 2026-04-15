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
    [Tooltip("Auto usa sprite direto quando existir, senao bloco isometrico para itens mapeados como bloco. ItemIconOnly ignora o bloco e usa sprite/atlas. IsometricBlockOnly sempre usa o bloco.")]
    public InventoryIconMode inventoryIconMode = InventoryIconMode.Auto;

    [Header("Stack")]
    [Min(1)] public int maxStack = 64;

    [Header("Tool")]
    public ToolType toolType = ToolType.None;
    [Min(1f)] public float toolEfficiency = 1f;

    [Header("Visual")]
    [Tooltip("Sprite principal do item. Quando definido, UI, drop no mundo e item plano na mao usam este sprite direto, sem depender de coordenadas de atlas.")]
    public Sprite itemSprite;

    [Header("Held Visual")]
    [Tooltip("Optional prefab shown in the player's hand when this item is selected.")]
    public GameObject heldPrefab;
    [Tooltip("Legacy toggle: overrides held position, rotation and scale together.")]
    public bool overrideHeldTransform;
    [Tooltip("Overrides only held local position when selected.")]
    public bool overrideHeldPosition;
    [Tooltip("Overrides only held local rotation when selected.")]
    public bool overrideHeldRotation;
    [Tooltip("Overrides only held local scale when selected.")]
    public bool overrideHeldScale;
    public Vector3 heldLocalPosition = new Vector3(0.35f, -0.3f, 0.55f);
    public Vector3 heldLocalEulerAngles = new Vector3(20f, -25f, -8f);
    public Vector3 heldLocalScale = Vector3.one;
}

public static class ItemIconResolver
{
    public static bool TryGetDirectSprite(Item item, out Sprite sprite)
    {
        sprite = item != null ? item.itemSprite : null;
        return sprite != null;
    }

    public static Sprite ResolveForUI(Item item)
    {
        if (item == null)
            return null;

        if (item.inventoryIconMode == InventoryIconMode.IsometricBlockOnly)
            return TryResolveBlockIcon(item, out Sprite forcedBlockIcon) ? forcedBlockIcon : null;

        if (TryGetDirectSprite(item, out Sprite directSprite))
            return directSprite;

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

        return null;
    }
}
