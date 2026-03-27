using UnityEngine;

[CreateAssetMenu(fileName = "FurnaceRecipe", menuName = "Crafting/Furnace Recipe")]
public class FurnaceRecipeSO : ScriptableObject
{
    [Header("Input")]
    [SerializeField] private Item inputItem;
    [Min(1)] [SerializeField] private int inputAmount = 1;

    [Header("Output")]
    [SerializeField] private Item outputItem;
    [Min(1)] [SerializeField] private int outputAmount = 1;

    [Header("Timing")]
    [Min(0.05f)] [SerializeField] private float cookDuration = 5f;

    public Item InputItem => inputItem;
    public int InputAmount => Mathf.Max(1, inputAmount);
    public Item OutputItem => outputItem;
    public int OutputAmount => Mathf.Max(1, outputAmount);
    public float CookDuration => Mathf.Max(0.05f, cookDuration);
    public bool IsValid => inputItem != null && outputItem != null;

    private void OnValidate()
    {
        inputAmount = Mathf.Max(1, inputAmount);
        outputAmount = Mathf.Max(1, outputAmount);
        cookDuration = Mathf.Max(0.05f, cookDuration);
    }
}
