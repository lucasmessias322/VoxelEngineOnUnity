using System;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct WarpLayer
{
    public bool enabled;
    public float scale;
    public float amplitude;
    public int octaves;
    public float persistence;
    public float lacunarity;
    public Vector2 offset;
    public float maxAmp;
}

public enum TerrainNoiseRole : byte
{
    LegacyAdditive = 0,
    Continentalness = 1,
    Erosion = 2,
    HillsNoise = 3,
    PeaksValleys = 4,
    MountainNoise = 5
}

[Serializable]
public struct NoiseLayer
{
    public bool enabled;
    public TerrainNoiseRole role;
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

    [BurstCompile]
    public static float Redistribution(float noise, float redistributionModifier = 1f, float exponent = 1f)
    {
        return math.pow(noise * redistributionModifier, exponent);
    }

    [BurstCompile]
    public static void AccumulateLayerByRole(
        NoiseLayer layer,
        float sample,
        ref bool hasTypedRoles,
        ref float legacyTotal,
        ref float legacyWeight,
        ref float continentalTotal,
        ref float continentalWeight,
        ref float erosionTotal,
        ref float erosionWeight,
        ref float hillsTotal,
        ref float hillsWeight,
        ref float peaksValleysTotal,
        ref float peaksValleysWeight,
        ref float mountainTotal,
        ref float mountainWeight)
    {
        float weight = math.max(1e-5f, layer.amplitude);

        switch (layer.role)
        {
            case TerrainNoiseRole.Continentalness:
                hasTypedRoles = true;
                continentalTotal += sample * weight;
                continentalWeight += weight;
                break;
            case TerrainNoiseRole.Erosion:
                hasTypedRoles = true;
                erosionTotal += sample * weight;
                erosionWeight += weight;
                break;
            case TerrainNoiseRole.HillsNoise:
                hasTypedRoles = true;
                hillsTotal += sample * weight;
                hillsWeight += weight;
                break;
            case TerrainNoiseRole.PeaksValleys:
                hasTypedRoles = true;
                peaksValleysTotal += sample * weight;
                peaksValleysWeight += weight;
                break;
            case TerrainNoiseRole.MountainNoise:
                hasTypedRoles = true;
                mountainTotal += sample * weight;
                mountainWeight += weight;
                break;
            default:
                legacyTotal += sample * layer.amplitude;
                legacyWeight += weight;
                break;
        }
    }

    [BurstCompile]
    public static float GetWeightedRoleSample(float total, float weight, float fallback)
    {
        if (weight <= 0f)
            return fallback;

        return math.clamp(total / weight, 0f, 1f);
    }

    [BurstCompile]
    public static float GetLegacyCenteredNoise(float legacyTotal, float legacyWeight)
    {
        return legacyTotal - legacyWeight * 0.5f;
    }

    [BurstCompile]
    public static float ComposeMinecraftLikeTerrainSignal(
        float continentalTotal,
        float continentalWeight,
        float erosionTotal,
        float erosionWeight,
        float hillsTotal,
        float hillsWeight,
        float peaksValleysTotal,
        float peaksValleysWeight,
        float mountainTotal,
        float mountainWeight,
        float legacyTotal,
        float legacyWeight)
    {
        float continentalness01 = GetWeightedRoleSample(continentalTotal, continentalWeight, 0.5f);
        float erosion01 = GetWeightedRoleSample(erosionTotal, erosionWeight, 1f);
        float hillsNoise01 = GetWeightedRoleSample(hillsTotal, hillsWeight, 0.5f);
        float peaksValleys01 = GetWeightedRoleSample(peaksValleysTotal, peaksValleysWeight, 0f);
        float mountainNoise01 = GetWeightedRoleSample(mountainTotal, mountainWeight, 0.5f);

        float continentalness = (continentalness01 - 0.5f) * math.max(1f, continentalWeight);
        float erosion = math.saturate(erosion01);
        float hillsNoise = (hillsNoise01 - 0.5f) * math.max(1f, hillsWeight);
        float peaksValleys = peaksValleysWeight > 0f ? math.abs(peaksValleys01 * 2f - 1f) : 0f;
        float mountainNoise = (mountainNoise01 - 0.5f) * math.max(1f, mountainWeight);

        return continentalness
             + erosion * hillsNoise
             + peaksValleys * mountainNoise
             + GetLegacyCenteredNoise(legacyTotal, legacyWeight);
    }

    [BurstCompile]
    public static float OctavePerlin(float nx, float nz, NoiseLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);
        float ridgeFactor = math.max(1f, layer.ridgeFactor);

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;

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
    public static float OctavePerlin(float nx, float nz, WarpLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);

        if (octaves == 1)
        {
            return noise.snoise(new float2(nx / scale, nz / scale)) * 0.5f + 0.5f;
        }

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = layer.maxAmp > 0f ? layer.maxAmp : 1f;

        for (int i = 0; i < octaves; i++)
        {
            float sample = noise.snoise(new float2((nx * frequency) / scale, (nz * frequency) / scale)) * 0.5f + 0.5f;
            total += sample * amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        float value = total / maxAmp;
        return math.clamp(value, 0f, 1f);
    }
}
