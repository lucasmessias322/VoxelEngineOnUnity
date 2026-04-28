using System.Collections.Generic;
using UnityEngine;

public class PlayerArmorQuickEquip : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private ArmorSlot[] armorSlots;
    [SerializeField] private bool autoFindArmorSlots = true;

    private readonly List<Slot> subscribedInventorySlots = new List<Slot>();
    private PlayerInventory subscribedInventory;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        SyncInventorySlotSubscriptions();
    }

    private void Start()
    {
        ResolveReferences();
        SyncInventorySlotSubscriptions();
    }

    private void Update()
    {
        ResolveReferences();
        SyncInventorySlotSubscriptions();
    }

    private void OnDisable()
    {
        UnsubscribeInventorySlotQuickTransfers();
    }

    [ContextMenu("Find Armor Slots")]
    public void FindArmorSlots()
    {
        armorSlots = FindObjectsOfType<ArmorSlot>(true);
    }

    private void ResolveReferences()
    {
        if (inventory == null)
            inventory = PlayerInventory.Instance != null ? PlayerInventory.Instance : FindAnyObjectByType<PlayerInventory>();

        if (autoFindArmorSlots && (armorSlots == null || armorSlots.Length == 0))
            FindArmorSlots();
    }

    private void SyncInventorySlotSubscriptions()
    {
        if (inventory == subscribedInventory && HasSameSubscribedSlots(inventory != null ? inventory.Slots : null))
            return;

        UnsubscribeInventorySlotQuickTransfers();

        subscribedInventory = inventory;
        if (subscribedInventory == null || subscribedInventory.Slots == null)
            return;

        Slot[] slots = subscribedInventory.Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot == null)
                continue;

            slot.QuickTransferRequested += HandleInventoryQuickTransferRequested;
            subscribedInventorySlots.Add(slot);
        }
    }

    private void UnsubscribeInventorySlotQuickTransfers()
    {
        for (int i = 0; i < subscribedInventorySlots.Count; i++)
        {
            Slot slot = subscribedInventorySlots[i];
            if (slot == null)
                continue;

            slot.QuickTransferRequested -= HandleInventoryQuickTransferRequested;
        }

        subscribedInventorySlots.Clear();
        subscribedInventory = null;
    }

    private bool HasSameSubscribedSlots(Slot[] currentSlots)
    {
        if (currentSlots == null)
            return subscribedInventorySlots.Count == 0;

        if (subscribedInventorySlots.Count != currentSlots.Length)
            return false;

        for (int i = 0; i < currentSlots.Length; i++)
        {
            if (subscribedInventorySlots[i] != currentSlots[i])
                return false;
        }

        return true;
    }

    private bool HandleInventoryQuickTransferRequested(Slot sourceSlot)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty || sourceSlot.item == null || !sourceSlot.item.IsArmor)
            return false;

        ArmorSlot armorSlot = FindArmorSlot(sourceSlot.item.armorType);
        if (armorSlot == null)
            return false;

        Slot targetSlot = armorSlot.SlotComponent;
        if (targetSlot == null || targetSlot == sourceSlot || !targetSlot.IsEmpty)
            return false;

        Item armorItem = sourceSlot.item;
        if (!armorSlot.CanAcceptItem(armorItem, 1) || !targetSlot.CanAccept(armorItem, 1))
            return false;

        int removed = sourceSlot.Remove(1);
        if (removed != 1)
            return false;

        targetSlot.SetContents(armorItem, 1);
        return true;
    }

    private ArmorSlot FindArmorSlot(ArmorType armorType)
    {
        if (armorSlots == null)
            return null;

        for (int i = 0; i < armorSlots.Length; i++)
        {
            ArmorSlot armorSlot = armorSlots[i];
            if (armorSlot != null && armorSlot.AcceptedArmorType == armorType)
                return armorSlot;
        }

        return null;
    }
}
