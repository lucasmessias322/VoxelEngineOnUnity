using System;
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
    private static readonly int CompactOpaqueFaceStride = Marshal.SizeOf<MeshGenerator.CompactOpaqueFace>();
    private static readonly Plane[] SharedFrustumPlanes = new Plane[6];
    private static readonly System.Collections.Generic.List<ChunkRenderSlice> ManagedRenderSlices = new System.Collections.Generic.List<ChunkRenderSlice>(2048);
    private static readonly System.Collections.Generic.List<ChunkRenderSlice> OpaqueRenderSlices = new System.Collections.Generic.List<ChunkRenderSlice>(512);
    private static bool renderHookRegistered;
    private static OpaqueRenderTelemetrySnapshot latestOpaqueRenderTelemetry;
    private static int telemetryFrame = -1;
    private static int telemetrySlicesRendered;
    private static int telemetryDrawCalls;
    private static int telemetrySingleDrawSlices;
    private static int telemetryRunBatchedSlices;
    private static int telemetryVisibleOpaqueSubchunks;
    private static int telemetrySavedDrawCalls;

    private MeshFilter meshFilter;
    [HideInInspector] public MeshRenderer meshRenderer;
    private Mesh mesh;
    private GraphicsBuffer pulledOpaqueFaceBuffer;
    private MaterialPropertyBlock pulledOpaquePropertyBlock;
    private Material opaqueMaterial;
    private Bounds localRenderBounds;
    private Color grassTint = Color.white;
    private int pulledOpaqueFaceCount;
    private int pulledOpaqueFaceCapacity;
    private int startSubchunkIndex;
    private int subchunkCount;
    private int logicalVisibleOpaqueSubchunkMask;
    private int visibleOpaqueSubchunkMask;
    private int opaqueGeometrySubchunkMask;
    private int[] pulledOpaqueFaceStartsByLocalSubchunk;
    private int[] pulledOpaqueFaceCountsByLocalSubchunk;
    private bool hasGeometry;
    private bool hasMeshGeometry;
    private bool hasOpaqueGeometry;
    private ChunkOpaqueRenderBackendKind activeOpaqueRenderBackendKind;
    private bool logicalShouldShow;
    private bool isFrustumVisible = true;
    private bool shouldRenderOpaqueGeometry;
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
        public readonly int singleDrawSlices;
        public readonly int runBatchedSlices;
        public readonly int visibleOpaqueSubchunks;
        public readonly int savedDrawCalls;

        public OpaqueRenderTelemetrySnapshot(
            int frame,
            int slicesRendered,
            int drawCalls,
            int singleDrawSlices,
            int runBatchedSlices,
            int visibleOpaqueSubchunks,
            int savedDrawCalls)
        {
            this.frame = frame;
            this.slicesRendered = slicesRendered;
            this.drawCalls = drawCalls;
            this.singleDrawSlices = singleDrawSlices;
            this.runBatchedSlices = runBatchedSlices;
            this.visibleOpaqueSubchunks = visibleOpaqueSubchunks;
            this.savedDrawCalls = savedDrawCalls;
        }

        public bool IsValid => frame >= 0;
    }

    public static OpaqueRenderTelemetrySnapshot GetLatestOpaqueRenderTelemetry()
    {
        return latestOpaqueRenderTelemetry;
    }

    public static bool SupportsOpaqueVertexPulling()
    {
        return SystemInfo.graphicsShaderLevel >= 45 &&
               SystemInfo.supportsInstancing &&
               SystemInfo.maxComputeBufferInputsVertex > 0;
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
        logicalShouldShow = hasGeometry && HasAnyVisibleGeometry(logicalSubchunks);
        logicalVisibleOpaqueSubchunkMask = logicalShouldShow ? GetVisibleOpaqueSubchunkMask(logicalSubchunks) : 0;

        if (!hasGeometry)
        {
            logicalShouldShow = false;
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
        ReleasePulledOpaqueFaceBuffer();

        hasGeometry = false;
        hasMeshGeometry = false;
        hasOpaqueGeometry = false;
        activeOpaqueRenderBackendKind = ChunkOpaqueRenderBackendKind.ClassicMesh;
        logicalShouldShow = false;
        logicalVisibleOpaqueSubchunkMask = 0;
        isFrustumVisible = true;
        shouldRenderOpaqueGeometry = false;
        visibleOpaqueSubchunkMask = 0;
        UpdateManagedRenderRegistration();
        UpdateOpaqueRenderRegistration();
        SetRendererEnabled(false);
        SetActiveState(false);
    }

    private void OnDestroy()
    {
        UnregisterManagedRenderSlice();
        UnregisterOpaqueRenderSlice();
        ReleasePulledOpaqueFaceBuffer();
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

        EnsurePulledOpaqueFaceBufferCapacity(totalOpaqueFaceCount);
        EnsurePulledOpaqueSubchunkMetadataCapacity();
        Array.Clear(pulledOpaqueFaceStartsByLocalSubchunk, 0, pulledOpaqueFaceStartsByLocalSubchunk.Length);
        Array.Clear(pulledOpaqueFaceCountsByLocalSubchunk, 0, pulledOpaqueFaceCountsByLocalSubchunk.Length);
        opaqueGeometrySubchunkMask = 0;

        NativeArray<MeshGenerator.CompactOpaqueFace> uploadData = new NativeArray<MeshGenerator.CompactOpaqueFace>(
            totalOpaqueFaceCount,
            Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);

        int writeOffset = 0;
        for (int sub = startSubchunkIndex; sub < EndSubchunkIndexExclusive; sub++)
        {
            MeshGenerator.SubchunkMeshRange range = subchunkRanges[sub];
            int localSubchunkIndex = sub - startSubchunkIndex;
            pulledOpaqueFaceStartsByLocalSubchunk[localSubchunkIndex] = writeOffset;
            pulledOpaqueFaceCountsByLocalSubchunk[localSubchunkIndex] = range.opaqueFaceCount;

            if (range.opaqueFaceCount <= 0)
                continue;

            opaqueGeometrySubchunkMask |= 1 << sub;

            NativeArray<MeshGenerator.CompactOpaqueFace>.Copy(
                compactOpaqueFaces.AsArray(),
                range.opaqueFaceStart,
                uploadData,
                writeOffset,
                range.opaqueFaceCount);

            writeOffset += range.opaqueFaceCount;
        }

        pulledOpaqueFaceBuffer.SetData(uploadData);
        uploadData.Dispose();

        pulledOpaqueFaceCount = totalOpaqueFaceCount;
        UpdatePulledOpaquePropertyBlock();
        UpdateOpaqueRenderRegistration();
    }

    private void ClearPulledOpaqueFaces()
    {
        pulledOpaqueFaceCount = 0;
        visibleOpaqueSubchunkMask = 0;
        opaqueGeometrySubchunkMask = 0;
        if (pulledOpaqueFaceStartsByLocalSubchunk != null)
            Array.Clear(pulledOpaqueFaceStartsByLocalSubchunk, 0, pulledOpaqueFaceStartsByLocalSubchunk.Length);
        if (pulledOpaqueFaceCountsByLocalSubchunk != null)
            Array.Clear(pulledOpaqueFaceCountsByLocalSubchunk, 0, pulledOpaqueFaceCountsByLocalSubchunk.Length);
        UpdatePulledOpaquePropertyBlock();
        UpdateOpaqueRenderRegistration();
    }

    private void EnsurePulledOpaqueFaceBufferCapacity(int requiredCount)
    {
        if (requiredCount <= 0)
            return;

        if (pulledOpaqueFaceBuffer != null && pulledOpaqueFaceCapacity >= requiredCount)
            return;

        ReleasePulledOpaqueFaceBuffer();

        pulledOpaqueFaceCapacity = requiredCount;
        pulledOpaqueFaceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pulledOpaqueFaceCapacity, CompactOpaqueFaceStride);
    }

    private void ReleasePulledOpaqueFaceBuffer()
    {
        if (pulledOpaqueFaceBuffer != null)
        {
            pulledOpaqueFaceBuffer.Dispose();
            pulledOpaqueFaceBuffer = null;
        }

        pulledOpaqueFaceCapacity = 0;
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

        if (pulledOpaqueFaceBuffer != null && pulledOpaqueFaceCount > 0)
            pulledOpaquePropertyBlock.SetBuffer(CompactOpaqueFacesPropertyId, pulledOpaqueFaceBuffer);
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
        visibleOpaqueSubchunkMask = isFrustumVisible ? logicalVisibleOpaqueSubchunkMask : 0;
        SetRendererEnabled(hasMeshGeometry && logicalShouldShow && isFrustumVisible);
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
            pulledOpaqueFaceBuffer != null &&
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
        latestOpaqueRenderTelemetry = default;
        telemetryFrame = -1;
        telemetrySlicesRendered = 0;
        telemetryDrawCalls = 0;
        telemetrySingleDrawSlices = 0;
        telemetryRunBatchedSlices = 0;
        telemetryVisibleOpaqueSubchunks = 0;
        telemetrySavedDrawCalls = 0;
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
        if (!shouldRenderOpaqueGeometry ||
            !isFrustumVisible ||
            !hasOpaqueGeometry ||
            pulledOpaqueFaceCount <= 0 ||
            pulledOpaqueFaceBuffer == null ||
            opaqueMaterial == null)
        {
            return;
        }

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

        if (subchunkCount <= 1 || CanRenderOpaqueSliceAsSingleDraw())
        {
            pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, 0f);
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, pulledOpaqueFaceCount);
            if (trackTelemetry)
                RecordOpaqueTelemetry(visibleOpaqueSubchunkCount, 1, true, false);
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

            pulledOpaquePropertyBlock.SetFloat(PulledOpaqueFaceBaseIndexPropertyId, runFaceStart);
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, runFaceCount);
            runCount++;
            localSubchunkIndex = nextLocalSubchunkIndex;
        }

        if (trackTelemetry)
            RecordOpaqueTelemetry(visibleOpaqueSubchunkCount, runCount, false, runCount > 0);
    }

    private bool CanRenderOpaqueSliceAsSingleDraw()
    {
        return opaqueGeometrySubchunkMask != 0 &&
               visibleOpaqueSubchunkMask == opaqueGeometrySubchunkMask;
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

    private static void RecordOpaqueTelemetry(int visibleOpaqueSubchunkCount, int actualDrawCalls, bool usedSingleDraw, bool usedRunBatching)
    {
        int currentFrame = Time.frameCount;
        if (telemetryFrame != currentFrame)
        {
            telemetryFrame = currentFrame;
            telemetrySlicesRendered = 0;
            telemetryDrawCalls = 0;
            telemetrySingleDrawSlices = 0;
            telemetryRunBatchedSlices = 0;
            telemetryVisibleOpaqueSubchunks = 0;
            telemetrySavedDrawCalls = 0;
        }

        telemetrySlicesRendered++;
        telemetryDrawCalls += actualDrawCalls;
        telemetryVisibleOpaqueSubchunks += visibleOpaqueSubchunkCount;
        telemetrySavedDrawCalls += Mathf.Max(0, visibleOpaqueSubchunkCount - actualDrawCalls);

        if (usedSingleDraw)
            telemetrySingleDrawSlices++;
        else if (usedRunBatching)
            telemetryRunBatchedSlices++;

        latestOpaqueRenderTelemetry = new OpaqueRenderTelemetrySnapshot(
            telemetryFrame,
            telemetrySlicesRendered,
            telemetryDrawCalls,
            telemetrySingleDrawSlices,
            telemetryRunBatchedSlices,
            telemetryVisibleOpaqueSubchunks,
            telemetrySavedDrawCalls);
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
