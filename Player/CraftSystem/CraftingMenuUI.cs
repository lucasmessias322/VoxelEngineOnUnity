using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public class CraftingMenuUI : MonoBehaviour
{
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
        SyncPanelWithInventory(force: true);
    }

    private void Start()
    {
        SyncInventorySubscription();
        PopulateRecipeList();
        EnsureRecipeSelection();
        SyncPanelWithInventory(force: true);
        UpdateRecipeDetails();
    }

    private void Update()
    {
        if (observedInventory != PlayerInventory.Instance)
            SyncInventorySubscription();

        SyncPanelWithInventory();
    }

    private void OnDisable()
    {
        UnsubscribeFromInventory();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        UnsubscribeFromInventory();
    }

    private void PopulateRecipeList()
    {
        if (recipeListParent == null || recipeButtonPrefab == null)
            return;

        foreach (Transform child in recipeListParent)
            Destroy(child.gameObject);

        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (craftingSystem == null || craftingSystem.craftRecipesSO == null || craftingSystem.craftRecipesSO.recipes == null)
            return;

        for (int i = 0; i < craftingSystem.craftRecipesSO.recipes.Count; i++)
        {
            Recipe recipe = craftingSystem.craftRecipesSO.recipes[i];
            if (recipe == null || recipe.resultItem == null)
                continue;

            GameObject buttonObject = Instantiate(recipeButtonPrefab, recipeListParent);
            Button button = buttonObject.GetComponent<Button>();
            // Text buttonText = buttonObject.GetComponentInChildren<Text>();

            // if (buttonText != null)
            //     buttonText.text = GetRecipeDisplayName(recipe);

            Image iconImage = buttonObject.transform.Find("ItemIcon")?.GetComponent<Image>();
            if (iconImage != null)
                iconImage.sprite = ResolveItemIcon(recipe.resultItem);

            if (button != null)
                button.onClick.AddListener(() => OnRecipeSelected(recipe));
        }
    }

    private void EnsureRecipeSelection()
    {
        if (selectedRecipe != null)
            return;

        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (craftingSystem == null || craftingSystem.craftRecipesSO == null || craftingSystem.craftRecipesSO.recipes == null)
            return;

        for (int i = 0; i < craftingSystem.craftRecipesSO.recipes.Count; i++)
        {
            Recipe recipe = craftingSystem.craftRecipesSO.recipes[i];
            if (recipe != null)
            {
                selectedRecipe = recipe;
                return;
            }
        }
    }

    private void OnRecipeSelected(Recipe recipe)
    {
        selectedRecipe = recipe;
        UpdateRecipeDetails();
    }

    private void UpdateRecipeDetails()
    {
        CraftingSystem craftingSystem = CraftingSystem.Instance;
        if (selectedRecipe == null || craftingSystem == null)
        {
            if (ItemName != null)
                ItemName.text = string.Empty;

            if (recipeDetailsText != null)
                recipeDetailsText.text = "Selecione uma receita.";

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

    public void HandleInventoryVisibilityChanged(bool isInventoryOpen)
    {
        lastKnownInventoryOpen = isInventoryOpen;
        SetCraftingPanelState(isInventoryOpen);
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
        SetCraftingPanelState(inventoryOpen);
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
}
