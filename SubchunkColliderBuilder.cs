using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

internal sealed class SubchunkColliderBuilder
{
    private readonly List<BoxCollider> boxColliders = new List<BoxCollider>(128);
    private int activeBoxColliderCount;
    private bool[] colliderSolidsBuffer;
    private bool[] colliderVisitedBuffer;
    private ulong[] colliderOccupancyBuffer;
    private ulong[] cachedColliderOccupancyBits;
    private int cachedColliderOccupancyWordCount;
    private int cachedColliderCount;
    private int cachedStartY = -1;
    private int cachedHeight;
    private bool hasCachedColliderLayout;

    public bool TryBuild(
        GameObject owner,
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY)
    {
        if (!TryResolveBuildRange(voxelData, blockMappings, startY, endY, out int clampedStartY, out int height))
        {
            Clear();
            return false;
        }

        int volume = Chunk.SizeX * height * Chunk.SizeZ;
        PrepareOccupancyBuffer(volume, out ulong[] occupancyBits, out int occupancyWordCount);
        bool hasSolidBlocks = FillOccupancyBuffer(voxelData, blockMappings, clampedStartY, height, occupancyBits);

        if (TryRestoreCachedColliders(owner, clampedStartY, height, occupancyBits, occupancyWordCount, out bool restoredHasColliders))
            return restoredHasColliders;

        if (!hasSolidBlocks)
        {
            CacheColliderLayout(clampedStartY, height, occupancyBits, occupancyWordCount, 0);
            activeBoxColliderCount = 0;
            DisableUnusedColliders(0);
            return false;
        }

        PrepareBuffers(volume, out bool[] solids, out bool[] visited);
        FillSolidBufferFromOccupancy(occupancyBits, volume, solids);

        int colliderCount = CreateGreedyColliders(owner, clampedStartY, height, solids, visited);
        activeBoxColliderCount = colliderCount;
        DisableUnusedColliders(colliderCount);
        CacheColliderLayout(clampedStartY, height, occupancyBits, occupancyWordCount, colliderCount);
        return colliderCount > 0;
    }

    public void SetEnabled(bool enabled)
    {
        for (int i = 0; i < activeBoxColliderCount; i++)
        {
            BoxCollider box = boxColliders[i];
            if (box != null)
                box.enabled = enabled;
        }
    }

    public void Clear()
    {
        activeBoxColliderCount = 0;
        DisableUnusedColliders(0);
    }

    private static bool TryResolveBuildRange(
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY,
        out int clampedStartY,
        out int height)
    {
        clampedStartY = 0;
        height = 0;

        if (!voxelData.IsCreated || blockMappings == null || blockMappings.Length == 0)
            return false;

        clampedStartY = Mathf.Clamp(startY, 0, Chunk.SizeY);
        int clampedEndY = Mathf.Clamp(endY, 0, Chunk.SizeY);
        height = clampedEndY - clampedStartY;
        return height > 0;
    }

    private void PrepareBuffers(int volume, out bool[] solids, out bool[] visited)
    {
        EnsureColliderBuffers(volume);
        solids = colliderSolidsBuffer;
        visited = colliderVisitedBuffer;
        Array.Clear(solids, 0, volume);
        Array.Clear(visited, 0, volume);
    }

    private void PrepareOccupancyBuffer(int volume, out ulong[] occupancyBits, out int occupancyWordCount)
    {
        occupancyWordCount = Mathf.Max(1, (volume + 63) >> 6);
        EnsureOccupancyBuffers(occupancyWordCount);
        occupancyBits = colliderOccupancyBuffer;
        Array.Clear(occupancyBits, 0, occupancyWordCount);
    }

    private static bool FillOccupancyBuffer(
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int clampedStartY,
        int height,
        ulong[] occupancyBits)
    {
        bool hasSolidBlocks = false;
        int plane = Chunk.SizeX * Chunk.SizeZ;

        for (int y = 0; y < height; y++)
        {
            int worldY = clampedStartY + y;
            int worldYBase = worldY * plane;
            int localYBase = y * Chunk.SizeX;

            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int worldZBase = z * Chunk.SizeX;
                int localZBase = z * Chunk.SizeX * height;

                for (int x = 0; x < Chunk.SizeX; x++)
                {
                    int worldIndex = worldYBase + worldZBase + x;
                    if (!IsBlockCollidable(voxelData[worldIndex], blockMappings))
                        continue;

                    int localIndex = x + localYBase + localZBase;
                    occupancyBits[localIndex >> 6] |= 1UL << (localIndex & 63);
                    hasSolidBlocks = true;
                }
            }
        }

        return hasSolidBlocks;
    }

    private static void FillSolidBufferFromOccupancy(ulong[] occupancyBits, int volume, bool[] solids)
    {
        for (int index = 0; index < volume; index++)
        {
            int wordIndex = index >> 6;
            if ((occupancyBits[wordIndex] & (1UL << (index & 63))) != 0)
                solids[index] = true;
        }
    }

    private int CreateGreedyColliders(
        GameObject owner,
        int clampedStartY,
        int height,
        bool[] solids,
        bool[] visited)
    {
        int colliderCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                for (int x = 0; x < Chunk.SizeX; x++)
                {
                    int startIndex = GetLocalIndex(x, y, z, Chunk.SizeX, height);
                    if (!solids[startIndex] || visited[startIndex])
                        continue;

                    int width = GetGreedyWidth(x, y, z, height, solids, visited);
                    int depth = GetGreedyDepth(x, y, z, width, height, solids, visited);
                    int boxHeight = GetGreedyHeight(x, y, z, width, depth, height, solids, visited);

                    MarkVisited(x, y, z, width, depth, boxHeight, height, visited);
                    ConfigureCollider(GetOrCreateBoxCollider(owner, colliderCount++), x, y, z, width, depth, boxHeight, clampedStartY);
                }
            }
        }

        return colliderCount;
    }

    private static int GetGreedyWidth(int x, int y, int z, int height, bool[] solids, bool[] visited)
    {
        int width = 1;
        while (x + width < Chunk.SizeX)
        {
            int index = GetLocalIndex(x + width, y, z, Chunk.SizeX, height);
            if (!solids[index] || visited[index])
                break;

            width++;
        }

        return width;
    }

    private static int GetGreedyDepth(int x, int y, int z, int width, int height, bool[] solids, bool[] visited)
    {
        int depth = 1;
        while (z + depth < Chunk.SizeZ && CanGrowDepth(x, y, z + depth, width, height, solids, visited))
            depth++;

        return depth;
    }

    private static bool CanGrowDepth(int x, int y, int z, int width, int height, bool[] solids, bool[] visited)
    {
        for (int ix = 0; ix < width; ix++)
        {
            int index = GetLocalIndex(x + ix, y, z, Chunk.SizeX, height);
            if (!solids[index] || visited[index])
                return false;
        }

        return true;
    }

    private static int GetGreedyHeight(int x, int y, int z, int width, int depth, int height, bool[] solids, bool[] visited)
    {
        int boxHeight = 1;
        while (y + boxHeight < height && CanGrowHeight(x, y + boxHeight, z, width, depth, height, solids, visited))
            boxHeight++;

        return boxHeight;
    }

    private static bool CanGrowHeight(int x, int y, int z, int width, int depth, int height, bool[] solids, bool[] visited)
    {
        for (int iz = 0; iz < depth; iz++)
        {
            for (int ix = 0; ix < width; ix++)
            {
                int index = GetLocalIndex(x + ix, y, z + iz, Chunk.SizeX, height);
                if (!solids[index] || visited[index])
                    return false;
            }
        }

        return true;
    }

    private static void MarkVisited(int x, int y, int z, int width, int depth, int boxHeight, int height, bool[] visited)
    {
        for (int iy = 0; iy < boxHeight; iy++)
        {
            for (int iz = 0; iz < depth; iz++)
            {
                for (int ix = 0; ix < width; ix++)
                {
                    int index = GetLocalIndex(x + ix, y + iy, z + iz, Chunk.SizeX, height);
                    visited[index] = true;
                }
            }
        }
    }

    private static void ConfigureCollider(BoxCollider box, int x, int y, int z, int width, int depth, int boxHeight, int clampedStartY)
    {
        box.center = new Vector3(
            x + width * 0.5f,
            clampedStartY + y + boxHeight * 0.5f,
            z + depth * 0.5f);
        box.size = new Vector3(width, boxHeight, depth);
        box.enabled = true;
    }

    private BoxCollider GetOrCreateBoxCollider(GameObject owner, int index)
    {
        if (index < boxColliders.Count && boxColliders[index] != null)
            return boxColliders[index];

        BoxCollider created = owner.AddComponent<BoxCollider>();
        created.isTrigger = false;

        if (index < boxColliders.Count)
            boxColliders[index] = created;
        else
            boxColliders.Add(created);

        return created;
    }

    private void DisableUnusedColliders(int startIndex)
    {
        for (int i = startIndex; i < boxColliders.Count; i++)
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

    private bool TryRestoreCachedColliders(
        GameObject owner,
        int clampedStartY,
        int height,
        ulong[] occupancyBits,
        int occupancyWordCount,
        out bool hasColliders)
    {
        hasColliders = false;

        if (!hasCachedColliderLayout ||
            cachedStartY != clampedStartY ||
            cachedHeight != height ||
            cachedColliderOccupancyWordCount != occupancyWordCount ||
            !OccupancyMatchesCache(occupancyBits, occupancyWordCount))
        {
            return false;
        }

        if (cachedColliderCount <= 0)
        {
            activeBoxColliderCount = 0;
            DisableUnusedColliders(0);
            return true;
        }

        if (!CanRestoreCachedColliderComponents(cachedColliderCount))
            return false;

        for (int i = 0; i < cachedColliderCount; i++)
            boxColliders[i].enabled = true;

        activeBoxColliderCount = cachedColliderCount;
        DisableUnusedColliders(cachedColliderCount);
        hasColliders = true;
        return true;
    }

    private bool OccupancyMatchesCache(ulong[] occupancyBits, int occupancyWordCount)
    {
        for (int i = 0; i < occupancyWordCount; i++)
        {
            if (cachedColliderOccupancyBits[i] != occupancyBits[i])
                return false;
        }

        return true;
    }

    private bool CanRestoreCachedColliderComponents(int colliderCount)
    {
        if (boxColliders.Count < colliderCount)
            return false;

        for (int i = 0; i < colliderCount; i++)
        {
            if (boxColliders[i] == null)
                return false;
        }

        return true;
    }

    private void CacheColliderLayout(
        int clampedStartY,
        int height,
        ulong[] occupancyBits,
        int occupancyWordCount,
        int colliderCount)
    {
        EnsureCachedOccupancyBuffer(occupancyWordCount);
        Array.Copy(occupancyBits, cachedColliderOccupancyBits, occupancyWordCount);
        cachedColliderOccupancyWordCount = occupancyWordCount;
        cachedColliderCount = colliderCount;
        cachedStartY = clampedStartY;
        cachedHeight = height;
        hasCachedColliderLayout = true;
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

    private void EnsureOccupancyBuffers(int occupancyWordCount)
    {
        if (occupancyWordCount <= 0)
            return;

        if (colliderOccupancyBuffer == null || colliderOccupancyBuffer.Length < occupancyWordCount)
            colliderOccupancyBuffer = new ulong[occupancyWordCount];
    }

    private void EnsureCachedOccupancyBuffer(int occupancyWordCount)
    {
        if (occupancyWordCount <= 0)
            return;

        if (cachedColliderOccupancyBits == null || cachedColliderOccupancyBits.Length < occupancyWordCount)
            cachedColliderOccupancyBits = new ulong[occupancyWordCount];
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
