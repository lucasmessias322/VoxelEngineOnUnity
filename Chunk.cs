// using UnityEngine;
// using Unity.Collections;
// using System.Collections.Generic;
// using System.Runtime.CompilerServices;

// [RequireComponent(typeof(MeshFilter))]
// [RequireComponent(typeof(MeshRenderer))]
// public class Chunk : MonoBehaviour
// {
//     public const int SizeX = 16;
//     public const int SizeY = 256;
//     public const int SizeZ = 16;

//     public const int SubChunkSize = 16;
//     public const int SubChunkCountY = SizeY / SubChunkSize;

//     public NativeArray<byte> skylight;
//     public bool HasSkylight => skylight.IsCreated;

//     public int surfaceSubY;

//     private MeshFilter meshFilter;
//     private MeshRenderer meshRenderer;
//     private Mesh mesh;

//     public SubChunk[] subChunks = new SubChunk[SubChunkCountY];

//     [SerializeField] private Material[] materials;

//     // 🔥 voxel data usado SOMENTE para colisão
//     // --- substitua a antiga declaração e SetVoxelData por este bloco ---
//     // 🔥 voxel data usado SOMENTE para colisão (agora 3D corretamente)
//     // --- Substituir declaração antiga por este bloco ---
//     // voxel data usado SOMENTE para colisão (3D: [x,y,z])
//     public byte[,,] voxelData;
//     public Dictionary<int, byte> overrides = new Dictionary<int, byte>();

//     [MethodImpl(MethodImplOptions.AggressiveInlining)]
//     public static int ToIndex(int x, int y, int z)
//     {
//         return x + y * SizeX + z * (SizeX * SizeY);
//     }

//     /// <summary>
//     /// Substitui o voxelData do chunk (array 3D [x,y,z]).
//     /// </summary>
//     public void SetVoxelData(byte[,,] data)
//     {
//         voxelData = data;
//     }






//     public enum ChunkState
//     {
//         Requested,
//         MeshReady,
//         Active
//     }

//     public ChunkState state;
//     public int generation;
//     public Vector2Int coord;

//     // =========================
//     // INIT
//     // =========================
//     private void Awake()
//     {
//         meshFilter = GetComponent<MeshFilter>();
//         meshRenderer = GetComponent<MeshRenderer>();

//         mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
//         mesh.MarkDynamic();
//         meshFilter.sharedMesh = mesh;

//         for (int i = 0; i < SubChunkCountY; i++)
//         {
//             GameObject go = new GameObject($"SubChunk_{i}");
//             go.transform.SetParent(transform, false);

//             MeshFilter mf = go.AddComponent<MeshFilter>();
//             MeshRenderer mr = go.AddComponent<MeshRenderer>();

//             Mesh m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
//             m.MarkDynamic();
//             mf.sharedMesh = m;

//             if (materials != null && materials.Length > 0)
//                 mr.sharedMaterials = materials;

//             subChunks[i] = new SubChunk
//             {
//                 yIndex = i,
//                 mesh = m,
//                 filter = mf,
//                 renderer = mr,
//                 isEmpty = true,
//                 collidersBuilt = false,
//                 aabbColliders = new List<BoxCollider>(32),
//                 bounds = new Bounds(
//                     new Vector3(SizeX * 0.5f, i * SubChunkSize + SubChunkSize * 0.5f, SizeZ * 0.5f),
//                     new Vector3(SizeX, SubChunkSize, SizeZ)
//                 )
//             };
//         }
//     }



//     public void SetMaterials(Material[] mats)
//     {
//         materials = mats;
//         if (meshRenderer != null)
//             meshRenderer.sharedMaterials = mats;

//         foreach (var sc in subChunks)
//             if (sc?.renderer != null)
//                 sc.renderer.sharedMaterials = mats;
//     }

//     // =========================
//     // SUBCHUNK MESH + COLLISION
//     // =========================
//     public void ApplySubChunkMeshData(
//         int subIndex,
//         Vector3[] verts,
//         int[] opaqueTris,
//         int[] waterTris,
//         Vector2[] uvs,
//         Vector3[] normals,
//         byte[] vertexLights
//     )
//     {
//         if (subIndex < 0 || subIndex >= SubChunkCountY) return;
//         SubChunk sc = subChunks[subIndex];
//         if (sc == null) return;

//         if (verts == null || verts.Length == 0)
//         {
//             sc.isEmpty = true;
//             sc.renderer.enabled = false;
//             sc.mesh.Clear(false);
//             return;
//         }

//         sc.isEmpty = false;

//         Mesh m = sc.mesh;
//         m.Clear(false);
//         m.SetVertices(verts);

//         if (uvs != null && uvs.Length == verts.Length)
//             m.SetUVs(0, uvs);

//         if (normals != null && normals.Length == verts.Length)
//             m.SetNormals(normals);

//         m.subMeshCount = 2;
//         m.SetIndices(opaqueTris ?? new int[0], MeshTopology.Triangles, 0, false);
//         m.SetIndices(waterTris ?? new int[0], MeshTopology.Triangles, 1, false);

//         if (vertexLights != null && vertexLights.Length == verts.Length)
//         {
//             Color[] cols = new Color[verts.Length];
//             for (int i = 0; i < verts.Length; i++)
//             {
//                 float l = Mathf.Lerp(0.15f, 1f, vertexLights[i] / 15f);
//                 cols[i] = new Color(l, l, l, 1f);
//             }
//             m.colors = cols;
//         }

//         m.RecalculateBounds();
//         sc.filter.sharedMesh = m;
//         sc.renderer.enabled = true;


//     }

//     // =========================
//     // AABB COLLISION (BEDROCK)
//     // =========================


//     // Substitui ClearAABBs
//     private void ClearAABBs(SubChunk sc)
//     {
//         // Em vez de destruir, apenas desativa para reutilização posterior.
//         if (sc.aabbColliders != null)
//         {
//             for (int i = 0; i < sc.aabbColliders.Count; i++)
//             {
//                 var c = sc.aabbColliders[i];
//                 if (c) c.enabled = false;
//             }
//         }
//         sc.collidersBuilt = false;
//     }
//     // Substitui GenerateAABBsMerged
//     private void GenerateAABBsMerged(SubChunk sc)
//     {
//         int yBase = sc.yIndex * SubChunkSize;

//         // Dicionário: key = (startY << 8) | height  -> lista de células (x,z)
//         var runs = new Dictionary<int, List<Vector2Int>>();

//         for (int x = 0; x < SizeX; x++)
//         {
//             for (int z = 0; z < SizeZ; z++)
//             {
//                 int startY = -1;
//                 for (int y = 0; y < SubChunkSize; y++)
//                 {
//                     byte block = voxelData[x, yBase + y, z];
//                     bool solid = BlockDataSO.IsSolidCache[block];

//                     if (solid && startY == -1)
//                     {
//                         startY = y;
//                     }

//                     if ((!solid || y == SubChunkSize - 1) && startY != -1)
//                     {
//                         int endY = (solid && y == SubChunkSize - 1) ? y : y - 1;
//                         int height = endY - startY + 1;

//                         int key = (startY << 8) | (height & 0xFF);
//                         if (!runs.TryGetValue(key, out var list))
//                         {
//                             list = new List<Vector2Int>();
//                             runs[key] = list;
//                         }
//                         list.Add(new Vector2Int(x, z));
//                         startY = -1;
//                     }
//                 }
//             }
//         }

//         // Reuse existing colliders em sc.aabbColliders em vez de destruir/criar.
//         int usedColliders = 0;

//         foreach (var kv in runs)
//         {
//             int key = kv.Key;
//             int startY = (key >> 8) & 0xFF;
//             int height = key & 0xFF;

//             // occupancy grid
//             bool[,] occ = new bool[SizeX, SizeZ];
//             foreach (var cell in kv.Value)
//                 occ[cell.x, cell.y] = true;

//             bool[,] visited = new bool[SizeX, SizeZ];

//             for (int zx = 0; zx < SizeX; zx++)
//             {
//                 for (int zz = 0; zz < SizeZ; zz++)
//                 {
//                     if (!occ[zx, zz] || visited[zx, zz]) continue;

//                     // expand largura (x)
//                     int w = 1;
//                     while (zx + w < SizeX && occ[zx + w, zz] && !visited[zx + w, zz]) w++;

//                     // expand profundidade (z)
//                     int d = 1;
//                     bool canExpand = true;
//                     while (canExpand && (zz + d) < SizeZ)
//                     {
//                         for (int xi = zx; xi < zx + w; xi++)
//                         {
//                             if (!occ[xi, zz + d] || visited[xi, zz + d])
//                             {
//                                 canExpand = false;
//                                 break;
//                             }
//                         }
//                         if (canExpand) d++;
//                     }

//                     // marca visitado
//                     for (int xi = zx; xi < zx + w; xi++)
//                         for (int zi = zz; zi < zz + d; zi++)
//                             visited[xi, zi] = true;

//                     // Reuse ou crie novo collider apenas se necessário
//                     BoxCollider bc;
//                     if (usedColliders < sc.aabbColliders.Count && sc.aabbColliders[usedColliders] != null)
//                     {
//                         bc = sc.aabbColliders[usedColliders];
//                         bc.enabled = true;
//                     }
//                     else
//                     {
//                         bc = sc.filter.gameObject.AddComponent<BoxCollider>();
//                         // assegura lista inicializada
//                         if (sc.aabbColliders == null) sc.aabbColliders = new List<BoxCollider>();
//                         sc.aabbColliders.Add(bc);
//                     }

//                     // center (local ao chunk/subchunk root)
//                     bc.center = new Vector3(
//                         zx + (w * 0.5f),
//                         yBase + startY + (height * 0.5f),
//                         zz + (d * 0.5f)
//                     );
//                     bc.size = new Vector3(w, height, d);

//                     usedColliders++;
//                 }
//             }
//         }

//         // Desativa colliders excedentes (mantém-os para reutilização futura)
//         for (int i = usedColliders; i < sc.aabbColliders.Count; i++)
//         {
//             if (sc.aabbColliders[i]) sc.aabbColliders[i].enabled = false;
//         }
//     }

//     public void EnableColliders()
//     {
//         if (voxelData == null) return;

//         foreach (var sc in subChunks)
//         {
//             if (sc == null || sc.isEmpty || sc.collidersBuilt)
//                 continue;


//             if (!sc.collidersBuilt)
//             {
//                 GenerateAABBsMerged(sc);
//                 sc.collidersBuilt = true;
//             }

//         }
//     }
//     public void DisableColliders()
//     {
//         foreach (var sc in subChunks)
//         {
//             if (sc == null || !sc.collidersBuilt)
//                 continue;

//             ClearAABBs(sc);
//         }
//     }

//     // =========================
//     // VISIBILITY (BEDROCK)
//     // =========================
//     public void UpdateSubchunkVisibilityBedrock(
//         int playerSubY,
//         int surfaceSubY,
//         int up,
//         int down,
//         Plane[] planes
//     )
//     {
//         int centerY = playerSubY < surfaceSubY ? playerSubY : surfaceSubY;

//         for (int i = 0; i < SubChunkCountY; i++)
//         {
//             var sc = subChunks[i];
//             if (sc == null || sc.isEmpty)
//             {
//                 sc.renderer.enabled = false;
//                 continue;
//             }

//             if (i < centerY - down || i > centerY + up)
//             {
//                 sc.renderer.enabled = false;
//                 continue;
//             }

//             Bounds worldBounds = sc.bounds;
//             worldBounds.center = transform.TransformPoint(sc.bounds.center);

//             sc.renderer.enabled =
//                 GeometryUtility.TestPlanesAABB(planes, worldBounds);
//         }
//     }
//     // adiciona em Chunk.cs (pode ficar perto de EnableColliders / GenerateAABBsMerged)
//     public void RebuildCollidersForSubchunk(int subIdx)
//     {
//         if (voxelData == null) return;
//         if (subIdx < 0 || subIdx >= subChunks.Length) return;
//         var sc = subChunks[subIdx];
//         // marca como não-built e força geração imediata
//         sc.collidersBuilt = false;
//         // GenerateAABBsMerged é private — se for private, torne-o internal/private com este wrapper
//         GenerateAABBsMerged(sc);
//         sc.collidersBuilt = true;
//     }

//     // =========================
//     // LIFECYCLE
//     // =========================
//     public void SetCoord(Vector2Int c)
//     {
//         coord = c;
//         gameObject.name = $"Chunk_{c.x}_{c.y}";
//     }

//     public void ResetChunk()
//     {
//         gameObject.SetActive(false);
//         generation = 0;

//         foreach (var sc in subChunks)
//         {
//             if (sc == null) continue;
//             ClearAABBs(sc); // agora apenas desativa, não destrói
//             sc.isEmpty = true;
//             if (sc.renderer) sc.renderer.enabled = false;
//         }
//     }

// }

// // =========================
// // DATA STRUCTURES
// // =========================
// public class SubChunk
// {
//     public int yIndex;
//     public Mesh mesh;
//     public MeshRenderer renderer;
//     public MeshFilter filter;
//     public bool isEmpty;
//     public Bounds bounds;

//     public bool collidersBuilt;
//     public List<BoxCollider> aabbColliders;


// }

using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 256;
    public const int SizeZ = 16;

    public const int SubChunkSize = 16;
    public const int SubChunkCountY = SizeY / SubChunkSize;

    public int surfaceSubY;

    // =========================
    // COMPONENTS
    // =========================
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;

    public SubChunk[] subChunks = new SubChunk[SubChunkCountY];

    [SerializeField] private Material[] materials;

    // =========================
    // VOXEL DATA (COLLISION)
    // =========================
    // 3D voxel data usado para colisão e queries
    public byte[,,] voxelData;

    // Substitua SetVoxelData para aceitar 3D
    public void SetVoxelData(byte[,,] data)
    {
        voxelData = data;
    }

    // 🔥 Overrides persistentes (Minecraft-style)
    // key = flattened index (x + y*SizeX + z*SizeX*SizeY)
    public Dictionary<int, byte> overrides = new Dictionary<int, byte>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToIndex(int x, int y, int z)
    {
        return x + y * SizeX + z * (SizeX * SizeY);
    }


    // =========================
    // STATE
    // =========================
    public int generation;
    public Vector2Int coord;

    // =========================
    // INIT
    // =========================
    private void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;

        for (int i = 0; i < SubChunkCountY; i++)
        {
            GameObject go = new GameObject($"SubChunk_{i}");
            go.transform.SetParent(transform, false);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();

            Mesh m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.MarkDynamic();
            mf.sharedMesh = m;

            if (materials != null && materials.Length > 0)
                mr.sharedMaterials = materials;

            subChunks[i] = new SubChunk
            {
                yIndex = i,
                mesh = m,
                filter = mf,
                renderer = mr,
                isEmpty = true,
                collidersBuilt = false,
                aabbColliders = new List<BoxCollider>(32),
                bounds = new Bounds(
                    new Vector3(SizeX * 0.5f, i * SubChunkSize + SubChunkSize * 0.5f, SizeZ * 0.5f),
                    new Vector3(SizeX, SubChunkSize, SizeZ)
                )
            };
        }
    }

    public void SetMaterials(Material[] mats)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;

        foreach (var sc in subChunks)
            if (sc?.renderer != null)
                sc.renderer.sharedMaterials = mats;
    }

    // =========================
    // SUBCHUNK MESH
    // =========================
    public void ApplySubChunkMeshData(
        int subIndex,
        Vector3[] verts,
        int[] opaqueTris,
        int[] waterTris,
        Vector2[] uvs,
        Vector3[] normals,
        byte[] vertexLights
    )
    {
        if (subIndex < 0 || subIndex >= SubChunkCountY) return;
        SubChunk sc = subChunks[subIndex];
        if (sc == null) return;

        if (verts == null || verts.Length == 0)
        {
            sc.isEmpty = true;
            sc.renderer.enabled = false;
            sc.mesh.Clear(false);
            return;
        }

        sc.isEmpty = false;

        Mesh m = sc.mesh;
        m.Clear(false);
        m.SetVertices(verts);

        if (uvs != null && uvs.Length == verts.Length)
            m.SetUVs(0, uvs);

        if (normals != null && normals.Length == verts.Length)
            m.SetNormals(normals);

        m.subMeshCount = 2;
        m.SetIndices(opaqueTris ?? new int[0], MeshTopology.Triangles, 0, false);
        m.SetIndices(waterTris ?? new int[0], MeshTopology.Triangles, 1, false);

        if (vertexLights != null && vertexLights.Length == verts.Length)
        {
            Color[] cols = new Color[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                float l = Mathf.Lerp(0.15f, 1f, vertexLights[i] / 15f);
                cols[i] = new Color(l, l, l, 1f);
            }
            m.colors = cols;
        }

        m.RecalculateBounds();
        sc.filter.sharedMesh = m;
        sc.renderer.enabled = true;
    }

    // =========================
    // COLLIDERS (AABB MERGED)
    // =========================
    private void ClearAABBs(SubChunk sc)
    {
        if (sc.aabbColliders == null) return;
        for (int i = 0; i < sc.aabbColliders.Count; i++)
        {
            if (sc.aabbColliders[i])
                sc.aabbColliders[i].enabled = false;
        }
        sc.collidersBuilt = false;
    }

    private void GenerateAABBsMerged(SubChunk sc)
    {
        int yBase = sc.yIndex * SubChunkSize;
        int used = 0;

        for (int x = 0; x < SizeX; x++)
        {
            for (int z = 0; z < SizeZ; z++)
            {
                int startY = -1;

                for (int y = 0; y < SubChunkSize; y++)
                {
                    byte block = voxelData[x, yBase + y, z];
                    bool solid = BlockDataSO.IsSolidCache[block];

                    if (solid && startY == -1)
                        startY = y;

                    if ((!solid || y == SubChunkSize - 1) && startY != -1)
                    {
                        int endY = (solid && y == SubChunkSize - 1) ? y : y - 1;
                        int height = endY - startY + 1;

                        BoxCollider bc;
                        if (used < sc.aabbColliders.Count)
                        {
                            bc = sc.aabbColliders[used];
                            bc.enabled = true;
                        }
                        else
                        {
                            bc = sc.filter.gameObject.AddComponent<BoxCollider>();
                            sc.aabbColliders.Add(bc);
                        }

                        bc.center = new Vector3(
                            x + 0.5f,
                            yBase + startY + height * 0.5f,
                            z + 0.5f
                        );
                        bc.size = new Vector3(1, height, 1);

                        used++;
                        startY = -1;
                    }
                }
            }
        }

        for (int i = used; i < sc.aabbColliders.Count; i++)
            if (sc.aabbColliders[i])
                sc.aabbColliders[i].enabled = false;

        sc.collidersBuilt = true;
    }

    public void EnableColliders()
    {
        if (voxelData == null) return;

        foreach (var sc in subChunks)
        {
            if (sc == null || sc.isEmpty || sc.collidersBuilt) continue;
            GenerateAABBsMerged(sc);
        }
    }

    public void DisableColliders()
    {
        foreach (var sc in subChunks)
        {
            if (sc == null || !sc.collidersBuilt) continue;
            ClearAABBs(sc);
        }
    }

    public void RebuildCollidersForSubchunk(int subIdx)
    {
        if (voxelData == null) return;
        if (subIdx < 0 || subIdx >= subChunks.Length) return;

        var sc = subChunks[subIdx];
        ClearAABBs(sc);
        GenerateAABBsMerged(sc);
    }

    // =========================
    // VISIBILITY (BEDROCK)
    // =========================
    public void UpdateSubchunkVisibilityBedrock(
        int playerSubY,
        int surfaceSubY,
        int up,
        int down,
        Plane[] planes
    )
    {
        int centerY = playerSubY < surfaceSubY ? playerSubY : surfaceSubY;

        for (int i = 0; i < SubChunkCountY; i++)
        {
            var sc = subChunks[i];
            if (sc == null || sc.isEmpty)
            {
                sc.renderer.enabled = false;
                continue;
            }

            if (i < centerY - down || i > centerY + up)
            {
                sc.renderer.enabled = false;
                continue;
            }

            Bounds worldBounds = sc.bounds;
            worldBounds.center = transform.TransformPoint(sc.bounds.center);
            sc.renderer.enabled = GeometryUtility.TestPlanesAABB(planes, worldBounds);
        }
    }

    // =========================
    // LIFECYCLE
    // =========================
    public void SetCoord(Vector2Int c)
    {
        coord = c;
        gameObject.name = $"Chunk_{c.x}_{c.y}";
    }

    public void ResetChunk()
    {
        gameObject.SetActive(false);
        generation = 0;

        overrides.Clear();

        foreach (var sc in subChunks)
        {
            if (sc == null) continue;
            ClearAABBs(sc);
            sc.isEmpty = true;
            if (sc.renderer) sc.renderer.enabled = false;
        }
    }
}

// =========================
// SUBCHUNK STRUCT
// =========================
public class SubChunk
{
    public int yIndex;
    public Mesh mesh;
    public MeshRenderer renderer;
    public MeshFilter filter;
    public bool isEmpty;
    public Bounds bounds;

    public bool collidersBuilt;
    public List<BoxCollider> aabbColliders;
}
