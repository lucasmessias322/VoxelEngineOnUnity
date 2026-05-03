using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Private State


    private const int InitialActiveChunkCollectionCapacity = 512;
    private const int InitialChunkPoolCapacity = 256;
    private const int InitialChunkWorkCollectionCapacity = 256;
    private const int InitialQueuedChunkWorkCapacity = 512;
    private const int InitialBlockEditCapacity = 8192;
    private const int InitialBlockEditChunkIndexCapacity = 512;
    private const int InitialPerChunkBlockEditCapacity = 32;
    private const int InitialInteractiveBlockLightRefreshCapacity = 1024;
    private const int InitialLightColumnCapacity = 4096;
    private const int InitialLightWorkCollectionCapacity = 1024;

    // Active chunks & pool
    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>(InitialActiveChunkCollectionCapacity);
    private Queue<Chunk> chunkPool = new Queue<Chunk>(InitialChunkPoolCapacity);

    // Pending work
    private Queue<Vector2Int> pendingChunks = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private HashSet<Vector2Int> pendingChunkSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private List<PendingMesh> pendingMeshes = new List<PendingMesh>(InitialQueuedChunkWorkCapacity);
    private List<PendingData> pendingDataJobs = new List<PendingData>(InitialChunkWorkCollectionCapacity);
    private List<PendingData> pendingMeshBuildRequests = new List<PendingData>(InitialChunkWorkCollectionCapacity);
    private readonly List<PendingChunkDataBufferReturn> pendingChunkDataBufferReturns = new List<PendingChunkDataBufferReturn>(64);
    private readonly List<Chunk> retiredChunksAwaitingRecycle = new List<Chunk>(64);
    private readonly Queue<Vector2Int> queuedChunkRebuilds = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkRebuildsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, int> queuedChunkRebuildMasks = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, bool> queuedChunkRebuildRequiresCollider = new Dictionary<Vector2Int, bool>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, float> queuedChunkRebuildEarliestProcessTime = new Dictionary<Vector2Int, float>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedLightingOnlyChunkRebuilds = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedLightingOnlyChunkRebuildsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, int> queuedLightingOnlyChunkRebuildMasks = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedChunkDetailPromotions = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkDetailPromotionsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedChunkJobTrackingRefreshes = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkJobTrackingRefreshSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> queuedColliderBuilds = new Queue<Vector3Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector3Int, PendingColliderBuild> queuedColliderBuildsByKey = new Dictionary<Vector3Int, PendingColliderBuild>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> queuedInteractiveBlockLightRefreshes = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, PendingInteractiveBlockLightRefresh> queuedInteractiveBlockLightRefreshesByPosition = new Dictionary<Vector3Int, PendingInteractiveBlockLightRefresh>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> terrainOverridePositionsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>(InitialBlockEditChunkIndexCapacity);
    private readonly List<Vector3Int> relevantTerrainOverridePositions = new List<Vector3Int>(InitialQueuedChunkWorkCapacity);
    private bool terrainOverrideIndexInitialized = false;

    // Overrides and light
    private Dictionary<Vector3Int, BlockType> blockOverrides = new Dictionary<Vector3Int, BlockType>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, BlockPlacementAxis> blockPlacementAxes = new Dictionary<Vector3Int, BlockPlacementAxis>(InitialBlockEditCapacity);
    private HashSet<Vector3Int> suppressedGrassBillboards = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> permanentGrassBillboardSuppressions = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> suppressedGrassBillboardsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>(InitialBlockEditChunkIndexCapacity);
    // private Dictionary<Vector3Int, byte> globalLightMap = new Dictionary<Vector3Int, byte>();
    private Dictionary<Vector2Int, ushort[]> globalLightColumns = new Dictionary<Vector2Int, ushort[]>(InitialLightColumnCapacity);
    // Misc
    private float offsetX, offsetZ;
    private int nextChunkGeneration = 0;
    private int meshesAppliedThisFrame = 0;
    private float frameTimeAccumulator = 0f;
    private int colliderPrewarmedChunkCount;
    private int chunkPoolCreatesThisFrame;
    private bool lastEnableBlockColliders = true;
    private bool lastEnableRealisticShader = true;
    private bool lastEnableVoxelLighting = true;
    private bool lastEnableHorizontalSkylight = true;
    private bool lastEnableAmbientOcclusion = true;
    private bool lastEnableWater = true;
    private bool lastEnableChunkDetailLod = true;
    private int lastWorldMaterialProfileHash = int.MinValue;
    private int lastChunkDetailLodDistance = 10;
    private TreeLeafQualityMode lastTreeLeafQuality = TreeLeafQualityMode.Medium;
    private int lastTreeLeafFoliageSettingsHash = int.MinValue;
    private int lastHorizontalSkylightStepLoss = 1;
    private int lastSunlightSmoothingPadding = 16;
    private TreeSpawnRuleData[] cachedTreeSpawnRules = Array.Empty<TreeSpawnRuleData>();
    private VegetationBillboardRuleData[] cachedVegetationBillboardRules = Array.Empty<VegetationBillboardRuleData>();
    private bool treeSpawnRulesDirty = true;
    private bool vegetationBillboardRulesDirty = true;
    private NativeArray<NoiseLayer> cachedNativeNoiseLayers;
    private NativeArray<BlockTextureMapping> cachedNativeBlockMappings;
    private Vector4 cachedElectricalLitLedUvRectData;
    private NativeArray<BlockModelCuboid> cachedNativeBlockModelCuboids;
    private NativeArray<byte> cachedNativeEffectiveLightOpacityByBlock;
    private NativeArray<ushort> cachedNativeLightEmissionByBlock;
    private NativeArray<OreSpawnSettings> cachedNativeOreSettings;
    private NativeArray<TreeSpawnRuleData> cachedNativeTreeSpawnRules;
    private NativeArray<VegetationBillboardRuleData> cachedNativeVegetationBillboardRules;
    private bool nativeGenerationConfigDirty = true;
    private int lastResolvedVisualSubchunksPerRenderer = int.MinValue;
    private float lastPlayerChunkCoordChangeTime = float.NegativeInfinity;
    private float lastPlayerChunkDetailMovementTime = float.NegativeInfinity;
    private float lastChunkDetailPromotionRequestTime = float.NegativeInfinity;
    private Vector2Int lastChunkDetailPromotionQueueCenter = new Vector2Int(int.MinValue, int.MinValue);
    private Vector3 lastChunkDetailPromotionSamplePosition = Vector3.zero;
    private float lastChunkDetailPromotionSampleRealtime = float.NegativeInfinity;
    private bool hasChunkDetailPromotionMovementSample = false;

    // Vulkan requires every declared StructuredBuffer to be bound, even when that code path is disabled at runtime.
    private static readonly int PulledOpaqueFacesBufferPropertyId = Shader.PropertyToID("_PulledOpaqueFaces");
    private static readonly int CompactOpaqueFacesBufferPropertyId = Shader.PropertyToID("_CompactOpaqueFaces");
    private static readonly int OpaqueGpuSectionsBufferPropertyId = Shader.PropertyToID("_OpaqueGpuSections");
    private static readonly int OpaqueBlockMappingsBufferPropertyId = Shader.PropertyToID("_OpaqueBlockMappings");
    private static readonly int UnityIndirectDrawArgsBufferPropertyId = Shader.PropertyToID("unity_IndirectDrawArgs");
    private static readonly int EnableRealisticShaderPropertyId = Shader.PropertyToID("_EnableRealisticShader");
    private const int PulledOpaqueFaceStrideBytes = 112;   // 7 * float4
    private const int CompactOpaqueFaceStrideBytes = 16;   // 4 * uint
    private const int OpaqueGpuSectionStrideBytes = 32;    // 2 * float4
    private const int OpaqueBlockMappingStrideBytes = 32;  // 2 * float4
    private const int UnityIndirectDrawArgsWordCount = 4;  // IndirectDrawArgs = 4 uint (16 bytes)
    private ComputeBuffer fallbackPulledOpaqueFacesBuffer;
    private ComputeBuffer fallbackCompactOpaqueFacesBuffer;
    private ComputeBuffer fallbackOpaqueGpuSectionsBuffer;
    private ComputeBuffer fallbackOpaqueBlockMappingsBuffer;
    private ComputeBuffer fallbackUnityIndirectDrawArgsBuffer;


    // Optimization temporaries
    private static readonly Vector2Int InvalidChunkCoord = new Vector2Int(int.MinValue, int.MinValue);
    private Vector2Int _lastChunkCoord = InvalidChunkCoord;
    private Vector2Int _lastColliderCenter = new Vector2Int(int.MinValue, int.MinValue);
    private int _lastColliderDistance = -1;
    private Vector2Int _lastPendingJobPriorityCenter = new Vector2Int(int.MinValue, int.MinValue);
    private bool pendingJobPrioritiesDirty = true;
    private Camera cachedMeshApplyPriorityCamera;
    private readonly Plane[] meshApplyPriorityFrustumPlanes = new Plane[6];
    private readonly List<Vector2Int> _tempToRemove = new List<Vector2Int>();
    private readonly List<Vector2Int> pendingChunkQueuePruneBuffer = new List<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly List<ChunkDetailPromotionCandidate> chunkDetailPromotionCandidateBuffer = new List<ChunkDetailPromotionCandidate>(128);
    private Comparison<PendingData> pendingDataDistanceComparison;
    private Comparison<PendingMesh> pendingMeshDistanceComparison;
    private readonly List<Vector2Int> loadedChunkCoordsBuffer = new List<Vector2Int>(256);
    private readonly List<int3> suppressedGrassBillboardInt3Buffer = new List<int3>(128);
    private readonly List<BlockEdit> requestChunkEditsBuffer = new List<BlockEdit>(128);
    private readonly List<BlockEdit> rebuildChunkEditsBuffer = new List<BlockEdit>(128);
    private readonly List<BlockEdit> fastRebuildOverrideEditsBuffer = new List<BlockEdit>(64);
    private readonly List<TreeSpawnRuleData> treeSpawnRuleBuildBuffer = new List<TreeSpawnRuleData>(12);
    private readonly List<VegetationBillboardRuleData> vegetationBillboardRuleBuildBuffer = new List<VegetationBillboardRuleData>(16);
    private readonly Queue<Vector3Int> propagateLightQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector2Int, int> propagateDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<(Vector3Int pos, ushort lightLevel)> removeLightDarkQueueBuffer = new Queue<(Vector3Int, ushort)>(InitialLightWorkCollectionCapacity);
    private readonly Queue<Vector3Int> removeLightRefillQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector3Int, ushort> removeLightAffectedContributionsBuffer = new Dictionary<Vector3Int, ushort>(InitialLightWorkCollectionCapacity);

    private enum ChunkDetailPromotionCandidateState : byte
    {
        Drop = 0,
        RequeueBlocked = 1,
        Ready = 2
    }

    private struct ChunkDetailPromotionCandidate
    {
        public Vector2Int coord;
        public Chunk chunk;
        public float score;
        public bool preferredView;
        public ChunkDetailPromotionCandidateState state;
    }

    private struct PendingInteractiveBlockLightRefresh
    {
        public Vector3Int position;
        public BlockType previousType;
        public BlockType newType;
        public float earliestRefreshTime;
    }

    private readonly Dictionary<Vector2Int, int> removeLightDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> refillLightQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly HashSet<Vector3Int> refillLightEnqueuedBuffer = new HashSet<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector2Int, int> refillLightDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> cleanupLightColumnKeysBuffer = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly List<Vector2Int> cleanupLightColumnsRemoveBuffer = new List<Vector2Int>(128);

    #endregion

}
