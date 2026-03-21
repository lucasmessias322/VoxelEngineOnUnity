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
    [HideInInspector] public Bounds worldBounds;
    public bool hasVoxelData = false;

    [HideInInspector] public MeshRenderer[] subRenderers;
    [NonSerialized] public ulong[] subchunkVisibilityMasks;
    [NonSerialized] public bool[] subchunkVisibilityValid;
    public bool HasInitializedSubchunks =>
        subchunks != null &&
        subchunks.Length == SubchunksPerColumn &&
        subRenderers != null &&
        subRenderers.Length == SubchunksPerColumn;

    public Unity.Jobs.JobHandle currentJob;
    public bool jobScheduled;

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
        if (jobScheduled && currentJob.IsCompleted == false)
        {
            currentJob.Complete();
        }


        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }

    public void InitializeSubchunks(Material[] materials)
    {
        EnsureVisibilityData();

        if (subchunks == null || subchunks.Length != SubchunksPerColumn)
            subchunks = new Subchunk[SubchunksPerColumn];

        if (subRenderers == null || subRenderers.Length != SubchunksPerColumn)
            subRenderers = new MeshRenderer[SubchunksPerColumn];

        for (int i = 0; i < SubchunksPerColumn; i++)
        {
            Subchunk sc = subchunks[i];
            if (sc == null)
            {
                sc = CreateSubchunk(materials, i);
                subchunks[i] = sc;
            }
            else
            {
                sc.Initialize(materials, i);
            }

            subRenderers[i] = sc != null ? sc.meshRenderer : null;
        }

        UpdateWorldBounds();
    }

    private Subchunk CreateSubchunk(Material[] materials, int subchunkIndex)
    {
        GameObject subObj = new GameObject($"Subchunk_{subchunkIndex}");
        subObj.transform.SetParent(transform, false);
        subObj.transform.localPosition = Vector3.zero;
        subObj.layer = gameObject.layer;

        Subchunk sc = subObj.AddComponent<Subchunk>();
        sc.Initialize(materials, subchunkIndex);
        subObj.SetActive(false);
        return sc;
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
        if (jobScheduled && !currentJob.IsCompleted)
            currentJob.Complete();

        jobScheduled = false;
        state = ChunkState.Inactive;
        generation = -1;
        hasVoxelData = false;
        ClearAllSubchunkVisibilityData();

        if (subchunks != null)
        {
            foreach (var sc in subchunks)
            {
                if (sc != null)
                {
                    sc.ClearMesh();
                    sc.gameObject.SetActive(false);
                }
            }
        }

        gameObject.SetActive(false);
    }
    public int generation;
}
