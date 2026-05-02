using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Chunk Queue & Processing

    private void ProcessChunkQueue(float budgetSecondsOverride = -1f)
    {
        float pipelineStartTime = Time.realtimeSinceStartup;
        float pipelineBudgetSeconds = GetBudgetSeconds(frameTimeBudgetMS);
        if (budgetSecondsOverride >= 0f && !float.IsPositiveInfinity(budgetSecondsOverride))
        {
            pipelineBudgetSeconds = pipelineBudgetSeconds > 0f
                ? Mathf.Min(pipelineBudgetSeconds, budgetSecondsOverride)
                : budgetSecondsOverride;
        }

        PrioritizePendingJobsByDistance();
        TryRequestUrgentPlayerChunk(GetCurrentPlayerChunkCoord());

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessPendingChunkRequests(pipelineStartTime, pipelineBudgetSeconds);

        ProcessCompletedDataStage(pipelineStartTime, pipelineBudgetSeconds);

        ProcessCompletedLightingStage(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessPendingMeshScheduleStage(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessCompletedMeshApplyStage(pipelineStartTime, pipelineBudgetSeconds);
    }

    private void ProcessQueuedChunkDetailPromotions(float updateFrameStartTime, float updateBudgetSeconds)
    {
        if (!enableChunkDetailLod)
            return;

        Vector2Int center = GetCurrentPlayerChunkCoord();
        SyncChunkDetailPromotionSettings(center);
        if (queuedChunkDetailPromotions.Count == 0)
            return;

        if (!HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            return;

        if (ShouldPauseDetailedChunkPromotions(center))
            return;

        Camera priorityCamera = enableChunkDetailPromotionCameraPrioritization ? ResolveMeshApplyPriorityCamera() : null;
        bool hasPriorityCamera = priorityCamera != null;
        if (hasPriorityCamera)
            GeometryUtility.CalculateFrustumPlanes(priorityCamera, meshApplyPriorityFrustumPlanes);

        if (chunkDetailPromotionIntervalSeconds > 0f &&
            Time.time - lastChunkDetailPromotionRequestTime < chunkDetailPromotionIntervalSeconds)
        {
            return;
        }

        int processed = 0;
        int perFrameLimit = Mathf.Max(1, maxChunkDetailPromotionsPerFrame);
        int attempts = queuedChunkDetailPromotions.Count;

        while (processed < perFrameLimit && attempts-- > 0 && queuedChunkDetailPromotions.Count > 0)
        {
            if (!HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
                break;

            if (!TryDequeueBestChunkDetailPromotion(center, priorityCamera, hasPriorityCamera, out Vector2Int coord, out Chunk chunk))
                break;

            if (TryGetVisualOnlyChunkDetailPromotionMask(coord, chunk, out int dirtySubchunkMask))
            {
                if (dirtySubchunkMask == 0)
                {
                    CommitChunkDetailGenerationTargetWithoutRebuild(chunk, true);
                    processed++;
                    continue;
                }

                RequestChunkRebuildImmediate(coord, dirtySubchunkMask, false);
                lastChunkDetailPromotionRequestTime = Time.time;
                processed++;
                continue;
            }

            bool rebuildColliders = enableBlockColliders && IsChunkInsideColliderDistance(coord);
            RequestChunkRebuildImmediate(coord, GetFullSubchunkMask(), rebuildColliders);
            lastChunkDetailPromotionRequestTime = Time.time;
            processed++;
        }
    }

    private static float GetBudgetSeconds(float budgetMS)
    {
        return budgetMS > 0f ? budgetMS / 1000f : 0f;
    }

    private static bool HasPipelineAndStageBudgetRemaining(
        float pipelineStartTime,
        float pipelineBudgetSeconds,
        float stageStartTime,
        float stageBudgetSeconds)
    {
        return HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds) &&
               HasUpdateBudgetRemaining(stageStartTime, stageBudgetSeconds);
    }

    private void ProcessPendingChunkRequests(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkDataScheduleBudgetMS);
        int processed = 0;
        int subchunksPerChunk = Mathf.Max(1, Chunk.SubchunksPerColumn);
        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();
        if (pendingChunks.Count == 0)
            EnsurePendingChunkCoverage(currentChunkCoord);

        if (pendingChunks.Count == 0)
            return;

        if (ShouldPauseChunkDataScheduling(currentChunkCoord))
            return;

        int pendingDataInRange = CountPendingDataJobsInRenderDistance(currentChunkCoord);
        int pendingMeshBuildsInRange = CountPendingMeshBuildRequestsInRenderDistance(currentChunkCoord);
        int pendingMeshesInRange = CountPendingMeshesInRenderDistance(currentChunkCoord);
        int realPending = pendingDataInRange +
                          pendingMeshBuildsInRange +
                          Mathf.CeilToInt(pendingMeshesInRange / (float)subchunksPerChunk);
        int hardPendingDataLimit = Mathf.Max(maxPendingDataJobs, maxPendingDataJobs * 3);
        bool jobsCongested = realPending > maxChunksPerFrame * 4;

        if (jobsCongested)
            return;

        while (processed < maxChunksPerFrame && pendingChunks.Count > 0)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;

            if (pendingDataInRange >= maxPendingDataJobs || pendingDataJobs.Count >= hardPendingDataLimit)
                break;

            Vector2Int coord = pendingChunks.Dequeue();
            pendingChunkSet.Remove(coord);

            if (!IsCoordInsideRenderDistance(coord, currentChunkCoord))
                continue;

            if (activeChunks.ContainsKey(coord) || HasScheduledChunkPipelineWork(coord))
                continue;

            if (!RequestChunk(coord))
            {
                EnqueuePendingChunk(coord);
                break;
            }

            pendingDataInRange++;
            processed++;
        }
    }

    private bool TryRequestUrgentPlayerChunk(Vector2Int currentChunkCoord)
    {
        if (currentChunkCoord == InvalidChunkCoord ||
            activeChunks.ContainsKey(currentChunkCoord) ||
            HasScheduledChunkPipelineWork(currentChunkCoord))
        {
            return false;
        }

        pendingChunkSet.Remove(currentChunkCoord);
        pendingJobPrioritiesDirty = true;

        if (RequestChunk(currentChunkCoord, true))
            return true;

        EnqueuePendingChunk(currentChunkCoord);
        return false;
    }

    private void ProcessCompletedDataStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingDataJobs.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkDataCompletionBudgetMS);
        Vector2Int urgentChunkCoord = GetCurrentPlayerChunkCoord();
        int dataProcessedThisFrame = 0;
        int dataCompletionsLimit = Mathf.Max(1, maxDataCompletionsPerFrame);
        int i = 0;

        while (i < pendingDataJobs.Count)
        {
            var pd = pendingDataJobs[i];
            bool urgentPlayerChunk = pd.coord == urgentChunkCoord;
            if (!urgentPlayerChunk &&
                !HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
            {
                break;
            }

            if (!urgentPlayerChunk && dataProcessedThisFrame >= dataCompletionsLimit)
                break;

            if (pd.terrainStageCompleted)
            {
                i++;
                continue;
            }

            if (!pd.terrainHandle.IsCompleted && !urgentPlayerChunk)
            {
                i++;
                continue;
            }

            pd.terrainHandle.Complete();
            MeshGenerator.ReleaseDataJobTempBuffers(ref pd.tempBuffers);
            pd.terrainStageCompleted = true;
            pendingDataJobs[i] = pd;
            dataProcessedThisFrame++;
            i++;
        }
    }

    private void ProcessCompletedLightingStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingDataJobs.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkLightingCompletionBudgetMS);
        Vector2Int urgentChunkCoord = GetCurrentPlayerChunkCoord();
        int lightingProcessedThisFrame = 0;
        int lightingCompletionsLimit = Mathf.Max(1, maxLightingCompletionsPerFrame);
        int i = 0;

        while (i < pendingDataJobs.Count)
        {
            var pd = pendingDataJobs[i];
            bool urgentPlayerChunk = pd.coord == urgentChunkCoord;
            if (!urgentPlayerChunk &&
                !HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
            {
                break;
            }

            if (!urgentPlayerChunk && lightingProcessedThisFrame >= lightingCompletionsLimit)
                break;

            if (!pd.terrainStageCompleted)
            {
                i++;
                continue;
            }

            if (!pd.lightingStageCompleted)
            {
                if (!pd.lightingHandle.IsCompleted && !urgentPlayerChunk)
                {
                    i++;
                    continue;
                }

                pd.lightingHandle.Complete();
                pd.lightingStageCompleted = true;
                lightingProcessedThisFrame++;
            }

            bool completedPostOverrideRefresh = pd.postOverrideRefreshScheduled;
            if (completedPostOverrideRefresh)
                DisposePostCompletionOverrideInputs(ref pd);
            else
                DisposeCompletedDataJobInputs(ref pd);

            bool hasActiveChunk = activeChunks.TryGetValue(pd.coord, out Chunk activeChunk);
            bool isLatestGeneration = hasActiveChunk && activeChunk.generation == pd.expectedGen;
            bool hasNewerRebuildQueued = HasQueuedChunkRebuild(pd.coord);

            if (isLatestGeneration)
            {
                if (hasNewerRebuildQueued)
                {
                    DisposeDataJobResources(ref pd);
                }
                else
                {
                    bool hadVoxelSnapshot = activeChunk.hasVoxelSnapshot;

                    if (!completedPostOverrideRefresh &&
                        TrySchedulePostCompletionOverrideRefresh(ref pd, activeChunk))
                    {
                        pendingDataJobs[i] = pd;
                        i++;
                        continue;
                    }

                    int resolvedVisualSubchunksPerRenderer = GetResolvedVisualSubchunksPerRenderer();
                    if (!activeChunk.HasInitializedSubchunks ||
                        activeChunk.visualSubchunksPerRenderer != resolvedVisualSubchunksPerRenderer)
                    {
                        activeChunk.InitializeSubchunks(ActiveWorldMaterials, resolvedVisualSubchunksPerRenderer);
                    }
                    else
                    {
                        RefreshChunkMaterialProfile(activeChunk, ActiveWorldMaterials);
                        activeChunk.UpdateWorldBounds();
                    }
                    ApplyChunkBiomeTint(activeChunk, pd.coord);
                    activeChunk.hasVoxelData = true;
                    activeChunk.hasDetailedGenerationData = ShouldGenerateSpaghettiCavesForChunk(pd.targetDetailedGeneration);
                    activeChunk.state = Chunk.ChunkState.MeshReady;
                    if (completedPostOverrideRefresh)
                        SyncCurrentBlockOverridesToVoxelSnapshot(pd.coord, pd.borderSize, activeChunk.voxelData);
                    activeChunk.hasVoxelSnapshot = true;
                    activeChunk.UpdateLightSnapshot(pd.light, pd.borderSize);
                    if (enableVoxelLighting)
                    {
                        SyncChunkBlockLightColumns(pd.coord, pd.light, pd.borderSize);
                        if (!hadVoxelSnapshot && ChunkHasBoundaryBlockLight(pd.light, pd.borderSize))
                            RequestNeighborChunkLightingRefresh(pd.coord);
                    }

                    pendingMeshBuildRequests.Add(pd);
                    pendingJobPrioritiesDirty = true;
                }
            }
            else
            {
                DisposeDataJobResources(ref pd);
            }

            pendingDataJobs[i] = pd;
            RemovePendingDataJobAtSwapBack(i);
            if (hasActiveChunk)
                RefreshChunkJobTracking(pd.coord, activeChunk);
        }
    }

    private void ProcessPendingMeshScheduleStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingMeshBuildRequests.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkMeshScheduleBudgetMS);
        int processed = 0;
        int perFrameLimit = Mathf.Max(1, maxMeshSchedulesPerFrame);
        int i = 0;

        while (i < pendingMeshBuildRequests.Count)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (processed >= perFrameLimit)
                break;

            PendingData pd = pendingMeshBuildRequests[i];
            bool hasActiveChunk = activeChunks.TryGetValue(pd.coord, out Chunk activeChunk);
            bool canScheduleMesh = hasActiveChunk &&
                                   activeChunk.generation == pd.expectedGen &&
                                   !HasQueuedChunkRebuild(pd.coord);

            if (canScheduleMesh)
                ScheduleSubchunkMeshJobs(ref pd, activeChunk);
            else
                DisposeDataJobResources(ref pd);

            pendingMeshBuildRequests[i] = pd;
            RemovePendingMeshBuildRequestAtSwapBack(i);
            processed++;

            if (hasActiveChunk)
                RefreshChunkJobTracking(pd.coord, activeChunk);
        }
    }

    private void ProcessCompletedMeshApplyStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingMeshes.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkMeshApplyBudgetMS);
        Camera priorityCamera = enableSmartMeshApplyPrioritization ? ResolveMeshApplyPriorityCamera() : null;
        bool hasPriorityCamera = priorityCamera != null;
        if (hasPriorityCamera)
            GeometryUtility.CalculateFrustumPlanes(priorityCamera, meshApplyPriorityFrustumPlanes);

        while (pendingMeshes.Count > 0)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (meshesAppliedThisFrame >= maxMeshAppliesPerFrame)
                break;

            int selectedIndex = SelectNextPendingMeshApplyIndex(priorityCamera, hasPriorityCamera);
            if (selectedIndex < 0)
                break;

            var pm = pendingMeshes[selectedIndex];
            if (!pm.jobCompleted)
            {
                pm.handle.Complete();
                pm.jobCompleted = true;
                pendingMeshes[selectedIndex] = pm;
            }

            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;

            bool hasActiveChunk = activeChunks.TryGetValue(pm.coord, out Chunk activeChunk);
            bool canApplyMesh = hasActiveChunk &&
                                activeChunk.generation == pm.expectedGen &&
                                !HasQueuedChunkRebuild(pm.coord);
            if (canApplyMesh)
            {
                if (activeChunk.TryGetVisualSlice(pm.visualSliceIndex, out ChunkRenderSlice visualSlice))
                {
                    if (pm.lightingOnlyRebuild)
                    {
                        if (visualSlice.ApplyRelitVertexData(pm.vertices))
                            meshesAppliedThisFrame++;
                    }
                    else
                    {
                        bool updatedSectionVisibility = false;
                        int sliceMask = activeChunk.GetVisualSliceMask(pm.visualSliceIndex);
                        for (int subchunkIndex = 0; subchunkIndex < Chunk.SubchunksPerColumn; subchunkIndex++)
                        {
                            int subchunkBit = 1 << subchunkIndex;
                            if ((sliceMask & subchunkBit) == 0 || (pm.dirtySubchunkMask & subchunkBit) == 0)
                                continue;

                            MeshGenerator.SubchunkMeshRange range = pm.subchunkRanges[subchunkIndex];
                            activeChunk.SetSubchunkLightingOnlyRebuildSupport(
                                subchunkIndex,
                                range.supportsLightingOnlyRebuild != 0);

                            if (activeChunk.SetSubchunkVisibilityData(subchunkIndex, pm.subchunkVisibilityMasks[subchunkIndex]))
                                updatedSectionVisibility = true;

                            bool hasStaticGeometry = range.vertexCount > 0;
                            bool hasSolidColliderGeometry = activeChunk.HasSubchunkColliderOccupancy(subchunkIndex);
                            activeChunk.SetSubchunkMeshState(subchunkIndex, hasStaticGeometry, hasSolidColliderGeometry);
                            if (hasStaticGeometry)
                            {
                                ApplyCachedSectionVisibility(pm.coord, subchunkIndex, activeChunk);
                            }

                            if (pm.buildColliders)
                            {
                                if (hasSolidColliderGeometry && IsChunkInsideColliderDistance(pm.coord))
                                {
                                    if (!activeChunk.TryActivateCachedSubchunkColliders(subchunkIndex))
                                    {
                                        activeChunk.MarkSubchunkColliderDataDirty(subchunkIndex);
                                        EnqueueColliderBuild(pm.coord, pm.expectedGen, subchunkIndex);
                                    }
                                    else
                                        activeChunk.SetSubchunkColliderSystemEnabled(subchunkIndex, true);
                                }
                                else
                                    activeChunk.ClearSubchunkColliderData(subchunkIndex);
                            }
                        }

                        visualSlice.ApplyMeshData(
                            pm.vertices,
                            pm.opaqueTriangles,
                            pm.transparentTriangles,
                            pm.billboardTriangles,
                            pm.waterTriangles,
                            pm.subchunkRanges,
                            activeChunk);
                        activeChunk.RefreshVisualSliceVisibility(pm.visualSliceIndex);
                        activeChunk.SyncDynamicBlockVisuals(blockData);
                        meshesAppliedThisFrame++;

                        if (updatedSectionVisibility)
                            InvalidateSectionOcclusionGraph();
                    }
                }
            }

            DisposePendingMesh(pm);
            RemovePendingMeshAtSwapBack(selectedIndex);
            if (hasActiveChunk)
            {
                RefreshChunkJobTracking(pm.coord, activeChunk);
            }
        }
    }

    #endregion

}
