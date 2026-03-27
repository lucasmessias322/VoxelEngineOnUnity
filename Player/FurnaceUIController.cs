using UnityEngine;

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
    [SerializeField] private Slot outputSlot;

    [Header("Recipes")]
    [SerializeField] private FurnaceRecipeSO[] recipes;

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
    private bool suppressSlotCallbacks;
    private bool previousInventoryOpen;
    private bool lastKnownPanelOpen;
    private Vector3Int activeFurnaceBlock = InvalidBlock;
    private PlayerInventory subscribedInventory;

    public bool IsFurnaceOpen => furnacePanel != null && furnacePanel.activeSelf;
    public bool IsWorldFurnaceOpen => activeFurnaceBlock != InvalidBlock;
    public FurnaceRecipeSO ActiveRecipe => activeRecipe;
    public float CurrentProgress01 => activeRecipe == null ? 0f : Mathf.Clamp01(cookTimer / activeRecipe.CookDuration);
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
    }

    private void OnValidate()
    {
        ResolveReferences();

        if (!Application.isPlaying)
        ConfigureSlots();
        RefreshSlotVisuals();
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
        UnsubscribeSlots();

        inputSlot = inputSlotReference;
        outputSlot = outputSlotReference;

        if (panelReference != null)
            furnacePanel = panelReference;

        if (availableRecipes != null)
            recipes = availableRecipes;

        ConfigureSlots();
        SubscribeSlots();
        lastKnownPanelOpen = IsFurnaceOpen;
        RefreshRecipeState(forceResetProgress: true);
        RefreshSlotVisuals();
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
            return;
        }

        if (forceResetProgress || activeRecipe != previousRecipe)
            cookTimer = 0f;

        RefreshSlotVisuals();
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
        if (activeRecipe == null || !CanOutputAccept(activeRecipe))
            return;

        cookTimer += deltaTime;
        float cookDuration = activeRecipe.CookDuration;

        while (cookTimer >= cookDuration)
        {
            FurnaceRecipeSO recipeToComplete = activeRecipe;
            if (!TryCompleteRecipe(recipeToComplete))
                return;

            cookTimer -= cookDuration;
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

            cookDuration = activeRecipe.CookDuration;
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
        if (sourceSlot == null || sourceSlot.IsEmpty || inputSlot == null)
            return false;

        Item sourceItem = sourceSlot.item;
        FurnaceRecipeSO sourceRecipe = FindRecipeForInput(sourceItem);
        if (sourceRecipe == null)
            return false;

        if (!inputSlot.IsEmpty && inputSlot.item != sourceItem)
            return false;

        int originalAmount = sourceSlot.amount;
        int remaining = inputSlot.Add(sourceItem, originalAmount);
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

        if (outputSlot != null)
            outputSlot.RefreshUI();
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[FurnaceUIController] {message}");
    }
}
