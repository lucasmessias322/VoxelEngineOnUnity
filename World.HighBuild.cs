using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    private const int HighBuildSectionHeight = 128;

    private sealed class HighBuildMeshData
    {
        public GameObject root;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;
        public Mesh mesh;
        public readonly List<BoxCollider> boxColliders = new List<BoxCollider>(128);
        public int activeBoxColliderCount;
        public bool hasColliderData;
    }

    // Key: (chunkX, highSectionY, chunkZ)
    private readonly Dictionary<Vector3Int, HighBuildMeshData> highBuildMeshes = new Dictionary<Vector3Int, HighBuildMeshData>();
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> highOverridePositionsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>();
    private readonly Queue<Vector2Int> queuedHighBuildRebuilds = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedHighBuildRebuildsSet = new HashSet<Vector2Int>();

    private void IndexHighOverride(Vector3Int worldPos, Vector2Int coord, BlockType type)
    {
        if (worldPos.y < Chunk.SizeY) return;

        if (type == BlockType.Air)
        {
            if (!highOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set)) return;
            set.Remove(worldPos);
            if (set.Count == 0) highOverridePositionsByChunk.Remove(coord);
            return;
        }

        if (!highOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
        {
            positions = new HashSet<Vector3Int>();
            highOverridePositionsByChunk[coord] = positions;
        }

        positions.Add(worldPos);
    }

    private void EnsureHighOverrideIndexForCoord(Vector2Int coord)
    {
        if (highOverridePositionsByChunk.ContainsKey(coord)) return;

        int minX = coord.x * Chunk.SizeX;
        int minZ = coord.y * Chunk.SizeZ;
        int maxX = minX + Chunk.SizeX - 1;
        int maxZ = minZ + Chunk.SizeZ - 1;

        HashSet<Vector3Int> positions = null;
        foreach (var kv in blockOverrides)
        {
            Vector3Int p = kv.Key;
            if (p.y < Chunk.SizeY) continue;
            if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
            if (kv.Value == BlockType.Air) continue;

            if (positions == null) positions = new HashSet<Vector3Int>();
            positions.Add(p);
        }

        if (positions != null && positions.Count > 0)
            highOverridePositionsByChunk[coord] = positions;
    }

    private void RequestHighBuildMeshRebuild(Vector2Int coord)
    {
        if (!queuedHighBuildRebuildsSet.Add(coord)) return;
        queuedHighBuildRebuilds.Enqueue(coord);
    }

    private void ProcessQueuedHighBuildMeshRebuilds()
    {
        if (queuedHighBuildRebuilds.Count == 0) return;

        // High-build meshes are lightweight compared to full chunk jobs; allow a higher budget.
        int perFrameLimit = Mathf.Max(2, maxChunkRebuildsPerFrame * 4);
        int processed = 0;
        int attempts = queuedHighBuildRebuilds.Count;

        while (processed < perFrameLimit && attempts-- > 0)
        {
            Vector2Int coord = queuedHighBuildRebuilds.Dequeue();
            queuedHighBuildRebuildsSet.Remove(coord);

            RebuildHighBuildMesh(coord);
            processed++;
        }
    }

    private void RebuildHighBuildMesh(Vector2Int coord)
    {
        if (!activeChunks.ContainsKey(coord))
        {
            RemoveHighBuildMesh(coord);
            return;
        }

        EnsureHighOverrideIndexForCoord(coord);
        if (!highOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions) || positions.Count == 0)
        {
            RemoveHighBuildMesh(coord);
            return;
        }

        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
        {
            RemoveHighBuildMesh(coord);
            return;
        }

        Dictionary<int, List<Vector3Int>> bySection = new Dictionary<int, List<Vector3Int>>();
        foreach (Vector3Int p in positions)
        {
            if (!blockOverrides.TryGetValue(p, out BlockType t) || t == BlockType.Air) continue;
            int section = GetHighSectionIndex(p.y);
            if (section < 0) continue;
            if (!bySection.TryGetValue(section, out List<Vector3Int> list))
            {
                list = new List<Vector3Int>();
                bySection[section] = list;
            }
            list.Add(p);
        }

        if (bySection.Count == 0)
        {
            RemoveHighBuildMesh(coord);
            return;
        }

        // Disable sections no longer used for this chunk coord.
        List<Vector3Int> stale = null;
        foreach (var kv in highBuildMeshes)
        {
            Vector3Int key = kv.Key;
            if (key.x != coord.x || key.z != coord.y) continue;
            if (bySection.ContainsKey(key.y)) continue;
            if (stale == null) stale = new List<Vector3Int>();
            stale.Add(key);
        }
        if (stale != null)
        {
            for (int i = 0; i < stale.Count; i++)
                DisableHighBuildMesh(stale[i]);
        }

        foreach (var sec in bySection)
            RebuildHighBuildSectionMesh(coord, sec.Key, sec.Value);
    }

    private void RebuildHighBuildSectionMesh(Vector2Int coord, int section, List<Vector3Int> positions)
    {
        HighBuildMeshData data = GetOrCreateHighBuildMesh(coord, section);

        List<Vector3> vertices = new List<Vector3>(positions.Count * 24);
        List<Vector3> normals = new List<Vector3>(positions.Count * 24);
        List<Vector2> uv0 = new List<Vector2>(positions.Count * 24);
        List<Vector2> uv1 = new List<Vector2>(positions.Count * 24);
        List<Vector4> uv2 = new List<Vector4>(positions.Count * 24);
        List<int> opaqueTris = new List<int>(positions.Count * 18);
        List<int> transparentTris = new List<int>(positions.Count * 18);
        List<int> waterTris = new List<int>(positions.Count * 18);

        float invAtlasTilesX = 1f / Mathf.Max(1, atlasTilesX);
        float invAtlasTilesY = 1f / Mathf.Max(1, atlasTilesY);

        foreach (Vector3Int pos in positions)
        {
            if (!blockOverrides.TryGetValue(pos, out BlockType blockType)) continue;
            if (blockType == BlockType.Air) continue;

            BlockTextureMapping mapping = GetMappingSafe(blockType);
            if (mapping.isEmpty) continue;

            for (int f = 0; f < 6; f++)
            {
                FaceDef face = FaceDefs[f];
                Vector3Int neighborPos = pos + face.normal;
                BlockType neighborType = GetBlockForHighMesh(neighborPos);
                if (!IsFaceVisibleForHighBuild(blockType, neighborType)) continue;

                int baseIndex = vertices.Count;
                Vector3 p = new Vector3(pos.x, pos.y, pos.z);

                vertices.Add(p + face.v0);
                vertices.Add(p + face.v1);
                vertices.Add(p + face.v2);
                vertices.Add(p + face.v3);

                normals.Add(face.normal3);
                normals.Add(face.normal3);
                normals.Add(face.normal3);
                normals.Add(face.normal3);

                uv0.Add(new Vector2(0f, 0f));
                uv0.Add(new Vector2(1f, 0f));
                uv0.Add(new Vector2(1f, 1f));
                uv0.Add(new Vector2(0f, 1f));

                Vector2Int tile = GetTileForFace(mapping, f);
                Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);

                uv1.Add(atlasUv);
                uv1.Add(atlasUv);
                uv1.Add(atlasUv);
                uv1.Add(atlasUv);

                float tint = GetTintForFace(mapping, f) ? 1f : 0f;
                Vector4 extra = new Vector4(1f, tint, 1f, 0f);
                uv2.Add(extra);
                uv2.Add(extra);
                uv2.Add(extra);
                uv2.Add(extra);

                List<int> targetTris = waterTris;
                if (blockType != BlockType.Water)
                    targetTris = mapping.isTransparent ? transparentTris : opaqueTris;

                targetTris.Add(baseIndex + 0);
                targetTris.Add(baseIndex + 1);
                targetTris.Add(baseIndex + 2);
                targetTris.Add(baseIndex + 0);
                targetTris.Add(baseIndex + 2);
                targetTris.Add(baseIndex + 3);
            }
        }

        if (vertices.Count == 0)
        {
            DisableHighBuildMesh(new Vector3Int(coord.x, section, coord.y));
            return;
        }

        data.mesh.Clear();
        data.mesh.SetVertices(vertices);
        data.mesh.SetNormals(normals);
        data.mesh.SetUVs(0, uv0);
        data.mesh.SetUVs(1, uv1);
        data.mesh.SetUVs(2, uv2);
        data.mesh.subMeshCount = 3;
        data.mesh.SetTriangles(opaqueTris, 0, true);
        data.mesh.SetTriangles(transparentTris, 1, true);
        data.mesh.SetTriangles(waterTris, 2, true);
        data.mesh.RecalculateBounds();
        data.meshFilter.sharedMesh = data.mesh;
        data.meshRenderer.enabled = true;
        data.root.SetActive(true);

        if (enableBlockColliders && (opaqueTris.Count > 0 || transparentTris.Count > 0))
            BuildHighBuildSectionColliders(data, coord, section, positions);
        else
            DisableHighBuildColliders(data, true);
    }

    private void SetHighBuildCollidersEnabled(bool enabled)
    {
        foreach (var kv in highBuildMeshes)
        {
            HighBuildMeshData data = kv.Value;
            if (data == null) continue;

            bool shouldEnable = enabled && data.hasColliderData;
            for (int i = 0; i < data.activeBoxColliderCount; i++)
            {
                BoxCollider box = data.boxColliders[i];
                if (box != null) box.enabled = shouldEnable;
            }
        }
    }

    private void RemoveHighBuildMesh(Vector2Int coord)
    {
        List<Vector3Int> keys = null;
        foreach (var kv in highBuildMeshes)
        {
            Vector3Int key = kv.Key;
            if (key.x != coord.x || key.z != coord.y) continue;
            if (keys == null) keys = new List<Vector3Int>();
            keys.Add(key);
        }

        if (keys == null) return;
        for (int i = 0; i < keys.Count; i++)
            DisableHighBuildMesh(keys[i]);
    }

    private void DisableHighBuildMesh(Vector3Int key)
    {
        if (!highBuildMeshes.TryGetValue(key, out HighBuildMeshData data)) return;
        DisableHighBuildColliders(data, true);
        if (data.mesh != null) data.mesh.Clear();
        if (data.root != null) data.root.SetActive(false);
    }

    private HighBuildMeshData GetOrCreateHighBuildMesh(Vector2Int coord, int section)
    {
        Vector3Int key = new Vector3Int(coord.x, section, coord.y);
        if (highBuildMeshes.TryGetValue(key, out HighBuildMeshData existing) && existing != null)
            return existing;

        GameObject go = new GameObject($"HighBuild_{coord.x}_{coord.y}_{section}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = gameObject.layer;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.materials = Material;

        Mesh mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mf.sharedMesh = mesh;
        go.SetActive(false);

        HighBuildMeshData created = new HighBuildMeshData
        {
            root = go,
            meshFilter = mf,
            meshRenderer = mr,
            mesh = mesh,
            activeBoxColliderCount = 0,
            hasColliderData = false
        };

        highBuildMeshes[key] = created;
        return created;
    }

    private void BuildHighBuildSectionColliders(HighBuildMeshData data, Vector2Int coord, int section, List<Vector3Int> positions)
    {
        int sizeX = Chunk.SizeX;
        int sizeY = HighBuildSectionHeight;
        int sizeZ = Chunk.SizeZ;
        int volume = sizeX * sizeY * sizeZ;

        bool[] solids = new bool[volume];
        bool[] visited = new bool[volume];

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int sectionMinY = Chunk.SizeY + section * HighBuildSectionHeight;

        for (int i = 0; i < positions.Count; i++)
        {
            Vector3Int pos = positions[i];
            if (!blockOverrides.TryGetValue(pos, out BlockType type))
                continue;

            BlockTextureMapping mapping = GetMappingSafe(type);
            if (!IsHighBuildColliderBlock(type, mapping))
                continue;

            int localX = pos.x - chunkMinX;
            int localY = pos.y - sectionMinY;
            int localZ = pos.z - chunkMinZ;

            if (localX < 0 || localX >= sizeX || localY < 0 || localY >= sizeY || localZ < 0 || localZ >= sizeZ)
                continue;

            int idx = GetHighBuildVoxelIndex(localX, localY, localZ, sizeX, sizeY);
            solids[idx] = true;
        }

        int colliderCount = 0;
        for (int y = 0; y < sizeY; y++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int startIndex = GetHighBuildVoxelIndex(x, y, z, sizeX, sizeY);
                    if (!solids[startIndex] || visited[startIndex])
                        continue;

                    int width = 1;
                    while (x + width < sizeX)
                    {
                        int idx = GetHighBuildVoxelIndex(x + width, y, z, sizeX, sizeY);
                        if (!solids[idx] || visited[idx]) break;
                        width++;
                    }

                    int depth = 1;
                    while (z + depth < sizeZ)
                    {
                        bool canGrowDepth = true;
                        for (int ix = 0; ix < width; ix++)
                        {
                            int idx = GetHighBuildVoxelIndex(x + ix, y, z + depth, sizeX, sizeY);
                            if (!solids[idx] || visited[idx])
                            {
                                canGrowDepth = false;
                                break;
                            }
                        }
                        if (!canGrowDepth) break;
                        depth++;
                    }

                    int boxHeight = 1;
                    while (y + boxHeight < sizeY)
                    {
                        bool canGrowHeight = true;
                        for (int iz = 0; iz < depth && canGrowHeight; iz++)
                        {
                            for (int ix = 0; ix < width; ix++)
                            {
                                int idx = GetHighBuildVoxelIndex(x + ix, y + boxHeight, z + iz, sizeX, sizeY);
                                if (!solids[idx] || visited[idx])
                                {
                                    canGrowHeight = false;
                                    break;
                                }
                            }
                        }
                        if (!canGrowHeight) break;
                        boxHeight++;
                    }

                    for (int iy = 0; iy < boxHeight; iy++)
                    {
                        for (int iz = 0; iz < depth; iz++)
                        {
                            for (int ix = 0; ix < width; ix++)
                            {
                                int idx = GetHighBuildVoxelIndex(x + ix, y + iy, z + iz, sizeX, sizeY);
                                visited[idx] = true;
                            }
                        }
                    }

                    BoxCollider box = GetOrCreateHighBuildBoxCollider(data, colliderCount++);
                    box.center = new Vector3(
                        chunkMinX + x + width * 0.5f,
                        sectionMinY + y + boxHeight * 0.5f,
                        chunkMinZ + z + depth * 0.5f);
                    box.size = new Vector3(width, boxHeight, depth);
                    box.enabled = true;
                }
            }
        }

        data.activeBoxColliderCount = colliderCount;
        data.hasColliderData = colliderCount > 0;

        for (int i = colliderCount; i < data.boxColliders.Count; i++)
        {
            BoxCollider box = data.boxColliders[i];
            if (box != null) box.enabled = false;
        }
    }

    private BoxCollider GetOrCreateHighBuildBoxCollider(HighBuildMeshData data, int index)
    {
        if (index < data.boxColliders.Count && data.boxColliders[index] != null)
            return data.boxColliders[index];

        BoxCollider created = data.root.AddComponent<BoxCollider>();
        created.isTrigger = false;

        if (index < data.boxColliders.Count)
            data.boxColliders[index] = created;
        else
            data.boxColliders.Add(created);

        return created;
    }

    private void DisableHighBuildColliders(HighBuildMeshData data, bool clearData)
    {
        if (data == null) return;

        int count = clearData ? data.boxColliders.Count : data.activeBoxColliderCount;
        for (int i = 0; i < count; i++)
        {
            BoxCollider box = data.boxColliders[i];
            if (box != null) box.enabled = false;
        }

        if (clearData)
        {
            data.activeBoxColliderCount = 0;
            data.hasColliderData = false;
        }
    }

    private static int GetHighBuildVoxelIndex(int x, int y, int z, int sizeX, int sizeY)
    {
        return x + y * sizeX + z * sizeX * sizeY;
    }

    private static int GetHighSectionIndex(int y)
    {
        if (y < Chunk.SizeY) return -1;
        return (y - Chunk.SizeY) / HighBuildSectionHeight;
    }

    private static bool IsHighBuildColliderBlock(BlockType type, BlockTextureMapping mapping)
    {
        if (type == BlockType.Air || type == BlockType.Water)
            return false;

        return mapping.isSolid && !mapping.isEmpty;
    }

    private BlockType GetBlockForHighMesh(Vector3Int pos)
    {
        if (blockOverrides.TryGetValue(pos, out BlockType t))
            return t;

        if (pos.y < 0) return BlockType.Air;
        if (pos.y >= Chunk.SizeY) return BlockType.Air;

        return GetBlockAt(pos);
    }

    private BlockTextureMapping GetMappingSafe(BlockType type)
    {
        int idx = (int)type;
        if (blockData == null || blockData.mappings == null || idx < 0 || idx >= blockData.mappings.Length)
            return default;
        return blockData.mappings[idx];
    }

    private bool IsFaceVisibleForHighBuild(BlockType current, BlockType neighbor)
    {
        if (current == neighbor)
        {
            BlockTextureMapping m = GetMappingSafe(current);
            if (current == BlockType.Water || m.isTransparent) return false;
        }

        BlockTextureMapping n = GetMappingSafe(neighbor);
        if (neighbor == BlockType.Air || n.isEmpty) return true;

        bool neighborOpaque = n.isSolid && !n.isTransparent;
        return !neighborOpaque;
    }

    private static Vector2Int GetTileForFace(BlockTextureMapping mapping, int faceIndex)
    {
        // 0:+X 1:-X 2:+Y 3:-Y 4:+Z 5:-Z
        if (faceIndex == 2) return mapping.top;
        if (faceIndex == 3) return mapping.bottom;
        return mapping.side;
    }

    private static bool GetTintForFace(BlockTextureMapping mapping, int faceIndex)
    {
        if (faceIndex == 2) return mapping.tintTop;
        if (faceIndex == 3) return mapping.tintBottom;
        return mapping.tintSide;
    }

    private struct FaceDef
    {
        public Vector3Int normal;
        public Vector3 normal3;
        public Vector3 v0;
        public Vector3 v1;
        public Vector3 v2;
        public Vector3 v3;
    }

    private static readonly FaceDef[] FaceDefs = new FaceDef[]
    {
        new FaceDef { normal = Vector3Int.right, normal3 = Vector3.right, v0 = new Vector3(1,0,0), v1 = new Vector3(1,1,0), v2 = new Vector3(1,1,1), v3 = new Vector3(1,0,1) },
        new FaceDef { normal = Vector3Int.left, normal3 = Vector3.left, v0 = new Vector3(0,0,1), v1 = new Vector3(0,1,1), v2 = new Vector3(0,1,0), v3 = new Vector3(0,0,0) },
        new FaceDef { normal = Vector3Int.up, normal3 = Vector3.up, v0 = new Vector3(0,1,1), v1 = new Vector3(1,1,1), v2 = new Vector3(1,1,0), v3 = new Vector3(0,1,0) },
        new FaceDef { normal = Vector3Int.down, normal3 = Vector3.down, v0 = new Vector3(0,0,0), v1 = new Vector3(1,0,0), v2 = new Vector3(1,0,1), v3 = new Vector3(0,0,1) },
        new FaceDef { normal = Vector3Int.forward, normal3 = Vector3.forward, v0 = new Vector3(1,0,1), v1 = new Vector3(1,1,1), v2 = new Vector3(0,1,1), v3 = new Vector3(0,0,1) },
        new FaceDef { normal = Vector3Int.back, normal3 = Vector3.back, v0 = new Vector3(0,0,0), v1 = new Vector3(0,1,0), v2 = new Vector3(1,1,0), v3 = new Vector3(1,0,0) }
    };
}
