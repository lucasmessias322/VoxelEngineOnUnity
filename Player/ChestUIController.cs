using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChestUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private const string RuntimeControllerName = "__RuntimeChestUIController";
    private const int ChestSlotCount = 27;
    private const int ChestColumnCount = 9;
    private const string IconChildName = "IconImage";
    private const string AmountChildName = "amount";

    public static ChestUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;

    [Header("Optional UI")]
    [SerializeField] private GameObject chestPanel;
    [SerializeField] private Transform chestSlotsContainer;
    [SerializeField] private Slot slotTemplate;
    [SerializeField] private bool createRuntimePanelWhenMissing = true;

    [Header("Interaction")]
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;
    [SerializeField] private bool rightClickOpensChest = true;
    [SerializeField] private bool autoOpenInventoryWithChest = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenChestRemoved = true;
    [SerializeField] private bool dropContentsWhenBroken = true;
    [SerializeField] private bool debugLogs;

    private readonly Dictionary<Vector3Int, ChestInventoryData> chestStorage = new Dictionary<Vector3Int, ChestInventoryData>();
    private readonly List<Slot> slotCollectBuffer = new List<Slot>(ChestSlotCount);
    private readonly List<Slot> subscribedInventorySlots = new List<Slot>(64);
    private readonly List<Transform> removeChildBuffer = new List<Transform>(4);

    private Slot[] chestSlots = System.Array.Empty<Slot>();
    private Vector3Int activeChestBlock = InvalidBlock;
    private bool suppressSlotCallbacks;
    private World subscribedWorld;
    private PlayerInventory subscribedInventory;

    public bool IsChestOpen => activeChestBlock != InvalidBlock;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapRuntimeInstance()
    {
        EnsureInstance();
    }

    public static ChestUIController EnsureInstance()
    {
        if (Instance != null)
            return Instance;

        ChestUIController existing = FindAnyObjectByType<ChestUIController>();
        if (existing != null)
            return existing;

        GameObject controllerObject = new GameObject(RuntimeControllerName);
        return controllerObject.AddComponent<ChestUIController>();
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
        SyncWorldSubscription();
        EnsureChestSlots();
        RefreshPanelVisibility();
    }

    private void Update()
    {
        ResolveReferences();
        SyncWorldSubscription();

        if (IsChestOpen && ShouldCloseChest())
        {
            if (closeWhenChestRemoved &&
                World.Instance != null &&
                World.Instance.GetBlockAt(activeChestBlock) != BlockType.chest)
            {
                RemoveChestStorageAndDrop(activeChestBlock);
            }

            CloseChestPanel();
        }

        RefreshPanelVisibility();
    }

    private void OnDestroy()
    {
        UnsubscribeWorld();
        UnsubscribeInventoryQuickTransfers();

        if (Instance == this)
            Instance = null;

        ApplyCraftingUiBlock(false);
    }

    public bool TryHandleChestInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensChest || !Input.GetMouseButtonDown(1))
            return false;

        if (!TryGetTargetChest(activeSelector != null ? activeSelector : selector, out Vector3Int chestBlock))
            return false;

        OpenChest(chestBlock);
        return true;
    }

    public void OpenChest(Vector3Int chestBlock)
    {
        ResolveReferences();
        EnsureChestSlots();

        if (chestSlots == null || chestSlots.Length == 0)
        {
            Log("Nao foi possivel abrir o bau: slots do painel nao encontrados.");
            return;
        }

        if (IsChestOpen)
            SaveActiveChestFromSlots();

        activeChestBlock = chestBlock;
        LoadChestIntoSlots(GetOrCreateChestData(chestBlock));

        if (FurnaceUIController.Instance != null)
            FurnaceUIController.Instance.CloseFurnacePanel();

        if (SteamEngineUIController.Instance != null)
            SteamEngineUIController.Instance.CloseSteamEnginePanel();

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.CloseCrafterUI(returnItemsToInventory: true);

        ApplyCraftingUiBlock(true);

        if (autoOpenInventoryWithChest && inventory != null && !inventory.IsInventoryOpen)
            inventory.SetInventoryOpen(true);

        SubscribeInventoryQuickTransfers();
        RefreshPanelVisibility();
        Log($"Bau aberto em {chestBlock}.");
    }

    public void CloseChestPanel()
    {
        if (!IsChestOpen)
        {
            RefreshPanelVisibility();
            return;
        }

        SaveActiveChestFromSlots();
        activeChestBlock = InvalidBlock;
        UnsubscribeInventoryQuickTransfers();
        ApplyCraftingUiBlock(false);
        RefreshPanelVisibility();
        Log("Bau fechado.");
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        if (!inventoryOpen && closeWhenInventoryCloses && IsChestOpen)
            CloseChestPanel();
        else
            RefreshPanelVisibility();
    }

    public bool HasItemStackInChest(Vector3Int chestBlock)
    {
        if (!IsChestBlockInWorld(chestBlock))
            return false;

        if (IsChestOpen && activeChestBlock == chestBlock)
            SaveActiveChestFromSlots();

        if (!chestStorage.TryGetValue(chestBlock, out ChestInventoryData data) || data == null)
            return false;

        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length; i++)
        {
            if (data.Items[i] != null && data.Amounts[i] > 0)
                return true;
        }

        return false;
    }

    public bool TryTakeItemStackFromChest(Vector3Int chestBlock, int maxAmount, out Item item, out int amount)
    {
        return TryTakeItemStackFromChest(chestBlock, maxAmount, null, out item, out amount);
    }

    public bool TryTakeItemStackFromChest(Vector3Int chestBlock, int maxAmount, Item preferredItem, out Item item, out int amount)
    {
        item = null;
        amount = 0;

        if (maxAmount <= 0 || !IsChestBlockInWorld(chestBlock))
            return false;

        if (IsChestOpen && activeChestBlock == chestBlock)
            SaveActiveChestFromSlots();

        if (!chestStorage.TryGetValue(chestBlock, out ChestInventoryData data) || data == null)
            return false;

        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length; i++)
        {
            Item slotItem = data.Items[i];
            int slotAmount = data.Amounts[i];
            if (slotItem == null || slotAmount <= 0)
                continue;

            if (preferredItem != null && slotItem != preferredItem)
                continue;

            amount = Mathf.Min(maxAmount, slotAmount);
            item = slotItem;
            data.Amounts[i] = slotAmount - amount;
            if (data.Amounts[i] <= 0)
            {
                data.Items[i] = null;
                data.Amounts[i] = 0;
            }

            RefreshActiveChestSlotsIfNeeded(chestBlock, data);
            return true;
        }

        return false;
    }

    public bool TryPeekItemStackFromChest(Vector3Int chestBlock, int maxAmount, out Item item, out int amount)
    {
        return TryPeekItemStackFromChest(chestBlock, maxAmount, null, out item, out amount);
    }

    public bool TryPeekItemStackFromChest(
        Vector3Int chestBlock,
        int maxAmount,
        System.Predicate<Item> itemFilter,
        out Item item,
        out int amount)
    {
        item = null;
        amount = 0;

        if (maxAmount <= 0 || !IsChestBlockInWorld(chestBlock))
            return false;

        if (IsChestOpen && activeChestBlock == chestBlock)
            SaveActiveChestFromSlots();

        if (!chestStorage.TryGetValue(chestBlock, out ChestInventoryData data) || data == null)
            return false;

        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length; i++)
        {
            Item slotItem = data.Items[i];
            int slotAmount = data.Amounts[i];
            if (slotItem == null || slotAmount <= 0)
                continue;

            if (itemFilter != null && !itemFilter(slotItem))
                continue;

            item = slotItem;
            amount = Mathf.Min(maxAmount, slotAmount);
            return true;
        }

        return false;
    }

    public int InsertItemStackIntoChest(Vector3Int chestBlock, Item item, int amount)
    {
        if (item == null || amount <= 0 || !IsChestBlockInWorld(chestBlock))
            return amount;

        if (IsChestOpen && activeChestBlock == chestBlock)
            SaveActiveChestFromSlots();

        ChestInventoryData data = GetOrCreateChestData(chestBlock);
        int remaining = InsertIntoMatchingChestDataSlots(data, item, amount);
        remaining = InsertIntoEmptyChestDataSlots(data, item, remaining);

        if (remaining != amount)
            RefreshActiveChestSlotsIfNeeded(chestBlock, data);

        return remaining;
    }

    public bool CanInsertItemStackIntoChest(Vector3Int chestBlock, Item item, int amount)
    {
        if (item == null || amount <= 0 || !IsChestBlockInWorld(chestBlock))
            return false;

        if (IsChestOpen && activeChestBlock == chestBlock)
            SaveActiveChestFromSlots();

        ChestInventoryData data = GetOrCreateChestData(chestBlock);
        return CountInsertCapacityInChestData(data, item) >= amount;
    }

    public static bool TrySpawnItemStack(Item item, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        if (item == null || amount <= 0)
            return false;

        if (BlockItemCatalog.TryGetBlockForItem(item, out BlockType blockType) &&
            World.Instance != null &&
            BlockDrop.Spawn(World.Instance, worldPosition, blockType, amount, throwDirection))
        {
            return true;
        }

        return InventoryItemDrop.Spawn(item, amount, worldPosition, throwDirection);
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
            Slot[] inventorySlots = inventory.Slots;
            for (int i = 0; i < inventorySlots.Length; i++)
            {
                if (inventorySlots[i] != null)
                {
                    slotTemplate = inventorySlots[i];
                    break;
                }
            }
        }
    }

    private static bool IsChestBlockInWorld(Vector3Int chestBlock)
    {
        return World.Instance != null && World.Instance.GetBlockAt(chestBlock) == BlockType.chest;
    }

    private void SyncWorldSubscription()
    {
        World currentWorld = World.Instance;
        if (subscribedWorld == currentWorld)
            return;

        UnsubscribeWorld();
        subscribedWorld = currentWorld;

        if (subscribedWorld != null)
            subscribedWorld.BlockChanged += HandleWorldBlockChanged;
    }

    private void UnsubscribeWorld()
    {
        if (subscribedWorld != null)
            subscribedWorld.BlockChanged -= HandleWorldBlockChanged;

        subscribedWorld = null;
    }

    private void EnsureChestSlots()
    {
        if (chestSlots != null && chestSlots.Length > 0)
            return;

        if (chestSlotsContainer != null)
            CollectConfiguredSlots();
        else if (chestPanel != null)
            chestSlotsContainer = FindChildByName(chestPanel.transform, "ChestSlots");

        if ((chestSlots == null || chestSlots.Length == 0) && chestSlotsContainer != null)
            CollectConfiguredSlots();

        if ((chestSlots == null || chestSlots.Length == 0) && createRuntimePanelWhenMissing)
            CreateRuntimeChestPanel();

        ConfigureChestSlots();
    }

    private void CollectConfiguredSlots()
    {
        slotCollectBuffer.Clear();
        if (chestSlotsContainer != null)
            chestSlotsContainer.GetComponentsInChildren(true, slotCollectBuffer);

        if (slotCollectBuffer.Count == 0)
        {
            chestSlots = System.Array.Empty<Slot>();
            return;
        }

        slotCollectBuffer.Sort(CompareSlotSiblingIndex);
        int count = Mathf.Min(ChestSlotCount, slotCollectBuffer.Count);
        chestSlots = new Slot[count];
        for (int i = 0; i < count; i++)
            chestSlots[i] = slotCollectBuffer[i];
    }

    private void CreateRuntimeChestPanel()
    {
        Canvas canvas = ResolveCanvas();
        if (canvas == null)
            return;

        GameObject panelObject = new GameObject("ChestPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
        chestPanel = panelObject;
        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.SetParent(canvas.transform, false);
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = new Vector2(0f, 150f);
        panelRect.sizeDelta = new Vector2(550f, 205f);

        Image panelImage = panelObject.GetComponent<Image>();
        panelImage.color = new Color(0.08f, 0.075f, 0.065f, 0.92f);

        GameObject slotsObject = new GameObject("ChestSlots", typeof(RectTransform), typeof(GridLayoutGroup));
        RectTransform slotsRect = slotsObject.GetComponent<RectTransform>();
        slotsRect.SetParent(panelRect, false);
        slotsRect.anchorMin = new Vector2(0.5f, 0.5f);
        slotsRect.anchorMax = new Vector2(0.5f, 0.5f);
        slotsRect.pivot = new Vector2(0.5f, 0.5f);
        slotsRect.anchoredPosition = Vector2.zero;
        slotsRect.sizeDelta = new Vector2(508f, 162f);

        GridLayoutGroup grid = slotsObject.GetComponent<GridLayoutGroup>();
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = ChestColumnCount;
        grid.cellSize = new Vector2(52f, 52f);
        grid.spacing = new Vector2(5f, 5f);
        grid.childAlignment = TextAnchor.MiddleCenter;

        chestSlotsContainer = slotsRect;
        chestSlots = new Slot[ChestSlotCount];
        for (int i = 0; i < ChestSlotCount; i++)
            chestSlots[i] = CreateRuntimeSlot(i, slotsRect);

        chestPanel.SetActive(false);
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

    private Slot CreateRuntimeSlot(int index, Transform parent)
    {
        GameObject slotObject;
        if (slotTemplate != null)
        {
            slotObject = Instantiate(slotTemplate.gameObject, parent, false);
            slotObject.name = $"ChestSlot ({index})";
            HideTemplateOnlyChildren(slotObject.transform);
        }
        else
        {
            slotObject = CreateFallbackSlotObject(index, parent);
        }

        RectTransform rect = slotObject.transform as RectTransform;
        if (rect != null)
            rect.sizeDelta = new Vector2(52f, 52f);

        Slot slot = slotObject.GetComponent<Slot>();
        if (slot == null)
            slot = slotObject.AddComponent<Slot>();

        return slot;
    }

    private GameObject CreateFallbackSlotObject(int index, Transform parent)
    {
        GameObject slotObject = new GameObject($"ChestSlot ({index})", typeof(RectTransform), typeof(Image));
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

        Image icon = iconObject.GetComponent<Image>();
        icon.raycastTarget = false;

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

    private void ConfigureChestSlots()
    {
        if (chestSlots == null)
            return;

        suppressSlotCallbacks = true;
        try
        {
            for (int i = 0; i < chestSlots.Length; i++)
            {
                Slot slot = chestSlots[i];
                if (slot == null)
                    continue;

                slot.SetIndex(i);
                slot.SetRequireInventorySlotMapping(false);
                slot.SetManualInteraction(true, true, true);
                slot.SlotChanged -= HandleChestSlotChanged;
                slot.SlotChanged += HandleChestSlotChanged;
                slot.QuickTransferRequested -= HandleChestSlotQuickTransfer;
                slot.QuickTransferRequested += HandleChestSlotQuickTransfer;
                slot.Clear();
                slot.RefreshUI();
            }
        }
        finally
        {
            suppressSlotCallbacks = false;
        }
    }

    private void LoadChestIntoSlots(ChestInventoryData data)
    {
        if (data == null || chestSlots == null)
            return;

        suppressSlotCallbacks = true;
        try
        {
            for (int i = 0; i < chestSlots.Length; i++)
            {
                Slot slot = chestSlots[i];
                if (slot == null)
                    continue;

                if (i < data.Items.Length && data.Items[i] != null && data.Amounts[i] > 0)
                    slot.SetContents(data.Items[i], data.Amounts[i]);
                else
                    slot.Clear();
            }
        }
        finally
        {
            suppressSlotCallbacks = false;
        }
    }

    private void RefreshActiveChestSlotsIfNeeded(Vector3Int chestBlock, ChestInventoryData data)
    {
        if (!IsChestOpen || activeChestBlock != chestBlock)
            return;

        LoadChestIntoSlots(data);
    }

    private void SaveActiveChestFromSlots()
    {
        if (!IsChestOpen || chestSlots == null)
            return;

        ChestInventoryData data = GetOrCreateChestData(activeChestBlock);
        for (int i = 0; i < data.Items.Length; i++)
        {
            if (i < chestSlots.Length && chestSlots[i] != null && !chestSlots[i].IsEmpty)
            {
                data.Items[i] = chestSlots[i].item;
                data.Amounts[i] = chestSlots[i].amount;
            }
            else
            {
                data.Items[i] = null;
                data.Amounts[i] = 0;
            }
        }
    }

    private ChestInventoryData GetOrCreateChestData(Vector3Int chestBlock)
    {
        if (!chestStorage.TryGetValue(chestBlock, out ChestInventoryData data) || data == null)
        {
            data = new ChestInventoryData(ChestSlotCount);
            chestStorage[chestBlock] = data;
        }

        data.EnsureSize(ChestSlotCount);
        return data;
    }

    private bool HandleChestSlotQuickTransfer(Slot slot)
    {
        if (!IsChestOpen || slot == null || inventory == null)
            return false;

        return inventory.TryMoveSlotToInventory(slot);
    }

    private void HandleChestSlotChanged(Slot slot)
    {
        if (suppressSlotCallbacks || !IsChestOpen)
            return;

        SaveActiveChestFromSlots();
    }

    private bool HandleInventoryQuickTransferRequested(Slot slot)
    {
        if (!IsChestOpen || slot == null || inventory == null || !inventory.ContainsSlot(slot))
            return false;

        return TryMoveSlotToChest(slot);
    }

    private bool TryMoveSlotToChest(Slot sourceSlot)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty || chestSlots == null || chestSlots.Length == 0)
            return false;

        Item sourceItem = sourceSlot.item;
        int originalAmount = sourceSlot.amount;
        int remaining = InsertIntoMatchingChestSlots(sourceItem, originalAmount, sourceSlot);
        remaining = InsertIntoEmptyChestSlots(sourceItem, remaining, sourceSlot);

        int movedAmount = originalAmount - remaining;
        if (movedAmount <= 0)
            return false;

        sourceSlot.Remove(movedAmount);
        SaveActiveChestFromSlots();
        return true;
    }

    private int InsertIntoMatchingChestSlots(Item item, int amount, Slot excludedSlot)
    {
        int remaining = amount;
        for (int i = 0; i < chestSlots.Length && remaining > 0; i++)
        {
            Slot slot = chestSlots[i];
            if (slot == null || slot == excludedSlot || slot.IsEmpty || slot.item != item)
                continue;

            remaining = slot.Add(item, remaining);
        }

        return remaining;
    }

    private int InsertIntoMatchingChestDataSlots(ChestInventoryData data, Item item, int amount)
    {
        int remaining = amount;
        if (data == null || item == null || remaining <= 0)
            return remaining;

        int stackLimit = Mathf.Max(1, item.maxStack);
        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length && remaining > 0; i++)
        {
            if (data.Items[i] != item || data.Amounts[i] <= 0)
                continue;

            int freeSpace = Mathf.Max(0, stackLimit - data.Amounts[i]);
            int moved = Mathf.Min(freeSpace, remaining);
            if (moved <= 0)
                continue;

            data.Amounts[i] += moved;
            remaining -= moved;
        }

        return remaining;
    }

    private int InsertIntoEmptyChestDataSlots(ChestInventoryData data, Item item, int amount)
    {
        int remaining = amount;
        if (data == null || item == null || remaining <= 0)
            return remaining;

        int stackLimit = Mathf.Max(1, item.maxStack);
        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length && remaining > 0; i++)
        {
            if (data.Items[i] != null && data.Amounts[i] > 0)
                continue;

            int moved = Mathf.Min(stackLimit, remaining);
            data.Items[i] = item;
            data.Amounts[i] = moved;
            remaining -= moved;
        }

        return remaining;
    }

    private int CountInsertCapacityInChestData(ChestInventoryData data, Item item)
    {
        if (data == null || item == null)
            return 0;

        int capacity = 0;
        int stackLimit = Mathf.Max(1, item.maxStack);
        data.EnsureSize(ChestSlotCount);
        for (int i = 0; i < data.Items.Length; i++)
        {
            Item slotItem = data.Items[i];
            int slotAmount = data.Amounts[i];
            if (slotItem == item && slotAmount > 0)
            {
                capacity += Mathf.Max(0, stackLimit - slotAmount);
            }
            else if (slotItem == null || slotAmount <= 0)
            {
                capacity += stackLimit;
            }
        }

        return capacity;
    }

    private int InsertIntoEmptyChestSlots(Item item, int amount, Slot excludedSlot)
    {
        int remaining = amount;
        for (int i = 0; i < chestSlots.Length && remaining > 0; i++)
        {
            Slot slot = chestSlots[i];
            if (slot == null || slot == excludedSlot || !slot.IsEmpty)
                continue;

            remaining = slot.Add(item, remaining);
        }

        return remaining;
    }

    private void SubscribeInventoryQuickTransfers()
    {
        UnsubscribeInventoryQuickTransfers();
        if (inventory == null || inventory.Slots == null)
            return;

        subscribedInventory = inventory;
        Slot[] inventorySlots = inventory.Slots;
        for (int i = 0; i < inventorySlots.Length; i++)
        {
            Slot slot = inventorySlots[i];
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

    private void HandleWorldBlockChanged(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (previousType != BlockType.chest || newType == BlockType.chest)
            return;

        if (IsChestOpen && activeChestBlock == worldPos)
            SaveActiveChestFromSlots();

        RemoveChestStorageAndDrop(worldPos);

        if (IsChestOpen && activeChestBlock == worldPos)
            CloseChestPanel();
    }

    private void RemoveChestStorageAndDrop(Vector3Int chestBlock)
    {
        if (!chestStorage.TryGetValue(chestBlock, out ChestInventoryData data) || data == null)
            return;

        if (dropContentsWhenBroken)
            DropChestContents(chestBlock, data);

        chestStorage.Remove(chestBlock);
    }

    private void DropChestContents(Vector3Int chestBlock, ChestInventoryData data)
    {
        if (data == null)
            return;

        Vector3 basePosition = chestBlock + Vector3.one * 0.5f + Vector3.up * 0.15f;
        for (int i = 0; i < data.Items.Length; i++)
        {
            Item item = data.Items[i];
            int amount = data.Amounts[i];
            if (item == null || amount <= 0)
                continue;

            TrySpawnItemStacks(item, amount, basePosition);
            data.Items[i] = null;
            data.Amounts[i] = 0;
        }
    }

    private bool TrySpawnItemStacks(Item item, int amount, Vector3 basePosition)
    {
        if (item == null || amount <= 0)
            return false;

        bool spawnedAny = false;
        int stackLimit = Mathf.Max(1, item.maxStack);
        int remaining = amount;

        while (remaining > 0)
        {
            int stackAmount = Mathf.Min(stackLimit, remaining);
            Vector3 offset = new Vector3(
                Random.Range(-0.18f, 0.18f),
                Random.Range(0f, 0.22f),
                Random.Range(-0.18f, 0.18f));
            Vector3 throwDirection = Random.insideUnitSphere;
            throwDirection.y = Mathf.Abs(throwDirection.y) + 0.35f;
            if (throwDirection.sqrMagnitude < 0.0001f)
                throwDirection = Vector3.up;

            throwDirection = throwDirection.normalized * Random.Range(0.8f, 1.7f);
            spawnedAny |= TrySpawnSingleItemStack(item, stackAmount, basePosition + offset, throwDirection);
            remaining -= stackAmount;
        }

        return spawnedAny;
    }

    private bool TrySpawnSingleItemStack(Item item, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        return TrySpawnItemStack(item, amount, worldPosition, throwDirection);
    }

    private bool ShouldCloseChest()
    {
        if (!IsChestOpen)
            return false;

        if (closeWhenChestRemoved && World.Instance != null && World.Instance.GetBlockAt(activeChestBlock) != BlockType.chest)
            return true;

        if (!closeWhenTooFar || distanceOrigin == null)
            return false;

        Vector3 blockCenter = activeChestBlock + Vector3.one * 0.5f;
        return (distanceOrigin.position - blockCenter).sqrMagnitude > maxInteractDistance * maxInteractDistance;
    }

    private void RefreshPanelVisibility()
    {
        if (chestPanel == null)
            return;

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        bool shouldBeVisible = inventoryOpen && IsChestOpen;
        if (chestPanel.activeSelf != shouldBeVisible)
            chestPanel.SetActive(shouldBeVisible);
    }

    private bool TryGetTargetChest(BlockSelector activeSelector, out Vector3Int chestBlock)
    {
        chestBlock = default;
        if (activeSelector == null || !activeSelector.TryGetSelectedBlock(out chestBlock, out _))
            return false;

        return World.Instance != null &&
               !activeSelector.IsBillboardHit &&
               World.Instance.GetBlockAt(chestBlock) == BlockType.chest;
    }

    private void ApplyCraftingUiBlock(bool blocked)
    {
        if (CraftingMenuUI.Instance != null)
            CraftingMenuUI.Instance.SetExternalPanelBlocked(blocked);

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.SetExternalPlayerCraftPanelBlocked(blocked);
    }

    private void HideTemplateOnlyChildren(Transform slotRoot)
    {
        removeChildBuffer.Clear();
        CollectChildrenNamed(slotRoot, "SelectedImage", removeChildBuffer);
        for (int i = 0; i < removeChildBuffer.Count; i++)
            removeChildBuffer[i].gameObject.SetActive(false);
    }

    private static void CollectChildrenNamed(Transform current, string childName, List<Transform> output)
    {
        if (current == null)
            return;

        for (int i = 0; i < current.childCount; i++)
        {
            Transform child = current.GetChild(i);
            if (child.name == childName)
                output.Add(child);

            CollectChildrenNamed(child, childName, output);
        }
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

    private static int CompareSlotSiblingIndex(Slot a, Slot b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a == null)
            return 1;
        if (b == null)
            return -1;

        return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[ChestUIController] {message}");
    }

    private sealed class ChestInventoryData
    {
        public Item[] Items;
        public int[] Amounts;

        public ChestInventoryData(int size)
        {
            Items = new Item[Mathf.Max(1, size)];
            Amounts = new int[Items.Length];
        }

        public void EnsureSize(int size)
        {
            size = Mathf.Max(1, size);
            if (Items != null && Amounts != null && Items.Length == size && Amounts.Length == size)
                return;

            Item[] oldItems = Items;
            int[] oldAmounts = Amounts;
            Items = new Item[size];
            Amounts = new int[size];

            int copyCount = Mathf.Min(size, oldItems != null ? oldItems.Length : 0);
            for (int i = 0; i < copyCount; i++)
            {
                Items[i] = oldItems[i];
                Amounts[i] = oldAmounts != null && i < oldAmounts.Length ? oldAmounts[i] : 0;
            }
        }
    }
}
