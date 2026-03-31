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
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
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

    private void HandleVisualFeatureToggle()
    {
        bool lightingChanged = lastEnableVoxelLighting != enableVoxelLighting;
        bool aoChanged = lastEnableAmbientOcclusion != enableAmbientOcclusion;
        bool horizontalLightingChanged = enableVoxelLighting && lastEnableHorizontalSkylight != enableHorizontalSkylight;
        bool horizontalLightingParamsChanged =
            enableVoxelLighting &&
            enableHorizontalSkylight &&
            (lastHorizontalSkylightStepLoss != horizontalSkylightStepLoss ||
             lastSunlightSmoothingPadding != sunlightSmoothingPadding);

        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;

        if (!lightingChanged && !aoChanged && !horizontalLightingChanged && !horizontalLightingParamsChanged)
            return;

        foreach (var kv in activeChunks)
            RequestChunkRebuild(kv.Key, GetFullSubchunkMask(), false);
    }

    private void RefreshSimulationDistanceState(Vector2Int simulationCenter)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
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
