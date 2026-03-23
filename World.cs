using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;


[Serializable]
public struct TreeSettings
{
    public int minHeight;
    public int maxHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int minSpacing;
    public float density;
    public float noiseScale;
    public int seed;
}

[Serializable]
public struct OreSpawnSettings
{
    public bool enabled;
    public BlockType blockType;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int veinsPerChunk;
    [Min(1)] public int minVeinSize;
    [Min(1)] public int maxVeinSize;
    [Min(0)] public int minSurfaceDepth;
    public bool replaceStone;
    public bool replaceDeepslate;
}

[Serializable]
public struct WormCaveSettings
{
    public bool enabled;
    [Range(0f, 1f)] public float spawnChance;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(1)] public int minWormsPerChunk;
    [Min(1)] public int maxWormsPerChunk;
    [Min(1)] public int minLength;
    [Min(1)] public int maxLength;
    [Range(0.1f, 4f)] public float stepSize;
    [Range(0.5f, 8f)] public float minRadius;
    [Range(0.5f, 8f)] public float maxRadius;
    [Range(0f, 2f)] public float radiusJitter;
    [Range(0f, 1f)] public float initialVerticalRange;
    [Range(0f, 1f)] public float verticalClamp;
    [Range(0f, 1f)] public float turnRate;
    [Range(0f, 1f)] public float verticalTurnRate;
    [Range(0f, 1f)] public float forkChance;
    [Min(0)] public int maxForksPerWorm;
    [Min(0)] public int minSurfaceDepth;
    [Range(0f, 1f)] public float surfaceEntranceChance;
    [Range(0f, 1f)] public float surfaceEntranceUpwardBias;
    [Min(0)] public int sourceChunkRadius;
    public int seedOffset;

    public static WormCaveSettings Default => new WormCaveSettings
    {
        enabled = true,
        spawnChance = 0.26f,
        minY = 8,
        maxY = 112,
        minWormsPerChunk = 1,
        maxWormsPerChunk = 2,
        minLength = 30,
        maxLength = 72,
        stepSize = 1.15f,
        minRadius = 1.3f,
        maxRadius = 2.8f,
        radiusJitter = 0.30f,
        initialVerticalRange = 0.2f,
        verticalClamp = 0.36f,
        turnRate = 0.17f,
        verticalTurnRate = 0.09f,
        forkChance = 0.06f,
        maxForksPerWorm = 1,
        minSurfaceDepth = 4,
        surfaceEntranceChance = 0.16f,
        surfaceEntranceUpwardBias = 0.35f,
        sourceChunkRadius = 1,
        seedOffset = 13391
    };

    public bool LooksUninitialized =>
        !enabled &&
        spawnChance == 0f &&
        minY == 0 &&
        maxY == 0 &&
        minWormsPerChunk == 0 &&
        maxWormsPerChunk == 0 &&
        minLength == 0 &&
        maxLength == 0 &&
        stepSize == 0f &&
        minRadius == 0f &&
        maxRadius == 0f;
}

[Serializable]
public enum CaveGenerationMode : byte
{
    ModernSpaghetti = 0,
    LegacyWorms = 1
}

[Serializable]
public struct SpaghettiCaveSettings
{
    public bool enabled;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int minSurfaceDepth;
    [Min(0)] public int entranceSurfaceDepth;
    [Range(-0.35f, 0.35f)] public float densityBias;
    public int seedOffset;

    public static SpaghettiCaveSettings Default => new SpaghettiCaveSettings
    {
        enabled = true,
        minY = 4,
        maxY = 320,
        minSurfaceDepth = 6,
        entranceSurfaceDepth = 0,
        densityBias = 0f,
        seedOffset = 48271
    };

    public bool LooksUninitialized =>
        !enabled &&
        minY == 0 &&
        maxY == 0 &&
        minSurfaceDepth == 0 &&
        entranceSurfaceDepth == 0 &&
        densityBias == 0f &&
        seedOffset == 0;

    public bool LooksLikeInitialSurfaceClosedDefault =>
        enabled &&
        minY == 4 &&
        maxY == 320 &&
        minSurfaceDepth == 6 &&
        entranceSurfaceDepth == 1 &&
        densityBias == 0f &&
        seedOffset == 48271;
}



#region Utilities

public static class LightUtils
{
    // Junta as duas luzes (0-15) em um Ãºnico byte
    public static byte PackLight(byte skyLight, byte blockLight)
    {
        return (byte)((skyLight << 4) | (blockLight & 0x0F));
    }

    // Extrai apenas a luz do cÃ©u (bits 4 a 7)
    public static byte GetSkyLight(byte packedLight)
    {
        return (byte)((packedLight >> 4) & 0x0F);
    }

    // Extrai apenas a luz dos blocos (bits 0 a 3)
    public static byte GetBlockLight(byte packedLight)
    {
        return (byte)(packedLight & 0x0F);
    }
}

#endregion

public partial class World : MonoBehaviour
{
    #region Singleton

    public static World Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (caveWormSettings.LooksUninitialized)
            caveWormSettings = WormCaveSettings.Default;
        if (caveSpaghettiSettings.LooksUninitialized || caveSpaghettiSettings.LooksLikeInitialSurfaceClosedDefault)
            caveSpaghettiSettings = SpaghettiCaveSettings.Default;

        EnsureLoadingBootstrapExists();
    }

    #endregion

    #region Inspector Fields - World Setup

    [Header("General")]
    public Transform player;
    public GameObject chunkPrefab;
    public int renderDistance = 4;
    [Tooltip("Raio em chunks usado pelos sistemas de simulacao. Limitado automaticamente pela renderDistance.")]
    [Min(0)]
    public int simulationDistance = 4;
    public int poolSize = 200;
    [Tooltip("Cria subchunks, MeshFilters, MeshRenderers e Meshes do pool no Start para evitar picos quando novas areas entram em cena.")]
    public bool prewarmPooledChunkVisuals = true;

    [Header("Atlas / Material")]
    public Material[] Material;
    public int atlasTilesX = 4;
    public int atlasTilesY = 4;

    [Header("Inventory Block Icons")]
    [Tooltip("Atlas opcional usado apenas para gerar os icones isometricos dos blocos no inventario. Se vazio, usa o atlas encontrado nos materiais do mundo.")]
    public Texture blockItemIconAtlasTexture;

    [Header("Terrain Layer Profile")]
    [Tooltip("Perfil com as camadas de terreno. Quando atribuido, o World usa as layers desse asset.")]
    public TerrainLayerProfileSO terrainLayerProfile;

    [Header("Noise Settings (Runtime)")]
    [Tooltip("Preenchido a partir do Terrain Layer Profile durante validacao/execucao.")]
    [SerializeField, HideInInspector] public NoiseLayer[] noiseLayers = Array.Empty<NoiseLayer>();

    [Header("Domain Warping Settings (Runtime)")]
    [Tooltip("Preenchido a partir do Terrain Layer Profile durante validacao/execucao.")]
    [SerializeField, HideInInspector] public WarpLayer[] warpLayers = Array.Empty<WarpLayer>();
    public int baseHeight = 64;
    public int heightVariation = 32;
    public int seed = 1337;

    [Header("Block Data")]
    public BlockDataSO blockData;

    [Header("Sea Settings")]
    public int seaLevel = 62;
    public BlockType waterBlock = BlockType.Water;

    public int CliffTreshold = 2;

    [Header("Ore Settings")]
    public OreSpawnSettings[] oreSettings = new OreSpawnSettings[]
    {
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.CoalOre,
            minY = 8,
            maxY = 160,
            veinsPerChunk = 18,
            minVeinSize = 6,
            maxVeinSize = 18,
            minSurfaceDepth = 4,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.IronOre,
            minY = 8,
            maxY = 96,
            veinsPerChunk = 12,
            minVeinSize = 4,
            maxVeinSize = 11,
            minSurfaceDepth = 5,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.GoldOre,
            minY = 6,
            maxY = 48,
            veinsPerChunk = 7,
            minVeinSize = 3,
            maxVeinSize = 9,
            minSurfaceDepth = 6,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.RedstoneOre,
            minY = 4,
            maxY = 24,
            veinsPerChunk = 7,
            minVeinSize = 3,
            maxVeinSize = 8,
            minSurfaceDepth = 7,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.DiamondOre,
            minY = 4,
            maxY = 20,
            veinsPerChunk = 4,
            minVeinSize = 3,
            maxVeinSize = 7,
            minSurfaceDepth = 8,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.EmeraldOre,
            minY = 16,
            maxY = 80,
            veinsPerChunk = 2,
            minVeinSize = 2,
            maxVeinSize = 5,
            minSurfaceDepth = 6,
            replaceStone = true,
            replaceDeepslate = false
        }
    };

    [Header("Cave Settings")]
    [Tooltip("Modo de geracao das cavernas. Modern Spaghetti usa density functions inspiradas no Minecraft moderno; Legacy Worms preserva o algoritmo antigo baseado em worms.")]
    public CaveGenerationMode caveGenerationMode = CaveGenerationMode.ModernSpaghetti;
    public SpaghettiCaveSettings caveSpaghettiSettings = SpaghettiCaveSettings.Default;
    [Header("Legacy Worm Cave Settings")]
    public WormCaveSettings caveWormSettings = WormCaveSettings.Default;

    [Header("Performance Settings")]
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;
    [Tooltip("Quantidade maxima de subchunks que podem reconstruir collider por frame.")]
    [Min(1)]
    public int maxColliderBuildsPerFrame = 1;
    [Tooltip("Quantidade maxima de jobs de dados concluidos e processados por frame.")]
    [Min(1)]
    public int maxDataCompletionsPerFrame = 2;
    public float frameTimeBudgetMS = 4f;
    [Tooltip("Limite de jobs de geraÃ§Ã£o de dados (inclui iluminaÃ§Ã£o) simultÃ¢neos para evitar queda brusca de FPS.")]
    [Min(1)]
    public int maxPendingDataJobs = 2;
    [Tooltip("Quantidade maxima de pedidos de rebuild de chunk processados por frame.")]
    [Min(1)]
    public int maxChunkRebuildsPerFrame = 1;

    [Header("Features Toggle")]
    public bool enableTrees = true;

    [Header("Billboard Grass")]
    public bool enableGrassBillboards = true;
    [Range(0f, 1f)]
    public float grassBillboardChance = 0.22f;
    public BlockType grassBillboardBlockType = BlockType.Leaves;
    [Range(0.2f, 2f)]
    public float grassBillboardHeight = 0.9f;
    [Range(0.01f, 1f)]
    public float grassBillboardNoiseScale = 0.12f;
    [Range(0f, 0.35f)]
    public float grassBillboardJitter = 0.16f;

    [Header("Ambient Occlusion")]
    [Tooltip("Liga/desliga o Ambient Occlusion da malha dos voxels para testes de performance.")]
    public bool enableAmbientOcclusion = true;
    [Tooltip("Forca do AO. 1 = padrao, >1 escurece mais os cantos, 0 desativa o AO.")]
    [Range(0f, 2.5f)]
    public float aoStrength = 1.35f;
    [Tooltip("Curva do AO. Valores maiores aumentam o contraste do escurecimento.")]
    [Range(0.5f, 3f)]
    public float aoCurveExponent = 1.25f;
    [Tooltip("Luz minima aplicada apos AO. Menor valor permite cantos mais escuros.")]
    [Range(0f, 1f)]
    public float aoMinLight = 0.08f;
    [Tooltip("Quando ativado, usa um greedy meshing rapido com validacao barata de AO/luz. Mantem o AO correto na maior parte dos casos sem o custo alto da busca exaustiva.")]
    public bool useFastBedrockStyleMeshing = true;

    [Header("Debug / Physics")]
    [Tooltip("Ativa ou desativa o sistema de colliders dos blocos. Quando desligado, novos chunks nao geram collider.")]
    public bool enableBlockColliders = true;

    [Header("Debug / Gizmos")]
    [Tooltip("Ativa a renderizacao de gizmos de debug do sistema de chunks.")]
    public bool debugDrawGizmos = false;
    [Tooltip("Quando ativado, os gizmos so aparecem em Play Mode.")]
    public bool debugGizmosOnlyWhenPlaying = true;
    [Tooltip("Mostra os limites do chunk onde o player esta.")]
    public bool debugDrawPlayerChunkBounds = true;
    [Tooltip("Mostra a grade de chunks na area de renderDistance.")]
    public bool debugDrawRenderDistanceGrid = true;
    [Tooltip("Mostra os bounds dos chunks ativos.")]
    public bool debugDrawActiveChunkBounds = true;
    [Tooltip("Mostra chunks pendentes de geracao/mesh.")]
    public bool debugDrawPendingChunkQueue = false;
    [Tooltip("Mostra os bounds de cada subchunk dos chunks ativos.")]
    public bool debugDrawSubchunkBounds = false;
    [Tooltip("Quando ligado, desenha apenas subchunks com geometria.")]
    public bool debugSubchunksOnlyWithGeometry = true;
    [Range(0f, 0.25f)]
    public float debugGizmoFillAlpha = 0.06f;

    public Color debugPlayerChunkColor = new Color(1f, 0.8f, 0.15f, 1f);
    public Color debugRenderGridColor = new Color(0.2f, 0.6f, 1f, 1f);
    public Color debugActiveChunkColor = new Color(0.25f, 1f, 0.35f, 1f);
    public Color debugPendingChunkColor = new Color(1f, 0.4f, 0.2f, 1f);
    public Color debugSubchunkColor = new Color(0.85f, 0.4f, 1f, 1f);

    [Header("Lighting")]
    [Tooltip("Liga/desliga o calculo de iluminacao voxel/skylight para testes de performance. Quando desligado, os chunks usam brilho uniforme.")]
    public bool enableVoxelLighting = true;
    [Tooltip("Padding horizontal em voxels usado apenas pela suavizacao de skylight entre chunks. Valores altos melhoram costuras visuais, mas aumentam o custo do volume de luz.")]
    [Min(1)]
    public int sunlightSmoothingPadding = 16;
    [Tooltip("Padding horizontal usado pelas etapas caras de geracao detalhada (arvores, cavernas e minerios). Mantido separado do padding de luz para reduzir custo.")]
    [Min(1)]
    public int detailedGenerationPadding = 1;

    #endregion

    #region Private State

    // Active chunks & pool
    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Queue<Chunk> chunkPool = new Queue<Chunk>();

    // Pending work
    private List<(Vector2Int coord, float distSq)> pendingChunks = new List<(Vector2Int, float)>();
    private List<PendingMesh> pendingMeshes = new List<PendingMesh>();
    private List<PendingData> pendingDataJobs = new List<PendingData>();
    private readonly Queue<Vector2Int> queuedChunkRebuilds = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> queuedChunkRebuildsSet = new HashSet<Vector2Int>();
    private readonly Dictionary<Vector2Int, int> queuedChunkRebuildMasks = new Dictionary<Vector2Int, int>();
    private readonly Dictionary<Vector2Int, bool> queuedChunkRebuildRequiresCollider = new Dictionary<Vector2Int, bool>();
    private readonly Queue<Vector3Int> queuedColliderBuilds = new Queue<Vector3Int>();
    private readonly Dictionary<Vector3Int, PendingColliderBuild> queuedColliderBuildsByKey = new Dictionary<Vector3Int, PendingColliderBuild>();
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> terrainOverridePositionsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>();
    private readonly List<Vector3Int> relevantTerrainOverridePositions = new List<Vector3Int>(128);
    private bool terrainOverrideIndexInitialized = false;

    // Overrides and light
    private Dictionary<Vector3Int, BlockType> blockOverrides = new Dictionary<Vector3Int, BlockType>();
    private HashSet<Vector3Int> suppressedGrassBillboards = new HashSet<Vector3Int>();
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> suppressedGrassBillboardsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>();
    // private Dictionary<Vector3Int, byte> globalLightMap = new Dictionary<Vector3Int, byte>();
    private Dictionary<Vector2Int, byte[]> globalLightColumns = new Dictionary<Vector2Int, byte[]>();
    // Misc
    private float offsetX, offsetZ;
    private int nextChunkGeneration = 0;
    private int meshesAppliedThisFrame = 0;
    private float frameTimeAccumulator = 0f;
    private bool lastEnableBlockColliders = true;
    private bool lastEnableVoxelLighting = true;
    private bool lastEnableAmbientOcclusion = true;
    private TreeSpawnRuleData[] cachedTreeSpawnRules = Array.Empty<TreeSpawnRuleData>();
    private bool treeSpawnRulesDirty = true;
    private NativeArray<NoiseLayer> cachedNativeNoiseLayers;
    private NativeArray<WarpLayer> cachedNativeWarpLayers;
    private NativeArray<BlockTextureMapping> cachedNativeBlockMappings;
    private NativeArray<byte> cachedNativeEffectiveLightOpacityByBlock;
    private NativeArray<OreSpawnSettings> cachedNativeOreSettings;
    private NativeArray<TreeSpawnRuleData> cachedNativeTreeSpawnRules;
    private bool nativeGenerationConfigDirty = true;


    // Optimization temporaries
    private Vector2Int _lastChunkCoord = new Vector2Int(-99999, -99999);
    private Vector2Int _lastSimulationCenter = new Vector2Int(int.MinValue, int.MinValue);
    private int _lastSimulationDistance = -1;
    private readonly HashSet<Vector2Int> _tempNeededCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _tempToRemove = new List<Vector2Int>();

    private Vector2Int GetCurrentPlayerChunkCoord()
    {
        if (player == null)
            return _lastChunkCoord;

        return new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );
    }

    private float GetChunkDistanceSqToPlayer(Vector2Int coord)
    {
        if (player == null)
        {
            float fallbackDx = coord.x - _lastChunkCoord.x;
            float fallbackDz = coord.y - _lastChunkCoord.y;
            return fallbackDx * fallbackDx + fallbackDz * fallbackDz;
        }

        // Usa distancia em coordenadas de chunk com posicao fracionaria do player.
        float playerChunkX = player.position.x / Chunk.SizeX;
        float playerChunkZ = player.position.z / Chunk.SizeZ;
        float centerX = coord.x + 0.5f;
        float centerZ = coord.y + 0.5f;
        float dx = centerX - playerChunkX;
        float dz = centerZ - playerChunkZ;
        return dx * dx + dz * dz;
    }

    private bool IsCoordInsideRenderDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideCircularDistance(coord, center, renderDistance);
    }

    private int GetEffectiveSimulationDistance()
    {
        return Mathf.Clamp(simulationDistance, 0, Mathf.Max(0, renderDistance));
    }

    private static bool IsCoordInsideCircularDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = coord.x - center.x;
        int dz = coord.y - center.y;
        return dx * dx + dz * dz <= clampedDistance * clampedDistance;
    }

    private static bool IsCoordInsideDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = Mathf.Abs(coord.x - center.x);
        int dz = Mathf.Abs(coord.y - center.y);
        return dx <= clampedDistance && dz <= clampedDistance;
    }

    private bool IsCoordInsideSimulationDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideDistance(coord, center, GetEffectiveSimulationDistance());
    }

    private void InvalidateNativeGenerationCaches()
    {
        nativeGenerationConfigDirty = true;
    }

    private void DisposeNativeGenerationCaches()
    {
        if (cachedNativeNoiseLayers.IsCreated) cachedNativeNoiseLayers.Dispose();
        if (cachedNativeWarpLayers.IsCreated) cachedNativeWarpLayers.Dispose();
        if (cachedNativeBlockMappings.IsCreated) cachedNativeBlockMappings.Dispose();
        if (cachedNativeEffectiveLightOpacityByBlock.IsCreated) cachedNativeEffectiveLightOpacityByBlock.Dispose();
        if (cachedNativeOreSettings.IsCreated) cachedNativeOreSettings.Dispose();
        if (cachedNativeTreeSpawnRules.IsCreated) cachedNativeTreeSpawnRules.Dispose();
    }

    private void EnsureNativeGenerationCaches()
    {
        bool cachesCreated = cachedNativeNoiseLayers.IsCreated &&
                             cachedNativeWarpLayers.IsCreated &&
                             cachedNativeBlockMappings.IsCreated &&
                             cachedNativeEffectiveLightOpacityByBlock.IsCreated &&
                             cachedNativeOreSettings.IsCreated &&
                             cachedNativeTreeSpawnRules.IsCreated;

        if (!nativeGenerationConfigDirty && cachesCreated)
        {
            return;
        }

        if (nativeGenerationConfigDirty && cachesCreated &&
            (pendingDataJobs.Count > 0 || pendingMeshes.Count > 0))
        {
            return;
        }

        DisposeNativeGenerationCaches();

        NoiseLayer[] runtimeNoiseLayers = noiseLayers ?? Array.Empty<NoiseLayer>();
        WarpLayer[] runtimeWarpLayers = warpLayers ?? Array.Empty<WarpLayer>();
        BlockTextureMapping[] runtimeBlockMappings = blockData != null && blockData.mappings != null
            ? blockData.mappings
            : Array.Empty<BlockTextureMapping>();
        OreSpawnSettings[] runtimeOreSettings = oreSettings ?? Array.Empty<OreSpawnSettings>();
        TreeSpawnRuleData[] runtimeTreeSpawnRules = GetActiveTreeSpawnRules();

        cachedNativeNoiseLayers = new NativeArray<NoiseLayer>(runtimeNoiseLayers, Allocator.Persistent);
        cachedNativeWarpLayers = new NativeArray<WarpLayer>(runtimeWarpLayers, Allocator.Persistent);
        cachedNativeBlockMappings = new NativeArray<BlockTextureMapping>(runtimeBlockMappings, Allocator.Persistent);
        cachedNativeEffectiveLightOpacityByBlock = new NativeArray<byte>(runtimeBlockMappings.Length, Allocator.Persistent);
        for (int i = 0; i < runtimeBlockMappings.Length; i++)
            cachedNativeEffectiveLightOpacityByBlock[i] = ChunkLighting.GetEffectiveOpacity(runtimeBlockMappings[i]);
        cachedNativeOreSettings = new NativeArray<OreSpawnSettings>(runtimeOreSettings, Allocator.Persistent);
        cachedNativeTreeSpawnRules = new NativeArray<TreeSpawnRuleData>(runtimeTreeSpawnRules, Allocator.Persistent);
        nativeGenerationConfigDirty = false;
    }

    private void RefreshSimulationDistanceStateIfNeeded()
    {
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int effectiveDistance = GetEffectiveSimulationDistance();
        if (simulationCenter == _lastSimulationCenter && effectiveDistance == _lastSimulationDistance)
            return;

        _lastSimulationCenter = simulationCenter;
        _lastSimulationDistance = effectiveDistance;
        RefreshSimulationDistanceState(simulationCenter);
    }

    private bool IsChunkInsideSimulationDistance(Vector2Int coord)
    {
        return IsCoordInsideSimulationDistance(coord, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos)
    {
        return IsWorldPositionInsideSimulationDistance(worldPos, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos, Vector2Int center)
    {
        return IsCoordInsideSimulationDistance(GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z), center);
    }

    private int CountPendingDataJobsInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingDataJobs[i].coord, center))
                count++;
        }

        return count;
    }

    private int CountPendingMeshesInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingMeshes[i].coord, center))
            {
                int remaining = 0;
                int mask = pendingMeshes[i].dirtySubchunkMask;
                for (int sub = pendingMeshes[i].nextSubchunkApplyIndex; sub < Chunk.SubchunksPerColumn; sub++)
                {
                    if ((mask & (1 << sub)) != 0)
                        remaining++;
                }

                count += Mathf.Max(1, remaining);
            }
        }

        return count;
    }

    private void RefreshPendingChunkPriorities()
    {
        for (int i = 0; i < pendingChunks.Count; i++)
        {
            var item = pendingChunks[i];
            pendingChunks[i] = (item.coord, GetChunkDistanceSqToPlayer(item.coord));
        }

        pendingChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));
    }

    private void PrioritizePendingJobsByDistance()
    {
        if (pendingDataJobs.Count > 1)
        {
            pendingDataJobs.Sort((a, b) =>
                GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord)));
        }

        if (pendingMeshes.Count > 1)
        {
            pendingMeshes.Sort((a, b) =>
            {
                int distCmp = GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord));
                if (distCmp != 0) return distCmp;
                return a.nextSubchunkApplyIndex.CompareTo(b.nextSubchunkApplyIndex);
            });
        }
    }

    private int GetMeshNeighborPadding()
    {
        return 1;
    }

    private int GetDetailedGenerationBorderSize()
    {
        return Mathf.Max(GetMeshNeighborPadding(), detailedGenerationPadding);
    }

    private int GetLightSmoothingBorderSize()
    {
        if (!enableVoxelLighting)
            return GetMeshNeighborPadding();

        return Mathf.Max(GetMeshNeighborPadding(), sunlightSmoothingPadding);
    }

    private int GetChunkBorderSize()
    {
        return Mathf.Max(GetDetailedGenerationBorderSize(), GetLightSmoothingBorderSize());
    }

    private static bool CanChunkProvideVoxelSnapshot(Chunk chunk)
    {
        return chunk != null &&
               chunk.voxelData.IsCreated &&
               chunk.hasVoxelSnapshot;
    }

    private static Vector3Int GetColliderBuildKey(Vector2Int coord, int subchunkIndex)
    {
        return new Vector3Int(coord.x, subchunkIndex, coord.y);
    }

    private void EnqueueColliderBuild(Vector2Int coord, int expectedGen, int subchunkIndex)
    {
        Vector3Int key = GetColliderBuildKey(coord, subchunkIndex);
        PendingColliderBuild request = new PendingColliderBuild
        {
            coord = coord,
            expectedGen = expectedGen,
            subchunkIndex = subchunkIndex
        };

        if (queuedColliderBuildsByKey.ContainsKey(key))
        {
            queuedColliderBuildsByKey[key] = request;
            return;
        }

        queuedColliderBuildsByKey.Add(key, request);
        queuedColliderBuilds.Enqueue(key);
    }

    private void ProcessPendingColliderBuilds()
    {
        if (!enableBlockColliders || queuedColliderBuilds.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, maxColliderBuildsPerFrame);
        int processed = 0;
        int attempts = queuedColliderBuilds.Count;
        BlockTextureMapping[] blockMappings = blockData != null ? blockData.mappings : null;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        while (processed < perFrameLimit && attempts-- > 0 && queuedColliderBuilds.Count > 0)
        {
            Vector3Int key = queuedColliderBuilds.Dequeue();
            if (!queuedColliderBuildsByKey.TryGetValue(key, out PendingColliderBuild request))
                continue;

            queuedColliderBuildsByKey.Remove(key);

            if (!activeChunks.TryGetValue(request.coord, out Chunk chunk) ||
                chunk == null ||
                chunk.generation != request.expectedGen ||
                !chunk.hasVoxelData ||
                !chunk.voxelData.IsCreated ||
                chunk.subchunks == null ||
                request.subchunkIndex < 0 ||
                request.subchunkIndex >= chunk.subchunks.Length)
            {
                continue;
            }

            Subchunk subchunk = chunk.subchunks[request.subchunkIndex];
            if (subchunk == null)
                continue;

            if (!IsCoordInsideSimulationDistance(request.coord, simulationCenter))
            {
                subchunk.SetColliderSystemEnabled(false);
                EnqueueColliderBuild(request.coord, request.expectedGen, request.subchunkIndex);
                continue;
            }

            if (!subchunk.hasGeometry || !subchunk.CanHaveColliders)
            {
                subchunk.ClearColliderData();
                continue;
            }

            int startY = request.subchunkIndex * Chunk.SubchunkHeight;
            int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);
            subchunk.RebuildColliders(chunk.voxelData, blockMappings, startY, endY);
            subchunk.SetColliderSystemEnabled(true);
            processed++;
        }
    }

    private void EnsureTerrainOverrideIndexBuilt()
    {
        if (terrainOverrideIndexInitialized)
            return;

        terrainOverridePositionsByChunk.Clear();
        foreach (var kv in blockOverrides)
        {
            Vector3Int worldPos = kv.Key;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;

            Vector2Int coord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
            if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
            {
                positions = new HashSet<Vector3Int>();
                terrainOverridePositionsByChunk[coord] = positions;
            }

            positions.Add(worldPos);
        }

        terrainOverrideIndexInitialized = true;
    }

    private void IndexTerrainOverride(Vector3Int worldPos, Vector2Int coord)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return;

        if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
        {
            positions = new HashSet<Vector3Int>();
            terrainOverridePositionsByChunk[coord] = positions;
        }

        positions.Add(worldPos);
    }

    private void CollectRelevantTerrainOverridePositions(Vector2Int coord, int borderSize, List<Vector3Int> output)
    {
        output.Clear();
        if (blockOverrides.Count == 0)
            return;

        EnsureTerrainOverrideIndexBuilt();

        int minX = coord.x * Chunk.SizeX - borderSize;
        int minZ = coord.y * Chunk.SizeZ - borderSize;
        int maxX = coord.x * Chunk.SizeX + Chunk.SizeX - 1 + borderSize;
        int maxZ = coord.y * Chunk.SizeZ + Chunk.SizeZ - 1 + borderSize;
        int chunkRadiusX = Mathf.CeilToInt(borderSize / (float)Chunk.SizeX);
        int chunkRadiusZ = Mathf.CeilToInt(borderSize / (float)Chunk.SizeZ);

        for (int dz = -chunkRadiusZ; dz <= chunkRadiusZ; dz++)
        {
            for (int dx = -chunkRadiusX; dx <= chunkRadiusX; dx++)
            {
                Vector2Int candidateCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                if (!terrainOverridePositionsByChunk.TryGetValue(candidateCoord, out HashSet<Vector3Int> positions))
                    continue;

                foreach (Vector3Int worldPos in positions)
                {
                    if (worldPos.x < minX || worldPos.x > maxX || worldPos.z < minZ || worldPos.z > maxZ)
                        continue;

                    output.Add(worldPos);
                }
            }
        }
    }

    private void AppendRelevantBlockEdits(Vector2Int coord, int borderSize, List<BlockEdit> editsList)
    {
        if (editsList == null)
            return;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;

            editsList.Add(new BlockEdit
            {
                x = worldPos.x,
                y = worldPos.y,
                z = worldPos.z,
                type = (int)overrideType
            });
        }
    }

    private TreeSpawnRuleData[] GetActiveTreeSpawnRules()
    {
        if (treeSpawnRulesDirty)
            RebuildTreeSpawnRuleCache();

        return cachedTreeSpawnRules;
    }

    private int GetMaxTreeCanopyRadiusForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 0;

        int maxRadius = 0;
        for (int i = 0; i < rules.Length; i++)
            maxRadius = Mathf.Max(maxRadius, TreeGenerationMetrics.GetHorizontalReach(rules[i].treeStyle, rules[i].settings));

        return maxRadius;
    }

    private int GetMaxTreeRadiusForGeneration()
    {
        return GetMaxTreeCanopyRadiusForGeneration();
    }

    private int GetMaxTreeMarginForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 1;

        int maxMargin = 1;
        for (int i = 0; i < rules.Length; i++)
        {
            TreeSettings s = rules[i].settings;
            maxMargin = Mathf.Max(maxMargin, Mathf.Max(1, TreeGenerationMetrics.GetVerticalMargin(rules[i].treeStyle, s)));
        }

        return maxMargin;
    }

    private void RebuildTreeSpawnRuleCache()
    {
        treeSpawnRulesDirty = false;

        List<TreeSpawnRuleData> rules = new List<TreeSpawnRuleData>(12);
        AddTreeRulesFromBiomeDefinitions(rules);
        SortTreeSpawnRules(rules);

        cachedTreeSpawnRules = rules.Count > 0 ? rules.ToArray() : Array.Empty<TreeSpawnRuleData>();
    }

    private void AddTreeRulesFromBiomeDefinitions(List<TreeSpawnRuleData> rules)
    {
        BiomeDefinitionSO[] definitions = GetConfiguredBiomeDefinitions();
        if (definitions == null || definitions.Length == 0)
            return;

        for (int i = 0; i < definitions.Length; i++)
        {
            BiomeDefinitionSO definition = definitions[i];
            if (definition == null || !definition.hasTrees)
                continue;
            if (definition.treeConfigs == null || definition.treeConfigs.Length == 0)
                continue;

            for (int j = 0; j < definition.treeConfigs.Length; j++)
            {
                BiomeTreeConfig treeConfig = definition.treeConfigs[j];
                if (!treeConfig.enabled)
                    continue;

                TreeSettings sanitized = SanitizeTreeSettings(treeConfig.treeStyle, treeConfig.settings);
                rules.Add(new TreeSpawnRuleData
                {
                    biome = definition.biomeType,
                    treeStyle = treeConfig.treeStyle,
                    settings = sanitized
                });
            }
        }
    }

    private static void SortTreeSpawnRules(List<TreeSpawnRuleData> rules)
    {
        if (rules == null || rules.Count <= 1)
            return;

        // Reserve space for larger canopies first so mixed-tree biomes behave more like Minecraft feature placement.
        rules.Sort((a, b) =>
        {
            int biomeCompare = a.biome.CompareTo(b.biome);
            if (biomeCompare != 0)
                return biomeCompare;

            int spacingCompare = TreeGenerationMetrics.GetPlacementSpacingRadius(b.treeStyle, b.settings)
                .CompareTo(TreeGenerationMetrics.GetPlacementSpacingRadius(a.treeStyle, a.settings));
            if (spacingCompare != 0)
                return spacingCompare;

            int densityCompare = a.settings.density.CompareTo(b.settings.density);
            if (densityCompare != 0)
                return densityCompare;

            return a.treeStyle.CompareTo(b.treeStyle);
        });
    }

    private TreeSettings SanitizeTreeSettings(TreeStyle treeStyle, TreeSettings raw)
    {
        TreeSettings s = raw;
        s.minHeight = Mathf.Max(1, s.minHeight);
        s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
        s.canopyRadius = Mathf.Max(0, s.canopyRadius);
        s.canopyHeight = Mathf.Max(1, s.canopyHeight);
        s.minSpacing = Mathf.Max(1, s.minSpacing);
        s.density = Mathf.Clamp01(s.density);
        s.noiseScale = Mathf.Max(0.0001f, s.noiseScale);

        switch (treeStyle)
        {
            case TreeStyle.TaigaSpruce:
                s.canopyRadius = Mathf.Max(2, s.canopyRadius);
                s.canopyHeight = Mathf.Max(5, s.canopyHeight);
                break;

            case TreeStyle.SavannaAcacia:
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(3, s.canopyHeight);
                s.minSpacing = Mathf.Max(6, s.minSpacing);
                break;

            case TreeStyle.FancyOak:
                s.minHeight = Mathf.Max(9, s.minHeight);
                s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(4, s.canopyHeight);
                s.minSpacing = Mathf.Max(9, s.minSpacing);
                break;

            case TreeStyle.Cactus:
                s.canopyRadius = Mathf.Max(1, s.canopyRadius);
                break;
        }

        if (s.seed == 0)
            s.seed = seed;

        return s;
    }

    #endregion

    #region Internal Structs (Pending jobs)

    private struct PendingMesh
    {
        public JobHandle handle;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector2> uv2;
        public NativeList<Vector3> normals;
        public Vector2Int coord;
        public int expectedGen;
        public Chunk parentChunk;
        public NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges;
        public NativeArray<ulong> subchunkVisibilityMasks;
        public int dirtySubchunkMask;
        public int nextSubchunkApplyIndex;

        // Data arrays (kept so we can dispose later)
        public NativeArray<int> heightCache;
        public NativeArray<byte> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public NativeArray<int3> suppressedBillboards;
        public NativeList<Vector4> extraUVs;
        public bool buildColliders;
    }

    private struct PendingData
    {
        public JobHandle handle;
        public NativeArray<int> heightCache;
        public NativeArray<byte> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public int borderSize;

        public Chunk chunk;
        public Vector2Int coord;
        public int expectedGen;
        public NativeArray<byte> chunkLightData;
        public NativeArray<byte> lightOpacityData;
        public NativeArray<BlockEdit> edits;
        public NativeArray<byte> fastRebuildSnapshotVoxelData;
        public NativeArray<byte> fastRebuildSnapshotLoadedChunks;
        public NativeArray<BlockEdit> fastRebuildOverrides;

        public NativeArray<bool> subchunkNonEmpty;
        public int dirtySubchunkMask;
        public bool rebuildColliders;
    }

    private struct PendingColliderBuild
    {
        public Vector2Int coord;
        public int expectedGen;
        public int subchunkIndex;
    }

    private const int FastRebuildChunkVoxelCount = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;

    [BurstCompile]
    private struct FastRebuildPopulateBlocksJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> snapshotVoxelData;
        [ReadOnly] public NativeArray<byte> snapshotLoadedChunks;

        public NativeArray<byte> blockTypes;

        public int borderSize;
        public int voxelSizeX;
        public int voxelPlaneSize;
        public int snapshotChunkRadius;
        public int snapshotChunkDiameter;

        public void Execute(int index)
        {
            int x = index % voxelSizeX;
            int temp = index / voxelSizeX;
            int y = temp % Chunk.SizeY;
            int z = temp / Chunk.SizeY;

            int relX = x - borderSize;
            int relZ = z - borderSize;

            int chunkOffsetX = FloorDiv(relX, Chunk.SizeX);
            int chunkOffsetZ = FloorDiv(relZ, Chunk.SizeZ);
            int localX = relX - chunkOffsetX * Chunk.SizeX;
            int localZ = relZ - chunkOffsetZ * Chunk.SizeZ;

            int slotX = chunkOffsetX + snapshotChunkRadius;
            int slotZ = chunkOffsetZ + snapshotChunkRadius;
            if (slotX < 0 || slotX >= snapshotChunkDiameter || slotZ < 0 || slotZ >= snapshotChunkDiameter)
            {
                blockTypes[index] = y <= 2 ? (byte)BlockType.Bedrock : (byte)BlockType.Air;
                return;
            }

            int slot = slotX + slotZ * snapshotChunkDiameter;
            if (snapshotLoadedChunks[slot] == 0)
            {
                blockTypes[index] = y <= 2 ? (byte)BlockType.Bedrock : (byte)BlockType.Air;
                return;
            }

            int srcIndex = slot * FastRebuildChunkVoxelCount + localX + localZ * Chunk.SizeX + y * Chunk.SizeX * Chunk.SizeZ;
            blockTypes[index] = snapshotVoxelData[srcIndex];
        }

        private static int FloorDiv(int value, int divisor)
        {
            if (value >= 0)
                return value / divisor;

            return -((-value + divisor - 1) / divisor);
        }
    }

    [BurstCompile]
    private struct FastRebuildApplyBlockOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;

        public NativeArray<byte> blockTypes;

        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int index = 0; index < overrides.Length; index++)
            {
                BlockEdit edit = overrides[index];
                if (edit.y < 0 || edit.y >= Chunk.SizeY)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                blockTypes[dstIndex] = (byte)math.clamp(edit.type, 0, byte.MaxValue);
            }
        }
    }

    [BurstCompile]
    private struct FastRebuildDerivedDataJob : IJob
    {
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;

        public NativeArray<bool> solids;
        public NativeArray<int> heightCache;
        public NativeArray<bool> subchunkNonEmpty;

        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
                subchunkNonEmpty[s] = false;

            for (int iz = 0; iz < voxelSizeZ; iz++)
            {
                int relZ = iz - borderSize;
                for (int ix = 0; ix < voxelSizeX; ix++)
                {
                    int relX = ix - borderSize;
                    int highestSolidY = 0;

                    for (int y = 0; y < Chunk.SizeY; y++)
                    {
                        int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                        BlockType blockType = (BlockType)blockTypes[idx];
                        bool isSolid = blockMappings[(int)blockType].isSolid;
                        solids[idx] = isSolid;
                        if (isSolid)
                            highestSolidY = y;

                        if (relX >= 0 && relX < Chunk.SizeX &&
                            relZ >= 0 && relZ < Chunk.SizeZ &&
                            blockType != BlockType.Air)
                        {
                            int subIdx = y / Chunk.SubchunkHeight;
                            if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                                subchunkNonEmpty[subIdx] = true;
                        }
                    }

                    heightCache[ix + iz * voxelSizeX] = highestSolidY;
                }
            }
        }
    }

    [BurstCompile]
    private struct FastRebuildPopulateOpacityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> snapshotVoxelData;
        [ReadOnly] public NativeArray<byte> snapshotLoadedChunks;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;

        public NativeArray<byte> opacity;

        public int borderSize;
        public int voxelSizeX;
        public int snapshotChunkRadius;
        public int snapshotChunkDiameter;

        public void Execute(int index)
        {
            int x = index % voxelSizeX;
            int temp = index / voxelSizeX;
            int y = temp % Chunk.SizeY;
            int z = temp / Chunk.SizeY;

            int relX = x - borderSize;
            int relZ = z - borderSize;

            int chunkOffsetX = FloorDiv(relX, Chunk.SizeX);
            int chunkOffsetZ = FloorDiv(relZ, Chunk.SizeZ);
            int localX = relX - chunkOffsetX * Chunk.SizeX;
            int localZ = relZ - chunkOffsetZ * Chunk.SizeZ;

            int slotX = chunkOffsetX + snapshotChunkRadius;
            int slotZ = chunkOffsetZ + snapshotChunkRadius;
            if (slotX < 0 || slotX >= snapshotChunkDiameter || slotZ < 0 || slotZ >= snapshotChunkDiameter)
            {
                opacity[index] = effectiveOpacityByBlock[(int)(y <= 2 ? BlockType.Bedrock : BlockType.Air)];
                return;
            }

            int slot = slotX + slotZ * snapshotChunkDiameter;
            if (snapshotLoadedChunks[slot] == 0)
            {
                opacity[index] = effectiveOpacityByBlock[(int)(y <= 2 ? BlockType.Bedrock : BlockType.Air)];
                return;
            }

            int srcIndex = slot * FastRebuildChunkVoxelCount + localX + localZ * Chunk.SizeX + y * Chunk.SizeX * Chunk.SizeZ;
            opacity[index] = effectiveOpacityByBlock[snapshotVoxelData[srcIndex]];
        }

        private static int FloorDiv(int value, int divisor)
        {
            if (value >= 0)
                return value / divisor;

            return -((-value + divisor - 1) / divisor);
        }
    }

    [BurstCompile]
    private struct FastRebuildApplyOpacityOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;

        public NativeArray<byte> opacity;

        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int index = 0; index < overrides.Length; index++)
            {
                BlockEdit edit = overrides[index];
                if (edit.y < 0 || edit.y >= Chunk.SizeY)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                opacity[dstIndex] = effectiveOpacityByBlock[edit.type];
            }
        }
    }

    #endregion

    #region Unity Callbacks

    private void OnValidate()
    {
        ApplyTerrainLayerProfileIfAssigned();
        EnsureTerrainLayerArraysInitialized();
        EnsureDefaultWarpLayersConfigured();
        MarkBiomeCachesDirty();
        InvalidateNativeGenerationCaches();
    }

    private void Start()
    {
        if (blockData != null) blockData.InitializeDictionary();
        ApplyTerrainLayerProfileIfAssigned();
        EnsureTerrainLayerArraysInitialized();
        EnsureDefaultWarpLayersConfigured();

        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        InitializeBiomeNoiseOffsets();
        InitializeNoiseLayers();
        InitializeWarpLayers();
        InvalidateNativeGenerationCaches();

        // Pre-instantiate pool
        for (int i = 0; i < poolSize; i++)
        {
            Chunk chunk = CreateChunkPoolEntry();
            chunkPool.Enqueue(chunk);
        }

        lastEnableBlockColliders = enableBlockColliders;
        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
    }

    private void OnDestroy()
    {
        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            PendingData pd = pendingDataJobs[i];
            pd.handle.Complete();
            DisposeDataJobResources(ref pd);
        }
        pendingDataJobs.Clear();

        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pm = pendingMeshes[i];
            pm.handle.Complete();
            DisposePendingMesh(pm);
        }
        pendingMeshes.Clear();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk != null && chunk.jobScheduled && !chunk.currentJob.IsCompleted)
                chunk.currentJob.Complete();
        }

        DisposeNativeGenerationCaches();
    }

    private void Update()
    {
        HandleBlockColliderToggle();
        HandleVisualFeatureToggle();
        meshesAppliedThisFrame = 0;
        ProcessQueuedWaterUpdates();
        ProcessQueuedChunkRebuilds();
        ProcessQueuedHighBuildMeshRebuilds();
        ProcessQueuedLeafDecay();
        UpdateChunks();
        RefreshSimulationDistanceStateIfNeeded();
        ProcessPendingColliderBuilds();
        ProcessChunkQueue();
        UpdateSectionOcclusionVisibility();
    }

    #endregion

    #region Initialization Helpers

    private void ApplyTerrainLayerProfileIfAssigned()
    {
        if (terrainLayerProfile == null)
            return;

        noiseLayers = terrainLayerProfile.CloneNoiseLayers();
        warpLayers = terrainLayerProfile.CloneWarpLayers();
    }

    private void EnsureTerrainLayerArraysInitialized()
    {
        if (noiseLayers == null)
            noiseLayers = Array.Empty<NoiseLayer>();

        if (warpLayers == null)
            warpLayers = Array.Empty<WarpLayer>();
    }

    private void EnsureDefaultWarpLayersConfigured()
    {
        if (warpLayers != null && warpLayers.Length > 0)
            return;

        warpLayers = new[]
        {
            new WarpLayer
            {
                enabled = true,
                scale = 960f,
                amplitude = 32f,
                octaves = 2,
                persistence = 0.5f,
                lacunarity = 2f,
                offset = Vector2.zero,
                maxAmp = 0f
            },
            new WarpLayer
            {
                enabled = true,
                scale = 280f,
                amplitude = 14f,
                octaves = 3,
                persistence = 0.55f,
                lacunarity = 2.15f,
                offset = new Vector2(173.4f, -91.2f),
                maxAmp = 0f
            }
        };
    }

    private void InitializeNoiseLayers()
    {
        if (noiseLayers == null) return;

        for (int i = 0; i < noiseLayers.Length; i++)
        {
            NoiseLayer layer = noiseLayers[i];
            if (!layer.enabled) continue;

            if (layer.scale <= 0f) layer.scale = 45f + i * 10f;
            if (layer.amplitude <= 0f) layer.amplitude = math.pow(0.55f, i);
            if (layer.octaves <= 0) layer.octaves = 3 + i;
            if (layer.lacunarity <= 0f) layer.lacunarity = 2.2f;
            if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.55f;
            if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1.1f + i * 0.05f;
            if (layer.exponent == 0f) layer.exponent = 1.1f;
            if (layer.ridgeFactor <= 0f) layer.ridgeFactor = 1f + i * 0.2f;

            if (layer.offset == Vector2.zero)
                layer.offset = new Vector2(offsetX + i * 13.37f, offsetZ + i * 7.53f);
            else
                layer.offset += new Vector2(offsetX, offsetZ);

            float amp = 1f;
            layer.maxAmp = 0f;
            for (int o = 0; o < layer.octaves; o++)
            {
                layer.maxAmp += amp;
                amp *= layer.persistence;
            }
            if (layer.maxAmp <= 0f) layer.maxAmp = 1f;

            noiseLayers[i] = layer;
        }
    }

    private void InitializeWarpLayers()
    {
        if (warpLayers == null) return;

        for (int i = 0; i < warpLayers.Length; i++)
        {
            WarpLayer layer = warpLayers[i];
            if (!layer.enabled) continue;
            if (layer.scale <= 0f) layer.scale = 300f + i * 200f;
            if (layer.amplitude <= 0f) layer.amplitude = 28f;

            if (layer.octaves <= 0) layer.octaves = 1;
            if (layer.lacunarity <= 0f) layer.lacunarity = 2f;
            if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.5f;

            if (layer.offset == Vector2.zero)
                layer.offset = new Vector2(offsetX + i * 23.45f, offsetZ + i * 11.89f);
            else
                layer.offset += new Vector2(offsetX, offsetZ);

            float amp = 1f;
            layer.maxAmp = 0f;
            for (int o = 0; o < layer.octaves; o++)
            {
                layer.maxAmp += amp;
                amp *= layer.persistence;
            }
            if (layer.maxAmp <= 0f) layer.maxAmp = 1f;

            warpLayers[i] = layer;
        }
    }

    #endregion

    #region Chunk Queue & Processing

    private void ProcessChunkQueue()
    {
        float frameStartTime = Time.realtimeSinceStartup;
        float budgetSeconds = frameTimeBudgetMS / 1000f;
        PrioritizePendingJobsByDistance();

        int dataProcessedThisFrame = 0;
        int dataCompletionsLimit = Mathf.Max(1, maxDataCompletionsPerFrame);

        // === STAGE 1: DATA JOBS (voxel generation) ===
        int i = 0;
        while (i < pendingDataJobs.Count)
        {
            if (Time.realtimeSinceStartup - frameStartTime > budgetSeconds) break;
            if (dataProcessedThisFrame >= dataCompletionsLimit) break;

            var pd = pendingDataJobs[i];
            if (!pd.handle.IsCompleted)
            {
                i++;
                continue;
            }

            // Complete and process
            pd.handle.Complete();
            dataProcessedThisFrame++;

            DisposeCompletedDataJobInputs(ref pd);
            pendingDataJobs[i] = pd;

            bool hasActiveChunk = activeChunks.TryGetValue(pd.coord, out Chunk activeChunk);
            bool isLatestGeneration = hasActiveChunk && activeChunk.generation == pd.expectedGen;
            bool hasNewerRebuildQueued = HasQueuedChunkRebuild(pd.coord);

            if (isLatestGeneration)
            {
                if (hasNewerRebuildQueued)
                {
                    DisposeDataJobResources(ref pd);
                }
                else
                {
                    if (!activeChunk.HasInitializedSubchunks)
                        activeChunk.InitializeSubchunks(Material);
                    else
                        activeChunk.UpdateWorldBounds();
                    ApplyChunkBiomeTint(activeChunk, pd.coord);
                    activeChunk.hasVoxelData = true;
                    activeChunk.state = Chunk.ChunkState.MeshReady;

                    ApplyCurrentBlockOverridesToChunkData(
                        pd.coord,
                        pd.blockTypes,
                        pd.solids,
                        pd.subchunkNonEmpty,
                        pd.heightCache,
                        pd.borderSize,
                        activeChunk.voxelData);
                    activeChunk.hasVoxelSnapshot = true;

                    ScheduleSubchunkMeshJobs(pd, activeChunk);
                }
            }
            else
            {
                DisposeDataJobResources(ref pd);
            }

            RemovePendingDataJobAtSwapBack(i);
            if (hasActiveChunk)
                RefreshChunkJobTracking(pd.coord, activeChunk);
        }

        // === STAGE 2: MESH JOBS (apply to GPU) ===
        i = 0;
        while (i < pendingMeshes.Count)
        {
            if (Time.realtimeSinceStartup - frameStartTime > budgetSeconds) break;
            if (meshesAppliedThisFrame >= maxMeshAppliesPerFrame) break;

            var pm = pendingMeshes[i];
            if (!pm.handle.IsCompleted)
            {
                i++;
                continue;
            }

            pm.handle.Complete();

            bool hasActiveChunk = activeChunks.TryGetValue(pm.coord, out Chunk activeChunk);
            bool canApplyMesh = hasActiveChunk &&
                                activeChunk.generation == pm.expectedGen &&
                                !HasQueuedChunkRebuild(pm.coord);

            if (canApplyMesh)
            {
                bool chunkProvidedBorderCoverBeforeApply = DoesChunkCurrentlyProvideBorderCover(pm.coord);
                bool updatedSectionVisibility = false;
                while (pm.nextSubchunkApplyIndex < Chunk.SubchunksPerColumn &&
                       meshesAppliedThisFrame < maxMeshAppliesPerFrame)
                {
                    int subchunkIndex = pm.nextSubchunkApplyIndex++;
                    if ((pm.dirtySubchunkMask & (1 << subchunkIndex)) == 0)
                        continue;

                    Subchunk sub = activeChunk.subchunks[subchunkIndex];
                    MeshGenerator.SubchunkMeshRange range = pm.subchunkRanges[subchunkIndex];
                    if (activeChunk.SetSubchunkVisibilityData(subchunkIndex, pm.subchunkVisibilityMasks[subchunkIndex]))
                        updatedSectionVisibility = true;
                    bool hasSolidColliderGeometry = range.opaqueCount > 0 || range.transparentCount > 0;
                    int startY = subchunkIndex * Chunk.SubchunkHeight;
                    int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);

                    if (range.vertexCount > 0)
                    {
                        sub.ApplyMeshData(pm.vertices, pm.opaqueTriangles, pm.transparentTriangles, pm.billboardTriangles,
                                          pm.waterTriangles, pm.uvs, pm.uv2, pm.normals, pm.extraUVs,
                                          range, startY, endY);
                        ApplyCachedSectionVisibility(pm.coord, subchunkIndex, sub);

                        if (pm.buildColliders)
                        {
                            if (hasSolidColliderGeometry && IsChunkInsideSimulationDistance(pm.coord))
                                EnqueueColliderBuild(pm.coord, pm.expectedGen, subchunkIndex);
                            else
                                sub.ClearColliderData();
                        }
                    }

                    else
                    {
                        sub.ClearMesh();
                    }

                    meshesAppliedThisFrame++;
                }

                if (pm.nextSubchunkApplyIndex < Chunk.SubchunksPerColumn)
                {
                    pendingMeshes[i] = pm;
                    i++;
                    continue;
                }

                activeChunk.state = Chunk.ChunkState.Active;
                if (!chunkProvidedBorderCoverBeforeApply)
                    RequestHorizontalNeighborVisualRefresh(pm.coord);

                if (updatedSectionVisibility)
                    InvalidateSectionOcclusionGraph();
            }

            DisposePendingMesh(pm);
            RemovePendingMeshAtSwapBack(i);
            if (hasActiveChunk)
                RefreshChunkJobTracking(pm.coord, activeChunk);
        }
    }

    #endregion

    #region Voxel Data Copy & Mesh Scheduling

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache)
    {
        ApplyCurrentBlockOverridesToChunkData(
            coord,
            blockTypes,
            solids,
            subchunkNonEmpty,
            heightCache,
            InferBorderSizeFromChunkArrays(blockTypes, heightCache),
            default);
    }

    private Chunk CreateChunkPoolEntry()
    {
        GameObject obj = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform);
        obj.SetActive(false);

        Chunk chunk = obj.GetComponent<Chunk>();
        if (chunk != null && prewarmPooledChunkVisuals)
        {
            chunk.InitializeSubchunks(Material);
            chunk.ResetChunk();
        }

        return chunk;
    }

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize,
        NativeArray<byte> voxelSnapshot)
    {
        if (blockOverrides.Count == 0 ||
            !blockTypes.IsCreated ||
            !solids.IsCreated ||
            !subchunkNonEmpty.IsCreated ||
            !heightCache.IsCreated ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0)
        {
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        if (relevantTerrainOverridePositions.Count == 0)
            return;

        bool hasRelevantOverrides = false;
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            if (idx < 0 || idx >= blockTypes.Length)
                continue;

            blockTypes[idx] = (byte)overrideType;
            UpdateVoxelSnapshotCell(voxelSnapshot, chunkMinX, chunkMinZ, worldPos, overrideType);
            hasRelevantOverrides = true;
        }

        if (!hasRelevantOverrides)
            return;

        RefreshChunkDerivedData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize);
    }

    private static void UpdateVoxelSnapshotCell(
        NativeArray<byte> voxelSnapshot,
        int chunkMinX,
        int chunkMinZ,
        Vector3Int worldPos,
        BlockType blockType)
    {
        if (!voxelSnapshot.IsCreated)
            return;

        int localX = worldPos.x - chunkMinX;
        int localZ = worldPos.z - chunkMinZ;
        int localY = worldPos.y;
        if (localX < 0 || localX >= Chunk.SizeX ||
            localZ < 0 || localZ >= Chunk.SizeZ ||
            localY < 0 || localY >= Chunk.SizeY)
        {
            return;
        }

        int snapshotIndex = localX + localZ * Chunk.SizeX + localY * Chunk.SizeX * Chunk.SizeZ;
        if (snapshotIndex < 0 || snapshotIndex >= voxelSnapshot.Length)
            return;

        voxelSnapshot[snapshotIndex] = (byte)blockType;
    }

    private static int InferBorderSizeFromChunkArrays(NativeArray<byte> blockTypes, NativeArray<int> heightCache)
    {
        if (heightCache.IsCreated && heightCache.Length > 0)
        {
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(heightCache.Length));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        if (blockTypes.IsCreated && blockTypes.Length > 0 && Chunk.SizeY > 0)
        {
            int paddedArea = blockTypes.Length / Chunk.SizeY;
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(paddedArea));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        return 0;
    }

    private void RefreshChunkDerivedData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int heightStride = voxelSizeX;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
            subchunkNonEmpty[s] = false;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    BlockType bt = (BlockType)blockTypes[idx];
                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid)
                        highestSolidY = y;

                    if (worldX >= chunkMinX &&
                        worldX < chunkMinX + Chunk.SizeX &&
                        worldZ >= chunkMinZ &&
                        worldZ < chunkMinZ + Chunk.SizeZ &&
                        bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }
    }

    private void ScheduleSubchunkMeshJobs(PendingData pd, Chunk activeChunk)
    {
        int borderSize = Mathf.Max(1, pd.borderSize);
        int dirtySubchunkMask = SanitizeDirtySubchunkMask(pd.dirtySubchunkMask);
        SealMissingHorizontalChunkBorders(pd.coord, pd.blockTypes, pd.solids, borderSize);
        List<int3> suppressedBillboardsForChunk = GetSuppressedGrassBillboardsForChunk(pd.coord);
        NativeArray<int3> nativeSuppressedBillboards = new NativeArray<int3>(suppressedBillboardsForChunk.Count, Allocator.Persistent);
        for (int s = 0; s < suppressedBillboardsForChunk.Count; s++)
            nativeSuppressedBillboards[s] = suppressedBillboardsForChunk[s];

        int meshSubchunkMask = 0;
        bool updatedEmptySectionVisibility = false;
        for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
        {
            if ((dirtySubchunkMask & (1 << sub)) == 0)
                continue;

            if (!pd.subchunkNonEmpty[sub])
            {
                activeChunk.subchunks[sub].ClearMesh();
                if (activeChunk.SetSubchunkVisibilityData(sub, SubchunkOcclusion.AllVisibleMask))
                    updatedEmptySectionVisibility = true;
                continue;
            }

            meshSubchunkMask |= 1 << sub;
        }

        if (updatedEmptySectionVisibility)
            InvalidateSectionOcclusionGraph();

        JobHandle combinedMeshHandle = default;
        if (meshSubchunkMask != 0)
        {
            float effectiveAoStrength = enableAmbientOcclusion ? aoStrength : 0f;

            MeshGenerator.ScheduleMeshJob(
                pd.heightCache, pd.blockTypes, pd.solids, pd.light, cachedNativeBlockMappings, nativeSuppressedBillboards,
                pd.subchunkNonEmpty,
                atlasTilesX, atlasTilesY, true, borderSize,
                pd.coord.x, pd.coord.y,
                meshSubchunkMask,
                enableGrassBillboards, grassBillboardChance, grassBillboardBlockType, grassBillboardHeight,
                grassBillboardNoiseScale, grassBillboardJitter,
                effectiveAoStrength, aoCurveExponent, aoMinLight, useFastBedrockStyleMeshing,
                out JobHandle meshHandle,
                out NativeList<Vector3> vertices,
                out NativeList<int> opaqueTriangles,
                out NativeList<int> transparentTriangles,
                out NativeList<int> billboardTriangles,
                out NativeList<int> waterTriangles,
                out NativeList<Vector2> uvs,
                out NativeList<Vector2> uv2,
                out NativeList<Vector3> normals,
                out NativeList<Vector4> extraUVs,
                out NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
                out NativeArray<ulong> subchunkVisibilityMasks
            );

            combinedMeshHandle = meshHandle;
            pendingMeshes.Add(new PendingMesh
            {
                handle = meshHandle,
                vertices = vertices,
                opaqueTriangles = opaqueTriangles,
                transparentTriangles = transparentTriangles,
                billboardTriangles = billboardTriangles,
                waterTriangles = waterTriangles,
                uvs = uvs,
                uv2 = uv2,
                normals = normals,
                extraUVs = extraUVs,
                coord = pd.coord,
                expectedGen = pd.expectedGen,
                parentChunk = activeChunk,
                subchunkRanges = subchunkRanges,
                subchunkVisibilityMasks = subchunkVisibilityMasks,
                dirtySubchunkMask = meshSubchunkMask,
                nextSubchunkApplyIndex = 0,
                heightCache = pd.heightCache,
                blockTypes = pd.blockTypes,
                solids = pd.solids,
                light = pd.light,
                suppressedBillboards = default,
                buildColliders = pd.rebuildColliders
            });
        }

        var disposeJob = new MeshGenerator.DisposeChunkDataJob
        {
            heightCache = pd.heightCache,
            blockTypes = pd.blockTypes,
            solids = pd.solids,
            light = pd.light,
            subchunkNonEmpty = pd.subchunkNonEmpty
        };
        JobHandle dataDisposeHandle = disposeJob.Schedule(combinedMeshHandle);

        var disposeSuppressedBillboardsJob = new MeshGenerator.DisposeSuppressedBillboardsJob
        {
            suppressedGrassBillboards = nativeSuppressedBillboards
        };
        JobHandle suppressedDisposeHandle = disposeSuppressedBillboardsJob.Schedule(combinedMeshHandle);

        activeChunk.currentJob = JobHandle.CombineDependencies(combinedMeshHandle, dataDisposeHandle, suppressedDisposeHandle);
    }

    private void SealMissingHorizontalChunkBorders(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int borderSize)
    {
        if (!blockTypes.IsCreated || !solids.IsCreated || borderSize <= 0)
            return;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int westCurrentX = borderSize;
        int eastCurrentX = borderSize + Chunk.SizeX - 1;
        int northCurrentZ = borderSize;
        int southCurrentZ = borderSize + Chunk.SizeZ - 1;
        int westNeighborX = borderSize - 1;
        int eastNeighborX = borderSize + Chunk.SizeX;
        int northNeighborZ = borderSize - 1;
        int southNeighborZ = borderSize + Chunk.SizeZ;

        if (!DoesChunkCurrentlyProvideBorderCover(coord + Vector2Int.left))
            ClearBorderPaddingX(westCurrentX, westNeighborX, borderSize, voxelSizeX, voxelPlaneSize, blockTypes, solids);

        if (!DoesChunkCurrentlyProvideBorderCover(coord + Vector2Int.right))
            ClearBorderPaddingX(eastCurrentX, eastNeighborX, borderSize, voxelSizeX, voxelPlaneSize, blockTypes, solids);

        if (!DoesChunkCurrentlyProvideBorderCover(coord + Vector2Int.down))
            ClearBorderPaddingZ(northCurrentZ, northNeighborZ, borderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize, blockTypes, solids);

        if (!DoesChunkCurrentlyProvideBorderCover(coord + Vector2Int.up))
            ClearBorderPaddingZ(southCurrentZ, southNeighborZ, borderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize, blockTypes, solids);
    }

    private bool DoesChunkCurrentlyProvideBorderCover(Vector2Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return false;

        if (chunk.state == Chunk.ChunkState.Active)
            return true;

        if (chunk.state == Chunk.ChunkState.Inactive || chunk.subchunks == null)
            return false;

        for (int i = 0; i < chunk.subchunks.Length; i++)
        {
            Subchunk subchunk = chunk.subchunks[i];
            if (subchunk != null && subchunk.hasGeometry)
                return true;
        }

        return false;
    }

    private static void ClearBorderPaddingX(
        int currentLocalX,
        int paddingLocalX,
        int borderSize,
        int voxelSizeX,
        int voxelPlaneSize,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids)
    {
        if (currentLocalX < 0 || currentLocalX >= voxelSizeX || paddingLocalX < 0 || paddingLocalX >= voxelSizeX)
            return;

        for (int z = borderSize; z < borderSize + Chunk.SizeZ; z++)
        {
            int currentIdx = currentLocalX + z * voxelPlaneSize;
            int paddingIdx = paddingLocalX + z * voxelPlaneSize;
            for (int y = 0; y < Chunk.SizeY; y++, currentIdx += voxelSizeX, paddingIdx += voxelSizeX)
            {
                if (!ShouldSealMissingHorizontalBorderForBlock((BlockType)blockTypes[currentIdx]))
                    continue;

                blockTypes[paddingIdx] = (byte)BlockType.Air;
                solids[paddingIdx] = false;
            }
        }
    }

    private static void ClearBorderPaddingZ(
        int currentLocalZ,
        int paddingLocalZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids)
    {
        if (currentLocalZ < 0 || currentLocalZ >= voxelSizeZ || paddingLocalZ < 0 || paddingLocalZ >= voxelSizeZ)
            return;

        int currentSliceStart = currentLocalZ * voxelPlaneSize;
        int paddingSliceStart = paddingLocalZ * voxelPlaneSize;
        for (int x = borderSize; x < borderSize + Chunk.SizeX; x++)
        {
            int currentIdx = currentSliceStart + x;
            int paddingIdx = paddingSliceStart + x;
            for (int y = 0; y < Chunk.SizeY; y++, currentIdx += voxelSizeX, paddingIdx += voxelSizeX)
            {
                if (!ShouldSealMissingHorizontalBorderForBlock((BlockType)blockTypes[currentIdx]))
                    continue;

                blockTypes[paddingIdx] = (byte)BlockType.Air;
                solids[paddingIdx] = false;
            }
        }
    }

    private static bool ShouldSealMissingHorizontalBorderForBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        return blockType != BlockType.Leaves &&
               blockType != BlockType.glass &&
               !TorchPlacementUtility.IsTorchLike(blockType);
    }

    private void RequestHorizontalNeighborVisualRefresh(Vector2Int coord)
    {
        int dirtySubchunkMask = GetFullSubchunkMask();
        RequestHorizontalNeighborVisualRefresh(coord + Vector2Int.left, dirtySubchunkMask);
        RequestHorizontalNeighborVisualRefresh(coord + Vector2Int.right, dirtySubchunkMask);
        RequestHorizontalNeighborVisualRefresh(coord + Vector2Int.down, dirtySubchunkMask);
        RequestHorizontalNeighborVisualRefresh(coord + Vector2Int.up, dirtySubchunkMask);
    }

    private void RequestHorizontalNeighborVisualRefresh(Vector2Int coord, int dirtySubchunkMask)
    {
        if (!DoesChunkCurrentlyProvideBorderCover(coord))
            return;

        RequestChunkRebuild(coord, dirtySubchunkMask, false);
    }

    private List<int3> GetSuppressedGrassBillboardsForChunk(Vector2Int chunkCoord)
    {
        if (!suppressedGrassBillboardsByChunk.TryGetValue(chunkCoord, out HashSet<Vector3Int> positions) || positions.Count == 0)
            return new List<int3>(0);

        List<int3> result = new List<int3>(positions.Count);
        foreach (Vector3Int pos in positions)
        {
            if (pos.y >= 0 && pos.y < Chunk.SizeY)
                result.Add(new int3(pos.x, pos.y, pos.z));
        }

        return result;
    }

    private static Vector2Int GetChunkCoordFromWorldXZ(int worldX, int worldZ)
    {
        return new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );
    }

    private void IndexSuppressedGrassBillboard(Vector3Int pos)
    {
        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (!suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set = new HashSet<Vector3Int>();
            suppressedGrassBillboardsByChunk[coord] = set;
        }

        set.Add(pos);
    }

    private bool RemoveSuppressedGrassBillboard(Vector3Int pos)
    {
        if (!suppressedGrassBillboards.Remove(pos))
            return false;

        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set.Remove(pos);
            if (set.Count == 0)
                suppressedGrassBillboardsByChunk.Remove(coord);
        }

        return true;
    }

    private void InjectGlobalLightColumns(
        NativeArray<byte> chunkLightData,
        int chunkMinX,
        int chunkMinZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (globalLightColumns.Count == 0) return;

        int minWX = chunkMinX - borderSize;
        int maxWX = chunkMinX + Chunk.SizeX + borderSize - 1;
        int minWZ = chunkMinZ - borderSize;
        int maxWZ = chunkMinZ + Chunk.SizeZ + borderSize - 1;

        int areaColumns = voxelSizeX * voxelSizeZ;

        // Sparse mode: iterate only existing global columns (better when emissive lights are sparse).
        if (globalLightColumns.Count < areaColumns)
        {
            foreach (var kv in globalLightColumns)
            {
                int wx = kv.Key.x;
                int wz = kv.Key.y;
                if (wx < minWX || wx > maxWX || wz < minWZ || wz > maxWZ) continue;

                byte[] column = kv.Value;
                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
            return;
        }

        // Dense mode: bounded grid lookup (better when many lit columns exist).
        for (int wx = minWX; wx <= maxWX; wx++)
        {
            for (int wz = minWZ; wz <= maxWZ; wz++)
            {
                var key = new Vector2Int(wx, wz);
                if (!globalLightColumns.TryGetValue(key, out byte[] column)) continue;

                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
        }
    }

    #endregion

    #region Requesting Chunks & Rebuilds

    private void UpdateChunks()
    {
        if (player == null) return;

        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();

        if (currentChunkCoord != _lastChunkCoord)
        {
            _lastChunkCoord = currentChunkCoord;
            _tempNeededCoords.Clear();
            bool activeSectionSetChanged = false;

            for (int x = -renderDistance; x <= renderDistance; x++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector2Int coord = new Vector2Int(currentChunkCoord.x + x, currentChunkCoord.y + z);
                    if (!IsCoordInsideRenderDistance(coord, currentChunkCoord))
                        continue;

                    _tempNeededCoords.Add(coord);
                }
            }

            // A. Remover chunks distantes
            _tempToRemove.Clear();
            foreach (var kv in activeChunks)
            {
                if (!_tempNeededCoords.Contains(kv.Key))
                    _tempToRemove.Add(kv.Key);
            }

            for (int i = 0; i < _tempToRemove.Count; i++)
            {
                Vector2Int coord = _tempToRemove[i];
                if (activeChunks.TryGetValue(coord, out Chunk chunk))
                {
                    chunk.ResetChunk();
                    chunkPool.Enqueue(chunk);
                    activeChunks.Remove(coord);
                    RequestHorizontalNeighborVisualRefresh(coord);
                    activeSectionSetChanged = true;

                    RemoveHighBuildMesh(coord);
                }
            }

            if (activeSectionSetChanged)
                InvalidateSectionOcclusionGraph();

            // B. Limpar pendentes desnecessÃ¡rios
            for (int i = pendingChunks.Count - 1; i >= 0; i--)
            {
                if (!_tempNeededCoords.Contains(pendingChunks[i].coord))
                    pendingChunks.RemoveAt(i);
            }

            // C. Encontrar novos chunks para gerar
            foreach (Vector2Int coord in _tempNeededCoords)
            {
                if (activeChunks.ContainsKey(coord)) continue;
                if (IsChunkJobPending(coord)) continue;

                float distSq = GetChunkDistanceSqToPlayer(coord);
                pendingChunks.Add((coord, distSq));
            }

            // D. Reordenar fila por distÃ¢ncia
            RefreshPendingChunkPriorities();
        }

        if (pendingChunks.Count > 1)
            RefreshPendingChunkPriorities();

        // Processa alguns itens da fila por frame
        if (pendingChunks.Count > 0)
        {
            int processed = 0;
            int subchunksPerChunk = Mathf.Max(1, Chunk.SubchunksPerColumn);
            int pendingDataInRange = CountPendingDataJobsInRenderDistance(currentChunkCoord);
            int pendingMeshesInRange = CountPendingMeshesInRenderDistance(currentChunkCoord);
            int realPending = pendingDataInRange + Mathf.CeilToInt(pendingMeshesInRange / (float)subchunksPerChunk);
            int hardPendingDataLimit = Mathf.Max(maxPendingDataJobs, maxPendingDataJobs * 3);
            bool jobsCongested = realPending > maxChunksPerFrame * 4;

            if (!jobsCongested)
            {
                while (processed < maxChunksPerFrame && pendingChunks.Count > 0)
                {
                    if (pendingDataInRange >= maxPendingDataJobs || pendingDataJobs.Count >= hardPendingDataLimit)
                        break;

                    var item = pendingChunks[0];
                    pendingChunks.RemoveAt(0);

                    if (!activeChunks.ContainsKey(item.coord))
                    {
                        RequestChunk(item.coord);
                        pendingDataInRange++;
                        processed++;
                    }
                }
            }
        }
    }

    private void RequestChunk(Vector2Int coord)
    {
        // Reuse or create chunk
        Chunk chunk;
        if (chunkPool.Count > 0)
        {
            chunk = chunkPool.Dequeue();
            try { chunk.ResetChunk(); } catch { }
            chunk.jobScheduled = false;
            chunk.hasVoxelData = false;
            chunk.currentJob = default;
            chunk.state = Chunk.ChunkState.Inactive;
        }
        else
        {
            chunk = CreateChunkPoolEntry();
        }

        Vector3 pos = new Vector3(coord.x * Chunk.SizeX, 0, coord.y * Chunk.SizeZ);
        chunk.transform.position = pos;
        chunk.UpdateWorldBounds(); // garante bounds atualizado
        chunk.SetCoord(coord);

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        if (!chunk.voxelData.IsCreated)
        {
            chunk.voxelData = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);
            chunk.hasVoxelData = false;
        }

        activeChunks.Add(coord, chunk);
        RequestHighBuildMeshRebuild(coord);

        // Build edits from blockOverrides
        var editsList = new List<BlockEdit>();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, editsList);

        NativeArray<BlockEdit> nativeEdits;
        if (editsList.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(editsList.Count, Allocator.Persistent);
            for (int i = 0; i < editsList.Count; i++) nativeEdits[i] = editsList[i];
        }
        else
        {
            nativeEdits = new NativeArray<BlockEdit>(0, Allocator.Persistent);
        }

        int treeMargin = GetMaxTreeMarginForGeneration();
        int detailBorderSize = GetDetailedGenerationBorderSize();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        EnsureNativeGenerationCaches();

        // InjeÃ§Ã£o da luz global
        // Light injection corrected for rebuild (uses borderSize)
        int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> chunkLightData = default;
        if (enableVoxelLighting)
        {
            chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.Persistent);
            InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }
        if (chunk.jobScheduled)
        {
            try { chunk.currentJob.Complete(); } catch { }
            chunk.jobScheduled = false;
        }

        // Agendamento do data job
        MeshGenerator.ScheduleDataJob(
            coord, cachedNativeNoiseLayers, cachedNativeWarpLayers, cachedNativeBlockMappings, cachedNativeEffectiveLightOpacityByBlock,
            baseHeight, offsetX, offsetZ, seaLevel,
            GetBiomeNoiseSettings(),
            seed,
            nativeEdits, treeMargin, dataBorderSize, lightBorderSize, detailBorderSize,
            GetMaxTreeRadiusForGeneration(), CliffTreshold, enableTrees,
            cachedNativeOreSettings,
            cachedNativeTreeSpawnRules,
            caveGenerationMode,
            caveWormSettings,
            caveSpaghettiSettings,
            enableVoxelLighting,
            chunkLightData,
            chunk.voxelData,
            out JobHandle dataHandle,
            out NativeArray<int> heightCache,
            out NativeArray<byte> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<byte> light,
            out NativeArray<byte> lightOpacityData,
            out NativeArray<bool> subchunkNonEmpty
        );

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            borderSize = dataBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            lightOpacityData = lightOpacityData,
            edits = nativeEdits,
            fastRebuildSnapshotVoxelData = default,
            fastRebuildSnapshotLoadedChunks = default,
            fastRebuildOverrides = default,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = GetFullSubchunkMask(),
            rebuildColliders = enableBlockColliders
        });

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
        chunk.gameObject.SetActive(true);
    }

    private bool TryScheduleFastChunkRebuild(Vector2Int coord, Chunk chunk, int expectedGen, int dirtySubchunkMask, bool rebuildColliders)
    {
        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return false;
        if (!CanChunkProvideVoxelSnapshot(chunk))
            return false;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return false;

        int copyBorderSize = GetMeshNeighborPadding();
        int lightBorderSize = GetLightSmoothingBorderSize();
        int copyVoxelSizeX = Chunk.SizeX + 2 * copyBorderSize;
        int copyVoxelSizeZ = Chunk.SizeZ + 2 * copyBorderSize;
        int copyVoxelPlaneSize = copyVoxelSizeX * Chunk.SizeY;
        int copyTotalVoxels = copyVoxelSizeX * Chunk.SizeY * copyVoxelSizeZ;
        int copyTotalHeightPoints = copyVoxelSizeX * copyVoxelSizeZ;

        int lightVoxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int lightVoxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int lightVoxelPlaneSize = lightVoxelSizeX * Chunk.SizeY;
        int lightTotalVoxels = lightVoxelSizeX * Chunk.SizeY * lightVoxelSizeZ;

        int maxSnapshotBorder = Mathf.Max(copyBorderSize, lightBorderSize);
        int snapshotChunkRadius = Mathf.Max(1, Mathf.CeilToInt(maxSnapshotBorder / (float)Chunk.SizeX));
        int snapshotChunkDiameter = snapshotChunkRadius * 2 + 1;
        int snapshotChunkCount = snapshotChunkDiameter * snapshotChunkDiameter;

        EnsureNativeGenerationCaches();

        NativeArray<int> heightCache = new NativeArray<int>(copyTotalHeightPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<byte> blockTypes = new NativeArray<byte>(copyTotalVoxels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<bool> solids = new NativeArray<bool>(copyTotalVoxels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<byte> light = new NativeArray<byte>(copyTotalVoxels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<bool> subchunkNonEmpty = new NativeArray<bool>(Chunk.SubchunksPerColumn, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<byte> lightOpacityData = default;
        NativeArray<byte> blockLightData = default;
        NativeArray<byte> snapshotVoxelData = new NativeArray<byte>(snapshotChunkCount * FastRebuildChunkVoxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<byte> snapshotLoadedChunks = new NativeArray<byte>(snapshotChunkCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<BlockEdit> nativeOverrides = BuildFastRebuildOverrideArray(coord, maxSnapshotBorder);

        CaptureFastRebuildSnapshot(coord, snapshotChunkRadius, snapshotVoxelData, snapshotLoadedChunks);

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        if (enableVoxelLighting)
        {
            lightOpacityData = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            blockLightData = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent);
            InjectGlobalLightColumns(blockLightData, chunkMinX, chunkMinZ, lightBorderSize, lightVoxelSizeX, lightVoxelSizeZ, lightVoxelPlaneSize);
        }

        var copyPopulateJob = new FastRebuildPopulateBlocksJob
        {
            snapshotVoxelData = snapshotVoxelData,
            snapshotLoadedChunks = snapshotLoadedChunks,
            blockTypes = blockTypes,
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelPlaneSize = copyVoxelPlaneSize,
            snapshotChunkRadius = snapshotChunkRadius,
            snapshotChunkDiameter = snapshotChunkDiameter
        };
        JobHandle copyPopulateHandle = copyPopulateJob.Schedule(copyTotalVoxels, 128);

        JobHandle copyOverrideHandle = copyPopulateHandle;
        if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
        {
            var copyOverrideJob = new FastRebuildApplyBlockOverridesJob
            {
                overrides = nativeOverrides,
                blockTypes = blockTypes,
                chunkMinX = chunkMinX,
                chunkMinZ = chunkMinZ,
                borderSize = copyBorderSize,
                voxelSizeX = copyVoxelSizeX,
                voxelSizeZ = copyVoxelSizeZ,
                voxelPlaneSize = copyVoxelPlaneSize
            };
            copyOverrideHandle = copyOverrideJob.Schedule(copyPopulateHandle);
        }

        var deriveDataJob = new FastRebuildDerivedDataJob
        {
            blockTypes = blockTypes,
            blockMappings = cachedNativeBlockMappings,
            solids = solids,
            heightCache = heightCache,
            subchunkNonEmpty = subchunkNonEmpty,
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelSizeZ = copyVoxelSizeZ,
            voxelPlaneSize = copyVoxelPlaneSize
        };
        JobHandle deriveDataHandle = deriveDataJob.Schedule(copyOverrideHandle);

        JobHandle visualDataHandle;
        if (enableVoxelLighting)
        {
            var opacityPopulateJob = new FastRebuildPopulateOpacityJob
            {
                snapshotVoxelData = snapshotVoxelData,
                snapshotLoadedChunks = snapshotLoadedChunks,
                effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                opacity = lightOpacityData,
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                snapshotChunkRadius = snapshotChunkRadius,
                snapshotChunkDiameter = snapshotChunkDiameter
            };
            JobHandle opacityPopulateHandle = opacityPopulateJob.Schedule(lightTotalVoxels, 128);

            JobHandle opacityOverrideHandle = opacityPopulateHandle;
            if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
            {
                var opacityOverrideJob = new FastRebuildApplyOpacityOverridesJob
                {
                    overrides = nativeOverrides,
                    effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                    opacity = lightOpacityData,
                    chunkMinX = chunkMinX,
                    chunkMinZ = chunkMinZ,
                    borderSize = lightBorderSize,
                    voxelSizeX = lightVoxelSizeX,
                    voxelSizeZ = lightVoxelSizeZ,
                    voxelPlaneSize = lightVoxelPlaneSize
                };
                opacityOverrideHandle = opacityOverrideJob.Schedule(opacityPopulateHandle);
            }

            var lightJob = new ChunkLighting.CroppedChunkLightingJob
            {
                opacity = lightOpacityData,
                light = light,
                blockLightData = blockLightData,
                inputVoxelSizeX = lightVoxelSizeX,
                inputVoxelSizeZ = lightVoxelSizeZ,
                inputTotalVoxels = lightTotalVoxels,
                inputVoxelPlaneSize = lightVoxelPlaneSize,
                outputVoxelSizeX = copyVoxelSizeX,
                outputVoxelSizeZ = copyVoxelSizeZ,
                outputVoxelPlaneSize = copyVoxelPlaneSize,
                outputOffsetX = lightBorderSize - copyBorderSize,
                outputOffsetZ = lightBorderSize - copyBorderSize,
                SizeY = Chunk.SizeY,
            };

            visualDataHandle = lightJob.Schedule(JobHandle.CombineDependencies(deriveDataHandle, opacityOverrideHandle));
        }
        else
        {
            byte fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            visualDataHandle = deriveDataHandle;
        }

        pendingDataJobs.Add(new PendingData
        {
            handle = visualDataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            borderSize = copyBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = blockLightData,
            lightOpacityData = lightOpacityData,
            edits = default,
            fastRebuildSnapshotVoxelData = snapshotVoxelData,
            fastRebuildSnapshotLoadedChunks = snapshotLoadedChunks,
            fastRebuildOverrides = nativeOverrides,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders
        });

        chunk.currentJob = visualDataHandle;
        chunk.jobScheduled = true;
        chunk.state = Chunk.ChunkState.MeshReady;
        return true;
    }

    private void CaptureFastRebuildSnapshot(
        Vector2Int coord,
        int chunkRadius,
        NativeArray<byte> snapshotVoxelData,
        NativeArray<byte> snapshotLoadedChunks)
    {
        if (!snapshotVoxelData.IsCreated || !snapshotLoadedChunks.IsCreated)
            return;

        int slot = 0;
        for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++, slot++)
            {
                Vector2Int sourceCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                bool isLoaded = activeChunks.TryGetValue(sourceCoord, out Chunk sourceChunk) &&
                                CanChunkProvideVoxelSnapshot(sourceChunk);

                snapshotLoadedChunks[slot] = isLoaded ? (byte)1 : (byte)0;
                if (!isLoaded)
                    continue;

                NativeArray<byte>.Copy(
                    sourceChunk.voxelData,
                    0,
                    snapshotVoxelData,
                    slot * FastRebuildChunkVoxelCount,
                    FastRebuildChunkVoxelCount);
            }
        }
    }

    private NativeArray<BlockEdit> BuildFastRebuildOverrideArray(Vector2Int coord, int borderSize)
    {
        if (blockOverrides.Count == 0)
            return new NativeArray<BlockEdit>(0, Allocator.Persistent);

        List<BlockEdit> editsList = new List<BlockEdit>(32);
        AppendRelevantBlockEdits(coord, borderSize, editsList);
        NativeArray<BlockEdit> nativeOverrides = new NativeArray<BlockEdit>(editsList.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < editsList.Count; i++)
            nativeOverrides[i] = editsList[i];

        return nativeOverrides;
    }

    private void FillFastRebuildArraysFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int heightStride = voxelSizeX;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
            subchunkNonEmpty[s] = false;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = (BlockType)srcChunk.voxelData[srcIdx];
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    blockTypes[idx] = (byte)bt;

                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid) highestSolidY = y;

                    if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }

        ApplyCurrentBlockOverridesToChunkData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize, default);
    }

    private void FillFastRebuildLightOpacityFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (!opacity.IsCreated || blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = (BlockType)srcChunk.voxelData[srcIdx];
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    opacity[idx] = ChunkLighting.GetEffectiveOpacity(mappings[(int)bt]);
                }
            }
        }

        ApplyCurrentBlockOverridesToLightOpacity(coord, borderSize, opacity);
    }

    private void ApplyCurrentBlockOverridesToLightOpacity(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (blockOverrides.Count == 0 ||
            !opacity.IsCreated ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0)
        {
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            opacity[idx] = ChunkLighting.GetEffectiveOpacity(blockData.mappings[(int)overrideType]);
        }
    }

    private bool TryResolveLoadedColumn(int worldX, int worldZ, out Chunk chunk, out int localX, out int localZ)
    {
        Vector2Int coord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(coord, out chunk) && CanChunkProvideVoxelSnapshot(chunk))
        {
            localX = worldX - coord.x * Chunk.SizeX;
            localZ = worldZ - coord.y * Chunk.SizeZ;

            if (localX >= 0 && localX < Chunk.SizeX && localZ >= 0 && localZ < Chunk.SizeZ)
                return true;
        }

        chunk = null;
        localX = 0;
        localZ = 0;
        return false;
    }

    private static int GetFullSubchunkMask()
    {
        return (1 << Chunk.SubchunksPerColumn) - 1;
    }

    private static int SanitizeDirtySubchunkMask(int dirtySubchunkMask)
    {
        return dirtySubchunkMask & GetFullSubchunkMask();
    }

    private static int GetDirtySubchunkMaskForWorldY(int worldY)
    {
        if (worldY < 0 || worldY >= Chunk.SizeY)
            return 0;

        int subchunkIndex = Mathf.Clamp(worldY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
        int subchunkStartY = subchunkIndex * Chunk.SubchunkHeight;
        int subchunkEndY = Mathf.Min(subchunkStartY + Chunk.SubchunkHeight, Chunk.SizeY) - 1;

        int mask = 1 << subchunkIndex;
        if (worldY == subchunkStartY && subchunkIndex > 0)
            mask |= 1 << (subchunkIndex - 1);
        if (worldY == subchunkEndY && subchunkIndex + 1 < Chunk.SubchunksPerColumn)
            mask |= 1 << (subchunkIndex + 1);

        return mask;
    }

    private static void AddDirtySubchunkMask(Dictionary<Vector2Int, int> dirtyMasks, Vector2Int coord, int dirtySubchunkMask)
    {
        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (dirtyMasks.TryGetValue(coord, out int existingMask))
            dirtyMasks[coord] = existingMask | dirtySubchunkMask;
        else
            dirtyMasks[coord] = dirtySubchunkMask;
    }

    private void RequestChunkRebuild(Vector2Int coord)
    {
        RequestChunkRebuild(coord, GetFullSubchunkMask(), true);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask)
    {
        RequestChunkRebuild(coord, dirtySubchunkMask, true);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders)
    {
        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (queuedChunkRebuildMasks.TryGetValue(coord, out int existingMask))
            queuedChunkRebuildMasks[coord] = existingMask | dirtySubchunkMask;
        else
            queuedChunkRebuildMasks[coord] = dirtySubchunkMask;

        if (queuedChunkRebuildRequiresCollider.TryGetValue(coord, out bool existingRequiresCollider))
            queuedChunkRebuildRequiresCollider[coord] = existingRequiresCollider || rebuildColliders;
        else
            queuedChunkRebuildRequiresCollider[coord] = rebuildColliders;

        if (queuedChunkRebuildsSet.Add(coord))
            queuedChunkRebuilds.Enqueue(coord);
    }

    private void ProcessQueuedChunkRebuilds()
    {
        if (queuedChunkRebuilds.Count == 0) return;
        int perFrameLimit = Mathf.Max(1, maxChunkRebuildsPerFrame);

        int processed = 0;
        int attempts = queuedChunkRebuilds.Count;
        while (processed < perFrameLimit && attempts-- > 0)
        {
            Vector2Int coord = queuedChunkRebuilds.Dequeue();
            queuedChunkRebuildsSet.Remove(coord);

            if (!queuedChunkRebuildMasks.TryGetValue(coord, out int dirtySubchunkMask))
            {
                queuedChunkRebuildRequiresCollider.Remove(coord);
                continue;
            }

            queuedChunkRebuildMasks.Remove(coord);
            bool requiresColliderRebuild = queuedChunkRebuildRequiresCollider.TryGetValue(coord, out bool queuedRequiresCollider) && queuedRequiresCollider;
            queuedChunkRebuildRequiresCollider.Remove(coord);

            if (IsChunkJobPending(coord))
            {
                RequestChunkRebuild(coord, dirtySubchunkMask, requiresColliderRebuild);
                continue;
            }

            RequestChunkRebuildImmediate(coord, dirtySubchunkMask, requiresColliderRebuild);
            processed++;
        }
    }

    private void RequestChunkRebuildImmediate(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk)) return;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (chunk.jobScheduled)
        {
            if (!chunk.currentJob.IsCompleted)
            {
                RequestChunkRebuild(coord, dirtySubchunkMask, rebuildColliders);
                return;
            }

            chunk.jobScheduled = false;
            chunk.currentJob = default;
        }

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        if (TryScheduleFastChunkRebuild(coord, chunk, expectedGen, dirtySubchunkMask, rebuildColliders))
            return;

        chunk.hasVoxelData = false;

        // Build edits similar ao RequestChunk
        var editsList = new List<BlockEdit>();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, editsList);

        NativeArray<BlockEdit> nativeEdits;
        if (editsList.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(editsList.Count, Allocator.Persistent);
            for (int i = 0; i < editsList.Count; i++) nativeEdits[i] = editsList[i];
        }
        else
        {
            nativeEdits = new NativeArray<BlockEdit>(0, Allocator.Persistent);
        }

        int treeMargin = GetMaxTreeMarginForGeneration();
        int detailBorderSize = GetDetailedGenerationBorderSize();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        EnsureNativeGenerationCaches();

        // Light injection corrected for rebuild (uses borderSize)
        int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> chunkLightData = default;
        if (enableVoxelLighting)
        {
            chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.Persistent);
            InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        MeshGenerator.ScheduleDataJob(
              coord,
              cachedNativeNoiseLayers,
              cachedNativeWarpLayers,
              cachedNativeBlockMappings,
              cachedNativeEffectiveLightOpacityByBlock,
              baseHeight,
              offsetX,
              offsetZ,
              seaLevel,
              GetBiomeNoiseSettings(),
              seed,
              nativeEdits,
              treeMargin,
              dataBorderSize,
              lightBorderSize,
              detailBorderSize,
              GetMaxTreeRadiusForGeneration(),
              CliffTreshold,
              enableTrees,
              cachedNativeOreSettings,
              cachedNativeTreeSpawnRules,
              caveGenerationMode,
              caveWormSettings,
              caveSpaghettiSettings,
              enableVoxelLighting,
              chunkLightData,
              chunk.voxelData,
              out JobHandle dataHandle,
              out NativeArray<int> heightCache,
              out NativeArray<byte> blockTypes,
              out NativeArray<bool> solids,
              out NativeArray<byte> light,
              out NativeArray<byte> lightOpacityData,
              out NativeArray<bool> subchunkNonEmpty
          );

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            borderSize = dataBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            lightOpacityData = lightOpacityData,
            edits = nativeEdits,
            fastRebuildSnapshotVoxelData = default,
            fastRebuildSnapshotLoadedChunks = default,
            fastRebuildOverrides = default,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders
        });

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
    }

    private bool IsChunkJobPending(Vector2Int coord)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
            if (pendingMeshes[i].coord == coord)
                return true;

        for (int i = 0; i < pendingDataJobs.Count; i++)
            if (pendingDataJobs[i].coord == coord)
                return true;

        for (int i = 0; i < pendingChunks.Count; i++)
            if (pendingChunks[i].coord == coord)
                return true;

        return false;
    }

    private bool HasQueuedChunkRebuild(Vector2Int coord)
    {
        return queuedChunkRebuildMasks.ContainsKey(coord);
    }

    private void RefreshChunkJobTracking(Vector2Int coord, Chunk chunk)
    {
        if (chunk == null || IsChunkJobPending(coord))
            return;

        chunk.jobScheduled = false;
        chunk.currentJob = default;

        if (chunk.state == Chunk.ChunkState.MeshReady)
            chunk.state = Chunk.ChunkState.Active;
    }

    private void RemovePendingDataJobAtSwapBack(int index)
    {
        int last = pendingDataJobs.Count - 1;
        if (index < 0 || index > last) return;
        if (index != last)
            pendingDataJobs[index] = pendingDataJobs[last];
        pendingDataJobs.RemoveAt(last);
    }

    private void RemovePendingMeshAtSwapBack(int index)
    {
        int last = pendingMeshes.Count - 1;
        if (index < 0 || index > last) return;
        if (index != last)
            pendingMeshes[index] = pendingMeshes[last];
        pendingMeshes.RemoveAt(last);
    }

    private void HandleBlockColliderToggle()
    {
        if (lastEnableBlockColliders == enableBlockColliders) return;

        lastEnableBlockColliders = enableBlockColliders;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null) continue;
            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);

            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk sc = chunk.subchunks[i];
                if (sc != null)
                {
                    sc.SetColliderSystemEnabled(chunkIsSimulated);

                    if (chunkIsSimulated &&
                        chunk.hasVoxelData &&
                        sc.hasGeometry &&
                        sc.CanHaveColliders &&
                        !sc.HasColliderData)
                    {
                        EnqueueColliderBuild(kv.Key, chunk.generation, i);
                    }
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
        {
            RequestHighBuildMeshRebuild(kv.Key);
        }
    }

    private void HandleVisualFeatureToggle()
    {
        bool lightingChanged = lastEnableVoxelLighting != enableVoxelLighting;
        bool aoChanged = lastEnableAmbientOcclusion != enableAmbientOcclusion;
        if (!lightingChanged && !aoChanged)
            return;

        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;

        foreach (var kv in activeChunks)
        {
            RequestChunkRebuild(kv.Key, GetFullSubchunkMask(), false);
        }
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
                Subchunk sc = chunk.subchunks[i];
                if (sc == null)
                    continue;

                if (!chunkIsSimulated)
                {
                    sc.SetColliderSystemEnabled(false);
                    continue;
                }

                if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated || !sc.hasGeometry || !sc.CanHaveColliders)
                {
                    sc.SetColliderSystemEnabled(false);
                    continue;
                }

                if (sc.HasColliderData)
                {
                    sc.SetColliderSystemEnabled(true);
                    continue;
                }

                EnqueueColliderBuild(kv.Key, chunk.generation, i);
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }

    private Vector2Int GetChunkCoordFromWorldPosition(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt(worldPos.z / Chunk.SizeZ)
        );
    }

    private Bounds GetChunkBoundsFromCoord(Vector2Int coord)
    {
        Vector3 center = new Vector3(
            coord.x * Chunk.SizeX + Chunk.SizeX * 0.5f,
            Chunk.SizeY * 0.5f,
            coord.y * Chunk.SizeZ + Chunk.SizeZ * 0.5f
        );
        Vector3 size = new Vector3(Chunk.SizeX, Chunk.SizeY, Chunk.SizeZ);
        return new Bounds(center, size);
    }

    private void DrawBoundsGizmo(Bounds b, Color color, bool filled)
    {
        if (filled)
        {
            Color fill = new Color(color.r, color.g, color.b, Mathf.Clamp01(debugGizmoFillAlpha));
            Gizmos.color = fill;
            Gizmos.DrawCube(b.center, b.size);
        }

        Gizmos.color = color;
        Gizmos.DrawWireCube(b.center, b.size);
    }

    private void OnDrawGizmos()
    {
        if (!debugDrawGizmos) return;
        if (debugGizmosOnlyWhenPlaying && !Application.isPlaying) return;
        if (player == null) return;

        Vector2Int playerCoord = GetChunkCoordFromWorldPosition(player.position);

        if (debugDrawRenderDistanceGrid)
        {
            for (int x = -renderDistance; x <= renderDistance; x++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector2Int coord = new Vector2Int(playerCoord.x + x, playerCoord.y + z);
                    if (!IsCoordInsideRenderDistance(coord, playerCoord))
                        continue;

                    DrawBoundsGizmo(GetChunkBoundsFromCoord(coord), debugRenderGridColor, false);
                }
            }
        }

        if (debugDrawPlayerChunkBounds)
        {
            DrawBoundsGizmo(GetChunkBoundsFromCoord(playerCoord), debugPlayerChunkColor, true);
        }

        if (debugDrawActiveChunkBounds)
        {
            foreach (var kv in activeChunks)
            {
                DrawBoundsGizmo(GetChunkBoundsFromCoord(kv.Key), debugActiveChunkColor, false);
            }
        }

        if (debugDrawPendingChunkQueue)
        {
            for (int i = 0; i < pendingChunks.Count; i++)
            {
                Bounds b = GetChunkBoundsFromCoord(pendingChunks[i].coord);
                Gizmos.color = debugPendingChunkColor;
                Gizmos.DrawWireCube(b.center, b.size);
                Gizmos.DrawSphere(b.center, 0.6f);
            }
        }

        if (debugDrawSubchunkBounds)
        {
            foreach (var kv in activeChunks)
            {
                Chunk chunk = kv.Value;
                if (chunk == null || chunk.subchunks == null) continue;

                for (int i = 0; i < chunk.subchunks.Length; i++)
                {
                    Subchunk sc = chunk.subchunks[i];
                    if (sc == null) continue;
                    if (debugSubchunksOnlyWithGeometry && !sc.hasGeometry) continue;

                    float minY = i * Chunk.SubchunkHeight;
                    Vector3 center = chunk.transform.position + new Vector3(Chunk.SizeX * 0.5f, minY + Chunk.SubchunkHeight * 0.5f, Chunk.SizeZ * 0.5f);
                    Vector3 size = new Vector3(Chunk.SizeX, Chunk.SubchunkHeight, Chunk.SizeZ);
                    Gizmos.color = debugSubchunkColor;
                    Gizmos.DrawWireCube(center, size);
                }
            }
        }

    }


    #endregion

    #region Dispose Helpers

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
        if (pm.transparentTriangles.IsCreated) pm.transparentTriangles.Dispose();
        if (pm.billboardTriangles.IsCreated) pm.billboardTriangles.Dispose();
        if (pm.waterTriangles.IsCreated) pm.waterTriangles.Dispose();
        if (pm.uvs.IsCreated) pm.uvs.Dispose();
        if (pm.uv2.IsCreated) pm.uv2.Dispose();
        if (pm.normals.IsCreated) pm.normals.Dispose();
        if (pm.suppressedBillboards.IsCreated) pm.suppressedBillboards.Dispose();
        if (pm.extraUVs.IsCreated) pm.extraUVs.Dispose();
        if (pm.subchunkRanges.IsCreated) pm.subchunkRanges.Dispose();
        if (pm.subchunkVisibilityMasks.IsCreated) pm.subchunkVisibilityMasks.Dispose();
    }

    #endregion


}




