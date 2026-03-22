using System;
using Unity.Collections;
using UnityEngine;

public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public const int SubchunkHeight = 16;
    public const int VoxelsPerLayer = SizeX * SizeZ;
    public const int VoxelsPerSubchunk = SizeX * SubchunkHeight * SizeZ;
    public const int TotalVoxelCount = SizeX * SizeY * SizeZ;
    public const int SubchunksPerColumn = (SizeY + SubchunkHeight - 1) / SubchunkHeight; // 384 -> 24

    [NonSerialized] private NativeArray<byte>[] sparseVoxelSections;
    [NonSerialized] private int[] sparseVoxelSectionNonAirCounts;

    [HideInInspector]
    public Subchunk[] subchunks;

    [HideInInspector]
    public Bounds worldBounds;

    public bool hasVoxelData = false;

    [HideInInspector]
    public MeshRenderer[] subRenderers;

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
        Requested,
        MeshReady,
        Active,
        Inactive
    }

    public ChunkState state;
    public Vector2Int coord;
    public int generation;

    private void Awake()
    {
        EnsureSparseVoxelStorage();
        hasVoxelData = false;
    }

    private void OnDestroy()
    {
        if (jobScheduled && !currentJob.IsCompleted)
            currentJob.Complete();

        DisposeAllVoxelSections();
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
        return sc;
    }

    public void UpdateWorldBounds()
    {
        worldBounds = new Bounds(
            transform.position + new Vector3(8f, 192f, 8f),
            new Vector3(16f, 384f, 16f));
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
        DisposeAllVoxelSections();
        ClearAllSubchunkVisibilityData();

        if (subchunks != null)
        {
            foreach (Subchunk sc in subchunks)
            {
                if (sc == null)
                    continue;

                sc.ClearMesh();
            }
        }

        gameObject.SetActive(false);
    }

    public bool HasVoxelSectionData(int subchunkIndex)
    {
        return sparseVoxelSections != null &&
               subchunkIndex >= 0 &&
               subchunkIndex < sparseVoxelSections.Length &&
               sparseVoxelSections[subchunkIndex].IsCreated;
    }

    public int GetHighestFilledSubchunkIndex()
    {
        if (sparseVoxelSectionNonAirCounts == null)
            return -1;

        for (int i = sparseVoxelSectionNonAirCounts.Length - 1; i >= 0; i--)
        {
            if (sparseVoxelSectionNonAirCounts[i] > 0 && HasVoxelSectionData(i))
                return i;
        }

        return -1;
    }

    public int GetHighestFilledWorldY()
    {
        int highestSubchunk = GetHighestFilledSubchunkIndex();
        if (highestSubchunk < 0)
            return -1;

        NativeArray<byte> sectionData = sparseVoxelSections[highestSubchunk];
        for (int localY = SubchunkHeight - 1; localY >= 0; localY--)
        {
            int sectionLayerBase = localY * VoxelsPerLayer;
            for (int i = 0; i < VoxelsPerLayer; i++)
            {
                if (sectionData[sectionLayerBase + i] != 0)
                    return highestSubchunk * SubchunkHeight + localY;
            }
        }

        return highestSubchunk * SubchunkHeight;
    }

    public bool TryGetVoxelSectionData(int subchunkIndex, out NativeArray<byte> sectionData)
    {
        if (HasVoxelSectionData(subchunkIndex))
        {
            sectionData = sparseVoxelSections[subchunkIndex];
            return true;
        }

        sectionData = default;
        return false;
    }

    public byte GetBlockId(int localX, int worldY, int localZ)
    {
        if (!IsInsideLocalBounds(localX, worldY, localZ))
            return 0;

        int subchunkIndex = worldY / SubchunkHeight;
        if (!HasVoxelSectionData(subchunkIndex))
            return 0;

        int localY = worldY - subchunkIndex * SubchunkHeight;
        return sparseVoxelSections[subchunkIndex][GetSectionLocalIndex(localX, localY, localZ)];
    }

    public bool SetBlockId(int localX, int worldY, int localZ, byte blockId)
    {
        if (!IsInsideLocalBounds(localX, worldY, localZ))
            return false;

        int subchunkIndex = worldY / SubchunkHeight;
        int localY = worldY - subchunkIndex * SubchunkHeight;
        int localIndex = GetSectionLocalIndex(localX, localY, localZ);

        if (!HasVoxelSectionData(subchunkIndex))
        {
            if (blockId == 0)
                return false;

            EnsureVoxelSectionData(subchunkIndex);
        }

        NativeArray<byte> sectionData = sparseVoxelSections[subchunkIndex];
        byte previous = sectionData[localIndex];
        if (previous == blockId)
            return false;

        sectionData[localIndex] = blockId;

        if (previous == 0 && blockId != 0)
        {
            sparseVoxelSectionNonAirCounts[subchunkIndex]++;
        }
        else if (previous != 0 && blockId == 0)
        {
            sparseVoxelSectionNonAirCounts[subchunkIndex]--;
            if (sparseVoxelSectionNonAirCounts[subchunkIndex] <= 0)
                DisposeVoxelSection(subchunkIndex);
        }

        return true;
    }

    public void CopyVoxelDataFromDense(NativeArray<BlockType> sourceBlockTypes, int border, NativeArray<bool> subchunkNonEmpty)
    {
        EnsureSparseVoxelStorage();

        int sourceVoxelSizeX = SizeX + 2 * border;
        int sourceVoxelSizeZ = SizeZ + 2 * border;
        int sourceActiveHeight = 0;
        if (sourceVoxelSizeX > 0 && sourceVoxelSizeZ > 0)
            sourceActiveHeight = Mathf.Clamp(sourceBlockTypes.Length / (sourceVoxelSizeX * sourceVoxelSizeZ), 0, SizeY);
        int sourceVoxelPlaneSize = sourceVoxelSizeX * sourceActiveHeight;

        for (int subchunkIndex = 0; subchunkIndex < SubchunksPerColumn; subchunkIndex++)
        {
            bool shouldPopulateSection = !subchunkNonEmpty.IsCreated || subchunkNonEmpty[subchunkIndex];
            if (!shouldPopulateSection)
            {
                DisposeVoxelSection(subchunkIndex);
                continue;
            }

            NativeArray<byte> sectionData = EnsureVoxelSectionData(subchunkIndex);
            for (int i = 0; i < sectionData.Length; i++)
                sectionData[i] = 0;

            int nonAirCount = 0;

            for (int localY = 0; localY < SubchunkHeight; localY++)
            {
                int worldY = subchunkIndex * SubchunkHeight + localY;
                if (worldY >= sourceActiveHeight)
                    break;

                int dstPlaneStart = localY * VoxelsPerLayer;
                for (int z = 0; z < SizeZ; z++)
                {
                    int srcBase = (z + border) * sourceVoxelPlaneSize + worldY * sourceVoxelSizeX + border;
                    int dstBase = dstPlaneStart + z * SizeX;

                    for (int x = 0; x < SizeX; x++)
                    {
                        byte value = (byte)sourceBlockTypes[srcBase + x];
                        sectionData[dstBase + x] = value;
                        if (value != 0)
                            nonAirCount++;
                    }
                }
            }

            sparseVoxelSectionNonAirCounts[subchunkIndex] = nonAirCount;
            if (nonAirCount == 0)
                DisposeVoxelSection(subchunkIndex);
        }
    }

    public void CopyVoxelDataSliceFromDense(
        NativeArray<BlockType> sourceBlockTypes,
        int border,
        int baseY,
        int sliceHeight,
        NativeArray<bool> subchunkNonEmpty)
    {
        EnsureSparseVoxelStorage();

        int sourceVoxelSizeX = SizeX + 2 * border;
        int sourceVoxelSizeZ = SizeZ + 2 * border;
        int sourceSliceHeight = 0;
        if (sourceVoxelSizeX > 0 && sourceVoxelSizeZ > 0)
            sourceSliceHeight = Mathf.Clamp(sourceBlockTypes.Length / (sourceVoxelSizeX * sourceVoxelSizeZ), 0, SizeY);
        int sourceVoxelPlaneSize = sourceVoxelSizeX * sourceSliceHeight;

        int clampedBaseY = Mathf.Clamp(baseY, 0, SizeY);
        int clampedEndY = Mathf.Clamp(clampedBaseY + Mathf.Min(sliceHeight, sourceSliceHeight), 0, SizeY);
        if (clampedEndY <= clampedBaseY)
            return;

        int startSubchunk = Mathf.Clamp(clampedBaseY / SubchunkHeight, 0, SubchunksPerColumn - 1);
        int endSubchunkExclusive = Mathf.Clamp((clampedEndY + SubchunkHeight - 1) / SubchunkHeight, 0, SubchunksPerColumn);
        for (int subchunkIndex = startSubchunk; subchunkIndex < endSubchunkExclusive; subchunkIndex++)
        {
            bool shouldPopulateSection = !subchunkNonEmpty.IsCreated || subchunkNonEmpty[subchunkIndex];
            if (!shouldPopulateSection)
            {
                DisposeVoxelSection(subchunkIndex);
                continue;
            }

            NativeArray<byte> sectionData = EnsureVoxelSectionData(subchunkIndex);
            for (int i = 0; i < sectionData.Length; i++)
                sectionData[i] = 0;

            int nonAirCount = 0;
            for (int localY = 0; localY < SubchunkHeight; localY++)
            {
                int worldY = subchunkIndex * SubchunkHeight + localY;
                if (worldY < clampedBaseY || worldY >= clampedEndY)
                    continue;

                int sourceY = worldY - clampedBaseY;
                int dstPlaneStart = localY * VoxelsPerLayer;
                for (int z = 0; z < SizeZ; z++)
                {
                    int srcBase = (z + border) * sourceVoxelPlaneSize + sourceY * sourceVoxelSizeX + border;
                    int dstBase = dstPlaneStart + z * SizeX;

                    for (int x = 0; x < SizeX; x++)
                    {
                        byte value = (byte)sourceBlockTypes[srcBase + x];
                        sectionData[dstBase + x] = value;
                        if (value != 0)
                            nonAirCount++;
                    }
                }
            }

            sparseVoxelSectionNonAirCounts[subchunkIndex] = nonAirCount;
            if (nonAirCount == 0)
                DisposeVoxelSection(subchunkIndex);
        }
    }

    public void WriteDenseSnapshot(NativeArray<byte> destination, int destinationStartIndex, int activeHeight)
    {
        if (!destination.IsCreated)
            return;

        int clampedHeight = Mathf.Clamp(activeHeight, 0, SizeY);
        int denseVoxelCount = SizeX * clampedHeight * SizeZ;
        for (int i = 0; i < denseVoxelCount; i++)
            destination[destinationStartIndex + i] = 0;

        if (sparseVoxelSections == null || clampedHeight == 0)
            return;

        for (int subchunkIndex = 0; subchunkIndex < SubchunksPerColumn; subchunkIndex++)
        {
            if (!HasVoxelSectionData(subchunkIndex))
                continue;

            NativeArray<byte> sectionData = sparseVoxelSections[subchunkIndex];
            int worldYStart = subchunkIndex * SubchunkHeight;
            if (worldYStart >= clampedHeight)
                break;

            for (int localY = 0; localY < SubchunkHeight && worldYStart + localY < clampedHeight; localY++)
            {
                NativeArray<byte>.Copy(
                    sectionData,
                    localY * VoxelsPerLayer,
                    destination,
                    destinationStartIndex + (worldYStart + localY) * VoxelsPerLayer,
                    VoxelsPerLayer);
            }
        }
    }

    private void EnsureSparseVoxelStorage()
    {
        if (sparseVoxelSections == null || sparseVoxelSections.Length != SubchunksPerColumn)
            sparseVoxelSections = new NativeArray<byte>[SubchunksPerColumn];

        if (sparseVoxelSectionNonAirCounts == null || sparseVoxelSectionNonAirCounts.Length != SubchunksPerColumn)
            sparseVoxelSectionNonAirCounts = new int[SubchunksPerColumn];
    }

    private NativeArray<byte> EnsureVoxelSectionData(int subchunkIndex)
    {
        EnsureSparseVoxelStorage();

        if (!sparseVoxelSections[subchunkIndex].IsCreated)
        {
            sparseVoxelSections[subchunkIndex] = new NativeArray<byte>(
                VoxelsPerSubchunk,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
        }

        return sparseVoxelSections[subchunkIndex];
    }

    private void DisposeAllVoxelSections()
    {
        if (sparseVoxelSections == null)
            return;

        for (int i = 0; i < sparseVoxelSections.Length; i++)
            DisposeVoxelSection(i);
    }

    private void DisposeVoxelSection(int subchunkIndex)
    {
        EnsureSparseVoxelStorage();

        if (sparseVoxelSections[subchunkIndex].IsCreated)
            sparseVoxelSections[subchunkIndex].Dispose();

        sparseVoxelSections[subchunkIndex] = default;
        sparseVoxelSectionNonAirCounts[subchunkIndex] = 0;
    }

    private static bool IsInsideLocalBounds(int localX, int worldY, int localZ)
    {
        return localX >= 0 && localX < SizeX &&
               localZ >= 0 && localZ < SizeZ &&
               worldY >= 0 && worldY < SizeY;
    }

    private static int GetSectionLocalIndex(int localX, int localY, int localZ)
    {
        return localX + localZ * SizeX + localY * VoxelsPerLayer;
    }
}
