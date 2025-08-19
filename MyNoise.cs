using UnityEngine;

public class MyNoise
{
    // Fractal Perlin (octaves) - versão geral
    public static float FractalNoise2D(float x, float z, int octaves, float persistence, float lacunarity, float scale, int seed)
    {
        float total = 0;
        float frequency = scale;
        float amplitude = 1;
        float maxValue = 0;

        for (int i = 0; i < octaves; i++)
        {
            float nx = (x + seed) * frequency;
            float nz = (z + seed) * frequency;
            total += Mathf.PerlinNoise(nx, nz) * amplitude;

            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxValue;
    }

    // Domain warping — retorna coords warpados
    public static Vector2 DomainWarp(float x, float z, float warpStrength, float warpScale, int seed)
    {
        float qx = Mathf.PerlinNoise((x + seed) * warpScale, (z + seed) * warpScale);
        float qz = Mathf.PerlinNoise((x - seed) * warpScale, (z - seed) * warpScale);

        float wx = x + (qx - 0.5f) * 2f * warpStrength;
        float wz = z + (qz - 0.5f) * 2f * warpStrength;

        return new Vector2(wx, wz);
    }

    // Ridged fractal noise — cria picos/montanhas (valores entre 0 e 1)
    public static float RidgedFractalNoise2D(float x, float z, int octaves, float persistence, float lacunarity, float scale, int seed)
    {
        float total = 0f;
        float frequency = scale;
        float amplitude = 1f;
        float maxValue = 0f;

        for (int i = 0; i < octaves; i++)
        {
            float nx = (x + seed) * frequency;
            float nz = (z + seed) * frequency;
            float n = Mathf.PerlinNoise(nx, nz); // 0..1
            // transforma em "ridge"
            float ridge = 1f - Mathf.Abs(2f * n - 1f); // 0..1 com crista no centro
            ridge = ridge * ridge; // acentua crista
            total += ridge * amplitude;

            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxValue;
    }



}