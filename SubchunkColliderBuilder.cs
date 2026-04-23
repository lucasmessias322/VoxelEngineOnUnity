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
    private bool cachedColliderLayoutHasCustomShapes;

    public bool TryBuild(
        GameObject owner,
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        BlockModelCuboid[] blockModelCuboids,
        int startY,
        int endY)
    {
        if (!TryResolveBuildRange(voxelData, blockMappings, startY, endY, out int clampedStartY, out int height))
        {
            Clear();
            return false;
        }

        int volume = Chunk.SizeX * height * Chunk.SizeZ;
        PrepareBuffers(volume, out bool[] solids, out bool[] visited);
        int occupancyWordCount = Chunk.ColliderOccupancyWordsPerSubchunk;
        EnsureOccupancyBuffers(occupancyWordCount);
        Array.Clear(colliderOccupancyBuffer, 0, occupancyWordCount);
        FillCubeSolidBufferAndOccupancy(voxelData, blockMappings, clampedStartY, height, solids, colliderOccupancyBuffer);

        int cubeColliderCount = CreateGreedyColliders(owner, clampedStartY, height, solids, visited);
        int colliderCount = cubeColliderCount;
        colliderCount = AppendCustomShapeColliders(owner, voxelData, blockMappings, blockModelCuboids, clampedStartY, height, colliderCount);
        activeBoxColliderCount = colliderCount;
        DisableUnusedColliders(colliderCount);
        CacheColliderLayout(
            clampedStartY,
            height,
            colliderOccupancyBuffer,
            0,
            occupancyWordCount,
            colliderCount,
            colliderCount > cubeColliderCount);
        return colliderCount > 0;
    }

    public bool TryBuild(
        GameObject owner,
        ulong[] occupancyBits,
        int occupancyWordOffset,
        int occupancyWordCount,
        int startY,
        int endY)
    {
        if (!TryResolveBuildRange(startY, endY, out int clampedStartY, out int height) ||
            occupancyBits == null ||
            occupancyWordOffset < 0 ||
            occupancyWordCount <= 0 ||
            occupancyWordOffset + occupancyWordCount > occupancyBits.Length)
        {
            Clear();
            return false;
        }

        Clear();
        return false;
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

    public void PrewarmBoxColliders(GameObject owner, int targetColliderCount)
    {
        if (owner == null || targetColliderCount <= 0)
            return;

        for (int i = 0; i < targetColliderCount; i++)
        {
            BoxCollider box = GetOrCreateBoxCollider(owner, i);
            if (box != null)
                box.enabled = false;
        }
    }

    public void Clear()
    {
        activeBoxColliderCount = 0;
        DisableUnusedColliders(0);
    }

    public bool TryRestoreCachedColliders(
        ulong[] occupancyBits,
        int occupancyWordOffset,
        int occupancyWordCount,
        int startY,
        int endY,
        out bool hasColliders)
    {
        hasColliders = false;

        if (!TryResolveBuildRange(startY, endY, out int clampedStartY, out int height) ||
            occupancyBits == null ||
            occupancyWordOffset < 0 ||
            occupancyWordCount <= 0 ||
            occupancyWordOffset + occupancyWordCount > occupancyBits.Length ||
            cachedColliderLayoutHasCustomShapes)
        {
            return false;
        }

        return TryRestoreCachedColliderLayoutInternal(
            occupancyBits,
            occupancyWordOffset,
            occupancyWordCount,
            clampedStartY,
            height,
            out hasColliders);
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

    private static bool TryResolveBuildRange(
        int startY,
        int endY,
        out int clampedStartY,
        out int height)
    {
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

    private static void FillCubeSolidBufferAndOccupancy(
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int clampedStartY,
        int height,
        bool[] solids,
        ulong[] occupancyBuffer)
    {
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
                    if (!TryGetCollidableMapping((BlockType)voxelData[worldIndex], blockMappings, out BlockTextureMapping mapping))
                        continue;

                    int localIndex = x + localYBase + localZBase;
                    occupancyBuffer[localIndex >> 6] |= 1UL << (localIndex & 63);

                    if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
                        solids[localIndex] = true;
                }
            }
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

    private static bool HasAnyOccupiedWord(ulong[] occupancyBits, int occupancyWordOffset, int occupancyWordCount)
    {
        for (int i = 0; i < occupancyWordCount; i++)
        {
            if (occupancyBits[occupancyWordOffset + i] != 0UL)
                return true;
        }

        return false;
    }

    private bool TryRestoreCachedColliderLayoutInternal(
        ulong[] occupancyBits,
        int occupancyWordOffset,
        int occupancyWordCount,
        int clampedStartY,
        int height,
        out bool hasColliders)
    {
        hasColliders = false;

        if (!hasCachedColliderLayout ||
            cachedStartY != clampedStartY ||
            cachedHeight != height ||
            cachedColliderOccupancyWordCount != occupancyWordCount ||
            !OccupancyMatchesCache(occupancyBits, occupancyWordOffset, occupancyWordCount))
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

    private bool OccupancyMatchesCache(ulong[] occupancyBits, int occupancyWordOffset, int occupancyWordCount)
    {
        for (int i = 0; i < occupancyWordCount; i++)
        {
            if (cachedColliderOccupancyBits[i] != occupancyBits[occupancyWordOffset + i])
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
        int occupancyWordOffset,
        int occupancyWordCount,
        int colliderCount,
        bool hasCustomShapeColliders)
    {
        EnsureCachedOccupancyBuffer(occupancyWordCount);
        Array.Copy(occupancyBits, occupancyWordOffset, cachedColliderOccupancyBits, 0, occupancyWordCount);
        cachedColliderOccupancyWordCount = occupancyWordCount;
        cachedColliderCount = colliderCount;
        cachedStartY = clampedStartY;
        cachedHeight = height;
        cachedColliderLayoutHasCustomShapes = hasCustomShapeColliders;
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

    private int AppendCustomShapeColliders(
        GameObject owner,
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        BlockModelCuboid[] blockModelCuboids,
        int clampedStartY,
        int height,
        int colliderCount)
    {
        World world = World.Instance;
        Chunk ownerChunk = owner != null ? owner.GetComponent<Chunk>() : null;
        Vector2Int chunkCoord = ownerChunk != null ? ownerChunk.coord : Vector2Int.zero;
        int plane = Chunk.SizeX * Chunk.SizeZ;

        for (int y = 0; y < height; y++)
        {
            int worldY = clampedStartY + y;
            int worldYBase = worldY * plane;

            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int worldZBase = z * Chunk.SizeX;
                for (int x = 0; x < Chunk.SizeX; x++)
                {
                    int worldIndex = worldYBase + worldZBase + x;
                    BlockType blockType = (BlockType)voxelData[worldIndex];
                    if (!TryGetCollidableMapping(blockType, blockMappings, out BlockTextureMapping mapping))
                        continue;

                    BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
                    if (shape == BlockRenderShape.Cube)
                        continue;

                    Vector3Int localBlockPos = new Vector3Int(x, worldY, z);
                    Vector3Int worldPos = new Vector3Int(
                        chunkCoord.x * Chunk.SizeX + x,
                        worldY,
                        chunkCoord.y * Chunk.SizeZ + z);

                    switch (shape)
                    {
                        case BlockRenderShape.Cuboid:
                            BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
                            colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, new ShapeBox(min, max));
                            break;

                        case BlockRenderShape.MultiCuboid:
                        {
                            BlockPlacementAxis multiAxis = world != null ? world.GetPlacementAxisAt(worldPos, blockType) : BlockPlacementAxis.Y;
                            int boxCount = BlockShapeUtility.GetMultiCuboidBoxCount(mapping, blockModelCuboids);
                            if (boxCount <= 0)
                            {
                                BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, new ShapeBox(fallbackMin, fallbackMax));
                                break;
                            }

                            for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
                            {
                                if (BlockShapeUtility.TryGetMultiCuboidBox(mapping, blockModelCuboids, boxIndex, multiAxis, blockType, out ShapeBox box))
                                    colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box);
                            }

                            break;
                        }

                        case BlockRenderShape.Stairs:
                        {
                            BlockPlacementAxis rawState = world != null ? world.GetPlacementAxisAt(worldPos, blockType) : BlockPlacementAxis.Y;
                            StairShapeVariant variant = StairShapeRuntimeUtility.ResolveShapeVariant(world, worldPos, (byte)rawState);
                            StairShapeUtility.ResolveBoxes((byte)rawState, variant, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
                            colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box0);
                            if (boxCount > 1) colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box1);
                            if (boxCount > 2) colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box2);
                            if (boxCount > 3) colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box3);
                            if (boxCount > 4) colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box4);
                            break;
                        }

                        case BlockRenderShape.Ramp:
                        {
                            BlockPlacementAxis rampAxis = world != null ? world.GetPlacementAxisAt(worldPos, blockType) : BlockPlacementAxis.Z;
                            RampShapeVariant rampVariant = RampShapeRuntimeUtility.ResolveShapeVariant(world, worldPos, rampAxis);
                            FixedList512Bytes<ShapeBox> rampBoxes = RampShapeUtility.BuildColliderBoxes(rampAxis, rampVariant);
                            for (int i = 0; i < rampBoxes.Length; i++)
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, rampBoxes[i]);
                            break;
                        }

                        case BlockRenderShape.VerticalRamp:
                        {
                            BlockPlacementAxis verticalRampAxis = world != null ? world.GetPlacementAxisAt(worldPos, blockType) : BlockPlacementAxis.Z;
                            FixedList512Bytes<ShapeBox> verticalRampBoxes = VerticalRampShapeUtility.BuildColliderBoxes(verticalRampAxis);
                            for (int i = 0; i < verticalRampBoxes.Length; i++)
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, verticalRampBoxes[i]);
                            break;
                        }

                        case BlockRenderShape.Fence:
                        {
                            byte connectionMask = FenceShapeUtility.ResolveConnectionMask(world, worldPos);
                            colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, FenceShapeUtility.GetCenterPostColliderBox());
                            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectWest));
                            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectEast));
                            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectSouth));
                            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
                                colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectNorth));
                            break;
                        }

                        default:
                        {
                            Bounds bounds = BlockShapeUtility.GetWorldBounds(localBlockPos, blockType, mapping, BlockPlacementAxis.Y);
                            ShapeBox box = new ShapeBox(bounds.min - (Vector3)localBlockPos, bounds.max - (Vector3)localBlockPos);
                            colliderCount = AddShapeColliderBox(owner, colliderCount, localBlockPos, box);
                            break;
                        }
                    }
                }
            }
        }

        return colliderCount;
    }

    private int AddShapeColliderBox(GameObject owner, int colliderIndex, Vector3Int blockPos, ShapeBox box)
    {
        BoxCollider collider = GetOrCreateBoxCollider(owner, colliderIndex);
        collider.center = (Vector3)blockPos + (box.min + box.max) * 0.5f;
        collider.size = box.max - box.min;
        collider.enabled = true;
        return colliderIndex + 1;
    }

    private static bool IsBlockCollidable(byte blockId, BlockTextureMapping[] blockMappings)
    {
        return TryGetCollidableMapping((BlockType)blockId, blockMappings, out _);
    }

    private static bool IsFullCubeCollidable(byte blockId, BlockTextureMapping[] blockMappings)
    {
        if (!TryGetCollidableMapping((BlockType)blockId, blockMappings, out BlockTextureMapping mapping))
            return false;

        return BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube;
    }

    private static bool TryGetCollidableMapping(BlockType blockType, BlockTextureMapping[] blockMappings, out BlockTextureMapping mapping)
    {
        mapping = default;
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType) || TorchPlacementUtility.IsTorchLike(blockType))
            return false;

        int mapIndex = (int)blockType;
        if (mapIndex < 0 || mapIndex >= blockMappings.Length)
            return false;

        mapping = blockMappings[mapIndex];
        return mapping.isSolid && !mapping.isEmpty;
    }
}
