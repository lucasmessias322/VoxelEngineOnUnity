using UnityEngine;

public partial class World
{
    private void HandleBlockColliderToggle()
    {
        if (lastEnableBlockColliders == enableBlockColliders)
            return;

        lastEnableBlockColliders = enableBlockColliders;
        Vector2Int colliderCenter = GetCurrentPlayerChunkCoord();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.SubchunkCount == 0)
                continue;

            bool chunkIsInsideColliderRange = enableBlockColliders && IsCoordInsideColliderDistance(kv.Key, colliderCenter);
            for (int i = 0; i < chunk.SubchunkCount; i++)
            {
                chunk.SetSubchunkColliderSystemEnabled(i, chunkIsInsideColliderRange);

                if (chunkIsInsideColliderRange &&
                    chunk.hasVoxelData &&
                    chunk.HasSubchunkGeometry(i) &&
                    chunk.CanSubchunkHaveColliders(i) &&
                    !chunk.HasSubchunkColliderData(i))
                {
                    EnqueueColliderBuild(kv.Key, chunk.generation, i);
                }
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);

        if (!enableBlockColliders)
        {
            queuedColliderBuilds.Clear();
            queuedColliderBuildsByKey.Clear();
            return;
        }

        foreach (var kv in activeChunks)
            RequestHighBuildMeshRebuild(kv.Key);
    }

    private void HandleRealisticShaderToggle()
    {
        bool realisticShaderChanged = lastEnableRealisticShader != enableRealisticShader;
        if (!realisticShaderChanged)
            return;

        lastEnableRealisticShader = enableRealisticShader;

        if (realisticShaderChanged)
            RefreshRealisticShaderModeOnRenderers();
    }

    private void HandleWorldMaterialProfileToggle()
    {
        int currentMaterialProfileHash = ComputeWorldMaterialProfileHash();
        if (lastWorldMaterialProfileHash == currentMaterialProfileHash)
            return;

        lastWorldMaterialProfileHash = currentMaterialProfileHash;
        RefreshWorldMaterialProfileOnRenderers();
    }

    private void RefreshRealisticShaderModeOnRenderers()
    {
        foreach (var kv in activeChunks)
            ApplyChunkBiomeTint(kv.Value, kv.Key);

        foreach (var kv in highBuildMeshes)
        {
            HighBuildMeshData data = kv.Value;
            if (data?.meshRenderer == null)
                continue;

            ApplyBiomeTintToRenderer(data.meshRenderer, new Vector2Int(kv.Key.x, kv.Key.z));
            ApplyRealisticShaderRendererSettings(data.meshRenderer);
        }
    }

    private void HandleVisualFeatureToggle()
    {
        bool lightingChanged = lastEnableVoxelLighting != enableVoxelLighting;
        bool aoChanged = lastEnableAmbientOcclusion != enableAmbientOcclusion;
        bool waterChanged = lastEnableWater != enableWater;
        bool chunkDetailLodChanged = lastEnableChunkDetailLod != enableChunkDetailLod ||
                                     lastChunkDetailLodDistance != chunkDetailLodDistance;
        bool leafQualityChanged = lastTreeLeafQuality != treeLeafQuality;
        int currentLeafFoliageSettingsHash = ComputeTreeLeafFoliageSettingsHash();
        bool leafFoliageSettingsChanged = lastTreeLeafFoliageSettingsHash != currentLeafFoliageSettingsHash;
        bool horizontalLightingChanged = enableVoxelLighting && lastEnableHorizontalSkylight != enableHorizontalSkylight;
        bool horizontalLightingParamsChanged =
            enableVoxelLighting &&
            enableHorizontalSkylight &&
            (lastHorizontalSkylightStepLoss != horizontalSkylightStepLoss ||
             lastSunlightSmoothingPadding != sunlightSmoothingPadding);

        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastEnableWater = enableWater;
        lastEnableChunkDetailLod = enableChunkDetailLod;
        lastChunkDetailLodDistance = chunkDetailLodDistance;
        lastTreeLeafQuality = treeLeafQuality;
        lastTreeLeafFoliageSettingsHash = currentLeafFoliageSettingsHash;
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;

        if (!enableWater && waterChanged)
        {
            queuedWaterUpdates.Clear();
            queuedWaterUpdateSet.Clear();
        }

        if (chunkDetailLodChanged)
            ClearChunkDetailPromotionQueue();

        if (!lightingChanged &&
            !aoChanged &&
            !waterChanged &&
            !chunkDetailLodChanged &&
            !leafQualityChanged &&
            !leafFoliageSettingsChanged &&
            !horizontalLightingChanged &&
            !horizontalLightingParamsChanged)
            return;

        foreach (var kv in activeChunks)
            RequestFullChunkRebuild(kv.Key, false);
    }

    private void RefreshColliderDistanceState(Vector2Int colliderCenter)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.SubchunkCount == 0)
                continue;

            bool chunkIsInsideColliderRange = enableBlockColliders && IsCoordInsideColliderDistance(kv.Key, colliderCenter);
            for (int i = 0; i < chunk.SubchunkCount; i++)
            {
                if (!chunkIsInsideColliderRange)
                {
                    chunk.SetSubchunkColliderSystemEnabled(i, false);
                    continue;
                }

                if (!chunk.hasVoxelData ||
                    !chunk.voxelData.IsCreated ||
                    !chunk.HasSubchunkGeometry(i) ||
                    !chunk.CanSubchunkHaveColliders(i))
                {
                    chunk.SetSubchunkColliderSystemEnabled(i, false);
                    continue;
                }

                if (chunk.HasSubchunkColliderData(i))
                {
                    chunk.SetSubchunkColliderSystemEnabled(i, true);
                    continue;
                }

                EnqueueColliderBuild(kv.Key, chunk.generation, i);
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }
}
