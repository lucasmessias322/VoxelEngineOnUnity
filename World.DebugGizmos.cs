using UnityEngine;

public partial class World
{
    private Vector2Int GetChunkCoordFromWorldPosition(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt(worldPos.z / Chunk.SizeZ));
    }

    private Bounds GetChunkBoundsFromCoord(Vector2Int coord)
    {
        Vector3 center = new Vector3(
            coord.x * Chunk.SizeX + Chunk.SizeX * 0.5f,
            Chunk.SizeY * 0.5f,
            coord.y * Chunk.SizeZ + Chunk.SizeZ * 0.5f);

        return new Bounds(center, new Vector3(Chunk.SizeX, Chunk.SizeY, Chunk.SizeZ));
    }

    private void DrawBoundsGizmo(Bounds bounds, Color color, bool filled)
    {
        if (filled)
        {
            Color fill = new Color(color.r, color.g, color.b, Mathf.Clamp01(debugGizmoFillAlpha));
            Gizmos.color = fill;
            Gizmos.DrawCube(bounds.center, bounds.size);
        }

        Gizmos.color = color;
        Gizmos.DrawWireCube(bounds.center, bounds.size);
    }

    private void OnDrawGizmos()
    {
        if (!debugDrawGizmos || (debugGizmosOnlyWhenPlaying && !Application.isPlaying) || player == null)
            return;

        Vector2Int playerCoord = GetChunkCoordFromWorldPosition(player.position);

        if (debugDrawRenderDistanceGrid)
        {
            for (int x = -renderDistance; x <= renderDistance; x++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector2Int coord = new Vector2Int(playerCoord.x + x, playerCoord.y + z);
                    if (!IsCoordInsideRenderDistance(coord, playerCoord))
                        continue;

                    DrawBoundsGizmo(GetChunkBoundsFromCoord(coord), debugRenderGridColor, false);
                }
            }
        }

        if (debugDrawPlayerChunkBounds)
            DrawBoundsGizmo(GetChunkBoundsFromCoord(playerCoord), debugPlayerChunkColor, true);

        if (debugDrawActiveChunkBounds)
        {
            foreach (var kv in activeChunks)
                DrawBoundsGizmo(GetChunkBoundsFromCoord(kv.Key), debugActiveChunkColor, false);
        }

        if (debugDrawPendingChunkQueue)
        {
            for (int i = 0; i < pendingChunks.Count; i++)
            {
                Bounds bounds = GetChunkBoundsFromCoord(pendingChunks[i].coord);
                Gizmos.color = debugPendingChunkColor;
                Gizmos.DrawWireCube(bounds.center, bounds.size);
                Gizmos.DrawSphere(bounds.center, 0.6f);
            }
        }

        if (!debugDrawSubchunkBounds)
            return;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.SubchunkCount == 0)
                continue;

            for (int i = 0; i < chunk.SubchunkCount; i++)
            {
                if (debugSubchunksOnlyWithGeometry && !chunk.HasSubchunkGeometry(i))
                    continue;

                float minY = i * Chunk.SubchunkHeight;
                Vector3 center = chunk.transform.position + new Vector3(
                    Chunk.SizeX * 0.5f,
                    minY + Chunk.SubchunkHeight * 0.5f,
                    Chunk.SizeZ * 0.5f);

                Gizmos.color = debugSubchunkColor;
                Gizmos.DrawWireCube(center, new Vector3(Chunk.SizeX, Chunk.SubchunkHeight, Chunk.SizeZ));
            }
        }
    }
}
