using System;
using Unity.Collections;
using Unity.Jobs;

public partial class World
{
    private static void SafeDisposeNativeArray<T>(ref NativeArray<T> array) where T : struct
    {
        if (!array.IsCreated)
            return;

        try
        {
            array.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        array = default;
    }

    private static void DisposeCompletedDataJobInputs(ref PendingData pd)
    {
        SafeDisposeNativeArray(ref pd.edits);
        MeshGenerator.ReturnByteBuffer(ref pd.chunkLightData);
        MeshGenerator.ReturnByteBuffer(ref pd.blockEmissionData);
        MeshGenerator.ReturnByteBuffer(ref pd.lightOpacityData);
        MeshGenerator.ReturnByteBuffer(ref pd.fastRebuildSnapshotVoxelData);
        MeshGenerator.ReturnByteBuffer(ref pd.fastRebuildSnapshotLoadedChunks);
        SafeDisposeNativeArray(ref pd.fastRebuildOverrides);
    }

    private static void DisposePostCompletionOverrideInputs(ref PendingData pd)
    {
        SafeDisposeNativeArray(ref pd.postCompletionOverrides);
        MeshGenerator.ReturnByteBuffer(ref pd.postCompletionDirtyColumns);
        pd.postOverrideRefreshScheduled = false;
    }

    private static void DisposeDataJobResources(ref PendingData pd)
    {
        MeshGenerator.ReleaseDataJobTempBuffers(ref pd.tempBuffers);
        MeshGenerator.ReturnIntBuffer(ref pd.heightCache);
        MeshGenerator.ReturnByteBuffer(ref pd.blockTypes);
        MeshGenerator.ReturnByteBuffer(ref pd.blockPlacementAxes);
        MeshGenerator.ReturnByteBuffer(ref pd.knownVoxelData);
        MeshGenerator.ReturnBoolBuffer(ref pd.solids);
        MeshGenerator.ReturnByteBuffer(ref pd.light);
        MeshGenerator.ReturnByteBuffer(ref pd.chunkLightData);
        MeshGenerator.ReturnByteBuffer(ref pd.blockEmissionData);
        MeshGenerator.ReturnByteBuffer(ref pd.lightOpacityData);
        SafeDisposeNativeArray(ref pd.edits);
        MeshGenerator.ReturnByteBuffer(ref pd.fastRebuildSnapshotVoxelData);
        MeshGenerator.ReturnByteBuffer(ref pd.fastRebuildSnapshotLoadedChunks);
        SafeDisposeNativeArray(ref pd.fastRebuildOverrides);
        SafeDisposeNativeArray(ref pd.postCompletionOverrides);
        MeshGenerator.ReturnByteBuffer(ref pd.postCompletionDirtyColumns);
        MeshGenerator.ReturnUlongBuffer(ref pd.subchunkColliderOccupancy);
        MeshGenerator.ReturnBoolBuffer(ref pd.subchunkNonEmpty);
        pd.postOverrideRefreshScheduled = false;
    }

    private void DisposePendingMesh(PendingMesh pm)
    {
        MeshGenerator.ReturnMeshVertexList(ref pm.vertices);
        MeshGenerator.ReturnMeshIndexList(ref pm.opaqueTriangles);
        MeshGenerator.ReturnMeshIndexList(ref pm.transparentTriangles);
        MeshGenerator.ReturnMeshIndexList(ref pm.billboardTriangles);
        MeshGenerator.ReturnMeshIndexList(ref pm.waterTriangles);
        if (pm.suppressedBillboards.IsCreated) pm.suppressedBillboards.Dispose();
        MeshGenerator.ReturnSubchunkRangeBuffer(ref pm.subchunkRanges);
        MeshGenerator.ReturnUlongBuffer(ref pm.subchunkVisibilityMasks);
    }

    private void QueueChunkDataBufferReturn(JobHandle handle, ref PendingData pd)
    {
        pendingChunkDataBufferReturns.Add(new PendingChunkDataBufferReturn
        {
            handle = handle,
            heightCache = pd.heightCache,
            blockTypes = pd.blockTypes,
            blockPlacementAxes = pd.blockPlacementAxes,
            knownVoxelData = pd.knownVoxelData,
            solids = pd.solids,
            light = pd.light,
            subchunkNonEmpty = pd.subchunkNonEmpty
        });

        pd.heightCache = default;
        pd.blockTypes = default;
        pd.blockPlacementAxes = default;
        pd.knownVoxelData = default;
        pd.solids = default;
        pd.light = default;
        pd.subchunkNonEmpty = default;
    }

    private void ProcessPendingChunkDataBufferReturns()
    {
        for (int i = pendingChunkDataBufferReturns.Count - 1; i >= 0; i--)
        {
            PendingChunkDataBufferReturn pendingReturn = pendingChunkDataBufferReturns[i];
            if (!pendingReturn.handle.IsCompleted)
                continue;

            pendingReturn.handle.Complete();
            ReturnChunkDataBuffers(ref pendingReturn);
            RemovePendingChunkDataBufferReturnAtSwapBack(i);
        }
    }

    private void CompletePendingChunkDataBufferReturns()
    {
        for (int i = pendingChunkDataBufferReturns.Count - 1; i >= 0; i--)
        {
            PendingChunkDataBufferReturn pendingReturn = pendingChunkDataBufferReturns[i];
            pendingReturn.handle.Complete();
            ReturnChunkDataBuffers(ref pendingReturn);
        }

        pendingChunkDataBufferReturns.Clear();
    }

    private static void ReturnChunkDataBuffers(ref PendingChunkDataBufferReturn pendingReturn)
    {
        MeshGenerator.ReturnIntBuffer(ref pendingReturn.heightCache);
        MeshGenerator.ReturnByteBuffer(ref pendingReturn.blockTypes);
        MeshGenerator.ReturnByteBuffer(ref pendingReturn.blockPlacementAxes);
        MeshGenerator.ReturnByteBuffer(ref pendingReturn.knownVoxelData);
        MeshGenerator.ReturnBoolBuffer(ref pendingReturn.solids);
        MeshGenerator.ReturnByteBuffer(ref pendingReturn.light);
        MeshGenerator.ReturnBoolBuffer(ref pendingReturn.subchunkNonEmpty);
    }

    private void RemovePendingChunkDataBufferReturnAtSwapBack(int index)
    {
        int last = pendingChunkDataBufferReturns.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingChunkDataBufferReturns[index] = pendingChunkDataBufferReturns[last];

        pendingChunkDataBufferReturns.RemoveAt(last);
    }
}
