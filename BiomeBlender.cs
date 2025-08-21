using UnityEngine;

public class BiomeBlender
{
    private Biome[] biomes;
    private float biomeNoiseScale;
    private int biomeSeed;

    // opcional: permitir configurar parâmetros do humidity noise
    private int humidityOctaves = 3;
    private float humidityPersistence = 0.5f;
    private float humidityLacunarity = 2f;

    public BiomeBlender(Biome[] biomesArray, float scale, int seed)
    {
        biomes = biomesArray;
        biomeNoiseScale = scale;
        biomeSeed = seed;
    }

    // Agora 'b' é especificamente a humidade (0..1). O índice/mescla de biomas é feita por ela.
    public void GetBiomeBlendAt(int worldX, int worldZ, out int i0, out int i1, out float t)
    {
        if (biomes == null || biomes.Length == 0)
        {
            i0 = i1 = 0; t = 0f;
            return;
        }

        // humidity noise (usamos os parâmetros fixos acima; ajuste no futuro se quiser)
        float humidity = MyNoise.FractalNoise2D(worldX, worldZ, humidityOctaves, humidityPersistence, humidityLacunarity, biomeNoiseScale, biomeSeed);

        float scaled = humidity * biomes.Length;
        i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, biomes.Length - 1);
        i1 = Mathf.Clamp(i0 + 1, 0, biomes.Length - 1);
        t = Mathf.Clamp01(scaled - i0);
    }
}
