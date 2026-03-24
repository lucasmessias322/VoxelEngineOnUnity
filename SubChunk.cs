using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class Subchunk : MonoBehaviour
{
    private bool hasColliderData;
    private bool canHaveColliders;
    private bool isVisible = true;
    private readonly List<BoxCollider> boxColliders = new List<BoxCollider>(128);
    private int activeBoxColliderCount;
    private bool[] colliderSolidsBuffer;
    private bool[] colliderVisitedBuffer;

    [HideInInspector]
    public bool hasGeometry;

    public bool CanHaveColliders => canHaveColliders;
    public bool HasColliderData => hasColliderData;
    public bool IsVisible => isVisible;

    public void Initialize(int subchunkIndex)
    {
        gameObject.name = $"SubchunkLogic_{subchunkIndex}";
        transform.localPosition = Vector3.zero;
        isVisible = true;
    }

    public void SetMeshState(bool geometryPresent, bool solidColliderGeometryPresent)
    {
        hasGeometry = geometryPresent;
        canHaveColliders = geometryPresent && solidColliderGeometryPresent;

        if (!canHaveColliders)
        {
            hasColliderData = false;
            DisableAllBoxColliders();
        }
    }

    public void SetVisible(bool visible)
    {
        isVisible = visible;
    }

    public void SetColliderSystemEnabled(bool enabled)
    {
        bool shouldEnable = enabled && hasGeometry && hasColliderData;
        for (int i = 0; i < activeBoxColliderCount; i++)
        {
            BoxCollider box = boxColliders[i];
            if (box != null)
                box.enabled = shouldEnable;
        }
    }

    public void ClearMesh()
    {
        hasGeometry = false;
        canHaveColliders = false;
        hasColliderData = false;
        DisableAllBoxColliders();
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
                        if (!solids[idx] || visited[idx])
                            break;
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

                        if (!canGrowDepth)
                            break;

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

                        if (!canGrowHeight)
                            break;

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
            if (box != null)
                box.enabled = false;
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
            if (box != null)
                box.enabled = false;
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
}

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ChunkRenderSlice : MonoBehaviour
{
    private static readonly VertexAttributeDescriptor[] ChunkVertexLayout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4)
    };

    private MeshFilter meshFilter;
    [HideInInspector] public MeshRenderer meshRenderer;
    private Mesh mesh;
    private int startSubchunkIndex;
    private int subchunkCount;
    private bool hasGeometry;

    public int StartSubchunkIndex => startSubchunkIndex;
    public int EndSubchunkIndexExclusive => startSubchunkIndex + subchunkCount;
    public bool HasGeometry => hasGeometry;

    public void Initialize(Material[] materials, int sliceIndex, int sliceStartSubchunkIndex, int sliceSubchunkCount)
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
                name = $"ChunkSliceMesh_{sliceIndex}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.MarkDynamic();
        }

        if (meshFilter.sharedMesh != mesh)
            meshFilter.sharedMesh = mesh;

        startSubchunkIndex = sliceStartSubchunkIndex;
        subchunkCount = Mathf.Max(1, sliceSubchunkCount);
        gameObject.name = $"ChunkSlice_{sliceIndex}";
        transform.localPosition = Vector3.zero;
    }

    public void ApplyMeshData(
        NativeList<MeshGenerator.PackedChunkVertex> vertices,
        NativeList<int> opaqueTris,
        NativeList<int> transparentTris,
        NativeList<int> billboardTris,
        NativeList<int> waterTris,
        NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
        Subchunk[] logicalSubchunks)
    {
        int totalVertexCount = 0;
        int totalOpaqueCount = 0;
        int totalTransparentCount = 0;
        int totalBillboardCount = 0;
        int totalWaterCount = 0;

        for (int sub = startSubchunkIndex; sub < EndSubchunkIndexExclusive; sub++)
        {
            MeshGenerator.SubchunkMeshRange range = subchunkRanges[sub];
            totalVertexCount += range.vertexCount;
            totalOpaqueCount += range.opaqueCount;
            totalTransparentCount += range.transparentCount;
            totalBillboardCount += range.billboardCount;
            totalWaterCount += range.waterCount;
        }

        if (totalVertexCount == 0)
        {
            ClearMesh();
            return;
        }

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var meshData = meshDataArray[0];
        meshData.SetVertexBufferParams(totalVertexCount, ChunkVertexLayout);

        NativeArray<MeshGenerator.PackedChunkVertex> vertexData = meshData.GetVertexData<MeshGenerator.PackedChunkVertex>();

        int totalIndexCount = totalOpaqueCount + totalTransparentCount + totalBillboardCount + totalWaterCount;
        meshData.SetIndexBufferParams(totalIndexCount, IndexFormat.UInt32);
        NativeArray<int> indexData = meshData.GetIndexData<int>();
        meshData.subMeshCount = 3;

        int targetVertexStart = 0;
        int opaqueTargetStart = 0;
        int transparentTargetStart = totalOpaqueCount;
        int waterTargetStart = totalOpaqueCount + totalTransparentCount + totalBillboardCount;

        int opaqueWriteOffset = opaqueTargetStart;
        int transparentWriteOffset = transparentTargetStart;
        int waterWriteOffset = waterTargetStart;

        for (int sub = startSubchunkIndex; sub < EndSubchunkIndexExclusive; sub++)
        {
            MeshGenerator.SubchunkMeshRange range = subchunkRanges[sub];
            if (range.vertexCount <= 0)
                continue;

            NativeArray<MeshGenerator.PackedChunkVertex>.Copy(
                vertices.AsArray(),
                range.vertexStart,
                vertexData,
                targetVertexStart,
                range.vertexCount);

            CopyTriangleRangeRebased(indexData, ref opaqueWriteOffset, opaqueTris, range.opaqueStart, range.opaqueCount, targetVertexStart);
            CopyTriangleRangeRebased(indexData, ref transparentWriteOffset, transparentTris, range.transparentStart, range.transparentCount, targetVertexStart);
            CopyTriangleRangeRebased(indexData, ref transparentWriteOffset, billboardTris, range.billboardStart, range.billboardCount, targetVertexStart);
            CopyTriangleRangeRebased(indexData, ref waterWriteOffset, waterTris, range.waterStart, range.waterCount, targetVertexStart);

            targetVertexStart += range.vertexCount;
        }

        meshData.SetSubMesh(0, new SubMeshDescriptor(opaqueTargetStart, totalOpaqueCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        meshData.SetSubMesh(1, new SubMeshDescriptor(transparentTargetStart, totalTransparentCount + totalBillboardCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        meshData.SetSubMesh(2, new SubMeshDescriptor(waterTargetStart, totalWaterCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        Mesh.ApplyAndDisposeWritableMeshData(
            meshDataArray,
            mesh,
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers);

        mesh.bounds = CreateMeshBounds(
            startSubchunkIndex * Chunk.SubchunkHeight,
            Mathf.Min(EndSubchunkIndexExclusive * Chunk.SubchunkHeight, Chunk.SizeY));

        hasGeometry = true;
        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        RefreshVisibility(logicalSubchunks);
    }

    public void RefreshVisibility(Subchunk[] logicalSubchunks)
    {
        bool shouldShow = hasGeometry && HasAnyVisibleGeometry(logicalSubchunks);

        if (!hasGeometry)
        {
            if (meshRenderer != null && meshRenderer.enabled)
                meshRenderer.enabled = false;
            if (gameObject.activeSelf)
                gameObject.SetActive(false);
            return;
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        if (meshRenderer != null && meshRenderer.enabled != shouldShow)
            meshRenderer.enabled = shouldShow;
    }

    public void ClearMesh()
    {
        if (mesh != null)
            mesh.Clear();

        hasGeometry = false;
        if (meshRenderer != null && meshRenderer.enabled)
            meshRenderer.enabled = false;
        if (gameObject.activeSelf)
            gameObject.SetActive(false);
    }

    private bool HasAnyVisibleGeometry(Subchunk[] logicalSubchunks)
    {
        if (logicalSubchunks == null)
            return false;

        int end = Mathf.Min(EndSubchunkIndexExclusive, logicalSubchunks.Length);
        for (int sub = startSubchunkIndex; sub < end; sub++)
        {
            Subchunk logicalSubchunk = logicalSubchunks[sub];
            if (logicalSubchunk != null && logicalSubchunk.hasGeometry && logicalSubchunk.IsVisible)
                return true;
        }

        return false;
    }

    private static void CopyTriangleRangeRebased(
        NativeArray<int> target,
        ref int targetStart,
        NativeList<int> source,
        int sourceStart,
        int count,
        int vertexDelta)
    {
        NativeArray<int> sourceArray = source.AsArray();
        for (int i = 0; i < count; i++)
            target[targetStart + i] = sourceArray[sourceStart + i] + vertexDelta;

        targetStart += count;
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

    private static Bounds CreateMeshBounds(int startY, int endY)
    {
        float height = Mathf.Max(1f, endY - startY);
        return new Bounds(
            new Vector3(Chunk.SizeX * 0.5f, startY + height * 0.5f, Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, height + 2f, Chunk.SizeZ + 2f));
    }
}
