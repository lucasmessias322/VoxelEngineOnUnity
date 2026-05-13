using System.Collections.Generic;
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
    [SerializeField] private Slot[] outputSlots;
    [SerializeField] private Image crushProgressFillImage;
    [SerializeField] private Image energyFillImage;
    [SerializeField] private Toggle dropOutputToggle;

    [Header("Magnetic Separator UI")]
    [SerializeField, Min(1)] private int magneticSeparatorOutputSlotCount = 3;
    [SerializeField] private bool createMissingMagneticSeparatorOutputSlots = true;
    [SerializeField, Min(0f)] private float generatedOutputSlotSpacing = 56f;

    [Header("Energy UI")]
    [SerializeField, Range(0f, 1f)] private float criticalEnergyThreshold = 0.25f;
    [SerializeField, Range(0f, 1f)] private float mediumEnergyThreshold = 0.6f;
    [SerializeField] private Color criticalEnergyColor = new Color(0.9f, 0.12f, 0.08f, 1f);
    [SerializeField] private Color mediumEnergyColor = new Color(1f, 0.78f, 0.12f, 1f);
    [SerializeField] private Color safeEnergyColor = new Color(0.18f, 0.78f, 0.24f, 1f);
    [SerializeField] private Color emptyEnergyColor = new Color(0.35f, 0.35f, 0.35f, 1f);

    [Header("Interaction")]
    [SerializeField] private bool rightClickOpensCrusher = true;
    [SerializeField] private bool autoOpenInventoryWithCrusher = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenCrusherRemoved = true;
    [Min(0.5f)][SerializeField] private float maxInteractDistance = 4f;

    private StoneCrusher activeCrusher;
    private Vector3Int activeCrusherBlock = InvalidBlock;
    private bool suppressSlotCallbacks;
    private bool suppressToggleCallbacks;
    private readonly List<Slot> resolvedOutputSlots = new List<Slot>(3);

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
        ResolveOutputSlots();
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

        if (SteamEngineUIController.Instance != null)
            SteamEngineUIController.Instance.CloseSteamEnginePanel();

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

        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            Slot slot = resolvedOutputSlots[i];
            if (slot == null)
                continue;

            slot.SetRequireInventorySlotMapping(false);
            slot.SetManualInteraction(canPickup: true, canInsert: false);
            slot.gameObject.SetActive(i == 0);
        }
    }

    private void SubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged += HandleSlotChanged;
            inputSlot.BeforePlaceIntoSlot += HandleInputBeforePlace;
        }

        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            Slot slot = resolvedOutputSlots[i];
            if (slot == null)
                continue;

            slot.SlotChanged += HandleSlotChanged;
            slot.BeforePlaceIntoSlot += HandleOutputBeforePlace;
            slot.BeforeTakeFromSlot += HandleOutputBeforeTake;
            slot.QuickTransferRequested += HandleOutputQuickTransfer;
        }
    }

    private void UnsubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged -= HandleSlotChanged;
            inputSlot.BeforePlaceIntoSlot -= HandleInputBeforePlace;
        }

        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            Slot slot = resolvedOutputSlots[i];
            if (slot == null)
                continue;

            slot.SlotChanged -= HandleSlotChanged;
            slot.BeforePlaceIntoSlot -= HandleOutputBeforePlace;
            slot.BeforeTakeFromSlot -= HandleOutputBeforeTake;
            slot.QuickTransferRequested -= HandleOutputQuickTransfer;
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
               amountToTake <= GetDisplayedOutputAmount(slot);
    }

    private bool HandleOutputQuickTransfer(Slot slot)
    {
        if (activeCrusher == null || inventory == null || slot == null || slot.IsEmpty)
            return false;

        Item item = slot.item;
        int amount = Mathf.Min(slot.amount, GetDisplayedOutputAmount(slot));
        if (item == null || amount <= 0)
            return false;

        int remaining = inventory.InsertItem(item, amount);
        int moved = amount - remaining;
        if (moved <= 0)
            return false;

        activeCrusher.RemoveOutputBuffer(GetOutputSlotIndex(slot), moved);
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

        if (IsOutputSlot(slot))
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
        int realAmount = GetDisplayedOutputAmount(slot);
        if (displayedAmount < realAmount)
            activeCrusher.RemoveOutputBuffer(GetOutputSlotIndex(slot), realAmount - displayedAmount);

        RefreshUi();
    }

    private void RefreshUi()
    {
        RefreshOutputSlotVisibility();
        suppressSlotCallbacks = true;

        if (activeCrusher != null)
        {
            SetSlotItem(inputSlot, activeCrusher.InputItem, activeCrusher.InputBufferAmount);
            RefreshOutputSlots();
        }
        else
        {
            ClearSlot(inputSlot);
            ClearOutputSlots();
        }

        suppressSlotCallbacks = false;

        if (crushProgressFillImage != null)
            crushProgressFillImage.fillAmount = activeCrusher != null ? activeCrusher.CrushProgress01 : 0f;

        RefreshEnergyUi();

        suppressToggleCallbacks = true;
        if (dropOutputToggle != null)
            dropOutputToggle.SetIsOnWithoutNotify(activeCrusher != null && activeCrusher.DropOutputToWorld);
        suppressToggleCallbacks = false;
    }

    private void RefreshEnergyUi()
    {
        if (energyFillImage == null)
            return;

        float energyLevel = activeCrusher != null ? activeCrusher.EnergyCharge01 : 0f;
        energyFillImage.fillAmount = energyLevel;
        energyFillImage.color = ResolveEnergyColor(energyLevel);
    }

    private Color ResolveEnergyColor(float energyLevel)
    {
        if (activeCrusher == null || energyLevel <= 0.0001f)
            return emptyEnergyColor;

        if (energyLevel <= criticalEnergyThreshold)
            return criticalEnergyColor;

        if (energyLevel <= mediumEnergyThreshold)
            return mediumEnergyColor;

        return safeEnergyColor;
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

    private void RefreshOutputSlots()
    {
        int activeSlotCount = GetActiveOutputSlotCount();
        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            Slot slot = resolvedOutputSlots[i];
            if (i < activeSlotCount)
                SetSlotItem(slot, activeCrusher.GetOutputItem(i), activeCrusher.GetOutputBufferAmount(i));
            else
                ClearSlot(slot);
        }
    }

    private void ClearOutputSlots()
    {
        for (int i = 0; i < resolvedOutputSlots.Count; i++)
            ClearSlot(resolvedOutputSlots[i]);
    }

    private void RefreshOutputSlotVisibility()
    {
        int activeSlotCount = GetActiveOutputSlotCount();
        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            Slot slot = resolvedOutputSlots[i];
            if (slot != null)
                slot.gameObject.SetActive(i < activeSlotCount);
        }
    }

    private int GetActiveOutputSlotCount()
    {
        if (activeCrusher == null)
            return resolvedOutputSlots.Count > 0 ? 1 : 0;

        int requestedCount = Mathf.Max(1, activeCrusher.OutputSlotCount);
        return Mathf.Clamp(requestedCount, 0, resolvedOutputSlots.Count);
    }

    private int GetDisplayedOutputAmount(Slot slot)
    {
        int slotIndex = GetOutputSlotIndex(slot);
        if (slotIndex < 0)
            return 0;

        return GetDisplayedOutputAmount(slotIndex, GetActiveOutputSlotCount());
    }

    private int GetDisplayedOutputAmount(int slotIndex, int activeSlotCount)
    {
        if (activeCrusher == null || slotIndex < 0 || slotIndex >= activeSlotCount)
            return 0;

        return activeCrusher.GetOutputBufferAmount(slotIndex);
    }

    private bool IsOutputSlot(Slot slot)
    {
        return GetOutputSlotIndex(slot) >= 0;
    }

    private int GetOutputSlotIndex(Slot slot)
    {
        if (slot == null)
            return -1;

        for (int i = 0; i < resolvedOutputSlots.Count; i++)
        {
            if (resolvedOutputSlots[i] == slot)
                return i;
        }

        return -1;
    }

    private void ResolveOutputSlots()
    {
        resolvedOutputSlots.Clear();
        AddOutputSlot(outputSlot);

        if (outputSlots != null)
        {
            for (int i = 0; i < outputSlots.Length; i++)
                AddOutputSlot(outputSlots[i]);
        }

        if (!createMissingMagneticSeparatorOutputSlots || outputSlot == null)
            return;

        int requiredCount = Mathf.Max(1, magneticSeparatorOutputSlotCount);
        while (resolvedOutputSlots.Count < requiredCount)
        {
            Slot generatedSlot = CreateGeneratedOutputSlot(resolvedOutputSlots.Count);
            if (generatedSlot == null)
                break;

            AddOutputSlot(generatedSlot);
        }
    }

    private void AddOutputSlot(Slot slot)
    {
        if (slot == null || resolvedOutputSlots.Contains(slot))
            return;

        resolvedOutputSlots.Add(slot);
    }

    private Slot CreateGeneratedOutputSlot(int slotIndex)
    {
        if (outputSlot == null || outputSlot.transform == null || outputSlot.transform.parent == null)
            return null;

        GameObject slotObject = Instantiate(outputSlot.gameObject, outputSlot.transform.parent);
        slotObject.name = $"Output Slot ({slotIndex + 1})";
        slotObject.SetActive(false);

        RectTransform sourceRect = outputSlot.transform as RectTransform;
        RectTransform generatedRect = slotObject.transform as RectTransform;
        LayoutGroup parentLayout = outputSlot.transform.parent.GetComponent<LayoutGroup>();
        if (sourceRect != null && generatedRect != null && parentLayout == null)
        {
            generatedRect.anchorMin = sourceRect.anchorMin;
            generatedRect.anchorMax = sourceRect.anchorMax;
            generatedRect.pivot = sourceRect.pivot;
            generatedRect.sizeDelta = sourceRect.sizeDelta;
            generatedRect.anchoredPosition = sourceRect.anchoredPosition + new Vector2(generatedOutputSlotSpacing * slotIndex, 0f);
        }

        Slot generatedSlot = slotObject.GetComponent<Slot>();
        if (generatedSlot != null)
            generatedSlot.SetContents(null, 0);

        return generatedSlot;
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
