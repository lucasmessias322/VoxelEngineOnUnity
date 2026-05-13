using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Initialization Helpers

    private void RefreshTerrainGenerationRuntimeState()
    {
        ApplyTerrainLayerProfileIfAssigned();
        EnsureTerrainLayerArraysInitialized();
        EnsureTerrainSplineShaperInitialized();
        flatWorldHeight = GetResolvedFlatWorldHeight();
        if (!flatWorldBiomeInitialized)
        {
            flatWorldBiome = BiomeType.Meadow;
            flatWorldBiomeInitialized = true;
        }
        flatWorldBiome = GetResolvedFlatWorldBiome();

        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        InitializeBiomeNoiseOffsets();
        InitializeNoiseLayers();
        MarkBiomeCachesDirty();
        MeshGenerator.ClearSpaghettiCarveMaskNeighborCache();
        MeshGenerator.ClearDataJobTempBufferPool();
    }

    private void EnsureTorchFireParticleControllerExists()
    {
        torchFireParticleController = GetComponent<TorchFireParticleController>();
        if (torchFireParticleController == null)
            torchFireParticleController = gameObject.AddComponent<TorchFireParticleController>();
    }

    private void HandleTerrainLayerProfileChanged(TerrainLayerProfileSO changedProfile)
    {
        if (changedProfile == null || changedProfile != terrainLayerProfile)
            return;

        RefreshTerrainGenerationRuntimeState();

        if (!Application.isPlaying || isShuttingDown)
            return;

        foreach (Vector2Int coord in activeChunks.Keys)
            RequestFullChunkRebuild(coord);
    }

    private void HandleBiomeDefinitionChanged(BiomeDefinitionSO changedDefinition)
    {
        if (changedDefinition == null)
            return;

        bool usesResourceDefinitions = biomeDefinitions == null || biomeDefinitions.Length == 0;
        bool referencesDefinition = usesResourceDefinitions;
        if (!referencesDefinition && biomeDefinitions != null)
        {
            for (int i = 0; i < biomeDefinitions.Length; i++)
            {
                if (biomeDefinitions[i] != changedDefinition)
                    continue;

                referencesDefinition = true;
                break;
            }
        }

        if (!referencesDefinition)
            return;

        MarkBiomeCachesDirty();

        if (!Application.isPlaying || isShuttingDown)
            return;

        foreach (Vector2Int coord in activeChunks.Keys)
            RequestFullChunkRebuild(coord);
    }

    private void ApplyTerrainLayerProfileIfAssigned()
    {
        if (terrainLayerProfile == null)
            return;

        noiseLayers = terrainLayerProfile.CloneNoiseLayers();
        terrainSplineShaper = terrainLayerProfile.CloneTerrainSplines();
    }

    private void EnsureTerrainLayerArraysInitialized()
    {
        if (noiseLayers == null)
            noiseLayers = Array.Empty<NoiseLayer>();
    }

    private void EnsureTerrainSplineShaperInitialized()
    {
        if (terrainSplineShaper.enabled || terrainSplineShaper.HasAnyControlPoints)
            return;

        if (!HasAnyModernTerrainRole())
            return;

        terrainSplineShaper = TerrainSplineShaperSettings.MinecraftModernDefault;
    }

    private bool HasAnyModernTerrainRole()
    {
        if (noiseLayers == null)
            return false;

        for (int i = 0; i < noiseLayers.Length; i++)
        {
            if (!noiseLayers[i].enabled)
                continue;

            if (noiseLayers[i].role != TerrainNoiseRole.LegacyAdditive)
                return true;
        }

        return false;
    }

    private void InitializeNoiseLayers()
    {
        if (noiseLayers == null) return;

        for (int i = 0; i < noiseLayers.Length; i++)
        {
            NoiseLayer layer = noiseLayers[i];
            if (!layer.enabled) continue;

            if (layer.scale <= 0f) layer.scale = 45f + i * 10f;
            if (layer.amplitude <= 0f) layer.amplitude = math.pow(0.55f, i);
            if (layer.octaves <= 0) layer.octaves = 3 + i;
            if (layer.lacunarity <= 0f) layer.lacunarity = 2.2f;
            if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.55f;
            if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1.1f + i * 0.05f;
            if (layer.exponent == 0f) layer.exponent = 1.1f;
            if (layer.ridgeFactor <= 0f) layer.ridgeFactor = 1f + i * 0.2f;
            if (layer.domainWarpStrength <= 0f) layer.domainWarpStrength = MyNoise.GetDefaultDomainWarpStrength(layer.role);
            if (layer.domainWarpScale <= 0f) layer.domainWarpScale = 0.88f;
            if (layer.domainWarpOctaves <= 0) layer.domainWarpOctaves = 3;
            if (layer.domainWarpGain <= 0f || layer.domainWarpGain >= 1f) layer.domainWarpGain = 0.5f;
            if (layer.domainWarpLacunarity <= 1f) layer.domainWarpLacunarity = 2.03f;

            if (layer.offset == Vector2.zero)
                layer.offset = new Vector2(i * 13.37f, i * 7.53f);

            float amp = 1f;
            layer.maxAmp = 0f;
            for (int o = 0; o < layer.octaves; o++)
            {
                layer.maxAmp += amp;
                amp *= layer.persistence;
            }
            if (layer.maxAmp <= 0f) layer.maxAmp = 1f;

            noiseLayers[i] = layer;
        }
    }

    #endregion

}
