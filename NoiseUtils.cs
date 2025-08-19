using UnityEngine;

public static class NoiseUtils
{
    public static float FractalNoise2D(float x, float y, int octaves, float persistence, float lacunarity, float scale, int seed)
    {
        float noise = 0f;
        float amplitude = 1f;
        float frequency = scale;
        float maxValue = 0f;  // Para normalização

        Random.InitState(seed);

        for (int i = 0; i < octaves; i++)
        {
            float sampleX = x * frequency + seed;
            float sampleY = y * frequency + seed;
            float perlinValue = Mathf.PerlinNoise(sampleX, sampleY) * 2 - 1;  // [-1,1] para variação melhor
            noise += perlinValue * amplitude;

            maxValue += amplitude;

            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return (noise / maxValue + 1f) / 2f;  // Normaliza para [0,1]
    }

    public static Vector2 DomainWarp(float x, float y, float strength, float scale, int seed)
    {
        float offsetX = FractalNoise2D(x, y, 1, 1f, 1f, scale, seed) * strength;
        float offsetY = FractalNoise2D(x + 1000, y + 1000, 1, 1f, 1f, scale, seed) * strength;
        return new Vector2(x + offsetX, y + offsetY);
    }
}