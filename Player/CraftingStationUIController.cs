using UnityEngine;

public class CraftingStationUIController : MonoBehaviour
{
    private struct OccupiedBounds
    {
        public bool hasItems;
        public int minX;
        public int minY;
        public int maxX;
        public int maxY;

        public int Width => hasItems ? (maxX - minX) + 1 : 0;
        public int Height => hasItems ? (maxY - minY) + 1 : 0;
    }

    private struct RecipeMatch
    {
        public CraftingRecipe recipe;
        public bool mirrored;
        public OccupiedBounds inputBounds;
        public OccupiedBounds recipeBounds;
    }

    [Header("Grid")]
    [Min(1)] [SerializeField] private int gridWidth = 3;
    [Min(1)] [SerializeField] private int gridHeight = 3;
    [SerializeField] private Slot[] craftingSlots;
    [SerializeField] private Slot outputSlot;

    [Header("Recipes")]
    [SerializeField] private CraftingRecipe[] recipes;
    [SerializeField] private bool autoConfigureSlotsOnAwake = true;

    private CraftingRecipe activeRecipe;
    private bool activeRecipeMirrored;
    private bool suppressCraftingRefresh;
    private bool refreshAfterOutputTake;

    private void Awake()
    {
        if (autoConfigureSlotsOnAwake)
            ConfigureSlots();

        RefreshCraftResult();
    }

    private void OnEnable()
    {
        SubscribeSlots();
        RefreshCraftResult();
    }

    private void OnDisable()
    {
        UnsubscribeSlots();
        ClearOutputSlot();
    }

    private void OnValidate()
    {
        gridWidth = Mathf.Max(1, gridWidth);
        gridHeight = Mathf.Max(1, gridHeight);
    }

    [ContextMenu("Configure Crafting Slots")]
    public void ConfigureSlots()
    {
        if (craftingSlots != null)
        {
            for (int i = 0; i < craftingSlots.Length; i++)
            {
                Slot slot = craftingSlots[i];
                if (slot == null)
                    continue;

                slot.SetRequireInventorySlotMapping(false);
                slot.SetManualInteraction(true, true, true);
            }
        }

        if (outputSlot != null)
        {
            outputSlot.SetRequireInventorySlotMapping(false);
            outputSlot.SetManualInteraction(true, false, false);
        }
    }

    [ContextMenu("Refresh Craft Result")]
    public void RefreshCraftResult()
    {
        if (!TryFindMatchingRecipe(out RecipeMatch match))
        {
            activeRecipe = null;
            activeRecipeMirrored = false;
            ClearOutputSlot();
            return;
        }

        activeRecipe = match.recipe;
        activeRecipeMirrored = match.mirrored;
        SetOutputSlot(match.recipe.OutputItem, match.recipe.OutputAmount);
    }

    private void SubscribeSlots()
    {
        if (craftingSlots != null)
        {
            for (int i = 0; i < craftingSlots.Length; i++)
            {
                if (craftingSlots[i] != null)
                    craftingSlots[i].SlotChanged += HandleCraftingSlotChanged;
            }
        }

        if (outputSlot != null)
        {
            outputSlot.BeforeTakeFromSlot += HandleOutputBeforeTake;
            outputSlot.AfterTakeFromSlot += HandleOutputAfterTake;
        }
    }

    private void UnsubscribeSlots()
    {
        if (craftingSlots != null)
        {
            for (int i = 0; i < craftingSlots.Length; i++)
            {
                if (craftingSlots[i] != null)
                    craftingSlots[i].SlotChanged -= HandleCraftingSlotChanged;
            }
        }

        if (outputSlot != null)
        {
            outputSlot.BeforeTakeFromSlot -= HandleOutputBeforeTake;
            outputSlot.AfterTakeFromSlot -= HandleOutputAfterTake;
        }
    }

    private void HandleCraftingSlotChanged(Slot _)
    {
        if (suppressCraftingRefresh)
            return;

        RefreshCraftResult();
    }

    private bool HandleOutputBeforeTake(Slot slot, Item currentItem, int currentAmount, int amountToTake)
    {
        if (slot != outputSlot || currentItem == null || currentAmount <= 0)
            return false;

        if (!TryFindMatchingRecipe(out RecipeMatch match))
            return false;

        if (match.recipe.OutputItem != currentItem || match.recipe.OutputAmount != currentAmount || amountToTake != currentAmount)
            return false;

        suppressCraftingRefresh = true;
        bool consumed = false;

        try
        {
            consumed = TryConsumeRecipe(match);
            refreshAfterOutputTake = consumed;
        }
        finally
        {
            suppressCraftingRefresh = false;
        }

        return consumed;
    }

    private void HandleOutputAfterTake(Slot slot, Item _, int __)
    {
        if (slot != outputSlot || !refreshAfterOutputTake)
            return;

        refreshAfterOutputTake = false;
        RefreshCraftResult();
    }

    private bool TryFindMatchingRecipe(out RecipeMatch match)
    {
        match = default;

        if (outputSlot == null || recipes == null || recipes.Length == 0)
            return false;

        OccupiedBounds inputBounds = CalculateInputBounds();
        if (!inputBounds.hasItems)
            return false;

        for (int i = 0; i < recipes.Length; i++)
        {
            CraftingRecipe recipe = recipes[i];
            if (recipe == null || recipe.OutputItem == null)
                continue;

            OccupiedBounds recipeBounds = CalculateRecipeBounds(recipe);
            if (!recipeBounds.hasItems)
                continue;

            if (inputBounds.Width != recipeBounds.Width || inputBounds.Height != recipeBounds.Height)
                continue;

            if (MatchesRecipe(recipe, inputBounds, recipeBounds, false))
            {
                match = new RecipeMatch
                {
                    recipe = recipe,
                    mirrored = false,
                    inputBounds = inputBounds,
                    recipeBounds = recipeBounds
                };
                return true;
            }

            if (!recipe.AllowHorizontalMirror)
                continue;

            if (!MatchesRecipe(recipe, inputBounds, recipeBounds, true))
                continue;

            match = new RecipeMatch
            {
                recipe = recipe,
                mirrored = true,
                inputBounds = inputBounds,
                recipeBounds = recipeBounds
            };
            return true;
        }

        return false;
    }

    private bool MatchesRecipe(CraftingRecipe recipe, OccupiedBounds inputBounds, OccupiedBounds recipeBounds, bool mirrored)
    {
        for (int y = 0; y < inputBounds.Height; y++)
        {
            for (int x = 0; x < inputBounds.Width; x++)
            {
                Slot inputSlot = GetCraftingSlot(inputBounds.minX + x, inputBounds.minY + y);
                CraftingRecipe.IngredientSlot ingredient = GetRecipeIngredient(recipe, recipeBounds, x, y, mirrored);

                if (ingredient.IsEmpty)
                {
                    if (inputSlot != null && !inputSlot.IsEmpty)
                        return false;

                    continue;
                }

                if (inputSlot == null || inputSlot.IsEmpty)
                    return false;

                if (inputSlot.item != ingredient.item || inputSlot.amount < ingredient.RequiredAmount)
                    return false;
            }
        }

        return true;
    }

    private bool TryConsumeRecipe(RecipeMatch match)
    {
        for (int y = 0; y < match.inputBounds.Height; y++)
        {
            for (int x = 0; x < match.inputBounds.Width; x++)
            {
                CraftingRecipe.IngredientSlot ingredient = GetRecipeIngredient(match.recipe, match.recipeBounds, x, y, match.mirrored);
                if (ingredient.IsEmpty)
                    continue;

                Slot slot = GetCraftingSlot(match.inputBounds.minX + x, match.inputBounds.minY + y);
                if (slot == null || slot.IsEmpty)
                    return false;

                if (slot.item != ingredient.item || slot.amount < ingredient.RequiredAmount)
                    return false;
            }
        }

        for (int y = 0; y < match.inputBounds.Height; y++)
        {
            for (int x = 0; x < match.inputBounds.Width; x++)
            {
                CraftingRecipe.IngredientSlot ingredient = GetRecipeIngredient(match.recipe, match.recipeBounds, x, y, match.mirrored);
                if (ingredient.IsEmpty)
                    continue;

                Slot slot = GetCraftingSlot(match.inputBounds.minX + x, match.inputBounds.minY + y);
                if (slot == null)
                    return false;

                slot.Remove(ingredient.RequiredAmount);
            }
        }

        activeRecipe = match.recipe;
        activeRecipeMirrored = match.mirrored;
        return true;
    }

    private OccupiedBounds CalculateInputBounds()
    {
        OccupiedBounds bounds = default;

        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                Slot slot = GetCraftingSlot(x, y);
                if (slot == null || slot.IsEmpty)
                    continue;

                ExpandBounds(ref bounds, x, y);
            }
        }

        return bounds;
    }

    private static OccupiedBounds CalculateRecipeBounds(CraftingRecipe recipe)
    {
        OccupiedBounds bounds = default;
        if (recipe == null)
            return bounds;

        for (int y = 0; y < recipe.Height; y++)
        {
            for (int x = 0; x < recipe.Width; x++)
            {
                CraftingRecipe.IngredientSlot ingredient = recipe.GetIngredient(x, y);
                if (ingredient.IsEmpty)
                    continue;

                ExpandBounds(ref bounds, x, y);
            }
        }

        return bounds;
    }

    private static void ExpandBounds(ref OccupiedBounds bounds, int x, int y)
    {
        if (!bounds.hasItems)
        {
            bounds.hasItems = true;
            bounds.minX = bounds.maxX = x;
            bounds.minY = bounds.maxY = y;
            return;
        }

        bounds.minX = Mathf.Min(bounds.minX, x);
        bounds.minY = Mathf.Min(bounds.minY, y);
        bounds.maxX = Mathf.Max(bounds.maxX, x);
        bounds.maxY = Mathf.Max(bounds.maxY, y);
    }

    private CraftingRecipe.IngredientSlot GetRecipeIngredient(CraftingRecipe recipe, OccupiedBounds recipeBounds, int normalizedX, int normalizedY, bool mirrored)
    {
        int localX = mirrored
            ? (recipeBounds.Width - 1) - normalizedX
            : normalizedX;

        int recipeX = recipeBounds.minX + localX;
        int recipeY = recipeBounds.minY + normalizedY;
        return recipe.GetIngredient(recipeX, recipeY);
    }

    private Slot GetCraftingSlot(int x, int y)
    {
        if (craftingSlots == null || x < 0 || y < 0 || x >= gridWidth || y >= gridHeight)
            return null;

        int index = (y * gridWidth) + x;
        if (index < 0 || index >= craftingSlots.Length)
            return null;

        return craftingSlots[index];
    }

    private void SetOutputSlot(Item item, int amount)
    {
        if (outputSlot == null)
            return;

        if (outputSlot.item == item && outputSlot.amount == amount)
            return;

        outputSlot.SetContents(item, amount);
    }

    private void ClearOutputSlot()
    {
        if (outputSlot == null || outputSlot.IsEmpty)
            return;

        outputSlot.SetContents(null, 0);
    }
}
