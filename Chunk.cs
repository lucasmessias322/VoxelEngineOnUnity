using System.Collections.Generic;
using System;
using Unity.Collections;
using UnityEngine;


public class Chunk : MonoBehaviour
{
    public struct SubchunkState
    {
        public bool hasGeometry;
        public bool canHaveColliders;
        public bool hasColliderData;
        public bool isVisible;
        public bool supportsLightingOnlyRebuild;
        public bool lightingOnlySupportValid;
    }

    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public const int LightSnapshotLength = SizeX * SizeY * SizeZ;
    public NativeArray<byte> voxelData;
    public const int SubchunkHeight = 16;
    public const int SubchunksPerColumn = (SizeY + SubchunkHeight - 1) / SubchunkHeight; // 384 -> 24
    public const int ColliderOccupancyWordsPerSubchunk = (SizeX * SubchunkHeight * SizeZ + 63) / 64;

    [SerializeField, HideInInspector] // Estado logico dos subchunks fica encapsulado no Chunk.
    private SubchunkState[] subchunks;
    [HideInInspector] public ChunkRenderSlice[] visualSlices;
    [HideInInspector] public Bounds worldBounds;
    public bool hasVoxelData = false;
    [NonSerialized] public bool hasVoxelSnapshot = false;
    [NonSerialized] private ushort[] lightSnapshot;
    [NonSerialized] private bool hasLightSnapshot = false;
    [NonSerialized] public bool pendingRecycle = false;
    [NonSerialized] public bool hasDetailedGenerationData = true;
    [NonSerialized] public bool requestedDetailedGeneration = true;
    [NonSerialized] public bool pendingDetailedGenerationSwap = false;
    [NonSerialized] public bool pendingDetailedGenerationTarget = true;
    [NonSerialized] public int pendingDetailedGenerationExpectedGen = -1;

    [HideInInspector] public MeshRenderer[] subRenderers;
    [NonSerialized] private SubchunkColliderBuilder[] subchunkColliderBuilders;
    [NonSerialized] private ulong[] subchunkColliderOccupancyBits;
    [NonSerialized] private bool[] subchunkColliderOccupancyValid;
    [NonSerialized] private bool[] subchunkColliderOccupancyHasSolids;
    [NonSerialized] public ulong[] subchunkVisibilityMasks;
    [NonSerialized] public bool[] subchunkVisibilityValid;
    [NonSerialized] public ulong lastLightingContextHash;
    [NonSerialized] public bool lightingContextHashValid;
    [NonSerialized] public int visualSubchunksPerRenderer;
    [NonSerialized] private Transform dynamicBlocksRoot;
    [NonSerialized] private Dictionary<int, DynamicBlockVisualInstance> dynamicBlockInstances;
    [NonSerialized] private HashSet<int> dynamicBlockTouchedKeys;
    [NonSerialized] private List<int> dynamicBlockRemovalKeys;
    public int SubchunkCount => subchunks?.Length ?? 0;
    public bool HasInitializedSubchunks =>
        subchunks != null &&
        subchunks.Length == SubchunksPerColumn &&
        subchunkColliderBuilders != null &&
        subchunkColliderBuilders.Length == SubchunksPerColumn &&
        visualSlices != null &&
        visualSlices.Length == GetVisualSliceCount(visualSubchunksPerRenderer) &&
        subRenderers != null &&
        subRenderers.Length == visualSlices.Length;

    public Unity.Jobs.JobHandle currentJob;
    public bool jobScheduled;

    private struct DynamicBlockVisualInstance
    {
        public GameObject gameObject;
        public BlockType blockType;
        public GameObject prefab;
    }

    public enum ChunkState
    {
        Requested,   // job agendado
        MeshReady,   // resultado chegou
        Active,       // mesh aplicado
        Inactive
    }

    public ChunkState state;

    public bool HasPendingDetailedGenerationSwap =>
        pendingDetailedGenerationSwap && pendingDetailedGenerationExpectedGen >= 0;

    public bool IsTargetingDetailedGeneration =>
        HasPendingDetailedGenerationSwap ? pendingDetailedGenerationTarget : requestedDetailedGeneration;


    private void Awake()
    {

        int total = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;

        voxelData = new NativeArray<byte>(total, Allocator.Persistent);
        pendingRecycle = false;

        hasVoxelData = false; // ainda útil para saber se já tem dados válidos
    }
    private void OnDestroy()
    {
        if (World.Instance != null && !World.Instance.IsShuttingDown)
            World.Instance.CompletePendingJobsForChunk(this);

        CompleteTrackedJob();
        ClearDynamicBlockVisuals();


        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }

    public static int GetVisualSliceCount(int subchunksPerRenderer)
    {
        int clamped = Mathf.Clamp(subchunksPerRenderer, 1, SubchunksPerColumn);
        return (SubchunksPerColumn + clamped - 1) / clamped;
    }

    public int GetVisualSliceIndexForSubchunk(int subchunkIndex)
    {
        int clampedSize = Mathf.Clamp(visualSubchunksPerRenderer, 1, SubchunksPerColumn);
        return Mathf.Clamp(subchunkIndex / clampedSize, 0, Mathf.Max(0, GetVisualSliceCount(clampedSize) - 1));
    }

    public int GetVisualSliceMask(int visualSliceIndex)
    {
        if (visualSliceIndex < 0 || visualSliceIndex >= GetVisualSliceCount(visualSubchunksPerRenderer))
            return 0;

        int start = visualSliceIndex * visualSubchunksPerRenderer;
        int end = Mathf.Min(start + visualSubchunksPerRenderer, SubchunksPerColumn);
        int mask = 0;
        for (int sub = start; sub < end; sub++)
            mask |= 1 << sub;

        return mask;
    }

    public bool TryGetVisualSlice(int visualSliceIndex, out ChunkRenderSlice visualSlice)
    {
        if (visualSlices != null &&
            visualSliceIndex >= 0 &&
            visualSliceIndex < visualSlices.Length)
        {
            visualSlice = visualSlices[visualSliceIndex];
            return visualSlice != null;
        }

        visualSlice = null;
        return false;
    }

    public void RefreshVisualSliceVisibility(int visualSliceIndex)
    {
        if (TryGetVisualSlice(visualSliceIndex, out ChunkRenderSlice visualSlice))
            visualSlice.RefreshVisibility(this);
    }

    public void RefreshVisualSliceVisibilityForSubchunk(int subchunkIndex)
    {
        RefreshVisualSliceVisibility(GetVisualSliceIndexForSubchunk(subchunkIndex));
    }

    public void RefreshAllVisualSliceVisibility()
    {
        if (visualSlices == null)
            return;

        for (int i = 0; i < visualSlices.Length; i++)
        {
            ChunkRenderSlice visualSlice = visualSlices[i];
            if (visualSlice != null)
                visualSlice.RefreshVisibility(this);
        }
    }

    public void InitializeSubchunks(Material[] materials, int subchunksPerVisualSlice)
    {
        EnsureVisibilityData();

        visualSubchunksPerRenderer = Mathf.Clamp(subchunksPerVisualSlice, 1, SubchunksPerColumn);

        EnsureSubchunkStorage();

        for (int i = 0; i < SubchunksPerColumn; i++)
        {
            subchunks[i].hasGeometry = false;
            subchunks[i].canHaveColliders = false;
            subchunks[i].hasColliderData = false;
            subchunks[i].isVisible = true;
            subchunks[i].supportsLightingOnlyRebuild = false;
            subchunks[i].lightingOnlySupportValid = false;
            subchunkColliderBuilders[i].Clear();
            ClearSubchunkColliderOccupancy(i);
        }

        int visualSliceCount = GetVisualSliceCount(visualSubchunksPerRenderer);
        if (visualSlices != null && visualSlices.Length != visualSliceCount)
        {
            for (int i = 0; i < visualSlices.Length; i++)
            {
                if (visualSlices[i] != null)
                    Destroy(visualSlices[i].gameObject);
            }

            visualSlices = null;
        }

        if (visualSlices == null || visualSlices.Length != visualSliceCount)
            visualSlices = new ChunkRenderSlice[visualSliceCount];

        if (subRenderers == null || subRenderers.Length != visualSliceCount)
            subRenderers = new MeshRenderer[visualSliceCount];

        for (int i = 0; i < visualSliceCount; i++)
        {
            int startSubchunk = i * visualSubchunksPerRenderer;
            int sliceSubchunkCount = Mathf.Min(visualSubchunksPerRenderer, SubchunksPerColumn - startSubchunk);

            ChunkRenderSlice visualSlice = visualSlices[i];
            if (visualSlice == null)
            {
                visualSlice = CreateVisualSlice(i, materials, startSubchunk, sliceSubchunkCount);
                visualSlices[i] = visualSlice;
            }
            else
            {
                visualSlice.Initialize(materials, i, startSubchunk, sliceSubchunkCount);
            }

            subRenderers[i] = visualSlice != null ? visualSlice.meshRenderer : null;
        }

        UpdateWorldBounds();
    }

    public void PrewarmSubchunkColliders(int collidersPerSubchunk)
    {
        if (collidersPerSubchunk <= 0)
            return;

        EnsureSubchunkStorage();
        for (int i = 0; i < subchunkColliderBuilders.Length; i++)
        {
            SubchunkColliderBuilder colliderBuilder = subchunkColliderBuilders[i];
            if (colliderBuilder == null)
                continue;

            colliderBuilder.PrewarmBoxColliders(gameObject, collidersPerSubchunk);
        }
    }

    private ChunkRenderSlice CreateVisualSlice(int sliceIndex, Material[] materials, int startSubchunkIndex, int sliceSubchunkCount)
    {
        GameObject sliceObj = new GameObject($"ChunkSlice_{sliceIndex}");
        sliceObj.transform.SetParent(transform, false);
        sliceObj.transform.localPosition = Vector3.zero;
        sliceObj.layer = gameObject.layer;

        ChunkRenderSlice visualSlice = sliceObj.AddComponent<ChunkRenderSlice>();
        visualSlice.Initialize(materials, sliceIndex, startSubchunkIndex, sliceSubchunkCount);
        sliceObj.SetActive(false);
        return visualSlice;
    }

    public void UpdateWorldBounds()
    {
        worldBounds = new Bounds(
            transform.position + new Vector3(8f, 192f, 8f),
            new Vector3(16f, 384f, 16f)
        );
    }

    public void EnsureVisibilityData()
    {
        if (subchunkVisibilityMasks == null || subchunkVisibilityMasks.Length != SubchunksPerColumn)
            subchunkVisibilityMasks = new ulong[SubchunksPerColumn];

        if (subchunkVisibilityValid == null || subchunkVisibilityValid.Length != SubchunksPerColumn)
            subchunkVisibilityValid = new bool[SubchunksPerColumn];
    }

    public bool SetSubchunkVisibilityData(int subchunkIndex, ulong visibilityMask)
    {
        EnsureVisibilityData();
        bool changed = !subchunkVisibilityValid[subchunkIndex] ||
                       subchunkVisibilityMasks[subchunkIndex] != visibilityMask;
        subchunkVisibilityMasks[subchunkIndex] = visibilityMask;
        subchunkVisibilityValid[subchunkIndex] = true;
        return changed;
    }

    public void ClearSubchunkVisibilityData(int subchunkIndex)
    {
        EnsureVisibilityData();
        subchunkVisibilityMasks[subchunkIndex] = 0UL;
        subchunkVisibilityValid[subchunkIndex] = false;
    }

    public void ClearAllSubchunkVisibilityData()
    {
        EnsureVisibilityData();
        Array.Clear(subchunkVisibilityMasks, 0, subchunkVisibilityMasks.Length);
        Array.Clear(subchunkVisibilityValid, 0, subchunkVisibilityValid.Length);
    }

    public bool TryGetSubchunkVisibilityData(int subchunkIndex, out ulong visibilityMask)
    {
        if (subchunkVisibilityValid != null &&
            subchunkIndex >= 0 &&
            subchunkIndex < subchunkVisibilityValid.Length &&
            subchunkVisibilityValid[subchunkIndex])
        {
            visibilityMask = subchunkVisibilityMasks[subchunkIndex];
            return true;
        }

        visibilityMask = 0UL;
        return false;
    }




    public Vector2Int coord;
    public void SetCoord(Vector2Int c)
    {
        coord = c;
        gameObject.name = $"Chunk_{c.x}_{c.y}";
        pendingRecycle = false;
    }

    public void UpdateLightSnapshot(NativeArray<ushort> sourceLightData, int borderSize)
    {
        hasLightSnapshot = false;
        if (!sourceLightData.IsCreated || borderSize < 0)
            return;

        int voxelSizeX = SizeX + 2 * borderSize;
        int voxelSizeZ = SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * SizeY;
        int expectedLength = voxelPlaneSize * voxelSizeZ;
        if (voxelSizeX <= 0 || voxelSizeZ <= 0 || sourceLightData.Length < expectedLength)
            return;

        if (lightSnapshot == null || lightSnapshot.Length != LightSnapshotLength)
            lightSnapshot = new ushort[LightSnapshotLength];

        for (int z = 0; z < SizeZ; z++)
        {
            int sourceZ = z + borderSize;
            for (int y = 0; y < SizeY; y++)
            {
                int sourceIndex = borderSize + y * voxelSizeX + sourceZ * voxelPlaneSize;
                int targetIndex = y * SizeX * SizeZ + z * SizeX;
                for (int x = 0; x < SizeX; x++)
                    lightSnapshot[targetIndex + x] = sourceLightData[sourceIndex + x];
            }
        }

        hasLightSnapshot = true;
    }

    public void UpdateBlockLightSnapshot(NativeArray<ushort> sourceLightData, int borderSize)
    {
        if (!hasLightSnapshot ||
            lightSnapshot == null ||
            lightSnapshot.Length != LightSnapshotLength ||
            !sourceLightData.IsCreated ||
            borderSize < 0)
        {
            return;
        }

        int voxelSizeX = SizeX + 2 * borderSize;
        int voxelSizeZ = SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * SizeY;
        int expectedLength = voxelPlaneSize * voxelSizeZ;
        if (voxelSizeX <= 0 || voxelSizeZ <= 0 || sourceLightData.Length < expectedLength)
            return;

        for (int z = 0; z < SizeZ; z++)
        {
            int sourceZ = z + borderSize;
            for (int y = 0; y < SizeY; y++)
            {
                int sourceIndex = borderSize + y * voxelSizeX + sourceZ * voxelPlaneSize;
                int targetIndex = y * SizeX * SizeZ + z * SizeX;
                for (int x = 0; x < SizeX; x++)
                {
                    ushort existingPackedLight = lightSnapshot[targetIndex + x];
                    ushort updatedBlockLight = sourceLightData[sourceIndex + x];
                    lightSnapshot[targetIndex + x] = LightUtils.PackLightRgb(
                        LightUtils.GetSkyLight(existingPackedLight),
                        LightUtils.GetBlockLightR(updatedBlockLight),
                        LightUtils.GetBlockLightG(updatedBlockLight),
                        LightUtils.GetBlockLightB(updatedBlockLight));
                }
            }
        }
    }

    public bool TryGetLightSnapshot(int localX, int y, int localZ, out ushort packedLight)
    {
        packedLight = 0;
        if (!hasLightSnapshot || lightSnapshot == null || lightSnapshot.Length != LightSnapshotLength)
            return false;
        if ((uint)localX >= SizeX || (uint)y >= SizeY || (uint)localZ >= SizeZ)
            return false;

        packedLight = lightSnapshot[localX + localZ * SizeX + y * SizeX * SizeZ];
        return true;
    }

    public void ResetChunk()
    {
        CompleteTrackedJob();
        ClearDynamicBlockVisuals();

        pendingRecycle = false;
        jobScheduled = false;
        currentJob = default;
        state = ChunkState.Inactive;
        generation = -1;
        hasVoxelData = false;
        hasVoxelSnapshot = false;
        hasLightSnapshot = false;
        hasDetailedGenerationData = true;
        requestedDetailedGeneration = true;
        pendingDetailedGenerationSwap = false;
        pendingDetailedGenerationTarget = true;
        pendingDetailedGenerationExpectedGen = -1;
        ClearAllSubchunkVisibilityData();
        lightingContextHashValid = false;
        lastLightingContextHash = 0;

        if (subchunks != null)
        {
            for (int i = 0; i < subchunks.Length; i++)
            {
                ClearSubchunkMesh(i);
                SetSubchunkVisible(i, false);
            }
        }

        if (visualSlices != null)
        {
            foreach (var visualSlice in visualSlices)
            {
                if (visualSlice != null)
                    visualSlice.ClearMesh();
            }
        }

        gameObject.SetActive(false);
    }

    public void SetPendingDetailedGenerationSwap(bool targetDetailedGeneration, int expectedGen)
    {
        pendingDetailedGenerationSwap = true;
        pendingDetailedGenerationTarget = targetDetailedGeneration;
        pendingDetailedGenerationExpectedGen = expectedGen;
    }

    public void ClearPendingDetailedGenerationSwap()
    {
        pendingDetailedGenerationSwap = false;
        pendingDetailedGenerationTarget = requestedDetailedGeneration;
        pendingDetailedGenerationExpectedGen = -1;
    }

    public bool TryCommitPendingDetailedGenerationSwap(int expectedGen)
    {
        if (!HasPendingDetailedGenerationSwap || pendingDetailedGenerationExpectedGen != expectedGen)
            return false;

        requestedDetailedGeneration = pendingDetailedGenerationTarget;
        pendingDetailedGenerationSwap = false;
        pendingDetailedGenerationExpectedGen = -1;
        return true;
    }
    public int generation;

    public void SyncDynamicBlockVisuals(BlockDataSO blockData)
    {
        if (blockData == null || !voxelData.IsCreated || !hasVoxelSnapshot)
        {
            ClearDynamicBlockVisuals();
            return;
        }

        if (blockData.mappings == null || blockData.mappings.Length == 0)
            blockData.InitializeDictionary();

        EnsureDynamicBlockVisualStorage();
        dynamicBlockTouchedKeys.Clear();

        int planeSize = SizeX * SizeZ;
        for (int y = 0; y < SizeY; y++)
        {
            int yBase = y * planeSize;
            for (int z = 0; z < SizeZ; z++)
            {
                int zBase = z * SizeX;
                for (int x = 0; x < SizeX; x++)
                {
                    int key = yBase + zBase + x;
                    BlockType blockType = (BlockType)voxelData[key];
                    if (!blockData.IsDynamicVisualBlock(blockType) ||
                        !blockData.TryGetDynamicBlockPrefabDefinition(blockType, out DynamicBlockPrefabDefinition definition))
                    {
                        continue;
                    }

                    dynamicBlockTouchedKeys.Add(key);
                    Vector3Int localPos = new Vector3Int(x, y, z);
                    DynamicBlockVisualInstance instance = GetOrCreateDynamicBlockVisual(key, blockType, definition, localPos);
                    ApplyDynamicBlockTransform(instance.gameObject, blockType, definition, localPos);
                    dynamicBlockInstances[key] = instance;
                }
            }
        }

        RemoveUntouchedDynamicBlockVisuals();
    }

    public void SyncDynamicBlockVisualAt(BlockDataSO blockData, Vector3Int worldPos)
    {
        if (blockData == null || !voxelData.IsCreated || !hasVoxelSnapshot)
        {
            ClearDynamicBlockVisuals();
            return;
        }

        int localX = worldPos.x - coord.x * SizeX;
        int localZ = worldPos.z - coord.y * SizeZ;
        int localY = worldPos.y;
        if ((uint)localX >= SizeX || (uint)localZ >= SizeZ || (uint)localY >= SizeY)
            return;

        if (blockData.mappings == null || blockData.mappings.Length == 0)
            blockData.InitializeDictionary();

        int key = localX + localZ * SizeX + localY * SizeX * SizeZ;
        BlockType blockType = (BlockType)voxelData[key];
        if (!blockData.IsDynamicVisualBlock(blockType) ||
            !blockData.TryGetDynamicBlockPrefabDefinition(blockType, out DynamicBlockPrefabDefinition definition))
        {
            if (dynamicBlockInstances != null &&
                dynamicBlockInstances.TryGetValue(key, out DynamicBlockVisualInstance existing))
            {
                DestroyDynamicBlockVisual(existing);
                dynamicBlockInstances.Remove(key);
            }

            return;
        }

        EnsureDynamicBlockVisualStorage();
        Vector3Int localPos = new Vector3Int(localX, localY, localZ);
        DynamicBlockVisualInstance instance = GetOrCreateDynamicBlockVisual(key, blockType, definition, localPos);
        ApplyDynamicBlockTransform(instance.gameObject, blockType, definition, localPos);
        dynamicBlockInstances[key] = instance;
    }

    public void ClearDynamicBlockVisuals()
    {
        if (dynamicBlockInstances != null)
        {
            foreach (KeyValuePair<int, DynamicBlockVisualInstance> pair in dynamicBlockInstances)
                DestroyDynamicBlockVisual(pair.Value);

            dynamicBlockInstances.Clear();
        }

        dynamicBlockTouchedKeys?.Clear();
        dynamicBlockRemovalKeys?.Clear();

        if (dynamicBlocksRoot != null)
        {
            DestroyDynamicGameObject(dynamicBlocksRoot.gameObject);
            dynamicBlocksRoot = null;
        }
    }

    private void EnsureDynamicBlockVisualStorage()
    {
        if (dynamicBlockInstances == null)
            dynamicBlockInstances = new Dictionary<int, DynamicBlockVisualInstance>();

        if (dynamicBlockTouchedKeys == null)
            dynamicBlockTouchedKeys = new HashSet<int>();

        if (dynamicBlockRemovalKeys == null)
            dynamicBlockRemovalKeys = new List<int>();
    }

    private Transform EnsureDynamicBlocksRoot()
    {
        if (dynamicBlocksRoot != null)
            return dynamicBlocksRoot;

        Transform existing = transform.Find("DynamicBlocks");
        if (existing != null)
        {
            dynamicBlocksRoot = existing;
            return dynamicBlocksRoot;
        }

        GameObject root = new GameObject("DynamicBlocks");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.layer = gameObject.layer;
        dynamicBlocksRoot = root.transform;
        return dynamicBlocksRoot;
    }

    private DynamicBlockVisualInstance GetOrCreateDynamicBlockVisual(
        int key,
        BlockType blockType,
        DynamicBlockPrefabDefinition definition,
        Vector3Int localPos)
    {
        if (dynamicBlockInstances.TryGetValue(key, out DynamicBlockVisualInstance existing) &&
            existing.gameObject != null &&
            existing.blockType == blockType &&
            existing.prefab == definition.prefab)
        {
            return existing;
        }

        if (existing.gameObject != null)
            DestroyDynamicBlockVisual(existing);

        GameObject created = Instantiate(definition.prefab, EnsureDynamicBlocksRoot());
        created.name = $"Dynamic_{blockType}_{localPos.x}_{localPos.y}_{localPos.z}";

        if (definition.inheritChunkLayer)
            SetLayerRecursively(created, gameObject.layer);

        Vector3Int worldPos = GetWorldPositionForLocalBlock(localPos);
        NotifyDynamicBlockSpawned(created, worldPos, blockType);

        return new DynamicBlockVisualInstance
        {
            gameObject = created,
            blockType = blockType,
            prefab = definition.prefab
        };
    }

    private void ApplyDynamicBlockTransform(
        GameObject instance,
        BlockType blockType,
        DynamicBlockPrefabDefinition definition,
        Vector3Int localPos)
    {
        if (instance == null)
            return;

        Transform instanceTransform = instance.transform;
        instanceTransform.SetParent(EnsureDynamicBlocksRoot(), false);
        instanceTransform.localPosition = (Vector3)localPos + definition.localOffset;
        instanceTransform.localRotation = ResolveDynamicBlockRotation(blockType, localPos, definition);
        instanceTransform.localScale = definition.localScale == Vector3.zero ? Vector3.one : definition.localScale;
    }

    private Quaternion ResolveDynamicBlockRotation(
        BlockType blockType,
        Vector3Int localPos,
        DynamicBlockPrefabDefinition definition)
    {
        Quaternion localRotation = Quaternion.Euler(definition.localEulerAngles);
        if (!definition.rotateWithPlacementAxis || World.Instance == null)
            return localRotation;

        Vector3Int worldPos = GetWorldPositionForLocalBlock(localPos);
        BlockPlacementAxis axis = World.Instance.GetPlacementAxisAt(worldPos, blockType);
        return Quaternion.Euler(0f, GetYawForPlacementAxis(axis), 0f) * localRotation;
    }

    private static float GetYawForPlacementAxis(BlockPlacementAxis axis)
    {
        switch (BlockPlacementRotationUtility.SanitizeStoredAxis(axis))
        {
            case BlockPlacementAxis.X:
                return 90f;
            case BlockPlacementAxis.ZNegative:
                return 180f;
            case BlockPlacementAxis.XNegative:
                return 270f;
            default:
                return 0f;
        }
    }

    private Vector3Int GetWorldPositionForLocalBlock(Vector3Int localPos)
    {
        return new Vector3Int(
            coord.x * SizeX + localPos.x,
            localPos.y,
            coord.y * SizeZ + localPos.z);
    }

    private void RemoveUntouchedDynamicBlockVisuals()
    {
        dynamicBlockRemovalKeys.Clear();
        foreach (KeyValuePair<int, DynamicBlockVisualInstance> pair in dynamicBlockInstances)
        {
            if (!dynamicBlockTouchedKeys.Contains(pair.Key))
                dynamicBlockRemovalKeys.Add(pair.Key);
        }

        for (int i = 0; i < dynamicBlockRemovalKeys.Count; i++)
        {
            int key = dynamicBlockRemovalKeys[i];
            if (dynamicBlockInstances.TryGetValue(key, out DynamicBlockVisualInstance instance))
                DestroyDynamicBlockVisual(instance);

            dynamicBlockInstances.Remove(key);
        }
    }

    private void NotifyDynamicBlockSpawned(GameObject instance, Vector3Int worldPos, BlockType blockType)
    {
        DynamicVoxelBlock[] behaviours = instance.GetComponentsInChildren<DynamicVoxelBlock>(true);
        for (int i = 0; i < behaviours.Length; i++)
            behaviours[i].Initialize(this, worldPos, blockType);
    }

    private static void NotifyDynamicBlockDespawned(GameObject instance)
    {
        DynamicVoxelBlock[] behaviours = instance.GetComponentsInChildren<DynamicVoxelBlock>(true);
        for (int i = 0; i < behaviours.Length; i++)
            behaviours[i].Despawn();
    }

    private static void DestroyDynamicBlockVisual(DynamicBlockVisualInstance instance)
    {
        if (instance.gameObject == null)
            return;

        NotifyDynamicBlockDespawned(instance.gameObject);
        DestroyDynamicGameObject(instance.gameObject);
    }

    private static void DestroyDynamicGameObject(GameObject target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(target);
        else
            UnityEngine.Object.DestroyImmediate(target);
    }

    private static void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null)
            return;

        target.layer = layer;
        Transform targetTransform = target.transform;
        for (int i = 0; i < targetTransform.childCount; i++)
            SetLayerRecursively(targetTransform.GetChild(i).gameObject, layer);
    }

    public void CompleteTrackedJob()
    {
        if (!jobScheduled)
            return;

        try
        {
            currentJob.Complete();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public bool HasSubchunkGeometry(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].hasGeometry;
    }

    public bool CanSubchunkHaveColliders(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].canHaveColliders;
    }

    public bool HasSubchunkColliderData(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].hasColliderData;
    }

    public bool HasSubchunkColliderOccupancy(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) &&
               subchunkColliderOccupancyValid != null &&
               subchunkColliderOccupancyHasSolids != null &&
               subchunkColliderOccupancyValid[subchunkIndex] &&
               subchunkColliderOccupancyHasSolids[subchunkIndex];
    }

    public bool IsSubchunkVisible(int subchunkIndex)
    {
        return IsSubchunkIndexValid(subchunkIndex) && subchunks[subchunkIndex].isVisible;
    }

    public bool TryGetSubchunkState(int subchunkIndex, out SubchunkState state)
    {
        if (IsSubchunkIndexValid(subchunkIndex))
        {
            state = subchunks[subchunkIndex];
            return true;
        }

        state = default;
        return false;
    }

    public void SetSubchunkMeshState(int subchunkIndex, bool geometryPresent, bool solidColliderGeometryPresent)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].hasGeometry = geometryPresent;
        subchunks[subchunkIndex].canHaveColliders = solidColliderGeometryPresent;

        if (!subchunks[subchunkIndex].canHaveColliders)
            ClearSubchunkColliderData(subchunkIndex);
    }

    public void SetSubchunkLightingOnlyRebuildSupport(int subchunkIndex, bool supported)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].supportsLightingOnlyRebuild = supported;
        subchunks[subchunkIndex].lightingOnlySupportValid = true;
    }

    public bool TryGetVisualSliceLightingOnlyRebuildSupport(int visualSliceIndex, out bool supported)
    {
        supported = false;
        if (visualSliceIndex < 0 || visualSliceIndex >= GetVisualSliceCount(visualSubchunksPerRenderer))
            return false;

        EnsureSubchunkStorage();

        int start = visualSliceIndex * visualSubchunksPerRenderer;
        int end = Mathf.Min(start + visualSubchunksPerRenderer, SubchunksPerColumn);
        supported = true;

        for (int sub = start; sub < end; sub++)
        {
            if (!subchunks[sub].lightingOnlySupportValid)
                return false;

            if (!subchunks[sub].supportsLightingOnlyRebuild)
                supported = false;
        }

        return true;
    }

    public void SetSubchunkVisible(int subchunkIndex, bool visible)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].isVisible = visible;
    }

    public void SetSubchunkColliderSystemEnabled(int subchunkIndex, bool enabled)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        bool shouldEnable = enabled &&
                            subchunks[subchunkIndex].canHaveColliders &&
                            subchunks[subchunkIndex].hasColliderData;
        colliderBuilder.SetEnabled(shouldEnable);
    }

    public void UpdateSubchunkColliderOccupancy(NativeArray<ulong> occupancyBits, int dirtySubchunkMask)
    {
        EnsureSubchunkStorage();

        if (!occupancyBits.IsCreated || occupancyBits.Length < SubchunksPerColumn * ColliderOccupancyWordsPerSubchunk)
        {
            for (int subchunkIndex = 0; subchunkIndex < SubchunksPerColumn; subchunkIndex++)
            {
                if ((dirtySubchunkMask & (1 << subchunkIndex)) == 0)
                    continue;

                ClearSubchunkColliderOccupancy(subchunkIndex);
            }

            return;
        }

        for (int subchunkIndex = 0; subchunkIndex < SubchunksPerColumn; subchunkIndex++)
        {
            if ((dirtySubchunkMask & (1 << subchunkIndex)) == 0)
                continue;

            int wordOffset = subchunkIndex * ColliderOccupancyWordsPerSubchunk;
            bool hasSolids = false;
            for (int wordIndex = 0; wordIndex < ColliderOccupancyWordsPerSubchunk; wordIndex++)
            {
                ulong word = occupancyBits[wordOffset + wordIndex];
                subchunkColliderOccupancyBits[wordOffset + wordIndex] = word;
                hasSolids |= word != 0UL;
            }

            subchunkColliderOccupancyValid[subchunkIndex] = true;
            subchunkColliderOccupancyHasSolids[subchunkIndex] = hasSolids;
        }
    }

    public bool TryActivateCachedSubchunkColliders(int subchunkIndex)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return false;

        if (!subchunks[subchunkIndex].canHaveColliders)
            return false;

        if (!TryGetSubchunkColliderOccupancyRange(subchunkIndex, out int wordOffset))
            return false;

        int startY = subchunkIndex * SubchunkHeight;
        int endY = Mathf.Min(startY + SubchunkHeight, SizeY);
        bool restored = colliderBuilder.TryRestoreCachedColliders(
            subchunkColliderOccupancyBits,
            wordOffset,
            ColliderOccupancyWordsPerSubchunk,
            startY,
            endY,
            out bool hasColliders);

        if (restored)
            subchunks[subchunkIndex].hasColliderData = hasColliders;

        return restored && hasColliders;
    }

    public void ClearSubchunkMesh(int subchunkIndex)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        subchunks[subchunkIndex].hasGeometry = false;
        subchunks[subchunkIndex].canHaveColliders = false;
        SetSubchunkLightingOnlyRebuildSupport(subchunkIndex, true);
        ClearSubchunkColliderData(subchunkIndex);
        ClearSubchunkColliderOccupancy(subchunkIndex);
    }

    public void ClearSubchunkColliderData(int subchunkIndex)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        subchunks[subchunkIndex].hasColliderData = false;
        colliderBuilder.Clear();
    }

    public void MarkSubchunkColliderDataDirty(int subchunkIndex)
    {
        if (!IsSubchunkIndexValid(subchunkIndex))
            return;

        // Keep the previous colliders enabled until the queued rebuild swaps in the new layout.
        subchunks[subchunkIndex].hasColliderData = false;
    }

    public void RebuildSubchunkColliders(
        int subchunkIndex,
        NativeArray<byte> voxelSource,
        BlockTextureMapping[] blockMappings,
        BlockModelCuboid[] blockModelCuboids,
        int startY,
        int endY)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return;

        if (!subchunks[subchunkIndex].canHaveColliders)
        {
            ClearSubchunkColliderData(subchunkIndex);
            return;
        }

        subchunks[subchunkIndex].hasColliderData = colliderBuilder.TryBuild(gameObject, voxelSource, blockMappings, blockModelCuboids, startY, endY);
    }

    public bool ForceRebuildSubchunkColliders(
        int subchunkIndex,
        NativeArray<byte> voxelSource,
        BlockTextureMapping[] blockMappings,
        BlockModelCuboid[] blockModelCuboids,
        int startY,
        int endY)
    {
        if (!TryGetSubchunkColliderBuilder(subchunkIndex, out SubchunkColliderBuilder colliderBuilder))
            return false;

        subchunks[subchunkIndex].canHaveColliders = true;
        subchunks[subchunkIndex].hasColliderData = colliderBuilder.TryBuild(gameObject, voxelSource, blockMappings, blockModelCuboids, startY, endY);
        return subchunks[subchunkIndex].hasColliderData;
    }

    private bool TryGetSubchunkColliderOccupancyRange(int subchunkIndex, out int wordOffset)
    {
        wordOffset = 0;
        EnsureSubchunkStorage();

        if (!IsSubchunkIndexValid(subchunkIndex) ||
            subchunkColliderOccupancyValid == null ||
            subchunkColliderOccupancyBits == null ||
            !subchunkColliderOccupancyValid[subchunkIndex])
        {
            return false;
        }

        wordOffset = subchunkIndex * ColliderOccupancyWordsPerSubchunk;
        return true;
    }

    private void ClearSubchunkColliderOccupancy(int subchunkIndex)
    {
        EnsureSubchunkStorage();
        if (!IsSubchunkIndexValid(subchunkIndex) ||
            subchunkColliderOccupancyBits == null ||
            subchunkColliderOccupancyValid == null ||
            subchunkColliderOccupancyHasSolids == null)
        {
            return;
        }

        int wordOffset = subchunkIndex * ColliderOccupancyWordsPerSubchunk;
        Array.Clear(subchunkColliderOccupancyBits, wordOffset, ColliderOccupancyWordsPerSubchunk);
        subchunkColliderOccupancyValid[subchunkIndex] = false;
        subchunkColliderOccupancyHasSolids[subchunkIndex] = false;
    }

    private void EnsureSubchunkStorage()
    {
        if (subchunks == null || subchunks.Length != SubchunksPerColumn)
            subchunks = new SubchunkState[SubchunksPerColumn];

        if (subchunkColliderBuilders == null || subchunkColliderBuilders.Length != SubchunksPerColumn)
            subchunkColliderBuilders = new SubchunkColliderBuilder[SubchunksPerColumn];

        int colliderOccupancyWordCount = SubchunksPerColumn * ColliderOccupancyWordsPerSubchunk;
        if (subchunkColliderOccupancyBits == null || subchunkColliderOccupancyBits.Length != colliderOccupancyWordCount)
            subchunkColliderOccupancyBits = new ulong[colliderOccupancyWordCount];

        if (subchunkColliderOccupancyValid == null || subchunkColliderOccupancyValid.Length != SubchunksPerColumn)
            subchunkColliderOccupancyValid = new bool[SubchunksPerColumn];

        if (subchunkColliderOccupancyHasSolids == null || subchunkColliderOccupancyHasSolids.Length != SubchunksPerColumn)
            subchunkColliderOccupancyHasSolids = new bool[SubchunksPerColumn];

        for (int i = 0; i < subchunkColliderBuilders.Length; i++)
        {
            if (subchunkColliderBuilders[i] == null)
                subchunkColliderBuilders[i] = new SubchunkColliderBuilder();
        }
    }

    private bool IsSubchunkIndexValid(int subchunkIndex)
    {
        return subchunks != null &&
               subchunkIndex >= 0 &&
               subchunkIndex < subchunks.Length;
    }

    private bool TryGetSubchunkColliderBuilder(int subchunkIndex, out SubchunkColliderBuilder colliderBuilder)
    {
        colliderBuilder = null;
        EnsureSubchunkStorage();

        if (!IsSubchunkIndexValid(subchunkIndex) ||
            subchunkColliderBuilders == null ||
            subchunkIndex >= subchunkColliderBuilders.Length)
        {
            return false;
        }

        colliderBuilder = subchunkColliderBuilders[subchunkIndex];
        return colliderBuilder != null;
    }
}
