using UnityEngine;


[CreateAssetMenu(fileName = "NewItem", menuName = "Inventory/Item")]
public class Item : ScriptableObject
{
    [Header("Info")]
    public string itemName;
    public Sprite icon;

    [Header("Stack")]
    [Min(1)] public int maxStack = 64;

    [Header("Held Visual")]
    [Tooltip("Optional prefab shown in the player's hand when this item is selected.")]
    public GameObject heldPrefab;
    [Tooltip("Use these values instead of the default held-item transform.")]
    public bool overrideHeldTransform;
    public Vector3 heldLocalPosition = new Vector3(0.35f, -0.3f, 0.55f);
    public Vector3 heldLocalEulerAngles = new Vector3(20f, -25f, -8f);
    public Vector3 heldLocalScale = Vector3.one;
}
