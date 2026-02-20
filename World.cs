using System.Collections.Generic;
using UnityEngine;
using System;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using System.Collections.Concurrent;

[Serializable]
public struct WarpLayer
{
    public bool enabled;
    public float scale;
    public float amplitude;
    public int octaves;
    public float persistence;
    public float lacunarity;
    public Vector2 offset;
    public float maxAmp;
}

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
public class World : MonoBehaviour
{
    public static World Instance { get; private set; }

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

    [Header("Cave Rarity Mask")]
    [Tooltip("Controla o tamanho dos 'bolsões' onde as cavernas podem existir. Valores altos (ex: 200-500) separam mais os sistemas de cavernas.")]
    public float caveRarityScale = 300f;
    [Range(-1f, 1f)]
    [Tooltip("Quanto MAIOR o valor, mais RARAS são as cavernas. Valores negativos geram muitas cavernas.")]
    public float caveRarityThreshold = 0.3f;
    public float caveMaskSmoothness = 5f; // Suaviza a transição para não cortar as cavernas retas


    [Header("Domain Warping Settings")]
    public WarpLayer[] warpLayers;
    public int baseHeight = 64;
    public int heightVariation = 32;
    public int seed = 1337;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Queue<Chunk> chunkPool = new Queue<Chunk>();

    private float offsetX, offsetZ;
    [Header("Block Data")]
    public BlockDataSO blockData;
    [Header("Sea Settings")]
    public int seaLevel = 62;
    public BlockType waterBlock = BlockType.Water;

    private int nextChunkGeneration = 0;

    // Adições para limitação
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;

    private List<(Vector2Int coord, float distSq)> pendingChunks = new List<(Vector2Int, float)>();
    private List<PendingMesh> pendingMeshes = new List<PendingMesh>();

    private Dictionary<Vector3Int, BlockType> blockOverrides = new Dictionary<Vector3Int, BlockType>();

    private struct PendingMesh
    {
        public JobHandle handle;
        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector2> uv2;
        public NativeList<Vector3> normals;
        public NativeList<byte> lightValues;
        public NativeList<byte> tintFlags;
        public NativeArray<MeshGenerator.BlockEdit> edits;
        public NativeArray<MeshGenerator.TreeInstance> trees;
        public Vector2Int coord;
        public int expectedGen;
        public Chunk chunk;
        public NativeList<byte> vertexAO;
        public NativeList<Vector4> extraUVs;

        public NativeArray<byte> meshLightData; // <--- ADICIONE ISTO
    }

    [Header("Tree Settings")]
    public TreeSettings treeSettings;

    public int CliffTreshold = 2;

    public float frameTimeBudgetMS = 4f;

    private Dictionary<Vector3Int, byte> globalLightMap = new Dictionary<Vector3Int, byte>();
    private readonly Vector3Int[] sixDirections = new Vector3Int[]
    {
        Vector3Int.up, Vector3Int.down, Vector3Int.left, Vector3Int.right, Vector3Int.forward, Vector3Int.back
    };
    // Em World.cs (Adicione estas variáveis)
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (blockData != null) blockData.InitializeDictionary();

        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        // Inicializa noiseLayers
        if (noiseLayers != null)
        {
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

        // Inicializa warpLayers
        if (warpLayers != null)
        {
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

        // Inicializa caveLayers
        if (caveLayers != null)
        {
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

        for (int i = 0; i < poolSize; i++)
        {
            GameObject obj = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform);
            obj.SetActive(false);
            Chunk chunk = obj.GetComponent<Chunk>();

            Material[] matsForChunk = (Material != null && Material.Length >= 3) ?
                new Material[] { Material[0], Material[1], Material[2] } :
                new Material[] { (Material.Length > 0 ? Material[0] : null),
                           (Material.Length > 1 ? Material[1] : Material[0]),
                           (Material.Length > 2 ? Material[2] : Material[0]) };

            chunk.SetMaterials(matsForChunk);
            chunkPool.Enqueue(chunk);
        }
    }

    private void Update()
    {
        UpdateChunks();
        ProcessChunkQueue();
    }

    private void ProcessChunkQueue()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = pendingMeshes.Count - 1; i >= 0; i--)
        {
            var pm = pendingMeshes[i];

            if (!pm.handle.IsCompleted) continue;

            pm.handle.Complete();
            if (pm.chunk != null)
            {
                pm.chunk.jobScheduled = false;
            }

            if (activeChunks.TryGetValue(pm.coord, out Chunk activeChunk))
            {
                if (activeChunk.generation == pm.expectedGen)
                {
                    if (stopwatch.Elapsed.TotalMilliseconds < frameTimeBudgetMS)
                    {
                        activeChunk.ApplyMeshData(
                            pm.vertices,
                            pm.opaqueTriangles,
                            pm.transparentTriangles,
                            pm.waterTriangles,
                            pm.uvs,
                            pm.uv2,
                            pm.normals,

                            pm.extraUVs
                        );

                        activeChunk.hasVoxelData = true;
                        activeChunk.gameObject.SetActive(true);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            DisposePendingMesh(pm);
            pendingMeshes.RemoveAt(i);
        }

        stopwatch.Stop();
    }

    private void DisposePendingMesh(PendingMesh pm)
    {
        if (pm.vertices.IsCreated) pm.vertices.Dispose();
        if (pm.opaqueTriangles.IsCreated) pm.opaqueTriangles.Dispose();
        if (pm.transparentTriangles.IsCreated) pm.transparentTriangles.Dispose();
        if (pm.waterTriangles.IsCreated) pm.waterTriangles.Dispose();
        if (pm.uvs.IsCreated) pm.uvs.Dispose();
        if (pm.uv2.IsCreated) pm.uv2.Dispose();
        if (pm.normals.IsCreated) pm.normals.Dispose();
        if (pm.lightValues.IsCreated) pm.lightValues.Dispose();
        if (pm.tintFlags.IsCreated) pm.tintFlags.Dispose();
        if (pm.edits.IsCreated) pm.edits.Dispose();
        if (pm.trees.IsCreated) pm.trees.Dispose();
        if (pm.meshLightData.IsCreated) pm.meshLightData.Dispose();
    }

    // ===================================================================================
    // VARIÁVEIS DE OTIMIZAÇÃO
    // ===================================================================================
    private Vector2Int _lastChunkCoord = new Vector2Int(-99999, -99999);
    private readonly HashSet<Vector2Int> _tempNeededCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _tempToRemove = new List<Vector2Int>();

    // ===================================================================================
    // MÉTODO UPDATECHUNKS OTIMIZADO
    // ===================================================================================
    private void UpdateChunks()
    {
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

            // A. REMOVER CHUNKS DISTANTES (Active -> Pool)
            _tempToRemove.Clear();
            foreach (var kv in activeChunks)
            {
                if (!_tempNeededCoords.Contains(kv.Key))
                {
                    _tempToRemove.Add(kv.Key);
                }
            }

            for (int i = 0; i < _tempToRemove.Count; i++)
            {
                Vector2Int coord = _tempToRemove[i];
                if (activeChunks.TryGetValue(coord, out Chunk chunk))
                {
                    chunk.ResetChunk();
                    chunkPool.Enqueue(chunk);
                    activeChunks.Remove(coord);
                }
            }

            // B. LIMPAR PENDENTES DESNECESSÁRIOS
            for (int i = pendingChunks.Count - 1; i >= 0; i--)
            {
                if (!_tempNeededCoords.Contains(pendingChunks[i].coord))
                {
                    pendingChunks.RemoveAt(i);
                }
            }

            // C. ENCONTRAR NOVOS CHUNKS PARA GERAR
            foreach (Vector2Int coord in _tempNeededCoords)
            {
                if (!activeChunks.ContainsKey(coord))
                {
                    bool alreadyPending = false;
                    for (int i = 0; i < pendingChunks.Count; i++)
                    {
                        if (pendingChunks[i].coord == coord)
                        {
                            alreadyPending = true;
                            break;
                        }
                    }

                    if (!alreadyPending)
                    {
                        float dx = coord.x - currentChunkCoord.x;
                        float dz = coord.y - currentChunkCoord.y;
                        float distSq = dx * dx + dz * dz;
                        pendingChunks.Add((coord, distSq));
                    }
                }
            }

            // D. REORDENAR A FILA
            for (int i = 0; i < pendingChunks.Count; i++)
            {
                var item = pendingChunks[i];
                float dx = item.coord.x - currentChunkCoord.x;
                float dz = item.coord.y - currentChunkCoord.y;
                pendingChunks[i] = (item.coord, dx * dx + dz * dz);
            }

            pendingChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        }

        // 3. PROCESSAR A FILA DE CRIAÇÃO
        if (pendingChunks.Count > 0)
        {
            int processed = 0;
            bool jobsCongested = pendingMeshes.Count > maxChunksPerFrame * 2;

            if (!jobsCongested)
            {
                while (processed < maxChunksPerFrame && pendingChunks.Count > 0)
                {
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
        Chunk chunk = (chunkPool.Count > 0) ? chunkPool.Dequeue() :
                  Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform).GetComponent<Chunk>();

        Material[] matsForChunk = (Material != null && Material.Length >= 3) ?
            new Material[] { Material[0], Material[1], Material[2] } :
            new Material[] { (Material.Length > 0 ? Material[0] : null),
                     (Material.Length > 1 ? Material[1] : Material[0]),
                     (Material.Length > 2 ? Material[2] : Material[0]) };

        chunk.SetMaterials(matsForChunk);

        Vector3 pos = new Vector3(coord.x * Chunk.SizeX, 0, coord.y * Chunk.SizeZ);
        chunk.transform.position = pos;

        chunk.SetCoord(coord);

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        if (!chunk.voxelData.IsCreated)
        {
            chunk.voxelData = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);
            chunk.hasVoxelData = false;
        }

        activeChunks.Add(coord, chunk);

        // --- BUILD edits FOR THIS CHUNK ---
        var editsList = new List<MeshGenerator.BlockEdit>();

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        int extend = 1;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        foreach (var kv in blockOverrides)
        {
            Vector3Int wp = kv.Key;
            if (wp.x >= minX && wp.x <= maxX && wp.z >= minZ && wp.z <= maxZ && wp.y >= 0 && wp.y < Chunk.SizeY)
            {
                MeshGenerator.BlockEdit be = new MeshGenerator.BlockEdit
                {
                    x = wp.x,
                    y = wp.y,
                    z = wp.z,
                    type = (int)kv.Value
                };
                editsList.Add(be);
            }
        }

        NativeArray<MeshGenerator.BlockEdit> nativeEdits;
        if (editsList.Count > 0)
        {
            nativeEdits = new NativeArray<MeshGenerator.BlockEdit>(editsList.Count, Allocator.Persistent);
            for (int i = 0; i < editsList.Count; i++) nativeEdits[i] = editsList[i];
        }
        else
        {
            nativeEdits = new NativeArray<MeshGenerator.BlockEdit>(0, Allocator.Persistent);
        }

        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord, treeSettings);

        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);
        int borderSize = treeSettings.canopyRadius + 2;
        int maxTreeRadius = treeSettings.canopyRadius;


        // === INÍCIO DA INJEÇÃO DA LUZ GLOBAL ===
        int voxelSizeX = Chunk.SizeX + 2; // +2 porque o border agora é sempre 1
        int voxelSizeZ = Chunk.SizeZ + 2;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;

        NativeArray<byte> chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.TempJob);

        for (int y = 0; y < Chunk.SizeY; y++)
        {
            for (int z = -1; z <= Chunk.SizeZ; z++)
            {
                for (int x = -1; x <= Chunk.SizeX; x++)
                {
                    Vector3Int wp = new Vector3Int(chunkMinX + x, y, chunkMinZ + z);
                    globalLightMap.TryGetValue(wp, out byte l); // Procura no dicionário infinito

                    int idx = (x + 1) + y * voxelSizeX + (z + 1) * voxelPlaneSize;
                    chunkLightData[idx] = l;
                }
            }
        }

        if (chunk.jobScheduled)
        {
            chunk.currentJob.Complete();
            chunk.jobScheduled = false;
        }

        MeshGenerator.ScheduleMeshJob(
             coord,
             noiseLayers,
             warpLayers,
             caveLayers,
             blockData.mappings,
             baseHeight,
             offsetX,
             offsetZ,
             atlasTilesX,
             atlasTilesY,
             true,
             seaLevel,
             caveThreshold,
             caveStride,
             maxCaveDepthMultiplier,
             caveRarityScale,
             caveRarityThreshold,
             caveMaskSmoothness,
             nativeEdits,
             nativeTrees,
             treeMargin,
             // int borderSize FOI REMOVIDO DAQUI 
             maxTreeRadius,
             CliffTreshold,
             chunk.voxelData,
             chunkLightData, // <--- ADICIONAMOS A LUZ AQUI
             out JobHandle handle,
             out NativeList<Vector3> vertices,
             out NativeList<int> opaqueTriangles,
             out NativeList<int> transparentTriangles,
             out NativeList<int> waterTriangles,
             out NativeList<Vector2> uvs,
             out NativeList<Vector2> uv2,
             out NativeList<Vector3> normals,
             out NativeList<byte> vertexLights,
             out NativeList<byte> tintFlags,
             out NativeList<byte> vertexAO,
             out NativeList<Vector4> extraUVs
         );

        pendingMeshes.Add(new PendingMesh
        {
            chunk = chunk,
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            transparentTriangles = transparentTriangles,
            waterTriangles = waterTriangles,
            uvs = uvs,
            uv2 = uv2,
            normals = normals,
            lightValues = vertexLights,
            edits = nativeEdits,
            trees = nativeTrees,
            coord = coord,
            expectedGen = expectedGen,
            tintFlags = tintFlags,
            vertexAO = vertexAO,
            extraUVs = extraUVs,
            meshLightData = chunkLightData // <--- GUARDE AQUI PARA O DISPOSE OCORRER NO FINAL
        });
        chunk.currentJob = handle;
        chunk.jobScheduled = true;
    }

    private void RequestChunkRebuild(Vector2Int coord)
    {
        if (IsChunkJobPending(coord))
            return;

        if (!activeChunks.TryGetValue(coord, out Chunk chunk)) return;

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;
        chunk.hasVoxelData = false;

        var editsList = new List<MeshGenerator.BlockEdit>();

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        int extend = 1;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        foreach (var kv in blockOverrides)
        {
            Vector3Int wp = kv.Key;
            if (wp.x >= minX && wp.x <= maxX && wp.z >= minZ && wp.z <= maxZ && wp.y >= 0 && wp.y < Chunk.SizeY)
            {
                MeshGenerator.BlockEdit be = new MeshGenerator.BlockEdit
                {
                    x = wp.x,
                    y = wp.y,
                    z = wp.z,
                    type = (int)kv.Value
                };
                editsList.Add(be);
            }
        }

        NativeArray<MeshGenerator.BlockEdit> nativeEdits;
        if (editsList.Count > 0)
        {
            nativeEdits = new NativeArray<MeshGenerator.BlockEdit>(editsList.Count, Allocator.Persistent);
            for (int i = 0; i < editsList.Count; i++) nativeEdits[i] = editsList[i];
        }
        else
        {
            nativeEdits = new NativeArray<MeshGenerator.BlockEdit>(0, Allocator.Persistent);
        }

        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord, treeSettings);
        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);
        int borderSize = treeSettings.canopyRadius + 2;
        int maxTreeRadius = treeSettings.canopyRadius;

        // === INÍCIO DA INJEÇÃO DA LUZ GLOBAL ===
        int voxelSizeX = Chunk.SizeX + 2; // +2 porque o border agora é sempre 1
        int voxelSizeZ = Chunk.SizeZ + 2;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;

        NativeArray<byte> chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.TempJob);

        for (int y = 0; y < Chunk.SizeY; y++)
        {
            for (int z = -1; z <= Chunk.SizeZ; z++)
            {
                for (int x = -1; x <= Chunk.SizeX; x++)
                {
                    Vector3Int wp = new Vector3Int(chunkMinX + x, y, chunkMinZ + z);
                    globalLightMap.TryGetValue(wp, out byte l); // Procura no dicionário infinito

                    int idx = (x + 1) + y * voxelSizeX + (z + 1) * voxelPlaneSize;
                    chunkLightData[idx] = l;
                }
            }
        }
        if (chunk.jobScheduled)
        {
            chunk.currentJob.Complete();
            chunk.jobScheduled = false;
        }

        // Chamar o gerador (Note a remoção do borderSize e a adição do chunkLightData)
        MeshGenerator.ScheduleMeshJob(
            coord,
            noiseLayers,
            warpLayers,
            caveLayers,
            blockData.mappings,
            baseHeight,
            offsetX,
            offsetZ,
            atlasTilesX,
            atlasTilesY,
            true,
            seaLevel,
            caveThreshold,
            caveStride,
            maxCaveDepthMultiplier,
            caveRarityScale,
            caveRarityThreshold,
            caveMaskSmoothness,
            nativeEdits,
            nativeTrees,
            treeMargin,
            // int borderSize FOI REMOVIDO DAQUI 
            maxTreeRadius,
            CliffTreshold,
            chunk.voxelData,
            chunkLightData, // <--- ADICIONAMOS A LUZ AQUI
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> transparentTriangles,
            out NativeList<int> waterTriangles,
            out NativeList<Vector2> uvs,
            out NativeList<Vector2> uv2,
            out NativeList<Vector3> normals,
            out NativeList<byte> vertexLights,
            out NativeList<byte> tintFlags,
            out NativeList<byte> vertexAO,
            out NativeList<Vector4> extraUVs
        );

        pendingMeshes.Add(new PendingMesh
        {
            chunk = chunk,
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            transparentTriangles = transparentTriangles,
            waterTriangles = waterTriangles,
            uvs = uvs,
            uv2 = uv2,
            normals = normals,
            lightValues = vertexLights,
            edits = nativeEdits,
            trees = nativeTrees,
            coord = coord,
            expectedGen = expectedGen,
            tintFlags = tintFlags,
            vertexAO = vertexAO,
            extraUVs = extraUVs,
            meshLightData = chunkLightData // <--- GUARDE AQUI PARA O DISPOSE OCORRER NO FINAL
        });

        chunk.currentJob = handle;
        chunk.jobScheduled = true;
    }

    private bool IsChunkJobPending(Vector2Int coord)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
            if (pendingMeshes[i].coord == coord)
                return true;

        return false;
    }

    // ===================================================================================
    // LÓGICA DE MODIFICAÇÃO DE BLOCOS (CORRIGIDA PARA VIZINHOS)
    // ===================================================================================

    // public void SetBlockAt(Vector3Int worldPos, BlockType type)
    // {
    //     BlockType current = GetBlockAt(worldPos);

    //     if (current == BlockType.Bedrock)
    //     {
    //         Debug.Log("Attempt to modify Bedrock ignored: " + worldPos);
    //         return;
    //     }

    //     blockOverrides[worldPos] = type;

    //     Vector2Int chunkCoord = new Vector2Int(
    //         Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
    //         Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
    //     );

    //     if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.hasVoxelData)
    //     {
    //         int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
    //         int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
    //         int ly = worldPos.y;

    //         if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
    //         {
    //             int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
    //             chunk.voxelData[idx] = (byte)type;
    //         }
    //     }

    //     // 1. Reconstrói o chunk atual
    //     if (activeChunks.ContainsKey(chunkCoord))
    //         RequestChunkRebuild(chunkCoord);

    //     // 2. Verifica as bordas e pede atualização dos vizinhos se necessário
    //     int localX = worldPos.x - chunkCoord.x * Chunk.SizeX;
    //     int localZ = worldPos.z - chunkCoord.y * Chunk.SizeZ;

    //     // Borda Esquerda (localX == 0) -> Atualiza Vizinho da Esquerda (X - 1)
    //     if (localX == 0) RequestNeighborUpdate(chunkCoord.x - 1, chunkCoord.y);

    //     // Borda Direita (localX == 15) -> Atualiza Vizinho da Direita (X + 1)
    //     if (localX == Chunk.SizeX - 1) RequestNeighborUpdate(chunkCoord.x + 1, chunkCoord.y);

    //     // Borda Trás (localZ == 0) -> Atualiza Vizinho de Trás (Z - 1)
    //     if (localZ == 0) RequestNeighborUpdate(chunkCoord.x, chunkCoord.y - 1);

    //     // Borda Frente (localZ == 15) -> Atualiza Vizinho da Frente (Z + 1)
    //     if (localZ == Chunk.SizeZ - 1) RequestNeighborUpdate(chunkCoord.x, chunkCoord.y + 1);
    // }

    public void SetBlockAt(Vector3Int worldPos, BlockType type)
    {
        // 1. Obtém o bloco atual para não fazer trabalho desnecessário e checar restrições
        BlockType current = GetBlockAt(worldPos);
        if (current == BlockType.Bedrock)
        {
            Debug.Log("Tentativa de modificar Bedrock ignorada: " + worldPos);
            return;
        }

        if (current == type) return; // Nenhuma mudança ocorreu

        // 2. Registo da edição física do bloco no dicionário
        blockOverrides[worldPos] = type;

        // 3. Determina quais chunks precisam ser reconstruídos apenas por causa da mudança geométrica
        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        HashSet<Vector2Int> chunksToRebuild = new HashSet<Vector2Int>();
        chunksToRebuild.Add(chunkCoord);

        // Verifica se a mudança foi nas beiradas para atualizar as faces dos chunks vizinhos
        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);

        if (localX == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.left);
        if (localX == Chunk.SizeX - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.right);
        if (localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.down);
        if (localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.up);

        // 4. Tratar iluminação dinâmica (Caminho B)
        byte newEmission = GetBlockEmission(type);
        byte oldEmission = GetBlockEmission(current);

        if (newEmission > 0)
        {
            // O jogador colocou um bloco luminoso novo ou mais forte (Propaga a luz)
            PropagateLightGlobal(worldPos, newEmission);
        }
        else if (oldEmission > 0 || globalLightMap.ContainsKey(worldPos))
        {
            // O jogador quebrou uma luz OU colocou um bloco sólido onde antes passava luz (Apaga/Recalcula)
            RemoveLightGlobal(worldPos);
        }

        // 5. Garantir que os chunks afetados geometricamente sejam reconstruídos
        // (Nota: Propagate e Remove já chamam RequestChunkRebuild internamente para a luz, 
        // mas este loop garante que a geometria vizinha não fique com "buracos" visuais).
        foreach (Vector2Int coord in chunksToRebuild)
        {
            RequestChunkRebuild(coord);
        }
    }
    private void RequestNeighborUpdate(int cx, int cz)
    {
        Vector2Int coord = new Vector2Int(cx, cz);
        // Usa activeChunks e o método de rebuild existente
        if (activeChunks.ContainsKey(coord))
        {
            RequestChunkRebuild(coord);
        }
    }

    // ===================================================================================
    // HELPER PARA DADOS DE VIZINHOS (PADDED DATA)
    // Use isso no MeshGenerator para resolver o AO na borda
    // ===================================================================================
    public NativeArray<byte> GetPaddedVoxelData(int chunkX, int chunkZ)
    {
        int sizeX = Chunk.SizeX;
        int sizeY = Chunk.SizeY;
        int sizeZ = Chunk.SizeZ;

        int padX = sizeX + 2;
        int padZ = sizeZ + 2;

        NativeArray<byte> paddedData = new NativeArray<byte>(padX * sizeY * padZ, Allocator.TempJob);

        for (int z = -1; z < sizeZ + 1; z++)
        {
            for (int x = -1; x < sizeX + 1; x++)
            {
                int currentCX = chunkX;
                int currentCZ = chunkZ;
                int readX = x;
                int readZ = z;

                if (x < 0) { currentCX--; readX = sizeX - 1; }
                else if (x >= sizeX) { currentCX++; readX = 0; }

                if (z < 0) { currentCZ--; readZ = sizeZ - 1; }
                else if (z >= sizeZ) { currentCZ++; readZ = 0; }

                if (activeChunks.TryGetValue(new Vector2Int(currentCX, currentCZ), out Chunk c))
                {
                    if (c.hasVoxelData)
                    {
                        for (int y = 0; y < sizeY; y++)
                        {
                            int srcIdx = readX + readZ * sizeX + y * sizeX * sizeZ;
                            int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                            paddedData[dstIdx] = c.voxelData[srcIdx];
                        }
                        continue;
                    }
                }

                for (int y = 0; y < sizeY; y++)
                {
                    int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                    paddedData[dstIdx] = 0;
                }
            }
        }

        return paddedData;
    }

    // ===================================================================================
    // GETTERS & UTILS
    // ===================================================================================

    public BlockType GetBlockAt(Vector3Int worldPos)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY) return BlockType.Air;
        if (worldPos.y <= 2) return BlockType.Bedrock;

        if (blockOverrides.TryGetValue(worldPos, out BlockType overridden))
            return overridden;

        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.hasVoxelData)
        {
            int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
            int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
            int ly = worldPos.y;

            if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
            {
                int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
                return (BlockType)chunk.voxelData[idx];
            }
        }

        // Se chunk não estiver gerado ou sem dados, fallback para geração procedural
        NativeArray<MeshGenerator.TreeInstance> trees = BuildTreeInstancesForChunk(chunkCoord, treeSettings);
        BlockType treeBlockFound = BlockType.Air;

        try
        {
            for (int i = 0; i < trees.Length; i++)
            {
                var t = trees[i];

                if (GetSurfaceBlockType(t.worldX, t.worldZ) != BlockType.Grass)
                    continue;

                int baseY = GetSurfaceHeight(t.worldX, t.worldZ);
                int trunkTop = baseY + t.trunkHeight;

                if (worldPos.x == t.worldX && worldPos.z == t.worldZ &&
                    worldPos.y > baseY && worldPos.y <= trunkTop)
                {
                    treeBlockFound = BlockType.Log;
                    break;
                }

                int canopyStartY = trunkTop - t.canopyHeight + 1;
                int canopyEndY = trunkTop + 1;

                if (worldPos.y >= canopyStartY && worldPos.y <= canopyEndY)
                {
                    int dx = worldPos.x - t.worldX;
                    int dz = worldPos.z - t.worldZ;
                    if (dx * dx + dz * dz <= t.canopyRadius * t.canopyRadius)
                    {
                        treeBlockFound = BlockType.Leaves;
                        break;
                    }
                }
            }
        }
        finally
        {
            trees.Dispose();
        }

        if (treeBlockFound != BlockType.Air)
            return treeBlockFound;

        bool isCave = false;
        int surfaceHeight = GetSurfaceHeight(worldX, worldZ);

        if (caveLayers != null && caveLayers.Length > 0)
        {
            int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (worldPos.y <= maxCaveY)
            {
                int border = treeSettings.canopyRadius + 2;

                int chunkMinX = chunkCoord.x * Chunk.SizeX;
                int chunkMinZ = chunkCoord.y * Chunk.SizeZ;

                int minWorldX = chunkMinX - border;
                int minWorldZ = chunkMinZ - border;
                int minWorldY = 0;

                int stride = math.max(1, caveStride);

                int cx0 = FloorDiv(worldX - minWorldX, stride);
                int cy0 = FloorDiv(worldPos.y - minWorldY, stride);
                int cz0 = FloorDiv(worldZ - minWorldZ, stride);

                int lowWX = minWorldX + cx0 * stride;
                int highWX = lowWX + stride;

                int lowWY = minWorldY + cy0 * stride;
                int highWY = lowWY + stride;

                int lowWZ = minWorldZ + cz0 * stride;
                int highWZ = lowWZ + stride;

                float c000 = ComputeCaveNoise(lowWX, lowWY, lowWZ);
                float c100 = ComputeCaveNoise(highWX, lowWY, lowWZ);
                float c010 = ComputeCaveNoise(lowWX, highWY, lowWZ);
                float c110 = ComputeCaveNoise(highWX, highWY, lowWZ);
                float c001 = ComputeCaveNoise(lowWX, lowWY, highWZ);
                float c101 = ComputeCaveNoise(highWX, lowWY, highWZ);
                float c011 = ComputeCaveNoise(lowWX, highWY, highWZ);
                float c111 = ComputeCaveNoise(highWX, highWY, highWZ);

                float fx = (float)(worldX - lowWX) / stride;
                float fy = (float)(worldPos.y - lowWY) / stride;
                float fz = (float)(worldZ - lowWZ) / stride;

                float x00 = Mathf.Lerp(c000, c100, fx);
                float x01 = Mathf.Lerp(c001, c101, fx);
                float x10 = Mathf.Lerp(c010, c110, fx);
                float x11 = Mathf.Lerp(c011, c111, fx);

                float z0 = Mathf.Lerp(x00, x01, fz);
                float z1 = Mathf.Lerp(x10, x11, fz);

                float interpolatedCave = Mathf.Lerp(z0, z1, fy);

                float surfaceBias = 0.001f * ((float)worldPos.y / math.max(1f, (float)surfaceHeight));
                if (worldPos.y < 5) surfaceBias -= 0.08f;

                float adjustedThreshold = caveThreshold - surfaceBias;

                if (interpolatedCave > adjustedThreshold)
                    isCave = true;
            }
        }

        if (isCave)
            return BlockType.Air;

        if (worldPos.y > surfaceHeight)
        {
            return (worldPos.y <= seaLevel) ? BlockType.Water : BlockType.Air;
        }
        else
        {
            bool isBeachArea = (surfaceHeight <= seaLevel + 2);
            bool isCliff = IsCliff(worldX, worldZ, CliffTreshold);
            int mountainStoneHeight = baseHeight + 70;
            bool isHighMountain = surfaceHeight >= mountainStoneHeight;

            if (worldPos.y == surfaceHeight)
            {
                if (isHighMountain)
                {
                    return BlockType.Stone;
                }
                else if (isCliff)
                {
                    return BlockType.Stone;
                }
                else
                {
                    return isBeachArea ? BlockType.Sand : BlockType.Grass;
                }
            }
            else if (worldPos.y > surfaceHeight - 4)
            {
                if (isCliff)
                {
                    return BlockType.Stone;
                }
                else
                {
                    return isBeachArea ? BlockType.Sand : BlockType.Dirt;
                }
            }
            else if (worldPos.y <= 2)
            {
                return BlockType.Bedrock;
            }
            else if (worldPos.y > surfaceHeight - 50)
            {
                return BlockType.Stone;
            }
            else
            {
                return BlockType.Deepslate;
            }
        }
    }
    private float ComputeCaveNoise(int wx, int wy, int wz)
    {
        float totalCave = 0f;
        float sumCaveAmp = 0f;

        for (int i = 0; i < caveLayers.Length; i++)
        {
            // ... (seu código de loop atual continua igual aqui dentro)
            var layer = caveLayers[i];
            if (!layer.enabled) continue;

            float nx = wx + layer.offset.x;
            float ny = (float)wy;
            float nz = wz + layer.offset.y;

            float n1 = MyNoise.OctavePerlin3D(nx, ny, nz, layer) * 2f - 1f;
            float n2 = MyNoise.OctavePerlin3D(nx + 128.5f, ny + 256.1f, nz + 64.3f, layer) * 2f - 1f;

            float tubeDistSq = (n1 * n1) + (n2 * n2);
            float tubeCave = Mathf.Max(0f, 1f - (tubeDistSq * 5f));

            float cheeseCave = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
            if (layer.redistributionModifier != 1f || layer.exponent != 1f)
            {
                cheeseCave = MyNoise.Redistribution(cheeseCave, layer.redistributionModifier, layer.exponent);
            }

            float finalSample = Mathf.Max(tubeCave, cheeseCave * 0.45f);

            totalCave += finalSample * layer.amplitude;
            sumCaveAmp += math.max(1e-5f, layer.amplitude);
        }

        float baseCaveResult = (sumCaveAmp > 0f) ? totalCave / sumCaveAmp : 0f;

        // === NOVA LÓGICA DA MÁSCARA AQUI ===
        float maskNx = (wx + offsetX) / caveRarityScale;
        float maskNy = wy / caveRarityScale;
        float maskNz = (wz + offsetZ) / caveRarityScale;

        // cnoise retorna valores entre aproximadamente -1 e 1
        float maskVal = noise.cnoise(new float3(maskNx, maskNy, maskNz));
        float maskWeight = Mathf.Clamp01((maskVal - caveRarityThreshold) * caveMaskSmoothness);

        return baseCaveResult * maskWeight;
    }
    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        float warpX = 0f;
        float warpZ = 0f;
        float sumWarpAmp = 0f;
        if (warpLayers != null)
        {
            for (int i = 0; i < warpLayers.Length; i++)
            {
                var layer = warpLayers[i];
                if (!layer.enabled) continue;

                float baseNx = worldX + layer.offset.x;
                float baseNz = worldZ + layer.offset.y;

                float sampleX = MyNoise.OctavePerlin(baseNx + 100f, baseNz, layer);
                float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);

                warpX += (sampleX * 2f - 1f) * layer.amplitude;
                warpZ += (sampleZ * 2f - 1f) * layer.amplitude;
                sumWarpAmp += layer.amplitude;
            }
        }
        if (sumWarpAmp > 0f)
        {
            warpX /= sumWarpAmp;
            warpZ /= sumWarpAmp;
        }
        warpX = (warpX - 0.5f) * 2f;
        warpZ = (warpZ - 0.5f) * 2f;

        float totalNoise = 0f;
        float sumAmp = 0f;
        bool hasActiveLayers = false;
        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
            {
                var layer = noiseLayers[i];
                if (!layer.enabled) continue;

                hasActiveLayers = true;

                float nx = (worldX + warpX) + layer.offset.x;
                float nz = (worldZ + warpZ) + layer.offset.y;

                float sample = MyNoise.OctavePerlin(nx, nz, layer);

                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                {
                    sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                }

                totalNoise += sample * layer.amplitude;
                sumAmp += math.max(1e-5f, layer.amplitude);
            }
        }

        if (!hasActiveLayers || sumAmp <= 0f)
        {
            float nx = (worldX + warpX) * 0.05f + offsetX;
            float nz = (worldZ + warpZ) * 0.05f + offsetZ;
            totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
            sumAmp = 1f;
        }

        return GetHeightFromNoise(totalNoise, sumAmp);
    }

    private NativeArray<MeshGenerator.TreeInstance> BuildTreeInstancesForChunk(Vector2Int coord, TreeSettings settings)
    {
        int cellSize = Mathf.Max(1, settings.minSpacing);
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        int searchMargin = settings.canopyRadius + settings.minSpacing;
        int cellX0 = Mathf.FloorToInt((float)(chunkMinX - searchMargin) / cellSize);
        int cellX1 = Mathf.FloorToInt((float)(chunkMaxX + searchMargin) / cellSize);
        int cellZ0 = Mathf.FloorToInt((float)(chunkMinZ - searchMargin) / cellSize);
        int cellZ1 = Mathf.FloorToInt((float)(chunkMaxZ + searchMargin) / cellSize);

        float freq = settings.noiseScale;

        List<MeshGenerator.TreeInstance> tmp = new List<MeshGenerator.TreeInstance>();

        for (int cx = cellX0; cx <= cellX1; cx++)
        {
            for (int cz = cellZ0; cz <= cellZ1; cz++)
            {
                float noiseX = (cx * 12.9898f + settings.seed) * freq;
                float noiseZ = (cz * 78.233f + settings.seed) * freq;
                float sample = Mathf.PerlinNoise(noiseX, noiseZ);

                if (sample > settings.density) continue;

                int worldX = cx * cellSize + Mathf.RoundToInt(Mathf.PerlinNoise(noiseX + 1f, noiseZ + 1f) * (cellSize - 1));
                int worldZ = cz * cellSize + Mathf.RoundToInt(Mathf.PerlinNoise(noiseX + 2f, noiseZ + 2f) * (cellSize - 1));

                if (worldX < chunkMinX - searchMargin || worldX > chunkMaxX + searchMargin ||
                    worldZ < chunkMinZ - searchMargin || worldZ > chunkMaxZ + searchMargin) continue;

                int surfaceY = GetSurfaceHeight(worldX, worldZ);
                if (surfaceY <= 0 || surfaceY >= Chunk.SizeY) continue;

                BlockType groundType = GetSurfaceBlockType(worldX, worldZ);
                if (groundType != BlockType.Grass && groundType != BlockType.Dirt) continue;
                if (IsCliff(worldX, worldZ, CliffTreshold)) continue;

                float heightNoise = Mathf.PerlinNoise((worldX + 0.1f) * 0.137f + settings.seed * 0.001f, (worldZ + 0.1f) * 0.243f + settings.seed * 0.001f);
                int trunkH = settings.minHeight + Mathf.RoundToInt(heightNoise * (settings.maxHeight - settings.minHeight + 1));

                tmp.Add(new MeshGenerator.TreeInstance
                {
                    worldX = worldX,
                    worldZ = worldZ,
                    trunkHeight = trunkH,
                    canopyRadius = settings.canopyRadius,
                    canopyHeight = settings.canopyHeight
                });
            }
        }

        var arr = new NativeArray<MeshGenerator.TreeInstance>(tmp.Count, Allocator.Persistent);
        for (int i = 0; i < tmp.Count; i++) arr[i] = tmp[i];
        return arr;
    }

    private int GetHeightFromNoise(float noise, float sumAmp)
    {
        float centered = noise - sumAmp * 0.5f;
        return math.clamp(baseHeight + (int)math.floor(centered), 1, Chunk.SizeY - 1);
    }

    private BlockType GetSurfaceBlockType(int worldX, int worldZ)
    {
        int h = GetSurfaceHeight(worldX, worldZ);
        bool isBeachArea = (h <= seaLevel + 2);
        bool isCliff = IsCliff(worldX, worldZ, CliffTreshold);
        int mountainStoneHeight = baseHeight + 70;
        bool isHighMountain = h >= mountainStoneHeight;

        if (isHighMountain)
        {
            return BlockType.Stone;
        }
        else if (isCliff)
        {
            return BlockType.Stone;
        }
        else
        {
            return isBeachArea ? BlockType.Sand : BlockType.Grass;
        }
    }

    private bool IsCliff(int worldX, int worldZ, int threshold = 2)
    {
        int h = GetSurfaceHeight(worldX, worldZ);

        int hN = GetSurfaceHeight(worldX, worldZ + 1);
        int hS = GetSurfaceHeight(worldX, worldZ - 1);
        int hE = GetSurfaceHeight(worldX + 1, worldZ);
        int hW = GetSurfaceHeight(worldX - 1, worldZ);

        int maxDiff = 0;
        maxDiff = math.max(maxDiff, math.abs(h - hN));
        maxDiff = math.max(maxDiff, math.abs(h - hS));
        maxDiff = math.max(maxDiff, math.abs(h - hE));
        maxDiff = math.max(maxDiff, math.abs(h - hW));

        return maxDiff >= threshold;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
        return q;
    }


    // ===================================================================================
    // SISTEMA DE ILUMINAÇÃO GLOBAL (BFS Desacoplado)
    // ===================================================================================

    public byte GetBlockOpacity(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return blockData.mappings[t].lightOpacity;
        }
        return 15;
    }

    public byte GetBlockEmission(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return blockData.mappings[t].lightEmission;
        }
        return 0;
    }

    public void PropagateLightGlobal(Vector3Int startWorldPos, byte lightEmission)
    {
        Queue<Vector3Int> lightQueue = new Queue<Vector3Int>();
        lightQueue.Enqueue(startWorldPos);

        globalLightMap[startWorldPos] = lightEmission;
        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        while (lightQueue.Count > 0)
        {
            Vector3Int node = lightQueue.Dequeue();
            globalLightMap.TryGetValue(node, out byte currentLight);

            Vector2Int chunkCoord = new Vector2Int(
                Mathf.FloorToInt((float)node.x / Chunk.SizeX),
                Mathf.FloorToInt((float)node.z / Chunk.SizeZ)
            );
            dirtiedChunks.Add(chunkCoord);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;

                // Limite vertical básico (SizeY)
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                // Aqui lê-se o bloco diretamente
                // (Nota: Certifique-se que o seu método GetBlockAt existe e retorna o bloco correto do mundo)
                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);

                if (opacity >= 15) continue; // Bloqueado por parede sólida

                globalLightMap.TryGetValue(neighborPos, out byte neighborLight);
                byte cost = (opacity < 1) ? (byte)1 : opacity;
                byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                if (candidateLight > neighborLight)
                {
                    globalLightMap[neighborPos] = candidateLight;
                    lightQueue.Enqueue(neighborPos);
                }
            }
        }

        // Reconstrói todos os chunks afetados pela luz
        foreach (Vector2Int coord in dirtiedChunks)
        {
            RequestChunkRebuild(coord);
        }
    }

    public void RemoveLightGlobal(Vector3Int startWorldPos)
    {
        if (!globalLightMap.TryGetValue(startWorldPos, out byte oldLight)) return;

        Queue<(Vector3Int pos, byte lightLevel)> darkQueue = new Queue<(Vector3Int, byte)>();
        Queue<Vector3Int> refillQueue = new Queue<Vector3Int>();
        HashSet<Vector2Int> dirtiedChunks = new HashSet<Vector2Int>();

        darkQueue.Enqueue((startWorldPos, oldLight));
        globalLightMap[startWorldPos] = 0;

        // Passo 1: Apagar luz recursivamente até encontrar fontes de luz mais fortes
        while (darkQueue.Count > 0)
        {
            var node = darkQueue.Dequeue();
            Vector2Int chunkCoord = new Vector2Int(Mathf.FloorToInt((float)node.pos.x / Chunk.SizeX), Mathf.FloorToInt((float)node.pos.z / Chunk.SizeZ));
            dirtiedChunks.Add(chunkCoord);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node.pos + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                if (globalLightMap.TryGetValue(neighborPos, out byte neighborLight) && neighborLight > 0)
                {
                    if (neighborLight < node.lightLevel)
                    {
                        globalLightMap[neighborPos] = 0;
                        darkQueue.Enqueue((neighborPos, neighborLight));
                    }
                    else if (neighborLight >= node.lightLevel)
                    {
                        refillQueue.Enqueue(neighborPos); // Guardar para preencher o vazio
                    }
                }
            }
        }

        // Passo 2: Re-propagar luz das bordas que sobreviveram
        while (refillQueue.Count > 0)
        {
            Vector3Int node = refillQueue.Dequeue();
            if (globalLightMap.TryGetValue(node, out byte currentLight))
            {
                foreach (Vector3Int dir in sixDirections)
                {
                    Vector3Int neighborPos = node + dir;
                    if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                    BlockType neighborBlock = GetBlockAt(neighborPos);
                    byte opacity = GetBlockOpacity(neighborBlock);
                    if (opacity >= 15) continue;

                    globalLightMap.TryGetValue(neighborPos, out byte neighborLight);
                    byte cost = (opacity < 1) ? (byte)1 : opacity;
                    byte candidateLight = (byte)Mathf.Max(0, currentLight - cost);

                    if (candidateLight > neighborLight)
                    {
                        globalLightMap[neighborPos] = candidateLight;
                        refillQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        foreach (Vector2Int coord in dirtiedChunks) RequestChunkRebuild(coord);
    }

}