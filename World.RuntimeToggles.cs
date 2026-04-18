using UnityEngine;

public partial class World
{
    private void HandleBlockColliderToggle()
    {
        if (lastEnableBlockColliders == enableBlockColliders)
            return;

        lastEnableBlockColliders = enableBlockColliders;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.SubchunkCount == 0)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.SubchunkCount; i++)
            {
                chunk.SetSubchunkColliderSystemEnabled(i, chunkIsSimulated);

                if (chunkIsSimulated &&
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
        bool emissivePointLightsChanged = lastEnableEmissiveBlockPointLights != enableEmissiveBlockPointLights;
        if (!realisticShaderChanged && !emissivePointLightsChanged)
            return;

        lastEnableRealisticShader = enableRealisticShader;
        lastEnableEmissiveBlockPointLights = enableEmissiveBlockPointLights;

        if (realisticShaderChanged)
            RefreshRealisticShaderModeOnRenderers();

        emissiveBlockLightController?.RefreshEmissivePointLightState();
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
        lastTreeLeafQuality = treeLeafQuality;
        lastTreeLeafFoliageSettingsHash = currentLeafFoliageSettingsHash;
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;

        if (!enableWater && waterChanged)
        {
            queuedWaterUpdates.Clear();
            queuedWaterUpdateSet.Clear();
        }

        if (!lightingChanged &&
            !aoChanged &&
            !waterChanged &&
            !leafQualityChanged &&
            !leafFoliageSettingsChanged &&
            !horizontalLightingChanged &&
            !horizontalLightingParamsChanged)
            return;

        foreach (var kv in activeChunks)
            RequestFullChunkRebuild(kv.Key, false);
    }

    private void RefreshSimulationDistanceState(Vector2Int simulationCenter)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.SubchunkCount == 0)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.SubchunkCount; i++)
            {
                if (!chunkIsSimulated)
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
