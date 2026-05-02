using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Collider Queue

    private void EnqueueColliderBuild(Vector2Int coord, int expectedGen, int subchunkIndex)
    {
        Vector3Int key = GetColliderBuildKey(coord, subchunkIndex);
        PendingColliderBuild request = new PendingColliderBuild
        {
            coord = coord,
            expectedGen = expectedGen,
            subchunkIndex = subchunkIndex
        };

        if (queuedColliderBuildsByKey.ContainsKey(key))
        {
            queuedColliderBuildsByKey[key] = request;
            return;
        }

        queuedColliderBuildsByKey.Add(key, request);
        queuedColliderBuilds.Enqueue(key);
    }

    private void ProcessPendingColliderBuilds()
    {
        if (!enableBlockColliders || queuedColliderBuilds.Count == 0)
            return;

        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = colliderBuildTimeBudgetMS > 0f ? colliderBuildTimeBudgetMS / 1000f : 0f;
        int perFrameLimit = Mathf.Max(1, maxColliderBuildsPerFrame);
        int processed = 0;
        int attempts = queuedColliderBuilds.Count;
        BlockTextureMapping[] blockMappings = blockData != null ? blockData.mappings : null;
        BlockModelCuboid[] blockModelCuboids = blockData != null ? blockData.runtimeMultiCuboidBoxes : null;
        Vector2Int colliderCenter = GetCurrentPlayerChunkCoord();

        while (processed < perFrameLimit && attempts-- > 0 && queuedColliderBuilds.Count > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            Vector3Int key = queuedColliderBuilds.Dequeue();
            if (!queuedColliderBuildsByKey.TryGetValue(key, out PendingColliderBuild request))
                continue;

            queuedColliderBuildsByKey.Remove(key);

            if (!activeChunks.TryGetValue(request.coord, out Chunk chunk) ||
                chunk == null ||
                chunk.generation != request.expectedGen ||
                !chunk.hasVoxelData ||
                !chunk.voxelData.IsCreated ||
                request.subchunkIndex < 0 ||
                request.subchunkIndex >= chunk.SubchunkCount)
            {
                continue;
            }

            if (!IsCoordInsideColliderDistance(request.coord, colliderCenter))
            {
                chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, false);
                continue;
            }

            if (!chunk.CanSubchunkHaveColliders(request.subchunkIndex))
            {
                chunk.ClearSubchunkColliderData(request.subchunkIndex);
                continue;
            }

            if (chunk.HasSubchunkColliderData(request.subchunkIndex))
            {
                chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, true);
                processed++;
                continue;
            }

            int startY = request.subchunkIndex * Chunk.SubchunkHeight;
            int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);
            chunk.RebuildSubchunkColliders(request.subchunkIndex, chunk.voxelData, blockMappings, blockModelCuboids, startY, endY);
            chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, true);
            processed++;
        }
    }

    private void EnsurePlayerChunkColliderSafety()
    {
        if (!enableBlockColliders || player == null)
            return;

        Vector2Int playerChunkCoord = GetCurrentPlayerChunkCoord();
        if (!activeChunks.TryGetValue(playerChunkCoord, out Chunk chunk) ||
            chunk == null ||
            !chunk.hasVoxelData ||
            !chunk.voxelData.IsCreated)
        {
            return;
        }

        BlockTextureMapping[] blockMappings = blockData != null ? blockData.mappings : null;
        BlockModelCuboid[] blockModelCuboids = blockData != null ? blockData.runtimeMultiCuboidBoxes : null;
        int playerSubchunk = Mathf.Clamp(Mathf.FloorToInt(player.position.y / Chunk.SubchunkHeight), 0, Chunk.SubchunksPerColumn - 1);

        EnsureImmediateSubchunkCollider(playerChunkCoord, chunk, playerSubchunk - 1, blockMappings, blockModelCuboids);
        EnsureImmediateSubchunkCollider(playerChunkCoord, chunk, playerSubchunk, blockMappings, blockModelCuboids);
        EnsureImmediateSubchunkCollider(playerChunkCoord, chunk, playerSubchunk + 1, blockMappings, blockModelCuboids);
    }

    private void EnsureImmediateSubchunkCollider(
        Vector2Int coord,
        Chunk chunk,
        int subchunkIndex,
        BlockTextureMapping[] blockMappings,
        BlockModelCuboid[] blockModelCuboids)
    {
        if (chunk == null ||
            subchunkIndex < 0 ||
            subchunkIndex >= chunk.SubchunkCount ||
            !IsCoordInsideColliderDistance(coord, GetCurrentPlayerChunkCoord()))
        {
            return;
        }

        if (chunk.HasSubchunkColliderData(subchunkIndex))
        {
            chunk.SetSubchunkColliderSystemEnabled(subchunkIndex, true);
            return;
        }

        int startY = subchunkIndex * Chunk.SubchunkHeight;
        int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);
        if (chunk.ForceRebuildSubchunkColliders(subchunkIndex, chunk.voxelData, blockMappings, blockModelCuboids, startY, endY))
            chunk.SetSubchunkColliderSystemEnabled(subchunkIndex, true);
    }

    #endregion

}
