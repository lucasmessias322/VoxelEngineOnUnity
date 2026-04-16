using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class LightOpacitySpaghettiCaveUtility
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const int SpaghettiHorizontalCellSize = 4;
    private const int SpaghettiVerticalCellSize = 4;

    public static bool ShouldApply(int dataBorderSize, int lightBorderSize, in SpaghettiCaveSettings settings)
    {
        return lightBorderSize > dataBorderSize &&
               settings.enabled;
    }

    public static void BuildCarveMask(
        Vector2Int coord,
        NativeArray<int> heightCache,
        NativeArray<byte> carveMask,
        NativeArray<byte> prefilledColumns,
        int prefilledColumnsStride,
        int border,
        int oreSeed,
        SpaghettiCaveSettings settings)
    {
        if (!heightCache.IsCreated || !carveMask.IsCreated || !settings.enabled || border <= 0)
            return;

        int minY = math.clamp(math.min(settings.minY, settings.maxY), 3, SizeY - 2);
        int maxY = math.clamp(math.max(settings.minY, settings.maxY), 3, SizeY - 2);
        if (maxY < minY)
            return;

        int voxelSizeX = SizeX + 2 * border;
        int voxelSizeZ = SizeZ + 2 * border;
        int voxelPlaneSize = voxelSizeX * SizeY;
        int heightStride = voxelSizeX;
        int minSurfaceDepth = math.max(0, settings.minSurfaceDepth);
        int entranceSurfaceDepth = math.max(0, math.min(settings.entranceSurfaceDepth, minSurfaceDepth));
        float densityBias = settings.densityBias;
        int worldSeed = oreSeed ^ settings.seedOffset ^ 0x4b1d2e37;
        var caveNoiseSampler = SpaghettiCaveNoiseUtility.Create(worldSeed);
        int chunkMinX = coord.x * SizeX;
        int chunkMinZ = coord.y * SizeZ;

        int globalEntranceMaxY = minY - 1;
        for (int localZ = 0; localZ < voxelSizeZ; localZ++)
        {
            for (int localX = 0; localX < voxelSizeX; localX++)
            {
                int columnSurfaceY = heightCache[localX + localZ * heightStride];
                int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
                globalEntranceMaxY = math.max(globalEntranceMaxY, entranceMaxY);
            }
        }

        if (globalEntranceMaxY < minY)
            return;

        int sampleMaxY = math.min(maxY, globalEntranceMaxY);
        int gridCountX = GetGridPointCount(0, voxelSizeX - 1, SpaghettiHorizontalCellSize);
        int gridCountY = GetGridPointCount(minY, sampleMaxY, SpaghettiVerticalCellSize);
        int gridCountZ = GetGridPointCount(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize);
        NativeArray<float2> densityGrid = new NativeArray<float2>(gridCountX * gridCountY * gridCountZ, Allocator.Temp);

        try
        {
            for (int gridZ = 0; gridZ < gridCountZ; gridZ++)
            {
                int localZ = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, gridZ);
                float sampleZ = localZ + chunkMinZ - border + 0.5f;

                for (int gridX = 0; gridX < gridCountX; gridX++)
                {
                    int localX = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, gridX);
                    float sampleX = localX + chunkMinX - border + 0.5f;

                    for (int gridY = 0; gridY < gridCountY; gridY++)
                    {
                        int voxelY = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, gridY);
                        float sampleY = voxelY + 0.5f;
                        densityGrid[GetGridIndex(gridX, gridY, gridZ, gridCountX, gridCountY)] =
                            SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, sampleX, sampleY, sampleZ, densityBias);
                    }
                }
            }

            for (int cellZ = 0; cellZ < gridCountZ - 1; cellZ++)
            {
                int localZ0 = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, cellZ);
                int localZ1 = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, cellZ + 1);
                int zSpan = math.max(1, localZ1 - localZ0);
                int localZMax = cellZ == gridCountZ - 2 ? localZ1 : localZ1 - 1;

                for (int cellX = 0; cellX < gridCountX - 1; cellX++)
                {
                    int localX0 = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, cellX);
                    int localX1 = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, cellX + 1);
                    int xSpan = math.max(1, localX1 - localX0);
                    int localXMax = cellX == gridCountX - 2 ? localX1 : localX1 - 1;

                    for (int cellY = 0; cellY < gridCountY - 1; cellY++)
                    {
                        int voxelY0 = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY);
                        int voxelY1 = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY + 1);
                        int ySpan = math.max(1, voxelY1 - voxelY0);
                        int voxelYMax = cellY == gridCountY - 2 ? voxelY1 : voxelY1 - 1;

                        float2 d000 = densityGrid[GetGridIndex(cellX, cellY, cellZ, gridCountX, gridCountY)];
                        float2 d100 = densityGrid[GetGridIndex(cellX + 1, cellY, cellZ, gridCountX, gridCountY)];
                        float2 d010 = densityGrid[GetGridIndex(cellX, cellY + 1, cellZ, gridCountX, gridCountY)];
                        float2 d110 = densityGrid[GetGridIndex(cellX + 1, cellY + 1, cellZ, gridCountX, gridCountY)];
                        float2 d001 = densityGrid[GetGridIndex(cellX, cellY, cellZ + 1, gridCountX, gridCountY)];
                        float2 d101 = densityGrid[GetGridIndex(cellX + 1, cellY, cellZ + 1, gridCountX, gridCountY)];
                        float2 d011 = densityGrid[GetGridIndex(cellX, cellY + 1, cellZ + 1, gridCountX, gridCountY)];
                        float2 d111 = densityGrid[GetGridIndex(cellX + 1, cellY + 1, cellZ + 1, gridCountX, gridCountY)];

                        bool requiresExactCellSampling =
                            cellX == 0 ||
                            cellX == gridCountX - 2 ||
                            cellZ == 0 ||
                            cellZ == gridCountZ - 2;

                        if (GetMinCarveDensity(d000, d100, d010, d110, d001, d101, d011, d111) >= 0f)
                        {
                            float centerWorldX = chunkMinX - border + (localX0 + localX1) * 0.5f + 0.5f;
                            float centerWorldY = (voxelY0 + voxelY1) * 0.5f + 0.5f;
                            float centerWorldZ = chunkMinZ - border + (localZ0 + localZ1) * 0.5f + 0.5f;
                            float centerDensity = SpaghettiCaveNoiseUtility.SampleCarveDensity(in caveNoiseSampler, centerWorldX, centerWorldY, centerWorldZ, densityBias);
                            if (centerDensity >= 0f)
                                continue;

                            requiresExactCellSampling = true;
                        }

                        for (int localZ = localZ0; localZ <= localZMax; localZ++)
                        {
                            float tz = (localZ - localZ0) / (float)zSpan;

                            for (int localX = localX0; localX <= localXMax; localX++)
                            {
                                if (IsColumnPrefilled(localX, localZ, prefilledColumns, prefilledColumnsStride))
                                    continue;

                                int columnSurfaceY = heightCache[localX + localZ * heightStride];
                                int regularMaxY = math.min(maxY, columnSurfaceY - minSurfaceDepth);
                                int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
                                if (entranceMaxY < voxelY0)
                                    continue;

                                float tx = (localX - localX0) / (float)xSpan;
                                int maxVoxelYForColumn = math.min(voxelYMax, entranceMaxY);
                                int voxelIndex = localX + voxelY0 * voxelSizeX + localZ * voxelPlaneSize;
                                float worldX = localX + chunkMinX - border + 0.5f;
                                float worldZ = localZ + chunkMinZ - border + 0.5f;

                                for (int voxelY = voxelY0; voxelY <= maxVoxelYForColumn; voxelY++, voxelIndex += voxelSizeX)
                                {
                                    float carveDensity;
                                    if (requiresExactCellSampling)
                                    {
                                        if (voxelY > regularMaxY)
                                        {
                                            float2 density = SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, worldX, voxelY + 0.5f, worldZ, densityBias);
                                            if (density.y >= 0f)
                                                continue;
                                            carveDensity = density.x;
                                        }
                                        else
                                        {
                                            carveDensity = SpaghettiCaveNoiseUtility.SampleCarveDensity(in caveNoiseSampler, worldX, voxelY + 0.5f, worldZ, densityBias);
                                        }
                                    }
                                    else
                                    {
                                        float ty = (voxelY - voxelY0) / (float)ySpan;
                                        float2 density = TrilinearInterpolate(d000, d100, d010, d110, d001, d101, d011, d111, tx, ty, tz);
                                        if (voxelY > regularMaxY && density.y >= 0f)
                                            continue;
                                        carveDensity = density.x;
                                    }

                                    if (carveDensity >= 0f)
                                        continue;

                                    carveMask[voxelIndex] = 1;
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (densityGrid.IsCreated)
                densityGrid.Dispose();
        }
    }

    private static bool IsColumnPrefilled(int localX, int localZ, NativeArray<byte> prefilledColumns, int prefilledColumnsStride)
    {
        if (!prefilledColumns.IsCreated || prefilledColumns.Length == 0 || prefilledColumnsStride <= 0)
            return false;

        int index = localX + localZ * prefilledColumnsStride;
        return (uint)index < (uint)prefilledColumns.Length && prefilledColumns[index] != 0;
    }

    private static int GetGridPointCount(int minInclusive, int maxInclusive, int step)
    {
        int safeStep = math.max(1, step);
        return math.max(2, ((maxInclusive - minInclusive) / safeStep) + 2);
    }

    private static int GetGridCoordinate(int minInclusive, int maxInclusive, int step, int index)
    {
        return math.min(maxInclusive, minInclusive + index * math.max(1, step));
    }

    private static int GetGridIndex(int x, int y, int z, int xCount, int yCount)
    {
        return x + y * xCount + z * xCount * yCount;
    }

    private static float GetMinCarveDensity(float2 d000, float2 d100, float2 d010, float2 d110, float2 d001, float2 d101, float2 d011, float2 d111)
    {
        float minA = math.min(math.min(d000.x, d100.x), math.min(d010.x, d110.x));
        float minB = math.min(math.min(d001.x, d101.x), math.min(d011.x, d111.x));
        return math.min(minA, minB);
    }

    private static float2 TrilinearInterpolate(float2 d000, float2 d100, float2 d010, float2 d110, float2 d001, float2 d101, float2 d011, float2 d111, float tx, float ty, float tz)
    {
        float2 x00 = math.lerp(d000, d100, tx);
        float2 x10 = math.lerp(d010, d110, tx);
        float2 x01 = math.lerp(d001, d101, tx);
        float2 x11 = math.lerp(d011, d111, tx);
        float2 y0 = math.lerp(x00, x10, ty);
        float2 y1 = math.lerp(x01, x11, ty);
        return math.lerp(y0, y1, tz);
    }
}
