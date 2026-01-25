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

    private struct PendingMesh
    {
        public JobHandle handle;
        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector3> normals;
        public Vector2Int coord;
        public int expectedGen;
    }

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
                    activeChunk.ApplyMeshData(pm.vertices.AsArray(), pm.opaqueTriangles.AsArray(), pm.waterTriangles.AsArray(), pm.uvs.AsArray(), pm.normals.AsArray());
                    activeChunk.gameObject.SetActive(true);
                    applied++;
                }
                // Dispose NativeLists
                pm.vertices.Dispose();
                pm.opaqueTriangles.Dispose();
                pm.waterTriangles.Dispose();
                pm.uvs.Dispose();
                pm.normals.Dispose();
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
            out JobHandle handle,
            out NativeList<Vector3> vertices,
            out NativeList<int> opaqueTriangles,
            out NativeList<int> waterTriangles,
            out NativeList<Vector2> uvs,
            out NativeList<Vector3> normals
        );

        pendingMeshes.Add(new PendingMesh
        {
            handle = handle,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            uvs = uvs,
            normals = normals,
            coord = coord,
            expectedGen = expectedGen
        });
    }

}