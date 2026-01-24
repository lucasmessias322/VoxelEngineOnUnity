
using UnityEngine;
using Unity.Burst;
using Unity.Mathematics;

/// <summary>
/// Utilitários de noise (inspirado no seu snippet).
/// Use OctavePerlin(nx, nz, layer) para substituir a implementação atual do MeshGenerator.
/// </summary>
public static class MyNoise
{
    public static float RemapValue(float value, float initialMin, float initialMax, float outputMin, float outputMax)
    {
        return outputMin + (value - initialMin) * (outputMax - outputMin) / (initialMax - initialMin);
    }

    public static float RemapValue01(float value, float outputMin, float outputMax)
    {
        return RemapValue(value, 0f, 1f, outputMin, outputMax);
    }

    public static int RemapValue01ToInt(float value, float outputMin, float outputMax)
    {
        return (int)RemapValue01(value, outputMin, outputMax);
    }

    /// <summary>
    /// Pequena função de redistribuição (opcional).
    /// noise em [0,1] -> aplica multiplicador e expoente.
    /// </summary>
    [BurstCompile]
    public static float Redistribution(float noise, float redistributionModifier = 1f, float exponent = 1f)
    {
        return math.pow(noise * redistributionModifier, exponent);
    }

    /// <summary>
    /// Octave Perlin genérico que aceita a struct NoiseLayer usada no seu projeto.
    /// Espera que o caller já passe coords que podem conter offsets/warping.
    /// Retorna valor aproximadamente em [0,1] (usa layer.maxAmp quando disponível).
    /// </summary>
    [BurstCompile]
    public static float OctavePerlin(float nx, float nz, NoiseLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);

        // Caso simples (1 octave)
        if (octaves == 1)
        {
            return noise.cnoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f;
        }

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.cnoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;

        // garante intervalo [0,1]
        return math.clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Sobrecarga para WarpLayer (sem redistribuição).
    /// </summary>
    [BurstCompile]
    public static float OctavePerlin(float nx, float nz, WarpLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);

        // Caso simples (1 octave)
        if (octaves == 1)
        {
            return noise.cnoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f;
        }

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.cnoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;

        // garante intervalo [0,1]
        return math.clamp(value, 0f, 1f);
    }
}