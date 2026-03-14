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
    private static float SmoothThreshold(float value, float min, float max)
    {
        float t = math.saturate((value - min) / math.max(1e-5f, max - min));
        return t * t * (3f - 2f * t);
    }

    [BurstCompile]
    private static float ApplyTerracing(float value, float stepHeight, float blend)
    {
        float safeStep = math.max(0.25f, stepHeight);
        float terracedValue = math.round(value / safeStep) * safeStep;
        return math.lerp(value, terracedValue, math.saturate(blend));
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
        float erosionInv = 1f - erosion;
        float hillsNoise = hillsNoise01 * 2f - 1f;
        float peakSignal = peaksValleysWeight > 0f ? math.abs(peaksValleys01 * 2f - 1f) : 0f;
        float mountainSignal = math.saturate((mountainNoise01 - 0.52f) / 0.48f);

        float inlandMask = SmoothThreshold(continentalness01, 0.46f, 0.62f);
        float foothillMask = SmoothThreshold(continentalness01, 0.41f, 0.58f) * math.pow(erosionInv, 0.85f);
        float peakMask = SmoothThreshold(peakSignal, 0.52f, 0.82f);
        float mountainMask = inlandMask * peakMask * math.pow(erosionInv, 1.35f);
        float cliffMask = mountainMask
            * SmoothThreshold(mountainNoise01, 0.6f, 0.88f)
            * SmoothThreshold(peakSignal, 0.6f, 0.9f);

        float hills = hillsNoise * math.max(1f, hillsWeight) * math.lerp(0.22f, 0.82f, erosion);
        float foothills = math.max(0f, hillsNoise) * math.max(1f, hillsWeight) * 0.7f * foothillMask;
        float mountainShoulders = math.pow(math.saturate((mountainNoise01 - 0.38f) / 0.62f), 1.35f) * 0.4f;
        float cliffAccent = math.pow(math.saturate((mountainNoise01 - 0.64f) / 0.36f), 2.8f)
            * math.max(1f, mountainWeight)
            * cliffMask
            * 1.15f;
        float mountains = (math.pow(mountainSignal, 2.4f) * 1.9f + mountainShoulders)
            * math.max(1f, mountainWeight)
            * mountainMask;
        float terrain = continentalness
             + hills
             + foothills
             + mountains
             + cliffAccent
             + GetLegacyCenteredNoise(legacyTotal, legacyWeight);

        float elevationMask = SmoothThreshold(terrain, 10f, 42f);
        float terraceMask = math.saturate(mountainMask * 0.22f + cliffMask * 0.38f) * elevationMask;
        float terraceStep = math.lerp(2.5f, 4.25f, cliffMask);

        return ApplyTerracing(terrain, terraceStep, terraceMask);
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
