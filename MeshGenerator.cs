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
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const float DefaultAOCurveExponent = 1.12f;
    private const float SurfaceHeightEncodeScale = 128f;
    private const float WireSurfaceBlockOffset = .5f / 16f;
    private const int SpaghettiCarveMaskCacheMaxEntries = 64;
    private const int TempGenerationPoolMaxArraysPerSize = 8;

    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;
    public struct SubchunkMeshRange
    {
        public int vertexStart;
        public int vertexCount;
        public int opaqueStart;
        public int opaqueCount;
        public int transparentStart;
        public int transparentCount;
        public int billboardStart;
        public int billboardCount;
        public int waterStart;
        public int waterCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PackedChunkVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector4 uv2;
        public Vector2 uv3;
    }
}
