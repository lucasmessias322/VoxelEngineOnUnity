using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class FurnaceUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

    public static FurnaceUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;
    [SerializeField] private GameObject furnacePanel;
    [SerializeField] private Slot inputSlot;
    [SerializeField] private Slot fuelSlot;
    [SerializeField] private Slot outputSlot;
    [SerializeField] private Slider fuelLevelSlider;
    [SerializeField] private Image cookProgressFillImage;

    [Header("Recipes")]
    [SerializeField] private FurnaceRecipeSO[] recipes;

    [Header("Fuel")]
    [SerializeField] private FurnaceFuelEntry[] fuelItems;

    [Header("Behavior")]
    [SerializeField] private bool autoOpenInventoryWithFurnace = true;
    [SerializeField] private bool closeWhenInventoryCloses = true;
    [SerializeField] private bool closeCraftingTableWhenFurnaceOpens = true;
    [SerializeField] private bool rightClickOpensFurnace = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenFurnaceRemoved = true;
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;
    [SerializeField] private bool debugLogs;

    private FurnaceRecipeSO activeRecipe;
    private float cookTimer;
    private float burnTimer;
    private float currentFuelDuration;
    private bool suppressSlotCallbacks;
    private bool previousInventoryOpen;
    private bool lastKnownPanelOpen;
    private Vector3Int activeFurnaceBlock = InvalidBlock;
    private PlayerInventory subscribedInventory;
    private readonly Dictionary<Vector3Int, FurnaceInventoryData> furnaceStorage = new Dictionary<Vector3Int, FurnaceInventoryData>();

    public bool IsFurnaceOpen => furnacePanel != null && furnacePanel.activeSelf;
    public bool IsWorldFurnaceOpen => activeFurnaceBlock != InvalidBlock;
    public FurnaceRecipeSO ActiveRecipe => activeRecipe;
    public float CurrentProgress01 => activeRecipe == null ? 0f : Mathf.Clamp01(cookTimer / activeRecipe.CookDuration);
    public float CurrentFuel01 => currentFuelDuration <= 0f ? 0f : Mathf.Clamp01(burnTimer / currentFuelDuration);
    public bool HasFuelBurning => burnTimer > 0f;
    public bool HasValidRecipe => activeRecipe != null;
    public bool IsOutputBlocked => activeRecipe != null && !CanOutputAccept(activeRecipe);

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
        SyncInventorySlotSubscriptions();
        previousInventoryOpen = inventory != null && inventory.IsInventoryOpen;
        lastKnownPanelOpen = IsFurnaceOpen;
        HandleFurnacePanelStateChanged(lastKnownPanelOpen, force: true);
        RefreshRecipeState(forceResetProgress: true);
        RefreshSlotVisuals();
        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    private void OnValidate()
    {
        ResolveReferences();

        if (!Application.isPlaying)
        ConfigureSlots();
        RefreshSlotVisuals();
        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    private void Update()
    {
        ResolveReferences();
        SyncInventorySlotSubscriptions();

        bool panelOpen = IsFurnaceOpen;
        if (panelOpen != lastKnownPanelOpen)
        {
            lastKnownPanelOpen = panelOpen;
            HandleFurnacePanelStateChanged(panelOpen);
        }

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        if (previousInventoryOpen != inventoryOpen)
        {
            previousInventoryOpen = inventoryOpen;
            HandleInventoryVisibilityChanged(inventoryOpen);
        }

        if (IsFurnaceOpen && ShouldCloseFurnace())
            CloseFurnacePanel();

        if (IsWorldFurnaceOpen)
            SaveActiveFurnaceFromSlots();

        ProcessStoredFurnaces(Time.deltaTime);

        if (IsWorldFurnaceOpen)
        {
            FurnaceInventoryData activeData = GetOrCreateFurnaceData(activeFurnaceBlock);
            LoadFurnaceIntoSlots(activeData);
            SyncActiveStateFromData(activeData);
        }
        else if (IsFurnaceOpen)
        {
            ProcessActiveRecipe(Time.deltaTime);
        }
        else
        {
            SyncActiveStateFromData(null);
        }

        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    private void OnDestroy()
    {
        UnsubscribeSlots();
        UnsubscribeInventorySlotQuickTransfers();

        if (Instance == this)
            Instance = null;

        ApplyCraftingUiBlock(false);
    }

    public void Initialize(
        Slot inputSlotReference,
        Slot outputSlotReference,
        GameObject panelReference = null,
        FurnaceRecipeSO[] availableRecipes = null)
    {
        Initialize(inputSlotReference, null, outputSlotReference, panelReference, availableRecipes, null);
    }

    public void Initialize(
        Slot inputSlotReference,
        Slot fuelSlotReference,
        Slot outputSlotReference,
        GameObject panelReference = null,
        FurnaceRecipeSO[] availableRecipes = null,
        Slider fuelSliderReference = null)
    {
        UnsubscribeSlots();

        inputSlot = inputSlotReference;
        fuelSlot = fuelSlotReference;
        outputSlot = outputSlotReference;
        fuelLevelSlider = fuelSliderReference != null ? fuelSliderReference : fuelLevelSlider;

        if (panelReference != null)
            furnacePanel = panelReference;

        if (availableRecipes != null)
            recipes = availableRecipes;

        ConfigureSlots();
        SubscribeSlots();
        lastKnownPanelOpen = IsFurnaceOpen;
        RefreshRecipeState(forceResetProgress: true);
        RefreshSlotVisuals();
        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    public void SetFuelSlider(Slider slider)
    {
        fuelLevelSlider = slider;
        RefreshFuelUi();
    }

    public void SetRecipes(FurnaceRecipeSO[] availableRecipes)
    {
        recipes = availableRecipes ?? System.Array.Empty<FurnaceRecipeSO>();
        RefreshRecipeState(forceResetProgress: true);
    }

    public void OpenFurnacePanel()
    {
        if (furnacePanel == null)
        {
            Log("OpenFurnacePanel ignorado: furnacePanel nao configurado.");
            return;
        }

        furnacePanel.SetActive(true);
        lastKnownPanelOpen = true;
        HandleFurnacePanelStateChanged(true, force: true);
        RefreshSlotVisuals();
        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    public bool TryHandleFurnaceInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensFurnace || !Input.GetMouseButtonDown(1))
            return false;

        if (!TryGetTargetFurnace(activeSelector != null ? activeSelector : selector, out Vector3Int furnaceBlock))
            return false;

        if (IsWorldFurnaceOpen)
            SaveActiveFurnaceFromSlots();

        activeFurnaceBlock = furnaceBlock;
        FurnaceInventoryData data = GetOrCreateFurnaceData(furnaceBlock);
        RefreshFurnaceRecipeState(data);
        LoadFurnaceIntoSlots(data);
        SyncActiveStateFromData(data);
        OpenFurnacePanel();
        return true;
    }

    public void CloseFurnacePanel()
    {
        if (IsWorldFurnaceOpen)
            SaveActiveFurnaceFromSlots();

        if (furnacePanel != null)
            furnacePanel.SetActive(false);

        activeFurnaceBlock = InvalidBlock;
        lastKnownPanelOpen = false;
        HandleFurnacePanelStateChanged(false, force: true);
        RefreshFuelUi();
        RefreshCookProgressUi();
    }

    public void ToggleFurnacePanel()
    {
        if (IsFurnaceOpen)
            CloseFurnacePanel();
        else
            OpenFurnacePanel();
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        if (!inventoryOpen && closeWhenInventoryCloses && IsFurnaceOpen)
            CloseFurnacePanel();
    }

    public bool CanCookItem(Item item)
    {
        return FindRecipeForInput(item) != null;
    }

    public bool CanUseItemAsFuel(Item item)
    {
        return IsFuelItem(item);
    }

    public bool HasOutputStackInFurnace(Vector3Int furnaceBlock)
    {
        if (!IsFurnaceBlockInWorld(furnaceBlock))
            return false;

        if (IsWorldFurnaceOpen && activeFurnaceBlock == furnaceBlock)
            SaveActiveFurnaceFromSlots();

        if (!furnaceStorage.TryGetValue(furnaceBlock, out FurnaceInventoryData data) || data == null)
            return false;

        return data.OutputItem != null && data.OutputAmount > 0;
    }

    public bool TryTakeOutputStackFromFurnace(Vector3Int furnaceBlock, int maxAmount, out Item item, out int amount)
    {
        item = null;
        amount = 0;

        if (maxAmount <= 0 || !IsFurnaceBlockInWorld(furnaceBlock))
            return false;

        if (IsWorldFurnaceOpen && activeFurnaceBlock == furnaceBlock)
            SaveActiveFurnaceFromSlots();

        if (!furnaceStorage.TryGetValue(furnaceBlock, out FurnaceInventoryData data) || data == null)
            return false;

        if (data.OutputItem == null || data.OutputAmount <= 0)
            return false;

        item = data.OutputItem;
        amount = Mathf.Min(maxAmount, data.OutputAmount);
        RemoveFromFurnaceDataSlot(ref data.OutputItem, ref data.OutputAmount, amount);
        RefreshActiveFurnaceSlotsIfNeeded(furnaceBlock, data);
        return true;
    }

    public bool TryPeekOutputStackFromFurnace(Vector3Int furnaceBlock, int maxAmount, out Item item, out int amount)
    {
        item = null;
        amount = 0;

        if (maxAmount <= 0 || !IsFurnaceBlockInWorld(furnaceBlock))
            return false;

        if (IsWorldFurnaceOpen && activeFurnaceBlock == furnaceBlock)
            SaveActiveFurnaceFromSlots();

        if (!furnaceStorage.TryGetValue(furnaceBlock, out FurnaceInventoryData data) || data == null)
            return false;

        if (data.OutputItem == null || data.OutputAmount <= 0)
            return false;

        item = data.OutputItem;
        amount = Mathf.Min(maxAmount, data.OutputAmount);
        return true;
    }

    public int InsertItemStackIntoFurnace(Vector3Int furnaceBlock, Item item, int amount)
    {
        return InsertItemStackIntoFurnace(furnaceBlock, item, amount, int.MaxValue, int.MaxValue);
    }

    public int InsertItemStackIntoFurnace(
        Vector3Int furnaceBlock,
        Item item,
        int amount,
        int maxInputAmount,
        int maxFuelAmount)
    {
        if (item == null || amount <= 0 || !IsFurnaceBlockInWorld(furnaceBlock))
            return amount;

        if (IsWorldFurnaceOpen && activeFurnaceBlock == furnaceBlock)
            SaveActiveFurnaceFromSlots();

        FurnaceInventoryData data = GetOrCreateFurnaceData(furnaceBlock);
        int remaining = amount;
        bool isCookable = CanCookItem(item);

        if (isCookable)
            remaining = InsertIntoFurnaceDataSlot(ref data.InputItem, ref data.InputAmount, item, remaining, maxInputAmount);

        if ((!isCookable || remaining == amount) && CanUseItemAsFuel(item))
            remaining = InsertIntoFurnaceDataSlot(ref data.FuelItem, ref data.FuelAmount, item, remaining, maxFuelAmount);

        if (remaining != amount)
        {
            RefreshFurnaceRecipeState(data);
            RefreshActiveFurnaceSlotsIfNeeded(furnaceBlock, data);
        }

        return remaining;
    }

    public int GetInsertableItemCountForFurnace(
        Vector3Int furnaceBlock,
        Item item,
        int amount,
        int maxInputAmount,
        int maxFuelAmount)
    {
        if (item == null || amount <= 0 || !IsFurnaceBlockInWorld(furnaceBlock))
            return 0;

        if (IsWorldFurnaceOpen && activeFurnaceBlock == furnaceBlock)
            SaveActiveFurnaceFromSlots();

        FurnaceInventoryData data = GetOrCreateFurnaceData(furnaceBlock);
        int remaining = amount;
        bool isCookable = CanCookItem(item);

        if (isCookable)
            remaining = CountRemainingAfterFurnaceDataSlotInsert(data.InputItem, data.InputAmount, item, remaining, maxInputAmount);

        if ((!isCookable || remaining == amount) && CanUseItemAsFuel(item))
            remaining = CountRemainingAfterFurnaceDataSlotInsert(data.FuelItem, data.FuelAmount, item, remaining, maxFuelAmount);

        return amount - remaining;
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

    private void SyncInventorySlotSubscriptions()
    {
        if (subscribedInventory == inventory)
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
            if (slot == null)
                continue;

            slot.QuickTransferRequested -= HandleInventoryQuickTransferRequested;
        }

        subscribedInventory = null;
    }

    private void ConfigureSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SetRequireInventorySlotMapping(false);
            inputSlot.SetManualInteraction(true, true, true);
            inputSlot.RefreshUI();
        }

        if (fuelSlot != null)
        {
            fuelSlot.SetRequireInventorySlotMapping(false);
            fuelSlot.SetManualInteraction(true, true, true);
            fuelSlot.RefreshUI();
        }

        if (outputSlot != null)
        {
            outputSlot.SetRequireInventorySlotMapping(false);
            outputSlot.SetManualInteraction(true, false, false);
            outputSlot.RefreshUI();
        }
    }

    private void SubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged += HandleSlotChanged;
            inputSlot.QuickTransferRequested += HandleQuickTransferRequested;
            inputSlot.BeforePlaceIntoSlot += HandleInputSlotBeforePlace;
        }

        if (fuelSlot != null)
        {
            fuelSlot.SlotChanged += HandleSlotChanged;
            fuelSlot.QuickTransferRequested += HandleQuickTransferRequested;
            fuelSlot.BeforePlaceIntoSlot += HandleFuelSlotBeforePlace;
        }

        if (outputSlot != null)
        {
            outputSlot.SlotChanged += HandleSlotChanged;
            outputSlot.QuickTransferRequested += HandleQuickTransferRequested;
        }
    }

    private void UnsubscribeSlots()
    {
        if (inputSlot != null)
        {
            inputSlot.SlotChanged -= HandleSlotChanged;
            inputSlot.QuickTransferRequested -= HandleQuickTransferRequested;
            inputSlot.BeforePlaceIntoSlot -= HandleInputSlotBeforePlace;
        }

        if (fuelSlot != null)
        {
            fuelSlot.SlotChanged -= HandleSlotChanged;
            fuelSlot.QuickTransferRequested -= HandleQuickTransferRequested;
            fuelSlot.BeforePlaceIntoSlot -= HandleFuelSlotBeforePlace;
        }

        if (outputSlot != null)
        {
            outputSlot.SlotChanged -= HandleSlotChanged;
            outputSlot.QuickTransferRequested -= HandleQuickTransferRequested;
        }
    }

    private void HandleSlotChanged(Slot _)
    {
        if (suppressSlotCallbacks)
            return;

        if (IsWorldFurnaceOpen)
            RefreshActiveFurnaceStateAfterSlotMutation();
        else
            RefreshRecipeState();

        RefreshSlotVisuals();
    }

    private bool HandleQuickTransferRequested(Slot slot)
    {
        if (slot == null || inventory == null)
            return false;

        return inventory.TryMoveSlotToInventory(slot);
    }

    private bool HandleInputSlotBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        if (slot != inputSlot)
            return true;

        return incomingItem != null && FindRecipeForInput(incomingItem) != null;
    }

    private bool HandleFuelSlotBeforePlace(Slot slot, Item incomingItem, int incomingAmount)
    {
        if (slot != fuelSlot)
            return true;

        return incomingItem != null && IsFuelItem(incomingItem);
    }

    private bool HandleInventoryQuickTransferRequested(Slot slot)
    {
        if (!IsFurnaceOpen || slot == null || inventory == null || !inventory.ContainsSlot(slot))
            return false;

        return TryMoveInventorySlotToFurnace(slot);
    }

    private void HandleFurnacePanelStateChanged(bool isOpen, bool force = false)
    {
        if (isOpen)
        {
            ApplyCraftingUiBlock(true);

            if (closeCraftingTableWhenFurnaceOpens && CraftingStationUIController.Instance != null)
                CraftingStationUIController.Instance.CloseCrafterUI(returnItemsToInventory: true);

            if (autoOpenInventoryWithFurnace && inventory != null && !inventory.IsInventoryOpen)
                inventory.SetInventoryOpen(true);

            Log("Painel da fornalha aberto.");
            return;
        }

        ApplyCraftingUiBlock(false);
        Log("Painel da fornalha fechado.");
    }

    private void ApplyCraftingUiBlock(bool blocked)
    {
        if (CraftingMenuUI.Instance != null)
            CraftingMenuUI.Instance.SetExternalPanelBlocked(blocked);

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.SetExternalPlayerCraftPanelBlocked(blocked);
    }

    private bool TryGetTargetFurnace(BlockSelector activeSelector, out Vector3Int furnaceBlock)
    {
        furnaceBlock = default;
        if (activeSelector == null || !activeSelector.TryGetSelectedBlock(out furnaceBlock, out _))
            return false;

        return World.Instance != null &&
               !activeSelector.IsBillboardHit &&
               IsFurnaceBlock(World.Instance.GetBlockAt(furnaceBlock));
    }

    private static bool IsFurnaceBlockInWorld(Vector3Int furnaceBlock)
    {
        return World.Instance != null && IsFurnaceBlock(World.Instance.GetBlockAt(furnaceBlock));
    }

    private bool ShouldCloseFurnace()
    {
        if (!IsWorldFurnaceOpen)
            return false;

        if (closeWhenFurnaceRemoved && World.Instance != null && !IsFurnaceBlock(World.Instance.GetBlockAt(activeFurnaceBlock)))
            return true;

        if (!closeWhenTooFar || distanceOrigin == null)
            return false;

        Vector3 blockCenter = activeFurnaceBlock + Vector3.one * 0.5f;
        return (distanceOrigin.position - blockCenter).sqrMagnitude > maxInteractDistance * maxInteractDistance;
    }

    private void RefreshRecipeState(bool forceResetProgress = false)
    {
        FurnaceRecipeSO previousRecipe = activeRecipe;
        activeRecipe = FindMatchingRecipe();

        if (activeRecipe == null)
        {
            cookTimer = 0f;
            RefreshSlotVisuals();
            RefreshCookProgressUi();
            return;
        }

        if (forceResetProgress || activeRecipe != previousRecipe)
            cookTimer = 0f;

        RefreshSlotVisuals();
        RefreshCookProgressUi();
    }

    private void RefreshFurnaceRecipeState(FurnaceInventoryData data, bool forceResetProgress = false)
    {
        if (data == null)
            return;

        FurnaceRecipeSO previousRecipe = data.ActiveRecipe;
        data.ActiveRecipe = FindMatchingRecipe(data);

        if (data.ActiveRecipe == null)
        {
            data.CookTimer = 0f;
            return;
        }

        if (forceResetProgress || data.ActiveRecipe != previousRecipe)
            data.CookTimer = 0f;
    }

    private FurnaceRecipeSO FindMatchingRecipe()
    {
        if (inputSlot == null || inputSlot.IsEmpty || recipes == null || recipes.Length == 0)
            return null;

        Item inputItem = inputSlot.item;
        int inputAmount = inputSlot.amount;

        for (int i = 0; i < recipes.Length; i++)
        {
            FurnaceRecipeSO recipe = recipes[i];
            if (recipe == null || !recipe.IsValid)
                continue;

            if (recipe.InputItem != inputItem || inputAmount < recipe.InputAmount)
                continue;

            return recipe;
        }

        return null;
    }

    private FurnaceRecipeSO FindMatchingRecipe(FurnaceInventoryData data)
    {
        if (data == null || data.InputItem == null || data.InputAmount <= 0 || recipes == null || recipes.Length == 0)
            return null;

        for (int i = 0; i < recipes.Length; i++)
        {
            FurnaceRecipeSO recipe = recipes[i];
            if (recipe == null || !recipe.IsValid)
                continue;

            if (recipe.InputItem != data.InputItem || data.InputAmount < recipe.InputAmount)
                continue;

            return recipe;
        }

        return null;
    }

    private FurnaceRecipeSO FindRecipeForInput(Item inputItem)
    {
        if (inputItem == null || recipes == null || recipes.Length == 0)
            return null;

        for (int i = 0; i < recipes.Length; i++)
        {
            FurnaceRecipeSO recipe = recipes[i];
            if (recipe == null || !recipe.IsValid)
                continue;

            if (recipe.InputItem == inputItem)
                return recipe;
        }

        return null;
    }

    private void ProcessActiveRecipe(float deltaTime)
    {
        RefreshRecipeState();
        float remainingDelta = Mathf.Max(0f, deltaTime);

        if (burnTimer > 0f)
            remainingDelta = ConsumeBurnTime(remainingDelta);

        RefreshRecipeState();

        while (remainingDelta > 0f)
        {
            if (activeRecipe == null || !CanOutputAccept(activeRecipe))
                break;

            if (burnTimer <= 0f && !TryStartFuelBurn())
                break;

            remainingDelta = ConsumeBurnTime(remainingDelta);
            RefreshRecipeState();
        }
    }

    private void ProcessStoredFurnaces(float deltaTime)
    {
        if (furnaceStorage.Count == 0)
            return;

        foreach (KeyValuePair<Vector3Int, FurnaceInventoryData> pair in furnaceStorage)
        {
            if (!IsFurnaceBlockInWorld(pair.Key))
                continue;

            ProcessFurnaceData(pair.Value, deltaTime);
        }
    }

    private void ProcessFurnaceData(FurnaceInventoryData data, float deltaTime)
    {
        if (data == null)
            return;

        RefreshFurnaceRecipeState(data);
        float remainingDelta = Mathf.Max(0f, deltaTime);

        if (data.BurnTimer > 0f)
            remainingDelta = ConsumeBurnTime(data, remainingDelta);

        RefreshFurnaceRecipeState(data);

        while (remainingDelta > 0f)
        {
            if (data.ActiveRecipe == null || !CanOutputAccept(data, data.ActiveRecipe))
                break;

            if (data.BurnTimer <= 0f && !TryStartFuelBurn(data))
                break;

            remainingDelta = ConsumeBurnTime(data, remainingDelta);
            RefreshFurnaceRecipeState(data);
        }
    }

    private bool TryCompleteRecipe(FurnaceRecipeSO recipe)
    {
        if (!CanConsumeInput(recipe) || !CanOutputAccept(recipe))
            return false;

        suppressSlotCallbacks = true;
        try
        {
            int removed = inputSlot.Remove(recipe.InputAmount);
            if (removed != recipe.InputAmount)
                return false;

            int remaining = outputSlot.Add(recipe.OutputItem, recipe.OutputAmount);
            if (remaining <= 0)
                return true;

            int inserted = recipe.OutputAmount - remaining;
            if (inserted > 0)
                outputSlot.Remove(inserted);

            inputSlot.Add(recipe.InputItem, removed);
            Log($"Falha ao finalizar receita da fornalha para {recipe.OutputItem.name}.");
            return false;
        }
        finally
        {
            suppressSlotCallbacks = false;
        }
    }

    private bool TryCompleteRecipe(FurnaceInventoryData data, FurnaceRecipeSO recipe)
    {
        if (!CanConsumeInput(data, recipe) || !CanOutputAccept(data, recipe))
            return false;

        int removed = RemoveFromFurnaceDataSlot(ref data.InputItem, ref data.InputAmount, recipe.InputAmount);
        if (removed != recipe.InputAmount)
            return false;

        int remaining = InsertIntoFurnaceDataSlot(ref data.OutputItem, ref data.OutputAmount, recipe.OutputItem, recipe.OutputAmount);
        if (remaining <= 0)
            return true;

        int inserted = recipe.OutputAmount - remaining;
        if (inserted > 0)
            RemoveFromFurnaceDataSlot(ref data.OutputItem, ref data.OutputAmount, inserted);

        InsertIntoFurnaceDataSlot(ref data.InputItem, ref data.InputAmount, recipe.InputItem, removed);
        Log($"Falha ao finalizar receita da fornalha para {recipe.OutputItem.name}.");
        return false;
    }

    private bool TryMoveInventorySlotToFurnace(Slot sourceSlot)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty)
            return false;

        Item sourceItem = sourceSlot.item;
        if (FindRecipeForInput(sourceItem) != null &&
            TryMoveInventorySlotToSpecificTarget(sourceSlot, inputSlot, sourceItem))
            return true;

        if (IsFuelItem(sourceItem) &&
            TryMoveInventorySlotToSpecificTarget(sourceSlot, fuelSlot, sourceItem))
            return true;

        return false;
    }

    private bool CanConsumeInput(FurnaceRecipeSO recipe)
    {
        return recipe != null &&
               inputSlot != null &&
               !inputSlot.IsEmpty &&
               inputSlot.item == recipe.InputItem &&
               inputSlot.amount >= recipe.InputAmount;
    }

    private bool CanConsumeInput(FurnaceInventoryData data, FurnaceRecipeSO recipe)
    {
        return data != null &&
               recipe != null &&
               data.InputItem == recipe.InputItem &&
               data.InputAmount >= recipe.InputAmount;
    }

    private bool CanOutputAccept(FurnaceRecipeSO recipe)
    {
        if (recipe == null || recipe.OutputItem == null || outputSlot == null)
            return false;

        int stackLimit = Mathf.Max(1, recipe.OutputItem.maxStack);
        if (outputSlot.IsEmpty)
            return recipe.OutputAmount <= stackLimit;

        if (outputSlot.item != recipe.OutputItem)
            return false;

        return outputSlot.amount + recipe.OutputAmount <= stackLimit;
    }

    private bool CanOutputAccept(FurnaceInventoryData data, FurnaceRecipeSO recipe)
    {
        if (data == null || recipe == null || recipe.OutputItem == null)
            return false;

        int stackLimit = Mathf.Max(1, recipe.OutputItem.maxStack);
        if (data.OutputItem == null || data.OutputAmount <= 0)
            return recipe.OutputAmount <= stackLimit;

        if (data.OutputItem != recipe.OutputItem)
            return false;

        return data.OutputAmount + recipe.OutputAmount <= stackLimit;
    }

    private static bool IsFurnaceBlock(BlockType blockType)
    {
        return blockType == BlockType.StoneFurnance;
    }

    private void RefreshSlotVisuals()
    {
        if (inputSlot != null)
            inputSlot.RefreshUI();

        if (fuelSlot != null)
            fuelSlot.RefreshUI();

        if (outputSlot != null)
            outputSlot.RefreshUI();
    }

    private void RefreshFuelUi()
    {
        if (fuelLevelSlider == null)
            return;

        fuelLevelSlider.minValue = 0f;
        fuelLevelSlider.maxValue = 1f;
        fuelLevelSlider.wholeNumbers = false;
        fuelLevelSlider.value = CurrentFuel01;
    }

    private void RefreshCookProgressUi()
    {
        if (cookProgressFillImage == null)
            return;

        cookProgressFillImage.fillAmount = CurrentProgress01;
    }

    private float ConsumeBurnTime(float deltaTime)
    {
        if (burnTimer <= 0f || deltaTime <= 0f)
            return 0f;

        float burnNow = Mathf.Min(burnTimer, deltaTime);
        burnTimer -= burnNow;

        if (activeRecipe != null && CanOutputAccept(activeRecipe))
            AdvanceCooking(burnNow);

        return deltaTime - burnNow;
    }

    private void AdvanceCooking(float deltaTime)
    {
        if (deltaTime <= 0f || activeRecipe == null || !CanOutputAccept(activeRecipe))
            return;

        cookTimer += deltaTime;

        while (activeRecipe != null && cookTimer >= activeRecipe.CookDuration)
        {
            FurnaceRecipeSO recipeToComplete = activeRecipe;
            if (!TryCompleteRecipe(recipeToComplete))
                return;

            cookTimer -= recipeToComplete.CookDuration;
            RefreshRecipeState();

            if (activeRecipe == null)
            {
                cookTimer = 0f;
                RefreshSlotVisuals();
                return;
            }

            if (activeRecipe != recipeToComplete)
            {
                cookTimer = 0f;
                return;
            }

            if (!CanOutputAccept(activeRecipe))
                return;
        }
    }

    private float ConsumeBurnTime(FurnaceInventoryData data, float deltaTime)
    {
        if (data == null || data.BurnTimer <= 0f || deltaTime <= 0f)
            return 0f;

        float burnNow = Mathf.Min(data.BurnTimer, deltaTime);
        data.BurnTimer -= burnNow;

        if (data.ActiveRecipe != null && CanOutputAccept(data, data.ActiveRecipe))
            AdvanceCooking(data, burnNow);

        return deltaTime - burnNow;
    }

    private void AdvanceCooking(FurnaceInventoryData data, float deltaTime)
    {
        if (data == null || deltaTime <= 0f || data.ActiveRecipe == null || !CanOutputAccept(data, data.ActiveRecipe))
            return;

        data.CookTimer += deltaTime;

        while (data.ActiveRecipe != null && data.CookTimer >= data.ActiveRecipe.CookDuration)
        {
            FurnaceRecipeSO recipeToComplete = data.ActiveRecipe;
            if (!TryCompleteRecipe(data, recipeToComplete))
                return;

            data.CookTimer -= recipeToComplete.CookDuration;
            RefreshFurnaceRecipeState(data);

            if (data.ActiveRecipe == null)
            {
                data.CookTimer = 0f;
                return;
            }

            if (data.ActiveRecipe != recipeToComplete)
            {
                data.CookTimer = 0f;
                return;
            }

            if (!CanOutputAccept(data, data.ActiveRecipe))
                return;
        }
    }

    private bool TryStartFuelBurn()
    {
        if (fuelSlot == null || fuelSlot.IsEmpty)
            return false;

        if (!TryGetFuelDuration(fuelSlot.item, out float fuelDuration))
            return false;

        int removed = fuelSlot.Remove(1);
        if (removed <= 0)
            return false;

        currentFuelDuration = fuelDuration;
        burnTimer = fuelDuration;
        return true;
    }

    private bool TryStartFuelBurn(FurnaceInventoryData data)
    {
        if (data == null || data.FuelItem == null || data.FuelAmount <= 0)
            return false;

        if (!TryGetFuelDuration(data.FuelItem, out float fuelDuration))
            return false;

        int removed = RemoveFromFurnaceDataSlot(ref data.FuelItem, ref data.FuelAmount, 1);
        if (removed <= 0)
            return false;

        data.CurrentFuelDuration = fuelDuration;
        data.BurnTimer = fuelDuration;
        return true;
    }

    private bool IsFuelItem(Item item)
    {
        return TryGetFuelDuration(item, out _);
    }

    private bool TryGetFuelDuration(Item item, out float fuelDuration)
    {
        fuelDuration = 0f;
        if (item == null || fuelItems == null || fuelItems.Length == 0)
            return false;

        for (int i = 0; i < fuelItems.Length; i++)
        {
            FurnaceFuelEntry fuelEntry = fuelItems[i];
            if (fuelEntry == null || fuelEntry.Item != item)
                continue;

            fuelDuration = Mathf.Max(0.1f, fuelEntry.BurnDuration);
            return true;
        }

        return false;
    }

    private bool TryMoveInventorySlotToSpecificTarget(Slot sourceSlot, Slot targetSlot, Item sourceItem)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty || targetSlot == null || sourceItem == null)
            return false;

        if (!targetSlot.IsEmpty && targetSlot.item != sourceItem)
            return false;

        int originalAmount = sourceSlot.amount;
        int remaining = targetSlot.Add(sourceItem, originalAmount);
        int moved = originalAmount - remaining;
        if (moved <= 0)
            return false;

        if (remaining <= 0)
            sourceSlot.Clear();
        else
            sourceSlot.SetContents(sourceItem, remaining);

        if (IsWorldFurnaceOpen)
            RefreshActiveFurnaceStateAfterSlotMutation();
        else
            RefreshRecipeState(forceResetProgress: false);

        RefreshSlotVisuals();
        return true;
    }

    private FurnaceInventoryData GetOrCreateFurnaceData(Vector3Int furnaceBlock)
    {
        if (!furnaceStorage.TryGetValue(furnaceBlock, out FurnaceInventoryData data) || data == null)
        {
            data = new FurnaceInventoryData();
            furnaceStorage[furnaceBlock] = data;
        }

        return data;
    }

    private void SaveActiveFurnaceFromSlots()
    {
        if (!IsWorldFurnaceOpen)
            return;

        FurnaceInventoryData data = GetOrCreateFurnaceData(activeFurnaceBlock);
        CopySlotToData(inputSlot, out data.InputItem, out data.InputAmount);
        CopySlotToData(fuelSlot, out data.FuelItem, out data.FuelAmount);
        CopySlotToData(outputSlot, out data.OutputItem, out data.OutputAmount);
        data.CookTimer = cookTimer;
        data.BurnTimer = burnTimer;
        data.CurrentFuelDuration = currentFuelDuration;
        data.ActiveRecipe = activeRecipe;
    }

    private void LoadFurnaceIntoSlots(FurnaceInventoryData data)
    {
        if (data == null)
            return;

        suppressSlotCallbacks = true;
        try
        {
            SetSlotContents(inputSlot, data.InputItem, data.InputAmount);
            SetSlotContents(fuelSlot, data.FuelItem, data.FuelAmount);
            SetSlotContents(outputSlot, data.OutputItem, data.OutputAmount);
        }
        finally
        {
            suppressSlotCallbacks = false;
        }
    }

    private void RefreshActiveFurnaceSlotsIfNeeded(Vector3Int furnaceBlock, FurnaceInventoryData data)
    {
        if (!IsWorldFurnaceOpen || activeFurnaceBlock != furnaceBlock)
            return;

        LoadFurnaceIntoSlots(data);
        SyncActiveStateFromData(data);
        RefreshSlotVisuals();
    }

    private void RefreshActiveFurnaceStateAfterSlotMutation()
    {
        if (!IsWorldFurnaceOpen)
            return;

        SaveActiveFurnaceFromSlots();
        FurnaceInventoryData data = GetOrCreateFurnaceData(activeFurnaceBlock);
        RefreshFurnaceRecipeState(data);
        SyncActiveStateFromData(data);
    }

    private void SyncActiveStateFromData(FurnaceInventoryData data)
    {
        activeRecipe = data != null ? data.ActiveRecipe : null;
        cookTimer = data != null ? data.CookTimer : 0f;
        burnTimer = data != null ? data.BurnTimer : 0f;
        currentFuelDuration = data != null ? data.CurrentFuelDuration : 0f;
    }

    private static void CopySlotToData(Slot slot, out Item item, out int amount)
    {
        if (slot == null || slot.IsEmpty)
        {
            item = null;
            amount = 0;
            return;
        }

        item = slot.item;
        amount = Mathf.Max(0, slot.amount);
    }

    private static void SetSlotContents(Slot slot, Item item, int amount)
    {
        if (slot == null)
            return;

        if (item != null && amount > 0)
            slot.SetContents(item, amount);
        else
            slot.Clear();
    }

    private static int InsertIntoFurnaceDataSlot(
        ref Item slotItem,
        ref int slotAmount,
        Item item,
        int amount,
        int maxSlotAmount = int.MaxValue)
    {
        if (item == null || amount <= 0)
            return amount;

        int stackLimit = ResolveFurnaceSlotLimit(item, maxSlotAmount);
        if (slotItem == null || slotAmount <= 0)
        {
            int moved = Mathf.Min(stackLimit, amount);
            slotItem = item;
            slotAmount = moved;
            return amount - moved;
        }

        if (slotItem != item)
            return amount;

        int freeSpace = Mathf.Max(0, stackLimit - slotAmount);
        int addNow = Mathf.Min(freeSpace, amount);
        if (addNow <= 0)
            return amount;

        slotAmount += addNow;
        return amount - addNow;
    }

    private static int CountRemainingAfterFurnaceDataSlotInsert(
        Item slotItem,
        int slotAmount,
        Item item,
        int amount,
        int maxSlotAmount)
    {
        if (item == null || amount <= 0)
            return amount;

        int stackLimit = ResolveFurnaceSlotLimit(item, maxSlotAmount);
        if (slotItem == null || slotAmount <= 0)
            return amount - Mathf.Min(stackLimit, amount);

        if (slotItem != item)
            return amount;

        int freeSpace = Mathf.Max(0, stackLimit - slotAmount);
        return amount - Mathf.Min(freeSpace, amount);
    }

    private static int ResolveFurnaceSlotLimit(Item item, int maxSlotAmount)
    {
        int itemStackLimit = item != null ? Mathf.Max(1, item.maxStack) : 1;
        int configuredLimit = maxSlotAmount > 0 ? maxSlotAmount : itemStackLimit;
        return Mathf.Max(1, Mathf.Min(itemStackLimit, configuredLimit));
    }

    private static int RemoveFromFurnaceDataSlot(ref Item slotItem, ref int slotAmount, int amountToRemove)
    {
        if (amountToRemove <= 0 || slotItem == null || slotAmount <= 0)
            return 0;

        int removed = Mathf.Min(amountToRemove, slotAmount);
        slotAmount -= removed;
        if (slotAmount <= 0)
        {
            slotItem = null;
            slotAmount = 0;
        }

        return removed;
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[FurnaceUIController] {message}");
    }

    [System.Serializable]
    private sealed class FurnaceFuelEntry
    {
        [SerializeField] private Item item;
        [Min(0.1f)] [SerializeField] private float burnDuration = 7.5f;

        public Item Item => item;
        public float BurnDuration => burnDuration;
    }

    private sealed class FurnaceInventoryData
    {
        public Item InputItem;
        public int InputAmount;
        public Item FuelItem;
        public int FuelAmount;
        public Item OutputItem;
        public int OutputAmount;
        public FurnaceRecipeSO ActiveRecipe;
        public float CookTimer;
        public float BurnTimer;
        public float CurrentFuelDuration;
    }
}
