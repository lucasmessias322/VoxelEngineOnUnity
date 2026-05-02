using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Chunk Lifecycle

    private void ProcessRetiredChunksAwaitingRecycle(int maxToProcess = int.MaxValue)
    {
        int processed = 0;
        for (int i = retiredChunksAwaitingRecycle.Count - 1; i >= 0; i--)
        {
            if (processed >= maxToProcess)
                break;

            Chunk chunk = retiredChunksAwaitingRecycle[i];
            if (chunk == null)
            {
                retiredChunksAwaitingRecycle.RemoveAt(i);
                continue;
            }

            if (IsChunkReferencedByActiveChunks(chunk))
            {
                RestoreActiveChunkIfNeeded(chunk);
                retiredChunksAwaitingRecycle.RemoveAt(i);
                continue;
            }

            if (!TryRecycleChunkWithoutBlocking(chunk))
                continue;

            retiredChunksAwaitingRecycle.RemoveAt(i);
            processed++;
        }
    }

    private void RetireChunkWithoutBlocking(Chunk chunk)
    {
        if (chunk == null)
            return;

        if (IsChunkReferencedByActiveChunks(chunk))
        {
            RestoreActiveChunkIfNeeded(chunk);
            return;
        }

        if (TryRecycleChunkWithoutBlocking(chunk))
            return;

        if (chunk.pendingRecycle)
            return;

        chunk.pendingRecycle = true;
        chunk.state = Chunk.ChunkState.Inactive;
        if (chunk.gameObject.activeSelf)
            chunk.gameObject.SetActive(false);
        retiredChunksAwaitingRecycle.Add(chunk);
    }

    private bool TryRecycleChunkWithoutBlocking(Chunk chunk)
    {
        if (chunk == null)
            return false;

        if (IsChunkReferencedByActiveChunks(chunk))
            return false;

        if (HasPendingJobReferencesForChunk(chunk))
            return false;

        if (chunk.jobScheduled && !IsChunkJobCompletedWithoutBlocking(chunk))
            return false;

        chunk.ResetChunk();
        if (!chunkPool.Contains(chunk))
            chunkPool.Enqueue(chunk);
        return true;
    }

    private bool IsChunkReferencedByActiveChunks(Chunk chunk)
    {
        if (chunk == null)
            return false;

        foreach (var kv in activeChunks)
        {
            if (ReferenceEquals(kv.Value, chunk))
                return true;
        }

        return false;
    }

    private bool CanUsePooledChunk(Chunk chunk)
    {
        if (chunk == null || chunk.pendingRecycle)
            return false;

        if (IsChunkReferencedByActiveChunks(chunk))
        {
            RestoreActiveChunkIfNeeded(chunk);
            return false;
        }

        return true;
    }

    private void RestoreActiveChunkIfNeeded(Chunk chunk)
    {
        if (chunk == null)
            return;

        chunk.pendingRecycle = false;
        if (!chunk.gameObject.activeSelf)
            chunk.gameObject.SetActive(true);
    }

    private bool IsChunkProtectingPlayer(Vector2Int coord, Chunk chunk, Vector2Int currentChunkCoord)
    {
        if (coord == currentChunkCoord)
            return true;

        if (chunk == null || player == null)
            return false;

        if (chunk.coord == currentChunkCoord)
            return true;

        Vector3 playerPosition = player.position;
        Vector3 chunkPosition = chunk.transform.position;
        return playerPosition.x >= chunkPosition.x &&
               playerPosition.x < chunkPosition.x + Chunk.SizeX &&
               playerPosition.z >= chunkPosition.z &&
               playerPosition.z < chunkPosition.z + Chunk.SizeZ;
    }

    private bool HasPendingJobReferencesForChunk(Chunk chunk)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (ReferenceEquals(pendingMeshes[i].parentChunk, chunk))
                return true;
        }

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (ReferenceEquals(pendingDataJobs[i].chunk, chunk))
                return true;
        }

        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            if (ReferenceEquals(pendingMeshBuildRequests[i].chunk, chunk))
                return true;
        }

        return false;
    }

    private static bool IsChunkJobCompletedWithoutBlocking(Chunk chunk)
    {
        if (chunk == null || !chunk.jobScheduled)
            return true;

        try
        {
            return chunk.currentJob.IsCompleted;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void UpdateChunks()
    {
        if (player == null) return;

        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();

        if (currentChunkCoord != _lastChunkCoord)
        {
            Vector2Int previousChunkCoord = _lastChunkCoord;
            bool hadPreviousChunkCoord = previousChunkCoord != InvalidChunkCoord;
            _lastChunkCoord = currentChunkCoord;
            lastPlayerChunkCoordChangeTime = Time.time;
            bool activeSectionSetChanged = false;

            // A. Remover chunks distantes
            _tempToRemove.Clear();
            foreach (var kv in activeChunks)
            {
                if (!IsCoordInsideRenderDistance(kv.Key, currentChunkCoord) &&
                    !IsChunkProtectingPlayer(kv.Key, kv.Value, currentChunkCoord))
                {
                    _tempToRemove.Add(kv.Key);
                }
            }

            for (int i = 0; i < _tempToRemove.Count; i++)
            {
                Vector2Int coord = _tempToRemove[i];
                if (activeChunks.TryGetValue(coord, out Chunk chunk))
                {
                    if (IsChunkProtectingPlayer(coord, chunk, currentChunkCoord))
                    {
                        RestoreActiveChunkIfNeeded(chunk);
                        continue;
                    }

                    InvalidateChunkBiomeTintCache(coord);
                    activeChunks.Remove(coord);
                    RetireChunkWithoutBlocking(chunk);
                    activeSectionSetChanged = true;

                    RemoveHighBuildMesh(coord);
                }
            }

            if (activeSectionSetChanged)
                InvalidateSectionOcclusionGraph();

            // B. Limpar pendentes desnecessÃ¡rios
            if (!hadPreviousChunkCoord)
                RebuildPendingChunkQueue(currentChunkCoord);
            else
                AppendPendingChunkFrontier(previousChunkCoord, currentChunkCoord);

            // D. Reordenar fila por distÃ¢ncia
            if (!hadPreviousChunkCoord)
                RebuildChunkDetailPromotionQueue(currentChunkCoord);
            else
                AppendChunkDetailPromotionFrontier(previousChunkCoord, currentChunkCoord);
        }

        // O scheduler central consome pendingChunks com orcamento proprio em ProcessChunkQueue.
    }

    #endregion

}
