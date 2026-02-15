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
    public int minHeight;      // Altura mínima do tronco (ex.: 4)
    public int maxHeight;      // Altura máxima do tronco (ex.: 6)
    public int canopyRadius;   // Raio da copa (ex.: 2)
    public int canopyHeight;   // Altura da copa (ex.: 3)
    public int minSpacing;     // Tamanho do grid para spacing (ex.: 4, como Minecraft)
    public float density;      // Probabilidade base (0-1, ex.: 0.1 para florestas)
    public float noiseScale;   // Escala do Perlin (ex.: 0.05f para variações suaves)
    public int seed;           // Seed global para noise
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
        public NativeList<int> transparentTriangles; // NOVO
        public NativeList<Vector2> uvs;
        public NativeList<Vector3> normals;
        public NativeList<byte> lightValues; // novo: luz por vértice (0..15)
        public NativeArray<MeshGenerator.BlockEdit> edits;
        public NativeArray<MeshGenerator.TreeInstance> trees; // NEW: árvores aplicadas no job
        public Vector2Int coord;
        public int expectedGen;
        public NativeList<byte> tintFlags;
        // NOVO: referência ao chunk para marcar hasVoxelData quando complete
        public Chunk chunk;
    }

    [Header("Tree Settings")]
    public TreeSettings treeSettings;


    public int CliffTreshold = 2; // diferença de altura para considerar um cliff

    // No topo da classe Chunk (ou em [Header("Grass Tint")]
    [Header("Grass Tint Settings")]
    public Color grassTintBase = new Color(0.8f, 0.4f, 0.4f);  // cor desejada quando bem iluminado
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

            // Certifique-se de passar 3 materiais: 0=opaco, 1=transparente, 2=água
            Material[] matsForChunk = (Material != null && Material.Length >= 3) ?
                new Material[] { Material[0], Material[1], Material[2] } :
                // fallback seguro caso o array no inspector esteja incompleto
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
                        pm.transparentTriangles.AsArray(),
                        pm.waterTriangles.AsArray(),
                        pm.uvs.AsArray(),
                        pm.normals.AsArray(),
                        pm.lightValues.AsArray(),
                        pm.tintFlags.AsArray()
                    );

                    // NOVO: marcar que os voxels foram preenchidos
                    activeChunk.hasVoxelData = true;

                    activeChunk.gameObject.SetActive(true);
                    applied++;
                }

                // Dispose NativeLists
                pm.vertices.Dispose();
                pm.opaqueTriangles.Dispose();
                pm.transparentTriangles.Dispose();
                pm.waterTriangles.Dispose();
                pm.uvs.Dispose();
                pm.normals.Dispose();
                pm.lightValues.Dispose();
                pm.tintFlags.Dispose();

                // Dispose edits & trees
                if (pm.edits.IsCreated) pm.edits.Dispose();
                if (pm.trees.IsCreated) pm.trees.Dispose();

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

        // garantir array de 3 materiais também aqui
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

        // NOVO: alocar voxelData
        if (!chunk.voxelData.IsCreated)
        {
            chunk.voxelData = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);
            chunk.hasVoxelData = false;
        }

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
        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord, treeSettings);

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
            CliffTreshold,
           chunk.voxelData,    // NOVO: passar voxelOutput para o job
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> transparentTriangles,
            out NativeList<int> waterTriangles,

            out NativeList<Vector2> uvs,
            out NativeList<Vector3> normals,
            out NativeList<byte> vertexLights, // novo out
            out NativeList<byte> tintFlags
        );

        pendingMeshes.Add(new PendingMesh
        {
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            transparentTriangles = transparentTriangles,
            waterTriangles = waterTriangles,

            uvs = uvs,
            normals = normals,
            lightValues = vertexLights,  // Novo
            edits = nativeEdits, // store to dispose later
            trees = nativeTrees,  // store trees to dispose later
            coord = coord,
            expectedGen = expectedGen,
            tintFlags = tintFlags
        });
    }

    // --- NEW: Request rebuild for an *active* chunk (usado quando editamos um bloco) ---
    private void RequestChunkRebuild(Vector2Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk)) return;

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;
        chunk.hasVoxelData = false; // será marcado true quando o job completar

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

        NativeArray<MeshGenerator.TreeInstance> nativeTrees = BuildTreeInstancesForChunk(coord, treeSettings);
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
            CliffTreshold,
            chunk.voxelData,     // NOVO: passar voxelOutput para o job
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> transparentTriangles,
            out NativeList<int> waterTriangles,

            out NativeList<Vector2> uvs,
            out NativeList<Vector3> normals,
            out NativeList<byte> vertexLights, // novo out
            out NativeList<byte> tintFlags // novo out
        );

        pendingMeshes.Add(new PendingMesh
        {
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            uvs = uvs,
            normals = normals,
            lightValues = vertexLights,
            edits = nativeEdits,
            trees = nativeTrees,
            coord = coord,
            expectedGen = expectedGen,
            tintFlags = tintFlags,
            chunk = chunk // NOVO: referência para marcar hasVoxelData
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

        // Opcional: atualizar voxelData imediatamente se chunk loaded

        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.hasVoxelData)
        {
            int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
            int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
            int ly = worldPos.y;
            if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
            {
                int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
                chunk.voxelData[idx] = (byte)type;
            }
        }
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
    public BlockType GetBlockAt(Vector3Int worldPos)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY) return BlockType.Air;
        if (worldPos.y <= 2) return BlockType.Bedrock;

        if (blockOverrides.TryGetValue(worldPos, out BlockType overridden))
            return overridden;

        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        // 3) Lógica de árvores (deve vir antes do terreno/cavernas)
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

        NativeArray<MeshGenerator.TreeInstance> trees = BuildTreeInstancesForChunk(chunkCoord, treeSettings);
        BlockType treeBlockFound = BlockType.Air;

        try
        {
            for (int i = 0; i < trees.Length; i++)
            {
                var t = trees[i];

                // Só considera árvores plantadas em grama
                if (GetSurfaceBlockType(t.worldX, t.worldZ) != BlockType.Grass)
                    continue;

                int baseY = GetSurfaceHeight(t.worldX, t.worldZ);
                int trunkTop = baseY + t.trunkHeight;

                // Tronco
                if (worldPos.x == t.worldX && worldPos.z == t.worldZ &&
                    worldPos.y > baseY && worldPos.y <= trunkTop)
                {
                    treeBlockFound = BlockType.Log;
                    break;
                }

                // Folhas (copa)
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

        // 4) Cavernas — AGORA ALINHADO COM O JOB DO MESHGENERATOR
        bool isCave = false;
        int surfaceHeight = GetSurfaceHeight(worldX, worldZ);

        if (caveLayers != null && caveLayers.Length > 0)
        {
            int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (worldPos.y <= maxCaveY)
            {
                // === Calcula o mesmo grid coarse que o job usa ===

                // Precisamos do border usado no job para alinhar minWorld
                int border = treeSettings.canopyRadius + 2;  // mesmo valor usado no RequestChunk / RequestChunkRebuild

                int chunkMinX = chunkCoord.x * Chunk.SizeX;
                int chunkMinZ = chunkCoord.y * Chunk.SizeZ;

                int minWorldX = chunkMinX - border;
                int minWorldZ = chunkMinZ - border;
                int minWorldY = 0;

                int stride = math.max(1, caveStride);

                // Índices coarse inferiores (usando FloorDiv para alinhar com job)
                int cx0 = FloorDiv(worldX - minWorldX, stride);
                int cy0 = FloorDiv(worldPos.y - minWorldY, stride);
                int cz0 = FloorDiv(worldZ - minWorldZ, stride);

                // Posições world dos 8 cantos do cubo coarse
                int lowWX = minWorldX + cx0 * stride;
                int highWX = lowWX + stride;

                int lowWY = minWorldY + cy0 * stride;
                int highWY = lowWY + stride;

                int lowWZ = minWorldZ + cz0 * stride;
                int highWZ = lowWZ + stride;

                // Amostra nos 8 pontos (usando a mesma função de noise)
                float c000 = ComputeCaveNoise(lowWX, lowWY, lowWZ);
                float c100 = ComputeCaveNoise(highWX, lowWY, lowWZ);
                float c010 = ComputeCaveNoise(lowWX, highWY, lowWZ);
                float c110 = ComputeCaveNoise(highWX, highWY, lowWZ);
                float c001 = ComputeCaveNoise(lowWX, lowWY, highWZ);
                float c101 = ComputeCaveNoise(highWX, lowWY, highWZ);
                float c011 = ComputeCaveNoise(lowWX, highWY, highWZ);
                float c111 = ComputeCaveNoise(highWX, highWY, highWZ);

                // Frações de interpolação
                float fx = (float)(worldX - lowWX) / stride;
                float fy = (float)(worldPos.y - lowWY) / stride;
                float fz = (float)(worldZ - lowWZ) / stride;

                // Interpolação trilinear (exatamente como no job)
                float x00 = Mathf.Lerp(c000, c100, fx);
                float x01 = Mathf.Lerp(c001, c101, fx);
                float x10 = Mathf.Lerp(c010, c110, fx);
                float x11 = Mathf.Lerp(c011, c111, fx);

                float z0 = Mathf.Lerp(x00, x01, fz);
                float z1 = Mathf.Lerp(x10, x11, fz);

                float interpolatedCave = Mathf.Lerp(z0, z1, fy);

                // Surface bias — alinhado com o job (usando surfaceHeight)
                float surfaceBias = 0.001f * ((float)worldPos.y / math.max(1f, (float)surfaceHeight));
                if (worldPos.y < 5) surfaceBias -= 0.08f;

                float adjustedThreshold = caveThreshold - surfaceBias;

                if (interpolatedCave > adjustedThreshold)
                    isCave = true;
            }
        }

        if (isCave)
            return BlockType.Air;

        // 5) Terreno natural (superfície, subsolo, água)
        if (worldPos.y > surfaceHeight)
        {
            return (worldPos.y <= seaLevel) ? BlockType.Water : BlockType.Air;
        }
        else
        {
            // 🔥 NOVO: replicando PopulateTerrainColumns para 100% de sincronização
            bool isBeachArea = (surfaceHeight <= seaLevel + 2);
            bool isCliff = IsCliff(worldX, worldZ, CliffTreshold);
            int mountainStoneHeight = baseHeight + 70; // ajuste como quiser (mesmo valor do MeshGenerator)
            bool isHighMountain = surfaceHeight >= mountainStoneHeight;

            if (worldPos.y == surfaceHeight)
            {
                if (isHighMountain)
                {
                    return BlockType.Stone; // ⛰️ topo de montanha alta
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
                    return BlockType.Stone; // 👈 cliff wall
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

    // Função auxiliar (já existia, mas mantida aqui para completude)
    private float ComputeCaveNoise(int wx, int wy, int wz)
    {
        float totalCave = 0f;
        float sumCaveAmp = 0f;

        for (int i = 0; i < caveLayers.Length; i++)
        {
            var layer = caveLayers[i];
            if (!layer.enabled) continue;

            float nx = wx + layer.offset.x;
            float ny = (float)wy;
            float nz = wz + layer.offset.y;

            float sample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
            if (layer.redistributionModifier != 1f || layer.exponent != 1f)
            {
                sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
            }
            totalCave += sample * layer.amplitude;
            sumCaveAmp += math.max(1e-5f, layer.amplitude);
        }

        return (sumCaveAmp > 0f) ? totalCave / sumCaveAmp : 0f;
    }

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


                float sampleX = MyNoise.OctavePerlin(baseNx + 100f, baseNz, layer);  // [0,1]
                float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);  // [0,1]

                // Centre em [-1,1] e aplique amplitude (força da distorção)
                // dentro do loop de warpLayers:
                warpX += (sampleX * 2f - 1f) * layer.amplitude;
                warpZ += (sampleZ * 2f - 1f) * layer.amplitude;
                sumWarpAmp += layer.amplitude;   // <<-- faltando
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
        float sumAmp = 0f;  // Inicialize corretamente (já está)
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

        // Fallback se não houver layers ativas ou sumAmp == 0 (já está ok)
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
        int cellSize = Mathf.Max(1, settings.minSpacing);  // Grid size para evitar árvores coladas (como Minecraft)
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        // Margem para overhang (copa saindo do chunk), como no seu código
        int searchMargin = settings.canopyRadius + settings.minSpacing;
        int cellX0 = Mathf.FloorToInt((float)(chunkMinX - searchMargin) / cellSize);
        int cellX1 = Mathf.FloorToInt((float)(chunkMaxX + searchMargin) / cellSize);
        int cellZ0 = Mathf.FloorToInt((float)(chunkMinZ - searchMargin) / cellSize);
        int cellZ1 = Mathf.FloorToInt((float)(chunkMaxZ + searchMargin) / cellSize);

        // Frequência para Perlin (ajuste para variações maiores/menores)
        float freq = settings.noiseScale;  // Ex.: 0.05f para clusters naturais

        List<MeshGenerator.TreeInstance> tmp = new List<MeshGenerator.TreeInstance>();

        for (int cx = cellX0; cx <= cellX1; cx++)
        {
            for (int cz = cellZ0; cz <= cellZ1; cz++)
            {
                // Perlin Noise para densidade (similar ao Minecraft: valor 0-1 decide spawn)
                // Use constantes para hash (como no seu código) para determinismo
                float noiseX = (cx * 12.9898f + settings.seed) * freq;
                float noiseZ = (cz * 78.233f + settings.seed) * freq;
                float sample = Mathf.PerlinNoise(noiseX, noiseZ);  // 0-1

                // Spawn se sample < density (inverta se quiser > para áreas mais densas)
                if (sample > settings.density) continue;  // Ajuste: quanto maior sample, menos spawn (para raridade)

                // Posição central na célula (com offset random para variação, como Minecraft)
                int worldX = cx * cellSize + Mathf.RoundToInt(Mathf.PerlinNoise(noiseX + 1f, noiseZ + 1f) * (cellSize - 1));
                int worldZ = cz * cellSize + Mathf.RoundToInt(Mathf.PerlinNoise(noiseX + 2f, noiseZ + 2f) * (cellSize - 1));

                // Verifique se dentro do chunk expandido
                if (worldX < chunkMinX - searchMargin || worldX > chunkMaxX + searchMargin ||
                    worldZ < chunkMinZ - searchMargin || worldZ > chunkMaxZ + searchMargin) continue;

                // Encontre altura da superfície (use sua função GetSurfaceHeight)
                int surfaceY = GetSurfaceHeight(worldX, worldZ);
                if (surfaceY <= 0 || surfaceY >= Chunk.SizeY) continue;

                // Condições Minecraft-like: só em Grass/Dirt, não em água/cliffs
                BlockType groundType = GetSurfaceBlockType(worldX, worldZ);
                if (groundType != BlockType.Grass && groundType != BlockType.Dirt) continue;
                if (IsCliff(worldX, worldZ, CliffTreshold)) continue;  // Sua função existente

                // Variação de altura do tronco com outra Perlin (como no seu código)
                float heightNoise = Mathf.PerlinNoise((worldX + 0.1f) * 0.137f + settings.seed * 0.001f, (worldZ + 0.1f) * 0.243f + settings.seed * 0.001f);
                int trunkH = settings.minHeight + Mathf.RoundToInt(heightNoise * (settings.maxHeight - settings.minHeight + 1));

                // Adicione a instância
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

        // Converta para NativeArray (para jobs/Burst)
        var arr = new NativeArray<MeshGenerator.TreeInstance>(tmp.Count, Allocator.Persistent);
        for (int i = 0; i < tmp.Count; i++) arr[i] = tmp[i];
        return arr;
    }


    private int GetHeightFromNoise(float noise, float sumAmp)
    {
        float centered = noise - sumAmp * 0.5f;  // Center around sumAmp/2 for ± variation
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
            return BlockType.Stone; // ⛰️ topo de montanha alta
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

    // 🔥 NOVO: função IsCliff replicando a do MeshGenerator (calcula alturas vizinhas manualmente)
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

    // Copie esta função de FloorDiv do MeshGenerator.cs para alinhar com coords negativas
    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
        return q;
    }

}