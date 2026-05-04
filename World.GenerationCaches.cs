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
        if (cachedNativeBlockVisualStateTextures.IsCreated) cachedNativeBlockVisualStateTextures.Dispose();
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
                             cachedNativeBlockVisualStateTextures.IsCreated &&
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
        cachedNativeBlockVisualStateTextures = BuildNativeBlockVisualStateTextures(runtimeBlockMappings.Length);
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

    private NativeArray<BlockVisualStateTextureMapping> BuildNativeBlockVisualStateTextures(int blockTypeCount)
    {
        int safeBlockTypeCount = Mathf.Max(0, blockTypeCount);
        int mappingCount = safeBlockTypeCount * BlockVisualStateUtility.StateCount;
        BlockVisualStateTextureMapping[] runtimeStateMappings = mappingCount > 0
            ? new BlockVisualStateTextureMapping[mappingCount]
            : Array.Empty<BlockVisualStateTextureMapping>();

        if (mappingCount <= 0)
            return new NativeArray<BlockVisualStateTextureMapping>(runtimeStateMappings, Allocator.Persistent);

        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (!EnsureAtlasGeneratorReady(generator))
            return new NativeArray<BlockVisualStateTextureMapping>(runtimeStateMappings, Allocator.Persistent);

        ApplyBuiltInBlockVisualStateTextures(generator, runtimeStateMappings, safeBlockTypeCount);
        ApplyConfiguredBlockVisualStateTextures(generator, runtimeStateMappings, safeBlockTypeCount);

        return new NativeArray<BlockVisualStateTextureMapping>(runtimeStateMappings, Allocator.Persistent);
    }

    private bool EnsureAtlasGeneratorReady(TextureAtlasGenerator generator)
    {
        if (generator == null)
            return false;

        if ((generator.GeneratedAtlas == null || generator.UvMap.Count == 0) &&
            !generator.TryApplyPersistedAtlasWithoutRebuild())
        {
            generator.GenerateAtlas();
        }

        return generator.GeneratedAtlas != null || generator.UvMap.Count > 0;
    }

    private void ApplyBuiltInBlockVisualStateTextures(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount)
    {
        ApplyAllFacesBlockVisualStateTexture(
            generator,
            stateMappings,
            blockTypeCount,
            BlockType.ledWhiteBlock,
            BlockVisualStateCondition.ElectricalPowered,
            "block/lampon");

        ApplyBatteryFrontVisualStateTexture(generator, stateMappings, blockTypeCount, BlockVisualStateCondition.BatteryCharge25, "block/battery25");
        ApplyBatteryFrontVisualStateTexture(generator, stateMappings, blockTypeCount, BlockVisualStateCondition.BatteryCharge50, "block/battery50");
        ApplyBatteryFrontVisualStateTexture(generator, stateMappings, blockTypeCount, BlockVisualStateCondition.BatteryCharge75, "block/battery75");
        ApplyBatteryFrontVisualStateTexture(generator, stateMappings, blockTypeCount, BlockVisualStateCondition.BatteryCharge100, "block/battery100");
    }

    private void ApplyBatteryFrontVisualStateTexture(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount,
        BlockVisualStateCondition state,
        string entryId)
    {
        if (!TryResolveAtlasUvRectData(generator, entryId, out Vector4 uvRectData))
            return;

        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, BlockType.batteryBlock, state, BlockFace.Front, uvRectData);
    }

    private void ApplyConfiguredBlockVisualStateTextures(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount)
    {
        if (blockData == null || blockData.stateTextureOverrides == null)
            return;

        for (int i = 0; i < blockData.stateTextureOverrides.Count; i++)
        {
            BlockStateTextureDefinition definition = blockData.stateTextureOverrides[i];
            if (definition == null || !BlockVisualStateUtility.IsValidState(definition.condition))
                continue;

            if (!string.IsNullOrWhiteSpace(definition.allFacesEntryId))
            {
                ApplyAllFacesBlockVisualStateTexture(
                    generator,
                    stateMappings,
                    blockTypeCount,
                    definition.blockType,
                    definition.condition,
                    definition.allFacesEntryId);
            }

            ApplyFaceSetBlockVisualStateTextures(
                generator,
                stateMappings,
                blockTypeCount,
                definition.blockType,
                definition.condition,
                definition.entryIds);
        }
    }

    private void ApplyAllFacesBlockVisualStateTexture(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount,
        BlockType blockType,
        BlockVisualStateCondition state,
        string entryId)
    {
        if (!TryResolveAtlasUvRectData(generator, entryId, out Vector4 uvRectData))
            return;

        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Top, uvRectData);
        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Bottom, uvRectData);
        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Right, uvRectData);
        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Left, uvRectData);
        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Front, uvRectData);
        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, BlockFace.Back, uvRectData);
    }

    private void ApplyFaceSetBlockVisualStateTextures(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount,
        BlockType blockType,
        BlockVisualStateCondition state,
        BlockFaceTextureEntryIdSet entryIds)
    {
        if (entryIds == null)
            return;

        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Top, entryIds);
        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Bottom, entryIds);
        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Right, entryIds);
        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Left, entryIds);
        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Front, entryIds);
        ApplyFaceEntryBlockVisualStateTexture(generator, stateMappings, blockTypeCount, blockType, state, BlockFace.Back, entryIds);
    }

    private void ApplyFaceEntryBlockVisualStateTexture(
        TextureAtlasGenerator generator,
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount,
        BlockType blockType,
        BlockVisualStateCondition state,
        BlockFace face,
        BlockFaceTextureEntryIdSet entryIds)
    {
        if (!entryIds.TryGet(face, out string entryId) ||
            !TryResolveAtlasUvRectData(generator, entryId, out Vector4 uvRectData))
        {
            return;
        }

        ApplyResolvedBlockVisualStateTexture(stateMappings, blockTypeCount, blockType, state, face, uvRectData);
    }

    private void ApplyResolvedBlockVisualStateTexture(
        BlockVisualStateTextureMapping[] stateMappings,
        int blockTypeCount,
        BlockType blockType,
        BlockVisualStateCondition state,
        BlockFace face,
        Vector4 uvRectData)
    {
        int index = BlockVisualStateUtility.GetTextureMappingIndex(blockType, state, blockTypeCount);
        if ((uint)index >= (uint)stateMappings.Length)
            return;

        BlockVisualStateTextureMapping mapping = stateMappings[index];
        mapping.SetUvRectData(face, uvRectData);
        stateMappings[index] = mapping;
    }

    private bool TryResolveAtlasUvRectData(string entryId, out Vector4 uvRectData)
    {
        uvRectData = default;
        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (!EnsureAtlasGeneratorReady(generator))
            return false;

        return TryResolveAtlasUvRectData(generator, entryId, out uvRectData);
    }

    private bool TryResolveAtlasUvRectData(TextureAtlasGenerator generator, string entryId, out Vector4 uvRectData)
    {
        uvRectData = default;
        if (generator == null)
            return false;

        if (!generator.TryGetUv(entryId, out Rect uvRect))
            return false;

        uvRectData = BlockAtlasUvUtility.RectToUvRectData(uvRect);
        return BlockAtlasUvUtility.IsValidUvRectData(uvRectData);
    }

    #endregion

}
