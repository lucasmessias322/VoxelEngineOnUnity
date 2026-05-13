using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class SteamEngineUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private const string RuntimeControllerName = "__RuntimeSteamEngineUIController";
    private const string IconChildName = "IconImage";
    private const string AmountChildName = "amount";

    public static SteamEngineUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;

    [Header("UI")]
    [SerializeField] private GameObject steamEnginePanel;
    [SerializeField] private Slot fuelSlot;
    [SerializeField] private Slot slotTemplate;
    [SerializeField] private Image burnFillImage;
    [SerializeField] private Image waterFillImage;
    [SerializeField] private bool createRuntimePanelWhenMissing = true;

    [Header("Interaction")]
    [SerializeField] private bool rightClickOpensSteamEngine = true;
    [SerializeField] private bool autoOpenInventoryWithSteamEngine = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenSteamEngineRemoved = true;
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;

    private readonly List<Slot> subscribedInventorySlots = new List<Slot>(64);
    private Vector3Int activeSteamEngineBlock = InvalidBlock;
    private bool suppressSlotCallbacks;
    private PlayerInventory subscribedInventory;

    public bool IsSteamEngineOpen => activeSteamEngineBlock != InvalidBlock;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapRuntimeInstance()
    {
        EnsureInstance();
    }

    public static SteamEngineUIController EnsureInstance()
    {
        if (Instance != null)
            return Instance;

        SteamEngineUIController existing = FindAnyObjectByType<SteamEngineUIController>();
        if (existing != null)
            return existing;

        GameObject controllerObject = new GameObject(RuntimeControllerName);
        return controllerObject.AddComponent<SteamEngineUIController>();
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
        ConfigureSlot();
        SubscribeSlot();
        RefreshPanelVisibility();
        RefreshUi();
    }

    private void Update()
    {
        ResolveReferences();
        EnsurePanel();
        SyncInventoryQuickTransfers();

        if (IsSteamEngineOpen && ShouldCloseSteamEngine())
            CloseSteamEnginePanel();

        RefreshPanelVisibility();
        RefreshUi();
    }

    private void OnDestroy()
    {
        UnsubscribeSlot();
        UnsubscribeInventoryQuickTransfers();

        if (Instance == this)
            Instance = null;

        ApplyCraftingUiBlock(false);
    }

    public bool TryHandleSteamEngineInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensSteamEngine || !Input.GetMouseButtonDown(1))
            return false;

        BlockSelector selectorToUse = activeSelector != null ? activeSelector : selector;
        if (selectorToUse == null ||
            !selectorToUse.TryGetSelectedBlock(out Vector3Int steamEngineBlock, out _) ||
            World.Instance == null ||
            World.Instance.GetBlockAt(steamEngineBlock) != BlockType.SteamEngine)
        {
            return false;
        }

        OpenSteamEngine(steamEngineBlock);
        return true;
    }

    public void OpenSteamEngine(Vector3Int steamEngineBlock)
    {
        ResolveReferences();
        EnsurePanel();

        activeSteamEngineBlock = steamEngineBlock;

        if (ChestUIController.Instance != null)
            ChestUIController.Instance.CloseChestPanel();

        if (FurnaceUIController.Instance != null)
            FurnaceUIController.Instance.CloseFurnacePanel();

        if (StoneCrusherUIController.Instance != null)
            StoneCrusherUIController.Instance.CloseCrusherPanel();

        if (SplitterUIController.Instance != null)
            SplitterUIController.Instance.CloseSplitterPanel();

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.CloseCrafterUI(returnItemsToInventory: true);

        ApplyCraftingUiBlock(true);

        if (autoOpenInventoryWithSteamEngine && inventory != null && !inventory.IsInventoryOpen)
            inventory.SetInventoryOpen(true);

        SubscribeInventoryQuickTransfers();
        RefreshPanelVisibility();
        RefreshUi();
    }

    public void CloseSteamEnginePanel()
    {
        if (!IsSteamEngineOpen)
        {
            RefreshPanelVisibility();
            return;
        }

        SyncFuelSlotToWorld();
        activeSteamEngineBlock = InvalidBlock;
        UnsubscribeInventoryQuickTransfers();
        ApplyCraftingUiBlock(false);
        RefreshPanelVisibility();
        RefreshUi();
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        if (!inventoryOpen && closeWhenInventoryCloses && IsSteamEngineOpen)
            CloseSteamEnginePanel();
        else
            RefreshPanelVisibility();
    }

    private void ConfigureSlot()
    {
        if (fuelSlot == null)
            return;

        fuelSlot.SetRequireInventorySlotMapping(false);
        fuelSlot.SetManualInteraction(canPickup: true, canInsert: true, canUseRightClick: true);
        fuelSlot.RefreshUI();
    }

    private void SubscribeSlot()
    {
        if (fuelSlot == null)
            return;

        fuelSlot.SlotChanged -= HandleFuelSlotChanged;
        fuelSlot.BeforePlaceIntoSlot -= HandleFuelBeforePlace;
        fuelSlot.QuickTransferRequested -= HandleFuelQuickTransfer;

        fuelSlot.SlotChanged += HandleFuelSlotChanged;
        fuelSlot.BeforePlaceIntoSlot += HandleFuelBeforePlace;
        fuelSlot.QuickTransferRequested += HandleFuelQuickTransfer;
    }

    private void UnsubscribeSlot()
    {
        if (fuelSlot == null)
            return;

        fuelSlot.SlotChanged -= HandleFuelSlotChanged;
        fuelSlot.BeforePlaceIntoSlot -= HandleFuelBeforePlace;
        fuelSlot.QuickTransferRequested -= HandleFuelQuickTransfer;
    }

    private bool HandleFuelBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        return World.Instance != null &&
               incomingItem != null &&
               incomingAmount > 0 &&
               World.Instance.CanUseSteamEngineFuel(incomingItem);
    }

    private bool HandleFuelQuickTransfer(Slot slot)
    {
        if (slot == null || slot.IsEmpty || inventory == null)
            return false;

        bool moved = inventory.TryMoveSlotToInventory(slot);
        if (moved)
            SyncFuelSlotToWorld();

        return moved;
    }

    private void HandleFuelSlotChanged(Slot slot)
    {
        if (suppressSlotCallbacks)
            return;

        SyncFuelSlotToWorld();
    }

    private void SyncFuelSlotToWorld()
    {
        if (!IsSteamEngineOpen || World.Instance == null || fuelSlot == null)
            return;

        if (fuelSlot.IsEmpty)
            World.Instance.SetSteamEngineFuelContents(activeSteamEngineBlock, null, 0);
        else
            World.Instance.SetSteamEngineFuelContents(activeSteamEngineBlock, fuelSlot.item, fuelSlot.amount);
    }

    private void RefreshUi()
    {
        suppressSlotCallbacks = true;
        try
        {
            if (fuelSlot != null)
            {
                if (IsSteamEngineOpen && World.Instance != null)
                {
                    Item fuelItem = World.Instance.GetSteamEngineFuelItem(activeSteamEngineBlock);
                    int fuelAmount = World.Instance.GetSteamEngineFuelAmount(activeSteamEngineBlock);
                    if (fuelItem != null && fuelAmount > 0)
                        fuelSlot.SetContents(fuelItem, fuelAmount);
                    else
                        fuelSlot.Clear();
                }
                else
                {
                    fuelSlot.Clear();
                }
            }
        }
        finally
        {
            suppressSlotCallbacks = false;
        }

        float burn01 = IsSteamEngineOpen && World.Instance != null
            ? World.Instance.GetSteamEngineFuel01(activeSteamEngineBlock)
            : 0f;
        float water01 = IsSteamEngineOpen && World.Instance != null
            ? World.Instance.GetSteamEngineWater01(activeSteamEngineBlock)
            : 0f;

        if (burnFillImage != null)
            burnFillImage.fillAmount = burn01;

        if (waterFillImage != null)
            waterFillImage.fillAmount = water01;
    }

    private void SyncInventoryQuickTransfers()
    {
        if (!IsSteamEngineOpen)
        {
            UnsubscribeInventoryQuickTransfers();
            return;
        }

        if (subscribedInventory == inventory)
            return;

        SubscribeInventoryQuickTransfers();
    }

    private void SubscribeInventoryQuickTransfers()
    {
        UnsubscribeInventoryQuickTransfers();
        if (inventory == null || inventory.Slots == null)
            return;

        subscribedInventory = inventory;
        Slot[] slots = inventory.Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot == null)
                continue;

            slot.QuickTransferRequested += HandleInventoryQuickTransferRequested;
            subscribedInventorySlots.Add(slot);
        }
    }

    private void UnsubscribeInventoryQuickTransfers()
    {
        for (int i = 0; i < subscribedInventorySlots.Count; i++)
        {
            Slot slot = subscribedInventorySlots[i];
            if (slot != null)
                slot.QuickTransferRequested -= HandleInventoryQuickTransferRequested;
        }

        subscribedInventorySlots.Clear();
        subscribedInventory = null;
    }

    private bool HandleInventoryQuickTransferRequested(Slot sourceSlot)
    {
        if (!IsSteamEngineOpen ||
            sourceSlot == null ||
            sourceSlot.IsEmpty ||
            inventory == null ||
            World.Instance == null ||
            !inventory.ContainsSlot(sourceSlot))
        {
            return false;
        }

        Item sourceItem = sourceSlot.item;
        int originalAmount = sourceSlot.amount;
        if (!World.Instance.CanUseSteamEngineFuel(sourceItem))
            return false;

        int remaining = World.Instance.InsertSteamEngineFuel(activeSteamEngineBlock, sourceItem, originalAmount);
        int moved = originalAmount - remaining;
        if (moved <= 0)
            return false;

        if (remaining <= 0)
            sourceSlot.Clear();
        else
            sourceSlot.SetContents(sourceItem, remaining);

        RefreshUi();
        return true;
    }

    private bool ShouldCloseSteamEngine()
    {
        if (!IsSteamEngineOpen)
            return false;

        if (closeWhenSteamEngineRemoved &&
            (World.Instance == null || World.Instance.GetBlockAt(activeSteamEngineBlock) != BlockType.SteamEngine))
        {
            return true;
        }

        if (!closeWhenTooFar)
            return false;

        Transform origin = distanceOrigin != null ? distanceOrigin : Camera.main != null ? Camera.main.transform : null;
        if (origin == null)
            return false;

        Vector3 blockCenter = activeSteamEngineBlock + Vector3.one * 0.5f;
        return (origin.position - blockCenter).sqrMagnitude > maxInteractDistance * maxInteractDistance;
    }

    private void RefreshPanelVisibility()
    {
        if (steamEnginePanel == null)
            return;

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        steamEnginePanel.SetActive(inventoryOpen && IsSteamEngineOpen);
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

        if (slotTemplate == null && inventory != null && inventory.Slots != null)
        {
            Slot[] slots = inventory.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null)
                {
                    slotTemplate = slots[i];
                    break;
                }
            }
        }
    }

    private void ApplyCraftingUiBlock(bool blocked)
    {
        if (CraftingMenuUI.Instance != null)
            CraftingMenuUI.Instance.SetExternalPanelBlocked(blocked);

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.SetExternalPlayerCraftPanelBlocked(blocked);
    }

    private void EnsurePanel()
    {
        if (steamEnginePanel != null)
        {
            if (fuelSlot == null)
                fuelSlot = steamEnginePanel.GetComponentInChildren<Slot>(true);

            ConfigureSlot();
            SubscribeSlot();
            return;
        }

        if (!createRuntimePanelWhenMissing)
            return;

        Canvas canvas = ResolveCanvas();
        if (canvas == null)
            return;

        CreateRuntimePanel(canvas);
        ConfigureSlot();
        SubscribeSlot();
    }

    private Canvas ResolveCanvas()
    {
        if (inventory != null)
        {
            if (inventory.SlotsContainer != null)
            {
                Canvas canvasFromSlots = inventory.SlotsContainer.GetComponentInParent<Canvas>(true);
                if (canvasFromSlots != null)
                    return canvasFromSlots;
            }

            Canvas canvasFromInventory = inventory.GetComponentInChildren<Canvas>(true);
            if (canvasFromInventory != null)
                return canvasFromInventory;
        }

        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        return canvases != null && canvases.Length > 0 ? canvases[0] : null;
    }

    private void CreateRuntimePanel(Canvas canvas)
    {
        GameObject panelObject = new GameObject("SteamEnginePanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
        steamEnginePanel = panelObject;

        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.SetParent(canvas.transform, false);
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = new Vector2(0f, 145f);
        panelRect.sizeDelta = new Vector2(300f, 155f);

        Image panelImage = panelObject.GetComponent<Image>();
        panelImage.color = new Color(0.08f, 0.075f, 0.065f, 0.92f);

        CreateRuntimeLabel(panelRect, "Steam Engine", new Vector2(0f, 55f), 22f, TextAlignmentOptions.Center);
        CreateRuntimeLabel(panelRect, "Carvao", new Vector2(-78f, 16f), 15f, TextAlignmentOptions.Center);
        CreateRuntimeLabel(panelRect, "Agua", new Vector2(64f, 24f), 14f, TextAlignmentOptions.Left);
        CreateRuntimeLabel(panelRect, "Fogo", new Vector2(64f, -20f), 14f, TextAlignmentOptions.Left);

        fuelSlot = CreateRuntimeSlot(panelRect, new Vector2(-78f, -22f));
        waterFillImage = CreateRuntimeBar(panelRect, new Vector2(62f, 2f), new Color(0.16f, 0.52f, 0.95f, 1f));
        burnFillImage = CreateRuntimeBar(panelRect, new Vector2(62f, -42f), new Color(1f, 0.45f, 0.08f, 1f));

        steamEnginePanel.SetActive(false);
    }

    private static TextMeshProUGUI CreateRuntimeLabel(
        RectTransform parent,
        string text,
        Vector2 anchoredPosition,
        float fontSize,
        TextAlignmentOptions alignment)
    {
        GameObject labelObject = new GameObject(text, typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.SetParent(parent, false);
        labelRect.anchorMin = new Vector2(0.5f, 0.5f);
        labelRect.anchorMax = new Vector2(0.5f, 0.5f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.anchoredPosition = anchoredPosition;
        labelRect.sizeDelta = new Vector2(250f, 28f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = Color.white;
        label.alignment = alignment;
        label.raycastTarget = false;
        return label;
    }

    private Slot CreateRuntimeSlot(RectTransform parent, Vector2 anchoredPosition)
    {
        GameObject slotObject;
        if (slotTemplate != null)
        {
            slotObject = Instantiate(slotTemplate.gameObject, parent, false);
            slotObject.name = "SteamEngineFuelSlot";
            HideTemplateOnlyChildren(slotObject.transform);
        }
        else
        {
            slotObject = CreateFallbackSlotObject(parent);
        }

        RectTransform slotRect = slotObject.transform as RectTransform;
        if (slotRect != null)
        {
            slotRect.anchorMin = new Vector2(0.5f, 0.5f);
            slotRect.anchorMax = new Vector2(0.5f, 0.5f);
            slotRect.pivot = new Vector2(0.5f, 0.5f);
            slotRect.anchoredPosition = anchoredPosition;
            slotRect.sizeDelta = new Vector2(52f, 52f);
        }

        Slot slot = slotObject.GetComponent<Slot>();
        if (slot == null)
            slot = slotObject.AddComponent<Slot>();

        slot.Clear();
        return slot;
    }

    private static GameObject CreateFallbackSlotObject(Transform parent)
    {
        GameObject slotObject = new GameObject("SteamEngineFuelSlot", typeof(RectTransform), typeof(Image));
        slotObject.transform.SetParent(parent, false);

        Image background = slotObject.GetComponent<Image>();
        background.color = new Color(0.18f, 0.17f, 0.15f, 0.95f);

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
        amountText.fontSize = 20f;
        amountText.color = Color.white;
        amountText.raycastTarget = false;

        slotObject.AddComponent<Slot>();
        return slotObject;
    }

    private static Image CreateRuntimeBar(RectTransform parent, Vector2 anchoredPosition, Color fillColor)
    {
        GameObject barObject = new GameObject("Bar", typeof(RectTransform), typeof(Image));
        RectTransform barRect = barObject.GetComponent<RectTransform>();
        barRect.SetParent(parent, false);
        barRect.anchorMin = new Vector2(0.5f, 0.5f);
        barRect.anchorMax = new Vector2(0.5f, 0.5f);
        barRect.pivot = new Vector2(0f, 0.5f);
        barRect.anchoredPosition = anchoredPosition;
        barRect.sizeDelta = new Vector2(120f, 14f);

        Image background = barObject.GetComponent<Image>();
        background.color = new Color(0.025f, 0.025f, 0.025f, 0.85f);

        GameObject fillObject = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.SetParent(barRect, false);
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(2f, 2f);
        fillRect.offsetMax = new Vector2(-2f, -2f);

        Image fill = fillObject.GetComponent<Image>();
        fill.color = fillColor;
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = (int)Image.OriginHorizontal.Left;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;
        return fill;
    }

    private static void HideTemplateOnlyChildren(Transform slotRoot)
    {
        if (slotRoot == null)
            return;

        Transform selected = FindChildByName(slotRoot, "SelectedImage");
        if (selected != null)
            selected.gameObject.SetActive(false);
    }

    private static Transform FindChildByName(Transform parent, string childName)
    {
        if (parent == null)
            return null;

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
