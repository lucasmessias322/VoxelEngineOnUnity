// VoxelWorld.cs
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[System.Serializable]
public class OreConfig
{
    public BlockType oreBlock;    // tipo do minério (ex.: BlockType.IronOre)
    public int minHeight;         // altura mínima de geração
    public int maxHeight;         // altura máxima de geração
    [Range(0f, 1f)] public float rarity; // chance por bloco dentro da faixa
    public int veinSize;          // tamanho médio da veia
    public int seedOffset;        // offset para evitar overlap entre minérios
}

/// <summary>
/// VoxelWorld — gera/streaming de chunks (versão com suporte a submeshes para água).
/// Versão adaptada para Job System + Burst + NativeCollections para o meshing.
/// </summary>
public class VoxelWorld : MonoBehaviour
{
    public static VoxelWorld Instance { get; private set; }
    [Header("Biomes")]
    public Biome[] biomes;                 // configurar no inspector
    public float biomeNoiseScale = 0.005f; // escala do mapa de biomas (baixo => grandes biomas)
    public int biomeSeed = 12345;          // seed para o mapa de biomas

    [Header("Chunk params")]
    public int chunkWidth = 16;
    public int chunkHeight = 128;
    public int chunkDepth = 16;
    public float blockSize = 1f;

    [Header("Noise / world")]
    public int bedrockLayers = 1;
    public int dirtLayers = 3;

    public int seed = 0;

    public NoiseSettings noiseSettings;

    [Header("Streaming")]
    public int viewDistanceInChunks = 4;
    public int physicsRadiusInChunks = 2;

    [Header("World")]
    public int seaLevel = 16;

    [Header("References")]
    public BlockDataSO blockDataSO;
    public Material chunkMaterial; // índice 0
    public Material leafMaterial;  // índice 1 (novo)
    public Material waterMaterial; // índice 2

    public Transform player;
    public GameObject chunkPrefab;

    [Header("Tree Settings")]
    [Tooltip("Padding em blocos usado ao gerar padded blocks para suportar árvores que atravessam chunk.")]
    public int treePadding = 3;
    public int treeSeed = 54321;
    [Range(0f, 1f)] public float treeSpawnChance = 0.02f;
    public int treeMinHeight = 4;
    public int treeMaxHeight = 6;
    public int treeLeafRadius = 2;
    public BlockType woodBlock = BlockType.Placeholder;
    public BlockType leavesBlock = BlockType.Placeholder;

    [Header("Tree Height Control")]
    public bool useFixedTreeHeight = false;
    [Range(1, 64)]
    public int fixedTreeHeight = 5;

    [Header("Ore Settings")]
    public OreConfig[] oreConfigs;

    // internal
    private readonly Dictionary<Vector2Int, Chunk> activeChunks = new();
    private readonly ConcurrentQueue<MeshJobResult> meshResults = new();
    private readonly ConcurrentDictionary<Vector2Int, bool> generatingChunks = new();
    private readonly Queue<GameObject> chunkPool = new();
    private CancellationTokenSource cts;
    private ChunkGenerator chunkGenerator;
    public enum AtlasOrientation { BottomToTop = 0, TopToBottom = 1 }

    [Header("Texture Atlas")]
    [Tooltip("Como as linhas do atlas estão numeradas: BottomToTop = linha 0 é a mais baixa (origem UV do Unity), TopToBottom = linha 0 é a mais alta (estilo Minecraft).")]
    public AtlasOrientation atlasOrientation = AtlasOrientation.TopToBottom;

    // -------------------------
    // Buffers reutilizáveis (para reduzir GC no main thread)
    // Usados apenas no main thread (ProcessMeshResults)
    // -------------------------
    private List<Vector2> reusableSolidUVs = new List<Vector2>(4096);
    private List<Vector2> reusableWaterUVs = new List<Vector2>(1024);
    private List<Vector3> reusableAllVerts = new List<Vector3>(8192);
    private List<Vector2> reusableAllUVs = new List<Vector2>(8192);
    private List<Vector3> reusableTempVerts = new List<Vector3>(8192); // usado se precisar temporariamente
    // -------------------------

    private SemaphoreSlim generationSemaphore;

    [Header("Generation Limits")]
    [Tooltip("Número máximo de chunks gerando simultaneamente.")]
    public int maxConcurrentGenerations = -1; // -1 para auto (processorCount - 1)
    [Tooltip("Número máximo de novas gerações iniciadas por frame.")]
    public int maxStartsPerFrame = 4;
    [Header("Startup streaming")]
    [Tooltip("Distância inicial de view (chunks) no startup. Depois expande até viewDistanceInChunks.")]
    public int initialViewDistance = 1;
    public float expandViewSeconds = 4f; // tempo total para expandir até viewDistanceInChunks

    // --- Construtores / Start / Destroy ---
    void Awake()
    {
        Instance = this;
    }
    private int currentViewDistance;
    void Start()
    {
        if (noiseSettings == null)
        {
            Debug.Log("NoiseSettings não definido");
            return;
        }
        if (blockDataSO != null) blockDataSO.InitializeDictionary();
        cts = new CancellationTokenSource();
        if (player == null)
        {
            var p = FindObjectOfType<Camera>();
            if (p != null) player = p.transform;
        }


        chunkGenerator = new ChunkGenerator(
            biomes, biomeNoiseScale, biomeSeed, chunkWidth, chunkHeight, chunkDepth,
            bedrockLayers, dirtLayers, seed, noiseSettings, seaLevel,
            treePadding, treeSeed, treeSpawnChance, treeMinHeight, treeMaxHeight, treeLeafRadius,
            woodBlock, leavesBlock,
            useFixedTreeHeight, fixedTreeHeight
        );

        int concurrent = maxConcurrentGenerations;
        if (concurrent < 0) concurrent = SystemInfo.processorCount - 1;
        concurrent = Mathf.Max(1, concurrent);
        generationSemaphore = new SemaphoreSlim(concurrent);

        // UpdateChunksImmediate();
        currentViewDistance = initialViewDistance;
        StartCoroutine(ExpandViewDistanceCoroutine());
        StartCoroutine(InitialChunkGenerationCoroutine()); // essa coroutine usa currentViewDistance em vez do antigo viewDistanceInChunks


    }

    void OnDestroy()
    {
        cts?.Cancel();
        cts?.Dispose();
        generationSemaphore?.Dispose();
    }

    void Update()
    {
        ProcessMeshResults();
        UpdateChunksStreaming();
        UpdateChunkColliders();
    }

    // private void ProcessMeshResults()
    // {
    //     while (meshResults.TryDequeue(out var res))
    //     {
    //         // garante chunk (cria/pega do pool)
    //         if (!activeChunks.TryGetValue(res.coord, out var chunk))
    //         {
    //             chunk = GetChunkFromPool(res.coord);
    //             activeChunks[res.coord] = chunk;
    //         }

    //         // Seta o inner blocks (w,h,d) no chunk
    //         chunk.SetBlocks(res.blocks);

    //         // --- UVs: reutilizar buffers em vez de alocar ---
    //         reusableSolidUVs.Clear();
    //         EnsureCapacity(reusableSolidUVs, res.solidVertices != null ? res.solidVertices.Count : 0);
    //         // Cada face adiciona 4 UVs; res.solidFaceBlockTypes.Count é número de faces
    //         for (int qi = 0; qi < res.solidFaceBlockTypes.Count; qi++)
    //         {
    //             var bt = (BlockType)res.solidFaceBlockTypes[qi];
    //             var normal = NormalFromIndex(res.solidFaceNormals[qi]);
    //             AddUVsForFace(reusableSolidUVs, bt, normal);
    //         }

    //         reusableWaterUVs.Clear();
    //         EnsureCapacity(reusableWaterUVs, res.waterVertices != null ? res.waterVertices.Count : 0);
    //         for (int qi = 0; qi < res.waterFaceBlockTypes.Count; qi++)
    //         {
    //             var bt = (BlockType)res.waterFaceBlockTypes[qi];
    //             var normal = NormalFromIndex(res.waterFaceNormals[qi]);
    //             AddUVsForFace(reusableWaterUVs, bt, normal);
    //         }

    //         // --- Construir listas de normais por-vértice (4 cópias da normal por face) ---
    //         List<Vector3> solidNormals = null;
    //         if (res.solidFaceNormals != null && res.solidFaceNormals.Count > 0)
    //         {
    //             int faceCount = res.solidFaceNormals.Count;
    //             solidNormals = new List<Vector3>(faceCount * 4);
    //             for (int f = 0; f < faceCount; f++)
    //             {
    //                 var n = NormalFromIndex(res.solidFaceNormals[f]);
    //                 // cada face tem 4 vértices (quad)
    //                 solidNormals.Add(n);
    //                 solidNormals.Add(n);
    //                 solidNormals.Add(n);
    //                 solidNormals.Add(n);
    //             }
    //         }

    //         List<Vector3> waterNormals = null;
    //         if (res.waterFaceNormals != null && res.waterFaceNormals.Count > 0)
    //         {
    //             int faceCount = res.waterFaceNormals.Count;
    //             waterNormals = new List<Vector3>(faceCount * 4);
    //             for (int f = 0; f < faceCount; f++)
    //             {
    //                 var n = NormalFromIndex(res.waterFaceNormals[f]);
    //                 waterNormals.Add(n);
    //                 waterNormals.Add(n);
    //                 waterNormals.Add(n);
    //                 waterNormals.Add(n);
    //             }
    //         }

    //         // --- Preparar vértices/UVs reutilizando buffers (para reduzir GC) ---
    //         reusableAllVerts.Clear();
    //         int expectedVerts = (res.solidVertices?.Count ?? 0) + (res.waterVertices?.Count ?? 0);
    //         EnsureCapacity(reusableAllVerts, expectedVerts);
    //         if (res.solidVertices != null) reusableAllVerts.AddRange(res.solidVertices);
    //         if (res.waterVertices != null) reusableAllVerts.AddRange(res.waterVertices);

    //         reusableAllUVs.Clear();
    //         EnsureCapacity(reusableAllUVs, expectedVerts);
    //         if (reusableSolidUVs != null) reusableAllUVs.AddRange(reusableSolidUVs);
    //         if (reusableWaterUVs != null) reusableAllUVs.AddRange(reusableWaterUVs);

    //         // Chamamos ApplyMeshData passando também as listas de normais por-vértice
    //         chunk.ApplyMeshData(
    //             res.solidVertices, res.solidTriangles, reusableSolidUVs, solidNormals,
    //             res.waterVertices, res.waterTriangles, reusableWaterUVs, waterNormals,
    //             new Material[] { chunkMaterial, waterMaterial },
    //             res.width, res.height, res.depth, res.blockSize
    //         );

    //         // Após aplicar, podemos limpar as listas do result (opcional para liberar referência mais cedo)
    //         // Não usamos pool para resultados em background; deixamos GC cuidar — já alocamos com boa capacidade.
    //     }
    // }


    // Helper que chama AddUVsTo 1 vez por face (AddUVsTo adiciona 4 UVs)


    private void ProcessMeshResults()
    {
        while (meshResults.TryDequeue(out var res))
        {
            // garante chunk (cria/pega do pool)
            if (!activeChunks.TryGetValue(res.coord, out var chunk))
            {
                chunk = GetChunkFromPool(res.coord);
                activeChunks[res.coord] = chunk;
            }

            // Seta o inner blocks no chunk
            chunk.SetBlocks(res.blocks);

            // --- UVs ---
            // sólidos
            reusableSolidUVs.Clear();
            EnsureCapacity(reusableSolidUVs, res.solidVertices != null ? res.solidVertices.Count : 0);
            for (int qi = 0; qi < res.solidFaceBlockTypes.Count; qi++)
            {
                var bt = (BlockType)res.solidFaceBlockTypes[qi];
                var normal = NormalFromIndex(res.solidFaceNormals[qi]);
                AddUVsForFace(reusableSolidUVs, bt, normal);
            }

            // folhas
            var reusableLeafUVs = new List<Vector2>(res.leafVertices != null ? res.leafVertices.Count : 0);
            for (int qi = 0; qi < (res.leafFaceBlockTypes?.Count ?? 0); qi++)
            {
                var bt = (BlockType)res.leafFaceBlockTypes[qi];
                var normal = NormalFromIndex(res.leafFaceNormals[qi]);
                AddUVsForFace(reusableLeafUVs, bt, normal);
            }

            // água
            reusableWaterUVs.Clear();
            EnsureCapacity(reusableWaterUVs, res.waterVertices != null ? res.waterVertices.Count : 0);
            for (int qi = 0; qi < res.waterFaceBlockTypes.Count; qi++)
            {
                var bt = (BlockType)res.waterFaceBlockTypes[qi];
                var normal = NormalFromIndex(res.waterFaceNormals[qi]);
                AddUVsForFace(reusableWaterUVs, bt, normal);
            }

            // --- Normais ---
            List<Vector3> solidNormals = null;
            if (res.solidFaceNormals != null && res.solidFaceNormals.Count > 0)
            {
                int faceCount = res.solidFaceNormals.Count;
                solidNormals = new List<Vector3>(faceCount * 4);
                for (int f = 0; f < faceCount; f++)
                {
                    var n = NormalFromIndex(res.solidFaceNormals[f]);
                    solidNormals.Add(n); solidNormals.Add(n); solidNormals.Add(n); solidNormals.Add(n);
                }
            }

            List<Vector3> leafNormals = null;
            if (res.leafFaceNormals != null && res.leafFaceNormals.Count > 0)
            {
                int faceCount = res.leafFaceNormals.Count;
                leafNormals = new List<Vector3>(faceCount * 4);
                for (int f = 0; f < faceCount; f++)
                {
                    var n = NormalFromIndex(res.leafFaceNormals[f]);
                    leafNormals.Add(n); leafNormals.Add(n); leafNormals.Add(n); leafNormals.Add(n);
                }
            }

            List<Vector3> waterNormals = null;
            if (res.waterFaceNormals != null && res.waterFaceNormals.Count > 0)
            {
                int faceCount = res.waterFaceNormals.Count;
                waterNormals = new List<Vector3>(faceCount * 4);
                for (int f = 0; f < faceCount; f++)
                {
                    var n = NormalFromIndex(res.waterFaceNormals[f]);
                    waterNormals.Add(n); waterNormals.Add(n); waterNormals.Add(n); waterNormals.Add(n);
                }
            }

            // --- Escolher materiais na mesma ordem dos submeshes ---
            var mats = new List<Material>();
            mats.Add(chunkMaterial); // sólidos sempre primeiro

            bool hasLeaves = res.leafVertices != null && res.leafVertices.Count > 0;
            bool hasWater = res.waterVertices != null && res.waterVertices.Count > 0;

            if (hasLeaves) mats.Add(leafMaterial); // folhas se existirem
            if (hasWater) mats.Add(waterMaterial); // água se existir

            chunk.ApplyMeshData(
                res.solidVertices, res.solidTriangles, reusableSolidUVs, solidNormals,
                res.leafVertices, res.leafTriangles, reusableLeafUVs, leafNormals,
                res.waterVertices, res.waterTriangles, reusableWaterUVs, waterNormals,
                mats.ToArray(),
                res.width, res.height, res.depth, res.blockSize
            );



        }
    }

    private void AddUVsForFace(List<Vector2> uvList, BlockType bt, Vector3 normal)
    {
        AddUVsTo(uvList, bt, normal);
    }

    // Converte índice de normal gerado no job para Vector3
    private Vector3 NormalFromIndex(int idx)
    {
        return idx switch
        {
            0 => Vector3.forward, // +z front
            1 => Vector3.back,    // -z back
            2 => Vector3.up,      // +y top
            3 => Vector3.down,    // -y bottom
            4 => Vector3.right,   // +x right
            5 => Vector3.left,    // -x left
            _ => Vector3.forward
        };
    }

    private void UpdateChunkColliders()
    {
        Vector2Int center = GetPlayerChunk();
        foreach (var kv in activeChunks)
        {
            Vector2Int coord = kv.Key;
            int dx = Mathf.Abs(coord.x - center.x);
            int dz = Mathf.Abs(coord.y - center.y);
            float dist = Mathf.Max(dx, dz);
            var chunk = kv.Value;
            if (dist <= physicsRadiusInChunks)
                chunk.EnableColliders();
            else
                chunk.DisableColliders();
        }
    }

    private Chunk GetChunkFromPool(Vector2Int coord)
    {
        GameObject go;
        if (chunkPool.Count > 0)
        {
            go = chunkPool.Dequeue();
            go.SetActive(true);
            go.name = $"Chunk_{coord.x}_{coord.y}";
        }
        else
        {
            go = Instantiate(chunkPrefab, transform);
            go.name = $"Chunk_{coord.x}_{coord.y}";
        }

        go.transform.localPosition = new Vector3(coord.x * chunkWidth * blockSize, 0, coord.y * chunkDepth * blockSize);
        var chunk = go.GetComponent<Chunk>();
        chunk.blockDataSO = blockDataSO;
        return chunk;
    }

    private void UpdateChunksStreaming()
    {
        Vector2Int center = GetPlayerChunk();

        // Coletar chunks necessários, com distância euclidiana para priorização
        var needed = new List<(Vector2Int coord, float dist)>();
        for (int cx = -currentViewDistance; cx <= currentViewDistance; cx++)
        {
            for (int cz = -currentViewDistance; cz <= currentViewDistance; cz++)
            {
                Vector2Int coord = new Vector2Int(center.x + cx, center.y + cz);
                float distSq = cx * cx + cz * cz;
                if (distSq <= currentViewDistance * currentViewDistance)
                {
                    if (!activeChunks.ContainsKey(coord) && !generatingChunks.ContainsKey(coord))
                    {
                        needed.Add((coord, distSq));
                    }
                }
            }
        }

        // Ordenar por distância (menor primeiro)
        needed.Sort((a, b) => a.dist.CompareTo(b.dist));

        // Iniciar limitados por frame
        int started = 0;
        foreach (var (coord, _) in needed)
        {
            if (started >= maxStartsPerFrame) break;
            if (generatingChunks.TryAdd(coord, true))
            {
                _ = StartGenerateChunkAsync(coord, cts.Token);
                started++;
            }
        }

        UnloadDistantChunks(center);
    }

    private void UnloadDistantChunks(Vector2Int center)
    {
        var toRemove = new List<Vector2Int>();
        foreach (var kv in activeChunks)
        {
            Vector2Int coord = kv.Key;
            int dx = Mathf.Abs(coord.x - center.x);
            int dz = Mathf.Abs(coord.y - center.y);
            if (Mathf.Max(dx, dz) > viewDistanceInChunks) toRemove.Add(coord);
        }

        foreach (var coord in toRemove)
        {
            var chunk = activeChunks[coord];
            activeChunks.Remove(coord);
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk.gameObject);
        }
    }


    private IEnumerator ExpandViewDistanceCoroutine()
    {
        int target = viewDistanceInChunks;
        if (initialViewDistance >= target) yield break;

        float elapsed = 0f;
        while (elapsed < expandViewSeconds)
        {
            float t = elapsed / expandViewSeconds;
            currentViewDistance = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(initialViewDistance, target, t)), initialViewDistance, target);
            elapsed += Time.deltaTime;
            yield return null;
        }
        currentViewDistance = target;
    }

    // adicione isto dentro da classe VoxelWorld

    private IEnumerator InitialChunkGenerationCoroutine()
    {
        Vector2Int center = GetPlayerChunk();

        // construir lista de coords necessárias ordenadas por distância (métrica já usada)
        var needed = new List<(Vector2Int coord, float dist)>();
        for (int cx = -viewDistanceInChunks; cx <= viewDistanceInChunks; cx++)
        {
            for (int cz = -viewDistanceInChunks; cz <= viewDistanceInChunks; cz++)
            {
                Vector2Int coord = new Vector2Int(center.x + cx, center.y + cz);
                float distSq = cx * cx + cz * cz;
                if (distSq <= viewDistanceInChunks * viewDistanceInChunks)
                {
                    if (!activeChunks.ContainsKey(coord) && !generatingChunks.ContainsKey(coord))
                    {
                        needed.Add((coord, distSq));
                    }
                }
            }
        }

        needed.Sort((a, b) => a.dist.CompareTo(b.dist));

        int index = 0;
        while (index < needed.Count)
        {
            int started = 0;
            while (index < needed.Count && started < maxStartsPerFrame)
            {
                var coord = needed[index].coord;
                if (generatingChunks.TryAdd(coord, true))
                {
                    _ = StartGenerateChunkAsync(coord, cts.Token);
                    started++;
                }
                index++;
            }

            // espera um frame para liberar recursos (espalha alocação/GC)
            yield return null;
        }
    }

    Vector2Int GetPlayerChunk()
    {
        if (player == null) return Vector2Int.zero;
        int cx = Mathf.FloorToInt(player.position.x / (chunkWidth * blockSize));
        int cz = Mathf.FloorToInt(player.position.z / (chunkDepth * blockSize));
        return new Vector2Int(cx, cz);
    }

    // private async Task StartGenerateChunkAsync(Vector2Int chunkCoord, CancellationToken token)
    // {
    //     await generationSemaphore.WaitAsync(token).ConfigureAwait(false);

    //     try
    //     {
    //         if (token.IsCancellationRequested) return;

    //         // gera padded flattened diretamente do generator (NativeArray)
    //         int pw, ph, pd;
    //         var paddedFlat = chunkGenerator.GenerateFlattenedPadded_Native(chunkCoord, out pw, out ph, out pd, Allocator.TempJob);

    //         // extrai o inner (w,h,d) para SetBlocks (mantendo o array BlockType[,,] que o resto do pipeline usa)
    //         int w = chunkWidth, h = chunkHeight, d = chunkDepth;
    //         int pad = (pw - w) / 2;
    //         if (pad < 0) pad = 0;

    //         var inner = new BlockType[w, h, d];
    //         // copiando usando a mesma fórmula de flatten
    //         int lpw = pw, lph = ph, lpd = pd;
    //         for (int x = 0; x < w; x++)
    //         {
    //             int srcX = x + pad;
    //             for (int y = 0; y < h; y++)
    //             {
    //                 for (int z = 0; z < d; z++)
    //                 {
    //                     int srcZ = z + pad;
    //                     int idx = (srcX * lph + y) * lpd + srcZ;
    //                     inner[x, y, z] = (BlockType)paddedFlat[idx];
    //                 }
    //             }
    //         }

    //         // Construir isEmptyByType native array (0/1) com base em blockDataSO
    //         int maxEnum = 0;
    //         foreach (BlockType bt in Enum.GetValues(typeof(BlockType)))
    //         {
    //             maxEnum = Math.Max(maxEnum, (int)bt);
    //         }
    //         var isEmptyByType = new NativeArray<byte>(maxEnum + 1, Allocator.TempJob);
    //         for (int i = 0; i < isEmptyByType.Length; i++) isEmptyByType[i] = 0;
    //         if (blockDataSO != null)
    //         {
    //             foreach (var kv in blockDataSO.blockTextureDict)
    //             {
    //                 var bt = kv.Key;
    //                 var mapping = kv.Value;
    //                 if ((int)bt >= 0 && (int)bt < isEmptyByType.Length)
    //                     isEmptyByType[(int)bt] = (byte)(mapping.isEmpty ? 1 : 0);
    //             }
    //         }

    //         // Preparar job (usa paddedFlat diretamente)
    //         float s = blockSize;
    //         int airInt = (int)BlockType.Air;
    //         int waterInt = (int)BlockType.Water;

    //         var solidFaces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);
    //         var waterFaces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);

    //         var job = new MeshGenJob
    //         {
    //             padded = paddedFlat,
    //             pw = lpw,
    //             ph = lph,
    //             pd = lpd,
    //             pad = pad,
    //             w = w,
    //             h = h,
    //             d = d,
    //             s = s,
    //             airInt = airInt,
    //             waterInt = waterInt,
    //             isEmptyByType = isEmptyByType,
    //             solidFaces = solidFaces,
    //             waterFaces = waterFaces
    //         };

    //         // Executa (IJob) — manter comportamento original (Schedule/Complete)
    //         var handle = job.Schedule();
    //         handle.Complete();

    //         // Coleta resultados para main thread
    //         var solidFaceArr = solidFaces.AsArray();
    //         var waterFaceArr = waterFaces.AsArray();

    //         var solidVerts = new List<Vector3>(solidFaceArr.Length * 4);
    //         var solidTris = new List<int>(solidFaceArr.Length * 6);
    //         var solidFaceBlockTypes = new List<int>(solidFaceArr.Length);
    //         var solidFaceNormals = new List<int>(solidFaceArr.Length);

    //         for (int i = 0; i < solidFaceArr.Length; i++)
    //         {
    //             var f = solidFaceArr[i];
    //             int vi = solidVerts.Count;
    //             solidVerts.Add(ToV3(f.v0));
    //             solidVerts.Add(ToV3(f.v1));
    //             solidVerts.Add(ToV3(f.v2));
    //             solidVerts.Add(ToV3(f.v3));
    //             solidTris.Add(vi + 0); solidTris.Add(vi + 1); solidTris.Add(vi + 2);
    //             solidTris.Add(vi + 0); solidTris.Add(vi + 2); solidTris.Add(vi + 3);

    //             solidFaceBlockTypes.Add(f.blockType);
    //             solidFaceNormals.Add(f.normal);
    //         }

    //         var waterVerts = new List<Vector3>(waterFaceArr.Length * 4);
    //         var waterTris = new List<int>(waterFaceArr.Length * 6);
    //         var waterFaceBlockTypes = new List<int>(waterFaceArr.Length);
    //         var waterFaceNormals = new List<int>(waterFaceArr.Length);

    //         for (int i = 0; i < waterFaceArr.Length; i++)
    //         {
    //             var f = waterFaceArr[i];
    //             int vi = waterVerts.Count;
    //             waterVerts.Add(ToV3(f.v0));
    //             waterVerts.Add(ToV3(f.v1));
    //             waterVerts.Add(ToV3(f.v2));
    //             waterVerts.Add(ToV3(f.v3));
    //             waterTris.Add(vi + 0); waterTris.Add(vi + 1); waterTris.Add(vi + 2);
    //             waterTris.Add(vi + 0); waterTris.Add(vi + 2); waterTris.Add(vi + 3);

    //             waterFaceBlockTypes.Add(f.blockType);
    //             waterFaceNormals.Add(f.normal);
    //         }

    //         // Dispose dos containers nativos
    //         solidFaces.Dispose();
    //         waterFaces.Dispose();
    //         paddedFlat.Dispose(); // <--- agora descarta o NativeArray alocado no generator
    //         isEmptyByType.Dispose();

    //         // Enfileira resultado para main thread aplicar mesh (UVs e Mesh)
    //         var result = new MeshJobResult()
    //         {
    //             coord = chunkCoord,
    //             blocks = inner,
    //             solidVertices = solidVerts,
    //             solidTriangles = solidTris,
    //             solidFaceBlockTypes = solidFaceBlockTypes,
    //             solidFaceNormals = solidFaceNormals,
    //             waterVertices = waterVerts,
    //             waterTriangles = waterTris,
    //             waterFaceBlockTypes = waterFaceBlockTypes,
    //             waterFaceNormals = waterFaceNormals,
    //             width = w,
    //             height = h,
    //             depth = d,
    //             blockSize = s
    //         };

    //         meshResults.Enqueue(result);
    //     }
    //     catch (OperationCanceledException) { }
    //     catch (Exception ex)
    //     {
    //         Debug.LogError($"Erro em geração de chunk {chunkCoord}: {ex}");
    //     }
    //     finally
    //     {
    //         generationSemaphore.Release();
    //         generatingChunks.TryRemove(chunkCoord, out _);
    //     }
    // }

    // --- Helpers e funções antigas adaptadas ---


    private async Task StartGenerateChunkAsync(Vector2Int chunkCoord, CancellationToken token)
    {
        await generationSemaphore.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (token.IsCancellationRequested) return;

            // gera padded flattened diretamente do generator (NativeArray)
            int pw, ph, pd;
            var paddedFlat = chunkGenerator.GenerateFlattenedPadded_Native(chunkCoord, out pw, out ph, out pd, Allocator.TempJob);

            // extrai o inner (w,h,d) para SetBlocks (mantendo o array BlockType[,,] que o resto do pipeline usa)
            int w = chunkWidth, h = chunkHeight, d = chunkDepth;
            int pad = (pw - w) / 2;
            if (pad < 0) pad = 0;

            var inner = new BlockType[w, h, d];
            // copiando usando a mesma fórmula de flatten
            int lpw = pw, lph = ph, lpd = pd;
            for (int x = 0; x < w; x++)
            {
                int srcX = x + pad;
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        int srcZ = z + pad;
                        int idx = (srcX * lph + y) * lpd + srcZ;
                        inner[x, y, z] = (BlockType)paddedFlat[idx];
                    }
                }
            }

            // Construir isEmptyByType native array (0/1) com base em blockDataSO
            int maxEnum = 0;
            foreach (BlockType bt in Enum.GetValues(typeof(BlockType)))
            {
                maxEnum = Math.Max(maxEnum, (int)bt);
            }
            var isEmptyByType = new NativeArray<byte>(maxEnum + 1, Allocator.TempJob);
            for (int i = 0; i < isEmptyByType.Length; i++) isEmptyByType[i] = 0;
            if (blockDataSO != null)
            {
                foreach (var kv in blockDataSO.blockTextureDict)
                {
                    var bt = kv.Key;
                    var mapping = kv.Value;
                    if ((int)bt >= 0 && (int)bt < isEmptyByType.Length)
                        isEmptyByType[(int)bt] = (byte)(mapping.isEmpty ? 1 : 0);
                }
            }

            // Preparar job (usa paddedFlat diretamente)
            float s = blockSize;
            int airInt = (int)BlockType.Air;
            int waterInt = (int)BlockType.Water;
            int leafInt = (int)BlockType.Leaves; // <--- novo: inteiro para folhas

            var solidFaces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);
            var leafFaces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);   // <--- novo
            var waterFaces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);

            var job = new MeshGenJob
            {
                padded = paddedFlat,
                pw = lpw,
                ph = lph,
                pd = lpd,
                pad = pad,
                w = w,
                h = h,
                d = d,
                s = s,
                airInt = airInt,
                waterInt = waterInt,
                leafInt = leafInt, // <-- passar o leafInt pro job
                isEmptyByType = isEmptyByType,
                solidFaces = solidFaces,
                leafFaces = leafFaces,     // <--- ligar a lista
                waterFaces = waterFaces
            };

            // Executa (IJob) — manter comportamento original (Schedule/Complete)
            var handle = job.Schedule();
            handle.Complete();

            // Coleta resultados para main thread
            var solidFaceArr = solidFaces.AsArray();
            var leafFaceArr = leafFaces.AsArray();   // <--- novo
            var waterFaceArr = waterFaces.AsArray();

            var solidVerts = new List<Vector3>(solidFaceArr.Length * 4);
            var solidTris = new List<int>(solidFaceArr.Length * 6);
            var solidFaceBlockTypes = new List<int>(solidFaceArr.Length);
            var solidFaceNormals = new List<int>(solidFaceArr.Length);

            for (int i = 0; i < solidFaceArr.Length; i++)
            {
                var f = solidFaceArr[i];
                int vi = solidVerts.Count;
                solidVerts.Add(ToV3(f.v0));
                solidVerts.Add(ToV3(f.v1));
                solidVerts.Add(ToV3(f.v2));
                solidVerts.Add(ToV3(f.v3));
                solidTris.Add(vi + 0); solidTris.Add(vi + 1); solidTris.Add(vi + 2);
                solidTris.Add(vi + 0); solidTris.Add(vi + 2); solidTris.Add(vi + 3);

                solidFaceBlockTypes.Add(f.blockType);
                solidFaceNormals.Add(f.normal);
            }

            var leafVerts = new List<Vector3>(leafFaceArr.Length * 4);    // <--- novo
            var leafTris = new List<int>(leafFaceArr.Length * 6);         // <--- novo
            var leafFaceBlockTypes = new List<int>(leafFaceArr.Length);   // <--- novo
            var leafFaceNormals = new List<int>(leafFaceArr.Length);      // <--- novo

            for (int i = 0; i < leafFaceArr.Length; i++)
            {
                var f = leafFaceArr[i];
                int vi = leafVerts.Count;
                leafVerts.Add(ToV3(f.v0));
                leafVerts.Add(ToV3(f.v1));
                leafVerts.Add(ToV3(f.v2));
                leafVerts.Add(ToV3(f.v3));
                leafTris.Add(vi + 0); leafTris.Add(vi + 1); leafTris.Add(vi + 2);
                leafTris.Add(vi + 0); leafTris.Add(vi + 2); leafTris.Add(vi + 3);

                leafFaceBlockTypes.Add(f.blockType);
                leafFaceNormals.Add(f.normal);
            }

            var waterVerts = new List<Vector3>(waterFaceArr.Length * 4);
            var waterTris = new List<int>(waterFaceArr.Length * 6);
            var waterFaceBlockTypes = new List<int>(waterFaceArr.Length);
            var waterFaceNormals = new List<int>(waterFaceArr.Length);

            for (int i = 0; i < waterFaceArr.Length; i++)
            {
                var f = waterFaceArr[i];
                int vi = waterVerts.Count;
                waterVerts.Add(ToV3(f.v0));
                waterVerts.Add(ToV3(f.v1));
                waterVerts.Add(ToV3(f.v2));
                waterVerts.Add(ToV3(f.v3));
                waterTris.Add(vi + 0); waterTris.Add(vi + 1); waterTris.Add(vi + 2);
                waterTris.Add(vi + 0); waterTris.Add(vi + 2); waterTris.Add(vi + 3);

                waterFaceBlockTypes.Add(f.blockType);
                waterFaceNormals.Add(f.normal);
            }

            // Dispose dos containers nativos
            solidFaces.Dispose();
            leafFaces.Dispose();   // <--- novo
            waterFaces.Dispose();
            paddedFlat.Dispose(); // <--- agora descarta o NativeArray alocado no generator
            isEmptyByType.Dispose();

            // Enfileira resultado para main thread aplicar mesh (UVs e Mesh)
            var result = new MeshJobResult()
            {
                coord = chunkCoord,
                blocks = inner,
                solidVertices = solidVerts,
                solidTriangles = solidTris,
                solidFaceBlockTypes = solidFaceBlockTypes,
                solidFaceNormals = solidFaceNormals,

                // FOLHAS
                leafVertices = leafVerts,
                leafTriangles = leafTris,
                leafFaceBlockTypes = leafFaceBlockTypes,
                leafFaceNormals = leafFaceNormals,

                waterVertices = waterVerts,
                waterTriangles = waterTris,
                waterFaceBlockTypes = waterFaceBlockTypes,
                waterFaceNormals = waterFaceNormals,
                width = w,
                height = h,
                depth = d,
                blockSize = s
            };

            meshResults.Enqueue(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.LogError($"Erro em geração de chunk {chunkCoord}: {ex}");
        }
        finally
        {
            generationSemaphore.Release();
            generatingChunks.TryRemove(chunkCoord, out _);
        }
    }



    private static Vector3 ToV3(float3 f) => new Vector3(f.x, f.y, f.z);

    // função original AddUVsTo (mantive praticamente igual)
    private void AddUVsTo(List<Vector2> uvList, BlockType bt, Vector3 normal)
    {
        Vector2Int tile;

        if (blockDataSO != null && blockDataSO.blockTextureDict.TryGetValue(bt, out var mapping))
        {
            tile = normal == Vector3.up ? mapping.top :
                   normal == Vector3.down ? mapping.bottom :
                   mapping.side;
        }
        else
        {
            // Evitar logs excessivos no hot-path: apenas um aviso
            // (assuma que em produção os mapeamentos estão corretos)
            if (blockDataSO == null)
            {
                tile = new Vector2Int(1, 0);
            }
            else if (blockDataSO.blockTextureDict.TryGetValue(BlockType.Placeholder, out var fallbackMapping))
            {
                tile = normal == Vector3.up ? fallbackMapping.top :
                       normal == Vector3.down ? fallbackMapping.bottom :
                       fallbackMapping.side;
            }
            else
            {
                tile = new Vector2Int(1, 0);
            }
        }

        int tileY = tile.y;
        if (blockDataSO != null && atlasOrientation == AtlasOrientation.TopToBottom)
        {
            tileY = (int)blockDataSO.atlasSize.y - 1 - tile.y;
        }

        float invX = 1f;
        float invY = 1f;
        if (blockDataSO != null)
        {
            invX = 1f / blockDataSO.atlasSize.x;
            invY = 1f / blockDataSO.atlasSize.y;
        }

        Vector2 uv00 = new Vector2(tile.x * invX, tileY * invY);
        Vector2 uv10 = new Vector2((tile.x + 1) * invX, tileY * invY);
        Vector2 uv11 = new Vector2((tile.x + 1) * invX, (tileY + 1) * invY);
        Vector2 uv01 = new Vector2(tile.x * invX, (tileY + 1) * invY);

        uvList.Add(uv00);
        uvList.Add(uv10);
        uvList.Add(uv11);
        uvList.Add(uv01);
    }


    // Estrutura do resultado (adaptada para incluir faces info)
    private class MeshJobResult
    {
        public Vector2Int coord;
        public BlockType[,,] blocks;

        // Vertices / indices prontos
        public List<Vector3> solidVertices;
        public List<int> solidTriangles;
        // Por-quad info para gerar UVs no main thread
        public List<int> solidFaceBlockTypes;
        public List<int> solidFaceNormals;

        // FOLHAS (submesh separado)
        public List<Vector3> leafVertices;
        public List<int> leafTriangles;
        public List<int> leafFaceBlockTypes;
        public List<int> leafFaceNormals;

        public List<Vector3> waterVertices;
        public List<int> waterTriangles;
        public List<int> waterFaceBlockTypes;
        public List<int> waterFaceNormals;

        public int width, height, depth;
        public float blockSize;
    }

    public void SetBlockAtWorld(Vector3 worldPos, BlockType blockType)
    {
        int cx = Mathf.FloorToInt(worldPos.x / (chunkWidth * blockSize));
        int cz = Mathf.FloorToInt(worldPos.z / (chunkDepth * blockSize));
        Vector2Int coord = new Vector2Int(cx, cz);

        if (!activeChunks.TryGetValue(coord, out var chunk))
            return;

        int lx = Mathf.FloorToInt((worldPos.x - chunk.transform.position.x) / blockSize);
        int ly = Mathf.FloorToInt((worldPos.y - chunk.transform.position.y) / blockSize);
        int lz = Mathf.FloorToInt((worldPos.z - chunk.transform.position.z) / blockSize);

        chunk.SetBlock(lx, ly, lz, blockType);

        // 🔑 Atualizar vizinhos se necessário
        RefreshNeighbors(coord, lx, ly, lz);
    }

    public BlockType GetBlockAtWorld(Vector3 worldPos)
    {
        int cx = Mathf.FloorToInt(worldPos.x / (chunkWidth * blockSize));
        int cz = Mathf.FloorToInt(worldPos.z / (chunkDepth * blockSize));
        Vector2Int coord = new Vector2Int(cx, cz);

        if (!activeChunks.TryGetValue(coord, out var chunk))
            return BlockType.Air;

        int lx = Mathf.FloorToInt((worldPos.x - chunk.transform.position.x) / blockSize);
        int ly = Mathf.FloorToInt((worldPos.y - chunk.transform.position.y) / blockSize);
        int lz = Mathf.FloorToInt((worldPos.z - chunk.transform.position.z) / blockSize);

        return chunk.GetBlock(lx, ly, lz);
    }

    public void RefreshNeighbors(Vector2Int coord, int lx, int ly, int lz)
    {
        // Checar limites do chunk
        if (lx == 0)
            RebuildChunkAt(coord + new Vector2Int(-1, 0));
        else if (lx == chunkWidth - 1)
            RebuildChunkAt(coord + new Vector2Int(1, 0));

        if (lz == 0)
            RebuildChunkAt(coord + new Vector2Int(0, -1));
        else if (lz == chunkDepth - 1)
            RebuildChunkAt(coord + new Vector2Int(0, 1));
    }

    private void RebuildChunkAt(Vector2Int coord)
    {
        if (activeChunks.TryGetValue(coord, out var neighbor))
        {
            neighbor.RebuildMesh();
        }
    }

    // -----------------------------------------------------------
    // Job / Structs para meshing (Burst)
    // -----------------------------------------------------------

    // FaceData — estrutura simples e blittable para comunicação via NativeList
    public struct FaceData
    {
        public float3 v0;
        public float3 v1;
        public float3 v2;
        public float3 v3;
        public int blockType; // (int)BlockType
        public int normal;    // 0..5 (forward, back, up, down, right, left)
    }

    // [BurstCompile]
    // public struct MeshGenJob : IJob
    // {
    //     [ReadOnly] public NativeArray<int> padded; // flatten (x * ph + y) * pd + z
    //     public int pw, ph, pd;
    //     public int pad, w, h, d;
    //     public float s;
    //     public int airInt;
    //     public int waterInt;
    //     [ReadOnly] public NativeArray<byte> isEmptyByType;

    //     // resultados
    //     public NativeList<FaceData> solidFaces;
    //     public NativeList<FaceData> waterFaces;

    //     public void Execute()
    //     {
    //         // loops sobre inner no padded: x = pad .. pad + w - 1 ; z = pad .. pad + d - 1 ; y = 0..h-1
    //         for (int x = pad; x < pad + w; x++)
    //         {
    //             for (int y = 0; y < h; y++)
    //             {
    //                 for (int z = pad; z < pad + d; z++)
    //                 {
    //                     int idx = FlattenIndex(x, y, z);
    //                     int bt = padded[idx];
    //                     if (bt == airInt) continue;

    //                     float3 basePos = new float3((x - pad) * s, y * s, (z - pad) * s);
    //                     bool isWater = (bt == waterInt);

    //                     // front (+z), normal index 0
    //                     if (IsFaceExposed(x, y, z + 1, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(0, 0, s),
    //                             v1 = basePos + new float3(s, 0, s),
    //                             v2 = basePos + new float3(s, s, s),
    //                             v3 = basePos + new float3(0, s, s),
    //                             blockType = bt,
    //                             normal = 0
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }

    //                     // back (-z), normal index 1
    //                     if (IsFaceExposed(x, y, z - 1, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(s, 0, 0),
    //                             v1 = basePos + new float3(0, 0, 0),
    //                             v2 = basePos + new float3(0, s, 0),
    //                             v3 = basePos + new float3(s, s, 0),
    //                             blockType = bt,
    //                             normal = 1
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }

    //                     // top (+y), normal index 2
    //                     if (IsFaceExposed(x, y + 1, z, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(0, s, 0),
    //                             v1 = basePos + new float3(0, s, s),
    //                             v2 = basePos + new float3(s, s, s),
    //                             v3 = basePos + new float3(s, s, 0),
    //                             blockType = bt,
    //                             normal = 2
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }

    //                     // bottom (-y), normal index 3
    //                     if (IsFaceExposed(x, y - 1, z, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(0, 0, 0),
    //                             v1 = basePos + new float3(s, 0, 0),
    //                             v2 = basePos + new float3(s, 0, s),
    //                             v3 = basePos + new float3(0, 0, s),
    //                             blockType = bt,
    //                             normal = 3
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }

    //                     // right (+x), normal index 4
    //                     if (IsFaceExposed(x + 1, y, z, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(s, 0, s),
    //                             v1 = basePos + new float3(s, 0, 0),
    //                             v2 = basePos + new float3(s, s, 0),
    //                             v3 = basePos + new float3(s, s, s),
    //                             blockType = bt,
    //                             normal = 4
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }

    //                     // left (-x), normal index 5
    //                     if (IsFaceExposed(x - 1, y, z, isWater))
    //                     {
    //                         var f = new FaceData
    //                         {
    //                             v0 = basePos + new float3(0, 0, 0),
    //                             v1 = basePos + new float3(0, 0, s),
    //                             v2 = basePos + new float3(0, s, s),
    //                             v3 = basePos + new float3(0, s, 0),
    //                             blockType = bt,
    //                             normal = 5
    //                         };
    //                         (isWater ? waterFaces : solidFaces).Add(f);
    //                     }
    //                 }
    //             }
    //         }
    //     }

    //     private int FlattenIndex(int x, int y, int z)
    //     {
    //         return (x * ph + y) * pd + z;
    //     }

    //     private bool IsFaceExposed(int nx, int ny, int nz, bool faceIsWater)
    //     {
    //         // Se fora no Y => exposto
    //         if (ny < 0 || ny >= ph) return true;
    //         if (nx < 0 || nx >= pw || nz < 0 || nz >= pd) return true;

    //         int nIdx = FlattenIndex(nx, ny, nz);
    //         int nb = padded[nIdx];

    //         if (faceIsWater)
    //         {
    //             // Para água consideramos ar ou fora como exposto
    //             return nb == airInt;
    //         }
    //         else
    //         {
    //             // Para sólidos: exposto se vizinho for "air" ou "isEmpty"
    //             if (nb == airInt) return true;
    //             if (nb >= 0 && nb < isEmptyByType.Length && isEmptyByType[nb] == 1) return true;
    //             return false;
    //         }
    //     }
    // }


    [BurstCompile]
    public struct MeshGenJob : IJob
    {
        [ReadOnly] public NativeArray<int> padded; // flatten (x * ph + y) * pd + z
        public int pw, ph, pd;
        public int pad, w, h, d;
        public float s;
        public int airInt;
        public int waterInt;
        public int leafInt; // <--- novo: inteiro representando BlockType.Leaves
        [ReadOnly] public NativeArray<byte> isEmptyByType;

        // resultados
        public NativeList<FaceData> solidFaces;
        public NativeList<FaceData> leafFaces;  // <--- novo
        public NativeList<FaceData> waterFaces;

        public void Execute()
        {
            // loops sobre inner no padded: x = pad .. pad + w - 1 ; z = pad .. pad + d - 1 ; y = 0..h-1
            for (int x = pad; x < pad + w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int z = pad; z < pad + d; z++)
                    {
                        int idx = FlattenIndex(x, y, z);
                        int bt = padded[idx];
                        if (bt == airInt) continue;

                        float3 basePos = new float3((x - pad) * s, y * s, (z - pad) * s);
                        bool isWater = (bt == waterInt);
                        bool isLeaf = (bt == leafInt);

                        // front (+z), normal index 0
                        if (IsFaceExposed(x, y, z + 1, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(0, 0, s),
                                v1 = basePos + new float3(s, 0, s),
                                v2 = basePos + new float3(s, s, s),
                                v3 = basePos + new float3(0, s, s),
                                blockType = bt,
                                normal = 0
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }

                        // back (-z), normal index 1
                        if (IsFaceExposed(x, y, z - 1, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(s, 0, 0),
                                v1 = basePos + new float3(0, 0, 0),
                                v2 = basePos + new float3(0, s, 0),
                                v3 = basePos + new float3(s, s, 0),
                                blockType = bt,
                                normal = 1
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }

                        // top (+y), normal index 2
                        if (IsFaceExposed(x, y + 1, z, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(0, s, 0),
                                v1 = basePos + new float3(0, s, s),
                                v2 = basePos + new float3(s, s, s),
                                v3 = basePos + new float3(s, s, 0),
                                blockType = bt,
                                normal = 2
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }

                        // bottom (-y), normal index 3
                        if (IsFaceExposed(x, y - 1, z, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(0, 0, 0),
                                v1 = basePos + new float3(s, 0, 0),
                                v2 = basePos + new float3(s, 0, s),
                                v3 = basePos + new float3(0, 0, s),
                                blockType = bt,
                                normal = 3
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }

                        // right (+x), normal index 4
                        if (IsFaceExposed(x + 1, y, z, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(s, 0, s),
                                v1 = basePos + new float3(s, 0, 0),
                                v2 = basePos + new float3(s, s, 0),
                                v3 = basePos + new float3(s, s, s),
                                blockType = bt,
                                normal = 4
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }

                        // left (-x), normal index 5
                        if (IsFaceExposed(x - 1, y, z, isWater))
                        {
                            var f = new FaceData
                            {
                                v0 = basePos + new float3(0, 0, 0),
                                v1 = basePos + new float3(0, 0, s),
                                v2 = basePos + new float3(0, s, s),
                                v3 = basePos + new float3(0, s, 0),
                                blockType = bt,
                                normal = 5
                            };
                            (isWater ? waterFaces : (isLeaf ? leafFaces : solidFaces)).Add(f);
                        }
                    }
                }
            }
        }

        private int FlattenIndex(int x, int y, int z)
        {
            return (x * ph + y) * pd + z;
        }

        private bool IsFaceExposed(int nx, int ny, int nz, bool faceIsWater)
        {
            // Se fora no Y => exposto
            if (ny < 0 || ny >= ph) return true;
            if (nx < 0 || nx >= pw || nz < 0 || nz >= pd) return true;

            int nIdx = FlattenIndex(nx, ny, nz);
            int nb = padded[nIdx];

            if (faceIsWater)
            {
                // Para água consideramos ar ou fora como exposto
                return nb == airInt;
            }
            else
            {
                // Para sólidos/folhas: exposto se vizinho for "air" ou "isEmpty"
                if (nb == airInt) return true;
                if (nb >= 0 && nb < isEmptyByType.Length && isEmptyByType[nb] == 1) return true;
                return false;
            }
        }
    }



    // -------------------------
    // Utilitários
    // -------------------------
    private void EnsureCapacity<T>(List<T> list, int capacity)
    {
        if (list.Capacity < capacity) list.Capacity = capacity;
    }
}