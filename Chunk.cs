using System.Collections.Generic;
using System;
using Unity.Collections;
using UnityEngine;


public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public NativeArray<byte> voxelData;
    public const int SubchunkHeight = 16;
    public const int SubchunksPerColumn = (SizeY + SubchunkHeight - 1) / SubchunkHeight; // 384 -> 24

    [HideInInspector] // Impede que a Unity serialize isso incorretamente no Prefab
    public Subchunk[] subchunks;
    [HideInInspector] public ChunkRenderSlice[] visualSlices;
    [HideInInspector] public Bounds worldBounds;
    public bool hasVoxelData = false;
    [NonSerialized] public bool hasVoxelSnapshot = false;

    [HideInInspector] public MeshRenderer[] subRenderers;
    [NonSerialized] public ulong[] subchunkVisibilityMasks;
    [NonSerialized] public bool[] subchunkVisibilityValid;
    [NonSerialized] public ulong lastLightingContextHash;
    [NonSerialized] public bool lightingContextHashValid;
    [NonSerialized] public int visualSubchunksPerRenderer;
    public bool HasInitializedSubchunks =>
        subchunks != null &&
        subchunks.Length == SubchunksPerColumn &&
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

        if (subchunks == null || subchunks.Length != SubchunksPerColumn)
            subchunks = new Subchunk[SubchunksPerColumn];

        for (int i = 0; i < SubchunksPerColumn; i++)
        {
            Subchunk sc = subchunks[i];
            if (sc == null)
            {
                sc = CreateSubchunk(i);
                subchunks[i] = sc;
            }
            else
            {
                sc.Initialize(i);
            }
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

    private Subchunk CreateSubchunk(int subchunkIndex)
    {
        GameObject subObj = new GameObject($"SubchunkLogic_{subchunkIndex}");
        subObj.transform.SetParent(transform, false);
        subObj.transform.localPosition = Vector3.zero;
        subObj.layer = gameObject.layer;

        Subchunk sc = subObj.AddComponent<Subchunk>();
        sc.Initialize(subchunkIndex);
        return sc;
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
            foreach (var sc in subchunks)
            {
                if (sc != null)
                {
                    sc.ClearMesh();
                    sc.SetVisible(false);
                }
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
    }
}
