using UnityEngine;
using UnityEngine.UI;

public sealed class StoneCrusherUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

    public static StoneCrusherUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;
    [SerializeField] private GameObject crusherPanel;
    [SerializeField] private Slot inputSlot;
    [SerializeField] private Slot outputSlot;
    [SerializeField] private Image crushProgressFillImage;
    [SerializeField] private Toggle dropOutputToggle;

    [Header("Interaction")]
    [SerializeField] private bool rightClickOpensCrusher = true;
    [SerializeField] private bool autoOpenInventoryWithCrusher = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenCrusherRemoved = true;
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;

    private StoneCrusher activeCrusher;
    private Vector3Int activeCrusherBlock = InvalidBlock;
    private bool suppressSlotCallbacks;
    private bool suppressToggleCallbacks;

    public bool IsCrusherOpen => activeCrusher != null && crusherPanel != null && crusherPanel.activeSelf;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        ResolveReferences();
        ConfigureSlots();
        SubscribeSlots();
        SubscribeToggle();
        RefreshPanelVisibility();
        RefreshUi();
    }

    private void OnDestroy()
    {
        UnsubscribeSlots();
        UnsubscribeToggle();

        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        ResolveReferences();

        if (IsCrusherOpen && ShouldCloseCrusher())
            CloseCrusherPanel();

        RefreshPanelVisibility();
        RefreshUi();
    }

    public bool TryHandleStoneCrusherInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensCrusher || !Input.GetMouseButtonDown(1))
            return false;

        BlockSelector selectorToUse = activeSelector != null ? activeSelector : selector;
        if (selectorToUse == null ||
            !selectorToUse.TryGetSelectedBlock(out Vector3Int crusherBlock, out _) ||
            !StoneCrusher.TryFindAtWorldBlock(crusherBlock, out StoneCrusher crusher))
        {
            return false;
        }

        OpenCrusher(crusher, crusherBlock);
        return true;
    }

    public void OpenCrusher(StoneCrusher crusher, Vector3Int crusherBlock)
    {
        if (crusher == null)
            return;

        ResolveReferences();
        activeCrusher = crusher;
        activeCrusherBlock = crusherBlock;

        if (ChestUIController.Instance != null)
            ChestUIController.Instance.CloseChestPanel();

        if (FurnaceUIController.Instance != null)
            FurnaceUIController.Instance.CloseFurnacePanel();

        if (SplitterUIController.Instance != null)
            SplitterUIController.Instance.CloseSplitterPanel();

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.CloseCrafterUI(returnItemsToInventory: true);

        if (autoOpenInventoryWithCrusher && inventory != null && !inventory.IsInventoryOpen)
            inventory.SetInventoryOpen(true);

        if (crusherPanel != null)
            crusherPanel.SetActive(true);

        RefreshUi();
    }

    public void CloseCrusherPanel()
    {
        activeCrusher = null;
        activeCrusherBlock = InvalidBlock;

        if (crusherPanel != null)
            crusherPanel.SetActive(false);

        RefreshUi();
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        if (!inventoryOpen && closeWhenInventoryCloses && IsCrusherOpen)
            CloseCrusherPanel();
        else
            RefreshPanelVisibility();
    }

    private void ConfigureSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SetRequireInventorySlotMapping(false);
            inputSlot.SetManualInteraction(canPickup: true, canInsert: true);
        }

        if (outputSlot != null)
        {
            outputSlot.SetRequireInventorySlotMapping(false);
            outputSlot.SetManualInteraction(canPickup: true, canInsert: false);
        }
    }

    private void SubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged += HandleSlotChanged;
            inputSlot.BeforePlaceIntoSlot += HandleInputBeforePlace;
        }

        if (outputSlot != null)
        {
            outputSlot.SlotChanged += HandleSlotChanged;
            outputSlot.BeforePlaceIntoSlot += HandleOutputBeforePlace;
            outputSlot.BeforeTakeFromSlot += HandleOutputBeforeTake;
            outputSlot.QuickTransferRequested += HandleOutputQuickTransfer;
        }
    }

    private void UnsubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged -= HandleSlotChanged;
            inputSlot.BeforePlaceIntoSlot -= HandleInputBeforePlace;
        }

        if (outputSlot != null)
        {
            outputSlot.SlotChanged -= HandleSlotChanged;
            outputSlot.BeforePlaceIntoSlot -= HandleOutputBeforePlace;
            outputSlot.BeforeTakeFromSlot -= HandleOutputBeforeTake;
            outputSlot.QuickTransferRequested -= HandleOutputQuickTransfer;
        }
    }

    private void SubscribeToggle()
    {
        if (dropOutputToggle != null)
            dropOutputToggle.onValueChanged.AddListener(HandleDropOutputToggleChanged);
    }

    private void UnsubscribeToggle()
    {
        if (dropOutputToggle != null)
            dropOutputToggle.onValueChanged.RemoveListener(HandleDropOutputToggleChanged);
    }

    private void HandleDropOutputToggleChanged(bool enabled)
    {
        if (suppressToggleCallbacks || activeCrusher == null)
            return;

        activeCrusher.SetDropOutputToWorld(enabled);
        RefreshUi();
    }

    private bool HandleInputBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        if (activeCrusher == null || incomingItem == null || incomingAmount <= 0)
            return false;

        return activeCrusher.IsValidInputItem(incomingItem) &&
               incomingAmount <= activeCrusher.GetInputBufferFreeSpace();
    }

    private bool HandleOutputBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        return false;
    }

    private bool HandleOutputBeforeTake(Slot slot, Item currentItem, int currentAmount, int amountToTake)
    {
        return activeCrusher != null &&
               currentItem != null &&
               amountToTake > 0 &&
               amountToTake <= activeCrusher.OutputBufferAmount;
    }

    private bool HandleOutputQuickTransfer(Slot slot)
    {
        if (activeCrusher == null || inventory == null || slot == null || slot.IsEmpty)
            return false;

        Item item = slot.item;
        int amount = Mathf.Min(slot.amount, activeCrusher.OutputBufferAmount);
        if (item == null || amount <= 0)
            return false;

        int remaining = inventory.InsertItem(item, amount);
        int moved = amount - remaining;
        if (moved <= 0)
            return false;

        activeCrusher.RemoveOutputBuffer(moved);
        RefreshUi();
        return true;
    }

    private void HandleSlotChanged(Slot slot)
    {
        if (suppressSlotCallbacks || activeCrusher == null || slot == null)
            return;

        if (slot == inputSlot)
        {
            SyncInputSlotMutation(slot);
            return;
        }

        if (slot == outputSlot)
            SyncOutputSlotMutation(slot);
    }

    private void SyncInputSlotMutation(Slot slot)
    {
        Item inputItem = activeCrusher.InputItem;
        if (inputItem == null)
        {
            RefreshUi();
            return;
        }

        if (!slot.IsEmpty && slot.item != inputItem)
        {
            RefreshUi();
            return;
        }

        int displayedAmount = slot.IsEmpty ? 0 : slot.amount;
        int realAmount = activeCrusher.InputBufferAmount;
        if (displayedAmount > realAmount)
            activeCrusher.TryInsertInputBuffer(displayedAmount - realAmount);
        else if (displayedAmount < realAmount)
            activeCrusher.RemoveInputBuffer(realAmount - displayedAmount);

        RefreshUi();
    }

    private void SyncOutputSlotMutation(Slot slot)
    {
        int displayedAmount = slot.IsEmpty ? 0 : slot.amount;
        int realAmount = activeCrusher.OutputBufferAmount;
        if (displayedAmount < realAmount)
            activeCrusher.RemoveOutputBuffer(realAmount - displayedAmount);

        RefreshUi();
    }

    private void RefreshUi()
    {
        suppressSlotCallbacks = true;

        if (activeCrusher != null)
        {
            SetSlotItem(inputSlot, activeCrusher.InputItem, activeCrusher.InputBufferAmount);
            SetSlotItem(outputSlot, activeCrusher.OutputItem, activeCrusher.OutputBufferAmount);
        }
        else
        {
            ClearSlot(inputSlot);
            ClearSlot(outputSlot);
        }

        suppressSlotCallbacks = false;

        if (crushProgressFillImage != null)
            crushProgressFillImage.fillAmount = activeCrusher != null ? activeCrusher.CrushProgress01 : 0f;

        suppressToggleCallbacks = true;
        if (dropOutputToggle != null)
            dropOutputToggle.SetIsOnWithoutNotify(activeCrusher != null && activeCrusher.DropOutputToWorld);
        suppressToggleCallbacks = false;
    }

    private static void SetSlotItem(Slot slot, Item item, int amount)
    {
        if (slot == null)
            return;

        if (amount <= 0 || item == null)
        {
            slot.SetContents(null, 0);
            return;
        }

        slot.SetContents(item, amount);
    }

    private static void ClearSlot(Slot slot)
    {
        if (slot != null)
            slot.SetContents(null, 0);
    }

    private bool ShouldCloseCrusher()
    {
        if (activeCrusher == null)
            return true;

        if (closeWhenCrusherRemoved &&
            (World.Instance == null || !StoneCrusher.TryFindAtWorldBlock(activeCrusherBlock, out StoneCrusher crusher) || crusher != activeCrusher))
        {
            return true;
        }

        if (!closeWhenTooFar)
            return false;

        Transform origin = distanceOrigin != null ? distanceOrigin : Camera.main != null ? Camera.main.transform : null;
        if (origin == null)
            return false;

        float maxDistanceSqr = maxInteractDistance * maxInteractDistance;
        Vector3 crusherCenter = activeCrusher.transform.position;
        return (origin.position - crusherCenter).sqrMagnitude > maxDistanceSqr;
    }

    private void RefreshPanelVisibility()
    {
        if (crusherPanel != null)
            crusherPanel.SetActive(activeCrusher != null);
    }

    private void ResolveReferences()
    {
        if (inventory == null)
            inventory = PlayerInventory.Instance != null ? PlayerInventory.Instance : FindAnyObjectByType<PlayerInventory>();

        if (selector == null)
            selector = FindAnyObjectByType<BlockSelector>();

        if (distanceOrigin == null && Camera.main != null)
            distanceOrigin = Camera.main.transform;
    }
}
