using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    private static readonly int CompactOpaqueFacesPropertyId = Shader.PropertyToID("_CompactOpaqueFaces");
    private static readonly int UseVertexPullingPropertyId = Shader.PropertyToID("_UseVertexPulling");
    private static readonly int UseCompactOpaqueFacesPropertyId = Shader.PropertyToID("_UseCompactOpaqueFaces");
    private static readonly int UseIndirectVertexPullingPropertyId = Shader.PropertyToID("_UseIndirectVertexPulling");
    private static readonly int PulledOpaqueFaceBaseIndexPropertyId = Shader.PropertyToID("_PulledOpaqueFaceBaseIndex");
    private static readonly int CompactChunkWorldOriginPropertyId = Shader.PropertyToID("_CompactChunkWorldOrigin");
    private static readonly int GrassTintPropertyId = Shader.PropertyToID("_GrassTint");
    private static readonly int OpaqueFaceSectionsComputePropertyId = Shader.PropertyToID("_OpaqueFaceSections");
    private static readonly int IndirectDrawArgsComputePropertyId = Shader.PropertyToID("_IndirectDrawArgs");
    private static readonly int SectionCountComputePropertyId = Shader.PropertyToID("_SectionCount");
    private static readonly int VisibleSectionMaskComputePropertyId = Shader.PropertyToID("_VisibleSectionMask");
    private static readonly int CompactOpaqueFaceStride = Marshal.SizeOf<MeshGenerator.CompactOpaqueFace>();
    private static readonly int OpaqueFaceSectionStride = Marshal.SizeOf<OpaqueFaceSection>();
    private static readonly Plane[] SharedFrustumPlanes = new Plane[6];
    private static readonly List<ChunkRenderSlice> ManagedRenderSlices = new List<ChunkRenderSlice>(2048);
    private static readonly List<ChunkRenderSlice> OpaqueRenderSlices = new List<ChunkRenderSlice>(512);
    private const string OpaqueIndirectArgsBuildResourcePath = "OpaqueIndirectArgsBuild";
    private static bool enableIndirectOpaqueDrawSubmission = true;
    private static bool renderHookRegistered;
    private static ComputeShader opaqueIndirectArgsBuildComputeShader;
    private static int opaqueIndirectArgsBuildKernel = -1;
    private static bool opaqueIndirectArgsBuildShaderLoaded;
    private static bool opaqueIndirectArgsComputeUnavailable;
    private static OpaqueRenderTelemetrySnapshot latestOpaqueRenderTelemetry;
    private static int telemetryFrame = -1;
    private static int telemetrySlicesRendered;
    private static int telemetryDrawCalls;
    private static int telemetryDirectPathDrawCalls;
    private static int telemetrySingleDrawSlices;
    private static int telemetryRunBatchedSlices;
    private static int telemetryIndirectSlices;
    private static int telemetryVisibleOpaqueSubchunks;
    private static int telemetrySavedDrawCalls;
    private static int telemetryIndirectSavedDrawCalls;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpaqueFaceSection
    {
        public uint faceStart;
        public uint faceCount;
    }

    private static class SharedOpaqueFacePool
    {
        private const int AllocationAlignment = 64;

        private struct Range
        {
            public int start;
            public int length;

            public Range(int start, int length)
            {
                this.start = start;
                this.length = length;
            }
        }

        private static readonly List<Range> FreeRanges = new List<Range>(128);
        private static GraphicsBuffer buffer;
        private static int capacity;
        private static int usedLength;
        private static int version;

        public static GraphicsBuffer Buffer => buffer;
        public static int Version => version;

        public static int Allocate(int requiredCount, out int allocatedLength)
        {
            allocatedLength = 0;
            if (requiredCount <= 0)
                return -1;

            int requestedLength = RoundUp(requiredCount, AllocationAlignment);
            for (int i = 0; i < FreeRanges.Count; i++)
            {
                Range range = FreeRanges[i];
                if (range.length < requestedLength)
                    continue;

                int allocatedStart = range.start;
                allocatedLength = requestedLength;
                if (range.length == requestedLength)
                {
                    FreeRanges.RemoveAt(i);
                }
                else
                {
                    FreeRanges[i] = new Range(range.start + requestedLength, range.length - requestedLength);
                }

                return allocatedStart;
            }

            EnsureCapacityFor(requestedLength);
            int start = usedLength;
            usedLength += requestedLength;
            allocatedLength = requestedLength;
            return start;
        }

        public static void Release(int start, int length)
        {
            if (start < 0 || length <= 0)
                return;

            Range releasedRange = new Range(start, length);
            int insertIndex = 0;
            while (insertIndex < FreeRanges.Count && FreeRanges[insertIndex].start < releasedRange.start)
                insertIndex++;

            FreeRanges.Insert(insertIndex, releasedRange);
            MergeNeighbors(insertIndex);
            TrimFreeTail();
        }

        public static void Reset()
        {
            if (buffer != null)
            {
                buffer.Dispose();
                buffer = null;
            }

            capacity = 0;
            usedLength = 0;
            version = 0;
            FreeRanges.Clear();
        }

        private static void EnsureCapacityFor(int additionalLength)
        {
            int requiredCapacity = usedLength + additionalLength;
            if (buffer != null && capacity >= requiredCapacity)
                return;

            int newCapacity = Mathf.Max(1024, capacity);
            while (newCapacity < requiredCapacity)
                newCapacity = Mathf.Max(newCapacity * 2, requiredCapacity);

            GraphicsBuffer newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity, CompactOpaqueFaceStride);
            if (buffer != null)
            {
                int copyCount = Mathf.Min(usedLength, capacity);
                if (copyCount > 0)
                {
                    // Unity only allows CopyBuffer between equal-sized buffers, so on growth we
                    // preserve the active range through a one-time CPU copy.
                    MeshGenerator.CompactOpaqueFace[] existingFaces = new MeshGenerator.CompactOpaqueFace[copyCount];
                    buffer.GetData(existingFaces, 0, 0, copyCount);
                    newBuffer.SetData(existingFaces, 0, 0, copyCount);
                }

                buffer.Dispose();
            }

            buffer = newBuffer;
            capacity = newCapacity;
            version++;
        }

        private static void MergeNeighbors(int index)
        {
            if (index < 0 || index >= FreeRanges.Count)
                return;

            int currentIndex = index;
            if (currentIndex > 0)
            {
                Range previous = FreeRanges[currentIndex - 1];
                Range current = FreeRanges[currentIndex];
                if (previous.start + previous.length == current.start)
                {
                    FreeRanges[currentIndex - 1] = new Range(previous.start, previous.length + current.length);
                    FreeRanges.RemoveAt(currentIndex);
                    currentIndex--;
                }
            }

            if (currentIndex < FreeRanges.Count - 1)
            {
                Range current = FreeRanges[currentIndex];
                Range next = FreeRanges[currentIndex + 1];
                if (current.start + current.length == next.start)
                {
                    FreeRanges[currentIndex] = new Range(current.start, current.length + next.length);
                    FreeRanges.RemoveAt(currentIndex + 1);
                }
            }
        }

        private static void TrimFreeTail()
        {
            while (FreeRanges.Count > 0)
            {
                Range last = FreeRanges[FreeRanges.Count - 1];
                if (last.start + last.length != usedLength)
                    break;

                usedLength = last.start;
                FreeRanges.RemoveAt(FreeRanges.Count - 1);
            }
        }

        private static int RoundUp(int value, int multiple)
        {
            if (multiple <= 1)
                return Mathf.Max(0, value);

            int remainder = value % multiple;
            return remainder == 0 ? value : value + multiple - remainder;
        }
    }

    private MeshFilter meshFilter;
    [HideInInspector] public MeshRenderer meshRenderer;
    private Mesh mesh;
    private GraphicsBuffer indirectOpaqueArgsBuffer;
    private GraphicsBuffer opaqueFaceSectionBuffer;
    private GraphicsBuffer.IndirectDrawArgs[] indirectOpaqueArgsData;
    private OpaqueFaceSection[] opaqueFaceSectionsData;
    private MaterialPropertyBlock pulledOpaquePropertyBlock;
    private Material opaqueMaterial;
    private Bounds localRenderBounds;
    private Color grassTint = Color.white;
    private int pulledOpaqueFaceCount;
    private int indirectOpaqueArgsCapacity;
    private int opaqueFaceSectionCapacity;
    private int pulledOpaqueFaceGlobalStart = -1;
    private int pulledOpaqueFaceAllocationLength;
    private int pulledOpaqueFaceBufferVersion = -1;
    private int startSubchunkIndex;
    private int subchunkCount;
    private int meshGeometrySubchunkMask;
    private int logicalVisibleOpaqueSubchunkMask;
    private int visibleOpaqueSubchunkMask;
    private int opaqueGeometrySubchunkMask;
    private int[] pulledOpaqueFaceStartsByLocalSubchunk;
    private int[] pulledOpaqueFaceCountsByLocalSubchunk;
    private int cachedIndirectOpaqueCommandCount;
    private bool hasGeometry;
    private bool hasMeshGeometry;
    private bool hasOpaqueGeometry;
    private ChunkOpaqueRenderBackendKind activeOpaqueRenderBackendKind;
    private bool logicalShouldShow;
    private bool logicalShouldShowMeshGeometry;
    private bool isFrustumVisible = true;
    private bool shouldRenderOpaqueGeometry;
    private bool indirectOpaqueArgsDirty = true;
    private bool registeredForManagedRendering;
    private bool registeredForOpaqueRendering;

    private readonly struct SliceMeshTotals
    {
        public readonly int vertexCount;
        public readonly int opaqueCount;
        public readonly int opaqueFaceCount;
        public readonly int transparentCount;
        public readonly int billboardCount;
        public readonly int waterCount;

        public SliceMeshTotals(
            int vertexCount,
            int opaqueCount,
            int opaqueFaceCount,
            int transparentCount,
            int billboardCount,
            int waterCount)
        {
            this.vertexCount = vertexCount;
            this.opaqueCount = opaqueCount;
            this.opaqueFaceCount = opaqueFaceCount;
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

    public readonly struct OpaqueRenderTelemetrySnapshot
    {
        public readonly int frame;
        public readonly int slicesRendered;
        public readonly int drawCalls;
        public readonly int directPathDrawCalls;
        public readonly int singleDrawSlices;
        public readonly int runBatchedSlices;
        public readonly int indirectSlices;
        public readonly int visibleOpaqueSubchunks;
        public readonly int savedDrawCalls;
        public readonly int indirectSavedDrawCalls;

        public OpaqueRenderTelemetrySnapshot(
            int frame,
            int slicesRendered,
            int drawCalls,
            int directPathDrawCalls,
            int singleDrawSlices,
            int runBatchedSlices,
            int visibleOpaqueSubchunks,
            int savedDrawCalls,
            int indirectSlices,
            int indirectSavedDrawCalls)
        {
            this.frame = frame;
            this.slicesRendered = slicesRendered;
            this.drawCalls = drawCalls;
            this.directPathDrawCalls = directPathDrawCalls;
            this.singleDrawSlices = singleDrawSlices;
            this.runBatchedSlices = runBatchedSlices;
            this.indirectSlices = indirectSlices;
            this.visibleOpaqueSubchunks = visibleOpaqueSubchunks;
            this.savedDrawCalls = savedDrawCalls;
            this.indirectSavedDrawCalls = indirectSavedDrawCalls;
        }

        public bool IsValid => frame >= 0;
    }

    public static OpaqueRenderTelemetrySnapshot GetLatestOpaqueRenderTelemetry()
    {
        return latestOpaqueRenderTelemetry;
    }

    public static bool SupportsOpaqueVertexPulling()
    {
        return ChunkOpaqueRenderSupport.SupportsVertexPulling();
    }

    public static bool SupportsIndirectOpaqueVertexPulling()
    {
        return ChunkOpaqueRenderSupport.SupportsIndirectVertexPulling();
    }

    public static bool IsIndirectOpaqueDrawSubmissionEnabled()
    {
        return enableIndirectOpaqueDrawSubmission;
    }

    public static void SetIndirectOpaqueDrawSubmissionEnabled(bool enabled)
    {
        enableIndirectOpaqueDrawSubmission = enabled;
    }

    public void Initialize(Material[] materials, int sliceIndex, int sliceStartSubchunkIndex, int sliceSubchunkCount)
    {
        EnsureRenderHookRegistered();

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
        EnsurePulledOpaqueSubchunkMetadataCapacity();
        EnsureIndirectOpaqueArgsCapacity(subchunkCount);
        localRenderBounds = CreateMeshBounds(
            startSubchunkIndex * Chunk.SubchunkHeight,
            Mathf.Min(EndSubchunkIndexExclusive * Chunk.SubchunkHeight, Chunk.SizeY));

        ConfigureOpaqueMaterial(sharedMaterials);

        gameObject.name = $"ChunkSlice_{sliceIndex}";
        transform.localPosition = Vector3.zero;
        isFrustumVisible = true;
    }

    public void SetGrassTint(Color tint)
    {
        if (grassTint == tint)
            return;

        grassTint = tint;
        UpdatePulledOpaquePropertyBlock();
    }

    public void ApplyMeshData(
        NativeList<MeshGenerator.PackedChunkVertex> vertices,
        NativeList<int> opaqueTris,
        NativeList<MeshGenerator.CompactOpaqueFace> compactOpaqueFaces,
        NativeList<int> transparentTris,
        NativeList<int> billboardTris,
        NativeList<int> waterTris,
        NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
        ChunkOpaqueRenderBackendKind opaqueRenderBackendKind,
        Subchunk[] logicalSubchunks)
    {
        activeOpaqueRenderBackendKind = opaqueRenderBackendKind;
        SliceMeshTotals totals = CalculateMeshTotals(subchunkRanges);
        UpdateMeshGeometrySubchunkMask(subchunkRanges);
        bool usePulledOpaqueBackend =
            activeOpaqueRenderBackendKind == ChunkOpaqueRenderBackendKind.PulledOpaque &&
            SupportsOpaqueVertexPulling() &&
            opaqueMaterial != null;

        if (usePulledOpaqueBackend && totals.opaqueFaceCount > 0)
            UploadCompactOpaqueFaces(compactOpaqueFaces, subchunkRanges, totals.opaqueFaceCount);
        else
            ClearPulledOpaqueFaces();

        hasOpaqueGeometry = usePulledOpaqueBackend && pulledOpaqueFaceCount > 0;

        if (totals.vertexCount > 0)
        {
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

            mesh.bounds = localRenderBounds;
            hasMeshGeometry = true;
        }
        else
        {
            if (mesh != null)
                mesh.Clear();

            hasMeshGeometry = false;
        }

        hasGeometry = hasMeshGeometry || hasOpaqueGeometry;
        if (!hasGeometry)
        {
            logicalShouldShow = false;
            logicalShouldShowMeshGeometry = false;
            logicalVisibleOpaqueSubchunkMask = 0;
            SetRendererEnabled(false);
            shouldRenderOpaqueGeometry = false;
            SetActiveState(false);
            return;
        }

        SetActiveState(true);
        RefreshVisibility(logicalSubchunks);
    }

    public void RefreshVisibility(Subchunk[] logicalSubchunks)
    {
        logicalVisibleOpaqueSubchunkMask = hasOpaqueGeometry ? GetVisibleOpaqueSubchunkMask(logicalSubchunks) : 0;
        logicalShouldShowMeshGeometry = hasMeshGeometry && HasAnyVisibleMeshGeometry(logicalSubchunks);
        logicalShouldShow = logicalShouldShowMeshGeometry || logicalVisibleOpaqueSubchunkMask != 0;

        if (!hasGeometry)
        {
            logicalShouldShow = false;
            logicalShouldShowMeshGeometry = false;
            logicalVisibleOpaqueSubchunkMask = 0;
            SetRendererEnabled(false);
            shouldRenderOpaqueGeometry = false;
            visibleOpaqueSubchunkMask = 0;
            SetActiveState(false);
            return;
        }

        SetActiveState(true);
        ApplyRenderVisibilityState();
    }

    public void UpdateFrustumVisibility(Plane[] frustumPlanes)
    {
        if (!hasGeometry)
            return;

        bool visible = GeometryUtility.TestPlanesAABB(frustumPlanes, GetWorldBounds(localRenderBounds));
        if (visible == isFrustumVisible)
            return;

        isFrustumVisible = visible;
        ApplyRenderVisibilityState();
    }

    public void ClearMesh()
    {
        if (mesh != null)
            mesh.Clear();

        ClearPulledOpaqueFaces();

        hasGeometry = false;
        hasMeshGeometry = false;
        hasOpaqueGeometry = false;
        activeOpaqueRenderBackendKind = ChunkOpaqueRenderBackendKind.ClassicMesh;
        logicalShouldShow = false;
        logicalShouldShowMeshGeometry = false;
        logicalVisibleOpaqueSubchunkMask = 0;
        isFrustumVisible = true;
        shouldRenderOpaqueGeometry = false;
        visibleOpaqueSubchunkMask = 0;
        meshGeometrySubchunkMask = 0;
        UpdateManagedRenderRegistration();
        UpdateOpaqueRenderRegistration();
        SetRendererEnabled(false);
        SetActiveState(false);
    }

    private void OnDestroy()
    {
        UnregisterManagedRenderSlice();
        UnregisterOpaqueRenderSlice();
        ReleaseSharedOpaqueFaceAllocation();
        ReleaseOpaqueFaceSectionBuffer();
        ReleaseIndirectOpaqueArgsBuffer();
    }

    private void OnEnable()
    {
        RegisterManagedRenderSlice();
    }

    private void OnDisable()
    {
        UnregisterManagedRenderSlice();
        UnregisterOpaqueRenderSlice();
    }

    private SliceMeshTotals CalculateMeshTotals(NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges)
    {
        int totalVertexCount = 0;
        int totalOpaqueCount = 0;
        int totalOpaqueFaceCount = 0;
        int totalTransparentCount = 0;
        int totalBillboardCount = 0;
        int totalWaterCount = 0;

        for (int sub = startSubchunkIndex; sub < EndSubchunkIndexExclusive; sub++)
        {
            MeshGenerator.SubchunkMeshRange range = subchunkRanges[sub];
            totalVertexCount += range.vertexCount;
            totalOpaqueCount += range.opaqueCount;
            totalOpaqueFaceCount += range.opaqueFaceCount;
            totalTransparentCount += range.transparentCount;
            totalBillboardCount += range.billboardCount;
            totalWaterCount += range.waterCount;
        }

        return new SliceMeshTotals(
            totalVertexCount,
            totalOpaqueCount,
            totalOpaqueFaceCount,
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

    private void UploadCompactOpaqueFaces(
        NativeList<MeshGenerator.CompactOpaqueFace> compactOpaqueFaces,
        NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
        int totalOpaqueFaceCount)
    {
        if (!compactOpaqueFaces.IsCreated || totalOpaqueFaceCount <= 0)
        {
            ClearPulledOpaqueFaces();
            return;
        }

        EnsureSharedOpaqueFaceAllocation(totalOpaqueFaceCount);
        EnsurePulledOpaqueSubchunkMetadataCapacity();
        EnsureIndirectOpaqueArgsCapacity(subchunkCount);
        EnsureOpaqueFaceSectionBufferCapacity(subchunkCount);
        Array.Clear(pulledOpaqueFaceStartsByLocalSubchunk, 0, pulledOpaqueFaceStartsByLocalSubchunk.Length);
        Array.Clear(pulledOpaqueFaceCountsByLocalSubchunk, 0, pulledOpaqueFaceCountsByLocalSubchunk.Length);
        opaqueGeometrySubchunkMask = 0;
        int sliceOpaqueFaceSourceStart = subchunkRanges[startSubchunkIndex].opaqueFaceStart;

        GraphicsBuffer sharedOpaqueFaceBuffer = GetCurrentPulledOpaqueFaceBuffer();
        if (sharedOpaqueFaceBuffer == null)
        {
            ClearPulledOpaqueFaces();
            return;
        }

        for (int sub = startSubchunkIndex; sub < EndSubchunkIndexExclusive; sub++)
        {
            MeshGenerator.SubchunkMeshRange range = subchunkRanges[sub];
            int localSubchunkIndex = sub - startSubchunkIndex;
            int localOpaqueFaceStart = range.opaqueFaceStart - sliceOpaqueFaceSourceStart;
            pulledOpaqueFaceStartsByLocalSubchunk[localSubchunkIndex] = localOpaqueFaceStart;
            pulledOpaqueFaceCountsByLocalSubchunk[localSubchunkIndex] = range.opaqueFaceCount;
            opaqueFaceSectionsData[localSubchunkIndex] = new OpaqueFaceSection
            {
                faceStart = (uint)Mathf.Max(0, pulledOpaqueFaceGlobalStart + localOpaqueFaceStart),
                faceCount = (uint)Mathf.Max(0, range.opaqueFaceCount)
            };

            if (range.opaqueFaceCount <= 0)
                continue;

            opaqueGeometrySubchunkMask |= 1 << sub;
        }

        sharedOpaqueFaceBuffer.SetData(compactOpaqueFaces.AsArray(), sliceOpaqueFaceSourceStart, pulledOpaqueFaceGlobalStart, totalOpaqueFaceCount);
        if (opaqueFaceSectionBuffer != null && opaqueFaceSectionsData != null)
            opaqueFaceSectionBuffer.SetData(opaqueFaceSectionsData, 0, 0, subchunkCount);

        pulledOpaqueFaceCount = totalOpaqueFaceCount;
        MarkIndirectOpaqueArgsDirty();
        UpdatePulledOpaquePropertyBlock();
        UpdateOpaqueRenderRegistration();
    }

    private void ClearPulledOpaqueFaces()
    {
        ReleaseSharedOpaqueFaceAllocation();
        pulledOpaqueFaceCount = 0;
        visibleOpaqueSubchunkMask = 0;
        opaqueGeometrySubchunkMask = 0;
        if (pulledOpaqueFaceStartsByLocalSubchunk != null)
            Array.Clear(pulledOpaqueFaceStartsByLocalSubchunk, 0, pulledOpaqueFaceStartsByLocalSubchunk.Length);
        if (pulledOpaqueFaceCountsByLocalSubchunk != null)
            Array.Clear(pulledOpaqueFaceCountsByLocalSubchunk, 0, pulledOpaqueFaceCountsByLocalSubchunk.Length);
        if (opaqueFaceSectionsData != null)
            Array.Clear(opaqueFaceSectionsData, 0, opaqueFaceSectionsData.Length);
        cachedIndirectOpaqueCommandCount = 0;
        pulledOpaqueFaceBufferVersion = -1;
        MarkIndirectOpaqueArgsDirty();
        UpdatePulledOpaquePropertyBlock();
        UpdateOpaqueRenderRegistration();
    }

    private void EnsureSharedOpaqueFaceAllocation(int requiredCount)
    {
        if (requiredCount <= 0)
            return;

        if (pulledOpaqueFaceGlobalStart >= 0 && pulledOpaqueFaceAllocationLength >= requiredCount)
            return;

        ReleaseSharedOpaqueFaceAllocation();
        pulledOpaqueFaceGlobalStart = SharedOpaqueFacePool.Allocate(requiredCount, out pulledOpaqueFaceAllocationLength);
        pulledOpaqueFaceBufferVersion = -1;
    }

    private void ReleaseSharedOpaqueFaceAllocation()
    {
        if (pulledOpaqueFaceGlobalStart >= 0 && pulledOpaqueFaceAllocationLength > 0)
            SharedOpaqueFacePool.Release(pulledOpaqueFaceGlobalStart, pulledOpaqueFaceAllocationLength);

        pulledOpaqueFaceGlobalStart = -1;
        pulledOpaqueFaceAllocationLength = 0;
        pulledOpaqueFaceBufferVersion = -1;
    }

    private void EnsureIndirectOpaqueArgsCapacity(int requiredCount)
    {
        if (!enableIndirectOpaqueDrawSubmission || !SupportsIndirectOpaqueVertexPulling())
            return;

        int safeRequiredCount = Mathf.Max(1, requiredCount);
        if (indirectOpaqueArgsBuffer != null && indirectOpaqueArgsCapacity >= safeRequiredCount && indirectOpaqueArgsData != null && indirectOpaqueArgsData.Length >= safeRequiredCount)
            return;

        ReleaseIndirectOpaqueArgsBuffer();

        indirectOpaqueArgsCapacity = safeRequiredCount;
        indirectOpaqueArgsBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments,
            indirectOpaqueArgsCapacity,
            GraphicsBuffer.IndirectDrawArgs.size);

        indirectOpaqueArgsData = new GraphicsBuffer.IndirectDrawArgs[indirectOpaqueArgsCapacity];
        MarkIndirectOpaqueArgsDirty();
    }

    private void ReleaseIndirectOpaqueArgsBuffer()
    {
        if (indirectOpaqueArgsBuffer != null)
        {
            indirectOpaqueArgsBuffer.Dispose();
            indirectOpaqueArgsBuffer = null;
        }

        indirectOpaqueArgsData = null;
        indirectOpaqueArgsCapacity = 0;
        cachedIndirectOpaqueCommandCount = 0;
        indirectOpaqueArgsDirty = true;
    }

    private void EnsureOpaqueFaceSectionBufferCapacity(int requiredCount)
    {
        if (requiredCount <= 0)
            return;

        if (opaqueFaceSectionBuffer != null &&
            opaqueFaceSectionCapacity >= requiredCount &&
            opaqueFaceSectionsData != null &&
            opaqueFaceSectionsData.Length >= requiredCount)
        {
            return;
        }

        ReleaseOpaqueFaceSectionBuffer();
        opaqueFaceSectionCapacity = requiredCount;
        opaqueFaceSectionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, opaqueFaceSectionCapacity, OpaqueFaceSectionStride);
        opaqueFaceSectionsData = new OpaqueFaceSection[opaqueFaceSectionCapacity];
        MarkIndirectOpaqueArgsDirty();
    }

    private void ReleaseOpaqueFaceSectionBuffer()
    {
        if (opaqueFaceSectionBuffer != null)
        {
            opaqueFaceSectionBuffer.Dispose();
            opaqueFaceSectionBuffer = null;
        }

        opaqueFaceSectionsData = null;
        opaqueFaceSectionCapacity = 0;
        MarkIndirectOpaqueArgsDirty();
    }

    private void EnsurePulledOpaqueSubchunkMetadataCapacity()
    {
        if (subchunkCount <= 0)
            return;

        if (pulledOpaqueFaceStartsByLocalSubchunk == null || pulledOpaqueFaceStartsByLocalSubchunk.Length != subchunkCount)
            pulledOpaqueFaceStartsByLocalSubchunk = new int[subchunkCount];

        if (pulledOpaqueFaceCountsByLocalSubchunk == null || pulledOpaqueFaceCountsByLocalSubchunk.Length != subchunkCount)
            pulledOpaqueFaceCountsByLocalSubchunk = new int[subchunkCount];
    }

    private void UpdatePulledOpaquePropertyBlock()
    {
        if (!SupportsOpaqueVertexPulling() || opaqueMaterial == null)
            return;

        pulledOpaquePropertyBlock = pulledOpaquePropertyBlock ?? new MaterialPropertyBlock();
        pulledOpaquePropertyBlock.Clear();
        bool useVertexPulling =
            activeOpaqueRenderBackendKind == ChunkOpaqueRenderBackendKind.PulledOpaque &&
            pulledOpaqueFaceCount > 0;
        pulledOpaquePropertyBlock.SetFloat(UseVertexPullingPropertyId, 0f);
        pulledOpaquePropertyBlock.SetFloat(UseCompactOpaqueFacesPropertyId, useVertexPulling ? 1f : 0f);
        pulledOpaquePropertyBlock.SetFloat(UseIndirectVertexPullingPropertyId, 0f);
        pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, 0f);
        Vector3 chunkWorldOrigin = transform.position;
        pulledOpaquePropertyBlock.SetVector(CompactChunkWorldOriginPropertyId, new Vector4(chunkWorldOrigin.x, chunkWorldOrigin.y, chunkWorldOrigin.z, 0f));
        pulledOpaquePropertyBlock.SetColor(GrassTintPropertyId, grassTint);

        GraphicsBuffer currentOpaqueFaceBuffer = GetCurrentPulledOpaqueFaceBuffer();
        if (currentOpaqueFaceBuffer != null && pulledOpaqueFaceCount > 0)
        {
            pulledOpaquePropertyBlock.SetBuffer(CompactOpaqueFacesPropertyId, currentOpaqueFaceBuffer);
            pulledOpaqueFaceBufferVersion = SharedOpaqueFacePool.Version;
        }
        else
        {
            pulledOpaqueFaceBufferVersion = -1;
        }

        if (indirectOpaqueArgsBuffer != null)
            pulledOpaquePropertyBlock.SetBuffer(IndirectDrawArgsComputePropertyId, indirectOpaqueArgsBuffer);
    }

    private void ConfigureOpaqueMaterial(Material[] sharedMaterials)
    {
        opaqueMaterial = sharedMaterials != null && sharedMaterials.Length > 0
            ? sharedMaterials[0]
            : null;

        if (opaqueMaterial != null && SupportsOpaqueVertexPulling())
            opaqueMaterial.enableInstancing = true;

        UpdatePulledOpaquePropertyBlock();
        UpdateOpaqueRenderRegistration();
    }

    private void ApplyRenderVisibilityState()
    {
        int nextVisibleOpaqueSubchunkMask = isFrustumVisible ? logicalVisibleOpaqueSubchunkMask : 0;
        if (visibleOpaqueSubchunkMask != nextVisibleOpaqueSubchunkMask)
            MarkIndirectOpaqueArgsDirty();

        visibleOpaqueSubchunkMask = nextVisibleOpaqueSubchunkMask;
        SetRendererEnabled(hasMeshGeometry && logicalShouldShowMeshGeometry && isFrustumVisible);
        shouldRenderOpaqueGeometry =
            activeOpaqueRenderBackendKind == ChunkOpaqueRenderBackendKind.PulledOpaque &&
            hasOpaqueGeometry &&
            visibleOpaqueSubchunkMask != 0;
        UpdateManagedRenderRegistration();
        UpdateOpaqueRenderRegistration();
    }

    private void UpdateManagedRenderRegistration()
    {
        bool shouldRegister = isActiveAndEnabled && hasGeometry;

        if (shouldRegister)
            RegisterManagedRenderSlice();
        else
            UnregisterManagedRenderSlice();
    }

    private void UpdateOpaqueRenderRegistration()
    {
        bool shouldRegister =
            isActiveAndEnabled &&
            shouldRenderOpaqueGeometry &&
            hasOpaqueGeometry &&
            pulledOpaqueFaceCount > 0 &&
            GetCurrentPulledOpaqueFaceBuffer() != null &&
            opaqueMaterial != null &&
            SupportsOpaqueVertexPulling();

        if (shouldRegister)
            RegisterOpaqueRenderSlice();
        else
            UnregisterOpaqueRenderSlice();
    }

    private void RegisterManagedRenderSlice()
    {
        if (registeredForManagedRendering)
            return;

        EnsureRenderHookRegistered();
        ManagedRenderSlices.Add(this);
        registeredForManagedRendering = true;
    }

    private void UnregisterManagedRenderSlice()
    {
        if (!registeredForManagedRendering)
            return;

        ManagedRenderSlices.Remove(this);
        registeredForManagedRendering = false;
    }

    private void RegisterOpaqueRenderSlice()
    {
        if (registeredForOpaqueRendering)
            return;

        EnsureRenderHookRegistered();
        OpaqueRenderSlices.Add(this);
        registeredForOpaqueRendering = true;
    }

    private void UnregisterOpaqueRenderSlice()
    {
        if (!registeredForOpaqueRendering)
            return;

        OpaqueRenderSlices.Remove(this);
        registeredForOpaqueRendering = false;
    }

    private static void EnsureRenderHookRegistered()
    {
        if (renderHookRegistered)
            return;

        RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        renderHookRegistered = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        if (renderHookRegistered)
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;

        renderHookRegistered = false;
        ManagedRenderSlices.Clear();
        OpaqueRenderSlices.Clear();
        SharedOpaqueFacePool.Reset();
        opaqueIndirectArgsBuildComputeShader = null;
        opaqueIndirectArgsBuildKernel = -1;
        opaqueIndirectArgsBuildShaderLoaded = false;
        opaqueIndirectArgsComputeUnavailable = false;
        latestOpaqueRenderTelemetry = default;
        telemetryFrame = -1;
        telemetrySlicesRendered = 0;
        telemetryDrawCalls = 0;
        telemetryDirectPathDrawCalls = 0;
        telemetrySingleDrawSlices = 0;
        telemetryRunBatchedSlices = 0;
        telemetryIndirectSlices = 0;
        telemetryVisibleOpaqueSubchunks = 0;
        telemetrySavedDrawCalls = 0;
        telemetryIndirectSavedDrawCalls = 0;
        enableIndirectOpaqueDrawSubmission = true;
    }

    private static void HandleBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera == null || (ManagedRenderSlices.Count == 0 && OpaqueRenderSlices.Count == 0))
            return;

        if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
            return;

        GeometryUtility.CalculateFrustumPlanes(camera, SharedFrustumPlanes);

        for (int i = 0; i < ManagedRenderSlices.Count; i++)
        {
            ChunkRenderSlice slice = ManagedRenderSlices[i];
            if (slice == null)
                continue;

            slice.UpdateFrustumVisibility(SharedFrustumPlanes);
        }

        for (int i = 0; i < OpaqueRenderSlices.Count; i++)
        {
            ChunkRenderSlice slice = OpaqueRenderSlices[i];
            if (slice == null)
                continue;

            slice.RenderOpaqueGeometry(camera);
        }
    }

    private void RenderOpaqueGeometry(Camera camera)
    {
        GraphicsBuffer currentOpaqueFaceBuffer = GetCurrentPulledOpaqueFaceBuffer();
        if (!shouldRenderOpaqueGeometry ||
            !isFrustumVisible ||
            !hasOpaqueGeometry ||
            pulledOpaqueFaceCount <= 0 ||
            currentOpaqueFaceBuffer == null ||
            opaqueMaterial == null)
        {
            return;
        }

        EnsureCurrentPulledOpaqueFaceBufferBinding(currentOpaqueFaceBuffer);
        Bounds worldBounds = GetWorldBounds(localRenderBounds);
        if (pulledOpaquePropertyBlock != null)
        {
            Vector3 chunkWorldOrigin = transform.position;
            pulledOpaquePropertyBlock.SetVector(CompactChunkWorldOriginPropertyId, new Vector4(chunkWorldOrigin.x, chunkWorldOrigin.y, chunkWorldOrigin.z, 0f));
        }

        RenderParams renderParams = new RenderParams(opaqueMaterial)
        {
            camera = camera,
            worldBounds = worldBounds,
            layer = gameObject.layer,
            matProps = pulledOpaquePropertyBlock,
            receiveShadows = meshRenderer == null || meshRenderer.receiveShadows,
            shadowCastingMode = meshRenderer != null ? meshRenderer.shadowCastingMode : ShadowCastingMode.On,
            renderingLayerMask = meshRenderer != null ? meshRenderer.renderingLayerMask : uint.MaxValue
        };

        bool trackTelemetry = ShouldTrackOpaqueTelemetry(camera);
        int visibleOpaqueSubchunkCount = trackTelemetry ? CountVisibleOpaqueSubchunksWithFaces() : 0;

        if (SupportsIndirectOpaqueVertexPulling() &&
            TryBuildIndirectOpaqueDrawCommands(out int indirectCommandCount) &&
            indirectCommandCount > 0)
        {
            if (pulledOpaquePropertyBlock != null && indirectOpaqueArgsBuffer != null)
                pulledOpaquePropertyBlock.SetBuffer(IndirectDrawArgsComputePropertyId, indirectOpaqueArgsBuffer);

            pulledOpaquePropertyBlock.SetFloat(UseIndirectVertexPullingPropertyId, 1f);
            pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, 0f);
            Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, indirectOpaqueArgsBuffer, indirectCommandCount);
            if (trackTelemetry)
                RecordOpaqueTelemetry(
                    visibleOpaqueSubchunkCount,
                    indirectCommandCount,
                    1,
                    indirectCommandCount == 1,
                    indirectCommandCount > 1,
                    true);
            return;
        }

        pulledOpaquePropertyBlock.SetFloat(UseIndirectVertexPullingPropertyId, 0f);
        if (subchunkCount <= 1 || CanRenderOpaqueSliceAsSingleDraw())
        {
            pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, pulledOpaqueFaceGlobalStart);
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, pulledOpaqueFaceCount);
            if (trackTelemetry)
                RecordOpaqueTelemetry(visibleOpaqueSubchunkCount, 1, 1, true, false, false);
            return;
        }

        int runCount = 0;
        for (int localSubchunkIndex = 0; localSubchunkIndex < subchunkCount;)
        {
            int subchunkBit = 1 << (startSubchunkIndex + localSubchunkIndex);
            int subchunkFaceCount = GetLocalOpaqueFaceCount(localSubchunkIndex);
            if ((visibleOpaqueSubchunkMask & subchunkBit) == 0 || subchunkFaceCount <= 0)
            {
                localSubchunkIndex++;
                continue;
            }

            int runFaceStart = GetLocalOpaqueFaceStart(localSubchunkIndex);
            int runFaceCount = subchunkFaceCount;
            int nextLocalSubchunkIndex = localSubchunkIndex + 1;

            while (nextLocalSubchunkIndex < subchunkCount)
            {
                int nextFaceCount = GetLocalOpaqueFaceCount(nextLocalSubchunkIndex);
                if (nextFaceCount <= 0)
                {
                    nextLocalSubchunkIndex++;
                    continue;
                }

                int nextSubchunkBit = 1 << (startSubchunkIndex + nextLocalSubchunkIndex);
                if ((visibleOpaqueSubchunkMask & nextSubchunkBit) == 0)
                    break;

                runFaceCount += nextFaceCount;
                nextLocalSubchunkIndex++;
            }

            pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, pulledOpaqueFaceGlobalStart + runFaceStart);
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, runFaceCount);
            runCount++;
            localSubchunkIndex = nextLocalSubchunkIndex;
        }

        if (trackTelemetry)
            RecordOpaqueTelemetry(visibleOpaqueSubchunkCount, runCount, runCount, false, runCount > 0, false);
    }

    private bool TryBuildIndirectOpaqueDrawCommands(out int commandCount)
    {
        commandCount = 0;

        if (!enableIndirectOpaqueDrawSubmission ||
            !SupportsIndirectOpaqueVertexPulling() ||
            pulledOpaqueFaceCount <= 0)
        {
            cachedIndirectOpaqueCommandCount = 0;
            return false;
        }

        if (!indirectOpaqueArgsDirty)
        {
            commandCount = cachedIndirectOpaqueCommandCount;
            return commandCount > 0;
        }

        EnsureIndirectOpaqueArgsCapacity(subchunkCount);
        if (indirectOpaqueArgsBuffer == null ||
            opaqueFaceSectionBuffer == null ||
            pulledOpaqueFaceCount <= 0)
        {
            cachedIndirectOpaqueCommandCount = 0;
            return false;
        }

        uint visibleLocalMask = GetVisibleLocalOpaqueSectionMask();
        int visibleRunCount = CountVisibleOpaqueRuns(visibleLocalMask);
        cachedIndirectOpaqueCommandCount = visibleRunCount;
        indirectOpaqueArgsDirty = false;

        if (visibleRunCount <= 0)
            return false;

        if (TryBuildIndirectOpaqueDrawCommandsGpu(visibleLocalMask))
        {
            commandCount = cachedIndirectOpaqueCommandCount;
            return true;
        }

        if (indirectOpaqueArgsData == null)
            return false;

        Array.Clear(indirectOpaqueArgsData, 0, indirectOpaqueArgsData.Length);
        for (int localSubchunkIndex = 0; localSubchunkIndex < subchunkCount;)
        {
            int subchunkFaceCount = GetLocalOpaqueFaceCount(localSubchunkIndex);
            if (((visibleLocalMask >> localSubchunkIndex) & 1u) == 0u || subchunkFaceCount <= 0)
            {
                localSubchunkIndex++;
                continue;
            }

            int runFaceStart = GetLocalOpaqueFaceStart(localSubchunkIndex);
            int runFaceCount = subchunkFaceCount;
            int nextLocalSubchunkIndex = localSubchunkIndex + 1;

            while (nextLocalSubchunkIndex < subchunkCount)
            {
                int nextFaceCount = GetLocalOpaqueFaceCount(nextLocalSubchunkIndex);
                if (nextFaceCount <= 0)
                {
                    nextLocalSubchunkIndex++;
                    continue;
                }

                if (((visibleLocalMask >> nextLocalSubchunkIndex) & 1u) == 0u)
                    break;

                runFaceCount += nextFaceCount;
                nextLocalSubchunkIndex++;
            }

            if (commandCount >= indirectOpaqueArgsData.Length)
                return false;

            indirectOpaqueArgsData[commandCount++] = new GraphicsBuffer.IndirectDrawArgs
            {
                vertexCountPerInstance = 6u,
                instanceCount = (uint)runFaceCount,
                startVertex = 0u,
                startInstance = (uint)(pulledOpaqueFaceGlobalStart + runFaceStart)
            };

            localSubchunkIndex = nextLocalSubchunkIndex;
        }

        if (commandCount <= 0)
            return false;

        indirectOpaqueArgsBuffer.SetData(indirectOpaqueArgsData, 0, 0, commandCount);
        return true;
    }

    private bool TryBuildIndirectOpaqueDrawCommandsGpu(uint visibleLocalMask)
    {
        // Keep the indirect submission itself enabled, but build the command buffer on CPU.
        // The GPU args builder is currently causing opaque slices to disappear in runtime.
        return false;
    }

    private bool CanRenderOpaqueSliceAsSingleDraw()
    {
        return opaqueGeometrySubchunkMask != 0 &&
               visibleOpaqueSubchunkMask == opaqueGeometrySubchunkMask;
    }

    private GraphicsBuffer GetCurrentPulledOpaqueFaceBuffer()
    {
        return pulledOpaqueFaceGlobalStart >= 0 ? SharedOpaqueFacePool.Buffer : null;
    }

    private void EnsureCurrentPulledOpaqueFaceBufferBinding(GraphicsBuffer currentOpaqueFaceBuffer)
    {
        if (pulledOpaquePropertyBlock == null || currentOpaqueFaceBuffer == null || pulledOpaqueFaceCount <= 0)
            return;

        int currentPoolVersion = SharedOpaqueFacePool.Version;
        if (pulledOpaqueFaceBufferVersion == currentPoolVersion)
            return;

        pulledOpaquePropertyBlock.SetBuffer(CompactOpaqueFacesPropertyId, currentOpaqueFaceBuffer);
        pulledOpaqueFaceBufferVersion = currentPoolVersion;
    }

    private void MarkIndirectOpaqueArgsDirty()
    {
        indirectOpaqueArgsDirty = true;
        cachedIndirectOpaqueCommandCount = 0;
    }

    private uint GetVisibleLocalOpaqueSectionMask()
    {
        uint mask = 0u;
        int safeSectionCount = Mathf.Min(subchunkCount, 32);
        for (int localSubchunkIndex = 0; localSubchunkIndex < safeSectionCount; localSubchunkIndex++)
        {
            int absoluteSubchunkBit = 1 << (startSubchunkIndex + localSubchunkIndex);
            if ((visibleOpaqueSubchunkMask & absoluteSubchunkBit) == 0)
                continue;

            if (GetLocalOpaqueFaceCount(localSubchunkIndex) <= 0)
                continue;

            mask |= 1u << localSubchunkIndex;
        }

        return mask;
    }

    private int CountVisibleOpaqueRuns(uint visibleLocalMask)
    {
        int runCount = 0;
        for (int localSubchunkIndex = 0; localSubchunkIndex < subchunkCount;)
        {
            int subchunkFaceCount = GetLocalOpaqueFaceCount(localSubchunkIndex);
            if (((visibleLocalMask >> localSubchunkIndex) & 1u) == 0u || subchunkFaceCount <= 0)
            {
                localSubchunkIndex++;
                continue;
            }

            runCount++;
            int nextLocalSubchunkIndex = localSubchunkIndex + 1;
            while (nextLocalSubchunkIndex < subchunkCount)
            {
                int nextFaceCount = GetLocalOpaqueFaceCount(nextLocalSubchunkIndex);
                if (nextFaceCount <= 0)
                {
                    nextLocalSubchunkIndex++;
                    continue;
                }

                if (((visibleLocalMask >> nextLocalSubchunkIndex) & 1u) == 0u)
                    break;

                nextLocalSubchunkIndex++;
            }

            localSubchunkIndex = nextLocalSubchunkIndex;
        }

        return runCount;
    }

    private static bool TryGetOpaqueIndirectArgsBuildComputeShader(out ComputeShader computeShader, out int kernelIndex)
    {
        computeShader = null;
        kernelIndex = -1;

        if (opaqueIndirectArgsComputeUnavailable)
            return false;

        if (!opaqueIndirectArgsBuildShaderLoaded)
        {
            opaqueIndirectArgsBuildShaderLoaded = true;
            opaqueIndirectArgsBuildComputeShader = Resources.Load<ComputeShader>(OpaqueIndirectArgsBuildResourcePath);
            if (opaqueIndirectArgsBuildComputeShader != null)
            {
                try
                {
                    opaqueIndirectArgsBuildKernel = opaqueIndirectArgsBuildComputeShader.FindKernel("BuildIndirectOpaqueArgs");
                }
                catch (Exception)
                {
                    opaqueIndirectArgsBuildKernel = -1;
                }
            }
        }

        if (opaqueIndirectArgsBuildComputeShader == null || opaqueIndirectArgsBuildKernel < 0)
            return false;

        computeShader = opaqueIndirectArgsBuildComputeShader;
        kernelIndex = opaqueIndirectArgsBuildKernel;
        return true;
    }

    private int GetLocalOpaqueFaceStart(int localSubchunkIndex)
    {
        return pulledOpaqueFaceStartsByLocalSubchunk != null &&
               localSubchunkIndex >= 0 &&
               localSubchunkIndex < pulledOpaqueFaceStartsByLocalSubchunk.Length
            ? pulledOpaqueFaceStartsByLocalSubchunk[localSubchunkIndex]
            : 0;
    }

    private int GetLocalOpaqueFaceCount(int localSubchunkIndex)
    {
        return pulledOpaqueFaceCountsByLocalSubchunk != null &&
               localSubchunkIndex >= 0 &&
               localSubchunkIndex < pulledOpaqueFaceCountsByLocalSubchunk.Length
            ? pulledOpaqueFaceCountsByLocalSubchunk[localSubchunkIndex]
            : 0;
    }

    private int CountVisibleOpaqueSubchunksWithFaces()
    {
        int visibleCount = 0;
        for (int localSubchunkIndex = 0; localSubchunkIndex < subchunkCount; localSubchunkIndex++)
        {
            int subchunkBit = 1 << (startSubchunkIndex + localSubchunkIndex);
            if ((visibleOpaqueSubchunkMask & subchunkBit) == 0)
                continue;

            if (GetLocalOpaqueFaceCount(localSubchunkIndex) > 0)
                visibleCount++;
        }

        return visibleCount;
    }

    private static bool ShouldTrackOpaqueTelemetry(Camera camera)
    {
        if (camera == null)
            return false;

        Camera mainCamera = Camera.main;
        return mainCamera == null || camera == mainCamera;
    }

    private static void RecordOpaqueTelemetry(
        int visibleOpaqueSubchunkCount,
        int directPathDrawCalls,
        int actualDrawCalls,
        bool usedSingleDraw,
        bool usedRunBatching,
        bool usedIndirect)
    {
        int currentFrame = Time.frameCount;
        if (telemetryFrame != currentFrame)
        {
            telemetryFrame = currentFrame;
            telemetrySlicesRendered = 0;
            telemetryDrawCalls = 0;
            telemetryDirectPathDrawCalls = 0;
            telemetrySingleDrawSlices = 0;
            telemetryRunBatchedSlices = 0;
            telemetryIndirectSlices = 0;
            telemetryVisibleOpaqueSubchunks = 0;
            telemetrySavedDrawCalls = 0;
            telemetryIndirectSavedDrawCalls = 0;
        }

        telemetrySlicesRendered++;
        telemetryDrawCalls += actualDrawCalls;
        telemetryDirectPathDrawCalls += directPathDrawCalls;
        telemetryVisibleOpaqueSubchunks += visibleOpaqueSubchunkCount;
        telemetrySavedDrawCalls += Mathf.Max(0, visibleOpaqueSubchunkCount - actualDrawCalls);
        telemetryIndirectSavedDrawCalls += Mathf.Max(0, directPathDrawCalls - actualDrawCalls);

        if (usedSingleDraw)
            telemetrySingleDrawSlices++;
        else if (usedRunBatching)
            telemetryRunBatchedSlices++;

        if (usedIndirect)
            telemetryIndirectSlices++;

        latestOpaqueRenderTelemetry = new OpaqueRenderTelemetrySnapshot(
            telemetryFrame,
            telemetrySlicesRendered,
            telemetryDrawCalls,
            telemetryDirectPathDrawCalls,
            telemetrySingleDrawSlices,
            telemetryRunBatchedSlices,
            telemetryVisibleOpaqueSubchunks,
            telemetrySavedDrawCalls,
            telemetryIndirectSlices,
            telemetryIndirectSavedDrawCalls);
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

    private void UpdateMeshGeometrySubchunkMask(NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges)
    {
        meshGeometrySubchunkMask = 0;
        if (!subchunkRanges.IsCreated)
            return;

        int end = Mathf.Min(EndSubchunkIndexExclusive, subchunkRanges.Length);
        for (int sub = startSubchunkIndex; sub < end; sub++)
        {
            if (subchunkRanges[sub].vertexCount > 0)
                meshGeometrySubchunkMask |= 1 << sub;
        }
    }

    private bool HasAnyVisibleMeshGeometry(Subchunk[] logicalSubchunks)
    {
        if (!hasMeshGeometry || meshGeometrySubchunkMask == 0 || logicalSubchunks == null)
            return false;

        int end = Mathf.Min(EndSubchunkIndexExclusive, logicalSubchunks.Length);
        for (int sub = startSubchunkIndex; sub < end; sub++)
        {
            if ((meshGeometrySubchunkMask & (1 << sub)) == 0)
                continue;

            Subchunk logicalSubchunk = logicalSubchunks[sub];
            if (logicalSubchunk != null && logicalSubchunk.IsVisible)
                return true;
        }

        return false;
    }

    private int GetVisibleOpaqueSubchunkMask(Subchunk[] logicalSubchunks)
    {
        if (!hasOpaqueGeometry || logicalSubchunks == null)
            return 0;

        int mask = 0;
        int end = Mathf.Min(EndSubchunkIndexExclusive, logicalSubchunks.Length);
        for (int sub = startSubchunkIndex; sub < end; sub++)
        {
            int localSubchunkIndex = sub - startSubchunkIndex;
            int faceCount = pulledOpaqueFaceCountsByLocalSubchunk != null &&
                            localSubchunkIndex >= 0 &&
                            localSubchunkIndex < pulledOpaqueFaceCountsByLocalSubchunk.Length
                ? pulledOpaqueFaceCountsByLocalSubchunk[localSubchunkIndex]
                : 0;
            if (faceCount <= 0)
                continue;

            Subchunk logicalSubchunk = logicalSubchunks[sub];
            if (logicalSubchunk != null && logicalSubchunk.hasGeometry && logicalSubchunk.IsVisible)
                mask |= 1 << sub;
        }

        return mask;
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

    private Bounds GetWorldBounds(Bounds localBounds)
    {
        Vector3 center = transform.TransformPoint(localBounds.center);
        Vector3 size = Vector3.Scale(localBounds.size, transform.lossyScale);
        return new Bounds(center, size);
    }

    private static Bounds CreateMeshBounds(int startY, int endY)
    {
        float height = Mathf.Max(1f, endY - startY);
        return new Bounds(
            new Vector3(Chunk.SizeX * 0.5f, startY + height * 0.5f, Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, height + 2f, Chunk.SizeZ + 2f));
    }
}
