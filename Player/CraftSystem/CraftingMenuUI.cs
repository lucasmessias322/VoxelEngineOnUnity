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
                owner.OnRecipeSelected(boundRecipe);
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

    private Recipe selectedRecipe;
    private PlayerInventory observedInventory;
    private bool? lastKnownInventoryOpen;
    private bool lastKnownCrafterOpen;
    private bool externalPanelBlocked;
    private bool recipeButtonPoolPrimed;
    private readonly List<RecipeButtonView> activeRecipeButtons = new List<RecipeButtonView>(32);
    private readonly Stack<RecipeButtonView> pooledRecipeButtons = new Stack<RecipeButtonView>(32);

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
        PrimeRecipeButtonPoolFromExistingChildren();
        RefreshAvailableRecipes();
        SyncPanelWithInventory(force: true);
    }

    private void Start()
    {
        SyncInventorySubscription();
        lastKnownCrafterOpen = IsCrafterOpen();
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
        List<Recipe> visibleRecipes = GetVisibleRecipes();
        PopulateRecipeList(visibleRecipes);
        EnsureRecipeSelection(visibleRecipes);
        UpdateRecipeDetails();
    }

    private void PopulateRecipeList(List<Recipe> visibleRecipes)
    {
        if (recipeListParent == null || recipeButtonPrefab == null)
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
        if (recipeButtonPoolPrimed || recipeListParent == null)
            return;

        recipeButtonPoolPrimed = true;

        for (int i = recipeListParent.childCount - 1; i >= 0; i--)
        {
            Transform child = recipeListParent.GetChild(i);
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
        while (pooledRecipeButtons.Count > 0)
        {
            RecipeButtonView pooledView = pooledRecipeButtons.Pop();
            if (pooledView == null)
                continue;

            pooledView.transform.SetParent(recipeListParent, false);
            pooledView.gameObject.SetActive(true);
            pooledView.Initialize(this);
            return pooledView;
        }

        GameObject buttonObject = Instantiate(recipeButtonPrefab, recipeListParent);
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

        view.transform.SetParent(recipeListParent, false);
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

    private void UpdateRecipeDetails()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (selectedRecipe != null &&
            craftingSystem != null &&
            !craftingSystem.IsRecipeAvailableInCurrentContext(selectedRecipe))
        {
            RefreshAvailableRecipes();
            return;
        }

        if (selectedRecipe == null || craftingSystem == null)
        {
            if (ItemName != null)
                ItemName.text = string.Empty;

            if (recipeDetailsText != null)
                recipeDetailsText.text = "Nenhuma receita disponivel nesse contexto.";

            if (craftButton != null)
            {
                craftButton.interactable = false;
                SetCraftButtonOpacity(0.5f);
            }

            return;
        }

        string resultName = GetItemDisplayName(selectedRecipe.resultItem);
        if (ItemName != null)
            ItemName.text = resultName;

        if (recipeDetailsText != null)
        {
            StringBuilder details = new StringBuilder();
            details.Append("Item: ").Append(resultName);

            if (selectedRecipe.resultQuantity > 1)
                details.Append(" x ").Append(selectedRecipe.resultQuantity);

            details.AppendLine();
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

            recipeDetailsText.text = details.ToString().TrimEnd();
        }

        bool canCraftNow = !craftingSystem.isCrafting && craftingSystem.CanCraft(selectedRecipe);
        if (craftButton != null)
        {
            craftButton.interactable = canCraftNow;
            SetCraftButtonOpacity(canCraftNow ? 1f : 0.5f);
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

    private IEnumerator DisableUntilCraftReady()
    {
        if (craftButton != null)
        {
            craftButton.interactable = false;
            SetCraftButtonOpacity(0.5f);
        }

        yield return new WaitUntil(() => CraftingSystem.Instance == null || !CraftingSystem.Instance.isCrafting);
        UpdateRecipeDetails();
    }

    private void SetCraftButtonOpacity(float alpha)
    {
        if (craftButton == null)
            return;

        Image image = craftButton.GetComponent<Image>();
        if (image == null)
            return;

        Color color = image.color;
        color.a = alpha;
        image.color = color;
    }

    public void ToggleCraftingPanel()
    {
        SetCraftingPanelState(!craftingPanelIsOpen);
    }

    public void SetCraftingPanelState(bool state)
    {
        craftingPanelIsOpen = state;

        if (craftingPanel != null)
            craftingPanel.SetActive(state);
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

    private static List<Recipe> GetVisibleRecipes()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (craftingSystem == null)
            return EmptyRecipeList;

        return craftingSystem.GetRecipesForCurrentContext();
    }
}
