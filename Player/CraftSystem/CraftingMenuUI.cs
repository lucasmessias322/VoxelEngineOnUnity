using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public class CraftingMenuUI : MonoBehaviour
{
    private static readonly List<Recipe> EmptyRecipeList = new List<Recipe>(0);

    private sealed class RecipeButtonView : MonoBehaviour
    {
        private Button cachedButton;
        private Image iconImage;
        private CraftingMenuUI owner;
        private Recipe boundRecipe;

        public Button Button => cachedButton;

        public void Initialize(CraftingMenuUI ownerMenu)
        {
            owner = ownerMenu;

            if (cachedButton == null)
                cachedButton = GetComponent<Button>();

            if (iconImage == null)
                iconImage = transform.Find("ItemIcon")?.GetComponent<Image>();

            if (cachedButton != null)
            {
                cachedButton.onClick.RemoveListener(HandleClick);
                cachedButton.onClick.AddListener(HandleClick);
            }
        }

        public void Bind(Recipe recipe)
        {
            boundRecipe = recipe;
            if (iconImage != null)
                iconImage.sprite = ResolveItemIcon(recipe != null ? recipe.resultItem : null);
        }

        public void Unbind()
        {
            boundRecipe = null;
            if (iconImage != null)
                iconImage.sprite = null;
        }

        private void HandleClick()
        {
            if (owner != null && boundRecipe != null)
                owner.HandleRecipeButtonClicked(boundRecipe);
        }

        private void OnDestroy()
        {
            if (cachedButton != null)
                cachedButton.onClick.RemoveListener(HandleClick);
        }
    }

    private const int MaxRecipeButtonPoolSize = 128;

    public static CraftingMenuUI Instance { get; private set; }

    [Header("UI References")]
    public GameObject craftingPanel;
    public bool craftingPanelIsOpen;
    public Transform recipeListParent;
    public GameObject recipeButtonPrefab;
    public Text ItemName;
    public Text recipeDetailsText;
    public Button craftButton;

    [Header("Creative Mode UI")]
    [SerializeField] private bool useSeparateCreativePanel = true;
    [SerializeField] private bool createCreativePanelAtRuntime = true;
    [SerializeField] private GameObject creativeCraftingPanel;
    [SerializeField] private Transform creativeRecipeListParent;
    [SerializeField] private GameObject creativeRecipeButtonPrefab;
    [SerializeField] private Text creativeItemName;
    [SerializeField] private Text creativeRecipeDetailsText;
    [SerializeField] private Button creativeTakeButton;
    [SerializeField] private Vector2 runtimeCreativePanelOffset = new Vector2(320f, 0f);
    [SerializeField] private string creativeButtonLabel = "Pegar";

    private Recipe selectedRecipe;
    private PlayerInventory observedInventory;
    private bool? lastKnownInventoryOpen;
    private bool lastKnownCrafterOpen;
    private bool lastKnownCreativeMode;
    private bool externalPanelBlocked;
    private readonly List<RecipeButtonView> activeRecipeButtons = new List<RecipeButtonView>(32);
    private readonly Stack<RecipeButtonView> pooledRecipeButtons = new Stack<RecipeButtonView>(32);
    private readonly HashSet<Transform> primedRecipeListParents = new HashSet<Transform>();
    private readonly Dictionary<Button, string> defaultCraftButtonLabels = new Dictionary<Button, string>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnEnable()
    {
        SyncInventorySubscription();
        lastKnownCrafterOpen = IsCrafterOpen();
        lastKnownCreativeMode = IsCreativeModeActive();
        EnsureCreativePanelIfNeeded();
        PrimeRecipeButtonPoolFromExistingChildren();
        RefreshAvailableRecipes();
        SyncPanelWithInventory(force: true);
    }

    private void Start()
    {
        SyncInventorySubscription();
        lastKnownCrafterOpen = IsCrafterOpen();
        lastKnownCreativeMode = IsCreativeModeActive();
        EnsureCreativePanelIfNeeded();
        PrimeRecipeButtonPoolFromExistingChildren();
        RefreshAvailableRecipes();
        SyncPanelWithInventory(force: true);
        UpdateRecipeDetails();
    }

    private void Update()
    {
        if (observedInventory != PlayerInventory.Instance)
            SyncInventorySubscription();

        bool crafterOpen = IsCrafterOpen();
        if (crafterOpen != lastKnownCrafterOpen)
        {
            lastKnownCrafterOpen = crafterOpen;
            RefreshAvailableRecipes();
        }

        bool creativeMode = IsCreativeModeActive();
        if (creativeMode != lastKnownCreativeMode)
        {
            lastKnownCreativeMode = creativeMode;
            EnsureCreativePanelIfNeeded();
            RefreshAvailableRecipes();
            SyncPanelWithInventory(force: true);
        }

        SyncPanelWithInventory();
    }

    private void OnDisable()
    {
        UnsubscribeFromInventory();
        ReleaseAllRecipeButtons();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        UnsubscribeFromInventory();
        ReleaseAllRecipeButtons();
    }

    public void RefreshAvailableRecipes()
    {
        EnsureCreativePanelIfNeeded();
        List<Recipe> visibleRecipes = GetVisibleRecipes();
        PopulateRecipeList(visibleRecipes);
        EnsureRecipeSelection(visibleRecipes);
        UpdateRecipeDetails();
    }

    private void PopulateRecipeList(List<Recipe> visibleRecipes)
    {
        Transform activeRecipeListParent = GetActiveRecipeListParent();
        GameObject activeRecipeButtonPrefab = GetActiveRecipeButtonPrefab();
        if (activeRecipeListParent == null || activeRecipeButtonPrefab == null)
            return;

        PrimeRecipeButtonPoolFromExistingChildren();
        ReleaseAllRecipeButtons();

        if (visibleRecipes == null || visibleRecipes.Count == 0)
            return;

        for (int i = 0; i < visibleRecipes.Count; i++)
        {
            Recipe recipe = visibleRecipes[i];
            if (recipe == null || recipe.resultItem == null)
                continue;

            RecipeButtonView buttonView = AcquireRecipeButton();
            if (buttonView == null || buttonView.Button == null)
                continue;

            buttonView.gameObject.name = $"Recipe_{GetRecipeDisplayName(recipe)}";
            buttonView.Bind(recipe);
            activeRecipeButtons.Add(buttonView);
        }
    }

    private void PrimeRecipeButtonPoolFromExistingChildren()
    {
        Transform activeRecipeListParent = GetActiveRecipeListParent();
        if (activeRecipeListParent == null || primedRecipeListParents.Contains(activeRecipeListParent))
            return;

        primedRecipeListParents.Add(activeRecipeListParent);

        for (int i = activeRecipeListParent.childCount - 1; i >= 0; i--)
        {
            Transform child = activeRecipeListParent.GetChild(i);
            if (child == null)
                continue;

            RecipeButtonView view = child.GetComponent<RecipeButtonView>();
            if (view == null)
            {
                if (child.GetComponent<Button>() == null)
                    continue;

                view = child.gameObject.AddComponent<RecipeButtonView>();
            }

            view.Initialize(this);
            ReturnRecipeButtonToPool(view);
        }
    }

    private RecipeButtonView AcquireRecipeButton()
    {
        Transform activeRecipeListParent = GetActiveRecipeListParent();
        if (activeRecipeListParent == null)
            return null;

        while (pooledRecipeButtons.Count > 0)
        {
            RecipeButtonView pooledView = pooledRecipeButtons.Pop();
            if (pooledView == null)
                continue;

            pooledView.transform.SetParent(activeRecipeListParent, false);
            pooledView.gameObject.SetActive(true);
            pooledView.Initialize(this);
            return pooledView;
        }

        GameObject buttonObject = Instantiate(GetActiveRecipeButtonPrefab(), activeRecipeListParent);
        RecipeButtonView view = buttonObject.GetComponent<RecipeButtonView>();
        if (view == null)
            view = buttonObject.AddComponent<RecipeButtonView>();
        view.Initialize(this);
        return view;
    }

    private void ReleaseAllRecipeButtons()
    {
        for (int i = activeRecipeButtons.Count - 1; i >= 0; i--)
            ReturnRecipeButtonToPool(activeRecipeButtons[i]);

        activeRecipeButtons.Clear();
    }

    private void ReturnRecipeButtonToPool(RecipeButtonView view)
    {
        if (view == null)
            return;

        view.Unbind();

        if (pooledRecipeButtons.Count >= MaxRecipeButtonPoolSize)
        {
            Destroy(view.gameObject);
            return;
        }

        Transform activeRecipeListParent = GetActiveRecipeListParent();
        if (activeRecipeListParent != null)
            view.transform.SetParent(activeRecipeListParent, false);

        view.gameObject.SetActive(false);
        pooledRecipeButtons.Push(view);
    }

    private void EnsureRecipeSelection(List<Recipe> visibleRecipes)
    {
        if (visibleRecipes != null)
        {
            for (int i = 0; i < visibleRecipes.Count; i++)
            {
                if (visibleRecipes[i] == selectedRecipe)
                    return;
            }
        }

        selectedRecipe = visibleRecipes != null && visibleRecipes.Count > 0
            ? visibleRecipes[0]
            : null;
    }

    private void OnRecipeSelected(Recipe recipe)
    {
        selectedRecipe = recipe;
        UpdateRecipeDetails();
    }

    private void HandleRecipeButtonClicked(Recipe recipe)
    {
        if (!IsCreativeModeActive())
        {
            OnRecipeSelected(recipe);
            return;
        }

        selectedRecipe = recipe;
        TakeCreativeRecipe(recipe);
    }

    private void UpdateRecipeDetails()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        bool creativeMode = IsCreativeModeActive();
        if (selectedRecipe != null &&
            craftingSystem != null &&
            !craftingSystem.IsRecipeAvailableInCurrentContext(selectedRecipe))
        {
            RefreshAvailableRecipes();
            return;
        }

        Text activeItemName = GetActiveItemNameText();
        Text activeDetailsText = GetActiveRecipeDetailsText();
        Button activeCraftButton = GetActiveCraftButton();

        if (selectedRecipe == null || craftingSystem == null)
        {
            if (activeItemName != null)
                activeItemName.text = string.Empty;

            if (activeDetailsText != null)
            {
                activeDetailsText.text = creativeMode
                    ? "Nenhum item criativo disponivel."
                    : "Nenhuma receita disponivel nesse contexto.";
            }

            if (activeCraftButton != null)
            {
                SetCraftButtonVisible(activeCraftButton, !creativeMode);
                activeCraftButton.interactable = false;
                SetCraftButtonOpacity(activeCraftButton, 0.5f);
                UpdateCraftButtonLabel(activeCraftButton, creativeMode);
            }

            return;
        }

        string resultName = GetItemDisplayName(selectedRecipe.resultItem);
        if (activeItemName != null)
            activeItemName.text = resultName;

        if (activeDetailsText != null)
        {
            StringBuilder details = new StringBuilder();
            details.Append("Item: ").Append(resultName);

            if (selectedRecipe.resultQuantity > 1)
                details.Append(" x ").Append(selectedRecipe.resultQuantity);

            details.AppendLine();
            if (creativeMode)
            {
                details.AppendLine("Modo criativo: sem custo.");
                details.AppendLine("Clique no item para pegar.");
                details.Append("Shift + clique pega o maximo que couber.");
            }
            else
            {
                details.AppendLine("Itens necessarios:");

                if (selectedRecipe.requiredItems != null && selectedRecipe.requiredItems.Count > 0)
                {
                    for (int i = 0; i < selectedRecipe.requiredItems.Count; i++)
                    {
                        RecipeItem recipeItem = selectedRecipe.requiredItems[i];
                        if (recipeItem == null || recipeItem.item == null || recipeItem.quantity <= 0)
                            continue;

                        details.Append("- ")
                               .Append(GetItemDisplayName(recipeItem.item))
                               .Append(" x ")
                               .Append(recipeItem.quantity)
                               .AppendLine();
                    }
                }
                else
                {
                    details.AppendLine("- Nenhum ingrediente");
                }
            }

            activeDetailsText.text = details.ToString().TrimEnd();
        }

        bool canCraftNow = !craftingSystem.isCrafting && craftingSystem.CanCraft(selectedRecipe);
        if (activeCraftButton != null)
        {
            SetCraftButtonVisible(activeCraftButton, !creativeMode);
            activeCraftButton.interactable = canCraftNow;
            SetCraftButtonOpacity(activeCraftButton, canCraftNow ? 1f : 0.5f);
            UpdateCraftButtonLabel(activeCraftButton, creativeMode);
        }
    }

    public void OnCraftButtonClicked()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (selectedRecipe == null || craftingSystem == null)
            return;

        if (craftingSystem.isCrafting)
            return;

        if (!craftingSystem.CanCraft(selectedRecipe))
            return;

        bool craftAll = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int craftCount = craftAll ? craftingSystem.GetMaxCraftAmount(selectedRecipe) : 1;
        craftingSystem.TryCraft(selectedRecipe, craftCount);
        UpdateRecipeDetails();

        if (craftingSystem.isCrafting)
            StartCoroutine(DisableUntilCraftReady());
    }

    private void TakeCreativeRecipe(Recipe recipe)
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (recipe == null || craftingSystem == null || !craftingSystem.CreativeModeEnabled)
            return;

        if (craftingSystem.isCrafting)
            return;

        if (!craftingSystem.CanCraft(recipe))
        {
            UpdateRecipeDetails();
            return;
        }

        bool takeAll = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int craftCount = takeAll ? craftingSystem.GetMaxCraftAmount(recipe) : 1;
        craftingSystem.TryCraft(recipe, craftCount);
        UpdateRecipeDetails();
    }

    private IEnumerator DisableUntilCraftReady()
    {
        Button activeCraftButton = GetActiveCraftButton();
        if (activeCraftButton != null)
        {
            activeCraftButton.interactable = false;
            SetCraftButtonOpacity(activeCraftButton, 0.5f);
        }

        yield return new WaitUntil(() => CraftingSystem.Instance == null || !CraftingSystem.Instance.isCrafting);
        UpdateRecipeDetails();
    }

    private void SetCraftButtonOpacity(Button button, float alpha)
    {
        if (button == null)
            return;

        Image image = button.GetComponent<Image>();
        if (image == null)
            return;

        Color color = image.color;
        color.a = alpha;
        image.color = color;
    }

    private static void SetCraftButtonVisible(Button button, bool visible)
    {
        if (button != null && button.gameObject.activeSelf != visible)
            button.gameObject.SetActive(visible);
    }

    private void UpdateCraftButtonLabel(Button button, bool creativeMode)
    {
        if (button == null)
            return;

        Text label = button.GetComponentInChildren<Text>(true);
        if (label == null)
            return;

        if (!defaultCraftButtonLabels.ContainsKey(button))
            defaultCraftButtonLabels.Add(button, label.text);

        label.text = creativeMode ? creativeButtonLabel : defaultCraftButtonLabels[button];
    }

    private void EnsureCreativePanelIfNeeded()
    {
        if (!useSeparateCreativePanel || !IsCreativeModeActive())
            return;

        if (creativeCraftingPanel == null && createCreativePanelAtRuntime && craftingPanel != null)
        {
            creativeCraftingPanel = Instantiate(craftingPanel, craftingPanel.transform.parent);
            creativeCraftingPanel.name = "CreativeCraftingPanel";

            if (creativeCraftingPanel.transform is RectTransform creativeRect &&
                craftingPanel.transform is RectTransform sourceRect)
            {
                creativeRect.anchoredPosition = sourceRect.anchoredPosition + runtimeCreativePanelOffset;
            }

            creativeCraftingPanel.SetActive(false);
        }

        ResolveCreativePanelReferences();
    }

    private void ResolveCreativePanelReferences()
    {
        if (creativeCraftingPanel == null)
            return;

        Transform sourceRoot = craftingPanel != null ? craftingPanel.transform : null;
        Transform creativeRoot = creativeCraftingPanel.transform;

        if (creativeRecipeListParent == null)
            creativeRecipeListParent = FindEquivalentTransform(sourceRoot, recipeListParent, creativeRoot);

        if (creativeItemName == null)
            creativeItemName = FindEquivalentComponent(sourceRoot, ItemName, creativeRoot);

        if (creativeRecipeDetailsText == null)
            creativeRecipeDetailsText = FindEquivalentComponent(sourceRoot, recipeDetailsText, creativeRoot);

        if (creativeTakeButton == null)
            creativeTakeButton = FindEquivalentComponent(sourceRoot, craftButton, creativeRoot);

        if (creativeRecipeButtonPrefab == null)
            creativeRecipeButtonPrefab = recipeButtonPrefab;
    }

    private GameObject GetActiveCraftingPanel()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeCraftingPanel != null)
            return creativeCraftingPanel;

        return craftingPanel;
    }

    private GameObject GetInactiveCraftingPanel(GameObject activePanel)
    {
        if (activePanel == creativeCraftingPanel)
            return craftingPanel;

        return creativeCraftingPanel;
    }

    private Transform GetActiveRecipeListParent()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeRecipeListParent != null)
            return creativeRecipeListParent;

        return recipeListParent;
    }

    private GameObject GetActiveRecipeButtonPrefab()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeRecipeButtonPrefab != null)
            return creativeRecipeButtonPrefab;

        return recipeButtonPrefab;
    }

    private Text GetActiveItemNameText()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeItemName != null)
            return creativeItemName;

        return ItemName;
    }

    private Text GetActiveRecipeDetailsText()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeRecipeDetailsText != null)
            return creativeRecipeDetailsText;

        return recipeDetailsText;
    }

    private Button GetActiveCraftButton()
    {
        if (IsCreativeModeActive() && useSeparateCreativePanel && creativeTakeButton != null)
            return creativeTakeButton;

        return craftButton;
    }

    private static Transform FindEquivalentTransform(Transform sourceRoot, Transform sourceTransform, Transform targetRoot)
    {
        if (sourceRoot == null || sourceTransform == null || targetRoot == null)
            return null;

        if (sourceTransform == sourceRoot)
            return targetRoot;

        List<int> siblingPath = new List<int>(8);
        Transform current = sourceTransform;
        while (current != null && current != sourceRoot)
        {
            siblingPath.Add(current.GetSiblingIndex());
            current = current.parent;
        }

        if (current != sourceRoot)
            return null;

        Transform resolved = targetRoot;
        for (int i = siblingPath.Count - 1; i >= 0; i--)
        {
            int childIndex = siblingPath[i];
            if (childIndex < 0 || childIndex >= resolved.childCount)
                return null;

            resolved = resolved.GetChild(childIndex);
        }

        return resolved;
    }

    private static T FindEquivalentComponent<T>(Transform sourceRoot, T sourceComponent, Transform targetRoot)
        where T : Component
    {
        if (sourceComponent == null)
            return null;

        Transform equivalent = FindEquivalentTransform(sourceRoot, sourceComponent.transform, targetRoot);
        return equivalent != null ? equivalent.GetComponent<T>() : null;
    }

    public void ToggleCraftingPanel()
    {
        SetCraftingPanelState(!craftingPanelIsOpen);
    }

    public void SetCraftingPanelState(bool state)
    {
        craftingPanelIsOpen = state;

        EnsureCreativePanelIfNeeded();

        GameObject activePanel = GetActiveCraftingPanel();
        GameObject inactivePanel = GetInactiveCraftingPanel(activePanel);

        if (activePanel != null)
            activePanel.SetActive(state);

        if (inactivePanel != null && inactivePanel != activePanel)
            inactivePanel.SetActive(false);
    }

    public void CloseCraftingPanel()
    {
        SetCraftingPanelState(false);
    }

    public void SetExternalPanelBlocked(bool blocked)
    {
        if (externalPanelBlocked == blocked)
            return;

        externalPanelBlocked = blocked;
        if (blocked)
        {
            CloseCraftingPanel();
            return;
        }

        SyncPanelWithInventory(force: true);
    }

    public void HandleInventoryVisibilityChanged(bool isInventoryOpen)
    {
        lastKnownInventoryOpen = isInventoryOpen;
        SetCraftingPanelState(isInventoryOpen && !externalPanelBlocked);
        RefreshAvailableRecipes();
    }

    private void SyncInventorySubscription()
    {
        if (observedInventory == PlayerInventory.Instance)
            return;

        UnsubscribeFromInventory();
        observedInventory = PlayerInventory.Instance;

        if (observedInventory != null)
            observedInventory.ContentsChanged += HandleInventoryContentsChanged;
    }

    private void UnsubscribeFromInventory()
    {
        if (observedInventory == null)
            return;

        observedInventory.ContentsChanged -= HandleInventoryContentsChanged;
        observedInventory = null;
    }

    private void HandleInventoryContentsChanged()
    {
        UpdateRecipeDetails();
    }

    private void SyncPanelWithInventory(bool force = false)
    {
        bool inventoryOpen = observedInventory != null && observedInventory.IsInventoryOpen;
        if (!force && lastKnownInventoryOpen.HasValue && lastKnownInventoryOpen.Value == inventoryOpen)
            return;

        lastKnownInventoryOpen = inventoryOpen;
        SetCraftingPanelState(inventoryOpen && !externalPanelBlocked);
    }

    private static string GetRecipeDisplayName(Recipe recipe)
    {
        if (recipe == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(recipe.recipeName))
            return recipe.recipeName;

        return GetItemDisplayName(recipe.resultItem);
    }

    private static string GetItemDisplayName(Item item)
    {
        if (item == null)
            return "Item";

        return string.IsNullOrWhiteSpace(item.itemName) ? item.name : item.itemName;
    }

    private static Sprite ResolveItemIcon(Item item)
    {
        return ItemIconResolver.ResolveForUI(item);
    }

    private static bool IsCrafterOpen()
    {
        return CraftingStationUIController.Instance != null &&
               CraftingStationUIController.Instance.IsCrafterOpen;
    }

    private static bool IsCreativeModeActive()
    {
        return CraftingSystem.Instance != null && CraftingSystem.Instance.CreativeModeEnabled;
    }

    private static List<Recipe> GetVisibleRecipes()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (craftingSystem == null)
            return EmptyRecipeList;

        return craftingSystem.GetRecipesForCurrentContext();
    }
}
