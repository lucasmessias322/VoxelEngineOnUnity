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
    [Min(0.1f)] [SerializeField] private float woodFuelDuration = 7.5f;
    [Min(0.1f)] [SerializeField] private float coalFuelDuration = 40f;

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

        ProcessActiveRecipe(Time.deltaTime);
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

        activeFurnaceBlock = furnaceBlock;
        OpenFurnacePanel();
        return true;
    }

    public void CloseFurnacePanel()
    {
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

    private bool IsFuelItem(Item item)
    {
        return TryGetFuelDuration(item, out _);
    }

    private bool TryGetFuelDuration(Item item, out float fuelDuration)
    {
        fuelDuration = 0f;
        if (item == null)
            return false;

        if (inventory != null && inventory.TryGetBlockForItem(item, out BlockType blockType))
        {
            if (IsCoalFuelBlock(blockType))
            {
                fuelDuration = coalFuelDuration;
                return true;
            }

            if (IsWoodFuelBlock(blockType))
            {
                fuelDuration = woodFuelDuration;
                return true;
            }
        }

        string itemName = string.IsNullOrWhiteSpace(item.itemName) ? item.name : item.itemName;
        if (MatchesFuelKeyword(itemName, "coal", "charcoal", "carvao", "carvão"))
        {
            fuelDuration = coalFuelDuration;
            return true;
        }

        if (MatchesFuelKeyword(itemName, "log", "wood", "plank", "madeira", "tronco"))
        {
            fuelDuration = woodFuelDuration;
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

        RefreshRecipeState(forceResetProgress: false);
        RefreshSlotVisuals();
        return true;
    }

    private static bool IsWoodFuelBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log ||
               blockType == BlockType.oak_planks;
    }

    private static bool IsCoalFuelBlock(BlockType blockType)
    {
        return blockType == BlockType.CoalOre;
    }

    private static bool MatchesFuelKeyword(string value, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(value) || keywords == null)
            return false;

        string lowerValue = value.ToLowerInvariant();
        for (int i = 0; i < keywords.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(keywords[i]) &&
                lowerValue.Contains(keywords[i].ToLowerInvariant()))
                return true;
        }

        return false;
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[FurnaceUIController] {message}");
    }
}
