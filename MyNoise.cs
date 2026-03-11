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

[Serializable]
public struct WorleyTunnelSettings
{
    public bool enabled;
    public float cellSize;
    public float tunnelDiameter;
    public float tunnelDiameterMin;
    public float tunnelDiameterMax;
    public int evaluationStride;
    public int minY;
    public int maxY;
    public int minSurfaceDepth;
    public int seed;
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
    public static bool ShouldCarveWorleyEdgeTunnel(int worldX, int worldY, int worldZ, int surfaceHeight, WorleyTunnelSettings settings)
    {
        if (!settings.enabled) return false;
        if (worldY <= 2) return false;
        if (worldY < settings.minY || worldY > settings.maxY) return false;
        if (worldY > surfaceHeight - settings.minSurfaceDepth) return false;

        int sampleStride = math.max(1, settings.evaluationStride);
        int bucketY = FloorDiv(worldY, sampleStride);

        if (sampleStride == 1)
        {
            float metric = EvaluateWorleyTunnelMetric(
                new float3(worldX + 0.5f, worldY + 0.5f, worldZ + 0.5f),
                settings
            );
            return metric <= 0f;
        }

        float3 sampleA = GetStrideSamplePosition(worldX, worldZ, bucketY, settings);
        float3 sampleB = GetStrideSamplePosition(worldX, worldZ, bucketY + 1, settings);

        float metricA = EvaluateWorleyTunnelMetric(sampleA, settings);
        float metricB = EvaluateWorleyTunnelMetric(sampleB, settings);

        float bucketStart = bucketY * sampleStride;
        float t = math.saturate((worldY - bucketStart + 0.5f) / sampleStride);
        float metricInterpolated = math.lerp(metricA, metricB, t);

        return metricInterpolated <= 0f;
    }

    [BurstCompile]
    public static float3 GetStrideSamplePosition(int worldX, int worldZ, int bucketY, WorleyTunnelSettings settings)
    {
        int sampleStride = math.max(1, settings.evaluationStride);
        return new float3(
            worldX + 0.5f,
            bucketY * sampleStride + sampleStride * 0.5f,
            worldZ + 0.5f
        );
    }

    [BurstCompile]
    public static float EvaluateWorleyTunnelMetric(float3 worldPos, WorleyTunnelSettings settings)
    {
        float safeCellSize = math.max(1f, settings.cellSize);
        WorleyNearestAndThirdSq3D(worldPos / safeCellSize, out float d1, out float d3);

        float edgeDistance = (math.sqrt(d3) - math.sqrt(d1)) * safeCellSize;
        float tunnelDiameter = SampleTunnelDiameter(worldPos, safeCellSize, settings);
        float radius = math.max(0.1f, tunnelDiameter * 0.5f);

        return edgeDistance - radius;
    }

    [BurstCompile]
    public static float WorleyEdgeDistance3D(float3 worldPos, float cellSize, int seed)
    {
        float safeCellSize = math.max(1f, cellSize);
        WorleyNearestAndThirdSq3D(worldPos / safeCellSize, out float d1, out float d3);
        return (math.sqrt(d3) - math.sqrt(d1)) * safeCellSize;
    }

    [BurstCompile]
    private static void WorleyNearestAndThirdSq3D(float3 p, out float d1, out float d3)
    {
        int3 baseCell = (int3)math.floor(p);

        d1 = float.MaxValue;
        float d2 = float.MaxValue;
        d3 = float.MaxValue;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int3 cell = baseCell + new int3(dx, dy, dz);
                    float3 feature = (float3)cell + new float3(0.5f);
                    float distSq = math.lengthsq(feature - p);

                    if (distSq < d1)
                    {
                        d3 = d2;
                        d2 = d1;
                        d1 = distSq;
                    }
                    else if (distSq < d2)
                    {
                        d3 = d2;
                        d2 = distSq;
                    }
                    else if (distSq < d3)
                    {
                        d3 = distSq;
                    }
                }
            }
        }
    }

    [BurstCompile]
    private static float SampleTunnelDiameter(float3 worldPos, float safeCellSize, WorleyTunnelSettings settings)
    {
        float fallbackDiameter = math.max(0.1f, settings.tunnelDiameter);
        float minDiameter = settings.tunnelDiameterMin > 0f ? settings.tunnelDiameterMin : fallbackDiameter;
        float maxDiameter = settings.tunnelDiameterMax > 0f ? settings.tunnelDiameterMax : fallbackDiameter;

        minDiameter = math.max(0.1f, minDiameter);
        maxDiameter = math.max(minDiameter, maxDiameter);

        if (math.abs(maxDiameter - minDiameter) < 1e-4f)
            return minDiameter;

        float frequency = 1f / math.max(8f, safeCellSize * 2.2f);
        float seedOffset = settings.seed * 0.00127f;
        float3 diameterPos = worldPos * frequency + new float3(seedOffset + 13.1f, seedOffset - 7.3f, seedOffset + 29.7f);
        float noiseSample = noise.snoise(diameterPos) * 0.5f + 0.5f;
        return math.lerp(minDiameter, maxDiameter, noiseSample);
    }

    [BurstCompile]
    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && a < 0) q--;
        return q;
    }

    [BurstCompile]
    public static float Redistribution(float noise, float redistributionModifier = 1f, float exponent = 1f)
    {
        return math.pow(noise * redistributionModifier, exponent);
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
