using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CraftingStationUIController : MonoBehaviour
{
    private static readonly Vector3Int InvalidBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

    public static CraftingStationUIController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private PlayerInventory inventory;
    [SerializeField] private BlockSelector selector;
    [SerializeField] private Transform distanceOrigin;

    [Header("Panels")]
    [SerializeField] private RectTransform playerCraftPanel;
    [SerializeField] private RectTransform craftingTablePanel;

    [Header("Craft References")]
    [SerializeField] private RectTransform playerCraftSlotsContainer;
    [SerializeField] private Slot playerCraftOutputSlot;
    [SerializeField] private RectTransform craftingTableSlotsContainer;
    [SerializeField] private Slot craftingTableOutputSlot;

    [Header("Recipes")]
    [SerializeField] private CraftingRecipe[] playerCraftRecipes;
    [SerializeField] private CraftingRecipe[] craftingTableRecipes;
    [SerializeField] private bool useResourcesRecipesAsFallback = true;

    [Header("Interaction")]
    [Min(0.5f)] [SerializeField] private float maxInteractDistance = 4f;
    [SerializeField] private bool rightClickOpensCrafter = true;
    [SerializeField] private bool closeWhenTooFar = true;
    [SerializeField] private bool closeWhenCrafterRemoved = true;
    [SerializeField] private bool debugLogs;

    private CraftingGridController playerCraftGrid;
    private CraftingGridController craftingTableGrid;
    private Vector3Int activeCrafterBlock = InvalidBlock;
    private bool previousInventoryOpen;
    private bool createdCraftingTablePanelAtRuntime;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        ResolveReferences();
        EnsurePanels();
        BuildCraftingControllers();
        previousInventoryOpen = inventory != null && inventory.IsInventoryOpen;
        RefreshPanelVisibility();
    }

    private void Update()
    {
        ResolveReferences();

        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;
        if (previousInventoryOpen != inventoryOpen)
            HandleInventoryVisibilityChanged(inventoryOpen);

        if (IsCraftingTableOpen && ShouldCloseCraftingTable())
            CloseCraftingTable(returnItemsToInventory: true);

        RefreshPanelVisibility();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public bool TryHandleCrafterInteraction(BlockSelector activeSelector)
    {
        if (!rightClickOpensCrafter || !Input.GetMouseButtonDown(1))
            return false;

        if (!TryGetTargetCrafter(activeSelector != null ? activeSelector : selector, out Vector3Int crafterBlock))
            return false;

        if (inventory != null)
            inventory.SetInventoryOpen(true);

        OpenCraftingTable(crafterBlock);
        return true;
    }

    public void HandleInventoryVisibilityChanged(bool inventoryOpen)
    {
        bool wasOpen = previousInventoryOpen;
        previousInventoryOpen = inventoryOpen;

        if (wasOpen && !inventoryOpen)
            HandleInventoryClosed();

        RefreshPanelVisibility();
    }

    private bool IsCraftingTableOpen => activeCrafterBlock != InvalidBlock;

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

    private void EnsurePanels()
    {
        if (playerCraftPanel == null)
        {
            playerCraftPanel = ResolveCraftPanel(playerCraftSlotsContainer, playerCraftOutputSlot) ??
                               FindRectTransformByName("PlayerCraft") ??
                               FindRectTransformByName("Craft");
        }

        playerCraftSlotsContainer = SanitizeSlotsContainerReference(playerCraftSlotsContainer, playerCraftPanel);
        playerCraftOutputSlot = SanitizeOutputReference(playerCraftOutputSlot, playerCraftPanel, playerCraftSlotsContainer);

        if (playerCraftSlotsContainer == null && playerCraftPanel != null)
            playerCraftSlotsContainer = FindChildRectTransformByName(playerCraftPanel, "slots");

        if (playerCraftOutputSlot == null)
            playerCraftOutputSlot = ResolveOutputSlot(playerCraftPanel, playerCraftSlotsContainer);

        if (craftingTablePanel == null)
        {
            craftingTablePanel = ResolveCraftPanel(craftingTableSlotsContainer, craftingTableOutputSlot) ??
                                 FindRectTransformByName("CraftingTable") ??
                                 FindRectTransformByName("PlayerCraft (1)");
        }

        craftingTableSlotsContainer = SanitizeSlotsContainerReference(craftingTableSlotsContainer, craftingTablePanel);
        craftingTableOutputSlot = SanitizeOutputReference(craftingTableOutputSlot, craftingTablePanel, craftingTableSlotsContainer);

        if (craftingTablePanel == null &&
            craftingTableSlotsContainer == null &&
            craftingTableOutputSlot == null &&
            playerCraftPanel != null)
        {
            craftingTablePanel = CreateCraftingTablePanel(playerCraftPanel);
            createdCraftingTablePanelAtRuntime = craftingTablePanel != null;
        }

        if (craftingTableSlotsContainer == null && craftingTablePanel != null)
            craftingTableSlotsContainer = FindChildRectTransformByName(craftingTablePanel, "slots");

        if (craftingTableOutputSlot == null)
            craftingTableOutputSlot = ResolveOutputSlot(craftingTablePanel, craftingTableSlotsContainer);
    }

    private void BuildCraftingControllers()
    {
        CraftingRecipe[] playerRecipes = ResolvePlayerRecipes();
        CraftingRecipe[] tableRecipes = ResolveCraftingTableRecipes();

        if (TryResolvePanelSlots(playerCraftPanel, playerCraftSlotsContainer, playerCraftOutputSlot, out Slot[] playerSlots, out Slot playerOutput))
        {
            playerCraftGrid = CreateRuntimeGridController("PlayerCraftGridController");
            playerCraftGrid.Initialize(2, 2, playerSlots, playerOutput, playerRecipes);
        }

        if (createdCraftingTablePanelAtRuntime && craftingTablePanel != null)
        {
            ConfigureCraftingTablePanelLayout(craftingTablePanel);

            if (craftingTableSlotsContainer == null)
                craftingTableSlotsContainer = FindChildRectTransformByName(craftingTablePanel, "slots");

            if (craftingTableOutputSlot == null)
                craftingTableOutputSlot = ResolveOutputSlot(craftingTablePanel, craftingTableSlotsContainer);
        }

        if (TryResolvePanelSlots(craftingTablePanel, craftingTableSlotsContainer, craftingTableOutputSlot, out Slot[] tableSlots, out Slot tableOutput))
        {
            craftingTableGrid = CreateRuntimeGridController("CraftingTableGridController");
            craftingTableGrid.Initialize(3, 3, tableSlots, tableOutput, tableRecipes);
        }
    }

    private void OpenCraftingTable(Vector3Int crafterBlock)
    {
        activeCrafterBlock = crafterBlock;
        craftingTableGrid?.RefreshCraftResult();
        RefreshPanelVisibility();
        Log($"Craft table aberta em {crafterBlock}.");
    }

    private void CloseCraftingTable(bool returnItemsToInventory)
    {
        if (returnItemsToInventory && inventory != null)
            craftingTableGrid?.ReturnIngredientsToInventory(inventory);

        activeCrafterBlock = InvalidBlock;
        craftingTableGrid?.RefreshCraftResult();
        RefreshPanelVisibility();
        Log("Craft table fechada.");
    }

    private void HandleInventoryClosed()
    {
        if (inventory != null)
            playerCraftGrid?.ReturnIngredientsToInventory(inventory);

        if (IsCraftingTableOpen)
            CloseCraftingTable(returnItemsToInventory: true);
    }

    private bool ShouldCloseCraftingTable()
    {
        if (!IsCraftingTableOpen)
            return false;

        if (closeWhenCrafterRemoved && World.Instance != null && World.Instance.GetBlockAt(activeCrafterBlock) != BlockType.Crafter)
            return true;

        if (!closeWhenTooFar || distanceOrigin == null)
            return false;

        Vector3 blockCenter = activeCrafterBlock + Vector3.one * 0.5f;
        return (distanceOrigin.position - blockCenter).sqrMagnitude > maxInteractDistance * maxInteractDistance;
    }

    private void RefreshPanelVisibility()
    {
        bool inventoryOpen = inventory != null && inventory.IsInventoryOpen;

        if (playerCraftPanel != null)
            playerCraftPanel.gameObject.SetActive(inventoryOpen && !IsCraftingTableOpen);

        if (craftingTablePanel != null)
            craftingTablePanel.gameObject.SetActive(inventoryOpen && IsCraftingTableOpen);
    }

    private bool TryGetTargetCrafter(BlockSelector activeSelector, out Vector3Int crafterBlock)
    {
        crafterBlock = default;
        if (activeSelector == null || !activeSelector.TryGetSelectedBlock(out crafterBlock, out _))
            return false;

        return World.Instance != null &&
               !activeSelector.IsBillboardHit &&
               World.Instance.GetBlockAt(crafterBlock) == BlockType.Crafter;
    }

    private static CraftingRecipe[] FilterRecipesByMaxSize(CraftingRecipe[] recipes, int maxWidth, int maxHeight)
    {
        if (recipes == null || recipes.Length == 0)
            return System.Array.Empty<CraftingRecipe>();

        List<CraftingRecipe> filtered = new List<CraftingRecipe>(recipes.Length);
        for (int i = 0; i < recipes.Length; i++)
        {
            CraftingRecipe recipe = recipes[i];
            if (recipe == null)
                continue;

            if (!TryGetOccupiedRecipeSize(recipe, out int occupiedWidth, out int occupiedHeight))
                continue;

            if (occupiedWidth <= maxWidth && occupiedHeight <= maxHeight)
                filtered.Add(recipe);
        }

        return filtered.ToArray();
    }

    private static bool TryGetOccupiedRecipeSize(CraftingRecipe recipe, out int occupiedWidth, out int occupiedHeight)
    {
        occupiedWidth = 0;
        occupiedHeight = 0;

        if (recipe == null || recipe.OutputItem == null)
            return false;

        bool hasIngredients = false;
        int minX = 0;
        int minY = 0;
        int maxX = 0;
        int maxY = 0;

        for (int y = 0; y < recipe.Height; y++)
        {
            for (int x = 0; x < recipe.Width; x++)
            {
                CraftingRecipe.IngredientSlot ingredient = recipe.GetIngredient(x, y);
                if (ingredient.IsEmpty)
                    continue;

                if (!hasIngredients)
                {
                    hasIngredients = true;
                    minX = maxX = x;
                    minY = maxY = y;
                    continue;
                }

                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
            }
        }

        if (!hasIngredients)
            return false;

        occupiedWidth = (maxX - minX) + 1;
        occupiedHeight = (maxY - minY) + 1;
        return true;
    }

    private CraftingRecipe[] ResolvePlayerRecipes()
    {
        if (HasConfiguredRecipes(playerCraftRecipes))
            return FilterRecipesByMaxSize(FilterNullRecipes(playerCraftRecipes), 2, 2);

        if (!useResourcesRecipesAsFallback)
            return System.Array.Empty<CraftingRecipe>();

        return FilterRecipesByMaxSize(Resources.LoadAll<CraftingRecipe>(string.Empty), 2, 2);
    }

    private CraftingRecipe[] ResolveCraftingTableRecipes()
    {
        if (HasConfiguredRecipes(craftingTableRecipes))
            return FilterNullRecipes(craftingTableRecipes);

        if (!useResourcesRecipesAsFallback)
            return System.Array.Empty<CraftingRecipe>();

        return Resources.LoadAll<CraftingRecipe>(string.Empty);
    }

    private static bool HasConfiguredRecipes(CraftingRecipe[] recipes)
    {
        if (recipes == null || recipes.Length == 0)
            return false;

        for (int i = 0; i < recipes.Length; i++)
        {
            if (recipes[i] != null)
                return true;
        }

        return false;
    }

    private static CraftingRecipe[] FilterNullRecipes(CraftingRecipe[] recipes)
    {
        if (recipes == null || recipes.Length == 0)
            return System.Array.Empty<CraftingRecipe>();

        List<CraftingRecipe> filtered = new List<CraftingRecipe>(recipes.Length);
        for (int i = 0; i < recipes.Length; i++)
        {
            if (recipes[i] != null)
                filtered.Add(recipes[i]);
        }

        return filtered.ToArray();
    }

    private CraftingGridController CreateRuntimeGridController(string controllerName)
    {
        Transform existing = transform.Find(controllerName);
        if (existing != null)
        {
            CraftingGridController existingController = existing.GetComponent<CraftingGridController>();
            if (existingController != null)
                return existingController;
        }

        GameObject controllerObject = new GameObject(controllerName);
        controllerObject.transform.SetParent(transform, false);
        return controllerObject.AddComponent<CraftingGridController>();
    }

    private static RectTransform FindRectTransformByName(string objectName)
    {
        RectTransform[] rectTransforms = Resources.FindObjectsOfTypeAll<RectTransform>();
        for (int i = 0; i < rectTransforms.Length; i++)
        {
            RectTransform rectTransform = rectTransforms[i];
            if (rectTransform == null || rectTransform.name != objectName)
                continue;

            if (!rectTransform.gameObject.scene.IsValid())
                continue;

            return rectTransform;
        }

        return null;
    }

    private static RectTransform FindChildRectTransformByName(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child as RectTransform;

            RectTransform nested = FindChildRectTransformByName(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static RectTransform ResolveCraftPanel(RectTransform slotsContainer, Slot outputSlot)
    {
        if (slotsContainer != null && slotsContainer.parent is RectTransform slotsParent)
            return slotsParent;

        if (outputSlot != null && outputSlot.transform.parent is RectTransform outputParent)
            return outputParent;

        return null;
    }

    private static Slot ResolveOutputSlot(RectTransform panel, RectTransform slotsContainer)
    {
        RectTransform searchRoot = panel;
        if (slotsContainer != null && slotsContainer.parent is RectTransform slotsParent)
            searchRoot = slotsParent;

        return FindOutputSlot(searchRoot, slotsContainer);
    }

    private static RectTransform SanitizeSlotsContainerReference(RectTransform slotsContainer, RectTransform panel)
    {
        if (slotsContainer == null)
            return null;

        if (panel == null)
            return slotsContainer;

        return slotsContainer == panel || slotsContainer.IsChildOf(panel)
            ? slotsContainer
            : null;
    }

    private static Slot SanitizeOutputReference(Slot outputSlot, RectTransform panel, RectTransform slotsContainer)
    {
        if (outputSlot == null)
            return null;

        Transform outputTransform = outputSlot.transform;
        if (outputTransform == null)
            return null;

        if (slotsContainer != null && outputTransform.IsChildOf(slotsContainer))
            return null;

        if (panel == null)
            return outputSlot;

        return outputTransform == panel || outputTransform.IsChildOf(panel)
            ? outputSlot
            : null;
    }

    private static RectTransform CreateCraftingTablePanel(RectTransform sourcePanel)
    {
        if (sourcePanel == null || sourcePanel.parent == null)
            return null;

        GameObject panelObject = Object.Instantiate(sourcePanel.gameObject, sourcePanel.parent, false);
        panelObject.name = "CraftingTable";

        RectTransform panel = panelObject.transform as RectTransform;
        RectTransform armorPanel = FindChildRectTransformByName(panel, "Armor");
        if (armorPanel != null)
            Object.Destroy(armorPanel.gameObject);

        panel.gameObject.SetActive(false);
        return panel;
    }

    private static void ConfigureCraftingTablePanelLayout(RectTransform panel)
    {
        if (panel == null)
            return;

        RectTransform slotsAndResult = FindChildRectTransformByName(panel, "Slotsandresult");
        RectTransform slotsContainer = FindChildRectTransformByName(panel, "slots");
        if (slotsAndResult == null || slotsContainer == null)
            return;

        Slot[] currentSlots = slotsContainer.GetComponentsInChildren<Slot>(true);
        if (currentSlots.Length == 0)
            return;

        Slot inputTemplate = currentSlots[0];
        while (slotsContainer.childCount < 9)
        {
            GameObject clone = Object.Instantiate(inputTemplate.gameObject, slotsContainer, false);
            clone.name = $"Slot ({slotsContainer.childCount})";
        }

        panel.sizeDelta = new Vector2(320f, 300f);

        slotsAndResult.anchorMin = new Vector2(0.5f, 0.5f);
        slotsAndResult.anchorMax = new Vector2(0.5f, 0.5f);
        slotsAndResult.anchoredPosition = Vector2.zero;
        slotsAndResult.sizeDelta = new Vector2(210f, 240f);

        GridLayoutGroup grid = slotsContainer.GetComponent<GridLayoutGroup>();
        if (grid != null)
        {
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
        }

        slotsContainer.anchorMin = new Vector2(0.5f, 0.5f);
        slotsContainer.anchorMax = new Vector2(0.5f, 0.5f);
        slotsContainer.anchoredPosition = new Vector2(-22.5f, 20f);
        slotsContainer.sizeDelta = new Vector2(160f, 160f);

        Slot outputSlot = FindOutputSlot(slotsAndResult, slotsContainer);
        if (outputSlot != null && outputSlot.transform is RectTransform outputRect)
        {
            outputRect.anchorMin = new Vector2(1f, 0.5f);
            outputRect.anchorMax = new Vector2(1f, 0.5f);
            outputRect.anchoredPosition = new Vector2(-25f, 0f);
            outputRect.sizeDelta = new Vector2(50f, 50f);
        }
    }

    private static bool TryResolvePanelSlots(RectTransform panel, RectTransform slotsContainerReference, Slot outputSlotReference, out Slot[] inputSlots, out Slot outputSlot)
    {
        inputSlots = System.Array.Empty<Slot>();
        outputSlot = SanitizeOutputReference(outputSlotReference, panel, slotsContainerReference);

        RectTransform slotsContainer = SanitizeSlotsContainerReference(slotsContainerReference, panel);
        if (slotsContainer == null && panel != null)
            slotsContainer = FindChildRectTransformByName(panel, "slots");

        if (slotsContainer == null)
            return false;

        Slot[] containerSlots = slotsContainer.GetComponentsInChildren<Slot>(true);
        if (containerSlots == null || containerSlots.Length == 0)
            return false;

        List<Slot> orderedSlots = new List<Slot>(containerSlots);
        orderedSlots.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        inputSlots = orderedSlots.ToArray();

        if (outputSlot == null)
            outputSlot = ResolveOutputSlot(panel, slotsContainer);

        return outputSlot != null;
    }

    private static Slot FindOutputSlot(RectTransform slotsAndResult, RectTransform slotsContainer)
    {
        if (slotsAndResult == null)
            return null;

        Slot[] allSlots = slotsAndResult.GetComponentsInChildren<Slot>(true);
        for (int i = 0; i < allSlots.Length; i++)
        {
            Slot slot = allSlots[i];
            if (slot == null)
                continue;

            if (slotsContainer != null && slot.transform.IsChildOf(slotsContainer))
                continue;

            return slot;
        }

        return null;
    }

    private void Log(string message)
    {
        if (debugLogs)
            Debug.Log($"[CraftingStationUIController] {message}");
    }
}
