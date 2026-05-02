using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Chunk Pool

    private int GetResolvedColliderPrewarmChunkCount()
    {
        if (!prewarmPooledChunkColliders || prewarmColliderBoxesPerSubchunk <= 0)
            return 0;

        if (prewarmColliderChunkCount > 0)
            return prewarmColliderChunkCount;

        int diameter = GetEffectiveColliderDistance() * 2 + 1;
        return Mathf.Max(1, diameter * diameter);
    }

    private static int CountCoordsInsideCircularDistance(int radius)
    {
        int clampedRadius = Mathf.Max(0, radius);
        int count = 0;
        int radiusSq = clampedRadius * clampedRadius;

        for (int z = -clampedRadius; z <= clampedRadius; z++)
        {
            for (int x = -clampedRadius; x <= clampedRadius; x++)
            {
                if (x * x + z * z <= radiusSq)
                    count++;
            }
        }

        return count;
    }

    private int GetRecommendedChunkPoolSize()
    {
        int baseRadius = Mathf.Max(0, renderDistance);
        int expandedRadius = baseRadius + Mathf.Max(0, chunkPoolExtraRadius);
        int activeCoverage = CountCoordsInsideCircularDistance(expandedRadius);
        int retireCoverage = Mathf.Max(8,
            CountCoordsInsideCircularDistance(baseRadius + 1) - CountCoordsInsideCircularDistance(baseRadius));
        int pipelineBuffer = Mathf.Max(
            Mathf.Max(8, maxChunksPerFrame * 4),
            Mathf.Max(maxPendingDataJobs * 4, maxMeshBuildRequestBacklog + maxPendingMeshJobBacklog));

        return activeCoverage + retireCoverage + pipelineBuffer + Mathf.Max(0, chunkPoolSafetyBuffer);
    }

    private int GetResolvedChunkPoolTargetSize()
    {
        int manualSize = Mathf.Max(1, poolSize);
        if (!autoSizeChunkPool)
            return manualSize;

        return Mathf.Max(manualSize, GetRecommendedChunkPoolSize());
    }

    private int GetLiveChunkEntryCount()
    {
        return activeChunks.Count + chunkPool.Count + retiredChunksAwaitingRecycle.Count;
    }

    private bool TryCreateChunkPoolEntryForRuntime()
    {
        Chunk chunk = CreateChunkPoolEntry();
        if (chunk == null)
            return false;

        chunkPool.Enqueue(chunk);
        chunkPoolCreatesThisFrame++;
        return true;
    }

    private void MaintainChunkPool()
    {
        int targetSize = GetResolvedChunkPoolTargetSize();
        if (GetLiveChunkEntryCount() >= targetSize)
            return;

        float startTime = Time.realtimeSinceStartup;
        float budgetSeconds = GetBudgetSeconds(chunkPoolWarmupBudgetMS);
        int created = 0;
        int perFrameLimit = Mathf.Max(1, maxChunkPoolEntriesCreatedPerFrame);

        while (created < perFrameLimit && GetLiveChunkEntryCount() < targetSize)
        {
            if (budgetSeconds > 0f && Time.realtimeSinceStartup - startTime >= budgetSeconds)
                break;

            if (!TryCreateChunkPoolEntryForRuntime())
                break;

            created++;
        }
    }

    private bool ShouldPrewarmCollidersForNextChunkPoolEntry()
    {
        return colliderPrewarmedChunkCount < GetResolvedColliderPrewarmChunkCount();
    }

    private void TryPrewarmChunkColliderPool(Chunk chunk)
    {
        if (chunk == null || !ShouldPrewarmCollidersForNextChunkPoolEntry())
            return;

        chunk.PrewarmSubchunkColliders(prewarmColliderBoxesPerSubchunk);
        colliderPrewarmedChunkCount++;
    }

    private Chunk CreateChunkPoolEntry()
    {
        GameObject obj = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform);
        obj.SetActive(false);

        Chunk chunk = obj.GetComponent<Chunk>();
        if (chunk != null && prewarmPooledChunkVisuals)
        {
            chunk.InitializeSubchunks(ActiveWorldMaterials, GetResolvedVisualSubchunksPerRenderer());
            chunk.ResetChunk();
        }

        TryPrewarmChunkColliderPool(chunk);

        return chunk;
    }

    #endregion

}
