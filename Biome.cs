using UnityEngine;

[System.Serializable]
public class Biome
{
    public string name = "Biome";

    [Header("Visual")]
    [Tooltip("Cor usada para as folhas/folhagem deste bioma")]
    public Color foliageColor = Color.green;
}
