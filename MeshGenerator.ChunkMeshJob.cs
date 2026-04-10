using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static partial class MeshGenerator
{
    // =========================================================================
    // JOB 2: CHUNK MESH JOB (Greedy Meshing e Arrays Visuais)
    // =========================================================================
    [BurstCompile]
    private partial struct ChunkMeshJob : IJob
    {
        // DeallocateOnJobCompletion limpa todos estes arrays criados no Schedule.
        [ReadOnly] public NativeArray<int> heightCache;
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<byte> blockPlacementAxes;
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> light;
        [ReadOnly] public NativeArray<int3> suppressedGrassBillboards;
        [ReadOnly] public NativeArray<VegetationBillboardRuleData> vegetationBillboardRules;
        [ReadOnly] public NativeArray<bool> subchunkNonEmpty;
        [ReadOnly] public NativeArray<byte> knownVoxelData;
        public bool useKnownVoxelData;

        public int border;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public int chunkCoordX;
        public int chunkCoordZ;
        public bool enableGrassBillboards;
        public float grassBillboardChance;
        public BlockType grassBillboardBlockType;
        public float grassBillboardHeight;
        public float grassBillboardNoiseScale;
        public float grassBillboardJitter;
        public BiomeNoiseSettings biomeNoiseSettings;
        public float aoStrength;
        public float aoCurveExponent;
        public float aoMinLight;
        public bool useFastBedrockStyleMeshing;
        public bool enableHighQualityLeafFoliage;
        public bool enableUltraLeafBillboards;
        public float leafFoliageSpawnChance;
        public float leafFoliageHeightMin;
        public float leafFoliageHeightMax;
        public float leafFoliageHalfWidthMin;
        public float leafFoliageHalfWidthMax;
        public float leafFoliageBaseYOffsetMin;
        public float leafFoliageBaseYOffsetMax;
        public float leafFoliageCenterJitter;
        public float leafUltraBillboardHeight;
        public float leafUltraBillboardHalfWidth;
        public float leafUltraBaseYOffset;
        public float leafUltraCenterJitter;
        public float leafUltraRotationOffsetDegrees;
        public float leafUltraRotationRandomDegrees;
        public float leafUltraFaceTiltDegrees;
        public float leafUltraFaceTiltRandomDegrees;
        public int dirtySubchunkMask;
        public NativeArray<SubchunkMeshRange> subchunkRanges;

        // LIMITES DO SUBCHUNK
        public int startY;
        public int endY;

        public NativeList<PackedChunkVertex> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
        public NativeArray<ulong> subchunkVisibilityMasks;
        private int currentSubchunkVertexStart;

        private struct GreedyFaceData
        {
            public byte blockId;
            public byte placementAxis;
            public byte valid;
            public byte faceLight;
            public byte surfaceHeight;
            public byte ao0;
            public byte ao1;
            public byte ao2;
            public byte ao3;
            public byte light0;
            public byte light1;
            public byte light2;
            public byte light3;
        }

        public void Execute()
        {
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;
            NativeArray<byte> occlusionState = new NativeArray<byte>(4096, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> occlusionQueue = new NativeArray<int>(4096, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            try
            {
                for (int sub = 0; sub < SubchunksPerColumn; sub++)
                {
                    if ((dirtySubchunkMask & (1 << sub)) == 0)
                    {
                        subchunkRanges[sub] = default;
                        continue;
                    }

                    startY = sub * Chunk.SubchunkHeight;
                    endY = math.min(startY + Chunk.SubchunkHeight, SizeY);

                    if (!subchunkNonEmpty[sub])
                    {
                        subchunkVisibilityMasks[sub] = SubchunkOcclusion.AllVisibleMask;
                        subchunkRanges[sub] = default;
                        continue;
                    }

                    SubchunkMeshRange range = new SubchunkMeshRange
                    {
                        vertexStart = vertices.Length,
                        opaqueStart = opaqueTriangles.Length,
                        transparentStart = transparentTriangles.Length,
                        billboardStart = billboardTriangles.Length,
                        waterStart = waterTriangles.Length
                    };
                    currentSubchunkVertexStart = range.vertexStart;

                    subchunkVisibilityMasks[sub] = ComputeVisibilityMask(occlusionState, occlusionQueue);

                    GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);
                    GenerateDecorativeMeshes(blockTypes, light, invAtlasTilesX, invAtlasTilesY);

                    range.vertexCount = vertices.Length - range.vertexStart;
                    range.opaqueCount = opaqueTriangles.Length - range.opaqueStart;
                    range.transparentCount = transparentTriangles.Length - range.transparentStart;
                    range.billboardCount = billboardTriangles.Length - range.billboardStart;
                    range.waterCount = waterTriangles.Length - range.waterStart;
                    subchunkRanges[sub] = range;
                }
            }
            finally
            {
                if (occlusionState.IsCreated) occlusionState.Dispose();
                if (occlusionQueue.IsCreated) occlusionQueue.Dispose();
            }
        }

        private int GetCurrentSubchunkLocalVertexIndex()
        {
            return vertices.Length - currentSubchunkVertexStart;
        }

        private void AddPackedVertex(Vector3 position, Vector3 normal, Vector2 uv0, Vector2 uv1, Vector4 uv2)
        {
            vertices.Add(new PackedChunkVertex
            {
                position = position,
                normal = normal,
                uv0 = uv0,
                uv1 = uv1,
                uv2 = uv2
            });
        }

        private ulong ComputeVisibilityMask(NativeArray<byte> occlusionState, NativeArray<int> queue)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int opaqueCount = 0;

            for (int localY = 0; localY < Chunk.SubchunkHeight; localY++)
            {
                int worldY = startY + localY;
                for (int localZ = 0; localZ < SizeZ; localZ++)
                {
                    int sampleZ = localZ + border;
                    for (int localX = 0; localX < SizeX; localX++)
                    {
                        int sampleX = localX + border;
                        int sampleIndex = sampleX + worldY * voxelSizeX + sampleZ * voxelPlaneSize;
                        int visIndex = localX | (localY << 8) | (localZ << 4);

                        if (IsOcclusionOpaque((BlockType)blockTypes[sampleIndex]))
                        {
                            occlusionState[visIndex] = 1;
                            opaqueCount++;
                        }
                        else
                        {
                            occlusionState[visIndex] = 0;
                        }
                    }
                }
            }

            if (opaqueCount == 4096)
                return 0UL;

            ulong visibilityMask = 0UL;
            for (int localY = 0; localY < Chunk.SubchunkHeight; localY++)
            {
                for (int localZ = 0; localZ < SizeZ; localZ++)
                {
                    for (int localX = 0; localX < SizeX; localX++)
                    {
                        bool isBoundary = localX == 0 || localX == SizeX - 1 ||
                                          localY == 0 || localY == Chunk.SubchunkHeight - 1 ||
                                          localZ == 0 || localZ == SizeZ - 1;
                        if (!isBoundary)
                            continue;

                        int startIndex = localX | (localY << 8) | (localZ << 4);
                        if (occlusionState[startIndex] != 0)
                            continue;

                        visibilityMask = FloodFillVisibilityMask(startIndex, occlusionState, queue, visibilityMask);
                    }
                }
            }

            return visibilityMask;
        }

        private ulong FloodFillVisibilityMask(int startIndex, NativeArray<byte> occlusionState, NativeArray<int> queue, ulong visibilityMask)
        {
            int head = 0;
            int tail = 0;
            byte faceMask = 0;

            queue[tail++] = startIndex;
            occlusionState[startIndex] = 1;

            while (head < tail)
            {
                int index = queue[head++];
                AddOcclusionEdges(index, ref faceMask);

                for (int face = 0; face < SubchunkOcclusion.FaceCount; face++)
                {
                    int neighborIndex = GetNeighborIndexAtFace(index, face);
                    if (neighborIndex >= 0 && occlusionState[neighborIndex] == 0)
                    {
                        occlusionState[neighborIndex] = 1;
                        queue[tail++] = neighborIndex;
                    }
                }
            }

            return SubchunkOcclusion.AddFaceSet(visibilityMask, faceMask);
        }

        private static void AddOcclusionEdges(int index, ref byte faceMask)
        {
            int x = index & 15;
            if (x == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.West));
            else if (x == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.East));

            int y = (index >> 8) & 15;
            if (y == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.Down));
            else if (y == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.Up));

            int z = (index >> 4) & 15;
            if (z == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.North));
            else if (z == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.South));
        }

        private static int GetNeighborIndexAtFace(int index, int face)
        {
            switch (face)
            {
                case SubchunkOcclusion.Down:
                    return ((index >> 8) & 15) == 0 ? -1 : index - 256;
                case SubchunkOcclusion.Up:
                    return ((index >> 8) & 15) == 15 ? -1 : index + 256;
                case SubchunkOcclusion.North:
                    return ((index >> 4) & 15) == 0 ? -1 : index - 16;
                case SubchunkOcclusion.South:
                    return ((index >> 4) & 15) == 15 ? -1 : index + 16;
                case SubchunkOcclusion.West:
                    return (index & 15) == 0 ? -1 : index - 1;
                case SubchunkOcclusion.East:
                    return (index & 15) == 15 ? -1 : index + 1;
                default:
                    return -1;
            }
        }

        private bool IsOcclusionOpaque(BlockType blockType)
        {
            if (blockType == BlockType.Air)
                return false;

            int blockIndex = (int)blockType;
            if (blockIndex < 0 || blockIndex >= blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[blockIndex];
            if (mapping.isEmpty || mapping.isTransparent || mapping.isLiquid)
                return false;

            return mapping.isSolid && BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube;
        }

        private byte GetBlockPlacementAxisValue(int voxelIndex)
        {
            if (!blockPlacementAxes.IsCreated)
                return (byte)BlockPlacementAxis.Y;

            if ((uint)voxelIndex >= (uint)blockPlacementAxes.Length)
                return (byte)BlockPlacementAxis.Y;

            return blockPlacementAxes[voxelIndex];
        }

        private static bool HasFace(in GreedyFaceData face)
        {
            return face.valid != 0;
        }

        private static bool HasSameSurface(in GreedyFaceData a, in GreedyFaceData b)
        {
            return HasFace(a) &&
                   HasFace(b) &&
                   a.blockId == b.blockId &&
                   a.placementAxis == b.placementAxis &&
                   a.faceLight == b.faceLight &&
                   a.surfaceHeight == b.surfaceHeight;
        }

        private static bool CanMergeAlongU(in GreedyFaceData left, in GreedyFaceData right)
        {
            // Merge only when the shared edge keeps the same AO signature.
            return HasSameSurface(left, right) &&
                   left.ao1 == right.ao0 &&
                   left.ao2 == right.ao3 &&
                   left.light1 == right.light0 &&
                   left.light2 == right.light3;
        }

        private static bool CanMergeAlongV(in GreedyFaceData bottom, in GreedyFaceData top)
        {
            return HasSameSurface(bottom, top) &&
                   bottom.ao3 == top.ao0 &&
                   bottom.ao2 == top.ao1 &&
                   bottom.light3 == top.light0 &&
                   bottom.light2 == top.light1;
        }

        private static Vector2 ComputePlacementAwareUv(
            int u,
            int v,
            float rawU,
            float rawV,
            float posD,
            BlockFace sampledFace,
            BlockPlacementAxis placementAxis)
        {
            Vector3 worldCoords = new Vector3(
                u == 0 ? rawU : v == 0 ? rawV : posD,
                u == 1 ? rawU : v == 1 ? rawV : posD,
                u == 2 ? rawU : v == 2 ? rawV : posD);

            Vector3 canonicalCoords = ToCanonicalCoords(worldCoords, placementAxis);
            return sampledFace switch
            {
                BlockFace.Top => new Vector2(canonicalCoords.x, canonicalCoords.z),
                BlockFace.Bottom => new Vector2(canonicalCoords.x, canonicalCoords.z),
                BlockFace.Right => new Vector2(canonicalCoords.z, canonicalCoords.y),
                BlockFace.Left => new Vector2(canonicalCoords.z, canonicalCoords.y),
                BlockFace.Front => new Vector2(canonicalCoords.x, canonicalCoords.y),
                BlockFace.Back => new Vector2(canonicalCoords.x, canonicalCoords.y),
                _ => new Vector2(canonicalCoords.x, canonicalCoords.y)
            };
        }

        private static BlockPlacementAxis ResolveUvPlacementAxis(BlockTextureMapping mapping, BlockPlacementAxis placementAxis)
        {
            if (!mapping.usePlacementAxisRotation)
                return BlockPlacementAxis.Y;

            if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
                BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
            {
                // Furnace/crafter style: rotate which face uses each texture,
                // but keep UV orientation in the default cube space.
                return BlockPlacementAxis.Y;
            }

            return placementAxis;
        }

        private static BlockFace ResolveUvSamplingFace(
            BlockTextureMapping mapping,
            BlockFace worldFace,
            BlockFace sampledFace)
        {
            if (!mapping.usePlacementAxisRotation)
                return worldFace;

            if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
                BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
            {
                // Horizontal-facing cubes (furnace/crafter): rotate face-to-texture mapping,
                // but keep UV orientation anchored to the actual world face plane.
                return worldFace;
            }

            return sampledFace;
        }

        private static Vector3 ToCanonicalCoords(Vector3 worldCoords, BlockPlacementAxis placementAxis)
        {
            return BlockPlacementRotationUtility.SanitizeAxis(placementAxis) switch
            {
                BlockPlacementAxis.X => new Vector3(-worldCoords.y, worldCoords.x, worldCoords.z),
                BlockPlacementAxis.Z => new Vector3(worldCoords.x, worldCoords.z, -worldCoords.y),
                _ => worldCoords
            };
        }

        private static byte GetRectVertexAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            int localX,
            int localY)
        {
            int cellX = localX == width ? startI + width - 1 : startI + localX;
            int cellY = localY == height ? startJ + height - 1 : startJ + localY;
            GreedyFaceData face = mask[cellX + cellY * sizeU];

            if (localX == width)
                return localY == height ? face.ao2 : face.ao1;

            return localY == height ? face.ao3 : face.ao0;
        }

        private static bool MatchesQuadInterpolationForAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            bool flipTriangle)
        {
            if (width <= 0 || height <= 0)
                return false;

            int scale = width * height;
            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);

            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int actual = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, x, y);
                    int expectedScaled;

                    if (!flipTriangle)
                    {
                        if (x * height >= y * width)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao11 - ao10) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao00 * scale +
                                             (ao11 - ao01) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                    }
                    else
                    {
                        if (x * height + y * width <= scale)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao11 * scale +
                                             (ao10 - ao11) * width * (height - y) +
                                             (ao01 - ao11) * height * (width - x);
                        }
                    }

                    if (actual * scale != expectedScaled)
                        return false;
                }
            }

            return true;
        }

        private static byte GetRectVertexLight(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            int localX,
            int localY)
        {
            int cellX = localX == width ? startI + width - 1 : startI + localX;
            int cellY = localY == height ? startJ + height - 1 : startJ + localY;
            GreedyFaceData face = mask[cellX + cellY * sizeU];

            if (localX == width)
                return localY == height ? face.light2 : face.light1;

            return localY == height ? face.light3 : face.light0;
        }

        private static bool MatchesQuadInterpolationForLight(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            bool flipTriangle)
        {
            if (width <= 0 || height <= 0)
                return false;

            int scale = width * height;
            int light00 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, 0);
            int light10 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, 0);
            int light11 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, height);
            int light01 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, height);

            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int actual = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, x, y);
                    int expectedScaled;

                    if (!flipTriangle)
                    {
                        if (x * height >= y * width)
                        {
                            expectedScaled = light00 * scale +
                                             (light10 - light00) * x * height +
                                             (light11 - light10) * y * width;
                        }
                        else
                        {
                            expectedScaled = light00 * scale +
                                             (light11 - light01) * x * height +
                                             (light01 - light00) * y * width;
                        }
                    }
                    else
                    {
                        if (x * height + y * width <= scale)
                        {
                            expectedScaled = light00 * scale +
                                             (light10 - light00) * x * height +
                                             (light01 - light00) * y * width;
                        }
                        else
                        {
                            expectedScaled = light11 * scale +
                                             (light10 - light11) * width * (height - y) +
                                             (light01 - light11) * height * (width - x);
                        }
                    }

                    if (actual * scale != expectedScaled)
                        return false;
                }
            }

            return true;
        }

        private static bool TryGetRepresentableRectFlip(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            out bool flipTriangle)
        {
            bool noFlipMatches =
                MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, false) &&
                MatchesQuadInterpolationForLight(mask, sizeU, startI, startJ, width, height, false);
            bool flipMatches =
                MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, true) &&
                MatchesQuadInterpolationForLight(mask, sizeU, startI, startJ, width, height, true);

            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);
            int light00 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, 0);
            int light10 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, 0);
            int light11 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, height);
            int light01 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, height);
            bool heuristicFlip = (ao00 + ao11 + light00 + light11) > (ao10 + ao01 + light10 + light01);

            if (noFlipMatches && flipMatches)
            {
                flipTriangle = heuristicFlip;
                return true;
            }

            if (flipMatches)
            {
                flipTriangle = true;
                return true;
            }

            if (noFlipMatches)
            {
                flipTriangle = false;
                return true;
            }

            flipTriangle = heuristicFlip;
            return false;
        }

        private static bool TryGetRepresentableRectFast(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int maxWidth,
            int maxHeight,
            out int resolvedWidth,
            out int resolvedHeight,
            out bool flipTriangle)
        {
            if (TryGetRepresentableRectFlip(mask, sizeU, startI, startJ, maxWidth, maxHeight, out flipTriangle))
            {
                resolvedWidth = maxWidth;
                resolvedHeight = maxHeight;
                return true;
            }

            int widthFirstW;
            int widthFirstH;
            bool widthFirstFlip;
            ShrinkRectUntilRepresentable(
                mask, sizeU, startI, startJ, maxWidth, maxHeight, true,
                out widthFirstW, out widthFirstH, out widthFirstFlip);

            int heightFirstW;
            int heightFirstH;
            bool heightFirstFlip;
            ShrinkRectUntilRepresentable(
                mask, sizeU, startI, startJ, maxWidth, maxHeight, false,
                out heightFirstW, out heightFirstH, out heightFirstFlip);

            int widthFirstArea = widthFirstW * widthFirstH;
            int heightFirstArea = heightFirstW * heightFirstH;

            if (widthFirstArea >= heightFirstArea)
            {
                resolvedWidth = widthFirstW;
                resolvedHeight = widthFirstH;
                flipTriangle = widthFirstFlip;
            }
            else
            {
                resolvedWidth = heightFirstW;
                resolvedHeight = heightFirstH;
                flipTriangle = heightFirstFlip;
            }

            return true;
        }

        private static void ShrinkRectUntilRepresentable(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int maxWidth,
            int maxHeight,
            bool shrinkWidthFirst,
            out int resolvedWidth,
            out int resolvedHeight,
            out bool flipTriangle)
        {
            int width = maxWidth;
            int height = maxHeight;

            while (true)
            {
                if (TryGetRepresentableRectFlip(mask, sizeU, startI, startJ, width, height, out flipTriangle))
                {
                    resolvedWidth = width;
                    resolvedHeight = height;
                    return;
                }

                if (width <= 1 && height <= 1)
                    break;

                if (shrinkWidthFirst)
                {
                    if (width > 1)
                        width--;
                    else
                        height--;
                }
                else
                {
                    if (height > 1)
                        height--;
                    else
                        width--;
                }
            }

            resolvedWidth = 1;
            resolvedHeight = 1;
            flipTriangle = (mask[startI + startJ * sizeU].ao0 + mask[startI + startJ * sizeU].ao2) >
                           (mask[startI + startJ * sizeU].ao1 + mask[startI + startJ * sizeU].ao3);
        }
    }
}
