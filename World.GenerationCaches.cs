using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Native Generation Caches

    private void InvalidateNativeGenerationCaches()
    {
        nativeGenerationConfigDirty = true;
    }

    private void DisposeNativeGenerationCaches()
    {
        if (cachedNativeNoiseLayers.IsCreated) cachedNativeNoiseLayers.Dispose();
        if (cachedNativeBlockMappings.IsCreated) cachedNativeBlockMappings.Dispose();
        if (cachedNativeBlockModelCuboids.IsCreated) cachedNativeBlockModelCuboids.Dispose();
        if (cachedNativeEffectiveLightOpacityByBlock.IsCreated) cachedNativeEffectiveLightOpacityByBlock.Dispose();
        if (cachedNativeLightEmissionByBlock.IsCreated) cachedNativeLightEmissionByBlock.Dispose();
        if (cachedNativeOreSettings.IsCreated) cachedNativeOreSettings.Dispose();
        if (cachedNativeTreeSpawnRules.IsCreated) cachedNativeTreeSpawnRules.Dispose();
        if (cachedNativeVegetationBillboardRules.IsCreated) cachedNativeVegetationBillboardRules.Dispose();
    }

    private void EnsureNativeGenerationCaches()
    {
        // Copia a configuracao viva do World para NativeArrays persistentes usados pelos jobs.
        bool cachesCreated = cachedNativeNoiseLayers.IsCreated &&
                             cachedNativeBlockMappings.IsCreated &&
                             cachedNativeBlockModelCuboids.IsCreated &&
                             cachedNativeEffectiveLightOpacityByBlock.IsCreated &&
                             cachedNativeLightEmissionByBlock.IsCreated &&
                             cachedNativeOreSettings.IsCreated &&
                             cachedNativeTreeSpawnRules.IsCreated &&
                             cachedNativeVegetationBillboardRules.IsCreated;

        if (!nativeGenerationConfigDirty && cachesCreated)
        {
            return;
        }

        if (nativeGenerationConfigDirty && cachesCreated &&
            (pendingDataJobs.Count > 0 || pendingMeshes.Count > 0))
        {
            // Evita trocar buffers no meio de jobs ainda em voo.
            return;
        }

        DisposeNativeGenerationCaches();

        NoiseLayer[] runtimeNoiseLayers = noiseLayers ?? Array.Empty<NoiseLayer>();
        BlockTextureMapping[] runtimeBlockMappings = blockData != null && blockData.mappings != null
            ? blockData.mappings
            : Array.Empty<BlockTextureMapping>();
        BlockModelCuboid[] runtimeBlockModelCuboids = blockData != null && blockData.runtimeMultiCuboidBoxes != null
            ? blockData.runtimeMultiCuboidBoxes
            : Array.Empty<BlockModelCuboid>();
        OreSpawnSettings[] runtimeOreSettings = oreSettings ?? Array.Empty<OreSpawnSettings>();
        TreeSpawnRuleData[] runtimeTreeSpawnRules = GetActiveTreeSpawnRules();
        VegetationBillboardRuleData[] runtimeVegetationBillboardRules = GetActiveVegetationBillboardRules();

        cachedNativeNoiseLayers = new NativeArray<NoiseLayer>(runtimeNoiseLayers, Allocator.Persistent);
        cachedNativeBlockMappings = new NativeArray<BlockTextureMapping>(runtimeBlockMappings, Allocator.Persistent);
        cachedNativeBlockModelCuboids = new NativeArray<BlockModelCuboid>(runtimeBlockModelCuboids, Allocator.Persistent);
        cachedNativeEffectiveLightOpacityByBlock = new NativeArray<byte>(runtimeBlockMappings.Length, Allocator.Persistent);
        cachedNativeLightEmissionByBlock = new NativeArray<ushort>(runtimeBlockMappings.Length, Allocator.Persistent);
        for (int i = 0; i < runtimeBlockMappings.Length; i++)
        {
            cachedNativeEffectiveLightOpacityByBlock[i] = ChunkLighting.GetEffectiveOpacity(runtimeBlockMappings[i]);
            cachedNativeLightEmissionByBlock[i] = LightUtils.PackEmission(runtimeBlockMappings[i].lightEmission, runtimeBlockMappings[i].lightColor);
        }
        cachedNativeOreSettings = new NativeArray<OreSpawnSettings>(runtimeOreSettings, Allocator.Persistent);
        cachedNativeTreeSpawnRules = new NativeArray<TreeSpawnRuleData>(runtimeTreeSpawnRules, Allocator.Persistent);
        cachedNativeVegetationBillboardRules = new NativeArray<VegetationBillboardRuleData>(runtimeVegetationBillboardRules, Allocator.Persistent);
        nativeGenerationConfigDirty = false;
    }

    #endregion

}
