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

    // Add this array to allow custom material assignment
    [Tooltip("Optional: Override materials array for chunk rendering.")]
    public Material[] materials;

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
    // private List<Vector2> reusableSolidUVs = new List<Vector2>(4096);
    // private List<Vector2> reusableWaterUVs = new List<Vector2>(1024);
    // private List<Vector3> reusableAllVerts = new List<Vector3>(8192);
    // private List<Vector2> reusableAllUVs = new List<Vector2>(8192);
    // private List<Vector3> reusableTempVerts = new List<Vector3>(8192); // usado se precisar temporariamente
    // -------------------------

    // --- caching for enum lookups (to avoid Enum.GetValues/boxing in hot-paths)
    private int cachedMaxBlockType = -1;
    private int[] cachedMaterialIndexByType; // index = (int)BlockType -> materialIndex
    private byte[] cachedIsEmptyByType;      // index = (int)BlockType -> 0/1
    private BlockDataSO cachedBlockDataSORef = null;


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

    private void EnsureBlockTypeCaches()
    {
        // recompute only if blockDataSO changed or not initialized
        if (cachedBlockDataSORef == blockDataSO && cachedMaxBlockType >= 0) return;

        cachedBlockDataSORef = blockDataSO;

        // descobrir max enum (uma vez). Usa Enum.GetValues UMA VEZ aqui — aceitável.
        int max = 0;
        foreach (BlockType bt in Enum.GetValues(typeof(BlockType)))
            max = Math.Max(max, (int)bt);

        cachedMaxBlockType = max;

        // criar arrays
        cachedMaterialIndexByType = new int[max + 1];
        cachedIsEmptyByType = new byte[max + 1];

        // defaults
        for (int i = 0; i <= max; i++)
        {
            cachedMaterialIndexByType[i] = 0;
            cachedIsEmptyByType[i] = 0;
        }

        if (blockDataSO != null)
        {
            foreach (var kv in blockDataSO.blockTextureDict)
            {
                int idx = (int)kv.Key;
                if (idx >= 0 && idx <= max)
                {
                    cachedMaterialIndexByType[idx] = Mathf.Max(0, kv.Value.materialIndex);
                    cachedIsEmptyByType[idx] = (byte)(kv.Value.isEmpty ? 1 : 0);
                }
            }
        }
    }


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

            // --- Agrupar faces (as faces foram empacotadas em res.solid* pelo StartGenerateChunkAsync) ---
            var faceVerts = res.solidVertices ?? new List<Vector3>();
            var faceTris = res.solidTriangles ?? new List<int>();
            var faceBlockTypes = res.solidFaceBlockTypes ?? new List<int>();
            var faceNormals = res.solidFaceNormals ?? new List<int>();

            int faceCount = faceBlockTypes.Count; // cada entrada é uma face (4 vértices, 6 índices)

            // Dicionários temporários por materialIndex
            var vertsByMat = new List<List<Vector3>>();
            var trisByMat = new List<List<int>>();
            var uvsByMat = new List<List<Vector2>>();
            var normsByMat = new List<List<Vector3>>();

            // Helper para garantir tamanho das listas por material index
            void EnsureMaterialSlot(int idx)
            {
                while (vertsByMat.Count <= idx)
                {
                    vertsByMat.Add(new List<Vector3>());
                    trisByMat.Add(new List<int>());
                    uvsByMat.Add(new List<Vector2>());
                    normsByMat.Add(new List<Vector3>());
                }
            }

            int GetMaterialIndexForBlockType(int blockTypeInt)
            {
                if (blockTypeInt < 0) return 0;
                // garante cache preparado
                if (cachedMaterialIndexByType == null) EnsureBlockTypeCaches();
                if (blockTypeInt >= cachedMaterialIndexByType.Length) return 0;
                return cachedMaterialIndexByType[blockTypeInt];
            }



            // Percorrer faces: cada face ocupa 4 vértices seguidos em faceVerts; face i tem verts index = i*4 .. i*4+3
            for (int fi = 0; fi < faceCount; fi++)
            {
                int matIndex = GetMaterialIndexForBlockType(faceBlockTypes[fi]);
                EnsureMaterialSlot(matIndex);

                var vlist = vertsByMat[matIndex];
                var tris = trisByMat[matIndex];
                var uvs = uvsByMat[matIndex];
                var norms = normsByMat[matIndex];

                int baseVertIndex = fi * 4;
                // adicionar 4 vértices
                vlist.Add(faceVerts[baseVertIndex + 0]);
                vlist.Add(faceVerts[baseVertIndex + 1]);
                vlist.Add(faceVerts[baseVertIndex + 2]);
                vlist.Add(faceVerts[baseVertIndex + 3]);

                // adicionar tri (indices relativos ao vlist)
                int vi = vlist.Count - 4;
                tris.Add(vi + 0); tris.Add(vi + 1); tris.Add(vi + 2);
                tris.Add(vi + 0); tris.Add(vi + 2); tris.Add(vi + 3);

                // UVs: usar AddUVsTo para esta face (esta função está em VoxelWorld e é segura na main thread)
                var bt = (BlockType)faceBlockTypes[fi];
                var normal = NormalFromIndex(faceNormals[fi]);
                AddUVsTo(uvs, bt, normal);

                // Normais per-vertex (4 cópias)
                norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);
            }

            // Montar array de materiais final (preferir VoxelWorld.materials[] se atribuído; senão montar fallback)
            Material[] finalMaterials = null;
            if (this.materials != null && this.materials.Length > 0)
            {
                finalMaterials = this.materials;
            }
            else
            {
                // fallback para compatibilidade com campos existentes
                var matsList = new List<Material>();
                if (chunkMaterial != null) matsList.Add(chunkMaterial);
                if (leafMaterial != null) matsList.Add(leafMaterial);
                if (waterMaterial != null) matsList.Add(waterMaterial);
                finalMaterials = matsList.ToArray();
            }

            // Garantir que finalMaterials tenha pelo menos o número de slots usados (ou será truncado no Chunk)
            // Chamar ApplyMeshDataByMaterial no chunk (ordem de materiais = índice)
            chunk.ApplyMeshDataByMaterial(
                vertsByMat,
                trisByMat,
                uvsByMat,
                normsByMat,
                finalMaterials,
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
            // int maxEnum = 0;
            // foreach (BlockType bt in Enum.GetValues(typeof(BlockType)))
            // {
            //     maxEnum = Math.Max(maxEnum, (int)bt);
            // }
            // var isEmptyByType = new NativeArray<byte>(maxEnum + 1, Allocator.TempJob);
            // for (int i = 0; i < isEmptyByType.Length; i++) isEmptyByType[i] = 0;
            // if (blockDataSO != null)
            // {
            //     foreach (var kv in blockDataSO.blockTextureDict)
            //     {
            //         var bt = kv.Key;
            //         var mapping = kv.Value;
            //         if ((int)bt >= 0 && (int)bt < isEmptyByType.Length)
            //             isEmptyByType[(int)bt] = (byte)(mapping.isEmpty ? 1 : 0);
            //     }
            // }
            // garante que caches estejam prontos
            EnsureBlockTypeCaches();
            int maxEnum = cachedMaxBlockType;
            var isEmptyByType = new NativeArray<byte>(maxEnum + 1, Allocator.TempJob);
            for (int i = 0; i <= maxEnum; i++) isEmptyByType[i] = cachedIsEmptyByType[i];


            // Preparar job (usa paddedFlat diretamente)
            float s = blockSize;
            int airInt = (int)BlockType.Air;

            var faces = new NativeList<VoxelWorld.FaceData>(Allocator.TempJob);

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
                isEmptyByType = isEmptyByType,
                faces = faces
            };

            // Executa (IJob) — manter comportamento original (Schedule/Complete)
            var handle = job.Schedule();
            handle.Complete();

            // Coleta resultados para main thread
            var faceArr = faces.AsArray();

            // transformar faces (cada face => 4 vértices + 6 índices) em listas simples para enfileirar
            var allVerts = new List<Vector3>(faceArr.Length * 4);
            var allTris = new List<int>(faceArr.Length * 6);
            var allFaceBlockTypes = new List<int>(faceArr.Length);
            var allFaceNormals = new List<int>(faceArr.Length);

            for (int i = 0; i < faceArr.Length; i++)
            {
                var f = faceArr[i];
                int vi = allVerts.Count;
                allVerts.Add(ToV3(f.v0));
                allVerts.Add(ToV3(f.v1));
                allVerts.Add(ToV3(f.v2));
                allVerts.Add(ToV3(f.v3));
                allTris.Add(vi + 0); allTris.Add(vi + 1); allTris.Add(vi + 2);
                allTris.Add(vi + 0); allTris.Add(vi + 2); allTris.Add(vi + 3);

                allFaceBlockTypes.Add(f.blockType);
                allFaceNormals.Add(f.normal);
            }

            // Dispose dos containers nativos
            faces.Dispose();
            paddedFlat.Dispose();
            isEmptyByType.Dispose();

            // Enfileira resultado para main thread aplicar mesh (agrupamento por material será feito na main thread)
            var result = new MeshJobResult()
            {
                coord = chunkCoord,
                blocks = inner,
                // usamos os campos "solid*" para transportar as faces combinadas (legacy names)
                solidVertices = allVerts,
                solidTriangles = allTris,
                solidFaceBlockTypes = allFaceBlockTypes,
                solidFaceNormals = allFaceNormals,
                // manter vazio os campos de water/leaf específicos (não usados aqui)
                waterVertices = null,
                waterTriangles = null,
                waterFaceBlockTypes = new List<int>(),
                waterFaceNormals = new List<int>(),
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



    [BurstCompile]
    public struct MeshGenJob : IJob
    {
        [ReadOnly] public NativeArray<int> padded; // flatten (x * ph + y) * pd + z
        public int pw, ph, pd;
        public int pad, w, h, d;
        public float s;
        public int airInt;
        [ReadOnly] public NativeArray<byte> isEmptyByType;

        // resultados: todas as faces (qualquer blockType)
        public NativeList<FaceData> faces;

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

                        // front (+z), normal index 0
                        if (IsFaceExposed(x, y, z + 1))
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
                            faces.Add(f);
                        }

                        // back (-z), normal index 1
                        if (IsFaceExposed(x, y, z - 1))
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
                            faces.Add(f);
                        }

                        // top (+y), normal index 2
                        if (IsFaceExposed(x, y + 1, z))
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
                            faces.Add(f);
                        }

                        // bottom (-y), normal index 3
                        if (IsFaceExposed(x, y - 1, z))
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
                            faces.Add(f);
                        }

                        // right (+x), normal index 4
                        if (IsFaceExposed(x + 1, y, z))
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
                            faces.Add(f);
                        }

                        // left (-x), normal index 5
                        if (IsFaceExposed(x - 1, y, z))
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
                            faces.Add(f);
                        }
                    }
                }
            }
        }

        private int FlattenIndex(int x, int y, int z)
        {
            return (x * ph + y) * pd + z;
        }

        private bool IsFaceExposed(int nx, int ny, int nz)
        {
            // Se fora no Y => exposto
            if (ny < 0 || ny >= ph) return true;
            if (nx < 0 || nx >= pw || nz < 0 || nz >= pd) return true;

            int nIdx = FlattenIndex(nx, ny, nz);
            int nb = padded[nIdx];

            // Para todos os tipos (não diferenciando água aqui), consideramos exposto se ar ou isEmpty.
            if (nb == airInt) return true;
            if (nb >= 0 && nb < isEmptyByType.Length && isEmptyByType[nb] == 1) return true;
            return false;
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