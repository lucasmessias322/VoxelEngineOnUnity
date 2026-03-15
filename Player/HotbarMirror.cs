using System.Collections.Generic;
using UnityEngine;

public class HotbarMirror : MonoBehaviour
{
    private const string SelectedChildName = "SelectedImage";

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private Transform hotbarSlotsContainer;

    [Header("Config")]
    [Min(1)] [SerializeField] private int hotbarSize = 7;
    [SerializeField] private bool includeInactiveSlots = true;
    [SerializeField] private bool autoMapHotbarSlotsOnAwake = true;
    [SerializeField] private bool enableNumberKeySelection = true;
    [SerializeField] private bool enableMouseWheelSelection = true;

    [Header("Runtime (Read Only)")]
    [SerializeField] private Slot[] hotbarSlots;
    [SerializeField] private int selectedSlotIndex;

    public int SelectedSlotIndex => selectedSlotIndex;

    private void Awake()
    {
        if (inventory == null)
            inventory = PlayerInventory.Instance != null ? PlayerInventory.Instance : FindAnyObjectByType<PlayerInventory>();

        if (autoMapHotbarSlotsOnAwake)
            MapHotbarSlotsFromContainer();

        SyncNow();
        RefreshSelectionVisuals();
    }

    private void Update()
    {
        HandleSelectionInput();
    }

    private void LateUpdate()
    {
        SyncNow();
    }

    [ContextMenu("Map Hotbar Slots From Container")]
    public void MapHotbarSlotsFromContainer()
    {
        if (hotbarSlotsContainer == null)
        {
            hotbarSlots = System.Array.Empty<Slot>();
            return;
        }

        List<Slot> found = new List<Slot>();
        CollectSlotsRecursive(hotbarSlotsContainer, found);
        hotbarSlots = found.ToArray();
        selectedSlotIndex = Mathf.Clamp(selectedSlotIndex, 0, Mathf.Max(0, hotbarSize - 1));
        RefreshSelectionVisuals();
    }

    [ContextMenu("Sync Hotbar Now")]
    public void SyncNow()
    {
        if (inventory == null)
            return;

        Slot[] invSlots = inventory.Slots;
        if (invSlots == null || invSlots.Length == 0)
        {
            inventory.MapSlotsFromContainer();
            invSlots = inventory.Slots;
        }
        if (invSlots == null)
            invSlots = System.Array.Empty<Slot>();

        int mirrorCount = Mathf.Min(hotbarSize, hotbarSlots != null ? hotbarSlots.Length : 0);
        for (int i = 0; i < mirrorCount; i++)
        {
            Slot source = invSlots.Length > i ? invSlots[i] : null;
            Slot target = hotbarSlots[i];
            if (target == null)
                continue;

            if (source == null || source.IsEmpty)
            {
                target.Clear();
                continue;
            }

            target.item = source.item;
            target.amount = source.amount;
            target.RefreshUI();
        }

        for (int i = mirrorCount; i < (hotbarSlots != null ? hotbarSlots.Length : 0); i++)
        {
            Slot extra = hotbarSlots[i];
            if (extra != null)
                extra.Clear();
        }

        RefreshSelectionVisuals();
    }

    public void SetSelectedSlotIndex(int index)
    {
        int maxIndex = Mathf.Max(0, hotbarSize - 1);
        selectedSlotIndex = Mathf.Clamp(index, 0, maxIndex);
        RefreshSelectionVisuals();
    }

    public bool TryGetSelectedItem(out Item selectedItem)
    {
        selectedItem = null;
        if (inventory == null)
            return false;

        Slot[] invSlots = inventory.Slots;
        if (invSlots == null || invSlots.Length == 0)
        {
            inventory.MapSlotsFromContainer();
            invSlots = inventory.Slots;
        }

        if (invSlots == null || selectedSlotIndex < 0 || selectedSlotIndex >= invSlots.Length)
            return false;

        Slot selectedSlot = invSlots[selectedSlotIndex];
        if (selectedSlot == null || selectedSlot.IsEmpty || selectedSlot.item == null)
            return false;

        selectedItem = selectedSlot.item;
        return true;
    }

    public bool TryGetSelectedBlockType(out BlockType blockType)
    {
        blockType = BlockType.Air;
        if (inventory == null)
            return false;

        if (!TryGetSelectedItem(out Item selectedItem))
            return false;

        return inventory.TryGetBlockForItem(selectedItem, out blockType);
    }

    public bool TryConsumeSelected(int amount = 1)
    {
        if (inventory == null) return false;
        return inventory.TryConsumeFromSlot(selectedSlotIndex, amount);
    }

    private void HandleSelectionInput()
    {
        if (enableNumberKeySelection)
        {
            int max = Mathf.Min(9, Mathf.Max(1, hotbarSize));
            for (int i = 0; i < max; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                {
                    SetSelectedSlotIndex(i);
                    return;
                }
            }
        }

        if (enableMouseWheelSelection)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (scroll > 0.01f)
                SetSelectedSlotIndex(selectedSlotIndex - 1);
            else if (scroll < -0.01f)
                SetSelectedSlotIndex(selectedSlotIndex + 1);
        }
    }

    private void RefreshSelectionVisuals()
    {
        if (hotbarSlots == null) return;

        for (int i = 0; i < hotbarSlots.Length; i++)
        {
            Slot hotbarSlot = hotbarSlots[i];
            if (hotbarSlot == null) continue;

            Transform selectedTransform = FindChildByName(hotbarSlot.transform, SelectedChildName);
            if (selectedTransform != null && selectedTransform.gameObject.activeSelf != (i == selectedSlotIndex))
                selectedTransform.gameObject.SetActive(i == selectedSlotIndex);
        }
    }

    private void CollectSlotsRecursive(Transform current, List<Slot> output)
    {
        if (current == null) return;

        if (!includeInactiveSlots && !current.gameObject.activeInHierarchy)
            return;

        Slot slot = current.GetComponent<Slot>();
        if (slot != null)
            output.Add(slot);

        for (int i = 0; i < current.childCount; i++)
            CollectSlotsRecursive(current.GetChild(i), output);
    }

    private static Transform FindChildByName(Transform parent, string childName)
    {
        if (parent == null) return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildByName(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
