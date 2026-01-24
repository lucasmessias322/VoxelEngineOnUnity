
// using UnityEngine;
// using Unity.Burst;
// using Unity.Mathematics;
// using System;

// [Serializable]
// public struct NoiseLayer
// {
//     public bool enabled;
//     public float scale;
//     public float amplitude;
//     public int octaves;
//     public float persistence;
//     public float lacunarity;
//     public Vector2 offset;
//     public float maxAmp; // precomputado (World.Start já faz isso)

//     // novos (opcionais) — controla redistribuição do layer
//     public float redistributionModifier; // default 1
//     public float exponent; // default 1

//     public float verticalScale;  // Novo: Frequência vertical (default 0.05f)
//     public float ridgeFactor;    // Novo: Fator para ridging (default 1f-3f, para formas irregulares)
// }
// /// <summary>
// /// Utilitários de noise (inspirado no seu snippet).
// /// Use OctavePerlin(nx, nz, layer) para substituir a implementação atual do MeshGenerator.
// /// </summary>
// /// 
// public static class MyNoise
// {
//     public static float RemapValue(float value, float initialMin, float initialMax, float outputMin, float outputMax)
//     {
//         return outputMin + (value - initialMin) * (outputMax - outputMin) / (initialMax - initialMin);
//     }

//     public static float RemapValue01(float value, float outputMin, float outputMax)
//     {
//         return RemapValue(value, 0f, 1f, outputMin, outputMax);
//     }

//     public static int RemapValue01ToInt(float value, float outputMin, float outputMax)
//     {
//         return (int)RemapValue01(value, outputMin, outputMax);
//     }

//     /// <summary>
//     /// Pequena função de redistribuição (opcional).
//     /// noise em [0,1] -> aplica multiplicador e expoente.
//     /// </summary>
//     [BurstCompile]
//     public static float Redistribution(float noise, float redistributionModifier = 1f, float exponent = 1f)
//     {
//         return math.pow(noise * redistributionModifier, exponent);
//     }

//     /// <summary>
//     /// Octave Perlin genérico que aceita a struct NoiseLayer usada no seu projeto.
//     /// Espera que o caller já passe coords que podem conter offsets/warping.
//     /// Retorna valor aproximadamente em [0,1] (usa layer.maxAmp quando disponível).
//     /// </summary>
//     [BurstCompile]
//     public static float OctavePerlin(float nx, float nz, NoiseLayer layer)
//     {
//         float scale = math.max(1e-5f, layer.scale);
//         int octaves = math.max(1, layer.octaves);
//         float persistence = math.clamp(layer.persistence, 0f, 1f);
//         float lacunarity = math.max(1f, layer.lacunarity);

//         // Caso simples (1 octave)
//         if (octaves == 1)
//         {
//             return noise.cnoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f;
//         }

//         float total = 0f;
//         float amplitude = 1f;
//         float frequency = 1f;
//         float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

//         for (int i = 0; i < octaves; i++)
//         {
//             float sample = noise.cnoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
//             total += sample * amplitude;
//             amplitude *= persistence;
//             frequency *= lacunarity;
//         }

//         float value = total / maxAmp;

//         // garante intervalo [0,1]
//         return math.clamp(value, 0f, 1f);
//     }

//     /// <summary>
//     /// Sobrecarga para WarpLayer (sem redistribuição).
//     /// </summary>
//     [BurstCompile]
//     public static float OctavePerlin(float nx, float nz, WarpLayer layer)
//     {
//         float scale = math.max(1e-5f, layer.scale);
//         int octaves = math.max(1, layer.octaves);
//         float persistence = math.clamp(layer.persistence, 0f, 1f);
//         float lacunarity = math.max(1f, layer.lacunarity);

//         // Caso simples (1 octave)
//         if (octaves == 1)
//         {
//             return noise.cnoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f;
//         }

//         float total = 0f;
//         float amplitude = 1f;
//         float frequency = 1f;
//         float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

//         for (int i = 0; i < octaves; i++)
//         {
//             float sample = noise.cnoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
//             total += sample * amplitude;
//             amplitude *= persistence;
//             frequency *= lacunarity;
//         }

//         float value = total / maxAmp;

//         // garante intervalo [0,1]
//         return math.clamp(value, 0f, 1f);
//     }

//     // Adicione ao final da classe MyNoise:

//     /// <summary>
//     /// Octave Perlin 3D para cavernas. Aceita NoiseLayer (reusa params como scale, octaves, etc.).
//     /// Coords (nx, ny, nz) já devem incluir offsets/warping se necessário.
//     /// Retorna valor em [0,1].
//     /// </summary>
//     [BurstCompile]
//     public static float OctavePerlin3D(float nx, float ny, float nz, NoiseLayer layer)
//     {
//         float scale = math.max(1e-5f, layer.scale);
//         int octaves = math.max(1, layer.octaves);
//         float persistence = math.clamp(layer.persistence, 0f, 1f);
//         float lacunarity = math.max(1f, layer.lacunarity);

//         // Caso simples (1 octave)
//         if (octaves == 1)
//         {
//             return noise.cnoise(new float3(nx / scale, ny / scale, nz / scale)) * 0.5f + 0.5f;
//         }

//         float total = 0f;
//         float amplitude = 1f;
//         float frequency = 1f;
//         float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

//         for (int i = 0; i < octaves; i++)
//         {
//             float sample = noise.cnoise(new float3((nx * frequency) / scale, (ny * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
//             total += sample * amplitude;
//             amplitude *= persistence;
//             frequency *= lacunarity;
//         }

//         float value = total / maxAmp;
//         return math.clamp(value, 0f, 1f);
//     }
// }

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
    public float maxAmp; // precomputado (World.Start já faz isso)

    // novos (opcionais) — controla redistribuição do layer
    public float redistributionModifier; // default 1
    public float exponent; // default 1

    public float verticalScale;  // Novo: Frequência vertical (default 0.05f)
    public float ridgeFactor;    // Novo: Fator para ridging (default 1f-3f, para formas irregulares)
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

    // Adicione ao final da classe MyNoise:

    /// <summary>
    /// Octave Perlin 3D para cavernas. Aceita NoiseLayer (reusa params como scale, octaves, etc.).
    /// Coords (nx, ny, nz) já devem incluir offsets/warping se necessário.
    /// Retorna valor em [0,1].
    /// </summary>
    // [BurstCompile]
    // public static float OctavePerlin3D(float nx, float ny, float nz, NoiseLayer layer)
    // {
    //     float scale = math.max(1e-5f, layer.scale);
    //     int octaves = math.max(1, layer.octaves);
    //     float persistence = math.clamp(layer.persistence, 0f, 1f);
    //     float lacunarity = math.max(1f, layer.lacunarity);

    //     // Caso simples (1 octave)
    //     if (octaves == 1)
    //     {
    //         return noise.snoise(new float3(nx / scale, ny / scale, nz / scale)) * 0.5f + 0.5f; // Changed to snoise (faster for 3D)
    //     }

    //     float total = 0f;
    //     float amplitude = 1f;
    //     float frequency = 1f;
    //     float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

    //     for (int i = 0; i < octaves; i++)
    //     {
    //         float sample = noise.snoise(new float3((nx * frequency) / scale, (ny * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f; // Changed to snoise
    //         total += sample * amplitude;
    //         amplitude *= persistence;
    //         frequency *= lacunarity;
    //     }

    //     float value = total / maxAmp;
    //     return math.clamp(value, 0f, 1f);
    // }
    [BurstCompile]
    public static float OctavePerlin3D(float nx, float ny, float nz, NoiseLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        float verticalScale = math.max(1e-5f, layer.verticalScale); // Use o novo campo (default 0.05f)
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);
        float ridgeFactor = math.max(0.1f, layer.ridgeFactor); // Garanta >0

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            // Aplique verticalScale diferente para y (reduz detalhe vertical, economiza em formas alongadas)
            float sample = noise.snoise(new float3((nx * frequency) / scale, (ny * frequency) / verticalScale, (nz * frequency) / scale)) * 0.5f + 0.5f;

            // Aplique ridging se ridgeFactor >1 (cria picos afiados como em cavernas do Bedrock)
            if (ridgeFactor > 1f)
            {
                sample = math.abs(1f - 2f * sample); // Inverte para ridges
                sample = math.pow(sample, ridgeFactor); // Expoente para afiar (maior ridgeFactor = mais afiado)
            }

            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        if (octaves == 1)
        {
            return noise.snoise(new float3(nx / scale, ny / verticalScale, nz / scale)) * 0.5f + 0.5f;
        }

        float value = total / maxAmp;
        return math.clamp(value, 0f, 1f);
    }
}