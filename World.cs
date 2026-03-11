using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;


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

    [Header("Noise Settings")]
    public NoiseLayer[] noiseLayers;

    [Header("Cave Settings")]
    public NoiseLayer[] caveLayers;
    public float caveThreshold = 0.58f;
    public int caveStride = 4;
    public int maxCaveDepthMultiplier = 1;

    [Header("Domain Warping Settings")]
    public WarpLayer[] warpLayers;
    public int baseHeight = 64;
    public int heightVariation = 32;
    public int seed = 1337;

    [Header("Block Data")]
    public BlockDataSO blockData;

    [Header("Sea Settings")]
    public int seaLevel = 62;
    public BlockType waterBlock = BlockType.Water;

    [Header("Tree Settings")]
    public TreeSettings treeSettings;
    public int CliffTreshold = 2;

    [Header("Performance Settings")]
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;
    public float frameTimeBudgetMS = 4f;
    [Tooltip("Limite de jobs de geraÃ§Ã£o de dados (inclui iluminaÃ§Ã£o) simultÃ¢neos para evitar queda brusca de FPS.")]
    [Min(1)]
    public int maxPendingDataJobs = 2;
    [Tooltip("Quantidade maxima de pedidos de rebuild de chunk processados por frame.")]
    [Min(1)]
    public int maxChunkRebuildsPerFrame = 1;

    [Header("Frustum Culling")]
    public bool enableFrustumCulling = true;
    public Camera mainCamera;

    [Header("Vertical Render Distance + Full Visibility")]
    [FormerlySerializedAs("verticalSubchunkRenderDistance")]
    [Tooltip("Quantos subchunks acima do jogador serao renderizados")]
    [Min(0)]
    public int verticalSubchunkRenderDistanceAbove = 4;

    [Tooltip("Quantos subchunks abaixo do jogador serao renderizados")]
    [Min(0)]
    public int verticalSubchunkRenderDistanceBelow = 2;

    [Tooltip("Chunks dentro deste raio horizontal (Chebyshev) terÃ£o TODOS os subchunks visÃ­veis (sem culling vertical).")]
    public int horizontalFullVisibilityRadius = 2;

    [Header("Features Toggle")]
    public bool enableCave = true;
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
    [Tooltip("Padding horizontal em voxels para propagaÃ§Ã£o de skylight entre chunks. Use 16 para eliminar costura visÃ­vel na suavizaÃ§Ã£o.")]
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
    // private Dictionary<Vector3Int, byte> globalLightMap = new Dictionary<Vector3Int, byte>();
    private Dictionary<Vector2Int, byte[]> globalLightColumns = new Dictionary<Vector2Int, byte[]>();
    // Misc
    private float offsetX, offsetZ;
    private int nextChunkGeneration = 0;
    private int meshesAppliedThisFrame = 0;
    private float frameTimeAccumulator = 0f;
    private bool lastEnableBlockColliders = true;


    // Optimization temporaries
    private Vector2Int _lastChunkCoord = new Vector2Int(-99999, -99999);
    private readonly HashSet<Vector2Int> _tempNeededCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _tempToRemove = new List<Vector2Int>();

    // Chunks that need recull
    private HashSet<Vector2Int> chunksToRecull = new HashSet<Vector2Int>();

    private int GetChunkBorderSize()
    {
        int treeBorder = treeSettings.canopyRadius + 1;
        return Mathf.Max(treeBorder, sunlightSmoothingPadding);
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
        public NativeArray<NoiseLayer> nativeCaveLayers;
        public NativeArray<BlockTextureMapping> nativeBlockMappings;

        public Chunk chunk;
        public Vector2Int coord;
        public int expectedGen;
        public NativeArray<byte> chunkLightData;
        public NativeArray<BlockEdit> edits;

        public NativeArray<bool> subchunkNonEmpty;
    }

    #endregion

    #region Unity Callbacks

    private void Start()
    {
        // Camera fallback
        if (mainCamera == null)
            mainCamera = Camera.main ?? FindObjectOfType<Camera>();

        if (blockData != null) blockData.InitializeDictionary();

        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        InitializeNoiseLayers();
        InitializeWarpLayers();
        InitializeCaveLayers();

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

    private void LateUpdate()
    {
        if (!enableFrustumCulling) return;
        UpdateVerticalSubchunkVisibility();
    }

    #endregion

    #region Initialization Helpers

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

    private void InitializeCaveLayers()
    {
        if (caveLayers == null) return;

        for (int i = 0; i < caveLayers.Length; i++)
        {
            NoiseLayer layer = caveLayers[i];
            if (!layer.enabled) continue;

            if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1f;
            if (layer.exponent == 0f) layer.exponent = 1f;

            if (layer.scale <= 0f) layer.scale = 0.03f;
            if (layer.octaves <= 0) layer.octaves = 4;
            if (layer.lacunarity <= 0f) layer.lacunarity = 2f;
            if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.5f;

            if (layer.offset == Vector2.zero)
                layer.offset = new Vector2(offsetX + i * 19.87f, offsetZ + i * 8.76f);
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

            caveLayers[i] = layer;
        }
    }

    #endregion

    #region Chunk Queue & Processing

    private void ProcessChunkQueue()
    {
        float frameStartTime = Time.realtimeSinceStartup;
        float budgetSeconds = frameTimeBudgetMS / 1000f;

        int dataProcessedThisFrame = 0;

        // === STAGE 1: DATA JOBS (voxel generation) ===
        int i = 0;
        while (i < pendingDataJobs.Count)
        {
            if (Time.realtimeSinceStartup - frameStartTime > budgetSeconds) break;
            if (dataProcessedThisFrame >= 2) break; // small safety limit (tune maxDataCompletionsPerFrame if needed)

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
                activeChunk.hasVoxelData = true;
                activeChunk.state = Chunk.ChunkState.MeshReady;

                CopyVoxelDataOptimized(pd.blockTypes, activeChunk.voxelData);

                ScheduleSubchunkMeshJobs(pd, activeChunk);
            }
            else
            {
                DisposeDataJobResources(pd);
            }

            pendingDataJobs.RemoveAt(i);
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
                    sub.ApplyMeshData(pm.vertices, pm.opaqueTriangles, pm.transparentTriangles, pm.billboardTriangles,
                                      pm.waterTriangles, pm.uvs, pm.uv2, pm.normals, pm.extraUVs, enableBlockColliders);
                }
                else
                {
                    sub.ClearMesh();
                }

                activeChunk.state = Chunk.ChunkState.Active;
                chunksToRecull.Add(pm.coord);
            }

            DisposePendingMesh(pm);
            pendingMeshes.RemoveAt(i);
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
            NativeArray<int3> nativeSuppressedBillboards = new NativeArray<int3>(suppressedBillboardsForChunk.Count, Allocator.TempJob);
            for (int s = 0; s < suppressedBillboardsForChunk.Count; s++)
                nativeSuppressedBillboards[s] = suppressedBillboardsForChunk[s];

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
                suppressedBillboards = nativeSuppressedBillboards
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
        disposeJob.Schedule(combinedMeshHandle);

        activeChunk.currentJob = combinedMeshHandle;
    }

    private List<int3> GetSuppressedGrassBillboardsForChunk(Vector2Int chunkCoord)
    {
        List<int3> result = new List<int3>();
        if (suppressedGrassBillboards.Count == 0) return result;

        int minX = chunkCoord.x * Chunk.SizeX;
        int minZ = chunkCoord.y * Chunk.SizeZ;
        int maxX = minX + Chunk.SizeX - 1;
        int maxZ = minZ + Chunk.SizeZ - 1;

        foreach (Vector3Int pos in suppressedGrassBillboards)
        {
            if (pos.x >= minX && pos.x <= maxX &&
                pos.z >= minZ && pos.z <= maxZ &&
                pos.y >= 0 && pos.y < Chunk.SizeY)
            {
                result.Add(new int3(pos.x, pos.y, pos.z));
            }
        }

        return result;
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

        Vector2Int currentChunkCoord = new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );

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

                float dx = coord.x - currentChunkCoord.x;
                float dz = coord.y - currentChunkCoord.y;
                float distSq = dx * dx + dz * dz;
                pendingChunks.Add((coord, distSq));
            }

            // D. Reordenar fila por distÃ¢ncia
            for (int i = 0; i < pendingChunks.Count; i++)
            {
                var item = pendingChunks[i];
                float dx = item.coord.x - currentChunkCoord.x;
                float dz = item.coord.y - currentChunkCoord.y;
                pendingChunks[i] = (item.coord, dx * dx + dz * dz);
            }

            pendingChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        }

        // Processa alguns itens da fila por frame
        if (pendingChunks.Count > 0)
        {
            int processed = 0;
            int realPending = pendingDataJobs.Count + Mathf.CeilToInt(pendingMeshes.Count / 6f);
            bool jobsCongested = realPending > maxChunksPerFrame * 4;

            if (!jobsCongested)
            {
                while (processed < maxChunksPerFrame && pendingChunks.Count > 0)
                {
                    if (pendingDataJobs.Count >= maxPendingDataJobs)
                        break;

                    var item = pendingChunks[0];
                    pendingChunks.RemoveAt(0);

                    if (!activeChunks.ContainsKey(item.coord))
                    {
                        RequestChunk(item.coord);
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

        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);

        // InjeÃ§Ã£o da luz global
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
            coord, noiseLayers, warpLayers, caveLayers, blockData.mappings,
            baseHeight, offsetX, offsetZ, seaLevel,
            caveThreshold, caveStride, maxCaveDepthMultiplier,
            nativeEdits, treeMargin, borderSize,
            treeSettings.canopyRadius, CliffTreshold, enableCave, enableTrees,
            chunkLightData,
            out JobHandle dataHandle,
            out NativeArray<int> heightCache,
            out NativeArray<BlockType> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<byte> light,
            out NativeArray<NoiseLayer> nativeNoiseLayers,
            out NativeArray<WarpLayer> nativeWarpLayers,
            out NativeArray<NoiseLayer> nativeCaveLayers,
            out NativeArray<BlockTextureMapping> nativeBlockMappings,
            out NativeArray<bool> subchunkNonEmpty,
            treeSettings
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
            nativeCaveLayers = nativeCaveLayers,
            nativeBlockMappings = nativeBlockMappings,
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

        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);

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

        MeshGenerator.ScheduleDataJob(
              coord,
              noiseLayers,
              warpLayers,
              caveLayers,
              blockData.mappings,
              baseHeight,
              offsetX,
              offsetZ,
              seaLevel,
              caveThreshold,
              caveStride,
              maxCaveDepthMultiplier,
              nativeEdits,
              treeMargin,
              borderSize,
              treeSettings.canopyRadius,
              CliffTreshold,
              enableCave,
              enableTrees,
              chunkLightData,
              out JobHandle dataHandle,
              out NativeArray<int> heightCache,
              out NativeArray<BlockType> blockTypes,
              out NativeArray<bool> solids,
              out NativeArray<byte> light,
              out NativeArray<NoiseLayer> nativeNoiseLayers,
              out NativeArray<WarpLayer> nativeWarpLayers,
              out NativeArray<NoiseLayer> nativeCaveLayers,
              out NativeArray<BlockTextureMapping> nativeBlockMappings,
              out NativeArray<bool> subchunkNonEmpty,
              treeSettings
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
            nativeCaveLayers = nativeCaveLayers,
            nativeBlockMappings = nativeBlockMappings,
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
        if (pd.nativeCaveLayers.IsCreated) pd.nativeCaveLayers.Dispose();
        if (pd.nativeBlockMappings.IsCreated) pd.nativeBlockMappings.Dispose();
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

