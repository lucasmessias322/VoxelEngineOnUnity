using System;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public enum TerrainNoiseRole : byte
{
    // Cada role representa um canal semantico do relevo, nao apenas "mais ruido".
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
        // Separa as noise layers por papel para que a composicao final possa tratar
        // continentalness, erosao e montanhas de forma independente.
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
    public static bool UsesSemanticTerrainShaping(TerrainNoiseRole role)
    {
        switch (role)
        {
            case TerrainNoiseRole.Continentalness:
            case TerrainNoiseRole.Erosion:
            case TerrainNoiseRole.PeaksValleys:
                return true;
            default:
                return false;
        }
    }

    [BurstCompile]
    public static bool UsesLegacyRidgeSharpening(TerrainNoiseRole role)
    {
        return role == TerrainNoiseRole.LegacyAdditive;
    }

    [BurstCompile]
    public static float GetMinecraftPeaksAndValleys(float weirdness)
    {
        // Mesma transformacao usada pelo vanilla para derivar ridges_folded a partir de weirdness.
        return -(math.abs(math.abs(weirdness) - (2f / 3f)) - (1f / 3f)) * 3f;
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
        float legacyWeight,
        in BiomeTerrainSettings biomeTerrain,
        in TerrainSplineShaperSettings terrainShaper)
    {
        // Junta os canais de relevo em um unico delta de altura seguindo um fluxo
        // parecido com o terrain shaping moderno do Minecraft.
        if (!terrainShaper.enabled)
            return ComposePreSplineMinecraftTerrainSignal(
                continentalTotal,
                continentalWeight,
                erosionTotal,
                erosionWeight,
                hillsTotal,
                hillsWeight,
                peaksValleysTotal,
                peaksValleysWeight,
                mountainTotal,
                mountainWeight,
                legacyTotal,
                legacyWeight,
                biomeTerrain);

        float reliefMultiplier = math.max(0.05f, biomeTerrain.reliefMultiplier);
        float hillsMultiplier = math.max(0f, biomeTerrain.hillsMultiplier);
        float mountainMultiplier = math.max(0f, biomeTerrain.mountainMultiplier);
        float flattenStrength = math.saturate(biomeTerrain.flattenStrength);

        float continentalness01 = GetWeightedRoleSample(continentalTotal, continentalWeight, 0.5f);
        float erosion01 = GetWeightedRoleSample(erosionTotal, erosionWeight, 1f);
        float hillsNoise01 = GetWeightedRoleSample(hillsTotal, hillsWeight, 0.5f);
        float peaksValleys01 = GetWeightedRoleSample(peaksValleysTotal, peaksValleysWeight, 0.5f);
        float mountainNoise01 = GetWeightedRoleSample(mountainTotal, mountainWeight, 0.5f);

        float continentalness = continentalness01 * 2f - 1f;
        float erosion = erosion01 * 2f - 1f;
        float hillsNoise = hillsNoise01 * 2f - 1f;
        // No Overworld moderno, o canal semanticamente equivalente aqui e o weirdness.
        float ridges = peaksValleys01 * 2f - 1f;
        float ridgesFolded = GetMinecraftPeaksAndValleys(ridges);
        float ridgePeakMask = math.saturate(ridgesFolded);
        float mountainVariation = mountainWeight > 0f ? mountainNoise01 : 0.5f;
        TerrainShapePoint shapePoint = new TerrainShapePoint
        {
            continents = continentalness,
            erosion = erosion,
            ridges = ridges,
            ridgesFolded = ridgesFolded
        };

        float flatness = math.saturate(TerrainSplineGraphEvaluator.Evaluate(
            terrainShaper,
            TerrainSplineGraphTarget.Factor,
            shapePoint,
            erosion01));
        float ruggedness = 1f - flatness;
        float jaggedness = math.max(0f, TerrainSplineGraphEvaluator.Evaluate(
            terrainShaper,
            TerrainSplineGraphTarget.Jaggedness,
            shapePoint,
            ridgesFolded));
        float offset = TerrainSplineGraphEvaluator.Evaluate(
            terrainShaper,
            TerrainSplineGraphTarget.Offset,
            shapePoint,
            continentalness);

        float continentalBaseline = offset * math.max(1f, continentalWeight) * 0.82f;
        float legacyDetail = GetLegacyCenteredNoise(legacyTotal, legacyWeight) * math.lerp(1.75f, 8.25f, ruggedness);
        float inlandness = math.saturate(offset * 0.5f + 0.55f);

        // Morros, foothills e montanhas passam a nascer do trio
        // continents/erosion/weirdness, com MountainNoise servindo so como variacao secundaria.
        float hills = hillsNoise
            * math.max(1f, hillsWeight)
            * math.lerp(0.10f, 0.72f, ruggedness)
            * hillsMultiplier;
        float foothills = math.max(0f, hillsNoise)
            * math.max(1f, hillsWeight)
            * ruggedness
            * inlandness
            * math.lerp(1f, 0.7f, ridgePeakMask)
            * 0.34f
            * math.lerp(0.7f, 1f, hillsMultiplier);
        float mountains = math.pow(ridgePeakMask, 1.35f)
            * math.max(1f, mountainWeight) * 0.12f
            * math.lerp(0.78f, 1.18f, mountainVariation)
            * jaggedness
            * ruggedness
            * inlandness
            * mountainMultiplier;
        float cliffAccent = math.pow(ridgePeakMask, 2.1f)
            * math.max(1f, mountainWeight) * 0.05f
            * math.pow(math.saturate((mountainVariation - 0.56f) / 0.44f), 1.8f)
            * jaggedness
            * ruggedness
            * inlandness
            * mountainMultiplier;

        float terrain = continentalBaseline + legacyDetail + hills + foothills + mountains + cliffAccent;
        float flattenedTerrain = continentalBaseline + legacyDetail * 0.42f;
        terrain = math.lerp(terrain, flattenedTerrain, flattenStrength * math.lerp(0.42f, 1f, flatness));
        terrain = continentalBaseline + (terrain - continentalBaseline) * reliefMultiplier;
        terrain += biomeTerrain.heightOffset;

        float hillsTerraceMask = ruggedness
            * SmoothThreshold(math.abs(hillsNoise), 0.18f, 0.74f)
            * SmoothThreshold(terrain, 4f, 18f)
            * math.lerp(1f, 0.46f, flattenStrength);
        terrain = ApplyTerracing(terrain, 2f, hillsTerraceMask * 0.38f);

        float mountainTerraceMask = inlandness
            * math.max(jaggedness, ridgePeakMask)
            * ruggedness
            * SmoothThreshold(terrain, 12f, 42f)
            * math.lerp(1f, 0.7f, flattenStrength);
        float mountainTerraceStep = math.lerp(3.4f, 4.6f, math.saturate(jaggedness));
        return ApplyTerracing(terrain, mountainTerraceStep, mountainTerraceMask * 0.16f);
    }

    [BurstCompile]
    private static float ComposePreSplineMinecraftTerrainSignal(
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
        float legacyWeight,
        in BiomeTerrainSettings biomeTerrain)
    {
        // Fallback para presets antigos que ainda nao usam splines de shaping.
        float reliefMultiplier = math.max(0.05f, biomeTerrain.reliefMultiplier);
        float hillsMultiplier = math.max(0f, biomeTerrain.hillsMultiplier);
        float mountainMultiplier = math.max(0f, biomeTerrain.mountainMultiplier);
        float flattenStrength = math.saturate(biomeTerrain.flattenStrength);
        float erosionBias = biomeTerrain.erosionBias;
        float erosionPower = math.max(0.1f, biomeTerrain.erosionPower);

        float continentalness01 = GetWeightedRoleSample(continentalTotal, continentalWeight, 0.5f);
        float erosion01 = GetWeightedRoleSample(erosionTotal, erosionWeight, 1f);
        float hillsNoise01 = GetWeightedRoleSample(hillsTotal, hillsWeight, 0.5f);
        float peaksValleys01 = GetWeightedRoleSample(peaksValleysTotal, peaksValleysWeight, 0f);
        float mountainNoise01 = GetWeightedRoleSample(mountainTotal, mountainWeight, 0.5f);

        float continentalness = (continentalness01 - 0.5f) * math.max(1f, continentalWeight);
        float erosion = math.saturate(math.pow(math.saturate(erosion01 + erosionBias), erosionPower));
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

        float hills = hillsNoise * math.max(1f, hillsWeight) * math.lerp(0.22f, 0.82f, erosion) * hillsMultiplier;
        float foothills = math.max(0f, hillsNoise) * math.max(1f, hillsWeight) * 0.7f * foothillMask * math.lerp(0.55f, 1f, hillsMultiplier);
        float mountainShoulders = math.pow(math.saturate((mountainNoise01 - 0.38f) / 0.62f), 1.35f) * 0.4f;
        float cliffAccent = math.pow(math.saturate((mountainNoise01 - 0.64f) / 0.36f), 2.8f)
            * math.max(1f, mountainWeight)
            * cliffMask
            * 1.15f
            * mountainMultiplier;
        float mountains = (math.pow(mountainSignal, 2.4f) * 1.9f + mountainShoulders)
            * math.max(1f, mountainWeight)
            * mountainMask
            * mountainMultiplier;

        float baseline = continentalness + GetLegacyCenteredNoise(legacyTotal, legacyWeight);
        float terrain = baseline + hills + foothills + mountains + cliffAccent;
        float flattenedBaseline = baseline + math.max(0f, continentalness) * 0.12f;
        terrain = math.lerp(terrain, flattenedBaseline, flattenStrength);
        terrain = baseline + (terrain - baseline) * reliefMultiplier;
        terrain += biomeTerrain.heightOffset;

        float hillsSteepnessMask = SmoothThreshold(math.abs(hillsNoise), 0.16f, 0.68f);
        float hillsElevationMask = SmoothThreshold(terrain, 3f, 18f);
        float hillsTerraceMask = math.saturate(
            foothillMask * 0.85f +
            mountainMask * 0.45f +
            inlandMask * hillsSteepnessMask * 0.35f) * hillsElevationMask * math.lerp(1f, 0.42f, flattenStrength);

        terrain = ApplyTerracing(terrain, 2f, hillsTerraceMask);

        float mountainElevationMask = SmoothThreshold(terrain, 10f, 42f);
        float mountainTerraceMask = math.saturate(mountainMask * 0.18f + cliffMask * 0.42f)
            * mountainElevationMask
            * math.lerp(1f, 0.68f, flattenStrength);
        float mountainTerraceStep = math.lerp(3.25f, 4.5f, cliffMask);

        return ApplyTerracing(terrain, mountainTerraceStep, mountainTerraceMask);
    }

    [BurstCompile]
    public static float ShapeLegacyTerrainSignal(float legacyTerrain, in BiomeTerrainSettings biomeTerrain)
    {
        float flattened = math.lerp(legacyTerrain, legacyTerrain * 0.4f, math.saturate(biomeTerrain.flattenStrength));
        return flattened * math.max(0.05f, biomeTerrain.reliefMultiplier) + biomeTerrain.heightOffset;
    }

    [BurstCompile]
    public static float OctavePerlin(float nx, float nz, NoiseLayer layer)
    {
        float scale = math.max(1e-5f, layer.scale);
        int octaves = math.max(1, layer.octaves);
        float persistence = math.clamp(layer.persistence, 0f, 1f);
        float lacunarity = math.max(1f, layer.lacunarity);
        float ridgeFactor = UsesLegacyRidgeSharpening(layer.role)
            ? math.max(1f, layer.ridgeFactor)
            : 1f;

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

}
