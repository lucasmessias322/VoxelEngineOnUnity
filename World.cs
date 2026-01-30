using System.Collections.Generic;
using UnityEngine;
using System;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics; // For noise functions if needed (faster than Mathf)
using System.Collections.Concurrent;

[Serializable]
public struct WarpLayer
{
    public bool enabled;
    public float scale;       // Baixa frequência, ex.: 0.005f para distorções suaves
    public float amplitude;   // Força da distorção, ex.: 10-50
    public int octaves;       // 1-2 para warping simples
    public float persistence;
    public float lacunarity;
    public Vector2 offset;    // Offset inicial
    public float maxAmp;      // Precomputado, como em NoiseLayer
}

[Serializable]
public struct TreeSettings
{
    public int minHeight;
    public int maxHeight;

    public int canopyRadius;
    public int canopyHeight;

    public int minSpacing; // distância mínima entre árvores (em blocos)
    public float density;  // 0..1 probabilidade por célula
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
    public NoiseLayer[] noiseLayers; // agora configurável no inspector (várias layers)

    [Header("Cave Settings")]
    public NoiseLayer[] caveLayers;
    public float caveThreshold = 0.58f; // Aumente um pouco (+0.03) para compensar interpolação
    public int caveStride = 4; // 3-5x mais rápido! 1=original, 4=suave e rápido como Bedrock
    public int maxCaveDepthMultiplier = 1; // Limite: cavernas só até seaLevel * isso

    [Header("Domain Warping Settings")]
    public WarpLayer[] warpLayers; // Configurável no inspector (ex.: 1-2 layers para warping em X/Z)
    public int baseHeight = 64;
    public int heightVariation = 32;
    public int seed = 1337;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Queue<Chunk> chunkPool = new Queue<Chunk>();

    private float offsetX, offsetZ;
    [Header("Block Data")]
    public BlockDataSO blockData;
    [Header("Sea Settings")]
    public int seaLevel = 62;       // nível do mar igual ao Minecraft
    public BlockType waterBlock = BlockType.Water;

    private int nextChunkGeneration = 0;

    // Adições para limitação
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;

    private List<(Vector2Int coord, float distSq)> pendingChunks = new List<(Vector2Int, float)>();

    private List<PendingMesh> pendingMeshes = new List<PendingMesh>();
    // Adicionar no topo da classe:
    private Dictionary<Vector3Int, BlockType> blockOverrides = new Dictionary<Vector3Int, BlockType>();

    private struct PendingMesh
    {
        public JobHandle handle;
        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector3> normals;
        public NativeList<byte> lightValues; // novo: luz por vértice (0..15)
        public NativeArray<MeshGenerator.BlockEdit> edits;
        public NativeArray<MeshGenerator.TreeInstance> trees; // NEW: árvores aplicadas no job
        public Vector2Int coord;
        public int expectedGen;
    }

    [Header("Tree Settings")]
    public TreeSettings treeSettings;

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
        // garante dicionário inicializado
        if (blockData != null) blockData.InitializeDictionary();

        // offsets base a partir do seed (aplicado globalmente por segurança)
        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        // inicializa offsets das layers (determinístico baseado no seed)
        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
            {
                NoiseLayer layer = noiseLayers[i];
                if (!layer.enabled) continue;
                // Defaults Bedrock-like para superfície
                if (layer.scale <= 0f) layer.scale = 45f + i * 10f; // 45,55,65... progressivo
                if (layer.amplitude <= 0f) layer.amplitude = math.pow(0.55f, i); // Decai: 1,0.55,0.3,...
                if (layer.octaves <= 0) layer.octaves = 3 + i;
                if (layer.lacunarity <= 0f) layer.lacunarity = 2.2f;
                if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.55f;
                if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1.1f + i * 0.05f; // Leve lift para planícies
                if (layer.exponent == 0f) layer.exponent = 1.1f;
                if (layer.ridgeFactor <= 0f) layer.ridgeFactor = 1f + i * 0.2f; // Ridge crescente para details

                // se offset não definido, atribui um offset derivado do seed + index para variação entre layers
                if (layer.offset == Vector2.zero)
                {
                    layer.offset = new Vector2(offsetX + i * 13.37f, offsetZ + i * 7.53f);
                }
                else
                {
                    // aplica pequeno deslocamento global para diferença entre execuções
                    layer.offset += new Vector2(offsetX, offsetZ);
                }

                // Precompute maxAmp for normalization outside the loop
                float amp = 1f;
                layer.maxAmp = 0f;
                for (int o = 0; o < layer.octaves; o++)
                {
                    layer.maxAmp += amp;
                    amp *= layer.persistence;
                }
                if (layer.maxAmp <= 0f) layer.maxAmp = 1f; // Safety

                noiseLayers[i] = layer; // Assign back
            }
        }

        if (warpLayers != null)
        {
            for (int i = 0; i < warpLayers.Length; i++)
            {
                WarpLayer layer = warpLayers[i];
                if (!layer.enabled) continue;
                if (layer.scale <= 0f) layer.scale = 300f + i * 200f; // Baixa freq distorção
                if (layer.amplitude <= 0f) layer.amplitude = 28f; // Dist ~50 blocks

                // Garantir valores seguros
                if (layer.octaves <= 0) layer.octaves = 1;
                if (layer.lacunarity <= 0f) layer.lacunarity = 2f;
                if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.5f;

                // Offset derivado do seed + index
                if (layer.offset == Vector2.zero)
                {
                    layer.offset = new Vector2(offsetX + i * 23.45f, offsetZ + i * 11.89f); // Diferentes multipliers para variação
                }
                else
                {
                    layer.offset += new Vector2(offsetX, offsetZ);
                }

                // Precompute maxAmp
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

        // Inicializa caveLayers (semelhante a noiseLayers)
        if (caveLayers != null)
        {
            for (int i = 0; i < caveLayers.Length; i++)
            {
                NoiseLayer layer = caveLayers[i];
                if (!layer.enabled) continue;
                // Mesmos saneamentos e precomputes que em noiseLayers
                if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1f;
                if (layer.exponent == 0f) layer.exponent = 1f;

                if (layer.scale <= 0f) layer.scale = 0.03f; // Boa para cavernas
                if (layer.octaves <= 0) layer.octaves = 4;
                if (layer.lacunarity <= 0f) layer.lacunarity = 2f;
                if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.5f;

                if (layer.offset == Vector2.zero)
                {
                    layer.offset = new Vector2(offsetX + i * 19.87f, offsetZ + i * 8.76f); // Offsets únicos
                }
                else
                {
                    layer.offset += new Vector2(offsetX, offsetZ);
                }

                // Precompute maxAmp
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
            chunk.SetMaterials(new Material[] { Material[0], Material[1] });  // MODIFICAÇÃO: Array de 2 materiais
            chunkPool.Enqueue(chunk);
        }
    }

    private void Update()
    {
        UpdateChunks();

        int applied = 0;
        for (int i = pendingMeshes.Count - 1; i >= 0 && applied < maxMeshAppliesPerFrame; i--)
        {
            var pm = pendingMeshes[i];
            if (pm.handle.IsCompleted)
            {
                pm.handle.Complete();
                if (activeChunks.TryGetValue(pm.coord, out Chunk activeChunk) && activeChunk.generation == pm.expectedGen)
                {
                    activeChunk.ApplyMeshData(
                        pm.vertices.AsArray(),
                        pm.opaqueTriangles.AsArray(),
                        pm.waterTriangles.AsArray(),
                        pm.uvs.AsArray(),
                        pm.normals.AsArray(),
                        pm.lightValues.AsArray()
                    );
                    activeChunk.gameObject.SetActive(true);
                    applied++;
                }

                // dispose NativeLists
                pm.vertices.Dispose();
                pm.opaqueTriangles.Dispose();
                pm.waterTriangles.Dispose();
                pm.uvs.Dispose();
                pm.normals.Dispose();
                pm.lightValues.Dispose();

                // dispose NativeArrays (edits & trees) if created
                if (pm.edits.IsCreated) pm.edits.Dispose(); // IMPORTANTE: liberar o NativeArray de edits
                if (pm.trees.IsCreated) pm.trees.Dispose(); // liberar trees
                pendingMeshes.RemoveAt(i);
            }
        }
    }

    private void UpdateChunks()
    {
        Vector2Int playerChunk = new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );

        HashSet<Vector2Int> needed = new HashSet<Vector2Int>();

        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int z = -renderDistance; z <= renderDistance; z++)
            {
                Vector2Int coord = new Vector2Int(playerChunk.x + x, playerChunk.y + z);
                needed.Add(coord);
            }
        }

        // Adicionar novos chunks missing à pending list
        foreach (var coord in needed)
        {
            if (!activeChunks.ContainsKey(coord) && !pendingChunks.Exists(p => p.coord == coord))
            {
                float dx = coord.x - playerChunk.x;
                float dz = coord.y - playerChunk.y;
                float distSq = dx * dx + dz * dz;
                pendingChunks.Add((coord, distSq));
            }
        }

        // Remover da pending chunks que não são mais needed ou já active (limpeza)
        pendingChunks.RemoveAll(p => !needed.Contains(p.coord) || activeChunks.ContainsKey(p.coord));

        // NEW: Update distSq for all remaining pending chunks based on CURRENT player position
        // This ensures priorities adapt as the player moves
        for (int i = 0; i < pendingChunks.Count; i++)
        {
            var item = pendingChunks[i];
            float dx = item.coord.x - playerChunk.x;  // Recalculate based on current playerChunk
            float dz = item.coord.y - playerChunk.y;
            float newDistSq = dx * dx + dz * dz;
            pendingChunks[i] = (item.coord, newDistSq);
        }

        // Ordenar pending por distSq (menor primeiro = mais próximos)
        pendingChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        // Processar até maxChunksPerFrame
        int processed = Math.Min(maxChunksPerFrame, pendingChunks.Count);
        for (int i = 0; i < processed; i++)
        {
            var coord = pendingChunks[i].coord;
            RequestChunk(coord);
        }

        // Remover os processados da lista
        pendingChunks.RemoveRange(0, processed);

        // Remover chunks que não são mais needed
        List<Vector2Int> toRemove = new List<Vector2Int>();
        foreach (var kv in activeChunks)
        {
            if (!needed.Contains(kv.Key))
            {
                kv.Value.ResetChunk();
                chunkPool.Enqueue(kv.Value);
                toRemove.Add(kv.Key);
            }
        }
        foreach (var r in toRemove) activeChunks.Remove(r);
    }

    private void RequestChunk(Vector2Int coord)
    {
        Chunk chunk = (chunkPool.Count > 0) ? chunkPool.Dequeue() :
                       Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform).GetComponent<Chunk>();

        chunk.SetMaterials(new Material[] { Material[0], Material[1] });

        Vector3 pos = new Vector3(coord.x * Chunk.SizeX, 0, coord.y * Chunk.SizeZ);
        chunk.transform.position = pos;

        chunk.SetCoord(coord);

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        activeChunks.Add(coord, chunk);

        // --- BUILD edits FOR THIS CHUNK ---
        var editsList = new List<MeshGenerator.BlockEdit>();

        // limites do chunk (em world coords)
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        // estender 1 bloco nas bordas para cobrir voxels "border" usados pelo job
        int extend = 1;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        // coletar overrides que caem dentro da área estendida (inclui blocos do chunk vizinho
        // que afetam a borda do chunk corrente)
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

        // --- BUILD tree instances FOR THIS CHUNK ---
        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord);

        // calcular margin baseado nas configurações (maior altura possível + canopy)
        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);

        // NOVO: calcular borderSize e maxTreeRadius
        int borderSize = treeSettings.canopyRadius + 2;  // ex: 5 para radius=3
        int maxTreeRadius = treeSettings.canopyRadius;

        MeshGenerator.ScheduleMeshJob(
            coord,
            noiseLayers,
            warpLayers,
            caveLayers,
            blockData.mappings,
            baseHeight,
            heightVariation,
            offsetX,
            offsetZ,
            atlasTilesX,
            atlasTilesY,
            true,
            seaLevel,
            caveThreshold,
            caveStride,
            maxCaveDepthMultiplier,
            nativeEdits, // EDIT: pass edits
            nativeTrees, // NEW: pass trees
            treeMargin,  // NEW: pass margin
            borderSize,      // NOVO
            maxTreeRadius,   // NOVO
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> waterTriangles,
            out NativeList<Vector2> uvs,
            out NativeList<Vector3> normals,
            out NativeList<byte> vertexLights // novo out
        );

        pendingMeshes.Add(new PendingMesh
        {
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            uvs = uvs,
            normals = normals,
            lightValues = vertexLights,  // Novo
            edits = nativeEdits, // store to dispose later
            trees = nativeTrees,  // store trees to dispose later
            coord = coord,
            expectedGen = expectedGen
        });
    }

    // --- NEW: Request rebuild for an *active* chunk (usado quando editamos um bloco) ---
    private void RequestChunkRebuild(Vector2Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk)) return;

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;

        // --- Substituir o bloco anterior de build de edits por este ---
        var editsList = new List<MeshGenerator.BlockEdit>();

        // limites do chunk (em world coords)
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        // estender 1 bloco nas bordas para cobrir voxels "border" usados pelo job
        int extend = 1;
        int minX = chunkMinX - extend;
        int minZ = chunkMinZ - extend;
        int maxX = chunkMaxX + extend;
        int maxZ = chunkMaxZ + extend;

        // coletar overrides que caem dentro da área estendida (inclui blocos do chunk vizinho
        // que afetam a borda do chunk corrente)
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

        // Rebuild tree instances for this chunk
        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord);
        int treeMargin = math.max(1, treeSettings.maxHeight + treeSettings.canopyHeight + 2);
        int borderSize = treeSettings.canopyRadius + 2;  // ex: 5 para radius=3
        int maxTreeRadius = treeSettings.canopyRadius;

        MeshGenerator.ScheduleMeshJob(
            coord,
            noiseLayers,
            warpLayers,
            caveLayers,
            blockData.mappings,
            baseHeight,
            heightVariation,
            offsetX,
            offsetZ,
            atlasTilesX,
            atlasTilesY,
            true,
            seaLevel,
            caveThreshold,
            caveStride,
            maxCaveDepthMultiplier,
            nativeEdits, // pass edits
            nativeTrees, // pass trees
            treeMargin,  // pass margin
            borderSize,      // NOVO
            maxTreeRadius,   // NOVO
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> waterTriangles,
            out NativeList<Vector2> uvs,
            out NativeList<Vector3> normals,
            out NativeList<byte> vertexLights // novo out
        );

        pendingMeshes.Add(new PendingMesh
        {
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            uvs = uvs,
            normals = normals,
            lightValues = vertexLights,
            edits = nativeEdits,
            trees = nativeTrees,
            coord = coord,
            expectedGen = expectedGen
        });
    }

    public void SetBlockAt(Vector3Int worldPos, BlockType type)
    {
        BlockType current = GetBlockAt(worldPos);

        // Impede alterar Bedrock (y <= 2)
        if (current == BlockType.Bedrock)
        {
            Debug.Log("Attempt to modify Bedrock ignored: " + worldPos);
            return;
        }
        // sempre registre a edição — se for Air, grava explicitamente Air para que o MeshGenerator
        // receba a instrução de remover o bloco gerado desse lugar
        blockOverrides[worldPos] = type;

        // Determina chunks afetados: chunk que contém o bloco + vizinhos se estiver na borda
        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        // Sempre re-gerar o chunk onde foi modificado
        if (activeChunks.ContainsKey(chunkCoord))
            RequestChunkRebuild(chunkCoord);

        // Se estiver na borda X/Z, regen vizinhos (x-1, x+1, z-1, z+1)
        int localX = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int localZ = worldPos.z - chunkCoord.y * Chunk.SizeZ;

        if (localX == 0 && activeChunks.ContainsKey(new Vector2Int(chunkCoord.x - 1, chunkCoord.y)))
            RequestChunkRebuild(new Vector2Int(chunkCoord.x - 1, chunkCoord.y));
        if (localX == Chunk.SizeX - 1 && activeChunks.ContainsKey(new Vector2Int(chunkCoord.x + 1, chunkCoord.y)))
            RequestChunkRebuild(new Vector2Int(chunkCoord.x + 1, chunkCoord.y));
        if (localZ == 0 && activeChunks.ContainsKey(new Vector2Int(chunkCoord.x, chunkCoord.y - 1)))
            RequestChunkRebuild(new Vector2Int(chunkCoord.x, chunkCoord.y - 1));
        if (localZ == Chunk.SizeZ - 1 && activeChunks.ContainsKey(new Vector2Int(chunkCoord.x, chunkCoord.y + 1)))
            RequestChunkRebuild(new Vector2Int(chunkCoord.x, chunkCoord.y + 1));
    }

    // ADICIONE ESSA FUNÇÃO NA SUA CLASSE World
    public BlockType GetBlockAt(Vector3Int worldPos)
    {
        // 1) limites Y
        if (worldPos.y < 0)
            return BlockType.Air;         // fora da área válida abaixo do mundo

        // MeshGenerator trata y <= 2 como bedrock; manter consistência evita permitir quebra.
        if (worldPos.y <= 2)
            return BlockType.Bedrock;     // camada inquebrável do fundo

        if (worldPos.y >= Chunk.SizeY)
            return BlockType.Air;         // fora do range vertical acima

        // 2) overrides (edits feitos pelo jogador / sistema)
        if (blockOverrides != null && blockOverrides.TryGetValue(worldPos, out BlockType overridden))
        {
            return overridden;
        }

        // 3) gerar height usando mesmas layers de world (warp + noise)
        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        // Compute surface height via helper
        int h = GetSurfaceHeight(worldX, worldZ);
        // --- DETECTAR ÁRVORES (TRONCO / COPA) ---
        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        // gerar árvores determinísticas do chunk
        NativeArray<MeshGenerator.TreeInstance> trees = BuildTreeInstancesForChunk(chunkCoord);

        for (int i = 0; i < trees.Length; i++)
        {
            var t = trees[i];

            int baseY = GetSurfaceHeight(t.worldX, t.worldZ);
            int trunkTop = baseY + t.trunkHeight;

            // TRONCO
            if (worldPos.x == t.worldX &&
                worldPos.z == t.worldZ &&
                worldPos.y > baseY &&
                worldPos.y <= trunkTop)
            {
                trees.Dispose();
                return BlockType.Log;
            }

            // COPA
            int canopyStartY = trunkTop - t.canopyHeight + 1;
            int canopyEndY = trunkTop + 1;

            if (worldPos.y >= canopyStartY && worldPos.y <= canopyEndY)
            {
                int dx = worldPos.x - t.worldX;
                int dz = worldPos.z - t.worldZ;

                if (dx * dx + dz * dz <= t.canopyRadius * t.canopyRadius)
                {
                    trees.Dispose();
                    return BlockType.Leaves;
                }
            }
        }

        trees.Dispose();


        // 4) cavernas (aplica somente até certa profundidade)
        bool isCave = false;
        if (caveLayers != null && caveLayers.Length > 0)
        {
            int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (worldPos.y <= maxCaveY)
            {
                float totalCave = 0f;
                float sumCaveAmp = 0f;
                for (int i = 0; i < caveLayers.Length; i++)
                {
                    var layer = caveLayers[i];
                    if (!layer.enabled) continue;

                    float nx = worldX + layer.offset.x;
                    float ny = worldPos.y;
                    float nz = worldZ + layer.offset.y;

                    float sample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
                    if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                        sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);

                    totalCave += sample * layer.amplitude;
                    sumCaveAmp += math.max(1e-5f, layer.amplitude);
                }
                if (sumCaveAmp > 0f) totalCave /= sumCaveAmp;

                // small surface bias (imita seu código de geração)
                float maxPossibleY = math.max(1f, (float)math.max(1, h));
                float relativeHeight = (float)worldPos.y / maxPossibleY;
                float surfaceBias = 0.001f * relativeHeight;
                if (worldPos.y < 5) surfaceBias -= 0.08f;

                float adjustedThreshold = caveThreshold - surfaceBias;
                if (totalCave > adjustedThreshold) isCave = true;
            }
        }

        // 5) decidir tipo final
        if (isCave)
        {
            return BlockType.Air;
        }

        if (worldPos.y > h)
        {
            // acima do terreno: se abaixo do mar -> água
            if (worldPos.y <= seaLevel)
                return BlockType.Water;
            return BlockType.Air;
        }
        else
        {
            // abaixo ou igual à superfície
            bool isBeachArea = (h <= seaLevel + 2);
            if (worldPos.y == h)
            {
                return isBeachArea ? BlockType.Sand : BlockType.Grass;
            }
            else if (worldPos.y > h - 4)
            {
                return isBeachArea ? BlockType.Sand : BlockType.Dirt;
            }
            else
            {
                return BlockType.Stone;
            }
        }
    }

    // helper local (replika do Job) - calcula altura da superfície para um (x,z)
    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        // Domain warping
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

                warpX += sampleX * layer.amplitude;
                warpZ += sampleZ * layer.amplitude;
                sumWarpAmp += math.max(1e-5f, layer.amplitude);
            }
        }
        if (sumWarpAmp > 0f)
        {
            warpX /= sumWarpAmp;
            warpZ /= sumWarpAmp;
        }
        warpX = (warpX - 0.5f) * 2f;
        warpZ = (warpZ - 0.5f) * 2f;

        // Noise layers (surface)
        float totalNoise = 0f;
        float sumAmp = 0f;
        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
            {
                var layer = noiseLayers[i];
                if (!layer.enabled) continue;

                float nx = (worldX + warpX) + layer.offset.x;
                float nz = (worldZ + warpZ) + layer.offset.y;

                float sample = MyNoise.OctavePerlin(nx, nz, layer);
                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                    sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);

                totalNoise += sample * layer.amplitude;
                sumAmp += math.max(1e-5f, layer.amplitude);
            }
        }

        if (sumAmp > 0f) totalNoise /= sumAmp;
        else
        {
            // fallback caso não haja layers
            float nx = (worldX + warpX) * 0.05f + offsetX;
            float nz = (worldZ + warpZ) * 0.05f + offsetZ;
            totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
        }

        return GetHeightFromNoise(totalNoise);
    }

    // Constrói as instâncias de árvore para um chunk (determinístico)
    private NativeArray<MeshGenerator.TreeInstance> BuildTreeInstancesForChunk(Vector2Int coord)
    {
        int cellSize = math.max(1, treeSettings.minSpacing);
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        List<MeshGenerator.TreeInstance> tmp = new List<MeshGenerator.TreeInstance>();

        // EXPANDIDO: margem para copas overhang
        int searchMargin = treeSettings.canopyRadius + treeSettings.minSpacing;
        int cellX0 = Mathf.FloorToInt((float)(chunkMinX - searchMargin) / cellSize);
        int cellX1 = Mathf.FloorToInt((float)(chunkMaxX + searchMargin) / cellSize);
        int cellZ0 = Mathf.FloorToInt((float)(chunkMinZ - searchMargin) / cellSize);
        int cellZ1 = Mathf.FloorToInt((float)(chunkMaxZ + searchMargin) / cellSize);

        float freq = 1f / math.max(1, cellSize * 3);

        for (int cx = cellX0; cx <= cellX1; cx++)
        {
            for (int cz = cellZ0; cz <= cellZ1; cz++)
            {
                float sample = Mathf.PerlinNoise((cx * 12.9898f + seed) * freq, (cz * 78.233f + seed) * freq);
                if (sample > treeSettings.density) continue;

                int worldX = cx * cellSize + (cellSize / 2);
                int worldZ = cz * cellSize + (cellSize / 2);

                // REMOVIDO: sem skip de bounds do chunk - agora inclui overhang

                int h = GetSurfaceHeight(worldX, worldZ);
                if (h <= 0 || h >= Chunk.SizeY) continue;

                if (GetSurfaceBlockType(worldX, worldZ) != BlockType.Grass) continue;


                float th = Mathf.PerlinNoise((worldX + 0.1f) * 0.137f + seed * 0.001f, (worldZ + 0.1f) * 0.243f + seed * 0.001f);
                int trunkH = treeSettings.minHeight + (int)(th * (treeSettings.maxHeight - treeSettings.minHeight + 0.0001f));
                trunkH = math.clamp(trunkH, treeSettings.minHeight, treeSettings.maxHeight);

                tmp.Add(new MeshGenerator.TreeInstance
                {
                    worldX = worldX,
                    worldZ = worldZ,
                    trunkHeight = trunkH,
                    canopyRadius = treeSettings.canopyRadius,
                    canopyHeight = treeSettings.canopyHeight
                });
            }

        }

        if (tmp.Count == 0)
        {
            return new NativeArray<MeshGenerator.TreeInstance>(0, Allocator.Persistent);
        }

        var arr = new NativeArray<MeshGenerator.TreeInstance>(tmp.Count, Allocator.Persistent);
        for (int i = 0; i < tmp.Count; i++) arr[i] = tmp[i];
        return arr;
    }

    // helper local (replika do Job)
    private int GetHeightFromNoise(float noise)
    {
        return math.clamp(baseHeight + (int)math.floor((noise - 0.5f) * 2f * heightVariation), 1, Chunk.SizeY - 1);
    }

    // NOVO: retorna APENAS o bloco da superfície (sem árvores, sem overrides)
    private BlockType GetSurfaceBlockType(int worldX, int worldZ)
    {
        int h = GetSurfaceHeight(worldX, worldZ);
        if (h <= 0 || h >= Chunk.SizeY) return BlockType.Air;

        bool isBeachArea = (h <= seaLevel + 2);
        return isBeachArea ? BlockType.Sand : BlockType.Grass;
    }

}