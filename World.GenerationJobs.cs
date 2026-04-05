using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    private struct PendingMesh
    {
        public JobHandle handle;
        public NativeList<MeshGenerator.PackedChunkVertex> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
        public Vector2Int coord;
        public int expectedGen;
        public Chunk parentChunk;
        public NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges;
        public NativeArray<ulong> subchunkVisibilityMasks;
        public int dirtySubchunkMask;
        public int visualSliceIndex;
        public NativeArray<int> heightCache;
        public NativeArray<byte> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public NativeArray<int3> suppressedBillboards;
        public bool buildColliders;
    }

    private struct PendingData
    {
        public JobHandle handle;
        public NativeArray<int> heightCache;
        public NativeArray<byte> blockTypes;
        public NativeArray<byte> knownVoxelData;
        public bool useKnownVoxelData;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public int borderSize;
        public Chunk chunk;
        public Vector2Int coord;
        public int expectedGen;
        public NativeArray<byte> chunkLightData;
        public NativeArray<byte> lightOpacityData;
        public NativeArray<BlockEdit> edits;
        public NativeArray<byte> fastRebuildSnapshotVoxelData;
        public NativeArray<byte> fastRebuildSnapshotLoadedChunks;
        public NativeArray<BlockEdit> fastRebuildOverrides;
        public NativeArray<BlockEdit> postCompletionOverrides;
        public NativeArray<byte> postCompletionDirtyColumns;
        public NativeArray<ulong> subchunkColliderOccupancy;
        public NativeArray<bool> subchunkNonEmpty;
        public int dirtySubchunkMask;
        public bool rebuildColliders;
        public bool postOverrideRefreshScheduled;
    }

    private struct PendingColliderBuild
    {
        public Vector2Int coord;
        public int expectedGen;
        public int subchunkIndex;
    }

    private const int FastRebuildChunkVoxelCount = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;

    [BurstCompile]
    private struct FastRebuildPopulateBlocksJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> snapshotVoxelData;
        [ReadOnly] public NativeArray<byte> snapshotLoadedChunks;
        public NativeArray<byte> blockTypes;
        public NativeArray<byte> knownVoxelData;
        public int borderSize;
        public int voxelSizeX;
        public int voxelPlaneSize;
        public int snapshotChunkRadius;
        public int snapshotChunkDiameter;

        public void Execute(int index)
        {
            int x = index % voxelSizeX;
            int temp = index / voxelSizeX;
            int y = temp % Chunk.SizeY;
            int z = temp / Chunk.SizeY;

            int relX = x - borderSize;
            int relZ = z - borderSize;
            int chunkOffsetX = FloorDiv(relX, Chunk.SizeX);
            int chunkOffsetZ = FloorDiv(relZ, Chunk.SizeZ);
            int localX = relX - chunkOffsetX * Chunk.SizeX;
            int localZ = relZ - chunkOffsetZ * Chunk.SizeZ;

            int slotX = chunkOffsetX + snapshotChunkRadius;
            int slotZ = chunkOffsetZ + snapshotChunkRadius;
            if (slotX < 0 || slotX >= snapshotChunkDiameter || slotZ < 0 || slotZ >= snapshotChunkDiameter)
            {
                knownVoxelData[index] = 0;
                blockTypes[index] = y <= 2 ? (byte)BlockType.Bedrock : (byte)BlockType.Air;
                return;
            }

            int slot = slotX + slotZ * snapshotChunkDiameter;
            if (snapshotLoadedChunks[slot] == 0)
            {
                knownVoxelData[index] = 0;
                blockTypes[index] = y <= 2 ? (byte)BlockType.Bedrock : (byte)BlockType.Air;
                return;
            }

            int srcIndex = slot * FastRebuildChunkVoxelCount + localX + localZ * Chunk.SizeX + y * Chunk.SizeX * Chunk.SizeZ;
            knownVoxelData[index] = 1;
            blockTypes[index] = snapshotVoxelData[srcIndex];
        }

        private static int FloorDiv(int value, int divisor)
        {
            if (value >= 0)
                return value / divisor;

            return -((-value + divisor - 1) / divisor);
        }
    }

    [BurstCompile]
    private struct FastRebuildApplyBlockOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        public NativeArray<byte> blockTypes;
        public NativeArray<byte> knownVoxelData;
        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int index = 0; index < overrides.Length; index++)
            {
                BlockEdit edit = overrides[index];
                if (edit.y < 0 || edit.y >= Chunk.SizeY)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                blockTypes[dstIndex] = (byte)math.clamp(edit.type, 0, byte.MaxValue);
                if (knownVoxelData.IsCreated)
                    knownVoxelData[dstIndex] = 1;
            }
        }
    }

    [BurstCompile]
    private struct PostApplyCurrentOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        public NativeArray<byte> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<int> heightCache;
        public NativeArray<bool> subchunkNonEmpty;
        public NativeArray<ulong> subchunkColliderOccupancy;
        public NativeArray<byte> dirtyColumns;
        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            if (!overrides.IsCreated || overrides.Length == 0 || !blockMappings.IsCreated || blockMappings.Length == 0)
                return;

            bool recomputeSubchunkNonEmpty = false;
            int maxBlockIndex = blockMappings.Length - 1;

            for (int i = 0; i < overrides.Length; i++)
            {
                BlockEdit edit = overrides[i];
                if (edit.y < 0 || edit.y >= Chunk.SizeY)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                byte blockId = (byte)math.clamp(edit.type, 0, maxBlockIndex);
                int voxelIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                blockTypes[voxelIndex] = blockId;
                solids[voxelIndex] = blockMappings[blockId].isSolid;
                dirtyColumns[ix + iz * voxelSizeX] = 1;

                int localX = edit.x - chunkMinX;
                int localZ = edit.z - chunkMinZ;
                if (localX < 0 || localX >= Chunk.SizeX || localZ < 0 || localZ >= Chunk.SizeZ)
                    continue;

                recomputeSubchunkNonEmpty = true;
            }

            for (int columnIndex = 0; columnIndex < dirtyColumns.Length; columnIndex++)
            {
                if (dirtyColumns[columnIndex] == 0)
                    continue;

                int ix = columnIndex % voxelSizeX;
                int iz = columnIndex / voxelSizeX;
                int highestSolidY = 0;
                int voxelIndex = ix + iz * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++, voxelIndex += voxelSizeX)
                {
                    if (solids[voxelIndex])
                        highestSolidY = y;
                }

                heightCache[ix + iz * voxelSizeX] = highestSolidY;
            }

            if (!recomputeSubchunkNonEmpty)
                return;

            for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
                subchunkNonEmpty[sub] = false;

            for (int i = 0; i < subchunkColliderOccupancy.Length; i++)
                subchunkColliderOccupancy[i] = 0UL;

            for (int localZ = 0; localZ < Chunk.SizeZ; localZ++)
            {
                int iz = localZ + borderSize;
                for (int localX = 0; localX < Chunk.SizeX; localX++)
                {
                    int ix = localX + borderSize;
                    int voxelIndex = ix + iz * voxelPlaneSize;
                    for (int y = 0; y < Chunk.SizeY; y++, voxelIndex += voxelSizeX)
                    {
                        BlockType blockType = (BlockType)blockTypes[voxelIndex];
                        if (blockType == BlockType.Air)
                            continue;

                        subchunkNonEmpty[y / Chunk.SubchunkHeight] = true;
                        if (IsBlockCollidable(blockType))
                        {
                            int subchunkIndex = y / Chunk.SubchunkHeight;
                            int localY = y - subchunkIndex * Chunk.SubchunkHeight;
                            int localIndex = localX + localY * Chunk.SizeX + localZ * Chunk.SizeX * Chunk.SubchunkHeight;
                            int wordIndex = subchunkIndex * Chunk.ColliderOccupancyWordsPerSubchunk + (localIndex >> 6);
                            subchunkColliderOccupancy[wordIndex] |= 1UL << (localIndex & 63);
                        }
                    }
                }
            }
        }

        private bool IsBlockCollidable(BlockType blockType)
        {
            if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
                return false;
            if (TorchPlacementUtility.IsTorchLike(blockType))
                return false;

            int mapIndex = (int)blockType;
            if (mapIndex < 0 || mapIndex >= blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            return mapping.isSolid && !mapping.isEmpty;
        }
    }

    [BurstCompile]
    private struct FastRebuildDerivedDataJob : IJob
    {
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        public NativeArray<bool> solids;
        public NativeArray<int> heightCache;
        public NativeArray<bool> subchunkNonEmpty;
        public NativeArray<ulong> subchunkColliderOccupancy;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
                subchunkNonEmpty[s] = false;

            for (int i = 0; i < subchunkColliderOccupancy.Length; i++)
                subchunkColliderOccupancy[i] = 0UL;

            for (int iz = 0; iz < voxelSizeZ; iz++)
            {
                int relZ = iz - borderSize;
                for (int ix = 0; ix < voxelSizeX; ix++)
                {
                    int relX = ix - borderSize;
                    int highestSolidY = 0;

                    for (int y = 0; y < Chunk.SizeY; y++)
                    {
                        int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                        BlockType blockType = (BlockType)blockTypes[idx];
                        bool isSolid = blockMappings[(int)blockType].isSolid;
                        solids[idx] = isSolid;
                        if (isSolid)
                            highestSolidY = y;

                        if (relX >= 0 && relX < Chunk.SizeX &&
                            relZ >= 0 && relZ < Chunk.SizeZ &&
                            blockType != BlockType.Air)
                        {
                            int subIdx = y / Chunk.SubchunkHeight;
                            if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            {
                                subchunkNonEmpty[subIdx] = true;
                                if (IsBlockCollidable(blockType))
                                {
                                    int localY = y - subIdx * Chunk.SubchunkHeight;
                                    int localIndex = relX + localY * Chunk.SizeX + relZ * Chunk.SizeX * Chunk.SubchunkHeight;
                                    int wordIndex = subIdx * Chunk.ColliderOccupancyWordsPerSubchunk + (localIndex >> 6);
                                    subchunkColliderOccupancy[wordIndex] |= 1UL << (localIndex & 63);
                                }
                            }
                        }
                    }

                    heightCache[ix + iz * voxelSizeX] = highestSolidY;
                }
            }
        }

        private bool IsBlockCollidable(BlockType blockType)
        {
            if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
                return false;
            if (TorchPlacementUtility.IsTorchLike(blockType))
                return false;

            int mapIndex = (int)blockType;
            if (mapIndex < 0 || mapIndex >= blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            return mapping.isSolid && !mapping.isEmpty;
        }
    }

    [BurstCompile]
    private struct FastRebuildPopulateOpacityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> snapshotVoxelData;
        [ReadOnly] public NativeArray<byte> snapshotLoadedChunks;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;
        public NativeArray<byte> opacity;
        public int borderSize;
        public int voxelSizeX;
        public int snapshotChunkRadius;
        public int snapshotChunkDiameter;

        public void Execute(int index)
        {
            int x = index % voxelSizeX;
            int temp = index / voxelSizeX;
            int y = temp % Chunk.SizeY;
            int z = temp / Chunk.SizeY;

            int relX = x - borderSize;
            int relZ = z - borderSize;
            int chunkOffsetX = FloorDiv(relX, Chunk.SizeX);
            int chunkOffsetZ = FloorDiv(relZ, Chunk.SizeZ);
            int localX = relX - chunkOffsetX * Chunk.SizeX;
            int localZ = relZ - chunkOffsetZ * Chunk.SizeZ;

            int slotX = chunkOffsetX + snapshotChunkRadius;
            int slotZ = chunkOffsetZ + snapshotChunkRadius;
            if (slotX < 0 || slotX >= snapshotChunkDiameter || slotZ < 0 || slotZ >= snapshotChunkDiameter)
            {
                opacity[index] = effectiveOpacityByBlock[(int)(y <= 2 ? BlockType.Bedrock : BlockType.Air)];
                return;
            }

            int slot = slotX + slotZ * snapshotChunkDiameter;
            if (snapshotLoadedChunks[slot] == 0)
            {
                opacity[index] = effectiveOpacityByBlock[(int)(y <= 2 ? BlockType.Bedrock : BlockType.Air)];
                return;
            }

            int srcIndex = slot * FastRebuildChunkVoxelCount + localX + localZ * Chunk.SizeX + y * Chunk.SizeX * Chunk.SizeZ;
            opacity[index] = effectiveOpacityByBlock[snapshotVoxelData[srcIndex]];
        }

        private static int FloorDiv(int value, int divisor)
        {
            if (value >= 0)
                return value / divisor;

            return -((-value + divisor - 1) / divisor);
        }
    }

    [BurstCompile]
    private struct FastRebuildApplyOpacityOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;
        public NativeArray<byte> opacity;
        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int index = 0; index < overrides.Length; index++)
            {
                BlockEdit edit = overrides[index];
                if (edit.y < 0 || edit.y >= Chunk.SizeY)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                opacity[dstIndex] = effectiveOpacityByBlock[edit.type];
            }
        }
    }
}
