using System.Collections.Generic;
using System;
using Unity.Collections;
using UnityEngine;


public class Chunk : MonoBehaviour
{
    public struct SubchunkState
    {
        public bool hasGeometry;
        public bool canHaveColliders;
        public bool hasColliderData;
        public bool isVisible;
    }

    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public NativeArray<byte> voxelData;
    public const int SubchunkHeight = 16;
    public const int SubchunksPerColumn = (SizeY + SubchunkHeight - 1) / SubchunkHeight; // 384 -> 24

    [HideInInspector] // Impede que a Unity serialize isso incorretamente no Prefab
    public SubchunkState[] subchunks;
    [HideInInspector] public ChunkRenderSlice[] visualSlices;
    [HideInInspector] public Bounds worldBounds;
    public bool hasVoxelData = false;
    [NonSerialized] public bool hasVoxelSnapshot = false;

    [HideInInspector] public MeshRenderer[] subRenderers;
    [NonSerialized] private SubchunkColliderBuilder[] subchunkColliderBuilders;
    [NonSerialized] public ulong[] subchunkVisibilityMasks;
    [NonSerialized] public bool[] subchunkVisibilityValid;
    [NonSerialized] public ulong lastLightingContextHash;
    [NonSerialized] public bool lightingContextHashValid;
    [NonSerialized] public int visualSubchunksPerRenderer;
    public bool HasInitializedSubchunks =>
        subchunks != null &&
        subchunks.Length == SubchunksPerColumn &&
        subchunkColliderBuilders != null &&
        subchunkColliderBuilders.Length == SubchunksPerColumn &&
        visualSlices != null &&
        visualSlices.Length == GetVisualSliceCount(visualSubchunksPerRenderer) &&
        subRenderers != null &&
        subRenderers.Length == visualSlices.Length;

    public Unity.Jobs.JobHandle currentJob;
    public bool jobScheduled;
    [NonSerialized] public int pendingVisualMeshApplyCount;
    [NonSerialized] public int visualMeshApplyBatchId;

    public enum ChunkState
    {
        Requested,   // job agendado
        MeshReady,   // resultado chegou
        Active,       // mesh aplicado
        Inactive
    }

    public ChunkState state;


    private void Awake()
    {

        int total = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;

        voxelData = new NativeArray<byte>(total, Allocator.Persistent);

        hasVoxelData = false; // ainda útil para saber se já tem dados válidos
    }
    private void OnDestroy()
    {
        if (World.Instance != null && !World.Instance.IsShuttingDown)
            World.Instance.CompletePendingJobsForChunk(this);

        CompleteTrackedJob();


        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }

    public static int GetVisualSliceCount(int subchunksPerRenderer)
    {
        int clamped = Mathf.Clamp(subchunksPerRenderer, 1, SubchunksPerColumn);
        return (SubchunksPerColumn + clamped - 1) / clamped;
    }

    public int GetVisualSliceIndexForSubchunk(int subchunkIndex)
    {
        int clampedSize = Mathf.Clamp(visualSubchunksPerRenderer, 1, SubchunksPerColumn);
        return Mathf.Clamp(subchunkIndex / clampedSize, 0, Mathf.Max(0, GetVisualSliceCount(clampedSize) - 1));
    }

    public int GetVisualSliceMask(int visualSliceIndex)
    {
        if (visualSliceIndex < 0 || visualSliceIndex >= GetVisualSliceCount(visualSubchunksPerRenderer))
            return 0;

        int start = visualSliceIndex * visualSubchunksPerRenderer;
        int end = Mathf.Min(start + visualSubchunksPerRenderer, SubchunksPerColumn);
        int mask = 0;
        for (int sub = start; sub < end; sub++)
            mask |= 1 << sub;

        return mask;
    }

    public bool TryGetVisualSlice(int visualSliceIndex, out ChunkRenderSlice visualSlice)
    {
        if (visualSlices != null &&
            visualSliceIndex >= 0 &&
            visualSliceIndex < visualSlices.Length)
        {
            visualSlice = visualSlices[visualSliceIndex];
            return visualSlice != null;
        }

        visualSlice = null;
        return false;
    }

    public void RefreshVisualSliceVisibility(int visualSliceIndex)
    {
        if (TryGetVisualSlice(visualSliceIndex, out ChunkRenderSlice visualSlice))
            visualSlice.RefreshVisibility(subchunks);
    }

    public void RefreshVisualSliceVisibilityForSubchunk(int subchunkIndex)
    {
        RefreshVisualSliceVisibility(GetVisualSliceIndexForSubchunk(subchunkIndex));
    }

    public void RefreshAllVisualSliceVisibility()
    {
        if (visualSlices == null)
            return;

        for (int i = 0; i < visualSlices.Length; i++)
        {
            ChunkRenderSlice visualSlice = visualSlices[i];
            if (visualSlice != null)
                visualSlice.RefreshVisibility(subchunks);
        }
    }

    public void InitializeSubchunks(Material[] materials, int subchunksPerVisualSlice)
    {
        EnsureVisibilityData();

        visualSubchunksPerRenderer = Mathf.Clamp(subchunksPerVisualSlice, 1, SubchunksPerColumn);

        EnsureSubchunkStorage();

        for (int i = 0; i < SubchunksPerColumn; i++)
        {
            subchunks[i].hasGeometry = false;
            subchunks[i].canHaveColliders = false;
            subchunks[i].hasColliderData = false;
            subchunks[i].isVisible = true;
            subchunkColliderBuilders[i].Clear();
        }

        int visualSliceCount = GetVisualSliceCount(visualSubchunksPerRenderer);
        if (visualSlices != null && visualSlices.Length != visualSliceCount)
        {
            for (int i = 0; i < visualSlices.Length; i++)
            {
                if (visualSlices[i] != null)
                    Destroy(visualSlices[i].gameObject);
            }

            visualSlices = null;
        }

        if (visualSlices == null || visualSlices.Length != visualSliceCount)
            visualSlices = new ChunkRenderSlice[visualSliceCount];

        if (subRenderers == null || subRenderers.Length != visualSliceCount)
            subRenderers = new MeshRenderer[visualSliceCount];

        for (int i = 0; i < visualSliceCount; i++)
        {
            int startSubchunk = i * visualSubchunksPerRenderer;
            int sliceSubchunkCount = Mathf.Min(visualSubchunksPerRenderer, SubchunksPerColumn - startSubchunk);

            ChunkRenderSlice visualSlice = visualSlices[i];
            if (visualSlice == null)
            {
                visualSlice = CreateVisualSlice(i, materials, startSubchunk, sliceSubchunkCount);
                visualSlices[i] = visualSlice;
            }
            else
            {
                visualSlice.Initialize(materials, i, startSubchunk, sliceSubchunkCount);
            }

            subRenderers[i] = visualSlice != null ? visualSlice.meshRenderer : null;
        }

        UpdateWorldBounds();
    }

    private ChunkRenderSlice CreateVisualSlice(int sliceIndex, Material[] materials, int startSubchunkIndex, int sliceSubchunkCount)
    {
        GameObject sliceObj = new GameObject($"ChunkSlice_{sliceIndex}");
        sliceObj.transform.SetParent(transform, false);
        sliceObj.transform.localPosition = Vector3.zero;
        sliceObj.layer = gameObject.layer;

        ChunkRenderSlice visualSlice = sliceObj.AddComponent<ChunkRenderSlice>();
        visualSlice.Initialize(materials, sliceIndex, startSubchunkIndex, sliceSubchunkCount);
        sliceObj.SetActive(false);
        return visualSlice;
    }

    public void UpdateWorldBounds()
    {
        worldBounds = new Bounds(
            transform.position + new Vector3(8f, 192f, 8f),
            new Vector3(16f, 384f, 16f)
        );
    }

    public void EnsureVisibilityData()
    {
        if (subchunkVisibilityMasks == null || subchunkVisibilityMasks.Length != SubchunksPerColumn)
            subchunkVisibilityMasks = new ulong[SubchunksPerColumn];

        if (subchunkVisibilityValid == null || subchunkVisibilityValid.Length != SubchunksPerColumn)
            subchunkVisibilityValid = new bool[SubchunksPerColumn];
    }

    public bool SetSubchunkVisibilityData(int subchunkIndex, ulong visibilityMask)
    {
        EnsureVisibilityData();
        bool changed = !subchunkVisibilityValid[subchunkIndex] ||
                       subchunkVisibilityMasks[subchunkIndex] != visibilityMask;
        subchunkVisibilityMasks[subchunkIndex] = visibilityMask;
        subchunkVisibilityValid[subchunkIndex] = true;
        return changed;
    }

    public void ClearSubchunkVisibilityData(int subchunkIndex)
    {
        EnsureVisibilityData();
        subchunkVisibilityMasks[subchunkIndex] = 0UL;
        subchunkVisibilityValid[subchunkIndex] = false;
    }

    public void ClearAllSubchunkVisibilityData()
    {
        EnsureVisibilityData();
        Array.Clear(subchunkVisibilityMasks, 0, subchunkVisibilityMasks.Length);
        Array.Clear(subchunkVisibilityValid, 0, subchunkVisibilityValid.Length);
    }

    public bool TryGetSubchunkVisibilityData(int subchunkIndex, out ulong visibilityMask)
    {
        if (subchunkVisibilityValid != null &&
            subchunkIndex >= 0 &&
            subchunkIndex < subchunkVisibilityValid.Length &&
            subchunkVisibilityValid[subchunkIndex])
        {
            visibilityMask = subchunkVisibilityMasks[subchunkIndex];
            return true;
        }

        visibilityMask = 0UL;
        return false;
    }




    public Vector2Int coord;
    public void SetCoord(Vector2Int c)
    {
        coord = c;
        gameObject.name = $"Chunk_{c.x}_{c.y}";
    }
    public void ResetChunk()
    {
        CompleteTrackedJob();

        jobScheduled = false;
        currentJob = default;
        pendingVisualMeshApplyCount = 0;
        visualMeshApplyBatchId = 0;
        state = ChunkState.Inactive;
        generation = -1;
        hasVoxelData = false;
        hasVoxelSnapshot = false;
        ClearAllSubchunkVisibilityData();
        lightingContextHashValid = false;
        lastLightingContextHash = 0;

        if (subchunks != null)
        {
            for (int i = 0; i < subchunks.Length; i++)
            {
                ClearSubchunkMesh(i);
                SetSubchunkVisible(i, false);
            }
        }

        if (visualSlices != null)
        {
            foreach (var visualSlice in visualSlices)
            {
                if (visualSlice != null)
                    visualSlice.ClearMesh();
            }
        }

        gameObject.SetActive(false);
    }
    public int generation;

    public void CompleteTrackedJob()
    {
        if (!jobScheduled)
            return;

        try
        {
            currentJob.Complete();
        }
        catch (InvalidOperationException)
        {
        }
    }

<<<<<<< HEAD
    public bool HasSubchunkGeometry(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].hasGeometry;
    }

    public bool CanSubchunkHaveColliders(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].canHaveColliders;
    }

    public bool HasSubchunkColliderData(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].hasColliderData;
    }

    public bool IsSubchunkVisible(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].isVisible;
    }

    public void SetSubchunkMeshState(int subchunkIndex, bool geometryPresent, bool solidColliderGeometryPresent)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].hasGeometry = geometryPresent;
        subchunks[subchunkIndex].canHaveColliders = geometryPresent && solidColliderGeometryPresent;

        if (!subchunks[subchunkIndex].canHaveColliders)
            ClearSubchunkColliderData(subchunkIndex);
    }

    public void SetSubchunkVisible(int subchunkIndex, bool visible)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].isVisible = visible;
    }

    public void SetSubchunkColliderSystemEnabled(int subchunkIndex, bool enabled)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        bool shouldEnable = enabled &&
                            subchunks[subchunkIndex].hasGeometry &&
                            subchunks[subchunkIndex].hasColliderData;
        colliderBuilder.SetEnabled(shouldEnable);
    }

    public void ClearSubchunkMesh(int subchunkIndex)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].hasGeometry = false;
        subchunks[subchunkIndex].canHaveColliders = false;
        ClearSubchunkColliderData(subchunkIndex);
    }

    public void ClearSubchunkColliderData(int subchunkIndex)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        subchunks[subchunkIndex].hasColliderData = false;
        colliderBuilder.Clear();
    }

    public void RebuildSubchunkColliders(
        int subchunkIndex,
        NativeArray<byte> voxelSource,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        if (!subchunks[subchunkIndex].hasGeometry || !subchunks[subchunkIndex].canHaveColliders)
        {
            ClearSubchunkColliderData(subchunkIndex);
            return;
        }

        subchunks[subchunkIndex].hasColliderData = colliderBuilder.TryBuild(gameObject, voxelSource, blockMappings, startY, endY);
    }

    private void EnsureSubchunkStorage()
    {
        if (subchunks == null || subchunks.Length != SubchunksPerColumn)
            subchunks = new SubchunkState[SubchunksPerColumn];

        if (subchunkColliderBuilders == null || subchunkColliderBuilders.Length != SubchunksPerColumn)
            subchunkColliderBuilders = new SubchunkColliderBuilder[SubchunksPerColumn];

        for (int i = 0; i < subchunkColliderBuilders.Length; i++)
        {
            if (subchunkColliderBuilders[i] == null)
                subchunkColliderBuilders[i] = new SubchunkColliderBuilder();
        }
    }

    private bool IsSubchunkIndexValid(int subchunkIndex)
    {
        return subchunks != null &&
               subchunkIndex >= 0 &&
               subchunkIndex < subchunks.Length;
    }

    private bool TryGetSubchunkColliderBuilder(int subchunkIndex, out SubchunkColliderBuilder colliderBuilder)
    {
        colliderBuilder = null;
        EnsureSubchunkStorage();

        if (!IsSubchunkIndexValid(subchunkIndex) ||
            subchunkColliderBuilders == null ||
            subchunkIndex >= subchunkColliderBuilders.Length)
        {
            return false;
        }

        colliderBuilder = subchunkColliderBuilders[subchunkIndex];
        return colliderBuilder != null;
=======
    public int BeginVisualMeshApplyBatch()
    {
        visualMeshApplyBatchId++;
        pendingVisualMeshApplyCount = 0;
        return visualMeshApplyBatchId;
    }

    public void SetPendingVisualMeshApplyCount(int batchId, int pendingCount)
    {
        if (batchId != visualMeshApplyBatchId)
            return;

        pendingVisualMeshApplyCount = Mathf.Max(0, pendingCount);
    }

    public bool CompleteVisualMeshApply(int batchId)
    {
        if (batchId != visualMeshApplyBatchId || pendingVisualMeshApplyCount <= 0)
            return false;

        pendingVisualMeshApplyCount--;
        return pendingVisualMeshApplyCount == 0;
>>>>>>> a06e462af060f08f44f519673eb4b6dba6baae60
    }
}
