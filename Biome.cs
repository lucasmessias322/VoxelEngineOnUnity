using UnityEngine;

[System.Serializable]
public class Biome
{
    public string name = "Biome";

    // Noise params (usadas com MyNoise.FractalNoise2D)
    public int octaves = 4;
    public float persistence = 0.5f;
    public float lacunarity = 2f;
    public float scale = 0.02f; // frequência base

    // altura relativa controlada por este bioma
    public float heightOffset = 0f;  // deslocamento absoluto
    public float heightMultiplier = 10f; // quanto o noise afeta a altura local

    // blocos de superfície e subsuperfície (use BlockType do seu projeto)
    public BlockType topBlock = BlockType.Grass;
    public BlockType subSurfaceBlock = BlockType.Dirt;
    public BlockType fillerBlock = BlockType.Stone; // camadas profundas / cliff

    
    [Header("Visual")]
    [Tooltip("Cor usada para as folhas/folhagem deste bioma")]
    public Color foliageColor = Color.green;
}
