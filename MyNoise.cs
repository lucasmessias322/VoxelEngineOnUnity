
using UnityEngine;
using Unity.Burst;
using Unity.Mathematics;
using System;


[Serializable]
public struct NoiseLayer
{
    public bool enabled;
    public float scale;
    public float amplitude;
    public int octaves;
    public float persistence;
    public float lacunarity;
    public Vector2 offset;
    public float maxAmp;
    public float redistributionModifier;
    public float exponent;
    public float verticalScale;
    public float ridgeFactor;

}

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
        float ridgeFactor = math.max(1f, layer.ridgeFactor); // Novo: ridge para superfície

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;

            // Ridge para picos/montanhas afiadas (como em Bedrock)
            if (ridgeFactor > 1f)
            {
                sample = math.abs(1f - 2f * sample);
                sample = math.pow(sample, ridgeFactor);
            }

            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;
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
            return noise.snoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f; // Changed to snoise
        }

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f; // Changed to snoise
            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;

        // garante intervalo [0,1]
        return math.clamp(value, 0f, 1f);
    }


    /// <summary>
    /// Octave Perlin 3D CORRIGIDO para cavernas Bedrock-like.
    /// - Ridge sempre aplicado.
    /// - verticalScale grande para cavernas horizontais.
    /// - if(1 octave) ANTES do loop.
    /// </summary>
    [BurstCompile]
    public static float OctavePerlin3D(float nx, float ny, float nz, NoiseLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        float verticalScale = math.max(scale * 0.5f, layer.verticalScale); // Min 0.5x scale para bias horizontal
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);
        float ridgeFactor = math.max(1f, layer.ridgeFactor);

        // Caso simples (1 octave) - CORRIGIDO: antes do loop, com ridge
        if (octaves == 1)
        {
            float sample = noise.snoise(new float3(nx / scale, ny / verticalScale, nz / scale)) * 0.5f + 0.5f;
            if (ridgeFactor > 1f)
            {
                sample = math.abs(1f - 2f * sample);
                sample = math.pow(sample, ridgeFactor);
            }
            return sample;
        }

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float3((nx * frequency) / scale, (ny * frequency) / verticalScale, (nz * frequency) / scale)) * 0.5f + 0.5f;

            if (ridgeFactor > 1f)
            {
                sample = math.abs(1f - 2f * sample);
                sample = math.pow(sample, ridgeFactor);
            }

            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;
        return math.clamp(value, 0f, 1f);
    }

    [BurstCompile]
    public static float RidgedOctavePerlin3D(float nx, float ny, float nz, NoiseLayer layer)
    {
        float value = OctavePerlin3D(nx, ny, nz, layer); // Chama existente
        value = 1f - math.abs(value * 2f - 1f); // Ridge invert
        value = math.pow(value, layer.ridgeFactor); // Afiar
        return math.clamp(value, 0f, 1f);
    }
}
