using System;
using UnityEngine;

[CreateAssetMenu(fileName = "CraftingRecipe", menuName = "Crafting/Recipe")]
public class CraftingRecipe : ScriptableObject
{
    [Serializable]
    public struct IngredientSlot
    {
        public Item item;
        [Min(1)] public int amount;

        public bool IsEmpty => item == null || amount <= 0;
        public int RequiredAmount => Mathf.Max(1, amount);
    }

    [Header("Grid")]
    [Min(1)] [SerializeField] private int width = 3;
    [Min(1)] [SerializeField] private int height = 3;
    [SerializeField] private bool allowHorizontalMirror = true;
    [SerializeField] private IngredientSlot[] ingredients = new IngredientSlot[9];

    [Header("Result")]
    [SerializeField] private Item outputItem;
    [Min(1)] [SerializeField] private int outputAmount = 1;

    public int Width => Mathf.Max(1, width);
    public int Height => Mathf.Max(1, height);
    public bool AllowHorizontalMirror => allowHorizontalMirror;
    public Item OutputItem => outputItem;
    public int OutputAmount => Mathf.Max(1, outputAmount);

    public IngredientSlot GetIngredient(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return default;

        int index = (y * Width) + x;
        if (ingredients == null || index < 0 || index >= ingredients.Length)
            return default;

        IngredientSlot ingredient = ingredients[index];
        if (ingredient.item != null && ingredient.amount <= 0)
            ingredient.amount = 1;

        return ingredient;
    }

    private void OnValidate()
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        outputAmount = Mathf.Max(1, outputAmount);

        int expectedSize = width * height;
        if (ingredients == null || ingredients.Length != expectedSize)
            Array.Resize(ref ingredients, expectedSize);

        for (int i = 0; i < ingredients.Length; i++)
        {
            if (ingredients[i].item != null && ingredients[i].amount <= 0)
                ingredients[i].amount = 1;
        }
    }
}
