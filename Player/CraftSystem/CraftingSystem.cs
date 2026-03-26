using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class RecipeItem
{
    public Item item;
    public int quantity;
}

[System.Serializable]
public class Recipe
{
    public string recipeName;
    public List<RecipeItem> requiredItems;
    public Item resultItem;
    public int resultQuantity = 1;
}

public class CraftingSystem : MonoBehaviour
{
    public static CraftingSystem Instance { get; private set; }

    public CraftRecipesSO craftRecipesSO;

    [Header("Cooldown Settings")]
    [Tooltip("Time in seconds between one craft and the next.")]
    public float craftDelay = 1f;
    public bool isCrafting;

    public PlayerInventory playerInventory;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public bool CanCraft(Recipe recipe)
    {
        return GetMaxCraftAmount(recipe) > 0;
    }

    public int GetMaxCraftAmount(Recipe recipe)
    {
        if (!TryResolveInventory(out PlayerInventory inventory))
            return 0;

        if (!IsRecipeValid(recipe))
            return 0;

        Dictionary<Item, int> requiredTotals = BuildRequiredItemTotals(recipe);
        int maxCraftsByMaterials = GetMaxCraftsByMaterials(inventory, requiredTotals);
        if (maxCraftsByMaterials <= 0)
            return 0;

        if (requiredTotals.Count == 0)
        {
            int availableCapacity = inventory.GetAvailableCapacityFor(recipe.resultItem);
            return availableCapacity / Mathf.Max(1, recipe.resultQuantity);
        }

        int minCrafts = 0;
        int maxCrafts = maxCraftsByMaterials;

        while (minCrafts < maxCrafts)
        {
            int probe = (minCrafts + maxCrafts + 1) / 2;
            Dictionary<Item, int> scaledRequirements = ScaleRequiredTotals(requiredTotals, probe);

            if (CanStoreCraftResultAfterConsumption(
                    inventory,
                    recipe.resultItem,
                    recipe.resultQuantity * probe,
                    scaledRequirements))
            {
                minCrafts = probe;
            }
            else
            {
                maxCrafts = probe - 1;
            }
        }

        return minCrafts;
    }

    public void TryCraft(Recipe recipe, int craftCount = 1)
    {
        if (isCrafting)
        {
            Debug.Log("Still waiting for the craft cooldown.");
            return;
        }

        int maxCraftAmount = GetMaxCraftAmount(recipe);
        if (maxCraftAmount <= 0)
        {
            Debug.Log("Missing materials or inventory space for this craft.");
            return;
        }

        int resolvedCraftCount = Mathf.Clamp(craftCount, 1, maxCraftAmount);
        StartCoroutine(ProcessCraft(recipe, resolvedCraftCount));
    }

    public IEnumerator ProcessCraft(Recipe recipe)
    {
        yield return ProcessCraft(recipe, 1);
    }

    private IEnumerator ProcessCraft(Recipe recipe, int craftCount)
    {
        if (!TryResolveInventory(out PlayerInventory inventory))
            yield break;

        if (!IsRecipeValid(recipe))
            yield break;

        int clampedCraftCount = Mathf.Max(1, craftCount);
        Dictionary<Item, int> requiredTotals = ScaleRequiredTotals(BuildRequiredItemTotals(recipe), clampedCraftCount);
        if (!HasRequiredItems(inventory, requiredTotals))
        {
            Debug.Log("Missing materials for this craft.");
            yield break;
        }

        int totalResultAmount = Mathf.Max(1, recipe.resultQuantity) * clampedCraftCount;
        if (!CanStoreCraftResultAfterConsumption(inventory, recipe.resultItem, totalResultAmount, requiredTotals))
        {
            Debug.Log("Not enough inventory space for the crafted item.");
            yield break;
        }

        isCrafting = true;

        foreach (KeyValuePair<Item, int> entry in requiredTotals)
            inventory.RemoveItem(entry.Key, entry.Value);

        int remaining = inventory.InsertItem(recipe.resultItem, totalResultAmount);
        if (remaining > 0)
        {
            Debug.LogWarning(
                $"[CraftingSystem] Inventory could not store {remaining}x {GetItemDisplayName(recipe.resultItem)} after crafting.");
        }

        yield return new WaitForSeconds(Mathf.Max(0f, craftDelay));
        isCrafting = false;
    }

    private static int GetMaxCraftsByMaterials(PlayerInventory inventory, Dictionary<Item, int> requiredTotals)
    {
        if (inventory == null)
            return 0;

        if (requiredTotals == null || requiredTotals.Count == 0)
            return int.MaxValue;

        int maxCrafts = int.MaxValue;
        foreach (KeyValuePair<Item, int> entry in requiredTotals)
        {
            if (entry.Key == null || entry.Value <= 0)
                continue;

            int craftsFromItem = inventory.CountItem(entry.Key) / entry.Value;
            maxCrafts = Mathf.Min(maxCrafts, craftsFromItem);
        }

        return maxCrafts == int.MaxValue ? 0 : maxCrafts;
    }

    private bool TryResolveInventory(out PlayerInventory inventory)
    {
        if (playerInventory == null)
            playerInventory = PlayerInventory.Instance;

        inventory = playerInventory;
        return inventory != null;
    }

    private static bool IsRecipeValid(Recipe recipe)
    {
        return recipe != null &&
               recipe.resultItem != null &&
               recipe.resultQuantity > 0;
    }

    private static Dictionary<Item, int> BuildRequiredItemTotals(Recipe recipe)
    {
        Dictionary<Item, int> totals = new Dictionary<Item, int>();
        if (recipe == null || recipe.requiredItems == null)
            return totals;

        for (int i = 0; i < recipe.requiredItems.Count; i++)
        {
            RecipeItem recipeItem = recipe.requiredItems[i];
            if (recipeItem == null || recipeItem.item == null || recipeItem.quantity <= 0)
                continue;

            if (totals.TryGetValue(recipeItem.item, out int currentAmount))
                totals[recipeItem.item] = currentAmount + recipeItem.quantity;
            else
                totals.Add(recipeItem.item, recipeItem.quantity);
        }

        return totals;
    }

    private static bool HasRequiredItems(PlayerInventory inventory, Dictionary<Item, int> requiredTotals)
    {
        if (inventory == null)
            return false;

        foreach (KeyValuePair<Item, int> entry in requiredTotals)
        {
            if (inventory.CountItem(entry.Key) < entry.Value)
                return false;
        }

        return true;
    }

    private static Dictionary<Item, int> ScaleRequiredTotals(Dictionary<Item, int> requiredTotals, int multiplier)
    {
        Dictionary<Item, int> scaledTotals = new Dictionary<Item, int>();
        if (requiredTotals == null || requiredTotals.Count == 0 || multiplier <= 0)
            return scaledTotals;

        foreach (KeyValuePair<Item, int> entry in requiredTotals)
            scaledTotals[entry.Key] = entry.Value * multiplier;

        return scaledTotals;
    }

    private static bool CanStoreCraftResultAfterConsumption(
        PlayerInventory inventory,
        Item resultItem,
        int resultQuantity,
        Dictionary<Item, int> requiredTotals)
    {
        if (inventory == null || resultItem == null)
            return false;

        Slot[] slots = inventory.Slots;
        if (slots == null || slots.Length == 0)
            return false;

        int stackLimit = Mathf.Max(1, resultItem.maxStack);
        int requiredSpace = Mathf.Max(1, resultQuantity);
        int availableCapacity = 0;
        Dictionary<Item, int> remainingToConsume = new Dictionary<Item, int>(requiredTotals);

        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot == null)
                continue;

            Item simulatedItem = slot.item;
            int simulatedAmount = slot.amount;

            if (simulatedItem != null &&
                remainingToConsume.TryGetValue(simulatedItem, out int amountToConsume) &&
                amountToConsume > 0)
            {
                int consumedHere = Mathf.Min(simulatedAmount, amountToConsume);
                simulatedAmount -= consumedHere;

                int remainingConsume = amountToConsume - consumedHere;
                if (remainingConsume > 0)
                    remainingToConsume[simulatedItem] = remainingConsume;
                else
                    remainingToConsume.Remove(simulatedItem);

                if (simulatedAmount <= 0)
                    simulatedItem = null;
            }

            if (simulatedItem == null || simulatedAmount <= 0)
            {
                availableCapacity += stackLimit;
                continue;
            }

            if (simulatedItem == resultItem)
                availableCapacity += Mathf.Max(0, stackLimit - simulatedAmount);
        }

        return availableCapacity >= requiredSpace;
    }

    private static string GetItemDisplayName(Item item)
    {
        if (item == null)
            return "item";

        return string.IsNullOrWhiteSpace(item.itemName) ? item.name : item.itemName;
    }
}
