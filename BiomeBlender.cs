using UnityEngine;

public class BiomeBlender
{
    private Biome[] biomes;
    private float biomeNoiseScale;
    private int biomeSeed;

    public BiomeBlender(Biome[] biomesArray, float scale, int seed)
    {
        biomes = biomesArray;
        biomeNoiseScale = scale;
        biomeSeed = seed;
    }

    public void GetBiomeBlendAt(int worldX, int worldZ, out int i0, out int i1, out float t)
    {
        if (biomes == null || biomes.Length == 0)
        {
            i0 = i1 = 0; t = 0f;
            return;
        }

        float b = NoiseUtils.FractalNoise2D(worldX, worldZ, 3, 0.5f, 2f, biomeNoiseScale, biomeSeed);

        float scaled = b * biomes.Length;
        i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, biomes.Length - 1);
        i1 = Mathf.Clamp(i0 + 1, 0, biomes.Length - 1);
        t = Mathf.Clamp01(scaled - i0);
    }
}