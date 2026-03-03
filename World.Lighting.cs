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

    #region Global Light Propagation


    public void PropagateLightGlobal(Vector3Int startWorldPos, byte lightEmission)
    {
        Queue<Vector3Int> lightQueue = new Queue<Vector3Int>();
        lightQueue.Enqueue(startWorldPos);

        globalLightMap[startWorldPos] = lightEmission;
        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        while (lightQueue.Count > 0)
        {
            Vector3Int node = lightQueue.Dequeue();
            globalLightMap.TryGetValue(node, out byte currentLight);

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
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);

                if (opacity >= 15) continue;

                globalLightMap.TryGetValue(neighborPos, out byte neighborLight);
                byte cost = (opacity < 1) ? (byte)1 : opacity;
                byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                if (candidateLight > neighborLight)
                {
                    globalLightMap[neighborPos] = candidateLight;
                    lightQueue.Enqueue(neighborPos);
                }
            }
        }

        foreach (Vector2Int coord in dirtiedChunks)
        {
            RequestChunkRebuild(coord);
        }
    }

    public void RemoveLightGlobal(Vector3Int startWorldPos)
    {
        if (!globalLightMap.TryGetValue(startWorldPos, out byte oldLight)) return;

        Queue<(Vector3Int pos, byte lightLevel)> darkQueue = new Queue<(Vector3Int, byte)>();
        Queue<Vector3Int> refillQueue = new Queue<Vector3Int>();
        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        darkQueue.Enqueue((startWorldPos, oldLight));
        globalLightMap[startWorldPos] = 0;

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

                if (globalLightMap.TryGetValue(neighborPos, out byte neighborLight) && neighborLight > 0)
                {
                    if (neighborLight < node.lightLevel)
                    {
                        globalLightMap[neighborPos] = 0;
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
            if (globalLightMap.TryGetValue(node, out byte currentLight))
            {
                foreach (Vector3Int dir in sixDirections)
                {
                    Vector3Int neighborPos = node + dir;
                    if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                    BlockType neighborBlock = GetBlockAt(neighborPos);
                    byte opacity = GetBlockOpacity(neighborBlock);
                    if (opacity >= 15) continue;

                    globalLightMap.TryGetValue(neighborPos, out byte neighborLight);
                    byte cost = (opacity < 1) ? (byte)1 : opacity;
                    byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                    if (candidateLight > neighborLight)
                    {
                        globalLightMap[neighborPos] = candidateLight;
                        refillQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        foreach (Vector2Int coord in dirtiedChunks) RequestChunkRebuild(coord);
    }


    #endregion
}