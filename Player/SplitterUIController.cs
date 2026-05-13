using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SplitterUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private const string RuntimeControllerName = "__RuntimeSplitterUIController";
    private const string IconChildName = "IconImage";
    private const string AmountChildName = "amount";

    public static SplitterUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;

    [Header("UI")]
    [SerializeField] private GameObject splitterPanel;
    [SerializeField] private Slot filterSlot;
    [SerializeField] private Toggle leftToggle;
    [SerializeField] private Toggle rightToggle;
    [SerializeField] private Toggle frontToggle;
    [SerializeField] private Toggle backToggle;
    [SerializeField] private bool createRuntimePanelWhenMissing = true;

    [Header("Interaction")]
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;
    [SerializeField] private bool rightClickOpensSplitter = true;
    [SerializeField] private bool autoOpenInventoryWithSplitter = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenSplitterRemoved = true;
    [SerializeField] private bool debugLogs;

    private Vector3Int activeSplitterBlock = InvalidBlock;
    private bool suppressUiCallbacks;
    private bool previousInventoryOpen;
    private PlayerInventory subscribedInventory;

    public bool IsSplitterOpen => activeSplitterBlock != InvalidBlock;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapRuntimeInstance()
    {
        EnsureInstance();
    }

    public static SplitterUIController EnsureInstance()
    {
        if (Instance != null)
            return Instance;

        SplitterUIController existing = FindAnyObjectByType<SplitterUIController>();
        if (existing != null)
            return existing;

        GameObject controllerObject = new GameObject(RuntimeControllerName);
        return controllerObject.AddComponent<SplitterUIController>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        ResolveReferences();
        EnsurePanel();
        ConfigureUi();
        SubscribeUi();
        previousInventoryOpen = inventory != null && inventory.IsInventoryOpen;
        RefreshPanelVisibility();
    }

    private void Update()
    {
        ResolveReferences();
        EnsurePanel();
        SyncInventorySlotSubscriptions();

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        if (previousInventoryOpen != inventoryOpen)
        {
            previousInventoryOpen = inventoryOpen;
            HandleInventoryVisibilityChanged(inventoryOpen);
        }

        if (IsSplitterOpen && ShouldCloseSplitter())
            CloseSplitterPanel();

        if (IsSplitterOpen)
            RefreshValidToggles();

        RefreshPanelVisibility();
    }

    private void OnDestroy()
    {
        UnsubscribeUi();
        UnsubscribeInventorySlotQuickTransfers();

        if (Instance == this)
            Instance = null;

        ApplyCraftingUiBlock(false);
    }

    public bool TryHandleSplitterInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensSplitter || !Input.GetMouseButtonDown(1))
            return false;

        if (!TryGetTargetSplitter(activeSelector != null ? activeSelector : selector, out Vector3Int splitterBlock))
            return false;

        OpenSplitter(splitterBlock);
        return true;
    }

    public void OpenSplitter(Vector3Int splitterBlock)
    {
        ResolveReferences();
        EnsurePanel();
        ConfigureUi();

        if (filterSlot == null)
        {
            Log("Nao foi possivel abrir o splitter: slot de filtro nao encontrado.");
            return;
        }

        activeSplitterBlock = splitterBlock;
        LoadFilterIntoSlot();
        LoadTogglesFromState();
        RefreshValidToggles();

        if (ChestUIController.Instance != null)
            ChestUIController.Instance.CloseChestPanel();

        if (FurnaceUIController.Instance != null)
            FurnaceUIController.Instance.CloseFurnacePanel();

        if (SteamEngineUIController.Instance != null)
            SteamEngineUIController.Instance.CloseSteamEnginePanel();

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.CloseCrafterUI(returnItemsToInventory: true);

        ApplyCraftingUiBlock(true);

        if (autoOpenInventoryWithSplitter && inventory != null && !inventory.IsInventoryOpen)
            inventory.SetInventoryOpen(true);

        SyncInventorySlotSubscriptions();
        RefreshPanelVisibility();
        Log($"Splitter aberto em {splitterBlock}.");
    }

    public void CloseSplitterPanel()
    {
        if (!IsSplitterOpen)
        {
            RefreshPanelVisibility();
            return;
        }

        SaveFilterFromSlot();
        activeSplitterBlock = InvalidBlock;
        UnsubscribeInventorySlotQuickTransfers();
        ApplyCraftingUiBlock(false);
        RefreshPanelVisibility();
        Log("Splitter fechado.");
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        if (!inventoryOpen && closeWhenInventoryCloses && IsSplitterOpen)
            CloseSplitterPanel();
        else
            RefreshPanelVisibility();
    }

    public void SetFrontFilter(bool enabled)
    {
        SetFaceFilter(ConveyorBeltUtility.SplitterFilterFace.Front, enabled);
    }

    public void SetBackFilter(bool enabled)
    {
        SetFaceFilter(ConveyorBeltUtility.SplitterFilterFace.Back, enabled);
    }

    public void SetLeftFilter(bool enabled)
    {
        SetFaceFilter(ConveyorBeltUtility.SplitterFilterFace.Left, enabled);
    }

    public void SetRightFilter(bool enabled)
    {
        SetFaceFilter(ConveyorBeltUtility.SplitterFilterFace.Right, enabled);
    }

    private void ResolveReferences()
    {
        if (inventory == null)
            inventory = PlayerInventory.Instance != null ? PlayerInventory.Instance : FindAnyObjectByType<PlayerInventory>();

        if (selector == null)
            selector = FindAnyObjectByType<BlockSelector>();

        if (distanceOrigin == null)
        {
            if (selector != null && selector.cam != null)
                distanceOrigin = selector.cam.transform;
            else if (inventory != null)
                distanceOrigin = inventory.transform;
            else
                distanceOrigin = transform;
        }
    }

    private void EnsurePanel()
    {
        if (splitterPanel == null && createRuntimePanelWhenMissing)
            CreateRuntimePanel();

        if (splitterPanel == null)
            return;

        if (filterSlot == null)
            filterSlot = splitterPanel.GetComponentInChildren<Slot>(true);

        frontToggle = frontToggle != null ? frontToggle : FindToggleByName("front");
        backToggle = backToggle != null ? backToggle : FindToggleByName("back");
        leftToggle = leftToggle != null ? leftToggle : FindToggleByName("left");
        rightToggle = rightToggle != null ? rightToggle : FindToggleByName("right");
    }

    private void CreateRuntimePanel()
    {
        Canvas canvas = ResolveCanvas();
        if (canvas == null)
            return;

        GameObject panel = new GameObject("SplitterPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.SetParent(canvas.transform, false);
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = new Vector2(360f, 0f);
        panelRect.sizeDelta = new Vector2(230f, 300f);

        Image background = panel.GetComponent<Image>();
        background.color = new Color(0.12f, 0.13f, 0.13f, 0.96f);

        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(16, 16, 14, 14);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        CreateLabel("Splitter", panelRect, 20f, TextAlignmentOptions.Center);
        filterSlot = CreateFilterSlot(panelRect);
        CreateLabel("Filtro", panelRect, 14f, TextAlignmentOptions.Center);
        frontToggle = CreateToggle("Front", panelRect);
        backToggle = CreateToggle("Back", panelRect);
        leftToggle = CreateToggle("Left", panelRect);
        rightToggle = CreateToggle("Right", panelRect);

        splitterPanel = panel;
        splitterPanel.SetActive(false);
    }

    private Canvas ResolveCanvas()
    {
        if (inventory != null && inventory.SlotsContainer != null)
        {
            Canvas inventoryCanvas = inventory.SlotsContainer.GetComponentInParent<Canvas>(true);
            if (inventoryCanvas != null)
                return inventoryCanvas;
        }

        Canvas existing = FindAnyObjectByType<Canvas>();
        if (existing != null)
            return existing;

        GameObject canvasObject = new GameObject("RuntimeUICanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        return canvas;
    }

    private TMP_Text CreateLabel(string text, Transform parent, float size, TextAlignmentOptions alignment)
    {
        GameObject labelObject = new GameObject(text, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        RectTransform rect = labelObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);

        LayoutElement layout = labelObject.GetComponent<LayoutElement>();
        layout.preferredHeight = Mathf.Max(24f, size + 8f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.color = Color.white;
        label.alignment = alignment;
        label.raycastTarget = false;
        return label;
    }

    private Slot CreateFilterSlot(Transform parent)
    {
        GameObject slotObject = new GameObject("FilterSlot", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        RectTransform slotRect = slotObject.GetComponent<RectTransform>();
        slotRect.SetParent(parent, false);
        slotRect.sizeDelta = new Vector2(56f, 56f);

        LayoutElement layout = slotObject.GetComponent<LayoutElement>();
        layout.preferredWidth = 56f;
        layout.preferredHeight = 56f;
        layout.flexibleWidth = 0f;

        Image background = slotObject.GetComponent<Image>();
        background.color = new Color(0.2f, 0.21f, 0.2f, 1f);

        GameObject iconObject = new GameObject(IconChildName, typeof(RectTransform), typeof(Image));
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.SetParent(slotObject.transform, false);
        iconRect.anchorMin = Vector2.zero;
        iconRect.anchorMax = Vector2.one;
        iconRect.offsetMin = new Vector2(5f, 5f);
        iconRect.offsetMax = new Vector2(-5f, -5f);
        iconObject.GetComponent<Image>().raycastTarget = false;

        GameObject amountObject = new GameObject(AmountChildName, typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform amountRect = amountObject.GetComponent<RectTransform>();
        amountRect.SetParent(slotObject.transform, false);
        amountRect.anchorMin = Vector2.zero;
        amountRect.anchorMax = Vector2.one;
        amountRect.offsetMin = new Vector2(2f, 1f);
        amountRect.offsetMax = new Vector2(-3f, -2f);

        TextMeshProUGUI amountText = amountObject.GetComponent<TextMeshProUGUI>();
        amountText.alignment = TextAlignmentOptions.BottomRight;
        amountText.fontSize = 18f;
        amountText.color = Color.white;
        amountText.raycastTarget = false;

        return slotObject.AddComponent<Slot>();
    }

    private Toggle CreateToggle(string label, Transform parent)
    {
        GameObject toggleObject = new GameObject($"{label}Toggle", typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
        RectTransform toggleRect = toggleObject.GetComponent<RectTransform>();
        toggleRect.SetParent(parent, false);

        LayoutElement layout = toggleObject.GetComponent<LayoutElement>();
        layout.preferredHeight = 30f;

        GameObject boxObject = new GameObject("CheckmarkBox", typeof(RectTransform), typeof(Image));
        RectTransform boxRect = boxObject.GetComponent<RectTransform>();
        boxRect.SetParent(toggleObject.transform, false);
        boxRect.anchorMin = new Vector2(0f, 0.5f);
        boxRect.anchorMax = new Vector2(0f, 0.5f);
        boxRect.pivot = new Vector2(0f, 0.5f);
        boxRect.anchoredPosition = Vector2.zero;
        boxRect.sizeDelta = new Vector2(22f, 22f);

        Image boxImage = boxObject.GetComponent<Image>();
        boxImage.color = new Color(0.23f, 0.24f, 0.24f, 1f);

        GameObject checkObject = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
        RectTransform checkRect = checkObject.GetComponent<RectTransform>();
        checkRect.SetParent(boxRect, false);
        checkRect.anchorMin = new Vector2(0.5f, 0.5f);
        checkRect.anchorMax = new Vector2(0.5f, 0.5f);
        checkRect.pivot = new Vector2(0.5f, 0.5f);
        checkRect.sizeDelta = new Vector2(12f, 12f);

        Image checkImage = checkObject.GetComponent<Image>();
        checkImage.color = new Color(0.1f, 0.75f, 0.55f, 1f);

        TMP_Text labelText = CreateLabel(label, toggleObject.transform, 16f, TextAlignmentOptions.Left);
        RectTransform labelRect = labelText.transform as RectTransform;
        if (labelRect != null)
        {
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(32f, 0f);
            labelRect.offsetMax = Vector2.zero;
        }

        Toggle toggle = toggleObject.GetComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        toggle.isOn = false;
        return toggle;
    }

    private void ConfigureUi()
    {
        if (filterSlot != null)
        {
            filterSlot.SetRequireInventorySlotMapping(false);
            filterSlot.SetManualInteraction(true, true, true);
            filterSlot.RefreshUI();
        }
    }

    private void SubscribeUi()
    {
        UnsubscribeUi();

        if (filterSlot != null)
        {
            filterSlot.SlotChanged += HandleFilterSlotChanged;
            filterSlot.QuickTransferRequested += HandleFilterQuickTransfer;
            filterSlot.BeforePlaceIntoSlot += HandleFilterBeforePlace;
        }

        SubscribeToggle(frontToggle, SetFrontFilter);
        SubscribeToggle(backToggle, SetBackFilter);
        SubscribeToggle(leftToggle, SetLeftFilter);
        SubscribeToggle(rightToggle, SetRightFilter);
    }

    private void UnsubscribeUi()
    {
        if (filterSlot != null)
        {
            filterSlot.SlotChanged -= HandleFilterSlotChanged;
            filterSlot.QuickTransferRequested -= HandleFilterQuickTransfer;
            filterSlot.BeforePlaceIntoSlot -= HandleFilterBeforePlace;
        }

        UnsubscribeToggle(frontToggle, SetFrontFilter);
        UnsubscribeToggle(backToggle, SetBackFilter);
        UnsubscribeToggle(leftToggle, SetLeftFilter);
        UnsubscribeToggle(rightToggle, SetRightFilter);
    }

    private static void SubscribeToggle(Toggle toggle, UnityEngine.Events.UnityAction<bool> action)
    {
        if (toggle != null)
            toggle.onValueChanged.AddListener(action);
    }

    private static void UnsubscribeToggle(Toggle toggle, UnityEngine.Events.UnityAction<bool> action)
    {
        if (toggle != null)
            toggle.onValueChanged.RemoveListener(action);
    }

    private void LoadFilterIntoSlot()
    {
        if (filterSlot == null || !IsSplitterOpen)
            return;

        suppressUiCallbacks = true;
        try
        {
            Item item = ConveyorBeltUtility.GetSplitterFilterItem(activeSplitterBlock);
            if (item != null)
                filterSlot.SetContents(item, 1);
            else
                filterSlot.Clear();
        }
        finally
        {
            suppressUiCallbacks = false;
        }
    }

    private void SaveFilterFromSlot()
    {
        if (!IsSplitterOpen || filterSlot == null)
            return;

        Item filterItem = !filterSlot.IsEmpty ? filterSlot.item : null;
        ConveyorBeltUtility.SetSplitterFilterItem(activeSplitterBlock, filterItem);
    }

    private void LoadTogglesFromState()
    {
        if (!IsSplitterOpen)
            return;

        suppressUiCallbacks = true;
        try
        {
            SetToggleWithoutNotify(frontToggle, ConveyorBeltUtility.GetSplitterFilterFaceEnabled(activeSplitterBlock, ConveyorBeltUtility.SplitterFilterFace.Front));
            SetToggleWithoutNotify(backToggle, ConveyorBeltUtility.GetSplitterFilterFaceEnabled(activeSplitterBlock, ConveyorBeltUtility.SplitterFilterFace.Back));
            SetToggleWithoutNotify(leftToggle, ConveyorBeltUtility.GetSplitterFilterFaceEnabled(activeSplitterBlock, ConveyorBeltUtility.SplitterFilterFace.Left));
            SetToggleWithoutNotify(rightToggle, ConveyorBeltUtility.GetSplitterFilterFaceEnabled(activeSplitterBlock, ConveyorBeltUtility.SplitterFilterFace.Right));
        }
        finally
        {
            suppressUiCallbacks = false;
        }
    }

    private void RefreshValidToggles()
    {
        if (!IsSplitterOpen)
            return;

        ConveyorBeltUtility.SplitterFilterOutputInfo info =
            ConveyorBeltUtility.GetSplitterFilterOutputInfo(World.Instance, activeSplitterBlock);

        ApplyToggleValidity(frontToggle, ConveyorBeltUtility.SplitterFilterFace.Front, info.front);
        ApplyToggleValidity(backToggle, ConveyorBeltUtility.SplitterFilterFace.Back, info.back);
        ApplyToggleValidity(leftToggle, ConveyorBeltUtility.SplitterFilterFace.Left, info.left);
        ApplyToggleValidity(rightToggle, ConveyorBeltUtility.SplitterFilterFace.Right, info.right);
    }

    private void ApplyToggleValidity(Toggle toggle, ConveyorBeltUtility.SplitterFilterFace face, bool valid)
    {
        if (toggle == null)
            return;

        toggle.interactable = valid;
        if (valid || !toggle.isOn)
            return;

        suppressUiCallbacks = true;
        try
        {
            toggle.SetIsOnWithoutNotify(false);
        }
        finally
        {
            suppressUiCallbacks = false;
        }

        ConveyorBeltUtility.SetSplitterFilterFaceEnabled(activeSplitterBlock, face, false);
    }

    private void SetFaceFilter(ConveyorBeltUtility.SplitterFilterFace face, bool enabled)
    {
        if (suppressUiCallbacks || !IsSplitterOpen)
            return;

        ConveyorBeltUtility.SetSplitterFilterFaceEnabled(activeSplitterBlock, face, enabled);
    }

    private void HandleFilterSlotChanged(Slot slot)
    {
        if (suppressUiCallbacks || !IsSplitterOpen || slot != filterSlot)
            return;

        SaveFilterFromSlot();
    }

    private bool HandleFilterBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        if (slot != filterSlot)
            return true;

        if (incomingItem == null || incomingAmount != 1)
            return false;

        return filterSlot == null || filterSlot.IsEmpty || filterSlot.item == incomingItem && filterSlot.amount <= 0;
    }

    private bool HandleFilterQuickTransfer(Slot slot)
    {
        if (slot != filterSlot || slot == null || slot.IsEmpty || inventory == null)
            return false;

        Item movedItem = slot.item;
        int remaining = inventory.InsertItem(movedItem, slot.amount, slot);
        int moved = slot.amount - remaining;
        if (moved <= 0)
            return false;

        if (remaining <= 0)
            slot.Clear();
        else
            slot.SetContents(movedItem, remaining);

        SaveFilterFromSlot();
        return true;
    }

    private bool HandleInventoryQuickTransferRequested(Slot slot)
    {
        if (!IsSplitterOpen || slot == null || slot.IsEmpty || inventory == null || !inventory.ContainsSlot(slot))
            return false;

        if (filterSlot == null || !filterSlot.IsEmpty)
            return false;

        Item movedItem = slot.item;
        if (!filterSlot.CanAccept(movedItem, 1))
            return false;

        int removed = slot.Remove(1);
        if (removed <= 0)
            return false;

        filterSlot.SetContents(movedItem, 1);
        SaveFilterFromSlot();
        return true;
    }

    private void SyncInventorySlotSubscriptions()
    {
        if (!IsSplitterOpen || inventory == null)
        {
            UnsubscribeInventorySlotQuickTransfers();
            return;
        }

        if (subscribedInventory == inventory)
            return;

        UnsubscribeInventorySlotQuickTransfers();
        subscribedInventory = inventory;

        Slot[] slots = subscribedInventory.Slots;
        if (slots == null)
            return;

        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot != null)
                slot.QuickTransferRequested += HandleInventoryQuickTransferRequested;
        }
    }

    private void UnsubscribeInventorySlotQuickTransfers()
    {
        if (subscribedInventory == null || subscribedInventory.Slots == null)
        {
            subscribedInventory = null;
            return;
        }

        Slot[] slots = subscribedInventory.Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot != null)
                slot.QuickTransferRequested -= HandleInventoryQuickTransferRequested;
        }

        subscribedInventory = null;
    }

    private bool TryGetTargetSplitter(BlockSelector activeSelector, out Vector3Int splitterBlock)
    {
        splitterBlock = default;
        if (activeSelector == null || !activeSelector.TryGetSelectedBlock(out splitterBlock, out _))
            return false;

        return World.Instance != null &&
               !activeSelector.IsBillboardHit &&
               World.Instance.GetBlockAt(splitterBlock) == BlockType.conveyorBelt_splitter;
    }

    private bool ShouldCloseSplitter()
    {
        if (!IsSplitterOpen)
            return false;

        if (closeWhenSplitterRemoved && World.Instance != null && World.Instance.GetBlockAt(activeSplitterBlock) != BlockType.conveyorBelt_splitter)
            return true;

        if (!closeWhenTooFar || distanceOrigin == null)
            return false;

        Vector3 blockCenter = activeSplitterBlock + Vector3.one * 0.5f;
        return (distanceOrigin.position - blockCenter).sqrMagnitude > maxInteractDistance * maxInteractDistance;
    }

    private void RefreshPanelVisibility()
    {
        if (splitterPanel == null)
            return;

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        bool shouldBeVisible = inventoryOpen && IsSplitterOpen;
        if (splitterPanel.activeSelf != shouldBeVisible)
            splitterPanel.SetActive(shouldBeVisible);
    }

    private void ApplyCraftingUiBlock(bool blocked)
    {
        if (CraftingMenuUI.Instance != null)
            CraftingMenuUI.Instance.SetExternalPanelBlocked(blocked);

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.SetExternalPlayerCraftPanelBlocked(blocked);
    }

    private Toggle FindToggleByName(string namePart)
    {
        if (splitterPanel == null)
            return null;

        Toggle[] toggles = splitterPanel.GetComponentsInChildren<Toggle>(true);
        for (int i = 0; i < toggles.Length; i++)
        {
            Toggle toggle = toggles[i];
            if (toggle != null && toggle.name.ToLowerInvariant().Contains(namePart))
                return toggle;
        }

        return null;
    }

    private static void SetToggleWithoutNotify(Toggle toggle, bool value)
    {
        if (toggle != null)
            toggle.SetIsOnWithoutNotify(value);
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[SplitterUIController] {message}");
    }
}
