using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Subchunk : MonoBehaviour
{
    private static readonly VertexAttributeDescriptor[] ChunkVertexLayout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4)
    };

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
    private bool canHaveColliders = false;
    private readonly List<BoxCollider> boxColliders = new List<BoxCollider>(128);
    private int activeBoxColliderCount = 0;
    private bool[] colliderSolidsBuffer;
    private bool[] colliderVisitedBuffer;

    [HideInInspector]
    public bool hasGeometry = false;
    public bool CanHaveColliders => canHaveColliders;
    public bool HasColliderData => hasColliderData;

    public void Initialize(Material[] materials, int subchunkIndex)
    {
        meshFilter = meshFilter != null ? meshFilter : GetComponent<MeshFilter>();
        meshRenderer = meshRenderer != null ? meshRenderer : GetComponent<MeshRenderer>();

        Material[] sharedMaterials = materials ?? System.Array.Empty<Material>();
        if (!HasSameSharedMaterials(meshRenderer.sharedMaterials, sharedMaterials))
            meshRenderer.sharedMaterials = sharedMaterials;

        mesh = mesh != null ? mesh : meshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh
            {
                name = $"SubchunkMesh_{subchunkIndex}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.MarkDynamic();
        }

        if (meshFilter.sharedMesh != mesh)
            meshFilter.sharedMesh = mesh;

        MeshCollider legacyMeshCollider = GetComponent<MeshCollider>();
        if (legacyMeshCollider != null)
            Destroy(legacyMeshCollider);

        transform.localPosition = Vector3.zero;
    }

    private static bool HasSameSharedMaterials(Material[] current, Material[] desired)
    {
        if (ReferenceEquals(current, desired))
            return true;
        if (current == null || desired == null)
            return current == desired;
        if (current.Length != desired.Length)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != desired[i])
                return false;
        }

        return true;
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
        int startY,
        int endY)
    {
        ApplyMeshData(
            vertices,
            opaqueTris,
            transparentTris,
            billboardTris,
            waterTris,
            uvs,
            uv2,
            normals,
            extraUVs,
            new MeshGenerator.SubchunkMeshRange
            {
                vertexStart = 0,
                vertexCount = vertices.Length,
                opaqueStart = 0,
                opaqueCount = opaqueTris.Length,
                transparentStart = 0,
                transparentCount = transparentTris.Length,
                billboardStart = 0,
                billboardCount = billboardTris.Length,
                waterStart = 0,
                waterCount = waterTris.Length
            },
            startY,
            endY);
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
        MeshGenerator.SubchunkMeshRange range,
        int startY,
        int endY)
    {
        if (range.vertexCount == 0)
        {
            ClearMesh();
            return;
        }

        int vertexCount = range.vertexCount;

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var meshData = meshDataArray[0];
        meshData.SetVertexBufferParams(vertexCount, ChunkVertexLayout);

        var vertData = meshData.GetVertexData<ChunkVertex>();
        for (int i = 0; i < vertexCount; i++)
        {
            int src = range.vertexStart + i;
            vertData[i] = new ChunkVertex
            {
                position = vertices[src],
                normal = normals[src],
                uv0 = uvs[src],
                uv1 = uv2[src],
                uv2 = extraUVs[src]
            };
        }

        int totalIndices = range.opaqueCount + range.transparentCount + range.billboardCount + range.waterCount;
        meshData.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

        var indexData = meshData.GetIndexData<int>();
        int indexOffset = 0;
        meshData.subMeshCount = 3;

        int count = range.opaqueCount;
        CopyTriangleRange(indexData, indexOffset, opaqueTris, range.opaqueStart, count, range.vertexStart);
        meshData.SetSubMesh(0, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        indexOffset += count;

        int transparentStart = indexOffset;
        int transparentCount = range.transparentCount + range.billboardCount;
        if (range.transparentCount > 0)
        {
            CopyTriangleRange(indexData, indexOffset, transparentTris, range.transparentStart, range.transparentCount, range.vertexStart);
            indexOffset += range.transparentCount;
        }
        if (range.billboardCount > 0)
        {
            CopyTriangleRange(indexData, indexOffset, billboardTris, range.billboardStart, range.billboardCount, range.vertexStart);
            indexOffset += range.billboardCount;
        }
        meshData.SetSubMesh(1, new SubMeshDescriptor(transparentStart, transparentCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        count = range.waterCount;
        CopyTriangleRange(indexData, indexOffset, waterTris, range.waterStart, count, range.vertexStart);
        meshData.SetSubMesh(2, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers);
        mesh.bounds = CreateMeshBounds(startY, endY);

        bool hasSolid = range.opaqueCount > 0 || range.transparentCount > 0;
        canHaveColliders = hasSolid;

        hasGeometry = true;
        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
        if (meshRenderer != null && !meshRenderer.enabled)
            meshRenderer.enabled = true;

        if (!hasSolid)
        {
            hasColliderData = false;
            DisableAllBoxColliders();
        }
    }

    private static void CopyTriangleRange(
        NativeArray<int> target,
        int targetStart,
        NativeList<int> source,
        int sourceStart,
        int count,
        int vertexBase)
    {
        for (int i = 0; i < count; i++)
            target[targetStart + i] = source[sourceStart + i] - vertexBase;
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
        if (!hasGeometry)
        {
            canHaveColliders = false;
            hasColliderData = false;
            DisableAllBoxColliders();
            if (meshRenderer != null && meshRenderer.enabled)
                meshRenderer.enabled = false;
            if (gameObject.activeSelf)
                gameObject.SetActive(false);
            return;
        }

        if (mesh != null) mesh.Clear();
        hasGeometry = false;
        canHaveColliders = false;
        hasColliderData = false;
        DisableAllBoxColliders();
        if (meshRenderer != null && meshRenderer.enabled)
            meshRenderer.enabled = false;
        if (gameObject.activeSelf)
            gameObject.SetActive(false);
    }

    public void ClearColliderData()
    {
        hasColliderData = false;
        DisableAllBoxColliders();
    }

    public void RebuildColliders(
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY)
    {
        if (!hasGeometry || !canHaveColliders)
        {
            hasColliderData = false;
            DisableAllBoxColliders();
            return;
        }

        BuildGreedyBoxColliders(voxelData, blockMappings, startY, endY);
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

        EnsureColliderBuffers(volume);
        bool[] solids = colliderSolidsBuffer;
        bool[] visited = colliderVisitedBuffer;
        System.Array.Clear(solids, 0, volume);
        System.Array.Clear(visited, 0, volume);

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

    private void EnsureColliderBuffers(int volume)
    {
        if (volume <= 0)
            return;

        if (colliderSolidsBuffer == null || colliderSolidsBuffer.Length < volume)
            colliderSolidsBuffer = new bool[volume];

        if (colliderVisitedBuffer == null || colliderVisitedBuffer.Length < volume)
            colliderVisitedBuffer = new bool[volume];
    }

    private static bool IsBlockCollidable(byte blockId, BlockTextureMapping[] blockMappings)
    {
        BlockType blockType = (BlockType)blockId;
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        if (TorchPlacementUtility.IsTorchLike(blockType))
            return false;

        int mapIndex = blockId;
        if (mapIndex < 0 || mapIndex >= blockMappings.Length)
            return false;

        BlockTextureMapping mapping = blockMappings[mapIndex];
        return mapping.isSolid && !mapping.isEmpty;
    }

    private static Bounds CreateMeshBounds(int startY, int endY)
    {
        float height = Mathf.Max(1f, endY - startY);

        // Small padding keeps custom meshes and billboards inside bounds
        // without paying RecalculateBounds() every rebuild.
        return new Bounds(
            new Vector3(Chunk.SizeX * 0.5f, startY + height * 0.5f, Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, height + 2f, Chunk.SizeZ + 2f));
    }
}
