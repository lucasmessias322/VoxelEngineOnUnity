using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class SpaghettiCaveNoiseUtility
{
    private const float DoubleNoiseWarp = 1.0181269f;
    private const float NoiseOffsetMagnitude = 2048f;

    public struct DoubleSimplexNoiseSampler
    {
        public float xzScale;
        public float yScale;
        public float frequency;
        public float3 primaryOffset;
        public float3 secondaryOffset;
    }

    public struct SpaghettiCaveNoiseSampler
    {
        public DoubleSimplexNoiseSampler spaghetti2dModulator;
        public DoubleSimplexNoiseSampler spaghetti2dThickness;
        public DoubleSimplexNoiseSampler spaghetti2dWeirdScaled;
        public DoubleSimplexNoiseSampler spaghetti2dElevation;
        public DoubleSimplexNoiseSampler roughnessModulator;
        public DoubleSimplexNoiseSampler roughness;
        public DoubleSimplexNoiseSampler entrancesOctave0;
        public DoubleSimplexNoiseSampler entrancesOctave1;
        public DoubleSimplexNoiseSampler entrancesOctave2;
        public DoubleSimplexNoiseSampler entranceRarity;
        public DoubleSimplexNoiseSampler entranceSpaghettiA;
        public DoubleSimplexNoiseSampler entranceSpaghettiB;
        public DoubleSimplexNoiseSampler entranceThickness;
    }

    public static SpaghettiCaveNoiseSampler Create(int worldSeed)
    {
        SpaghettiCaveNoiseSampler sampler;
        sampler.spaghetti2dModulator = CreateDoubleSimplexNoiseSampler(worldSeed, 0x4d0f12a1, -11, 2f, 1f);
        sampler.spaghetti2dThickness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x63be5ab9, -11, 2f, 1f);
        sampler.spaghetti2dWeirdScaled = CreateDoubleSimplexNoiseSampler(worldSeed, 0x7c159d51, -7, 1f, 1f);
        sampler.spaghetti2dElevation = CreateDoubleSimplexNoiseSampler(worldSeed, 0x1f3d8a77, -8, 1f, 0f);
        sampler.roughnessModulator = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2c8f3bd3, -8, 1f, 1f);
        sampler.roughness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x54e21f79, -5, 1f, 1f);
        sampler.entrancesOctave0 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1, -7, 0.75f, 0.5f);
        sampler.entrancesOctave1 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1 + 977, -6, 0.75f, 0.5f);
        sampler.entrancesOctave2 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1 + 1954, -5, 0.75f, 0.5f);
        sampler.entranceRarity = CreateDoubleSimplexNoiseSampler(worldSeed, 0x1495f0e3, -11, 2f, 1f);
        sampler.entranceSpaghettiA = CreateDoubleSimplexNoiseSampler(worldSeed, 0x6a31dcb7, -7, 1f, 1f);
        sampler.entranceSpaghettiB = CreateDoubleSimplexNoiseSampler(worldSeed, 0x0dc7612f, -7, 1f, 1f);
        sampler.entranceThickness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x45fa21cd, -8, 1f, 1f);
        return sampler;
    }

    public static float2 SampleDensityPair(
        in SpaghettiCaveNoiseSampler sampler,
        float worldX,
        float worldY,
        float worldZ,
        float densityBias)
    {
        float roughness = SampleRoughness(in sampler, worldX, worldY, worldZ);
        float spaghetti2d = SampleSpaghetti2d(in sampler, worldX, worldY, worldZ);
        float entrancesDensity = SampleEntrances(in sampler, worldX, worldY, worldZ, roughness);
        float carveDensity = math.min(spaghetti2d + roughness, entrancesDensity);
        return new float2(carveDensity + densityBias, entrancesDensity + densityBias);
    }

    private static DoubleSimplexNoiseSampler CreateDoubleSimplexNoiseSampler(
        int worldSeed,
        int noiseSalt,
        int octave,
        float xzScale,
        float yScale)
    {
        float frequency = math.exp2((float)octave);
        uint state = Hash((uint)(worldSeed ^ noiseSalt));

        DoubleSimplexNoiseSampler sampler;
        sampler.xzScale = xzScale;
        sampler.yScale = yScale;
        sampler.frequency = frequency;
        sampler.primaryOffset = NextNoiseOffset(ref state);
        sampler.secondaryOffset = NextNoiseOffset(ref state);
        return sampler;
    }

    private static float SampleSpaghetti2d(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float modulator = SampleDoubleSimplexNoise(in sampler.spaghetti2dModulator, worldX, worldY, worldZ);
        float thicknessMod = -0.95f - 0.35f * SampleDoubleSimplexNoise(in sampler.spaghetti2dThickness, worldX, worldY, worldZ);
        float weirdScaled = SampleWeirdScaledSampler(in sampler.spaghetti2dWeirdScaled, worldX, worldY, worldZ, modulator, false);
        float elevationNoise = SampleDoubleSimplexNoise(in sampler.spaghetti2dElevation, worldX, worldY, worldZ);
        float elevationGradient = SampleYClampedGradient(worldY, -64f, 320f, 8f, -40f);
        float elevationBand = math.abs(elevationNoise * 8f + elevationGradient);

        float longitudinal = weirdScaled + 0.083f * thicknessMod;
        float elevationTerm = Cube(elevationBand + thicknessMod);
        return math.clamp(math.max(longitudinal, elevationTerm), -1f, 1f);
    }

    private static float SampleRoughness(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float roughnessModulator = SampleDoubleSimplexNoise(in sampler.roughnessModulator, worldX, worldY, worldZ);
        float roughness = SampleDoubleSimplexNoise(in sampler.roughness, worldX, worldY, worldZ);
        return (-0.05f - 0.05f * roughnessModulator) * (-0.4f + math.abs(roughness));
    }

    private static float SampleEntrances(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ, float roughness)
    {
        float entranceNoise = SampleNormalizedOctavedNoise(
            in sampler.entrancesOctave0,
            in sampler.entrancesOctave1,
            in sampler.entrancesOctave2,
            worldX,
            worldY,
            worldZ,
            0.4f,
            0.5f,
            1f);
        float entranceHead = 0.37f + entranceNoise + SampleYClampedGradient(worldY, -10f, 30f, 0.3f, 0f);

        float rarityNoise = SampleDoubleSimplexNoise(in sampler.entranceRarity, worldX, worldY, worldZ);
        float spaghettiA = SampleWeirdScaledSampler(in sampler.entranceSpaghettiA, worldX, worldY, worldZ, rarityNoise, true);
        float spaghettiB = SampleWeirdScaledSampler(in sampler.entranceSpaghettiB, worldX, worldY, worldZ, rarityNoise, true);
        float thicknessMod = -0.0765f - 0.0115f * SampleDoubleSimplexNoise(in sampler.entranceThickness, worldX, worldY, worldZ);
        float entranceBody = roughness + math.clamp(math.max(spaghettiA, spaghettiB) + thicknessMod, -1f, 1f);

        return math.min(entranceHead, entranceBody);
    }

    private static float SampleWeirdScaledSampler(
        in DoubleSimplexNoiseSampler sampler,
        float worldX,
        float worldY,
        float worldZ,
        float inputValue,
        bool useType1Rarity)
    {
        float rarity = useType1Rarity
            ? GetRarity3D(inputValue)
            : GetRarity2D(inputValue);

        float sample = SampleDoubleSimplexNoise(
            in sampler,
            worldX / rarity,
            worldY / rarity,
            worldZ / rarity);

        return rarity * math.abs(sample);
    }

    private static float SampleNormalizedOctavedNoise(
        in DoubleSimplexNoiseSampler octave0,
        in DoubleSimplexNoiseSampler octave1,
        in DoubleSimplexNoiseSampler octave2,
        float worldX,
        float worldY,
        float worldZ,
        float amplitude0,
        float amplitude1,
        float amplitude2)
    {
        float total = 0f;
        float weightSum = 0f;

        if (math.abs(amplitude0) > 1e-5f)
        {
            total += amplitude0 * SampleDoubleSimplexNoise(in octave0, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude0);
        }

        if (math.abs(amplitude1) > 1e-5f)
        {
            total += amplitude1 * SampleDoubleSimplexNoise(in octave1, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude1);
        }

        if (math.abs(amplitude2) > 1e-5f)
        {
            total += amplitude2 * SampleDoubleSimplexNoise(in octave2, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude2);
        }

        if (weightSum <= 1e-5f)
            return 0f;

        return math.clamp(total / weightSum, -1f, 1f);
    }

    private static float SampleDoubleSimplexNoise(in DoubleSimplexNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float3 samplePos = new float3(
            worldX * sampler.xzScale * sampler.frequency,
            worldY * sampler.yScale * sampler.frequency,
            worldZ * sampler.xzScale * sampler.frequency);

        float primary = noise.snoise(samplePos + sampler.primaryOffset);
        float secondary = noise.snoise(samplePos * DoubleNoiseWarp + sampler.secondaryOffset);
        return math.clamp((primary + secondary) * 0.5f, -1f, 1f);
    }

    private static float GetRarity2D(float rarity)
    {
        if (rarity < -0.75f)
            return 0.5f;
        if (rarity < -0.5f)
            return 0.75f;
        if (rarity < 0.5f)
            return 1f;
        if (rarity < 0.75f)
            return 2f;
        return 3f;
    }

    private static float GetRarity3D(float rarity)
    {
        if (rarity < -0.5f)
            return 0.75f;
        if (rarity < 0f)
            return 1f;
        if (rarity < 0.5f)
            return 1.5f;
        return 2f;
    }

    private static float SampleYClampedGradient(float y, float fromY, float toY, float fromValue, float toValue)
    {
        float t = math.saturate((y - fromY) / math.max(1e-5f, toY - fromY));
        return math.lerp(fromValue, toValue, t);
    }

    private static float Cube(float value)
    {
        return value * value * value;
    }

    private static float3 NextNoiseOffset(ref uint state)
    {
        return new float3(
            NextSignedFloat(ref state),
            NextSignedFloat(ref state),
            NextSignedFloat(ref state)) * NoiseOffsetMagnitude;
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16;
        v *= 0x7feb352du;
        v ^= v >> 15;
        v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }

    private static float NextFloat01(ref uint state)
    {
        state = Hash(state + 0x9e3779b9u);
        return (state & 0x00ffffffu) / 16777215f;
    }

    private static float NextSignedFloat(ref uint state)
    {
        return NextFloat01(ref state) * 2f - 1f;
    }
}
