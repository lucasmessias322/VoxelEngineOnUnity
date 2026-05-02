using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Material Profile & Terrain Mode

    private TerrainDensitySettings GetTerrainDensitySettings()
    {
        TerrainDensitySettings settings = terrainDensity.Sanitized();
        if (IsFlatWorldMode())
            settings.enabled = false;

        return settings;
    }

    private bool IsFlatWorldMode()
    {
        return terrainMode == WorldTerrainMode.Flat;
    }

    private int GetResolvedFlatWorldHeight()
    {
        int fallbackHeight = baseHeight > 2 ? baseHeight : 64;
        int requestedHeight = flatWorldHeight > 2 ? flatWorldHeight : fallbackHeight;
        return Mathf.Clamp(requestedHeight, 3, Chunk.SizeY - 1);
    }

    public Material[] Material
    {
        get => ActiveWorldMaterials;
        set => pcMaterials = value;
    }

    private bool IsMobileMaterialProfileSelected => materialProfile == WorldMaterialProfile.MobileUnlit;

    private Material[] ActiveWorldMaterials
    {
        get
        {
            if (IsMobileMaterialProfileSelected && HasAnyMaterial(mobileMaterials))
                return mobileMaterials;

            return pcMaterials ?? Array.Empty<Material>();
        }
    }

    private static bool HasAnyMaterial(Material[] materials)
    {
        if (materials == null)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null)
                return true;
        }

        return false;
    }

    private static bool ContainsMaterial(Material[] materials, Material material)
    {
        if (materials == null || material == null)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            if (ReferenceEquals(materials[i], material))
                return true;
        }

        return false;
    }

    private bool IsWorldMaterial(Material material)
    {
        return ContainsMaterial(pcMaterials, material) || ContainsMaterial(mobileMaterials, material);
    }

    public void CollectWorldMaterials(List<Material> target)
    {
        if (target == null)
            return;

        AppendUniqueWorldMaterials(pcMaterials, target);
        AppendUniqueWorldMaterials(mobileMaterials, target);
    }

    private static void AppendUniqueWorldMaterials(Material[] source, List<Material> target)
    {
        if (source == null)
            return;

        for (int i = 0; i < source.Length; i++)
        {
            Material material = source[i];
            if (material != null && !target.Contains(material))
                target.Add(material);
        }
    }

    private int ComputeWorldMaterialProfileHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (int)materialProfile;

            Material[] activeMaterials = ActiveWorldMaterials;
            if (activeMaterials == null)
                return hash;

            hash = hash * 31 + activeMaterials.Length;
            for (int i = 0; i < activeMaterials.Length; i++)
            {
                Material material = activeMaterials[i];
                hash = hash * 31 + (material != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material) : 0);
            }

            return hash;
        }
    }

    private void RefreshWorldMaterialProfileOnRenderers()
    {
        Material[] activeMaterials = ActiveWorldMaterials;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null)
                continue;

            RefreshChunkMaterialProfile(chunk, activeMaterials);
            ApplyChunkBiomeTint(chunk, kv.Key);
        }

        foreach (Chunk pooledChunk in chunkPool)
            RefreshChunkMaterialProfile(pooledChunk, activeMaterials);

        for (int i = 0; i < retiredChunksAwaitingRecycle.Count; i++)
            RefreshChunkMaterialProfile(retiredChunksAwaitingRecycle[i], activeMaterials);

        foreach (var kv in highBuildMeshes)
        {
            HighBuildMeshData data = kv.Value;
            RefreshHighBuildSourceMaterials(data, activeMaterials);
            if (data?.meshRenderer == null)
                continue;

            ApplyBiomeTintToRenderer(data.meshRenderer, new Vector2Int(kv.Key.x, kv.Key.z));
            ApplyRealisticShaderRendererSettings(data.meshRenderer);
        }

        ChunkRenderSlice[] allChunkSlices = FindObjectsByType<ChunkRenderSlice>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < allChunkSlices.Length; i++)
        {
            ChunkRenderSlice slice = allChunkSlices[i];
            if (slice == null)
                continue;

            slice.UpdateSourceMaterials(activeMaterials);
            ApplyRealisticShaderRendererSettings(slice.meshRenderer);
        }
    }

    private static void RefreshChunkMaterialProfile(Chunk chunk, Material[] activeMaterials)
    {
        if (chunk == null || chunk.visualSlices == null)
            return;

        for (int i = 0; i < chunk.visualSlices.Length; i++)
        {
            ChunkRenderSlice visualSlice = chunk.visualSlices[i];
            if (visualSlice != null)
                visualSlice.UpdateSourceMaterials(activeMaterials);
        }
    }

    private void EnsureShaderFallbackBuffersBound()
    {
        if (fallbackPulledOpaqueFacesBuffer == null)
        {
            fallbackPulledOpaqueFacesBuffer = new ComputeBuffer(1, PulledOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackPulledOpaqueFacesBuffer.SetData(new float[28]);
        }

        if (fallbackCompactOpaqueFacesBuffer == null)
        {
            fallbackCompactOpaqueFacesBuffer = new ComputeBuffer(1, CompactOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackCompactOpaqueFacesBuffer.SetData(new uint[4]);
        }

        if (fallbackOpaqueGpuSectionsBuffer == null)
        {
            fallbackOpaqueGpuSectionsBuffer = new ComputeBuffer(1, OpaqueGpuSectionStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueGpuSectionsBuffer.SetData(new float[8]);
        }

        if (fallbackOpaqueBlockMappingsBuffer == null)
        {
            fallbackOpaqueBlockMappingsBuffer = new ComputeBuffer(1, OpaqueBlockMappingStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueBlockMappingsBuffer.SetData(new float[8]);
        }

        if (fallbackUnityIndirectDrawArgsBuffer == null)
        {
            fallbackUnityIndirectDrawArgsBuffer = new ComputeBuffer(UnityIndirectDrawArgsWordCount, sizeof(uint), ComputeBufferType.Raw);
            fallbackUnityIndirectDrawArgsBuffer.SetData(new uint[UnityIndirectDrawArgsWordCount]);
        }

        Shader.SetGlobalBuffer(PulledOpaqueFacesBufferPropertyId, fallbackPulledOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(CompactOpaqueFacesBufferPropertyId, fallbackCompactOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(OpaqueGpuSectionsBufferPropertyId, fallbackOpaqueGpuSectionsBuffer);
        Shader.SetGlobalBuffer(OpaqueBlockMappingsBufferPropertyId, fallbackOpaqueBlockMappingsBuffer);
        Shader.SetGlobalBuffer(UnityIndirectDrawArgsBufferPropertyId, fallbackUnityIndirectDrawArgsBuffer);
    }

    private void ReleaseShaderFallbackBuffers()
    {
        ReleaseComputeBuffer(ref fallbackPulledOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackCompactOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueGpuSectionsBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueBlockMappingsBuffer);
        ReleaseComputeBuffer(ref fallbackUnityIndirectDrawArgsBuffer);
    }

    private static void ReleaseComputeBuffer(ref ComputeBuffer buffer)
    {
        if (buffer == null)
            return;

        buffer.Release();
        buffer = null;
    }

    private BlockType ResolveWaterStateForDebug(BlockType blockType)
    {
        if (!enableWater && FluidBlockUtility.IsWater(blockType))
            return BlockType.Air;

        return blockType;
    }

    private int ComputeTreeLeafFoliageSettingsHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp01(treeLeafFoliageSpawnChance) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHeightMin, 0.2f, 2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHeightMax, 0.2f, 2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHalfWidthMin, 0.5f, 1f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHalfWidthMax, 0.5f, 1f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageBaseYOffsetMin, -0.2f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageBaseYOffsetMax, -0.2f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageCenterJitter, 0f, 0.2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBillboardHeight, 0.4f, 2.5f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBillboardHalfWidth, 0.5f, 1.6f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBaseYOffset, -0.4f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraCenterJitter, 0f, 0.2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraRotationOffsetDegrees, 0f, 45f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraRotationRandomDegrees, 0f, 30f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraFaceTiltDegrees, 0f, 60f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraFaceTiltRandomDegrees, 0f, 30f) * 1000f);
            return hash;
        }
    }

    private BlockType GetProceduralSeaBlockOrAir(int worldY)
    {
        if (IsFlatWorldMode() || !enableWater || worldY > seaLevel)
            return BlockType.Air;

        return BlockType.Water;
    }

    #endregion

}
