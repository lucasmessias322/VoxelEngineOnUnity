using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

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
    private bool externallySuppressed;

    private readonly struct SliceMeshTotals
    {
        public readonly int vertexCount;
        public readonly int opaqueCount;
        public readonly int transparentCount;
        public readonly int billboardCount;
        public readonly int waterCount;

        public SliceMeshTotals(
            int vertexCount,
            int opaqueCount,
            int transparentCount,
            int billboardCount,
            int waterCount)
        {
            this.vertexCount = vertexCount;
            this.opaqueCount = opaqueCount;
            this.transparentCount = transparentCount;
            this.billboardCount = billboardCount;
            this.waterCount = waterCount;
        }

        public int TotalTransparentLikeCount => transparentCount + billboardCount;
        public int TotalIndexCount => opaqueCount + transparentCount + billboardCount + waterCount;
    }

    public int StartSubchunkIndex => startSubchunkIndex;
    public int EndSubchunkIndexExclusive => startSubchunkIndex + subchunkCount;
    public bool HasGeometry => hasGeometry;
    public Mesh SharedMesh => meshFilter != null ? meshFilter.sharedMesh : mesh;
    public static VertexAttributeDescriptor[] SharedVertexLayout => ChunkVertexLayout;

    public void Initialize(Material[] materials, int sliceIndex, int sliceStartSubchunkIndex, int sliceSubchunkCount)
    {
        meshFilter = meshFilter != null ? meshFilter : GetComponent<MeshFilter>();
        meshRenderer = meshRenderer != null ? meshRenderer : GetComponent<MeshRenderer>();

        Material[] sharedMaterials = materials ?? Array.Empty<Material>();
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
        SliceMeshTotals totals = CalculateMeshTotals(subchunkRanges);
        if (totals.vertexCount == 0)
        {
            ClearMesh();
            return;
        }

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var meshData = meshDataArray[0];
        meshData.SetVertexBufferParams(totals.vertexCount, ChunkVertexLayout);
        meshData.SetIndexBufferParams(totals.TotalIndexCount, IndexFormat.UInt32);
        meshData.subMeshCount = 3;

        NativeArray<MeshGenerator.PackedChunkVertex> vertexData = meshData.GetVertexData<MeshGenerator.PackedChunkVertex>();
        NativeArray<int> indexData = meshData.GetIndexData<int>();

        CopySliceGeometry(vertices, opaqueTris, transparentTris, billboardTris, waterTris, subchunkRanges, vertexData, indexData, totals);
        ConfigureSubMeshes(meshData, totals);

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
        SetActiveState(true);
        RefreshVisibility(logicalSubchunks);
    }

    public void RefreshVisibility(Subchunk[] logicalSubchunks)
    {
        bool shouldShow = hasGeometry && !externallySuppressed && HasAnyVisibleGeometry(logicalSubchunks);

        if (!hasGeometry)
        {
            SetRendererEnabled(false);
            SetActiveState(false);
            return;
        }

        SetActiveState(true);
        SetRendererEnabled(shouldShow);
    }

    public void SetExternallySuppressed(bool suppressed)
    {
        externallySuppressed = suppressed;
    }

    public void ClearMesh()
    {
        if (mesh != null)
            mesh.Clear();

        hasGeometry = false;
        externallySuppressed = false;
        SetRendererEnabled(false);
        SetActiveState(false);
    }

    private SliceMeshTotals CalculateMeshTotals(NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges)
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

        return new SliceMeshTotals(
            totalVertexCount,
            totalOpaqueCount,
            totalTransparentCount,
            totalBillboardCount,
            totalWaterCount);
    }

    private void CopySliceGeometry(
        NativeList<MeshGenerator.PackedChunkVertex> vertices,
        NativeList<int> opaqueTris,
        NativeList<int> transparentTris,
        NativeList<int> billboardTris,
        NativeList<int> waterTris,
        NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
        NativeArray<MeshGenerator.PackedChunkVertex> vertexData,
        NativeArray<int> indexData,
        SliceMeshTotals totals)
    {
        int targetVertexStart = 0;
        int opaqueWriteOffset = 0;
        int transparentWriteOffset = totals.opaqueCount;
        int waterWriteOffset = totals.opaqueCount + totals.TotalTransparentLikeCount;

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
    }

    private static void ConfigureSubMeshes(Mesh.MeshData meshData, SliceMeshTotals totals)
    {
        int transparentStart = totals.opaqueCount;
        int waterStart = totals.opaqueCount + totals.TotalTransparentLikeCount;

        meshData.SetSubMesh(0, new SubMeshDescriptor(0, totals.opaqueCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        meshData.SetSubMesh(1, new SubMeshDescriptor(transparentStart, totals.TotalTransparentLikeCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        meshData.SetSubMesh(2, new SubMeshDescriptor(waterStart, totals.waterCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
    }

    private void SetActiveState(bool active)
    {
        if (gameObject.activeSelf != active)
            gameObject.SetActive(active);
    }

    private void SetRendererEnabled(bool enabled)
    {
        if (meshRenderer != null && meshRenderer.enabled != enabled)
            meshRenderer.enabled = enabled;
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
