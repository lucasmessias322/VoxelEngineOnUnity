using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Subchunk : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector4 uv2;
    }

    private MeshFilter meshFilter;
    [HideInInspector] public MeshRenderer meshRenderer;
    private Mesh mesh;
    private bool hasColliderData = false;
    private readonly List<BoxCollider> boxColliders = new List<BoxCollider>(128);
    private int activeBoxColliderCount = 0;

    [HideInInspector]
    public bool hasGeometry = false;

    public void Initialize(Material[] materials, int subchunkIndex)
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.materials = materials;

        mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;

        MeshCollider legacyMeshCollider = GetComponent<MeshCollider>();
        if (legacyMeshCollider != null)
            Destroy(legacyMeshCollider);

        transform.localPosition = Vector3.zero;
    }

    public void ApplyMeshData(
        NativeList<Vector3> vertices,
        NativeList<int> opaqueTris,
        NativeList<int> transparentTris,
        NativeList<int> billboardTris,
        NativeList<int> waterTris,
        NativeList<Vector2> uvs,
        NativeList<Vector2> uv2,
        NativeList<Vector3> normals,
        NativeList<Vector4> extraUVs,
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY,
        bool enableBlockColliders)
    {
        if (vertices.Length == 0)
        {
            hasGeometry = false;
            hasColliderData = false;
            DisableAllBoxColliders();
            gameObject.SetActive(false);
            return;
        }

        int vertexCount = vertices.Length;

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var meshData = meshDataArray[0];

        var vertexAttributes = new NativeArray<VertexAttributeDescriptor>(5, Allocator.Temp);
        vertexAttributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        vertexAttributes[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
        vertexAttributes[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);
        vertexAttributes[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2);
        vertexAttributes[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4);

        meshData.SetVertexBufferParams(vertexCount, vertexAttributes);
        vertexAttributes.Dispose();

        var vertData = meshData.GetVertexData<ChunkVertex>();
        for (int i = 0; i < vertexCount; i++)
        {
            vertData[i] = new ChunkVertex
            {
                position = vertices[i],
                normal = normals[i],
                uv0 = uvs[i],
                uv1 = uv2[i],
                uv2 = extraUVs[i]
            };
        }

        int totalIndices = opaqueTris.Length + transparentTris.Length + billboardTris.Length + waterTris.Length;
        meshData.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

        var indexData = meshData.GetIndexData<int>();
        int indexOffset = 0;
        meshData.subMeshCount = 3;

        int count = opaqueTris.Length;
        indexData.Slice(indexOffset, count).CopyFrom(opaqueTris.AsArray());
        meshData.SetSubMesh(0, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        indexOffset += count;

        int transparentStart = indexOffset;
        int transparentCount = transparentTris.Length + billboardTris.Length;
        if (transparentTris.Length > 0)
        {
            indexData.Slice(indexOffset, transparentTris.Length).CopyFrom(transparentTris.AsArray());
            indexOffset += transparentTris.Length;
        }
        if (billboardTris.Length > 0)
        {
            indexData.Slice(indexOffset, billboardTris.Length).CopyFrom(billboardTris.AsArray());
            indexOffset += billboardTris.Length;
        }
        meshData.SetSubMesh(1, new SubMeshDescriptor(transparentStart, transparentCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        count = waterTris.Length;
        indexData.Slice(indexOffset, count).CopyFrom(waterTris.AsArray());
        meshData.SetSubMesh(2, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        mesh.Clear();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers);
        mesh.RecalculateBounds();

        bool hasSolid = opaqueTris.Length > 0 || transparentTris.Length > 0;

        hasGeometry = true;
        gameObject.SetActive(true);
        meshRenderer.enabled = true;

        if (enableBlockColliders && hasSolid)
            BuildGreedyBoxColliders(voxelData, blockMappings, startY, endY);
        else
        {
            hasColliderData = false;
            DisableAllBoxColliders();
        }
    }

    public void SetVisible(bool visible)
    {
        if (meshRenderer == null) return;
        bool shouldShow = visible && hasGeometry;
        if (meshRenderer.enabled != shouldShow)
            meshRenderer.enabled = shouldShow;
    }

    public void SetColliderSystemEnabled(bool enabled)
    {
        bool shouldEnable = enabled && hasGeometry && hasColliderData;
        for (int i = 0; i < activeBoxColliderCount; i++)
        {
            BoxCollider box = boxColliders[i];
            if (box != null) box.enabled = shouldEnable;
        }
    }

    public void ClearMesh()
    {
        if (mesh != null) mesh.Clear();
        hasGeometry = false;
        hasColliderData = false;
        DisableAllBoxColliders();
        if (meshRenderer != null) meshRenderer.enabled = false;
        gameObject.SetActive(false);
    }

    private void BuildGreedyBoxColliders(NativeArray<byte> voxelData, BlockTextureMapping[] blockMappings, int startY, int endY)
    {
        if (!voxelData.IsCreated || blockMappings == null || blockMappings.Length == 0)
        {
            hasColliderData = false;
            DisableAllBoxColliders();
            return;
        }

        int clampedStartY = Mathf.Clamp(startY, 0, Chunk.SizeY);
        int clampedEndY = Mathf.Clamp(endY, 0, Chunk.SizeY);
        int height = clampedEndY - clampedStartY;
        if (height <= 0)
        {
            hasColliderData = false;
            DisableAllBoxColliders();
            return;
        }

        int sizeX = Chunk.SizeX;
        int sizeZ = Chunk.SizeZ;
        int plane = sizeX * sizeZ;
        int volume = sizeX * height * sizeZ;

        bool[] solids = new bool[volume];
        bool[] visited = new bool[volume];

        for (int y = 0; y < height; y++)
        {
            int worldY = clampedStartY + y;
            int worldYBase = worldY * plane;
            int localYBase = y * sizeX;

            for (int z = 0; z < sizeZ; z++)
            {
                int worldZBase = z * sizeX;
                int localZBase = z * sizeX * height;

                for (int x = 0; x < sizeX; x++)
                {
                    int worldIndex = worldYBase + worldZBase + x;
                    byte blockId = voxelData[worldIndex];
                    if (!IsBlockCollidable(blockId, blockMappings))
                        continue;

                    int localIndex = x + localYBase + localZBase;
                    solids[localIndex] = true;
                }
            }
        }

        int colliderCount = 0;
        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int startIndex = GetLocalIndex(x, y, z, sizeX, height);
                    if (!solids[startIndex] || visited[startIndex])
                        continue;

                    int width = 1;
                    while (x + width < sizeX)
                    {
                        int idx = GetLocalIndex(x + width, y, z, sizeX, height);
                        if (!solids[idx] || visited[idx]) break;
                        width++;
                    }

                    int depth = 1;
                    while (z + depth < sizeZ)
                    {
                        bool canGrowDepth = true;
                        for (int ix = 0; ix < width; ix++)
                        {
                            int idx = GetLocalIndex(x + ix, y, z + depth, sizeX, height);
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
                    while (y + boxHeight < height)
                    {
                        bool canGrowHeight = true;
                        for (int iz = 0; iz < depth && canGrowHeight; iz++)
                        {
                            for (int ix = 0; ix < width; ix++)
                            {
                                int idx = GetLocalIndex(x + ix, y + boxHeight, z + iz, sizeX, height);
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
                                int idx = GetLocalIndex(x + ix, y + iy, z + iz, sizeX, height);
                                visited[idx] = true;
                            }
                        }
                    }

                    BoxCollider box = GetOrCreateBoxCollider(colliderCount++);
                    box.center = new Vector3(
                        x + width * 0.5f,
                        clampedStartY + y + boxHeight * 0.5f,
                        z + depth * 0.5f);
                    box.size = new Vector3(width, boxHeight, depth);
                    box.enabled = true;
                }
            }
        }

        activeBoxColliderCount = colliderCount;
        hasColliderData = colliderCount > 0;

        for (int i = colliderCount; i < boxColliders.Count; i++)
        {
            BoxCollider box = boxColliders[i];
            if (box != null) box.enabled = false;
        }
    }

    private BoxCollider GetOrCreateBoxCollider(int index)
    {
        if (index < boxColliders.Count && boxColliders[index] != null)
            return boxColliders[index];

        BoxCollider created = gameObject.AddComponent<BoxCollider>();
        created.isTrigger = false;

        if (index < boxColliders.Count)
            boxColliders[index] = created;
        else
            boxColliders.Add(created);

        return created;
    }

    private void DisableAllBoxColliders()
    {
        activeBoxColliderCount = 0;
        for (int i = 0; i < boxColliders.Count; i++)
        {
            BoxCollider box = boxColliders[i];
            if (box != null) box.enabled = false;
        }
    }

    private static int GetLocalIndex(int x, int y, int z, int sizeX, int sizeY)
    {
        return x + y * sizeX + z * sizeX * sizeY;
    }

    private static bool IsBlockCollidable(byte blockId, BlockTextureMapping[] blockMappings)
    {
        if (blockId == (byte)BlockType.Air || blockId == (byte)BlockType.Water)
            return false;

        int mapIndex = blockId;
        if (mapIndex < 0 || mapIndex >= blockMappings.Length)
            return false;

        BlockTextureMapping mapping = blockMappings[mapIndex];
        return mapping.isSolid && !mapping.isEmpty;
    }
}
