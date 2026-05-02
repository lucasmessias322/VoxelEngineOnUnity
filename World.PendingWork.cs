using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Pending Work & Scheduling

    private void RefreshColliderDistanceStateIfNeeded()
    {
        Vector2Int colliderCenter = GetCurrentPlayerChunkCoord();
        int effectiveDistance = GetEffectiveColliderDistance();
        Vector2Int previousColliderCenter = _lastColliderCenter;
        int previousColliderDistance = _lastColliderDistance;
        if (colliderCenter == _lastColliderCenter && effectiveDistance == _lastColliderDistance)
            return;

        _lastColliderCenter = colliderCenter;
        _lastColliderDistance = effectiveDistance;
        RefreshColliderDistanceState(previousColliderCenter, previousColliderDistance, colliderCenter, effectiveDistance);
    }

    private bool IsChunkInsideSimulationDistance(Vector2Int coord)
    {
        return IsCoordInsideSimulationDistance(coord, GetCurrentPlayerChunkCoord());
    }

    private bool IsChunkInsideColliderDistance(Vector2Int coord)
    {
        return IsCoordInsideColliderDistance(coord, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos)
    {
        return IsWorldPositionInsideSimulationDistance(worldPos, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos, Vector2Int center)
    {
        return IsCoordInsideSimulationDistance(GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z), center);
    }

    private int CountPendingDataJobsInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingDataJobs[i].coord, center))
                count++;
        }

        return count;
    }

    private int CountPendingMeshesInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingMeshes[i].coord, center))
                count++;
        }

        return count;
    }

    private int CountReadyPendingMeshes()
    {
        int count = 0;
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pendingMesh = pendingMeshes[i];
            if (pendingMesh.jobCompleted || pendingMesh.handle.IsCompleted)
                count++;
        }

        return count;
    }

    private int CountPendingMeshBuildRequestsInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingMeshBuildRequests[i].coord, center))
                count++;
        }

        return count;
    }

    private bool ShouldPauseChunkDataScheduling(Vector2Int center)
    {
        int readyMeshBacklog = CountReadyPendingMeshes();
        if (pauseChunkSchedulingWhenMeshesReady && readyMeshBacklog > Mathf.Max(0, maxReadyMeshApplyBacklog))
            return true;

        int meshBuildBacklogLimit = Mathf.Max(1, maxMeshBuildRequestBacklog);
        if (CountPendingMeshBuildRequestsInRenderDistance(center) > meshBuildBacklogLimit)
            return true;

        int meshJobBacklogLimit = Mathf.Max(1, maxPendingMeshJobBacklog);
        if (CountPendingMeshesInRenderDistance(center) > meshJobBacklogLimit)
            return true;

        return false;
    }

    private void PrioritizePendingJobsByDistance()
    {
        Vector2Int priorityCenter = GetCurrentPlayerChunkCoord();
        if (!pendingJobPrioritiesDirty &&
            priorityCenter == _lastPendingJobPriorityCenter &&
            pendingChunks.Count == pendingChunkSet.Count)
            return;

        _lastPendingJobPriorityCenter = priorityCenter;
        pendingJobPrioritiesDirty = false;
        ReprioritizePendingChunkQueue(priorityCenter);

        if (pendingDataJobs.Count > 1)
        {
            if (pendingDataDistanceComparison == null)
                pendingDataDistanceComparison = ComparePendingDataByDistance;

            pendingDataJobs.Sort(pendingDataDistanceComparison);
        }

        if (pendingMeshBuildRequests.Count > 1)
        {
            if (pendingDataDistanceComparison == null)
                pendingDataDistanceComparison = ComparePendingDataByDistance;

            pendingMeshBuildRequests.Sort(pendingDataDistanceComparison);
        }

        if (pendingMeshes.Count > 1)
        {
            if (pendingMeshDistanceComparison == null)
                pendingMeshDistanceComparison = ComparePendingMeshByDistance;

            pendingMeshes.Sort(pendingMeshDistanceComparison);
        }
    }

    private int ComparePendingDataByDistance(PendingData a, PendingData b)
    {
        return GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord));
    }

    private int ComparePendingMeshByDistance(PendingMesh a, PendingMesh b)
    {
        int distCmp = GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord));
        if (distCmp != 0)
            return distCmp;

        return a.visualSliceIndex.CompareTo(b.visualSliceIndex);
    }

    private Camera ResolveMeshApplyPriorityCamera()
    {
        if (cachedMeshApplyPriorityCamera != null && cachedMeshApplyPriorityCamera.isActiveAndEnabled)
            return cachedMeshApplyPriorityCamera;

        cachedMeshApplyPriorityCamera = null;
        if (player != null)
            cachedMeshApplyPriorityCamera = player.GetComponentInChildren<Camera>();

        if (cachedMeshApplyPriorityCamera == null || !cachedMeshApplyPriorityCamera.isActiveAndEnabled)
            cachedMeshApplyPriorityCamera = Camera.main;

        return cachedMeshApplyPriorityCamera != null && cachedMeshApplyPriorityCamera.isActiveAndEnabled
            ? cachedMeshApplyPriorityCamera
            : null;
    }

    private bool TryResolvePendingMeshApplyTarget(PendingMesh pm, out Chunk activeChunk, out ChunkRenderSlice visualSlice)
    {
        visualSlice = null;
        if (!activeChunks.TryGetValue(pm.coord, out activeChunk) || activeChunk == null)
            return false;

        if (activeChunk.generation != pm.expectedGen || HasQueuedChunkRebuild(pm.coord))
            return false;

        return activeChunk.TryGetVisualSlice(pm.visualSliceIndex, out visualSlice) && visualSlice != null;
    }

    private int SelectNextPendingMeshApplyIndex(Camera priorityCamera, bool hasPriorityCamera)
    {
        int bestIndex = -1;
        int staleReadyIndex = -1;
        float bestScore = float.NegativeInfinity;
        int readyCandidatesScanned = 0;
        int scanLimit = Mathf.Max(0, meshApplyPriorityScanLimit);

        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pm = pendingMeshes[i];
            if (!pm.jobCompleted && !pm.handle.IsCompleted)
                continue;

            if (!TryResolvePendingMeshApplyTarget(pm, out Chunk activeChunk, out ChunkRenderSlice visualSlice))
            {
                if (staleReadyIndex < 0)
                    staleReadyIndex = i;
                continue;
            }

            if (!enableSmartMeshApplyPrioritization)
                return i;

            float score = ComputePendingMeshApplyPriority(pm, activeChunk, visualSlice, priorityCamera, hasPriorityCamera);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }

            readyCandidatesScanned++;
            if (scanLimit > 0 && readyCandidatesScanned >= scanLimit)
                break;
        }

        return bestIndex >= 0 ? bestIndex : staleReadyIndex;
    }

    private float ComputePendingMeshApplyPriority(
        PendingMesh pm,
        Chunk activeChunk,
        ChunkRenderSlice visualSlice,
        Camera priorityCamera,
        bool hasPriorityCamera)
    {
        float score = pm.lightingOnlyRebuild ? 12000f : 0f;
        score -= GetChunkDistanceSqToPlayer(pm.coord) * 650f;

        Bounds sliceBounds = GetPendingMeshSliceWorldBounds(pm, activeChunk, visualSlice);
        if (visualSlice != null && visualSlice.meshRenderer != null)
        {
            if (visualSlice.meshRenderer.isVisible)
                score += 90000f;
            if (visualSlice.meshRenderer.enabled)
                score += 6000f;
        }

        if (!hasPriorityCamera || priorityCamera == null)
            return score - pm.visualSliceIndex;

        bool isInsideFrustum = GeometryUtility.TestPlanesAABB(meshApplyPriorityFrustumPlanes, sliceBounds);
        score += isInsideFrustum ? 55000f : -45000f;

        Transform cameraTransform = priorityCamera.transform;
        Vector3 toSlice = sliceBounds.center - cameraTransform.position;
        float distanceSq = toSlice.sqrMagnitude;
        score -= distanceSq * 0.018f;

        if (distanceSq > 0.001f)
        {
            float forwardDot = Vector3.Dot(cameraTransform.forward, toSlice / Mathf.Sqrt(distanceSq));
            score += forwardDot >= 0f ? forwardDot * 14000f : forwardDot * 28000f;
        }

        float cameraY = cameraTransform.position.y;
        if (cameraY >= sliceBounds.min.y - Chunk.SubchunkHeight &&
            cameraY <= sliceBounds.max.y + Chunk.SubchunkHeight)
        {
            score += 8000f;
        }

        return score;
    }

    private Bounds GetPendingMeshSliceWorldBounds(PendingMesh pm, Chunk activeChunk, ChunkRenderSlice visualSlice)
    {
        int startSubchunk = visualSlice != null
            ? visualSlice.StartSubchunkIndex
            : Mathf.Clamp(pm.visualSliceIndex * Mathf.Max(1, activeChunk.visualSubchunksPerRenderer), 0, Chunk.SubchunksPerColumn - 1);
        int endSubchunk = visualSlice != null
            ? visualSlice.EndSubchunkIndexExclusive
            : Mathf.Min(startSubchunk + Mathf.Max(1, activeChunk.visualSubchunksPerRenderer), Chunk.SubchunksPerColumn);

        float startY = startSubchunk * Chunk.SubchunkHeight;
        float endY = Mathf.Min(endSubchunk * Chunk.SubchunkHeight, Chunk.SizeY);
        float height = Mathf.Max(1f, endY - startY);
        Vector3 origin = activeChunk != null ? activeChunk.transform.position : new Vector3(pm.coord.x * Chunk.SizeX, 0f, pm.coord.y * Chunk.SizeZ);

        return new Bounds(
            origin + new Vector3(Chunk.SizeX * 0.5f, startY + height * 0.5f, Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, height + 2f, Chunk.SizeZ + 2f));
    }

    private bool HasOtherPendingMeshJobs(Vector2Int coord, int expectedGen, int excludeIndex)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (i == excludeIndex)
                continue;

            PendingMesh pm = pendingMeshes[i];
            if (pm.coord == coord && pm.expectedGen == expectedGen)
                return true;
        }

        return false;
    }

    private int GetMeshNeighborPadding()
    {
        return 1;
    }

    private void ClearPendingChunkQueue()
    {
        pendingChunks.Clear();
        pendingChunkSet.Clear();
        pendingJobPrioritiesDirty = true;
    }

    private void EnqueuePendingChunk(Vector2Int coord)
    {
        if (activeChunks.ContainsKey(coord) || !pendingChunkSet.Add(coord))
            return;

        pendingChunks.Enqueue(coord);
        pendingJobPrioritiesDirty = true;
    }

    private void EnqueuePendingChunkRing(Vector2Int center, int ring)
    {
        if (ring <= 0)
        {
            if (IsCoordInsideRenderDistance(center, center))
                EnqueuePendingChunk(center);
            return;
        }

        int minX = center.x - ring;
        int maxX = center.x + ring;
        int minZ = center.y - ring;
        int maxZ = center.y + ring;

        for (int x = minX; x <= maxX; x++)
        {
            Vector2Int top = new Vector2Int(x, minZ);
            if (IsCoordInsideRenderDistance(top, center))
                EnqueuePendingChunk(top);
        }

        for (int z = minZ + 1; z <= maxZ - 1; z++)
        {
            Vector2Int right = new Vector2Int(maxX, z);
            if (IsCoordInsideRenderDistance(right, center))
                EnqueuePendingChunk(right);
        }

        for (int x = maxX; x >= minX; x--)
        {
            Vector2Int bottom = new Vector2Int(x, maxZ);
            if (IsCoordInsideRenderDistance(bottom, center))
                EnqueuePendingChunk(bottom);
        }

        for (int z = maxZ - 1; z >= minZ + 1; z--)
        {
            Vector2Int left = new Vector2Int(minX, z);
            if (IsCoordInsideRenderDistance(left, center))
                EnqueuePendingChunk(left);
        }
    }

    private void RebuildPendingChunkQueue(Vector2Int center)
    {
        ClearPendingChunkQueue();

        for (int ring = 0; ring <= renderDistance; ring++)
            EnqueuePendingChunkRing(center, ring);
    }

    private void EnsurePendingChunkCoverage(Vector2Int center)
    {
        for (int ring = 0; ring <= renderDistance; ring++)
            EnqueuePendingChunkRing(center, ring);
    }

    private void AppendPendingChunkFrontier(Vector2Int previousCenter, Vector2Int currentCenter)
    {
        int deltaX = currentCenter.x - previousCenter.x;
        int deltaZ = currentCenter.y - previousCenter.y;
        if (deltaX == 0 && deltaZ == 0)
            return;

        EnsurePendingChunkCoverage(currentCenter);
    }

    private void EnqueuePendingChunkFromSet(Vector2Int coord, Vector2Int center)
    {
        if (!IsCoordInsideRenderDistance(coord, center) || !pendingChunkSet.Contains(coord))
            return;

        pendingChunks.Enqueue(coord);
    }

    private void EnqueuePendingChunkRingFromSet(Vector2Int center, int ring)
    {
        if (ring <= 0)
        {
            EnqueuePendingChunkFromSet(center, center);
            return;
        }

        int minX = center.x - ring;
        int maxX = center.x + ring;
        int minZ = center.y - ring;
        int maxZ = center.y + ring;

        for (int x = minX; x <= maxX; x++)
            EnqueuePendingChunkFromSet(new Vector2Int(x, minZ), center);

        for (int z = minZ + 1; z <= maxZ - 1; z++)
            EnqueuePendingChunkFromSet(new Vector2Int(maxX, z), center);

        for (int x = maxX; x >= minX; x--)
            EnqueuePendingChunkFromSet(new Vector2Int(x, maxZ), center);

        for (int z = maxZ - 1; z >= minZ + 1; z--)
            EnqueuePendingChunkFromSet(new Vector2Int(minX, z), center);
    }

    private void ReprioritizePendingChunkQueue(Vector2Int center)
    {
        pendingChunkQueuePruneBuffer.Clear();
        foreach (Vector2Int coord in pendingChunkSet)
        {
            if (!IsCoordInsideRenderDistance(coord, center) ||
                activeChunks.ContainsKey(coord) ||
                HasScheduledChunkPipelineWork(coord))
            {
                pendingChunkQueuePruneBuffer.Add(coord);
            }
        }

        for (int i = 0; i < pendingChunkQueuePruneBuffer.Count; i++)
            pendingChunkSet.Remove(pendingChunkQueuePruneBuffer[i]);

        pendingChunks.Clear();
        if (pendingChunkSet.Count == 0)
            return;

        for (int ring = 0; ring <= renderDistance; ring++)
            EnqueuePendingChunkRingFromSet(center, ring);
    }

    private int GetDetailedGenerationBorderSize()
    {
        return Mathf.Max(GetMeshNeighborPadding(), detailedGenerationPadding);
    }

    private int GetLightSmoothingBorderSize()
    {
        if (!enableVoxelLighting || !enableHorizontalSkylight)
            return GetMeshNeighborPadding();

        return Mathf.Max(GetMeshNeighborPadding(), sunlightSmoothingPadding);
    }

    private int GetChunkBorderSize()
    {
        return Mathf.Max(GetDetailedGenerationBorderSize(), GetLightSmoothingBorderSize());
    }

    private int GetResolvedVisualSubchunksPerRenderer()
    {
        return Mathf.Clamp(visualSubchunksPerRenderer, 1, Chunk.SubchunksPerColumn);
    }

    private void ApplyResolvedVisualSubchunkRendererLayout()
    {
        int resolved = GetResolvedVisualSubchunksPerRenderer();
        if (resolved == lastResolvedVisualSubchunksPerRenderer)
            return;

        lastResolvedVisualSubchunksPerRenderer = resolved;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null)
                continue;

            chunk.InitializeSubchunks(ActiveWorldMaterials, resolved);
            chunk.UpdateWorldBounds();
            RequestFullChunkRebuild(kv.Key, false);
        }
    }

    private static bool CanChunkProvideVoxelSnapshot(Chunk chunk)
    {
        return chunk != null &&
               chunk.voxelData.IsCreated &&
               chunk.hasVoxelSnapshot;
    }

    private static Vector3Int GetColliderBuildKey(Vector2Int coord, int subchunkIndex)
    {
        return new Vector3Int(coord.x, subchunkIndex, coord.y);
    }

    #endregion

}
