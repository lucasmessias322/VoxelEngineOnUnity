using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class World
{
    private void HandleBlockColliderToggle()
    {
        if (lastEnableBlockColliders == enableBlockColliders)
            return;

        lastEnableBlockColliders = enableBlockColliders;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk subchunk = chunk.subchunks[i];
                if (subchunk == null)
                    continue;

                subchunk.SetColliderSystemEnabled(chunkIsSimulated);

                if (chunkIsSimulated &&
                    chunk.hasVoxelData &&
                    subchunk.hasGeometry &&
                    subchunk.CanHaveColliders &&
                    !subchunk.HasColliderData)
                {
                    EnqueueColliderBuild(kv.Key, chunk.generation, i);
                }
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);

        if (!enableBlockColliders)
        {
            queuedColliderBuilds.Clear();
            queuedColliderBuildsByKey.Clear();
            return;
        }

        foreach (var kv in activeChunks)
            RequestHighBuildMeshRebuild(kv.Key);
    }

    private void HandleVisualFeatureToggle()
    {
        bool lightingChanged = lastEnableVoxelLighting != enableVoxelLighting;
        bool aoChanged = lastEnableAmbientOcclusion != enableAmbientOcclusion;
        bool horizontalLightingChanged = enableVoxelLighting && lastEnableHorizontalSkylight != enableHorizontalSkylight;
        bool horizontalLightingParamsChanged =
            enableVoxelLighting &&
            enableHorizontalSkylight &&
            (lastHorizontalSkylightStepLoss != horizontalSkylightStepLoss ||
             lastSunlightSmoothingPadding != sunlightSmoothingPadding);

        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;

        if (!lightingChanged && !aoChanged && !horizontalLightingChanged && !horizontalLightingParamsChanged)
            return;

        foreach (var kv in activeChunks)
            RequestChunkRebuild(kv.Key, GetFullSubchunkMask(), false);
    }

    private void RefreshSimulationDistanceState(Vector2Int simulationCenter)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk subchunk = chunk.subchunks[i];
                if (subchunk == null)
                    continue;

                if (!chunkIsSimulated)
                {
                    subchunk.SetColliderSystemEnabled(false);
                    continue;
                }

                if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated || !subchunk.hasGeometry || !subchunk.CanHaveColliders)
                {
                    subchunk.SetColliderSystemEnabled(false);
                    continue;
                }

                if (subchunk.HasColliderData)
                {
                    subchunk.SetColliderSystemEnabled(true);
                    continue;
                }

                EnqueueColliderBuild(kv.Key, chunk.generation, i);
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }
}

public partial class World
{
    private struct DistantChunkClusterSourceSubmesh
    {
        public Mesh mesh;
        public int subMeshIndex;
        public Matrix4x4 transform;
    }

    private struct DistantChunkClusterMergeSourceInfo
    {
        public int meshDataIndex;
        public int outputVertexStart;
        public int outputIndexStart;
        public int vertexCount;
        public int indexStart;
        public int indexCount;
        public int baseVertex;
        public byte usesUInt32Indices;
        public float4x4 transform;
    }

    private sealed class DistantChunkClusterCategoryRenderer
    {
        public GameObject root;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;
        public Mesh mesh;
        public int materialIndex;
        public readonly List<DistantChunkClusterSourceSubmesh> sources = new List<DistantChunkClusterSourceSubmesh>(32);
    }

    private sealed class DistantChunkClusterTintGroupData
    {
        public int tintKey;
        public Color grassTint;
        public GameObject root;
        public DistantChunkClusterCategoryRenderer opaque;
        public DistantChunkClusterCategoryRenderer transparent;
        public DistantChunkClusterCategoryRenderer water;
    }

    private sealed class DistantChunkClusterPendingCategoryMerge
    {
        public DistantChunkClusterCategoryRenderer categoryRenderer;
        public string meshName;
        public Mesh.MeshDataArray sourceMeshDataArray;
        public bool hasSourceMeshDataArray;
        public NativeArray<DistantChunkClusterMergeSourceInfo> sourceInfos;
        public NativeArray<MeshGenerator.PackedChunkVertex> outputVertices;
        public NativeArray<int> outputIndices;
        public JobHandle handle;
        public int vertexCount;
        public int indexCount;

        public void Dispose()
        {
            if (handle.IsCompleted)
                handle.Complete();

            if (hasSourceMeshDataArray)
            {
                sourceMeshDataArray.Dispose();
                hasSourceMeshDataArray = false;
            }

            if (sourceInfos.IsCreated)
                sourceInfos.Dispose();

            if (outputVertices.IsCreated)
                outputVertices.Dispose();

            if (outputIndices.IsCreated)
                outputIndices.Dispose();
        }
    }

    private sealed class DistantChunkClusterPendingTintGroupMerge
    {
        public int tintKey;
        public Color grassTint;
        public DistantChunkClusterTintGroupData tintGroup;
        public DistantChunkClusterPendingCategoryMerge opaque;
        public DistantChunkClusterPendingCategoryMerge transparent;
        public DistantChunkClusterPendingCategoryMerge water;
        public JobHandle combinedHandle;
    }

    private sealed class DistantChunkClusterPendingMerge
    {
        public Vector2Int clusterCoord;
        public readonly List<DistantChunkClusterPendingTintGroupMerge> tintGroupMerges = new List<DistantChunkClusterPendingTintGroupMerge>(4);
        public Vector2Int[] renderedChunkCoords = Array.Empty<Vector2Int>();
        public int[] activeTintKeys = Array.Empty<int>();
        public JobHandle combinedHandle;
    }

    private sealed class DistantChunkClusterData
    {
        public Vector2Int clusterCoord;
        public readonly HashSet<Vector2Int> memberCoords = new HashSet<Vector2Int>();
        public readonly HashSet<Vector2Int> renderedChunkCoords = new HashSet<Vector2Int>();
        public GameObject root;
        public readonly Dictionary<int, DistantChunkClusterTintGroupData> tintGroups = new Dictionary<int, DistantChunkClusterTintGroupData>();
        public DistantChunkClusterPendingMerge pendingMerge;
        public bool rebuildQueuedWhilePending;
    }

    [BurstCompile]
    private struct DistantChunkClusterMergeJob : IJobParallelFor
    {
        [ReadOnly] public Mesh.MeshDataArray sourceMeshDataArray;
        [ReadOnly] public NativeArray<DistantChunkClusterMergeSourceInfo> sourceInfos;
        [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<MeshGenerator.PackedChunkVertex> outputVertices;
        [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<int> outputIndices;

        public void Execute(int sourceIndex)
        {
            DistantChunkClusterMergeSourceInfo sourceInfo = sourceInfos[sourceIndex];
            Mesh.MeshData sourceMeshData = sourceMeshDataArray[sourceInfo.meshDataIndex];
            NativeArray<MeshGenerator.PackedChunkVertex> sourceVertices = sourceMeshData.GetVertexData<MeshGenerator.PackedChunkVertex>();
            float3x3 normalMatrix = new float3x3(
                sourceInfo.transform.c0.xyz,
                sourceInfo.transform.c1.xyz,
                sourceInfo.transform.c2.xyz);

            for (int i = 0; i < sourceInfo.vertexCount; i++)
            {
                MeshGenerator.PackedChunkVertex sourceVertex = sourceVertices[i];
                float3 transformedNormal = math.mul(normalMatrix, (float3)sourceVertex.normal);
                float normalLengthSq = math.lengthsq(transformedNormal);
                sourceVertex.position = math.transform(sourceInfo.transform, (float3)sourceVertex.position);
                sourceVertex.normal = normalLengthSq > 1e-8f
                    ? math.normalize(transformedNormal)
                    : math.up();
                outputVertices[sourceInfo.outputVertexStart + i] = sourceVertex;
            }

            if (sourceInfo.usesUInt32Indices != 0)
            {
                NativeArray<int> sourceIndices = sourceMeshData.GetIndexData<int>();
                for (int i = 0; i < sourceInfo.indexCount; i++)
                {
                    outputIndices[sourceInfo.outputIndexStart + i] =
                        sourceInfo.outputVertexStart +
                        sourceInfo.baseVertex +
                        sourceIndices[sourceInfo.indexStart + i];
                }

                return;
            }

            NativeArray<ushort> sourceIndices16 = sourceMeshData.GetIndexData<ushort>();
            for (int i = 0; i < sourceInfo.indexCount; i++)
            {
                outputIndices[sourceInfo.outputIndexStart + i] =
                    sourceInfo.outputVertexStart +
                    sourceInfo.baseVertex +
                    sourceIndices16[sourceInfo.indexStart + i];
            }
        }
    }

    private readonly Dictionary<Vector2Int, DistantChunkClusterData> distantChunkClusters = new Dictionary<Vector2Int, DistantChunkClusterData>();
    private readonly Dictionary<Vector2Int, Vector2Int> distantChunkClusterByChunk = new Dictionary<Vector2Int, Vector2Int>();
    private readonly Dictionary<Vector2Int, int> distantChunkClusterRebuildDueFrameByCoord = new Dictionary<Vector2Int, int>();
    private readonly Dictionary<Vector2Int, Color> distantChunkGrassTintByChunk = new Dictionary<Vector2Int, Color>();
    private readonly Queue<Vector2Int> queuedDistantChunkClusterRebuilds = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedDistantChunkClusterRebuildsSet = new HashSet<Vector2Int>();
    private readonly List<DistantChunkClusterData> distantChunkClustersWithPendingMerges = new List<DistantChunkClusterData>();
    private readonly HashSet<Vector2Int> distantChunkRenderedChunksScratch = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> distantChunkToUnsuppressScratch = new List<Vector2Int>();
    private readonly List<int> distantChunkActiveTintKeysScratch = new List<int>(8);
    private const MeshUpdateFlags DistantChunkClusterMeshUploadFlags =
        MeshUpdateFlags.DontRecalculateBounds |
        MeshUpdateFlags.DontNotifyMeshUsers |
        MeshUpdateFlags.DontValidateIndices;

    private bool distantChunkBatchingConfigured;
    private bool lastEnableDistantChunkBatching;
    private int lastDistantChunkBatchStartDistance = -1;
    private int lastDistantChunkClusterSize = -1;
    private Material[] lastDistantChunkBatchMaterials;
    private bool distantChunkClusterAssignmentsDirty = true;
    private Vector2Int lastDistantChunkBatchCenter = new Vector2Int(int.MinValue, int.MinValue);

    private void UpdateDistantChunkBatching()
    {
        Vector2Int currentCenter = GetCurrentPlayerChunkCoord();
        bool configChanged = !distantChunkBatchingConfigured ||
                             lastEnableDistantChunkBatching != enableDistantChunkBatching ||
                             lastDistantChunkBatchStartDistance != distantChunkBatchStartDistance ||
                             lastDistantChunkClusterSize != distantChunkClusterSize ||
                             !HasSameMaterialReferences(lastDistantChunkBatchMaterials, Material);

        distantChunkBatchingConfigured = true;
        lastEnableDistantChunkBatching = enableDistantChunkBatching;
        lastDistantChunkBatchStartDistance = distantChunkBatchStartDistance;
        lastDistantChunkClusterSize = distantChunkClusterSize;
        lastDistantChunkBatchMaterials = Material;

        if (!IsDistantChunkBatchingAvailable())
        {
            ClearDistantChunkBatching(false);
            return;
        }

        if (configChanged)
            ClearDistantChunkBatching(false);

        ProcessCompletedDistantChunkClusterMergeJobs();

        if (configChanged || distantChunkClusterAssignmentsDirty || currentCenter != lastDistantChunkBatchCenter)
        {
            ReconcileDistantChunkBatchAssignments(currentCenter);
            lastDistantChunkBatchCenter = currentCenter;
            distantChunkClusterAssignmentsDirty = false;
        }

        ProcessQueuedDistantChunkClusterRebuilds();
    }

    private bool IsDistantChunkBatchingAvailable()
    {
        return enableDistantChunkBatching &&
               renderDistance > 0 &&
               GetResolvedDistantChunkBatchStartDistance() < renderDistance &&
               GetResolvedDistantChunkClusterSize() >= 2 &&
               Material != null &&
               Material.Length > 0;
    }

    private int GetResolvedDistantChunkBatchStartDistance()
    {
        return Mathf.Clamp(distantChunkBatchStartDistance, 0, Mathf.Max(0, renderDistance));
    }

    private int GetResolvedDistantChunkClusterSize()
    {
        return Mathf.Max(2, distantChunkClusterSize);
    }

    private void ReconcileDistantChunkBatchAssignments(Vector2Int center)
    {
        if (distantChunkClusterByChunk.Count > 0)
        {
            List<Vector2Int> staleCoords = new List<Vector2Int>();
            foreach (var kv in distantChunkClusterByChunk)
            {
                if (!activeChunks.ContainsKey(kv.Key))
                    staleCoords.Add(kv.Key);
            }

            for (int i = 0; i < staleCoords.Count; i++)
            {
                Vector2Int chunkCoord = staleCoords[i];
                if (distantChunkClusterByChunk.TryGetValue(chunkCoord, out Vector2Int clusterCoord))
                    RemoveChunkFromDistantChunkCluster(chunkCoord, clusterCoord);
            }
        }

        foreach (var kv in activeChunks)
        {
            Vector2Int chunkCoord = kv.Key;
            bool shouldBatch = TryGetDesiredDistantChunkClusterCoord(chunkCoord, center, out Vector2Int desiredClusterCoord);
            bool isAssigned = distantChunkClusterByChunk.TryGetValue(chunkCoord, out Vector2Int currentClusterCoord);

            if (shouldBatch)
            {
                if (!isAssigned || currentClusterCoord != desiredClusterCoord)
                {
                    if (isAssigned)
                        RemoveChunkFromDistantChunkCluster(chunkCoord, currentClusterCoord);

                    AddChunkToDistantChunkCluster(chunkCoord, desiredClusterCoord);
                }
            }
            else if (isAssigned)
            {
                RemoveChunkFromDistantChunkCluster(chunkCoord, currentClusterCoord);
            }
        }
    }

    private bool TryGetDesiredDistantChunkClusterCoord(Vector2Int chunkCoord, Vector2Int center, out Vector2Int clusterCoord)
    {
        if (!IsDistantChunkBatchingAvailable() ||
            !IsCoordInsideRenderDistance(chunkCoord, center) ||
            IsCoordInsideCircularDistance(chunkCoord, center, GetResolvedDistantChunkBatchStartDistance()))
        {
            clusterCoord = default;
            return false;
        }

        int clusterSize = GetResolvedDistantChunkClusterSize();
        clusterCoord = new Vector2Int(
            FloorDiv(chunkCoord.x, clusterSize),
            FloorDiv(chunkCoord.y, clusterSize));
        return true;
    }

    private void AddChunkToDistantChunkCluster(Vector2Int chunkCoord, Vector2Int clusterCoord)
    {
        DistantChunkClusterData cluster = GetOrCreateDistantChunkCluster(clusterCoord);
        distantChunkClusterByChunk[chunkCoord] = clusterCoord;
        cluster.memberCoords.Add(chunkCoord);
        distantChunkGrassTintByChunk[chunkCoord] = EvaluateChunkGrassTint(chunkCoord);
        QueueDistantChunkClusterRebuild(clusterCoord);
    }

    private void RemoveChunkFromDistantChunkCluster(Vector2Int chunkCoord, Vector2Int clusterCoord)
    {
        distantChunkClusterByChunk.Remove(chunkCoord);
        distantChunkGrassTintByChunk.Remove(chunkCoord);

        if (!distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData cluster))
            return;

        cluster.memberCoords.Remove(chunkCoord);

        if (cluster.memberCoords.Count == 0 &&
            cluster.renderedChunkCoords.Count == 0 &&
            cluster.pendingMerge == null)
        {
            DestroyDistantChunkCluster(clusterCoord);
            return;
        }

        QueueDistantChunkClusterRebuild(clusterCoord);
    }

    private DistantChunkClusterData GetOrCreateDistantChunkCluster(Vector2Int clusterCoord)
    {
        if (distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData existing))
            return existing;

        GameObject root = new GameObject($"DistantChunkCluster_{clusterCoord.x}_{clusterCoord.y}");
        root.transform.SetParent(transform, false);
        root.transform.position = GetDistantChunkClusterWorldOrigin(clusterCoord);
        root.layer = chunkPrefab != null ? chunkPrefab.layer : gameObject.layer;
        root.SetActive(false);

        DistantChunkClusterData created = new DistantChunkClusterData
        {
            clusterCoord = clusterCoord,
            root = root
        };

        distantChunkClusters.Add(clusterCoord, created);
        return created;
    }

    private DistantChunkClusterTintGroupData GetOrCreateDistantChunkClusterTintGroup(
        DistantChunkClusterData cluster,
        Color grassTint)
    {
        if (cluster == null || cluster.root == null)
            return null;

        int tintKey = PackGrassTintKey(grassTint);
        if (cluster.tintGroups.TryGetValue(tintKey, out DistantChunkClusterTintGroupData existing))
        {
            existing.grassTint = grassTint;
            return existing;
        }

        GameObject groupRoot = new GameObject($"Tint_{tintKey}");
        groupRoot.transform.SetParent(cluster.root.transform, false);
        groupRoot.transform.localPosition = Vector3.zero;
        groupRoot.layer = cluster.root.layer;
        groupRoot.SetActive(false);

        DistantChunkClusterTintGroupData created = new DistantChunkClusterTintGroupData
        {
            tintKey = tintKey,
            grassTint = grassTint,
            root = groupRoot,
            opaque = CreateDistantChunkClusterCategoryRenderer(groupRoot.transform, $"Opaque_{tintKey}", 0),
            transparent = CreateDistantChunkClusterCategoryRenderer(groupRoot.transform, $"Transparent_{tintKey}", 1),
            water = CreateDistantChunkClusterCategoryRenderer(groupRoot.transform, $"Water_{tintKey}", 2)
        };

        ApplyDistantChunkClusterTintGroupGrassTint(created);
        cluster.tintGroups.Add(tintKey, created);
        return created;
    }

    private DistantChunkClusterCategoryRenderer CreateDistantChunkClusterCategoryRenderer(Transform parent, string name, int materialIndex)
    {
        GameObject rendererRoot = new GameObject(name);
        rendererRoot.transform.SetParent(parent, false);
        rendererRoot.transform.localPosition = Vector3.zero;
        rendererRoot.layer = parent.gameObject.layer;

        MeshFilter meshFilter = rendererRoot.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = rendererRoot.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = GetDistantChunkBatchMaterial(materialIndex);
        rendererRoot.SetActive(false);

        return new DistantChunkClusterCategoryRenderer
        {
            root = rendererRoot,
            meshFilter = meshFilter,
            meshRenderer = meshRenderer,
            materialIndex = materialIndex
        };
    }

    private void ApplyDistantChunkClusterTintGroupGrassTint(DistantChunkClusterTintGroupData tintGroup)
    {
        if (tintGroup == null)
            return;

        ApplyDistantChunkClusterCategoryGrassTint(tintGroup.opaque, tintGroup.grassTint);
        ApplyDistantChunkClusterCategoryGrassTint(tintGroup.transparent, tintGroup.grassTint);
        ApplyDistantChunkClusterCategoryGrassTint(tintGroup.water, tintGroup.grassTint);
    }

    private void ApplyDistantChunkClusterCategoryGrassTint(
        DistantChunkClusterCategoryRenderer categoryRenderer,
        Color grassTint)
    {
        if (categoryRenderer?.meshRenderer == null)
            return;

        ApplyBiomeTintToRenderer(categoryRenderer.meshRenderer, grassTint);
    }

    private Color GetCachedDistantChunkGrassTint(Vector2Int chunkCoord)
    {
        if (distantChunkGrassTintByChunk.TryGetValue(chunkCoord, out Color cachedGrassTint))
            return cachedGrassTint;

        Color grassTint = EvaluateChunkGrassTint(chunkCoord);
        distantChunkGrassTintByChunk[chunkCoord] = grassTint;
        return grassTint;
    }

    private Vector3 GetDistantChunkClusterWorldOrigin(Vector2Int clusterCoord)
    {
        int clusterSize = GetResolvedDistantChunkClusterSize();
        return new Vector3(
            clusterCoord.x * clusterSize * Chunk.SizeX,
            0f,
            clusterCoord.y * clusterSize * Chunk.SizeZ);
    }

    private Material GetDistantChunkBatchMaterial(int materialIndex)
    {
        if (Material == null || Material.Length == 0)
            return null;

        int safeIndex = Mathf.Clamp(materialIndex, 0, Material.Length - 1);
        return Material[safeIndex];
    }

    private void QueueDistantChunkClusterRebuild(Vector2Int clusterCoord)
    {
        int dueFrame = Time.frameCount + Mathf.Max(0, distantChunkClusterRebuildDebounceFrames);
        distantChunkClusterRebuildDueFrameByCoord[clusterCoord] = dueFrame;

        if (distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData cluster) &&
            cluster.pendingMerge != null)
        {
            cluster.rebuildQueuedWhilePending = true;
            return;
        }

        if (!queuedDistantChunkClusterRebuildsSet.Add(clusterCoord))
            return;

        queuedDistantChunkClusterRebuilds.Enqueue(clusterCoord);
    }

    private void ProcessQueuedDistantChunkClusterRebuilds()
    {
        if (queuedDistantChunkClusterRebuilds.Count == 0)
            return;

        int rebuildBudget = Mathf.Max(1, maxDistantChunkClusterRebuildsPerFrame);
        int attempts = queuedDistantChunkClusterRebuilds.Count;
        while (rebuildBudget > 0 && attempts-- > 0 && queuedDistantChunkClusterRebuilds.Count > 0)
        {
            Vector2Int clusterCoord = queuedDistantChunkClusterRebuilds.Dequeue();
            if (!distantChunkClusters.ContainsKey(clusterCoord))
            {
                queuedDistantChunkClusterRebuildsSet.Remove(clusterCoord);
                distantChunkClusterRebuildDueFrameByCoord.Remove(clusterCoord);
                continue;
            }

            if (distantChunkClusterRebuildDueFrameByCoord.TryGetValue(clusterCoord, out int dueFrame) &&
                dueFrame > Time.frameCount)
            {
                queuedDistantChunkClusterRebuilds.Enqueue(clusterCoord);
                continue;
            }

            queuedDistantChunkClusterRebuildsSet.Remove(clusterCoord);
            distantChunkClusterRebuildDueFrameByCoord.Remove(clusterCoord);

            RebuildDistantChunkCluster(clusterCoord);
            rebuildBudget--;
        }
    }

    private void RebuildDistantChunkCluster(Vector2Int clusterCoord)
    {
        if (!distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData cluster))
            return;

        if (cluster.pendingMerge != null)
        {
            cluster.rebuildQueuedWhilePending = true;
            return;
        }

        cluster.root.transform.position = GetDistantChunkClusterWorldOrigin(clusterCoord);
        ClearDistantChunkClusterTintGroupSources(cluster);
        distantChunkRenderedChunksScratch.Clear();

        foreach (Vector2Int chunkCoord in cluster.memberCoords)
        {
            if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk) || chunk == null)
                continue;

            DistantChunkClusterTintGroupData tintGroup = GetOrCreateDistantChunkClusterTintGroup(cluster, GetCachedDistantChunkGrassTint(chunkCoord));
            if (tintGroup == null)
                continue;

            if (AppendChunkToDistantClusterSources(cluster, chunk, tintGroup.opaque.sources, tintGroup.transparent.sources, tintGroup.water.sources))
                distantChunkRenderedChunksScratch.Add(chunkCoord);
        }

        if (!HasAnyDistantChunkClusterTintGroupSources(cluster))
        {
            ApplyDistantChunkClusterRenderedChunks(cluster, distantChunkRenderedChunksScratch);
            ClearDistantChunkClusterVisual(cluster);

            if (cluster.memberCoords.Count == 0 && cluster.renderedChunkCoords.Count == 0)
                DestroyDistantChunkCluster(clusterCoord);

            return;
        }

        ScheduleDistantChunkClusterMerge(cluster, CreateDistantChunkRenderedChunkArray(distantChunkRenderedChunksScratch));
    }

    private static void ClearDistantChunkClusterTintGroupSources(DistantChunkClusterData cluster)
    {
        if (cluster == null || cluster.tintGroups.Count == 0)
            return;

        foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
        {
            tintGroup?.opaque?.sources.Clear();
            tintGroup?.transparent?.sources.Clear();
            tintGroup?.water?.sources.Clear();
        }
    }

    private static bool HasAnyDistantChunkClusterTintGroupSources(DistantChunkClusterData cluster)
    {
        if (cluster == null || cluster.tintGroups.Count == 0)
            return false;

        foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
        {
            if (tintGroup == null)
                continue;

            if ((tintGroup.opaque != null && tintGroup.opaque.sources.Count > 0) ||
                (tintGroup.transparent != null && tintGroup.transparent.sources.Count > 0) ||
                (tintGroup.water != null && tintGroup.water.sources.Count > 0))
            {
                return true;
            }
        }

        return false;
    }

    private bool AppendChunkToDistantClusterSources(
        DistantChunkClusterData cluster,
        Chunk chunk,
        List<DistantChunkClusterSourceSubmesh> opaqueSources,
        List<DistantChunkClusterSourceSubmesh> transparentSources,
        List<DistantChunkClusterSourceSubmesh> waterSources)
    {
        if (chunk.visualSlices == null || cluster.root == null)
            return false;

        bool contributed = false;
        Matrix4x4 clusterWorldToLocal = cluster.root.transform.worldToLocalMatrix;

        for (int i = 0; i < chunk.visualSlices.Length; i++)
        {
            ChunkRenderSlice visualSlice = chunk.visualSlices[i];
            if (visualSlice == null || !visualSlice.HasGeometry)
                continue;

            Mesh sourceMesh = visualSlice.SharedMesh;
            if (sourceMesh == null || sourceMesh.vertexCount == 0 || sourceMesh.subMeshCount == 0)
                continue;

            Matrix4x4 sliceTransform = clusterWorldToLocal * visualSlice.transform.localToWorldMatrix;
            if (AppendMeshSubmeshToSourceList(sourceMesh, 0, sliceTransform, opaqueSources))
                contributed = true;
            if (AppendMeshSubmeshToSourceList(sourceMesh, 1, sliceTransform, transparentSources))
                contributed = true;
            if (AppendMeshSubmeshToSourceList(sourceMesh, 2, sliceTransform, waterSources))
                contributed = true;
        }

        return contributed;
    }

    private static bool AppendMeshSubmeshToSourceList(
        Mesh sourceMesh,
        int subMeshIndex,
        Matrix4x4 transform,
        List<DistantChunkClusterSourceSubmesh> output)
    {
        if (sourceMesh == null ||
            subMeshIndex < 0 ||
            subMeshIndex >= sourceMesh.subMeshCount ||
            sourceMesh.GetIndexCount(subMeshIndex) == 0)
        {
            return false;
        }

        output.Add(new DistantChunkClusterSourceSubmesh
        {
            mesh = sourceMesh,
            subMeshIndex = subMeshIndex,
            transform = transform
        });
        return true;
    }

    private void ScheduleDistantChunkClusterMerge(DistantChunkClusterData cluster, Vector2Int[] renderedChunkCoords)
    {
        if (cluster == null)
            return;

        DistantChunkClusterPendingMerge pendingMerge = new DistantChunkClusterPendingMerge
        {
            clusterCoord = cluster.clusterCoord,
            renderedChunkCoords = renderedChunkCoords ?? Array.Empty<Vector2Int>()
        };

        JobHandle combinedHandle = default;
        bool hasScheduledJob = false;
        distantChunkActiveTintKeysScratch.Clear();

        foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
        {
            DistantChunkClusterPendingTintGroupMerge pendingTintGroupMerge = ScheduleDistantChunkClusterTintGroupMerge(
                cluster,
                tintGroup);
            if (pendingTintGroupMerge == null)
                continue;

            pendingMerge.tintGroupMerges.Add(pendingTintGroupMerge);
            distantChunkActiveTintKeysScratch.Add(pendingTintGroupMerge.tintKey);

            combinedHandle = hasScheduledJob
                ? JobHandle.CombineDependencies(combinedHandle, pendingTintGroupMerge.combinedHandle)
                : pendingTintGroupMerge.combinedHandle;
            hasScheduledJob = true;
        }

        if (!hasScheduledJob)
        {
            ApplyDistantChunkClusterRenderedChunks(cluster, pendingMerge.renderedChunkCoords);
            ClearDistantChunkClusterVisual(cluster);

            if (cluster.memberCoords.Count == 0 && cluster.renderedChunkCoords.Count == 0)
                DestroyDistantChunkCluster(cluster.clusterCoord);

            return;
        }

        pendingMerge.activeTintKeys = distantChunkActiveTintKeysScratch.Count > 0
            ? distantChunkActiveTintKeysScratch.ToArray()
            : Array.Empty<int>();
        pendingMerge.combinedHandle = combinedHandle;
        cluster.pendingMerge = pendingMerge;
        cluster.rebuildQueuedWhilePending = false;
        distantChunkClustersWithPendingMerges.Add(cluster);
    }

    private DistantChunkClusterPendingTintGroupMerge ScheduleDistantChunkClusterTintGroupMerge(
        DistantChunkClusterData cluster,
        DistantChunkClusterTintGroupData tintGroup)
    {
        if (cluster == null || tintGroup == null)
            return null;

        DistantChunkClusterPendingTintGroupMerge pendingTintGroupMerge = new DistantChunkClusterPendingTintGroupMerge
        {
            tintKey = tintGroup.tintKey,
            grassTint = tintGroup.grassTint,
            tintGroup = tintGroup
        };

        pendingTintGroupMerge.opaque = ScheduleDistantChunkClusterCategoryMerge(
            tintGroup.opaque,
            $"DistantClusterOpaque_{cluster.clusterCoord.x}_{cluster.clusterCoord.y}_{tintGroup.tintKey}");
        pendingTintGroupMerge.transparent = ScheduleDistantChunkClusterCategoryMerge(
            tintGroup.transparent,
            $"DistantClusterTransparent_{cluster.clusterCoord.x}_{cluster.clusterCoord.y}_{tintGroup.tintKey}");
        pendingTintGroupMerge.water = ScheduleDistantChunkClusterCategoryMerge(
            tintGroup.water,
            $"DistantClusterWater_{cluster.clusterCoord.x}_{cluster.clusterCoord.y}_{tintGroup.tintKey}");

        JobHandle combinedHandle = default;
        bool hasScheduledJob = false;

        if (pendingTintGroupMerge.opaque != null)
        {
            combinedHandle = pendingTintGroupMerge.opaque.handle;
            hasScheduledJob = true;
        }

        if (pendingTintGroupMerge.transparent != null)
        {
            combinedHandle = hasScheduledJob
                ? JobHandle.CombineDependencies(combinedHandle, pendingTintGroupMerge.transparent.handle)
                : pendingTintGroupMerge.transparent.handle;
            hasScheduledJob = true;
        }

        if (pendingTintGroupMerge.water != null)
        {
            combinedHandle = hasScheduledJob
                ? JobHandle.CombineDependencies(combinedHandle, pendingTintGroupMerge.water.handle)
                : pendingTintGroupMerge.water.handle;
            hasScheduledJob = true;
        }

        if (!hasScheduledJob)
            return null;

        pendingTintGroupMerge.combinedHandle = combinedHandle;
        return pendingTintGroupMerge;
    }

    private DistantChunkClusterPendingCategoryMerge ScheduleDistantChunkClusterCategoryMerge(
        DistantChunkClusterCategoryRenderer categoryRenderer,
        string meshName)
    {
        if (categoryRenderer == null ||
            categoryRenderer.sources == null ||
            categoryRenderer.sources.Count == 0)
        {
            return null;
        }

        List<DistantChunkClusterSourceSubmesh> sources = categoryRenderer.sources;
        int validSourceCount = 0;
        int totalVertexCount = 0;
        int totalIndexCount = 0;

        for (int i = 0; i < sources.Count; i++)
        {
            DistantChunkClusterSourceSubmesh source = sources[i];
            if (source.mesh == null ||
                source.subMeshIndex < 0 ||
                source.subMeshIndex >= source.mesh.subMeshCount)
            {
                continue;
            }

            int vertexCount = source.mesh.vertexCount;
            SubMeshDescriptor subMesh = source.mesh.GetSubMesh(source.subMeshIndex);
            if (vertexCount <= 0 || subMesh.indexCount <= 0)
            {
                continue;
            }

            validSourceCount++;
            totalVertexCount += vertexCount;
            totalIndexCount += subMesh.indexCount;
        }

        if (validSourceCount == 0 || totalVertexCount == 0 || totalIndexCount == 0)
            return null;

        Mesh[] sourceMeshes = new Mesh[validSourceCount];
        NativeArray<DistantChunkClusterMergeSourceInfo> sourceInfos =
            new NativeArray<DistantChunkClusterMergeSourceInfo>(validSourceCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        int sourceWriteIndex = 0;
        int outputVertexStart = 0;
        int outputIndexStart = 0;

        for (int i = 0; i < sources.Count; i++)
        {
            DistantChunkClusterSourceSubmesh source = sources[i];
            if (source.mesh == null ||
                source.subMeshIndex < 0 ||
                source.subMeshIndex >= source.mesh.subMeshCount)
            {
                continue;
            }

            int vertexCount = source.mesh.vertexCount;
            SubMeshDescriptor subMesh = source.mesh.GetSubMesh(source.subMeshIndex);
            if (vertexCount <= 0 || subMesh.indexCount <= 0)
                continue;

            sourceMeshes[sourceWriteIndex] = source.mesh;
            sourceInfos[sourceWriteIndex] = new DistantChunkClusterMergeSourceInfo
            {
                meshDataIndex = sourceWriteIndex,
                outputVertexStart = outputVertexStart,
                outputIndexStart = outputIndexStart,
                vertexCount = vertexCount,
                indexStart = subMesh.indexStart,
                indexCount = subMesh.indexCount,
                baseVertex = subMesh.baseVertex,
                usesUInt32Indices = source.mesh.indexFormat == IndexFormat.UInt32 ? (byte)1 : (byte)0,
                transform = ToFloat4x4(source.transform)
            };

            outputVertexStart += vertexCount;
            outputIndexStart += subMesh.indexCount;
            sourceWriteIndex++;
        }

        DistantChunkClusterPendingCategoryMerge pendingCategoryMerge = new DistantChunkClusterPendingCategoryMerge
        {
            categoryRenderer = categoryRenderer,
            meshName = meshName,
            sourceInfos = sourceInfos,
            outputVertices = new NativeArray<MeshGenerator.PackedChunkVertex>(totalVertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            outputIndices = new NativeArray<int>(totalIndexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            vertexCount = totalVertexCount,
            indexCount = totalIndexCount
        };

        pendingCategoryMerge.sourceMeshDataArray = Mesh.AcquireReadOnlyMeshData(sourceMeshes);
        pendingCategoryMerge.hasSourceMeshDataArray = true;

        DistantChunkClusterMergeJob mergeJob = new DistantChunkClusterMergeJob
        {
            sourceMeshDataArray = pendingCategoryMerge.sourceMeshDataArray,
            sourceInfos = pendingCategoryMerge.sourceInfos,
            outputVertices = pendingCategoryMerge.outputVertices,
            outputIndices = pendingCategoryMerge.outputIndices
        };

        pendingCategoryMerge.handle = mergeJob.Schedule(validSourceCount, 1);
        return pendingCategoryMerge;
    }

    private void ProcessCompletedDistantChunkClusterMergeJobs()
    {
        int completionsRemaining = Mathf.Max(1, maxDistantChunkClusterMergeCompletionsPerFrame);
        for (int i = distantChunkClustersWithPendingMerges.Count - 1; i >= 0; i--)
        {
            if (completionsRemaining <= 0)
                break;

            DistantChunkClusterData cluster = distantChunkClustersWithPendingMerges[i];
            if (cluster == null || cluster.pendingMerge == null)
            {
                distantChunkClustersWithPendingMerges.RemoveAt(i);
                continue;
            }

            if (!cluster.pendingMerge.combinedHandle.IsCompleted)
                continue;

            DistantChunkClusterPendingMerge pendingMerge = cluster.pendingMerge;
            pendingMerge.combinedHandle.Complete();
            distantChunkClustersWithPendingMerges.RemoveAt(i);
            cluster.pendingMerge = null;

            for (int mergeIndex = 0; mergeIndex < pendingMerge.tintGroupMerges.Count; mergeIndex++)
            {
                DistantChunkClusterPendingTintGroupMerge pendingTintGroupMerge = pendingMerge.tintGroupMerges[mergeIndex];
                if (pendingTintGroupMerge?.tintGroup == null)
                    continue;

                ApplyCompletedDistantChunkClusterCategoryMerge(pendingTintGroupMerge.tintGroup.opaque, pendingTintGroupMerge.opaque);
                ApplyCompletedDistantChunkClusterCategoryMerge(pendingTintGroupMerge.tintGroup.transparent, pendingTintGroupMerge.transparent);
                ApplyCompletedDistantChunkClusterCategoryMerge(pendingTintGroupMerge.tintGroup.water, pendingTintGroupMerge.water);
                ApplyDistantChunkClusterTintGroupGrassTint(pendingTintGroupMerge.tintGroup);
                UpdateDistantChunkClusterTintGroupRootState(pendingTintGroupMerge.tintGroup);
            }

            ClearInactiveDistantChunkClusterTintGroups(cluster, pendingMerge.activeTintKeys);
            UpdateDistantChunkClusterRootState(cluster);
            ApplyDistantChunkClusterRenderedChunks(cluster, pendingMerge.renderedChunkCoords);
            DisposeDistantChunkClusterPendingMerge(pendingMerge);

            bool rebuildQueuedWhilePending = cluster.rebuildQueuedWhilePending;
            cluster.rebuildQueuedWhilePending = false;

            if (cluster.memberCoords.Count == 0 && cluster.renderedChunkCoords.Count == 0)
            {
                DestroyDistantChunkCluster(cluster.clusterCoord);
                continue;
            }

            if (rebuildQueuedWhilePending)
                QueueDistantChunkClusterRebuild(cluster.clusterCoord);

            completionsRemaining--;
        }
    }

    private void ClearInactiveDistantChunkClusterTintGroups(DistantChunkClusterData cluster, int[] activeTintKeys)
    {
        if (cluster == null || cluster.tintGroups.Count == 0)
            return;

        distantChunkActiveTintKeysScratch.Clear();
        if (activeTintKeys != null)
        {
            for (int i = 0; i < activeTintKeys.Length; i++)
                distantChunkActiveTintKeysScratch.Add(activeTintKeys[i]);
        }

        foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
        {
            if (tintGroup == null)
                continue;

            if (distantChunkActiveTintKeysScratch.Contains(tintGroup.tintKey))
                continue;

            ClearDistantChunkClusterTintGroupVisual(tintGroup);
        }
    }

    private void ApplyCompletedDistantChunkClusterCategoryMerge(
        DistantChunkClusterCategoryRenderer categoryRenderer,
        DistantChunkClusterPendingCategoryMerge pendingCategoryMerge)
    {
        if (categoryRenderer == null)
            return;

        if (pendingCategoryMerge == null || pendingCategoryMerge.vertexCount <= 0 || pendingCategoryMerge.indexCount <= 0)
        {
            ClearDistantChunkClusterCategoryVisual(categoryRenderer);
            return;
        }

        Mesh mesh = GetOrCreateDistantChunkClusterCategoryMesh(categoryRenderer, pendingCategoryMerge.meshName);
        mesh.Clear(false);
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertexBufferParams(pendingCategoryMerge.vertexCount, ChunkRenderSlice.SharedVertexLayout);
        mesh.SetIndexBufferParams(pendingCategoryMerge.indexCount, IndexFormat.UInt32);
        mesh.subMeshCount = 1;
        mesh.SetVertexBufferData(
            pendingCategoryMerge.outputVertices,
            0,
            0,
            pendingCategoryMerge.vertexCount,
            0,
            DistantChunkClusterMeshUploadFlags);
        mesh.SetIndexBufferData(
            pendingCategoryMerge.outputIndices,
            0,
            0,
            pendingCategoryMerge.indexCount,
            DistantChunkClusterMeshUploadFlags);
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, pendingCategoryMerge.indexCount, MeshTopology.Triangles), DistantChunkClusterMeshUploadFlags);
        mesh.bounds = GetDistantChunkClusterLocalBounds();

        if (categoryRenderer.meshFilter != null)
            categoryRenderer.meshFilter.sharedMesh = mesh;

        ApplyDistantChunkClusterCategoryMesh(categoryRenderer, true);
    }

    private static Mesh GetOrCreateDistantChunkClusterCategoryMesh(
        DistantChunkClusterCategoryRenderer categoryRenderer,
        string meshName)
    {
        if (categoryRenderer.mesh == null)
        {
            categoryRenderer.mesh = new Mesh
            {
                name = meshName,
                indexFormat = IndexFormat.UInt32
            };
            categoryRenderer.mesh.MarkDynamic();
        }

        categoryRenderer.mesh.name = meshName;
        return categoryRenderer.mesh;
    }

    private static void DisposeDistantChunkClusterPendingMerge(DistantChunkClusterPendingMerge pendingMerge)
    {
        if (pendingMerge == null)
            return;

        for (int i = 0; i < pendingMerge.tintGroupMerges.Count; i++)
        {
            DistantChunkClusterPendingTintGroupMerge tintGroupMerge = pendingMerge.tintGroupMerges[i];
            tintGroupMerge?.opaque?.Dispose();
            tintGroupMerge?.transparent?.Dispose();
            tintGroupMerge?.water?.Dispose();
        }

        pendingMerge.tintGroupMerges.Clear();
    }

    private void CancelDistantChunkClusterPendingMerge(DistantChunkClusterData cluster)
    {
        if (cluster?.pendingMerge == null)
            return;

        DistantChunkClusterPendingMerge pendingMerge = cluster.pendingMerge;
        pendingMerge.combinedHandle.Complete();
        DisposeDistantChunkClusterPendingMerge(pendingMerge);
        cluster.pendingMerge = null;
        cluster.rebuildQueuedWhilePending = false;
        distantChunkClustersWithPendingMerges.Remove(cluster);
    }

    private static Vector2Int[] CreateDistantChunkRenderedChunkArray(HashSet<Vector2Int> renderedChunkCoords)
    {
        if (renderedChunkCoords == null || renderedChunkCoords.Count == 0)
            return Array.Empty<Vector2Int>();

        Vector2Int[] chunkCoords = new Vector2Int[renderedChunkCoords.Count];
        int writeIndex = 0;
        foreach (Vector2Int chunkCoord in renderedChunkCoords)
            chunkCoords[writeIndex++] = chunkCoord;

        return chunkCoords;
    }

    private void ApplyDistantChunkClusterRenderedChunks(DistantChunkClusterData cluster, Vector2Int[] renderedChunkCoords)
    {
        distantChunkRenderedChunksScratch.Clear();
        if (renderedChunkCoords != null)
        {
            for (int i = 0; i < renderedChunkCoords.Length; i++)
                distantChunkRenderedChunksScratch.Add(renderedChunkCoords[i]);
        }

        ApplyDistantChunkClusterRenderedChunks(cluster, distantChunkRenderedChunksScratch);
    }

    private static float4x4 ToFloat4x4(Matrix4x4 matrix)
    {
        return new float4x4(
            new float4(matrix.m00, matrix.m10, matrix.m20, matrix.m30),
            new float4(matrix.m01, matrix.m11, matrix.m21, matrix.m31),
            new float4(matrix.m02, matrix.m12, matrix.m22, matrix.m32),
            new float4(matrix.m03, matrix.m13, matrix.m23, matrix.m33));
    }

    private void ApplyDistantChunkClusterCategoryMesh(DistantChunkClusterCategoryRenderer categoryRenderer, bool hasGeometry)
    {
        if (categoryRenderer == null)
            return;

        Material material = GetDistantChunkBatchMaterial(categoryRenderer.materialIndex);
        if (categoryRenderer.meshRenderer != null && categoryRenderer.meshRenderer.sharedMaterial != material)
            categoryRenderer.meshRenderer.sharedMaterial = material;

        if (!hasGeometry)
        {
            if (categoryRenderer.meshFilter != null)
                categoryRenderer.meshFilter.sharedMesh = null;

            if (categoryRenderer.meshRenderer != null)
                categoryRenderer.meshRenderer.enabled = false;

            if (categoryRenderer.root != null && categoryRenderer.root.activeSelf)
                categoryRenderer.root.SetActive(false);
            return;
        }

        if (categoryRenderer.root != null && !categoryRenderer.root.activeSelf)
            categoryRenderer.root.SetActive(true);

        if (categoryRenderer.meshRenderer != null && !categoryRenderer.meshRenderer.enabled)
            categoryRenderer.meshRenderer.enabled = true;
    }

    private Bounds GetDistantChunkClusterLocalBounds()
    {
        float width = GetResolvedDistantChunkClusterSize() * Chunk.SizeX;
        float depth = GetResolvedDistantChunkClusterSize() * Chunk.SizeZ;
        return new Bounds(
            new Vector3(width * 0.5f, Chunk.SizeY * 0.5f, depth * 0.5f),
            new Vector3(width + 4f, Chunk.SizeY + 4f, depth + 4f));
    }

    private void UpdateDistantChunkClusterRootState(DistantChunkClusterData cluster)
    {
        if (cluster == null || cluster.root == null)
            return;

        bool anyActive = false;
        foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
        {
            UpdateDistantChunkClusterTintGroupRootState(tintGroup);
            if (IsDistantChunkClusterTintGroupActive(tintGroup))
                anyActive = true;
        }

        if (cluster.root.activeSelf != anyActive)
            cluster.root.SetActive(anyActive);
    }

    private static void UpdateDistantChunkClusterTintGroupRootState(DistantChunkClusterTintGroupData tintGroup)
    {
        if (tintGroup == null || tintGroup.root == null)
            return;

        bool anyActive =
            IsDistantChunkClusterCategoryActive(tintGroup.opaque) ||
            IsDistantChunkClusterCategoryActive(tintGroup.transparent) ||
            IsDistantChunkClusterCategoryActive(tintGroup.water);

        if (tintGroup.root.activeSelf != anyActive)
            tintGroup.root.SetActive(anyActive);
    }

    private static bool IsDistantChunkClusterTintGroupActive(DistantChunkClusterTintGroupData tintGroup)
    {
        return tintGroup != null &&
               tintGroup.root != null &&
               tintGroup.root.activeSelf;
    }

    private static bool IsDistantChunkClusterCategoryActive(DistantChunkClusterCategoryRenderer categoryRenderer)
    {
        return categoryRenderer != null &&
               categoryRenderer.root != null &&
               categoryRenderer.root.activeSelf &&
               categoryRenderer.meshRenderer != null &&
               categoryRenderer.meshRenderer.enabled &&
               categoryRenderer.mesh != null &&
               categoryRenderer.mesh.vertexCount > 0;
    }

    private void ApplyDistantChunkClusterRenderedChunks(DistantChunkClusterData cluster, HashSet<Vector2Int> renderedChunkCoords)
    {
        if (cluster.renderedChunkCoords.Count > 0)
        {
            distantChunkToUnsuppressScratch.Clear();
            foreach (Vector2Int chunkCoord in cluster.renderedChunkCoords)
            {
                if (!renderedChunkCoords.Contains(chunkCoord))
                    distantChunkToUnsuppressScratch.Add(chunkCoord);
            }

            for (int i = 0; i < distantChunkToUnsuppressScratch.Count; i++)
                SetChunkDistantBatchSuppressed(distantChunkToUnsuppressScratch[i], false);
        }

        foreach (Vector2Int chunkCoord in renderedChunkCoords)
        {
            if (!cluster.renderedChunkCoords.Contains(chunkCoord))
                SetChunkDistantBatchSuppressed(chunkCoord, true);
        }

        cluster.renderedChunkCoords.Clear();
        foreach (Vector2Int chunkCoord in renderedChunkCoords)
            cluster.renderedChunkCoords.Add(chunkCoord);
    }

    private void SetChunkDistantBatchSuppressed(Vector2Int chunkCoord, bool suppressed)
    {
        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk) || chunk == null || chunk.visualSlices == null)
            return;

        for (int i = 0; i < chunk.visualSlices.Length; i++)
        {
            ChunkRenderSlice visualSlice = chunk.visualSlices[i];
            if (visualSlice != null)
                visualSlice.SetExternallySuppressed(suppressed);
        }

        chunk.RefreshAllVisualSliceVisibility();
    }

    private void ClearDistantChunkClusterVisual(DistantChunkClusterData cluster)
    {
        if (cluster?.tintGroups != null)
        {
            foreach (DistantChunkClusterTintGroupData tintGroup in cluster.tintGroups.Values)
                ClearDistantChunkClusterTintGroupVisual(tintGroup);
        }

        if (cluster != null && cluster.root != null && cluster.root.activeSelf)
            cluster.root.SetActive(false);
    }

    private static void ClearDistantChunkClusterTintGroupVisual(DistantChunkClusterTintGroupData tintGroup)
    {
        if (tintGroup == null)
            return;

        ClearDistantChunkClusterCategoryVisual(tintGroup.opaque);
        ClearDistantChunkClusterCategoryVisual(tintGroup.transparent);
        ClearDistantChunkClusterCategoryVisual(tintGroup.water);

        if (tintGroup.root != null && tintGroup.root.activeSelf)
            tintGroup.root.SetActive(false);
    }

    private void DestroyDistantChunkCluster(Vector2Int clusterCoord)
    {
        if (!distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData cluster))
            return;

        queuedDistantChunkClusterRebuildsSet.Remove(clusterCoord);
        distantChunkClusterRebuildDueFrameByCoord.Remove(clusterCoord);
        CancelDistantChunkClusterPendingMerge(cluster);

        foreach (Vector2Int chunkCoord in cluster.renderedChunkCoords)
            SetChunkDistantBatchSuppressed(chunkCoord, false);

        cluster.renderedChunkCoords.Clear();
        cluster.memberCoords.Clear();

        if (cluster.tintGroups.Count > 0)
        {
            List<DistantChunkClusterTintGroupData> tintGroups = new List<DistantChunkClusterTintGroupData>(cluster.tintGroups.Values);
            for (int i = 0; i < tintGroups.Count; i++)
                DestroyDistantChunkClusterTintGroup(tintGroups[i]);

            cluster.tintGroups.Clear();
        }

        if (cluster.root != null)
            Destroy(cluster.root);

        distantChunkClusters.Remove(clusterCoord);
    }

    private void ClearDistantChunkBatching(bool destroyClusters)
    {
        if (distantChunkClusters.Count > 0)
        {
            List<Vector2Int> clusterCoords = new List<Vector2Int>(distantChunkClusters.Keys);
            for (int i = 0; i < clusterCoords.Count; i++)
            {
                Vector2Int clusterCoord = clusterCoords[i];
                if (!distantChunkClusters.TryGetValue(clusterCoord, out DistantChunkClusterData cluster))
                    continue;

                CancelDistantChunkClusterPendingMerge(cluster);

                foreach (Vector2Int chunkCoord in cluster.renderedChunkCoords)
                    SetChunkDistantBatchSuppressed(chunkCoord, false);

                cluster.renderedChunkCoords.Clear();
                cluster.memberCoords.Clear();
                ClearDistantChunkClusterVisual(cluster);

                if (destroyClusters)
                    DestroyDistantChunkCluster(clusterCoord);
            }
        }

        distantChunkClusterByChunk.Clear();
        distantChunkGrassTintByChunk.Clear();
        queuedDistantChunkClusterRebuilds.Clear();
        queuedDistantChunkClusterRebuildsSet.Clear();
        distantChunkClusterRebuildDueFrameByCoord.Clear();
        distantChunkClustersWithPendingMerges.Clear();
        distantChunkClusterAssignmentsDirty = true;
        lastDistantChunkBatchCenter = new Vector2Int(int.MinValue, int.MinValue);

        if (destroyClusters)
            distantChunkClusters.Clear();
    }

    private void DisposeDistantChunkBatching()
    {
        ClearDistantChunkBatching(true);
        distantChunkBatchingConfigured = false;
        lastDistantChunkBatchMaterials = null;
    }

    private static void ReleaseTemporaryCombinedMesh(Mesh mesh)
    {
        if (mesh != null)
            UnityEngine.Object.Destroy(mesh);
    }

    private static bool HasSameMaterialReferences(Material[] current, Material[] desired)
    {
        if (ReferenceEquals(current, desired))
            return true;
        if (current == null || desired == null)
            return current == desired;
        if (current.Length != desired.Length)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != desired[i])
                return false;
        }

        return true;
    }

    private void MarkDistantChunkClusterDirty(Vector2Int chunkCoord)
    {
        if (distantChunkClusterByChunk.TryGetValue(chunkCoord, out Vector2Int clusterCoord))
            QueueDistantChunkClusterRebuild(clusterCoord);
    }

    private static void ClearDistantChunkClusterCategoryVisual(DistantChunkClusterCategoryRenderer categoryRenderer)
    {
        if (categoryRenderer == null)
            return;

        if (categoryRenderer.mesh != null)
            categoryRenderer.mesh.Clear(false);

        if (categoryRenderer.meshFilter != null)
            categoryRenderer.meshFilter.sharedMesh = null;

        if (categoryRenderer.meshRenderer != null)
            categoryRenderer.meshRenderer.enabled = false;

        if (categoryRenderer.root != null && categoryRenderer.root.activeSelf)
            categoryRenderer.root.SetActive(false);
    }

    private static void DestroyDistantChunkClusterCategory(DistantChunkClusterCategoryRenderer categoryRenderer)
    {
        if (categoryRenderer == null)
            return;

        if (categoryRenderer.mesh != null)
        {
            UnityEngine.Object.Destroy(categoryRenderer.mesh);
            categoryRenderer.mesh = null;
        }

        if (categoryRenderer.meshFilter != null)
            categoryRenderer.meshFilter.sharedMesh = null;

        if (categoryRenderer.root != null)
            UnityEngine.Object.Destroy(categoryRenderer.root);
    }

    private static void DestroyDistantChunkClusterTintGroup(DistantChunkClusterTintGroupData tintGroup)
    {
        if (tintGroup == null)
            return;

        DestroyDistantChunkClusterCategory(tintGroup.opaque);
        DestroyDistantChunkClusterCategory(tintGroup.transparent);
        DestroyDistantChunkClusterCategory(tintGroup.water);

        if (tintGroup.root != null)
            UnityEngine.Object.Destroy(tintGroup.root);
    }

    private static int PackGrassTintKey(Color grassTint)
    {
        Color32 tint32 = (Color32)grassTint;
        return tint32.r |
               (tint32.g << 8) |
               (tint32.b << 16) |
               (tint32.a << 24);
    }
}
