using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private readonly Vector3Int[] sixDirections = new Vector3Int[]
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    #region Light Helpers

    public byte GetBlockOpacity(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return blockData.mappings[t].lightOpacity;
        }
        return 15;
    }

    public byte GetBlockEmission(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return blockData.mappings[t].lightEmission;
        }
        return 0;
    }

    #endregion

    #region Column Light Helpers (MUITO mais rápido)

    private byte GetColumnLight(int worldX, int worldZ, int y)
    {
        if (y < 0 || y >= Chunk.SizeY) return 0;

        var key = new Vector2Int(worldX, worldZ);
        return globalLightColumns.TryGetValue(key, out byte[] column) ? column[y] : (byte)0;
    }

    private void SetColumnLight(int worldX, int worldZ, int y, byte value)
    {
        if (y < 0 || y >= Chunk.SizeY) return;

        var key = new Vector2Int(worldX, worldZ);

        if (!globalLightColumns.TryGetValue(key, out byte[] column))
        {
            if (value == 0) return;
            column = new byte[Chunk.SizeY];
            globalLightColumns[key] = column;
        }

        column[y] = value;
    }

    #endregion

    #region Global Light Propagation


    public void PropagateLightGlobal(Vector3Int startWorldPos, byte lightEmission)
    {
        Queue<Vector3Int> lightQueue = new Queue<Vector3Int>();
        lightQueue.Enqueue(startWorldPos);

        // Define a luz inicial usando o novo sistema de colunas
        SetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y, lightEmission);

        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        while (lightQueue.Count > 0)
        {
            Vector3Int node = lightQueue.Dequeue();

            // Pega a luz atual da coluna
            byte currentLight = GetColumnLight(node.x, node.z, node.y);

            // Marca o chunk como sujo
            Vector2Int chunkCoord = new Vector2Int(
                Mathf.FloorToInt((float)node.x / Chunk.SizeX),
                Mathf.FloorToInt((float)node.z / Chunk.SizeZ)
            );
            dirtiedChunks.Add(chunkCoord);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;

                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                    continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ)
                );

                if (!IsChunkLoaded(neighborChunk))
                    continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);

                if (opacity >= 15)
                    continue;

                // Pega a luz do vizinho usando coluna
                byte neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);

                byte cost = (byte)(1 + opacity);
                byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                if (candidateLight > neighborLight)
                {
                    // Define a nova luz usando coluna
                    SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, candidateLight);
                    lightQueue.Enqueue(neighborPos);
                }
            }
        }

        // Reconstrói os chunks que receberam luz
        foreach (Vector2Int coord in dirtiedChunks)
        {
            RequestChunkRebuild(coord);
        }
    }
    public void RemoveLightGlobal(Vector3Int startWorldPos)
    {
        byte oldLight = GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y);
        if (oldLight == 0) return;

        Queue<(Vector3Int pos, byte lightLevel)> darkQueue = new Queue<(Vector3Int, byte)>();
        Queue<Vector3Int> refillQueue = new Queue<Vector3Int>();
        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        darkQueue.Enqueue((startWorldPos, oldLight));
        SetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y, 0);

        while (darkQueue.Count > 0)
        {
            var node = darkQueue.Dequeue();
            Vector2Int chunkCoord = new Vector2Int(Mathf.FloorToInt((float)node.pos.x / Chunk.SizeX), Mathf.FloorToInt((float)node.pos.z / Chunk.SizeZ));
            dirtiedChunks.Add(chunkCoord);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node.pos + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                byte neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                if (neighborLight > 0)
                {
                    if (neighborLight < node.lightLevel)
                    {
                        SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, 0);
                        darkQueue.Enqueue((neighborPos, neighborLight));
                    }
                    else if (neighborLight >= node.lightLevel)
                    {
                        refillQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        while (refillQueue.Count > 0)
        {
            Vector3Int node = refillQueue.Dequeue();
            byte currentLight = GetColumnLight(node.x, node.z, node.y);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);
                if (opacity >= 15) continue;

                byte neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                byte cost = (byte)(1 + opacity);
                byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                if (candidateLight > neighborLight)
                {
                    SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, candidateLight);
                    refillQueue.Enqueue(neighborPos);
                }
            }
        }

        foreach (Vector2Int coord in dirtiedChunks) RequestChunkRebuild(coord);

        CleanupEmptyLightColumns();
    }

    public void RefillLightGlobal(Vector3Int startWorldPos)
    {
        if (startWorldPos.y < 0 || startWorldPos.y >= Chunk.SizeY) return;

        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();
        Queue<Vector3Int> refillQueue = new Queue<Vector3Int>();
        HashSet<Vector3Int> enqueued = new HashSet<Vector3Int>();

        // Try to relight the changed voxel from the brightest lit neighbor.
        BlockType startBlock = GetBlockAt(startWorldPos);
        byte startOpacity = GetBlockOpacity(startBlock);
        if (startOpacity < 15)
        {
            byte bestAtStart = GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int n = startWorldPos + dir;
                if (n.y < 0 || n.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt((float)n.x / Chunk.SizeX),
                    Mathf.FloorToInt((float)n.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                byte nLight = GetColumnLight(n.x, n.z, n.y);
                if (nLight == 0) continue;

                byte candidate = (byte)Mathf.Max(0, nLight - (1 + startOpacity));
                if (candidate > bestAtStart) bestAtStart = candidate;
            }

            if (bestAtStart > GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y))
            {
                SetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y, bestAtStart);
            }
        }

        // Seed queue with changed position and lit neighbors.
        if (GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y) > 0)
        {
            refillQueue.Enqueue(startWorldPos);
            enqueued.Add(startWorldPos);
        }

        foreach (Vector3Int dir in sixDirections)
        {
            Vector3Int n = startWorldPos + dir;
            if (n.y < 0 || n.y >= Chunk.SizeY) continue;

            Vector2Int neighborChunk = new Vector2Int(
                Mathf.FloorToInt((float)n.x / Chunk.SizeX),
                Mathf.FloorToInt((float)n.z / Chunk.SizeZ)
            );
            if (!IsChunkLoaded(neighborChunk)) continue;

            if (GetColumnLight(n.x, n.z, n.y) > 0 && enqueued.Add(n))
            {
                refillQueue.Enqueue(n);
            }
        }

        while (refillQueue.Count > 0)
        {
            Vector3Int node = refillQueue.Dequeue();
            byte currentLight = GetColumnLight(node.x, node.z, node.y);
            if (currentLight == 0) continue;

            Vector2Int chunkCoord = new Vector2Int(
                Mathf.FloorToInt((float)node.x / Chunk.SizeX),
                Mathf.FloorToInt((float)node.z / Chunk.SizeZ)
            );
            dirtiedChunks.Add(chunkCoord);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt((float)neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt((float)neighborPos.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);
                if (opacity >= 15) continue;

                byte neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                byte candidateLight = (byte)Mathf.Max(0, currentLight - (1 + opacity));

                if (candidateLight > neighborLight)
                {
                    SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, candidateLight);
                    if (enqueued.Add(neighborPos))
                    {
                        refillQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        foreach (Vector2Int coord in dirtiedChunks)
        {
            RequestChunkRebuild(coord);
        }
    }

    private void CleanupEmptyLightColumns()
    {
        var toRemove = new List<Vector2Int>();
        foreach (var kv in globalLightColumns)
        {
            bool allZero = true;
            for (int i = 0; i < kv.Value.Length; i++)
            {
                if (kv.Value[i] != 0) { allZero = false; break; }
            }
            if (allZero) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) globalLightColumns.Remove(k);
    }
    #endregion
}
