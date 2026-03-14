using System;
using System.Collections.Generic;
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



#region Utilities

public static class LightUtils
{
    // Junta as duas luzes (0-15) em um único byte
    public static byte PackLight(byte skyLight, byte blockLight)
    {
        return (byte)((skyLight << 4) | (blockLight & 0x0F));
    }

    // Extrai apenas a luz do céu (bits 4 a 7)
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
    }

    #endregion

    #region Inspector Fields - World Setup

    [Header("General")]
    public Transform player;
    public GameObject chunkPrefab;
    public int renderDistance = 4;
    public int poolSize = 200;

    [Header("Atlas / Material")]
    public Material[] Material;
    public int atlasTilesX = 4;
    public int atlasTilesY = 4;

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

    [Header("Worm Cave Settings")]
    public WormCaveSettings caveWormSettings = WormCaveSettings.Default;

    [Header("Performance Settings")]
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;
    [Tooltip("Quantidade maxima de jobs de dados concluidos e processados por frame.")]
    [Min(1)]
    public int maxDataCompletionsPerFrame = 2;
    public float frameTimeBudgetMS = 4f;
    [Tooltip("Limite de jobs de geração de dados (inclui iluminação) simultâneos para evitar queda brusca de FPS.")]
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
    [Tooltip("Forca do AO. 1 = padrao, >1 escurece mais os cantos, 0 desativa o AO.")]
    [Range(0f, 2.5f)]
    public float aoStrength = 1.35f;
    [Tooltip("Curva do AO. Valores maiores aumentam o contraste do escurecimento.")]
    [Range(0.5f, 3f)]
    public float aoCurveExponent = 1.25f;
    [Tooltip("Luz minima aplicada apos AO. Menor valor permite cantos mais escuros.")]
    [Range(0f, 1f)]
    public float aoMinLight = 0.08f;

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
    [Tooltip("Padding horizontal em voxels para propagação de skylight entre chunks. Use 16 para eliminar costura visível na suavização.")]
    [Min(1)]
    public int sunlightSmoothingPadding = 16;

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
    private TreeSpawnRuleData[] cachedTreeSpawnRules = Array.Empty<TreeSpawnRuleData>();
    private bool treeSpawnRulesDirty = true;


    // Optimization temporaries
    private Vector2Int _lastChunkCoord = new Vector2Int(-99999, -99999);
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
        int dx = Mathf.Abs(coord.x - center.x);
        int dz = Mathf.Abs(coord.y - center.y);
        return dx <= renderDistance && dz <= renderDistance;
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
                count++;
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
                return a.subchunkIndex.CompareTo(b.subchunkIndex);
            });
        }
    }

    private int GetChunkBorderSize()
    {
        int treeBorder = GetMaxTreeCanopyRadiusForGeneration() + 1;
        return Mathf.Max(treeBorder, sunlightSmoothingPadding);
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
            maxRadius = Mathf.Max(maxRadius, Mathf.Max(0, rules[i].settings.canopyRadius));

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
            maxMargin = Mathf.Max(maxMargin, Mathf.Max(1, s.maxHeight + s.canopyHeight + 2));
        }

        return maxMargin;
    }

    private void RebuildTreeSpawnRuleCache()
    {
        treeSpawnRulesDirty = false;

        List<TreeSpawnRuleData> rules = new List<TreeSpawnRuleData>(12);
        AddTreeRulesFromBiomeDefinitions(rules);

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
        public NativeList<byte> lightValues;
        public NativeList<byte> tintFlags;
        public Vector2Int coord;
        public int expectedGen;
        public int subchunkIndex;
        public Subchunk targetSubchunk;
        public Chunk parentChunk;

        // Data arrays (kept so we can dispose later)
        public NativeArray<int> heightCache;
        public NativeArray<BlockType> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public NativeArray<BlockTextureMapping> nativeBlockMappings;
        public NativeArray<int3> suppressedBillboards;
        public NativeList<byte> vertexAO;
        public NativeList<Vector4> extraUVs;
    }

    private struct PendingData
    {
        public JobHandle handle;
        public NativeArray<int> heightCache;
        public NativeArray<BlockType> blockTypes;
        public NativeArray<bool> solids;
        public NativeArray<byte> light;
        public NativeArray<NoiseLayer> nativeNoiseLayers;
        public NativeArray<WarpLayer> nativeWarpLayers;
        public NativeArray<BlockTextureMapping> nativeBlockMappings;
        public NativeArray<OreSpawnSettings> nativeOreSettings;
        public NativeArray<TreeSpawnRuleData> nativeTreeSpawnRules;

        public Chunk chunk;
        public Vector2Int coord;
        public int expectedGen;
        public NativeArray<byte> chunkLightData;
        public NativeArray<BlockEdit> edits;

        public NativeArray<bool> subchunkNonEmpty;
    }

    #endregion

    #region Unity Callbacks

    private void OnValidate()
    {
        ApplyTerrainLayerProfileIfAssigned();
        EnsureTerrainLayerArraysInitialized();
        EnsureDefaultWarpLayersConfigured();
        MarkBiomeCachesDirty();
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

        // Pre-instantiate pool
        for (int i = 0; i < poolSize; i++)
        {
            GameObject obj = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform);
            obj.SetActive(false);
            Chunk chunk = obj.GetComponent<Chunk>();
            chunkPool.Enqueue(chunk);
        }

        lastEnableBlockColliders = enableBlockColliders;
        InitializeDistantTerrainLodState();
    }

    private void Update()
    {
        HandleBlockColliderToggle();
        meshesAppliedThisFrame = 0;
        ProcessQueuedChunkRebuilds();
        ProcessQueuedHighBuildMeshRebuilds();
        UpdateChunks();
        UpdateDistantTerrainLod();
        ProcessChunkQueue();
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

            if (pd.edits.IsCreated) pd.edits.Dispose();
            if (pd.chunkLightData.IsCreated) pd.chunkLightData.Dispose();

            if (activeChunks.TryGetValue(pd.coord, out Chunk activeChunk) &&
                activeChunk.generation == pd.expectedGen)
            {
                activeChunk.InitializeSubchunks(Material);
                ApplyChunkBiomeTint(activeChunk, pd.coord);
                activeChunk.hasVoxelData = true;
                activeChunk.state = Chunk.ChunkState.MeshReady;

                CopyVoxelDataOptimized(pd.blockTypes, activeChunk.voxelData);
               

                ScheduleSubchunkMeshJobs(pd, activeChunk);

                if (pd.nativeNoiseLayers.IsCreated) pd.nativeNoiseLayers.Dispose();
                if (pd.nativeWarpLayers.IsCreated) pd.nativeWarpLayers.Dispose();
                if (pd.nativeOreSettings.IsCreated) pd.nativeOreSettings.Dispose();
            }
            else
            {
                DisposeDataJobResources(pd);
            }

            RemovePendingDataJobAtSwapBack(i);
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

            if (activeChunks.TryGetValue(pm.coord, out Chunk activeChunk) &&
                activeChunk.generation == pm.expectedGen)
            {
                Subchunk sub = activeChunk.subchunks[pm.subchunkIndex];

                if (pm.vertices.Length > 0)
                {
                    sub.gameObject.SetActive(true);
                    int startY = pm.subchunkIndex * Chunk.SubchunkHeight;
                    int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);
                    sub.ApplyMeshData(pm.vertices, pm.opaqueTriangles, pm.transparentTriangles, pm.billboardTriangles,
                                      pm.waterTriangles, pm.uvs, pm.uv2, pm.normals, pm.extraUVs,
                                      activeChunk.voxelData, blockData != null ? blockData.mappings : null,
                                      startY, endY, enableBlockColliders);
                }
                else
                {
                    sub.ClearMesh();
                }

                activeChunk.state = Chunk.ChunkState.Active;
            }

            DisposePendingMesh(pm);
            RemovePendingMeshAtSwapBack(i);
            meshesAppliedThisFrame++;
        }
    }

    #endregion

    #region Voxel Data Copy & Mesh Scheduling

    private void CopyVoxelDataOptimized(NativeArray<BlockType> src, NativeArray<byte> dst)
    {
        // Copia bloco por bloco do native BlockType array para o voxelData (byte).
        int border = GetChunkBorderSize();
        int vx = Chunk.SizeX + 2 * border;
        int plane = vx * Chunk.SizeY;

        int dstIndex = 0;

        for (int y = 0; y < Chunk.SizeY; y++)
        {
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int srcBase = (z + border) * plane + y * vx + border;
                for (int x = 0; x < Chunk.SizeX; x++)
                {
                    dst[dstIndex++] = (byte)src[srcBase + x];
                }
            }
        }
    }

    private void ScheduleSubchunkMeshJobs(PendingData pd, Chunk activeChunk)
    {
        int borderSize = GetChunkBorderSize();
        NativeList<JobHandle> meshHandles = new NativeList<JobHandle>(Chunk.SubchunksPerColumn, Allocator.Temp);
        List<int3> suppressedBillboardsForChunk = GetSuppressedGrassBillboardsForChunk(pd.coord);
        NativeArray<int3> nativeSuppressedBillboards = new NativeArray<int3>(suppressedBillboardsForChunk.Count, Allocator.TempJob);
        for (int s = 0; s < suppressedBillboardsForChunk.Count; s++)
            nativeSuppressedBillboards[s] = suppressedBillboardsForChunk[s];

        for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
        {
            if (!pd.subchunkNonEmpty[sub])
            {
                activeChunk.subchunks[sub].ClearMesh();
                continue;
            }

            int startY = sub * Chunk.SubchunkHeight;
            if (startY >= Chunk.SizeY)
            {
                activeChunk.subchunks[sub].ClearMesh();
                continue;
            }

            int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);

            MeshGenerator.ScheduleMeshJob(
                pd.heightCache, pd.blockTypes, pd.solids, pd.light, pd.nativeBlockMappings, nativeSuppressedBillboards,
                atlasTilesX, atlasTilesY, true, borderSize,
                pd.coord.x, pd.coord.y,
                startY, endY,
                enableGrassBillboards, grassBillboardChance, grassBillboardBlockType, grassBillboardHeight,
                grassBillboardNoiseScale, grassBillboardJitter,
                aoStrength, aoCurveExponent, aoMinLight,
                out JobHandle meshHandle,
                out NativeList<Vector3> vertices,
                out NativeList<int> opaqueTriangles,
                out NativeList<int> transparentTriangles,
                out NativeList<int> billboardTriangles,
                out NativeList<int> waterTriangles,
                out NativeList<Vector2> uvs,
                out NativeList<Vector2> uv2,
                out NativeList<Vector3> normals,
                out NativeList<byte> vertexLights,
                out NativeList<byte> tintFlags,
                out NativeList<byte> vertexAO,
                out NativeList<Vector4> extraUVs
            );

            meshHandles.Add(meshHandle);

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
                lightValues = vertexLights,
                tintFlags = tintFlags,
                vertexAO = vertexAO,
                extraUVs = extraUVs,
                coord = pd.coord,
                expectedGen = pd.expectedGen,
                subchunkIndex = sub,
                parentChunk = activeChunk,
                heightCache = pd.heightCache,
                blockTypes = pd.blockTypes,
                solids = pd.solids,
                light = pd.light,
                nativeBlockMappings = pd.nativeBlockMappings,
                suppressedBillboards = default
            });
        }

        JobHandle combinedMeshHandle = meshHandles.Length > 0
            ? JobHandle.CombineDependencies(meshHandles.AsArray())
            : new JobHandle();

        meshHandles.Dispose();

        var disposeJob = new MeshGenerator.DisposeChunkDataJob
        {
            heightCache = pd.heightCache,
            blockTypes = pd.blockTypes,
            solids = pd.solids,
            light = pd.light,
            blockMappings = pd.nativeBlockMappings,
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

            for (int x = -renderDistance; x <= renderDistance; x++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    _tempNeededCoords.Add(new Vector2Int(currentChunkCoord.x + x, currentChunkCoord.y + z));
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
                    
                    RemoveHighBuildMesh(coord);
                }
            }

            // B. Limpar pendentes desnecessários
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

            // D. Reordenar fila por distância
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
            chunk = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform).GetComponent<Chunk>();
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

        RemoveDistantTerrainChunk(coord);
        activeChunks.Add(coord, chunk);
        RequestHighBuildMeshRebuild(coord);

        // Build edits from blockOverrides
        var editsList = new List<BlockEdit>();
        int borderSize = GetChunkBorderSize();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        // Must cover the same padded area used by generation/lighting.
        int extend = borderSize;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        foreach (var kv in blockOverrides)
        {
            Vector3Int wp = kv.Key;
            if (wp.x >= minX && wp.x <= maxX && wp.z >= minZ && wp.z <= maxZ && wp.y >= 0 && wp.y < Chunk.SizeY)
            {
                editsList.Add(new BlockEdit
                {
                    x = wp.x,
                    y = wp.y,
                    z = wp.z,
                    type = (int)kv.Value
                });
            }
        }

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
        TreeSpawnRuleData[] activeTreeSpawnRules = GetActiveTreeSpawnRules();

        // Injeção da luz global
        // Light injection corrected for rebuild (uses borderSize)
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.TempJob);

        InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, borderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        if (chunk.jobScheduled)
        {
            try { chunk.currentJob.Complete(); } catch { }
            chunk.jobScheduled = false;
        }

        // Agendamento do data job
        MeshGenerator.ScheduleDataJob(
            coord, noiseLayers, warpLayers, blockData.mappings,
            baseHeight, offsetX, offsetZ, seaLevel,
            GetBiomeNoiseSettings(),
            seed,
            nativeEdits, treeMargin, borderSize,
            GetMaxTreeRadiusForGeneration(), CliffTreshold, enableTrees,
            oreSettings,
            activeTreeSpawnRules,
            caveWormSettings,
            chunkLightData,
            out JobHandle dataHandle,
            out NativeArray<int> heightCache,
            out NativeArray<BlockType> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<byte> light,
            out NativeArray<NoiseLayer> nativeNoiseLayers,
            out NativeArray<WarpLayer> nativeWarpLayers,
            out NativeArray<BlockTextureMapping> nativeBlockMappings,
            out NativeArray<OreSpawnSettings> nativeOreSettings,
            out NativeArray<TreeSpawnRuleData> nativeTreeSpawnRules,
            out NativeArray<bool> subchunkNonEmpty
        );

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            nativeNoiseLayers = nativeNoiseLayers,
            nativeWarpLayers = nativeWarpLayers,
            nativeBlockMappings = nativeBlockMappings,
            nativeOreSettings = nativeOreSettings,
            nativeTreeSpawnRules = nativeTreeSpawnRules,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            edits = nativeEdits,
            subchunkNonEmpty = subchunkNonEmpty
        });

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
        chunk.gameObject.SetActive(true);
    }

    private bool TryScheduleFastChunkRebuild(Vector2Int coord, Chunk chunk, int expectedGen)
    {
        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return false;
        if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated)
            return false;

        int borderSize = GetChunkBorderSize();
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int totalVoxels = voxelSizeX * Chunk.SizeY * voxelSizeZ;
        int totalHeightPoints = voxelSizeX * voxelSizeZ;

        NativeArray<int> heightCache = new NativeArray<int>(totalHeightPoints, Allocator.TempJob);
        NativeArray<BlockType> blockTypes = new NativeArray<BlockType>(totalVoxels, Allocator.TempJob);
        NativeArray<bool> solids = new NativeArray<bool>(totalVoxels, Allocator.TempJob);
        NativeArray<byte> light = new NativeArray<byte>(totalVoxels, Allocator.TempJob);
        NativeArray<bool> subchunkNonEmpty = new NativeArray<bool>(Chunk.SubchunksPerColumn, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockData.mappings, Allocator.TempJob);
        NativeArray<byte> blockLightData = new NativeArray<byte>(totalVoxels, Allocator.TempJob);

        FillFastRebuildArraysFromLoadedChunks(
            coord, borderSize,
            heightCache, blockTypes, solids, subchunkNonEmpty
        );

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        InjectGlobalLightColumns(blockLightData, chunkMinX, chunkMinZ, borderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);

        var lightJob = new ChunkLighting.ChunkLightingJob
        {
            blockTypes = blockTypes,
            light = light,
            blockLightData = blockLightData,
            blockMappings = nativeBlockMappings,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            totalVoxels = totalVoxels,
            voxelPlaneSize = voxelPlaneSize,
            SizeX = Chunk.SizeX,
            SizeY = Chunk.SizeY,
            SizeZ = Chunk.SizeZ
        };

        JobHandle lightHandle = lightJob.Schedule();

        pendingDataJobs.Add(new PendingData
        {
            handle = lightHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            nativeNoiseLayers = default,
            nativeWarpLayers = default,
            nativeBlockMappings = nativeBlockMappings,
            nativeOreSettings = default,
            nativeTreeSpawnRules = default,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = blockLightData,
            edits = default,
            subchunkNonEmpty = subchunkNonEmpty
        });

        chunk.currentJob = lightHandle;
        chunk.jobScheduled = true;
        chunk.state = Chunk.ChunkState.MeshReady;
        return true;
    }

    private void FillFastRebuildArraysFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<int> heightCache,
        NativeArray<BlockType> blockTypes,
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
                    blockTypes[idx] = bt;

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
    }

    private bool TryResolveLoadedColumn(int worldX, int worldZ, out Chunk chunk, out int localX, out int localZ)
    {
        Vector2Int coord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(coord, out chunk) && chunk.hasVoxelData && chunk.voxelData.IsCreated)
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

    private void RequestChunkRebuild(Vector2Int coord)
    {
        if (!queuedChunkRebuildsSet.Add(coord)) return;
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

            if (IsChunkJobPending(coord))
            {
                if (queuedChunkRebuildsSet.Add(coord))
                    queuedChunkRebuilds.Enqueue(coord);
                continue;
            }

            RequestChunkRebuildImmediate(coord);
            processed++;
        }
    }

    private void RequestChunkRebuildImmediate(Vector2Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk)) return;

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        if (chunk.jobScheduled)
        {
            try { chunk.currentJob.Complete(); } catch { }
            chunk.jobScheduled = false;
        }

        if (TryScheduleFastChunkRebuild(coord, chunk, expectedGen))
            return;

        chunk.hasVoxelData = false;

        // Build edits similar ao RequestChunk
        var editsList = new List<BlockEdit>();
        int borderSize = GetChunkBorderSize();

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        // Must cover the same padded area used by generation/lighting.
        int extend = borderSize;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        foreach (var kv in blockOverrides)
        {
            Vector3Int wp = kv.Key;
            if (wp.x >= minX && wp.x <= maxX && wp.z >= minZ && wp.z <= maxZ && wp.y >= 0 && wp.y < Chunk.SizeY)
            {
                editsList.Add(new BlockEdit
                {
                    x = wp.x,
                    y = wp.y,
                    z = wp.z,
                    type = (int)kv.Value
                });
            }
        }

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
        TreeSpawnRuleData[] activeTreeSpawnRules = GetActiveTreeSpawnRules();

        // Light injection corrected for rebuild (uses borderSize)
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.TempJob);

        InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, borderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);

        MeshGenerator.ScheduleDataJob(
              coord,
              noiseLayers,
              warpLayers,
              blockData.mappings,
              baseHeight,
              offsetX,
              offsetZ,
              seaLevel,
              GetBiomeNoiseSettings(),
              seed,
              nativeEdits,
              treeMargin,
              borderSize,
              GetMaxTreeRadiusForGeneration(),
              CliffTreshold,
              enableTrees,
              oreSettings,
              activeTreeSpawnRules,
              caveWormSettings,
              chunkLightData,
              out JobHandle dataHandle,
              out NativeArray<int> heightCache,
              out NativeArray<BlockType> blockTypes,
              out NativeArray<bool> solids,
              out NativeArray<byte> light,
              out NativeArray<NoiseLayer> nativeNoiseLayers,
              out NativeArray<WarpLayer> nativeWarpLayers,
              out NativeArray<BlockTextureMapping> nativeBlockMappings,
              out NativeArray<OreSpawnSettings> nativeOreSettings,
              out NativeArray<TreeSpawnRuleData> nativeTreeSpawnRules,
              out NativeArray<bool> subchunkNonEmpty
          );

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            light = light,
            nativeNoiseLayers = nativeNoiseLayers,
            nativeWarpLayers = nativeWarpLayers,
            nativeBlockMappings = nativeBlockMappings,
            nativeOreSettings = nativeOreSettings,
            nativeTreeSpawnRules = nativeTreeSpawnRules,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            edits = nativeEdits,
            subchunkNonEmpty = subchunkNonEmpty
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

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null) continue;

            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk sc = chunk.subchunks[i];
                if (sc != null)
                    sc.SetColliderSystemEnabled(enableBlockColliders);
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);

        // If colliders were re-enabled, rebuild active chunks so subchunks generated while disabled gain collider data.
        if (enableBlockColliders)
        {
            foreach (var kv in activeChunks)
            {
                RequestChunkRebuild(kv.Key);
                RequestHighBuildMeshRebuild(kv.Key);
            }
        }
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

        DrawDistantTerrainLodGizmos();
    }


    #endregion

    #region Dispose Helpers

    private void DisposeDataJobResources(in PendingData pd)
    {
        if (pd.heightCache.IsCreated) pd.heightCache.Dispose();
        if (pd.blockTypes.IsCreated) pd.blockTypes.Dispose();
        if (pd.solids.IsCreated) pd.solids.Dispose();
        if (pd.light.IsCreated) pd.light.Dispose();
        if (pd.nativeNoiseLayers.IsCreated) pd.nativeNoiseLayers.Dispose();
        if (pd.nativeWarpLayers.IsCreated) pd.nativeWarpLayers.Dispose();
        if (pd.nativeBlockMappings.IsCreated) pd.nativeBlockMappings.Dispose();
        if (pd.nativeOreSettings.IsCreated) pd.nativeOreSettings.Dispose();
        if (pd.nativeTreeSpawnRules.IsCreated) pd.nativeTreeSpawnRules.Dispose();
        if (pd.chunkLightData.IsCreated) pd.chunkLightData.Dispose();
        if (pd.edits.IsCreated) pd.edits.Dispose();
        if (pd.subchunkNonEmpty.IsCreated) pd.subchunkNonEmpty.Dispose();
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
        if (pm.lightValues.IsCreated) pm.lightValues.Dispose();
        if (pm.tintFlags.IsCreated) pm.tintFlags.Dispose();
        if (pm.suppressedBillboards.IsCreated) pm.suppressedBillboards.Dispose();
        if (pm.vertexAO.IsCreated) pm.vertexAO.Dispose();
        if (pm.extraUVs.IsCreated) pm.extraUVs.Dispose();
    }

    #endregion


}




