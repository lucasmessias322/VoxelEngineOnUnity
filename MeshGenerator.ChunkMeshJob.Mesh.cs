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
    private partial struct ChunkMeshJob
    {
        private void GenerateMesh(NativeArray<int> heightCache, NativeArray<byte> blockTypes, NativeArray<bool> solids, NativeArray<byte> light, float invAtlasTilesX, float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxMask = math.max(voxelSizeX * SizeY, math.max(voxelSizeX * voxelSizeZ, SizeY * voxelSizeZ));
            NativeArray<GreedyFaceData> mask = new NativeArray<GreedyFaceData>(maxMask, Allocator.Temp);

            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int normalSign = side == 0 ? 1 : -1;

                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;

                    int sizeU = u == 0 ? voxelSizeX : u == 1 ? SizeY : voxelSizeZ;
                    int sizeV = v == 0 ? voxelSizeX : v == 1 ? SizeY : voxelSizeZ;
                    int chunkSize = axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ;

                    int minN = axis == 1 ? startY : border;
                    int maxN = axis == 1 ? endY : border + chunkSize;

                    int minU = u == 1 ? startY : border;
                    int maxU = u == 1 ? endY : (u == 0 ? border + SizeX : border + SizeZ);

                    int minV = v == 1 ? startY : border;
                    int maxV = v == 1 ? endY : (v == 0 ? border + SizeX : border + SizeZ);

                    Vector3 normal = new Vector3(axis == 0 ? normalSign : 0, axis == 1 ? normalSign : 0, axis == 2 ? normalSign : 0);
                    BlockFace faceType = BlockFaceUtility.FromAxisNormal(axis, normalSign);

                    Vector3Int stepU = new Vector3Int(u == 0 ? 1 : 0, u == 1 ? 1 : 0, u == 2 ? 1 : 0);
                    Vector3Int stepV = new Vector3Int(v == 0 ? 1 : 0, v == 1 ? 1 : 0, v == 2 ? 1 : 0);

                    for (int n = minN; n < maxN; n++)
                    {
                        for (int j = minV; j < maxV; j++)
                        {
                            for (int i = minU; i < maxU; i++)
                            {
                                int x = u == 0 ? i : v == 0 ? j : n;
                                int y = u == 1 ? i : v == 1 ? j : n;
                                int z = u == 2 ? i : v == 2 ? j : n;

                                int maskIndex = i + j * sizeU;
                                int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                                BlockType current = (BlockType)blockTypes[idx];

                                if (current == BlockType.Air)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                BlockTextureMapping currentMapping = blockMappings[(int)current];
                                if (enableUltraLeafBillboards && current == BlockType.Leaves)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                if (BlockShapeUtility.GetEffectiveRenderShape(currentMapping) != BlockRenderShape.Cube)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                int nx = x + (axis == 0 ? normalSign : 0);
                                int ny = y + (axis == 1 ? normalSign : 0);
                                int nz = z + (axis == 2 ? normalSign : 0);

                                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;
                                bool isVisible;
                                if (outside)
                                {
                                    isVisible = true;
                                }
                                else
                                {
                                    if (!IsVoxelSampleKnown(nx, ny, nz, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                                    {
                                        mask[maskIndex] = default;
                                        continue;
                                    }

                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = (BlockType)blockTypes[nIdx];
                                    isVisible = IsFaceVisibleForCurrentBlock(current, neighbor);
                                }

                                if (!isVisible)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                byte packed = outside
                                    ? LightUtils.PackLight(15, 0)
                                    : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                byte faceLight = (byte)math.max(
                                    (int)LightUtils.GetSkyLight(packed),
                                    (int)LightUtils.GetBlockLight(packed)
                                );

                                int aoPlaneN = n + normalSign;
                                Vector3Int aoPos = new Vector3Int(
                                    axis == 0 ? aoPlaneN : x,
                                    axis == 1 ? aoPlaneN : y,
                                    axis == 2 ? aoPlaneN : z
                                );

                                byte ao0;
                                byte ao1;
                                byte ao2;
                                byte ao3;
                                bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(currentMapping);
                                if (disableAOForCurrentBlock)
                                {
                                    ao0 = 3;
                                    ao1 = 3;
                                    ao2 = 3;
                                    ao3 = 3;
                                }
                                else
                                {
                                    ao0 = GetVertexAO(aoPos, -stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao1 = GetVertexAO(aoPos, stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao2 = GetVertexAO(aoPos, stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao3 = GetVertexAO(aoPos, -stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                }

                                byte light0 = GetVertexLight(aoPos, -stepU, -stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light1 = GetVertexLight(aoPos, stepU, -stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light2 = GetVertexLight(aoPos, stepU, stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light3 = GetVertexLight(aoPos, -stepU, stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                if (IsEmissiveBlock(currentMapping))
                                {
                                    byte emission = currentMapping.lightEmission;
                                    light0 = (byte)math.max((int)light0, (int)emission);
                                    light1 = (byte)math.max((int)light1, (int)emission);
                                    light2 = (byte)math.max((int)light2, (int)emission);
                                    light3 = (byte)math.max((int)light3, (int)emission);
                                }

                                light0 = (byte)math.max((int)light0, (int)faceLight);
                                light1 = (byte)math.max((int)light1, (int)faceLight);
                                light2 = (byte)math.max((int)light2, (int)faceLight);
                                light3 = (byte)math.max((int)light3, (int)faceLight);
                                byte placementAxis = GetBlockPlacementAxisValue(idx);

                                mask[maskIndex] = new GreedyFaceData
                                {
                                    blockId = (byte)current,
                                    placementAxis = placementAxis,
                                    valid = 1,
                                    faceLight = faceLight,
                                    surfaceHeight = 0,
                                    ao0 = ao0,
                                    ao1 = ao1,
                                    ao2 = ao2,
                                    ao3 = ao3,
                                    light0 = light0,
                                    light1 = light1,
                                    light2 = light2,
                                    light3 = light3
                                };
                            }
                        }

                        for (int j = minV; j < maxV; j++)
                        {
                            int i = minU;
                            while (i < maxU)
                            {
                                GreedyFaceData startFace = mask[i + j * sizeU];
                                if (!HasFace(startFace))
                                {
                                    i++;
                                    continue;
                                }

                                bool isWaterFace = FluidBlockUtility.IsWater((BlockType)startFace.blockId);
                                int w = 1;
                                while (!isWaterFace && i + w < maxU && CanMergeAlongU(mask[i + w - 1 + j * sizeU], mask[i + w + j * sizeU]))
                                    w++;

                                int h = 1;
                                while (!isWaterFace && j + h < maxV)
                                {
                                    bool canGrow = true;
                                    for (int k = 0; k < w; k++)
                                    {
                                        GreedyFaceData candidate = mask[i + k + (j + h) * sizeU];
                                        if (!HasFace(candidate) ||
                                            !CanMergeAlongV(mask[i + k + (j + h - 1) * sizeU], candidate) ||
                                            (k > 0 && !CanMergeAlongU(mask[i + k - 1 + (j + h) * sizeU], candidate)))
                                        {
                                            canGrow = false;
                                            break;
                                        }
                                    }

                                    if (!canGrow)
                                        break;

                                    h++;
                                }

                                bool flipTriangle = (startFace.ao0 + startFace.ao2) > (startFace.ao1 + startFace.ao3);
                                if (!useFastBedrockStyleMeshing)
                                {
                                    int maxW = w;
                                    int maxH = h;
                                    int bestW = 1;
                                    int bestH = 1;
                                    int bestArea = 1;

                                    // Keep the largest sub-rectangle whose AO and light gradient still fit one quad.
                                    for (int testH = 1; testH <= maxH; testH++)
                                    {
                                        for (int testW = 1; testW <= maxW; testW++)
                                        {
                                            int area = testW * testH;
                                            if (area < bestArea || (area == bestArea && testW <= bestW))
                                                continue;

                                            if (!TryGetRepresentableRectFlip(mask, sizeU, i, j, testW, testH, out bool candidateFlip))
                                                continue;

                                            bestW = testW;
                                            bestH = testH;
                                            bestArea = area;
                                            flipTriangle = candidateFlip;
                                        }
                                    }

                                    w = bestW;
                                    h = bestH;
                                }
                                else
                                {
                                    TryGetRepresentableRectFast(mask, sizeU, i, j, w, h, out w, out h, out flipTriangle);
                                }

                                GreedyFaceData bottomLeftFace = mask[i + j * sizeU];
                                GreedyFaceData bottomRightFace = mask[i + (w - 1) + j * sizeU];
                                GreedyFaceData topRightFace = mask[i + (w - 1) + (j + h - 1) * sizeU];
                                GreedyFaceData topLeftFace = mask[i + (j + h - 1) * sizeU];

                                BlockType bt = (BlockType)bottomLeftFace.blockId;
                                byte ao0 = bottomLeftFace.ao0;
                                byte ao1 = bottomRightFace.ao1;
                                byte ao2 = topRightFace.ao2;
                                byte ao3 = topLeftFace.ao3;
                                byte light0 = bottomLeftFace.light0;
                                byte light1 = bottomRightFace.light1;
                                byte light2 = topRightFace.light2;
                                byte light3 = topLeftFace.light3;
                                int baseBlockY = u == 1 ? i : v == 1 ? j : n;
                                int blockX = u == 0 ? i : v == 0 ? j : n;
                                int blockY = u == 1 ? i : v == 1 ? j : n;
                                int blockZ = u == 2 ? i : v == 2 ? j : n;

                                int vIndex = GetCurrentSubchunkLocalVertexIndex();
                                BlockTextureMapping m = blockMappings[(int)bt];
                                BlockPlacementAxis placementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(bottomLeftFace.placementAxis);
                                BlockFace sampledFace = BlockPlacementRotationUtility.ResolveFaceForPlacement(m, faceType, placementAxis);
                                BlockPlacementAxis uvPlacementAxis = ResolveUvPlacementAxis(m, placementAxis);
                                BlockFace uvSamplingFace = ResolveUvSamplingFace(m, faceType, sampledFace);
                                bool tint = m.GetTint(sampledFace);
                                bool useGrassSideOverlay =
                                    bt == BlockType.Grass &&
                                    sampledFace != BlockFace.Top &&
                                    sampledFace != BlockFace.Bottom;
                                int faceSubchunkIndex = math.clamp(baseBlockY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
                                float packedSubchunkAndOverlay =
                                    faceSubchunkIndex + (useGrassSideOverlay ? 0.25f : 0f);
                                Vector2Int tile = m.GetTileCoord(sampledFace);
                                Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);

                                for (int l = 0; l < 4; l++)
                                {
                                    int du = (l == 1 || l == 2) ? w : 0;
                                    int dv = (l == 2 || l == 3) ? h : 0;

                                    float rawU = i + du;
                                    float rawV = j + dv;
                                    float posD = n + (normalSign > 0 ? 1f : 0f);
                                    int cornerUOffset = du > 0 ? 1 : 0;
                                    int cornerVOffset = dv > 0 ? 1 : 0;

                                    float px = (u == 0 ? rawU : v == 0 ? rawV : posD) - border;
                                    float py = (u == 1 ? rawU : v == 1 ? rawV : posD);
                                    float pz = (u == 2 ? rawU : v == 2 ? rawV : posD) - border;

                                    if (FluidBlockUtility.IsWater(bt) && py > baseBlockY + 0.5f)
                                        py = baseBlockY + GetWaterVertexHeight01(bt, blockX, blockY, blockZ, axis, normalSign, cornerUOffset, cornerVOffset, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                                    Vector2 uvCoord = ComputePlacementAwareUv(
                                        u,
                                        v,
                                        rawU,
                                        rawV,
                                        posD,
                                        uvSamplingFace,
                                        uvPlacementAxis);

                                    byte currentAO = l == 0 ? ao0 : (l == 1 ? ao1 : (l == 2 ? ao2 : ao3));
                                    byte currentLight = l == 0 ? light0 : (l == 1 ? light1 : (l == 2 ? light2 : light3));
                                    float rawLight = currentLight / 15f;
                                    float floatTint = tint ? 1f : 0f;
                                    float aoCurve = aoCurveExponent > 0f ? aoCurveExponent : DefaultAOCurveExponent;
                                    float aoBase = currentAO / 3f;
                                    float aoCurved = math.pow(aoBase, aoCurve);
                                    float aoDarkened = 1f - (1f - aoCurved) * math.max(0f, aoStrength);
                                    float floatAO = math.max(math.saturate(aoMinLight), math.saturate(aoDarkened));
                                    AddPackedVertex(
                                        new Vector3(px, py, pz),
                                        normal,
                                        uvCoord,
                                        atlasUv,
                                        new Vector4(rawLight, floatTint, floatAO, packedSubchunkAndOverlay));
                                }

                                NativeList<int> tris = FluidBlockUtility.IsWater(bt)
                                    ? waterTriangles
                                    : (blockMappings[(int)bt].isTransparent ? transparentTriangles : opaqueTriangles);

                                if (normalSign > 0)
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 3);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                }
                                else
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 1);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 1);
                                    }
                                }

                                for (int y0 = 0; y0 < h; y0++)
                                {
                                    for (int x0 = 0; x0 < w; x0++)
                                        mask[i + x0 + (j + y0) * sizeU] = default;
                                }

                                i += w;
                            }
                        }
                    }
                }
            }

            mask.Dispose();
        }
        private bool IsOccluder(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (!TryGetResolvedVoxelIndex(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return false;

            if (!solids[idx])
                return false;

            BlockType blockType = (BlockType)blockTypes[idx];
            BlockTextureMapping mapping = blockMappings[(int)blockType];
            return CastsAmbientOcclusion(blockType, mapping);
        }

        private bool IsVoxelSampleKnown(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            if (!useKnownVoxelData)
                return true;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            return knownVoxelData[idx] != 0;
        }

        private bool TryGetResolvedVoxelIndex(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize, out int idx)
        {
            idx = -1;
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if (!useKnownVoxelData || knownVoxelData[idx] != 0)
                return true;

            int clampedX = math.clamp(x, border, border + SizeX - 1);
            int clampedZ = math.clamp(z, border, border + SizeZ - 1);
            idx = clampedX + y * voxelSizeX + clampedZ * voxelPlaneSize;
            return !useKnownVoxelData || knownVoxelData[idx] != 0;
        }

        private byte GetVertexAO(Vector3Int pos, Vector3Int d1, Vector3Int d2, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            bool s1 = IsOccluder(pos.x + d1.x, pos.y + d1.y, pos.z + d1.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool s2 = IsOccluder(pos.x + d2.x, pos.y + d2.y, pos.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool c = IsOccluder(pos.x + d1.x + d2.x, pos.y + d1.y + d2.y, pos.z + d1.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (s1 && s2) return 0;
            return (byte)(3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (c ? 1 : 0));
        }

        private static bool CastsAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
        {
            // AO deve vir de cubos cheios que realmente fecham a iluminaÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o ambiente.
            // Folhas sÃƒÆ’Ã‚Â£o a exceÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o: mesmo transparentes, devem sombrear como no Minecraft.
            if (BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube ||
                mapping.isEmpty ||
                mapping.isLiquid ||
                mapping.lightOpacity == 0)
            {
                return false;
            }

            return !mapping.isTransparent || blockType == BlockType.Leaves;
        }

        private float GetWaterVertexHeight01(
            BlockType blockType,
            int x,
            int y,
            int z,
            int axis,
            int normalSign,
            int cornerUOffset,
            int cornerVOffset,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!FluidBlockUtility.IsWater(blockType))
                return 1f;

            if (HasWaterAbove(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return 1f;

            ResolveWaterCornerOffsets(axis, normalSign, cornerUOffset, cornerVOffset, out int cornerXOffset, out int cornerZOffset);
            return GetWaterCornerHeight01(x, y, z, cornerXOffset, cornerZOffset, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        private void ResolveWaterCornerOffsets(
            int axis,
            int normalSign,
            int cornerUOffset,
            int cornerVOffset,
            out int cornerXOffset,
            out int cornerZOffset)
        {
            if (axis == 0)
            {
                cornerXOffset = normalSign > 0 ? 1 : 0;
                cornerZOffset = cornerVOffset;
                return;
            }

            if (axis == 1)
            {
                cornerXOffset = cornerVOffset;
                cornerZOffset = cornerUOffset;
                return;
            }

            cornerXOffset = cornerUOffset;
            cornerZOffset = normalSign > 0 ? 1 : 0;
        }

        private float GetWaterCornerHeight01(
            int x,
            int y,
            int z,
            int cornerXOffset,
            int cornerZOffset,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int sampleMinX = x + cornerXOffset - 1;
            int sampleMinZ = z + cornerZOffset - 1;
            float accumulatedHeight = 0f;
            int accumulatedWeight = 0;

            for (int dz = 0; dz < 2; dz++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int sampleX = sampleMinX + dx;
                    int sampleZ = sampleMinZ + dz;

                    if (HasWaterAbove(sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                        return 1f;

                    BlockType sampleType = GetBlockTypeSafe(sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    if (FluidBlockUtility.IsWater(sampleType))
                    {
                        float sampleHeight = GetWaterOwnHeight01(sampleType, sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                        if (sampleHeight >= 0.8f)
                        {
                            accumulatedHeight += sampleHeight * 10f;
                            accumulatedWeight += 10;
                        }
                        else
                        {
                            accumulatedHeight += sampleHeight;
                            accumulatedWeight += 1;
                        }
                    }
                    else if (!IsSolidWaterNeighbor(sampleType))
                    {
                        accumulatedWeight += 1;
                    }
                }
            }

            if (accumulatedWeight <= 0)
                return FluidBlockUtility.GetWaterSurfaceHeight01(BlockType.WaterFlow7);

            return accumulatedHeight / accumulatedWeight;
        }

        private float GetWaterOwnHeight01(
            BlockType blockType,
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!FluidBlockUtility.IsWater(blockType))
                return 0f;

            if (HasWaterAbove(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return 1f;

            if (FluidBlockUtility.IsFallingWater(blockType))
                return 1f;

            return FluidBlockUtility.GetWaterSurfaceHeight01(blockType);
        }

        private bool HasWaterAbove(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || z < 0 || z >= voxelSizeZ)
                return false;

            if (y + 1 >= SizeY || y < 0)
                return false;

            if (!TryGetResolvedVoxelIndex(x, y + 1, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int aboveIndex))
                return false;

            return FluidBlockUtility.IsWater((BlockType)blockTypes[aboveIndex]);
        }

        private BlockType GetBlockTypeSafe(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (!TryGetResolvedVoxelIndex(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int index))
                return BlockType.Air;

            return (BlockType)blockTypes[index];
        }

        private bool IsSolidWaterNeighbor(BlockType blockType)
        {
            if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
                return false;

            if (TorchPlacementUtility.IsTorchLike(blockType))
                return false;

            BlockTextureMapping mapping = blockMappings[(int)blockType];
            return mapping.isSolid;
        }

        private byte GetVertexLight(Vector3Int pos, Vector3Int d1, Vector3Int d2, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            int l0 = SampleLightValue(pos, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l1 = SampleLightValue(pos + d1, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l2 = SampleLightValue(pos + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l3 = SampleLightValue(pos + d1 + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            return (byte)((l0 + l1 + l2 + l3 + 2) / 4);
        }

        private int SampleLightValue(Vector3Int pos, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (pos.y < 0)
                return 0;
            if (pos.y >= SizeY)
                return 15;

            int clampedX = math.clamp(pos.x, 0, voxelSizeX - 1);
            int clampedZ = math.clamp(pos.z, 0, voxelSizeZ - 1);
            if (!TryGetResolvedVoxelIndex(clampedX, pos.y, clampedZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return 15;

            byte packed = light[idx];
            return math.max((int)LightUtils.GetSkyLight(packed), (int)LightUtils.GetBlockLight(packed));
        }

        private static bool IsEmissiveBlock(BlockTextureMapping mapping)
        {
            return mapping.isLightSource || mapping.lightEmission > 0;
        }
    }
}
