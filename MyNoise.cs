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
    public int carvePadding;
    public int minY;
    public int maxY;
    public int minSurfaceDepth;
    public int seed;
    public bool perlinEnabled;
    public float perlinScale;
    public float perlinWarpStrength;
    public float perlinRadiusJitter;
    public int perlinOctaves;
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
        int surfaceDepth = ResolveSurfaceCarveDepth(worldX, worldZ, settings);
        if (worldY > surfaceHeight - surfaceDepth) return false;

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
    public static int ResolveSurfaceCarveDepth(int worldX, int worldZ, WorleyTunnelSettings settings)
    {
        int baselineDepth = math.max(0, settings.minSurfaceDepth);
        if (baselineDepth <= 1)
            return baselineDepth;

        float scale = math.max(12f, settings.cellSize * 2.4f);
        float seedOffset = settings.seed * 0.00107f;
        float2 maskPos = new float2(
            (worldX + seedOffset * 173.3f) / scale,
            (worldZ - seedOffset * 149.7f) / scale
        );

        // Keep most columns capped, but allow sparse natural entrances and a wider shallow band.
        float entranceMask = noise.snoise(maskPos) * 0.5f + 0.5f;
        float openChance = math.clamp(0.025f + baselineDepth * 0.005f, 0.025f, 0.12f);
        if (entranceMask >= 1f - openChance)
            return 0;

        float shallowChance = math.clamp(openChance * 2.6f, openChance, 0.4f);
        if (entranceMask >= 1f - shallowChance)
            return math.max(1, baselineDepth / 2);

        return baselineDepth;
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
        return EvaluateWorleyPerlinTunnelMetric(worldPos, settings);
    }

    [BurstCompile]
    public static float EvaluateWorleyPerlinTunnelMetric(float3 worldPos, WorleyTunnelSettings settings)
    {
        float rawMetric = EvaluateRawWorleyTunnelMetric(worldPos, settings);
        if (rawMetric > EstimateMaxPerlinDetailReach(settings))
            return rawMetric;

        return ApplyPerlinDetailToRawMetric(rawMetric, worldPos, settings);
    }

    [BurstCompile]
    private static float EvaluateRawWorleyTunnelMetric(float3 worldPos, WorleyTunnelSettings settings)
    {
        float safeCellSize = math.max(1f, settings.cellSize);
        WorleyNearestAndThirdSq3D(worldPos / safeCellSize, settings.seed, out float d1, out float d3);

        float edgeDistance = (math.sqrt(d3) - math.sqrt(d1)) * safeCellSize;
        float tunnelDiameter = SampleTunnelDiameter(worldPos, safeCellSize, settings);
        float radius = math.max(0.1f, tunnelDiameter * 0.5f);

        return edgeDistance - radius;
    }

    [BurstCompile]
    private static float ApplyPerlinDetailToRawMetric(float rawMetric, float3 worldPos, WorleyTunnelSettings settings)
    {
        if (!settings.perlinEnabled)
            return rawMetric;

        float metric = rawMetric;
        if (settings.perlinWarpStrength > 0f)
        {
            float3 samplePos = worldPos + SamplePerlinWarpOffset(worldPos, settings);
            metric = EvaluateRawWorleyTunnelMetric(samplePos, settings);
        }

        if (settings.perlinRadiusJitter > 0f)
        {
            metric -= SamplePerlinRadiusJitter(worldPos, settings);
        }

        return metric;
    }

    [BurstCompile]
    private static float EstimateMaxPerlinDetailReach(WorleyTunnelSettings settings)
    {
        float reach = 0.5f;

        if (settings.perlinEnabled)
        {
            // Conservative bound: spatial warp can pull samples closer to tunnel edges.
            reach += math.max(0f, settings.perlinWarpStrength) * 2.2f;
            reach += math.max(0f, settings.perlinRadiusJitter);
        }

        return reach;
    }

    [BurstCompile]
    public static float WorleyEdgeDistance3D(float3 worldPos, float cellSize, int seed)
    {
        float safeCellSize = math.max(1f, cellSize);
        WorleyNearestAndThirdSq3D(worldPos / safeCellSize, seed, out float d1, out float d3);
        return (math.sqrt(d3) - math.sqrt(d1)) * safeCellSize;
    }

    [BurstCompile]
    private static void WorleyNearestAndThirdSq3D(float3 p, int seed, out float d1, out float d3)
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
                    float3 feature = GetWorleyFeaturePoint(cell, seed);
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
    private static float3 GetWorleyFeaturePoint(int3 cell, int seed)
    {
        uint seedU = (uint)seed;
        float x = HashToFloat01(HashInt3(cell, seedU ^ 0xA511E9B3u));
        float y = HashToFloat01(HashInt3(cell, seedU ^ 0x63D83595u));
        float z = HashToFloat01(HashInt3(cell, seedU ^ 0xB5297A4Du));

        // Avoids clustering right on cell borders and reduces visible axis artifacts.
        const float pad = 0.08f;
        return (float3)cell + new float3(
            math.lerp(pad, 1f - pad, x),
            math.lerp(pad, 1f - pad, y),
            math.lerp(pad, 1f - pad, z)
        );
    }

    [BurstCompile]
    private static uint HashInt3(int3 v, uint seed)
    {
        uint h = seed;
        h ^= (uint)v.x * 0x9E3779B9u;
        h = HashUInt(h);
        h ^= (uint)v.y * 0x85EBCA6Bu;
        h = HashUInt(h);
        h ^= (uint)v.z * 0xC2B2AE35u;
        return HashUInt(h);
    }

    [BurstCompile]
    private static uint HashUInt(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    [BurstCompile]
    private static float HashToFloat01(uint h)
    {
        return (h & 0x00FFFFFFu) * (1f / 16777215f);
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
        float noiseSample = noise.cnoise(diameterPos) * 0.5f + 0.5f;
        return math.lerp(minDiameter, maxDiameter, noiseSample);
    }

    [BurstCompile]
    private static float3 SamplePerlinWarpOffset(float3 worldPos, WorleyTunnelSettings settings)
    {
        float scale = math.max(4f, settings.perlinScale);
        int octaves = math.clamp(settings.perlinOctaves, 1, 4);
        float strength = math.max(0f, settings.perlinWarpStrength);
        if (strength <= 0f) return float3.zero;

        float frequency = 1f / scale;
        float seedOffset = settings.seed * 0.00191f;
        float3 p = worldPos * frequency + new float3(seedOffset + 19.3f, seedOffset - 27.1f, seedOffset + 41.7f);

        float3 warp = new float3(
            SamplePerlinFbm3D(p + new float3(13.7f, -3.1f, 7.9f), octaves),
            SamplePerlinFbm3D(p + new float3(-5.3f, 29.4f, -17.2f), octaves),
            SamplePerlinFbm3D(p + new float3(31.8f, -11.6f, 23.5f), octaves)
        );

        return warp * strength;
    }

    [BurstCompile]
    private static float SamplePerlinRadiusJitter(float3 worldPos, WorleyTunnelSettings settings)
    {
        if (!settings.perlinEnabled || settings.perlinRadiusJitter <= 0f)
            return 0f;

        float scale = math.max(4f, settings.perlinScale);
        int octaves = math.clamp(settings.perlinOctaves, 1, 4);
        float frequency = 1f / scale;
        float seedOffset = settings.seed * 0.00271f;

        float3 p = worldPos * frequency + new float3(seedOffset + 71.3f, seedOffset - 43.9f, seedOffset + 12.5f);
        float n = SamplePerlinFbm3D(p, octaves) * 0.5f + 0.5f;
        return n * settings.perlinRadiusJitter;
    }

    [BurstCompile]
    private static float SamplePerlinFbm3D(float3 p, int octaves)
    {
        int safeOctaves = math.clamp(octaves, 1, 4);
        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxAmp = 0f;

        for (int i = 0; i < safeOctaves; i++)
        {
            total += noise.cnoise(p * frequency) * amplitude;
            maxAmp += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return maxAmp > 0f ? total / maxAmp : 0f;
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
