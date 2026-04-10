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
    [BurstCompile]
    public struct DisposeChunkDataJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int> heightCache;
        [DeallocateOnJobCompletion] public NativeArray<byte> blockTypes;
        [DeallocateOnJobCompletion] public NativeArray<byte> blockPlacementAxes;
        [DeallocateOnJobCompletion] public NativeArray<byte> knownVoxelData;
        [DeallocateOnJobCompletion] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion] public NativeArray<byte> light;
        [DeallocateOnJobCompletion] public NativeArray<bool> subchunkNonEmpty; // ÃƒÂ¢Ã¢â‚¬Â Ã‚Â NOVO
        public void Execute() { }
    }

    [BurstCompile]
    public struct DisposeSuppressedBillboardsJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int3> suppressedGrassBillboards;
        public void Execute() { }
    }
}
