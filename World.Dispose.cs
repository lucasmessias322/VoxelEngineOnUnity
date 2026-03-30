using System;
using Unity.Collections;

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
        SafeDisposeNativeArray(ref pd.chunkLightData);
        SafeDisposeNativeArray(ref pd.lightOpacityData);
        SafeDisposeNativeArray(ref pd.fastRebuildSnapshotVoxelData);
        SafeDisposeNativeArray(ref pd.fastRebuildSnapshotLoadedChunks);
        SafeDisposeNativeArray(ref pd.fastRebuildOverrides);
    }

    private static void DisposeDataJobResources(ref PendingData pd)
    {
        SafeDisposeNativeArray(ref pd.heightCache);
        SafeDisposeNativeArray(ref pd.blockTypes);
        SafeDisposeNativeArray(ref pd.knownVoxelData);
        SafeDisposeNativeArray(ref pd.solids);
        SafeDisposeNativeArray(ref pd.light);
        SafeDisposeNativeArray(ref pd.chunkLightData);
        SafeDisposeNativeArray(ref pd.lightOpacityData);
        SafeDisposeNativeArray(ref pd.edits);
        SafeDisposeNativeArray(ref pd.fastRebuildSnapshotVoxelData);
        SafeDisposeNativeArray(ref pd.fastRebuildSnapshotLoadedChunks);
        SafeDisposeNativeArray(ref pd.fastRebuildOverrides);
        SafeDisposeNativeArray(ref pd.subchunkNonEmpty);
    }

    private void DisposePendingMesh(PendingMesh pm)
    {
        if (pm.vertices.IsCreated) pm.vertices.Dispose();
        if (pm.opaqueTriangles.IsCreated) pm.opaqueTriangles.Dispose();
        if (pm.compactOpaqueFaces.IsCreated) pm.compactOpaqueFaces.Dispose();
        if (pm.transparentTriangles.IsCreated) pm.transparentTriangles.Dispose();
        if (pm.billboardTriangles.IsCreated) pm.billboardTriangles.Dispose();
        if (pm.waterTriangles.IsCreated) pm.waterTriangles.Dispose();
        if (pm.suppressedBillboards.IsCreated) pm.suppressedBillboards.Dispose();
        if (pm.subchunkRanges.IsCreated) pm.subchunkRanges.Dispose();
        if (pm.subchunkVisibilityMasks.IsCreated) pm.subchunkVisibilityMasks.Dispose();
    }
}
