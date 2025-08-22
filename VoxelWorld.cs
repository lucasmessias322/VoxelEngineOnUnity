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

public class VoxelWorld : MonoBehaviour
{
    public static VoxelWorld Instance { get; private set; }

    [Header("Chunk params")]
    public int chunkWidth = 16;
    public int chunkHeight = 128;
    public int chunkDepth = 16;
    public float blockSize = 1f;
    [Header("Terrain Presets")]
    public float presetNoiseScale = 0.01f;
    public int presetSeed = 9999;

    [Header("Biomes")]
    public Biome[] biomes;                 // configurar no inspector
    public float biomeNoiseScale = 0.005f; // escala do mapa de biomas (baixo => grandes biomas)
    public int biomeSeed = 12345;          // seed para o mapa de biomas

    public BlockType defaultTopBlock = BlockType.Grass;
    public BlockType defaultSubSurfaceBlock = BlockType.Dirt;
    public BlockType defaultFillerBlock = BlockType.Stone;


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

    // fields em VoxelWorld
    private readonly ConcurrentQueue<Vector2Int> chunksToRebuild = new();
    public int maxChunkRebuildsPerFrame = 4; // ajustável no inspector se você tornar público

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
    private BiomeBlender biomeBlender;
    public bool SpawnTrees;
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
            var p = Camera.main; ;
            if (p != null) player = p.transform;
        }
        biomeBlender = new BiomeBlender(biomes, biomeNoiseScale, biomeSeed);



        chunkGenerator = new ChunkGenerator(
     chunkWidth, chunkHeight, chunkDepth,
    bedrockLayers, dirtLayers, seed, noiseSettings, seaLevel,
    treePadding, treeSpawnChance, treeMinHeight, treeMaxHeight, treeLeafRadius,
    woodBlock, leavesBlock,


    defaultTopBlock, defaultSubSurfaceBlock, defaultFillerBlock
);
        int concurrent = maxConcurrentGenerations;
        if (concurrent < 0) concurrent = SystemInfo.processorCount - 1;
        concurrent = Mathf.Max(1, concurrent);
        generationSemaphore = new SemaphoreSlim(concurrent);


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

        UpdateChunksStreaming();
        UpdateChunkColliders();

        ProcessChunkRebuildQueue(); // NOVO: processa N rebuilds por frame
    }

    private void ProcessChunkRebuildQueue()
    {
        int processed = 0;
        var processedSet = new HashSet<Vector2Int>(); // evita rebuild duplo do mesmo chunk nessa frame
        while (processed < maxChunkRebuildsPerFrame && chunksToRebuild.TryDequeue(out var coord))
        {
            if (processedSet.Contains(coord)) continue;
            processedSet.Add(coord);

            if (activeChunks.TryGetValue(coord, out var chunk))
            {
                // Rebuild mesh; reconstrução de colisores deixamos para condição de physics
                bool rebuildCollidersNow = /* checar se chunk está dentro de physicsRadiusInChunks */
                    (Mathf.Max(Mathf.Abs(coord.x - GetPlayerChunk().x), Mathf.Abs(coord.y - GetPlayerChunk().y)) <= physicsRadiusInChunks);

                chunk.ApplyPendingChanges(rebuildCollidersNow);
            }

            processed++;
        }
    }

    public Color GetFoliageColorAt(int worldX, int worldZ)
    {
        if (biomes == null || biomes.Length == 0)
            return Color.green;

        biomeBlender ??= new BiomeBlender(biomes, biomeNoiseScale, biomeSeed);

        biomeBlender.GetBiomeBlendAt(worldX, worldZ, out int i0, out int i1, out float t);

        Color c0 = biomes[Mathf.Clamp(i0, 0, biomes.Length - 1)].foliageColor;
        Color c1 = biomes[Mathf.Clamp(i1, 0, biomes.Length - 1)].foliageColor;
        return Color.Lerp(c0, c1, t);
    }
    public void EnsureBlockTypeCaches()
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

    // retorna chunk ativo (cria e adiciona em activeChunks se necessário)
    public Chunk GetOrCreateChunk(Vector2Int coord)
    {
        if (activeChunks.TryGetValue(coord, out var chunk))
            return chunk;

        chunk = GetChunkFromPool(coord); // usa o método privado já existente
        activeChunks[coord] = chunk;
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

            // recebe tanto o NativeArray (para o Job) quanto o int[] gerenciado (para leitura rápida)
            int pw, ph, pd;
            var paddedFlat = chunkGenerator.GenerateFlattenedPadded_Native(chunkCoord, out pw, out ph, out pd, out int[] paddedManaged, Allocator.TempJob, SpawnTrees);

            // --- usa o paddedManaged (int[]) para preencher inner (muito mais rápido que ler paddedFlat item-a-item) ---
            int lpw = pw, lph = ph, lpd = pd;
            int w = chunkWidth, h = chunkHeight, d = chunkDepth;
            int pad = (lpw - w) / 2;
            if (pad < 0) pad = 0;

            var inner = new BlockType[w, h, d];

            int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

            for (int x = 0; x < w; x++)
            {
                int srcX = x + pad;
                for (int y = 0; y < h; y++)
                {
                    for (int z = 0; z < d; z++)
                    {
                        int srcZ = z + pad;
                        int idx = FlattenIndex(srcX, y, srcZ);
                        inner[x, y, z] = (BlockType)paddedManaged[idx]; // leitura rápida de int[]
                    }
                }
            }

            // liberamos referência ao managed (ela fica elegível ao GC após método sair)
            // paddedManaged = null; // opcional

            // preparar caches / dados para o job
            EnsureBlockTypeCaches();
            int maxEnum = cachedMaxBlockType;
            var isEmptyByType = new NativeArray<byte>(cachedIsEmptyByType, Allocator.TempJob);

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
                waterInt = (int)BlockType.Water,
                isEmptyByType = isEmptyByType,
                faces = faces
            };

            // schedule + complete (mesma lógica anterior)
            var handle = job.Schedule();
            handle.Complete();

            var faceArr = faces.AsArray();

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

            // Enfileira resultado para main thread aplicar mesh
            var result = new MeshJobResult()
            {
                coord = chunkCoord,
                blocks = inner,
                solidVertices = allVerts,
                solidTriangles = allTris,
                solidFaceBlockTypes = allFaceBlockTypes,
                solidFaceNormals = allFaceNormals,
                waterVertices = null,
                waterTriangles = null,
                waterFaceBlockTypes = new List<int>(),
                waterFaceNormals = new List<int>(),
                width = w,
                height = h,
                depth = d,
                blockSize = s
            };

            // meshResults.Enqueue(result);
            // envia para o MeshBuilder (thread-safe)
            if (MeshBuilder.Instance != null)
                MeshBuilder.Instance.QueueResult(result);
            else
                meshResults.Enqueue(result); // fallback se ainda preferir

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
    public int[] GetCachedMaterialIndexByType()
    {
        EnsureBlockTypeCaches(); // garante inicialização
        return cachedMaterialIndexByType;
    }

    private static Vector3 ToV3(float3 f) => new Vector3(f.x, f.y, f.z);

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

        // Em vez de chamar RebuildMesh direto, enfileira para rebuild controlado
        chunksToRebuild.Enqueue(coord);

        // Atualiza vizinhos: só marca para rebuild, não chama RebuildMesh direto
        RefreshNeighborsMarkOnly(coord, lx, ly, lz);
    }
    public void RefreshNeighborsMarkOnly(Vector2Int coord, int lx, int ly, int lz)
    {
        if (lx == 0)
            EnqueueChunkForRebuild(coord + new Vector2Int(-1, 0));
        else if (lx == chunkWidth - 1)
            EnqueueChunkForRebuild(coord + new Vector2Int(1, 0));

        if (lz == 0)
            EnqueueChunkForRebuild(coord + new Vector2Int(0, -1));
        else if (lz == chunkDepth - 1)
            EnqueueChunkForRebuild(coord + new Vector2Int(0, 1));
    }

    private void EnqueueChunkForRebuild(Vector2Int coord)
    {
        // só se já existir ativo
        if (activeChunks.ContainsKey(coord))
            chunksToRebuild.Enqueue(coord);
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
        public int waterInt; // <- ADICIONADO
        [ReadOnly] public NativeArray<byte> isEmptyByType;

        // resultados: todas as faces (qualquer blockType)
        public NativeList<FaceData> faces;

        public void Execute()
        {
            // loops sobre inner no padded: x = pad . pad + w - 1 ; z = pad . pad + d - 1 ; y = 0.h-1
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
                        if (IsFaceExposed(bt, x, y, z + 1))
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
                        if (IsFaceExposed(bt, x, y, z - 1))
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
                        if (IsFaceExposed(bt, x, y + 1, z))
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
                        if (IsFaceExposed(bt, x, y - 1, z))
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
                        if (IsFaceExposed(bt, x + 1, y, z))
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
                        if (IsFaceExposed(bt, x - 1, y, z))
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

        // assinatura alterada para receber o blockType da face de origem
        private bool IsFaceExposed(int originBt, int nx, int ny, int nz)
        {
            // Se fora no Y => exposto
            if (ny < 0 || ny >= ph) return true;
            if (nx < 0 || nx >= pw || nz < 0 || nz >= pd) return true;

            int nIdx = FlattenIndex(nx, ny, nz);
            int nb = padded[nIdx];

            // Se a face pertence à água: exposto somente se vizinho for ar
            if (originBt == waterInt) return (nb == airInt);

            // Para todos os outros tipos: exposto se ar ou isEmptyByType
            if (nb == airInt) return true;
            if (nb >= 0 && nb < isEmptyByType.Length && isEmptyByType[nb] == 1) return true;
            return false;
        }
    }

}