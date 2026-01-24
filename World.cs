
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
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
                                     // No topo da classe World, após warpLayers:
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

    private ConcurrentQueue<MeshBuildResult> meshResults = new ConcurrentQueue<MeshBuildResult>();

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

    private void Start()
    {
        Instance = this;

        // garante dicionário inicializado
        if (blockData != null) blockData.InitializeDictionary();

        // offsets base a partir do seed (aplicado globalmente por segurança)
        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        // inicializa offsets das layers (determinístico baseado no seed)
        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
            {// depois de noiseLayers[i] = layer; adicione:
                if (noiseLayers[i].redistributionModifier == 0f) noiseLayers[i].redistributionModifier = 1f;
                if (noiseLayers[i].exponent == 0f) noiseLayers[i].exponent = 1f;

                // garantir valores seguros
                if (noiseLayers[i].scale <= 0f) noiseLayers[i].scale = 0.05f;
                if (noiseLayers[i].octaves <= 0) noiseLayers[i].octaves = 1;
                if (noiseLayers[i].lacunarity <= 0f) noiseLayers[i].lacunarity = 2f;
                if (noiseLayers[i].persistence <= 0f || noiseLayers[i].persistence > 1f) noiseLayers[i].persistence = 0.5f;

                // se offset não definido, atribui um offset derivado do seed + index para variação entre layers
                NoiseLayer layer = noiseLayers[i]; // Copy to modify (since struct)
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
                WarpLayer layer = warpLayers[i]; // Copy to modify

                // Garantir valores seguros
                if (layer.scale <= 0f) layer.scale = 0.005f; // Baixa para warping
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
                // Mesmos saneamentos e precomputes que em noiseLayers
                if (caveLayers[i].redistributionModifier == 0f) caveLayers[i].redistributionModifier = 1f;
                if (caveLayers[i].exponent == 0f) caveLayers[i].exponent = 1f;

                if (caveLayers[i].scale <= 0f) caveLayers[i].scale = 0.03f; // Boa para cavernas
                if (caveLayers[i].octaves <= 0) caveLayers[i].octaves = 4;
                if (caveLayers[i].lacunarity <= 0f) caveLayers[i].lacunarity = 2f;
                if (caveLayers[i].persistence <= 0f || caveLayers[i].persistence > 1f) caveLayers[i].persistence = 0.5f;

                NoiseLayer layer = caveLayers[i];
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
        MeshBuildResult res;
        while (meshResults.TryDequeue(out res) && applied < maxMeshAppliesPerFrame)
        {
            if (res == null) continue;

            if (activeChunks.TryGetValue(res.coord, out Chunk activeChunk) && activeChunk.generation == res.expectedGen)
            {
                activeChunk.ApplyMeshData(res.vertices, res.opaqueTriangles, res.waterTriangles, res.uvs, res.normals);
                activeChunk.gameObject.SetActive(true);
                applied++;
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

        NativeArray<NoiseLayer> nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayers, Allocator.TempJob);
        NativeArray<WarpLayer> nativeWarpLayers = new NativeArray<WarpLayer>(warpLayers, Allocator.TempJob);
        NativeArray<NoiseLayer> nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayers, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockData.mappings, Allocator.TempJob);

        ChunkBuildJob job = new ChunkBuildJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            baseHeight = baseHeight,
            heightVariation = heightVariation,
            offsetX = offsetX,
            offsetZ = offsetZ,
            blockMappings = nativeBlockMappings,
            atlasTilesX = atlasTilesX,
            atlasTilesY = atlasTilesY,
            generateSides = true,
            seaLevel = seaLevel,
            warpLayers = nativeWarpLayers,
            expectedGen = expectedGen,
            caveLayers = nativeCaveLayers,
            caveThreshold = caveThreshold,
            caveStride = caveStride,
            maxCaveDepthMultiplier = maxCaveDepthMultiplier

        };

        // Schedule only the ChunkBuildJob (DisposeJob is removed)
        JobHandle computeHandle = job.Schedule();


    }

    [BurstCompile]
    public struct DisposeJob : IJob
    {
        public NativeArray<NoiseLayer> noise;
        public NativeArray<WarpLayer> warp;
        public NativeArray<BlockTextureMapping> blocks;

        public void Execute()
        {
            noise.Dispose();
            warp.Dispose();
            blocks.Dispose();
        }
    }
    [BurstCompile]
    public struct ChunkBuildJob : IJob
    {
        public Vector2Int coord;

        // Add [DeallocateOnJobCompletion] to each NativeArray field
        [DeallocateOnJobCompletion]
        public NativeArray<NoiseLayer> noiseLayers;

        [DeallocateOnJobCompletion]
        public NativeArray<WarpLayer> warpLayers;

        [DeallocateOnJobCompletion]
        public NativeArray<BlockTextureMapping> blockMappings;
        [DeallocateOnJobCompletion]
        public NativeArray<NoiseLayer> caveLayers;
        public int caveStride;
        public int maxCaveDepthMultiplier;
        public float caveThreshold;
        public int baseHeight;
        public int heightVariation;
        public float offsetX;
        public float offsetZ;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public float seaLevel;  // Note: This was typed as 'float' but used as 'int' in some places—consider changing to int if it's always integral.
        public int expectedGen;

        public void Execute()
        {
            MeshBuildResult result = MeshGenerator.BuildChunkMesh(
     coord, noiseLayers, baseHeight, heightVariation,
     offsetX, offsetZ,
     blockMappings, atlasTilesX, atlasTilesY, generateSides, seaLevel,
     warpLayers, caveLayers, caveThreshold,
    caveStride, maxCaveDepthMultiplier);  // Adicione caveLayers e caveThreshold

            result.expectedGen = expectedGen;
            World.Instance.meshResults.Enqueue(result);
        }
    }

}
