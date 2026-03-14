using System;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainLayerProfile", menuName = "ScriptableObjects/Terrain Layer Profile", order = 3)]
public class TerrainLayerProfileSO : ScriptableObject
{
    [Header("Terrain Noise Layers")]
    public NoiseLayer[] noiseLayers = Array.Empty<NoiseLayer>();

    [Header("Domain Warp Layers")]
    public WarpLayer[] warpLayers = Array.Empty<WarpLayer>();

    public NoiseLayer[] CloneNoiseLayers()
    {
        if (noiseLayers == null || noiseLayers.Length == 0)
            return Array.Empty<NoiseLayer>();

        NoiseLayer[] copy = new NoiseLayer[noiseLayers.Length];
        Array.Copy(noiseLayers, copy, noiseLayers.Length);
        return copy;
    }

    public WarpLayer[] CloneWarpLayers()
    {
        if (warpLayers == null || warpLayers.Length == 0)
            return Array.Empty<WarpLayer>();

        WarpLayer[] copy = new WarpLayer[warpLayers.Length];
        Array.Copy(warpLayers, copy, warpLayers.Length);
        return copy;
    }

    [ContextMenu("Apply Preset/Minecraft Surface")]
    public void ApplyMinecraftSurfacePreset()
    {
        noiseLayers = new[]
        {
            CreateNoiseLayer(
                role: TerrainNoiseRole.Continentalness,
                scale: 1400f,
                amplitude: 42f,
                octaves: 4,
                persistence: 0.5f,
                lacunarity: 2f,
                redistributionModifier: 1f,
                exponent: 1f,
                ridgeFactor: 1f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.Erosion,
                scale: 420f,
                amplitude: 1f,
                octaves: 3,
                persistence: 0.5f,
                lacunarity: 2f,
                redistributionModifier: 1f,
                exponent: 1f,
                ridgeFactor: 1f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.HillsNoise,
                scale: 150f,
                amplitude: 24f,
                octaves: 4,
                persistence: 0.52f,
                lacunarity: 2.1f,
                redistributionModifier: 1.05f,
                exponent: 1.05f,
                ridgeFactor: 1f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.PeaksValleys,
                scale: 260f,
                amplitude: 1f,
                octaves: 3,
                persistence: 0.52f,
                lacunarity: 2.05f,
                redistributionModifier: 1f,
                exponent: 1f,
                ridgeFactor: 1f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.MountainNoise,
                scale: 95f,
                amplitude: 34f,
                octaves: 5,
                persistence: 0.55f,
                lacunarity: 2.2f,
                redistributionModifier: 1.1f,
                exponent: 1.08f,
                ridgeFactor: 1.7f
            ),
        };
    }

    [ContextMenu("Apply Preset/Legacy Additive")]
    public void ApplyLegacyAdditivePreset()
    {
        noiseLayers = new[]
        {
            CreateNoiseLayer(
                role: TerrainNoiseRole.LegacyAdditive,
                scale: 45f,
                amplitude: 1f,
                octaves: 4,
                persistence: 0.55f,
                lacunarity: 2.2f,
                redistributionModifier: 1.1f,
                exponent: 1.1f,
                ridgeFactor: 1f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.LegacyAdditive,
                scale: 90f,
                amplitude: 0.65f,
                octaves: 4,
                persistence: 0.52f,
                lacunarity: 2.15f,
                redistributionModifier: 1.05f,
                exponent: 1.08f,
                ridgeFactor: 1.15f
            ),
            CreateNoiseLayer(
                role: TerrainNoiseRole.LegacyAdditive,
                scale: 180f,
                amplitude: 0.4f,
                octaves: 3,
                persistence: 0.5f,
                lacunarity: 2f,
                redistributionModifier: 1f,
                exponent: 1f,
                ridgeFactor: 1f
            ),
        };
    }

    [ContextMenu("Apply Preset/Clear Noise Layers")]
    public void ClearNoiseLayers()
    {
        noiseLayers = Array.Empty<NoiseLayer>();
    }

    private static NoiseLayer CreateNoiseLayer(
        TerrainNoiseRole role,
        float scale,
        float amplitude,
        int octaves,
        float persistence,
        float lacunarity,
        float redistributionModifier,
        float exponent,
        float ridgeFactor)
    {
        return new NoiseLayer
        {
            enabled = true,
            role = role,
            scale = scale,
            amplitude = amplitude,
            octaves = octaves,
            persistence = persistence,
            lacunarity = lacunarity,
            offset = Vector2.zero,
            maxAmp = 0f,
            redistributionModifier = redistributionModifier,
            exponent = exponent,
            verticalScale = 1f,
            ridgeFactor = ridgeFactor
        };
    }
}
