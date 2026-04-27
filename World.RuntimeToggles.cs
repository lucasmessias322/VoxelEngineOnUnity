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

            ApplyColliderRangeStateForChunk(kv.Key, chunk, enableBlockColliders && IsCoordInsideColliderDistance(kv.Key, colliderCenter));
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
        RefreshRuntimeMaterialProfileConsumers();
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

    private void ApplyColliderRangeStateForChunk(Vector2Int coord, Chunk chunk, bool chunkIsInsideColliderRange)
    {
        if (chunk == null || chunk.SubchunkCount == 0)
            return;

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

            EnqueueColliderBuild(coord, chunk.generation, i);
        }
    }

    private void ApplyColliderRangeStateForCoord(Vector2Int coord, bool chunkIsInsideColliderRange)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return;

        ApplyColliderRangeStateForChunk(coord, chunk, chunkIsInsideColliderRange);
    }

    private void ApplyColliderRangeStateForColumn(int x, int centerZ, int distance, bool chunkIsInsideColliderRange)
    {
        for (int z = centerZ - distance; z <= centerZ + distance; z++)
            ApplyColliderRangeStateForCoord(new Vector2Int(x, z), chunkIsInsideColliderRange);
    }

    private void ApplyColliderRangeStateForRow(int z, int centerX, int distance, bool chunkIsInsideColliderRange)
    {
        for (int x = centerX - distance; x <= centerX + distance; x++)
            ApplyColliderRangeStateForCoord(new Vector2Int(x, z), chunkIsInsideColliderRange);
    }

    private void RefreshColliderDistanceState(Vector2Int previousColliderCenter, int previousColliderDistance, Vector2Int colliderCenter, int effectiveDistance)
    {
        if (!enableBlockColliders ||
            previousColliderCenter == InvalidChunkCoord ||
            previousColliderDistance < 0 ||
            previousColliderDistance != effectiveDistance)
        {
            RefreshColliderDistanceStateFull(colliderCenter);
            return;
        }

        int deltaX = colliderCenter.x - previousColliderCenter.x;
        int deltaZ = colliderCenter.y - previousColliderCenter.y;
        if (deltaX == 0 && deltaZ == 0)
        {
            RefreshColliderDistanceStateFull(colliderCenter);
            return;
        }

        if (Mathf.Abs(deltaX) > 1 || Mathf.Abs(deltaZ) > 1)
        {
            RefreshColliderDistanceStateFull(colliderCenter);
            return;
        }

        if (deltaX != 0)
        {
            int moveX = deltaX > 0 ? 1 : -1;
            int leavingX = previousColliderCenter.x - effectiveDistance * moveX;
            int enteringX = colliderCenter.x + effectiveDistance * moveX;
            ApplyColliderRangeStateForColumn(leavingX, previousColliderCenter.y, effectiveDistance, false);
            ApplyColliderRangeStateForColumn(enteringX, colliderCenter.y, effectiveDistance, true);
        }

        if (deltaZ != 0)
        {
            int moveZ = deltaZ > 0 ? 1 : -1;
            int leavingZ = previousColliderCenter.y - effectiveDistance * moveZ;
            int enteringZ = colliderCenter.y + effectiveDistance * moveZ;
            ApplyColliderRangeStateForRow(leavingZ, previousColliderCenter.x, effectiveDistance, false);
            ApplyColliderRangeStateForRow(enteringZ, colliderCenter.x, effectiveDistance, true);
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }

    private void RefreshColliderDistanceStateFull(Vector2Int colliderCenter)
    {
        foreach (var kv in activeChunks)
            ApplyColliderRangeStateForChunk(kv.Key, kv.Value, enableBlockColliders && IsCoordInsideColliderDistance(kv.Key, colliderCenter));

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }
}
