using UnityEngine;

[RequireComponent(typeof(Slot))]
public class ArmorSlot : MonoBehaviour
{
    [Header("Armor Slot")]
    [SerializeField] private ArmorType acceptedArmorType = ArmorType.Helmet;
    [SerializeField] private bool usableOutsideInventorySlotMapping = true;
    [SerializeField] private bool allowQuickMoveToInventory = true;

    [Header("Player Visual")]
    [SerializeField] private GameObject armorVisual;
    [SerializeField] private bool controlArmorVisual = true;

    private Slot slot;

    public Slot SlotComponent
    {
        get
        {
            ResolveSlot();
            return slot;
        }
    }

    public ArmorType AcceptedArmorType => acceptedArmorType;

    public bool HasEquippedArmor
    {
        get
        {
            ResolveSlot();
            return slot != null && !slot.IsEmpty && slot.item != null && slot.item.IsArmorType(acceptedArmorType);
        }
    }

    private void Awake()
    {
        ResolveSlot();
        ConfigureSlot();
        RefreshVisual();
    }

    private void OnEnable()
    {
        ResolveSlot();
        ConfigureSlot();
        SubscribeSlot();
        RefreshVisual();
    }

    private void OnDisable()
    {
        UnsubscribeSlot();
    }

    private void OnValidate()
    {
        ResolveSlot();
        ConfigureSlot();
        RefreshVisual();
    }

    public bool CanAcceptItem(Item incomingItem, int incomingAmount = 1)
    {
        return incomingAmount == 1 &&
               incomingItem != null &&
               incomingItem.IsArmorType(acceptedArmorType);
    }

    public void RefreshVisual()
    {
        if (!controlArmorVisual || armorVisual == null)
            return;

        armorVisual.SetActive(HasEquippedArmor);
    }

    private void ResolveSlot()
    {
        if (slot == null)
            slot = GetComponent<Slot>();
    }

    private void ConfigureSlot()
    {
        if (slot == null)
            return;

        if (usableOutsideInventorySlotMapping)
            slot.SetRequireInventorySlotMapping(false);
    }

    private void SubscribeSlot()
    {
        if (slot == null)
            return;

        UnsubscribeSlot();
        slot.BeforePlaceIntoSlot += HandleBeforePlaceIntoSlot;
        slot.SlotChanged += HandleSlotChanged;
        slot.QuickTransferRequested += HandleQuickTransferRequested;
    }

    private void UnsubscribeSlot()
    {
        if (slot == null)
            return;

        slot.BeforePlaceIntoSlot -= HandleBeforePlaceIntoSlot;
        slot.SlotChanged -= HandleSlotChanged;
        slot.QuickTransferRequested -= HandleQuickTransferRequested;
    }

    private bool HandleBeforePlaceIntoSlot(Slot targetSlot, Item incomingItem, int incomingAmount)
    {
        return targetSlot == slot && CanAcceptItem(incomingItem, incomingAmount);
    }

    private void HandleSlotChanged(Slot changedSlot)
    {
        if (changedSlot == slot)
            RefreshVisual();
    }

    private bool HandleQuickTransferRequested(Slot sourceSlot)
    {
        if (!allowQuickMoveToInventory || sourceSlot != slot || slot == null || slot.IsEmpty)
            return false;

        PlayerInventory inventory = PlayerInventory.Instance;
        return inventory != null && inventory.TryMoveSlotToInventory(slot);
    }
}
