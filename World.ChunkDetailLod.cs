using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Chunk Detail LOD

    private Vector2Int GetCurrentPlayerChunkCoord()
    {
        if (player == null)
            return _lastChunkCoord;

        return new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );
    }

    private float GetChunkDistanceSqToPlayer(Vector2Int coord)
    {
        if (player == null)
        {
            float fallbackDx = coord.x - _lastChunkCoord.x;
            float fallbackDz = coord.y - _lastChunkCoord.y;
            return fallbackDx * fallbackDx + fallbackDz * fallbackDz;
        }

        // Usa distancia em coordenadas de chunk com posicao fracionaria do player.
        float playerChunkX = player.position.x / Chunk.SizeX;
        float playerChunkZ = player.position.z / Chunk.SizeZ;
        float centerX = coord.x + 0.5f;
        float centerZ = coord.y + 0.5f;
        float dx = centerX - playerChunkX;
        float dz = centerZ - playerChunkZ;
        return dx * dx + dz * dz;
    }

    private bool IsCoordInsideRenderDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideCircularDistance(coord, center, renderDistance);
    }

    private bool ShouldChunkUseDetailedGeneration(Vector2Int coord)
    {
        return ShouldChunkUseDetailedGeneration(coord, GetCurrentPlayerChunkCoord());
    }

    private bool ShouldChunkUseDetailedGeneration(Vector2Int coord, Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        return IsCoordInsideCircularDistance(coord, center, Mathf.Max(0, chunkDetailLodDistance));
    }

    private bool ShouldChunkStartDetailedGeneration(Vector2Int coord)
    {
        return ShouldChunkStartDetailedGeneration(coord, GetCurrentPlayerChunkCoord());
    }

    private bool ShouldChunkStartDetailedGeneration(Vector2Int coord, Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        if (!ShouldChunkUseDetailedGeneration(coord, center))
            return false;

        int immediateDistance = Mathf.Clamp(chunkImmediateDetailDistance, 0, Mathf.Max(0, chunkDetailLodDistance));
        if (immediateDistance > 0 && IsCoordInsideCircularDistance(coord, center, immediateDistance))
            return true;

        return !ShouldPauseDetailedChunkPromotions(center);
    }

    private SpaghettiCaveSettings GetSpaghettiCaveSettingsForChunk(bool useDetailedGeneration)
    {
        if (ShouldGenerateSpaghettiCavesForChunk(useDetailedGeneration))
            return caveSpaghettiSettings;

        SpaghettiCaveSettings simplifiedSettings = caveSpaghettiSettings;
        simplifiedSettings.enabled = false;
        return simplifiedSettings;
    }

    private bool ShouldGenerateSpaghettiCavesForChunk(bool useDetailedGeneration)
    {
        return useDetailedGeneration || generateSpaghettiCavesInLodChunks;
    }

    private bool HasCompatibleGenerationDataForRequestedDetail(Chunk chunk, bool useDetailedGeneration)
    {
        if (chunk == null)
            return false;

        // Detailed voxel data is a superset of the simplified visual LOD data, so it
        // can be reused for both paths. Only the promotion to detailed visuals needs
        // to verify that the detailed voxel variant already exists.
        return !useDetailedGeneration || chunk.hasDetailedGenerationData;
    }

    private static bool ShouldDeferChunkDetailGenerationSwap(Chunk chunk, bool targetDetailedGeneration)
    {
        if (chunk == null || chunk.requestedDetailedGeneration == targetDetailedGeneration)
            return false;

        if (!chunk.HasInitializedSubchunks)
            return false;

        return chunk.state == Chunk.ChunkState.Active || chunk.state == Chunk.ChunkState.MeshReady;
    }

    private static void PrepareChunkDetailGenerationTarget(Chunk chunk, bool targetDetailedGeneration, int expectedGen)
    {
        if (chunk == null)
            return;

        if (ShouldDeferChunkDetailGenerationSwap(chunk, targetDetailedGeneration))
        {
            chunk.SetPendingDetailedGenerationSwap(targetDetailedGeneration, expectedGen);
            return;
        }

        chunk.requestedDetailedGeneration = targetDetailedGeneration;
        chunk.ClearPendingDetailedGenerationSwap();
    }

    private bool ShouldGenerateGrassBillboardsForChunk(bool useDetailedGeneration)
    {
        return enableGrassBillboards && useDetailedGeneration && !IsFlatWorldMode();
    }

    private static bool ShouldGenerateDetailedLeafFoliageForChunk(bool useDetailedGeneration)
    {
        return useDetailedGeneration;
    }

    private bool ShouldPauseDetailedChunkPromotions(Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        float lastRelevantMovementTime = lastPlayerChunkCoordChangeTime;
        if (chunkDetailPromotionMaxPlayerSpeed > 0f)
            lastRelevantMovementTime = Mathf.Max(lastRelevantMovementTime, lastPlayerChunkDetailMovementTime);

        if (Time.time - lastRelevantMovementTime < Mathf.Max(0f, chunkDetailPromotionDelaySeconds))
            return true;

        int pendingDataInRange = CountPendingDataJobsInRenderDistance(center);
        int pendingMeshBuildsInRange = CountPendingMeshBuildRequestsInRenderDistance(center);
        int pendingMeshesInRange = CountPendingMeshesInRenderDistance(center);
        int pendingDataLimit = Mathf.Max(1, maxPendingDataJobs * 2);
        int pendingMeshBuildLimit = Mathf.Max(1, maxMeshBuildRequestBacklog * 2);
        int pendingMeshLimit = Mathf.Max(1, maxPendingMeshJobBacklog);

        if (pendingDataInRange >= pendingDataLimit)
            return true;

        if (pendingMeshBuildsInRange >= pendingMeshBuildLimit)
            return true;

        if (pendingMeshesInRange >= pendingMeshLimit)
            return true;

        return false;
    }

    private void RebuildChunkDetailPromotionQueue(Vector2Int center)
    {
        ClearChunkDetailPromotionQueue();
        lastChunkDetailPromotionQueueCenter = center;

        if (!enableChunkDetailLod || activeChunks == null || activeChunks.Count == 0)
            return;

        foreach (var kv in activeChunks)
            EnqueueChunkDetailPromotionIfActiveAndNeeded(kv.Key, center);
    }

    private void AppendChunkDetailPromotionFrontier(Vector2Int previousCenter, Vector2Int currentCenter)
    {
        lastChunkDetailPromotionQueueCenter = currentCenter;
        if (!enableChunkDetailLod)
        {
            ClearChunkDetailPromotionQueue();
            return;
        }

        int radius = Mathf.Max(0, chunkDetailLodDistance);
        int minX = currentCenter.x - radius;
        int maxX = currentCenter.x + radius;
        int minZ = currentCenter.y - radius;
        int maxZ = currentCenter.y + radius;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2Int coord = new Vector2Int(x, z);
                if (!IsCoordInsideCircularDistance(coord, currentCenter, radius))
                    continue;

                if (IsCoordInsideCircularDistance(coord, previousCenter, radius))
                    continue;

                EnqueueChunkDetailPromotionIfActiveAndNeeded(coord, currentCenter);
            }
        }
    }

    private void EnqueueChunkDetailPromotionIfActiveAndNeeded(Vector2Int coord, Vector2Int center)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk activeChunk) || activeChunk == null)
            return;

        if (!ShouldChunkUseDetailedGeneration(coord, center))
            return;

        if (activeChunk.IsTargetingDetailedGeneration)
            return;

        EnqueueChunkDetailPromotion(coord);
    }

    private void SyncChunkDetailPromotionSettings(Vector2Int center)
    {
        bool settingsChanged = lastEnableChunkDetailLod != enableChunkDetailLod ||
                               lastChunkDetailLodDistance != chunkDetailLodDistance;
        if (!settingsChanged)
            return;

        lastEnableChunkDetailLod = enableChunkDetailLod;
        lastChunkDetailLodDistance = chunkDetailLodDistance;

        if (!enableChunkDetailLod)
        {
            ClearChunkDetailPromotionQueue();
            lastChunkDetailPromotionQueueCenter = center;
            return;
        }

        RebuildChunkDetailPromotionQueue(center);
    }

    private void UpdateChunkDetailPromotionMovementState()
    {
        if (player == null)
        {
            hasChunkDetailPromotionMovementSample = false;
            lastChunkDetailPromotionSampleRealtime = float.NegativeInfinity;
            return;
        }

        Vector3 currentPosition = player.position;
        currentPosition.y = 0f;
        float currentRealtime = Time.unscaledTime;

        if (!hasChunkDetailPromotionMovementSample)
        {
            hasChunkDetailPromotionMovementSample = true;
            lastChunkDetailPromotionSamplePosition = currentPosition;
            lastChunkDetailPromotionSampleRealtime = currentRealtime;
            return;
        }

        float deltaTime = currentRealtime - lastChunkDetailPromotionSampleRealtime;
        if (deltaTime > 0f && chunkDetailPromotionMaxPlayerSpeed > 0f)
        {
            float horizontalSpeed = Vector3.Distance(currentPosition, lastChunkDetailPromotionSamplePosition) / deltaTime;
            if (horizontalSpeed >= chunkDetailPromotionMaxPlayerSpeed)
                lastPlayerChunkDetailMovementTime = Time.time;
        }

        lastChunkDetailPromotionSamplePosition = currentPosition;
        lastChunkDetailPromotionSampleRealtime = currentRealtime;
    }

    private void EnqueueChunkDetailPromotion(Vector2Int coord)
    {
        if (!queuedChunkDetailPromotionsSet.Add(coord))
            return;

        queuedChunkDetailPromotions.Enqueue(coord);
    }

    private void ClearChunkDetailPromotionQueue()
    {
        queuedChunkDetailPromotions.Clear();
        queuedChunkDetailPromotionsSet.Clear();
    }

    private static void CommitChunkDetailGenerationTargetWithoutRebuild(Chunk chunk, bool targetDetailedGeneration)
    {
        if (chunk == null)
            return;

        chunk.requestedDetailedGeneration = targetDetailedGeneration;
        chunk.ClearPendingDetailedGenerationSwap();
    }

    private bool TryGetVisualOnlyChunkDetailPromotionMask(Vector2Int coord, Chunk chunk, out int dirtySubchunkMask)
    {
        dirtySubchunkMask = 0;
        if (!HasCompatibleGenerationDataForRequestedDetail(chunk, true) ||
            !CanChunkProvideVoxelSnapshot(chunk))
        {
            return false;
        }

        dirtySubchunkMask = GetGrassBillboardPromotionDirtySubchunkMask(coord, chunk);
        return true;
    }

    private int GetGrassBillboardPromotionDirtySubchunkMask(Vector2Int coord, Chunk chunk)
    {
        if (!enableGrassBillboards ||
            IsFlatWorldMode() ||
            grassBillboardChance <= 0f ||
            !CanChunkProvideVoxelSnapshot(chunk))
        {
            return 0;
        }

        VegetationBillboardRuleData[] vegetationRules = GetActiveVegetationBillboardRules();
        BiomeNoiseSettings biomeNoiseSettings = GetBiomeNoiseSettings();
        NativeArray<byte> voxelData = chunk.voxelData;
        int voxelPlaneSize = Chunk.SizeX * Chunk.SizeZ;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int dirtySubchunkMask = 0;
        int fullMask = GetFullSubchunkMask();

        for (int localZ = 0; localZ < Chunk.SizeZ; localZ++)
        {
            for (int localX = 0; localX < Chunk.SizeX; localX++)
            {
                int voxelIndex = localX + localZ * Chunk.SizeX;
                for (int y = 0; y + 1 < Chunk.SizeY; y++, voxelIndex += voxelPlaneSize)
                {
                    BlockType groundBlockType = ResolveWaterStateForDebug((BlockType)voxelData[voxelIndex]);
                    if (groundBlockType == BlockType.Air)
                        continue;

                    BlockType aboveBlockType = ResolveWaterStateForDebug((BlockType)voxelData[voxelIndex + voxelPlaneSize]);
                    if (aboveBlockType != BlockType.Air)
                        continue;

                    int worldX = chunkMinX + localX;
                    int worldY = y + 1;
                    int worldZ = chunkMinZ + localZ;
                    if (IsGrassBillboardSuppressed(new Vector3Int(worldX, worldY, worldZ)))
                        continue;

                    if (!VegetationBillboardUtility.TryResolveBillboardRule(
                            biomeNoiseSettings,
                            vegetationRules,
                            worldX,
                            worldY,
                            worldZ,
                            groundBlockType,
                            grassBillboardChance,
                            grassBillboardNoiseScale,
                            grassBillboardBlockType,
                            out _,
                            out _))
                    {
                        continue;
                    }

                    int billboardSubchunkIndex = Mathf.Clamp(worldY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
                    dirtySubchunkMask |= 1 << billboardSubchunkIndex;
                    if (dirtySubchunkMask == fullMask)
                        return dirtySubchunkMask;
                }
            }
        }

        return dirtySubchunkMask;
    }

    private ChunkDetailPromotionCandidate EvaluateChunkDetailPromotionCandidate(
        Vector2Int coord,
        Vector2Int center,
        Camera priorityCamera,
        bool hasPriorityCamera)
    {
        ChunkDetailPromotionCandidate candidate = new ChunkDetailPromotionCandidate
        {
            coord = coord,
            chunk = null,
            score = float.NegativeInfinity,
            preferredView = false,
            state = ChunkDetailPromotionCandidateState.Drop
        };

        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return candidate;

        if (!ShouldChunkUseDetailedGeneration(coord, center))
            return candidate;

        if (chunk.IsTargetingDetailedGeneration)
            return candidate;

        candidate.chunk = chunk;
        if (HasQueuedChunkRebuild(coord) || IsChunkJobPending(coord))
        {
            candidate.state = ChunkDetailPromotionCandidateState.RequeueBlocked;
            return candidate;
        }

        candidate.score = ComputeChunkDetailPromotionPriority(coord, chunk, priorityCamera, hasPriorityCamera, out bool preferredView);
        candidate.preferredView = preferredView;
        candidate.state = ChunkDetailPromotionCandidateState.Ready;
        return candidate;
    }

    private float ComputeChunkDetailPromotionPriority(
        Vector2Int coord,
        Chunk chunk,
        Camera priorityCamera,
        bool hasPriorityCamera,
        out bool preferredView)
    {
        float score = -GetChunkDistanceSqToPlayer(coord) * 700f;
        Bounds chunkBounds = GetChunkDetailPromotionBounds(coord, chunk);

        if (ChunkHasAnyVisibleRenderer(chunk))
            score += 80000f;

        preferredView = false;
        if (!enableChunkDetailPromotionCameraPrioritization || !hasPriorityCamera || priorityCamera == null)
            return score;

        bool isInsideFrustum = GeometryUtility.TestPlanesAABB(meshApplyPriorityFrustumPlanes, chunkBounds);
        Transform cameraTransform = priorityCamera.transform;
        Vector3 toChunk = chunkBounds.center - cameraTransform.position;
        float distanceSq = toChunk.sqrMagnitude;
        float forwardDot = 1f;
        if (distanceSq > 0.001f)
            forwardDot = Vector3.Dot(cameraTransform.forward, toChunk / Mathf.Sqrt(distanceSq));

        preferredView = isInsideFrustum || forwardDot >= 0.2f;
        score += isInsideFrustum ? 55000f : -45000f;
        score -= distanceSq * 0.018f;
        score += forwardDot >= 0f ? forwardDot * 18000f : forwardDot * 32000f;

        float cameraY = cameraTransform.position.y;
        if (cameraY >= chunkBounds.min.y - Chunk.SubchunkHeight &&
            cameraY <= chunkBounds.max.y + Chunk.SubchunkHeight)
        {
            score += 6000f;
        }

        return score;
    }

    private Bounds GetChunkDetailPromotionBounds(Vector2Int coord, Chunk chunk)
    {
        if (chunk != null)
        {
            Bounds bounds = chunk.worldBounds;
            bounds.Expand(new Vector3(2f, 2f, 2f));
            return bounds;
        }

        return new Bounds(
            new Vector3(coord.x * Chunk.SizeX + Chunk.SizeX * 0.5f, Chunk.SizeY * 0.5f, coord.y * Chunk.SizeZ + Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, Chunk.SizeY + 2f, Chunk.SizeZ + 2f));
    }

    private bool ChunkHasAnyVisibleRenderer(Chunk chunk)
    {
        if (chunk == null || chunk.subRenderers == null)
            return false;

        for (int i = 0; i < chunk.subRenderers.Length; i++)
        {
            MeshRenderer renderer = chunk.subRenderers[i];
            if (renderer != null && renderer.enabled && renderer.isVisible)
                return true;
        }

        return false;
    }

    private bool TryDequeueBestChunkDetailPromotion(
        Vector2Int center,
        Camera priorityCamera,
        bool hasPriorityCamera,
        out Vector2Int coord,
        out Chunk chunk)
    {
        coord = InvalidChunkCoord;
        chunk = null;
        int queuedCount = queuedChunkDetailPromotions.Count;
        if (queuedCount == 0)
            return false;

        int scanLimit = Mathf.Max(0, chunkDetailPromotionPriorityScanLimit);
        int scanCount = scanLimit > 0 ? Mathf.Min(queuedCount, scanLimit) : queuedCount;
        if (scanCount <= 0)
            scanCount = queuedCount;

        chunkDetailPromotionCandidateBuffer.Clear();
        for (int i = 0; i < scanCount && queuedChunkDetailPromotions.Count > 0; i++)
        {
            Vector2Int candidateCoord = queuedChunkDetailPromotions.Dequeue();
            queuedChunkDetailPromotionsSet.Remove(candidateCoord);
            chunkDetailPromotionCandidateBuffer.Add(EvaluateChunkDetailPromotionCandidate(candidateCoord, center, priorityCamera, hasPriorityCamera));
        }

        bool preferVisibleCandidates =
            enableChunkDetailPromotionCameraPrioritization &&
            deferOutOfViewChunkDetailPromotions &&
            chunkDetailPromotionOutOfViewBacklogThreshold > 0 &&
            queuedCount >= chunkDetailPromotionOutOfViewBacklogThreshold;

        int bestOverallIndex = -1;
        int bestPreferredIndex = -1;
        float bestOverallScore = float.NegativeInfinity;
        float bestPreferredScore = float.NegativeInfinity;
        for (int i = 0; i < chunkDetailPromotionCandidateBuffer.Count; i++)
        {
            ChunkDetailPromotionCandidate candidate = chunkDetailPromotionCandidateBuffer[i];
            if (candidate.state != ChunkDetailPromotionCandidateState.Ready)
                continue;

            if (candidate.score > bestOverallScore)
            {
                bestOverallScore = candidate.score;
                bestOverallIndex = i;
            }

            if (candidate.preferredView && candidate.score > bestPreferredScore)
            {
                bestPreferredScore = candidate.score;
                bestPreferredIndex = i;
            }
        }

        int selectedIndex = preferVisibleCandidates && bestPreferredIndex >= 0
            ? bestPreferredIndex
            : bestOverallIndex;

        for (int i = 0; i < chunkDetailPromotionCandidateBuffer.Count; i++)
        {
            ChunkDetailPromotionCandidate candidate = chunkDetailPromotionCandidateBuffer[i];
            if (i == selectedIndex)
            {
                coord = candidate.coord;
                chunk = candidate.chunk;
                continue;
            }

            if (candidate.state != ChunkDetailPromotionCandidateState.Drop)
                EnqueueChunkDetailPromotion(candidate.coord);
        }

        chunkDetailPromotionCandidateBuffer.Clear();
        return selectedIndex >= 0;
    }

    #endregion

}
