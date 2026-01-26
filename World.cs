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
    public Camera mainCamera;

    public Transform player;
    public GameObject chunkPrefab;
    public int collisionDistance = 2; // em chunks

    [Header("Render Distance")]
    public int chunkRenderDistance = 8;

    // Bedrock-style vertical subchunk culling
    public int subchunkRenderUp = 2;
    public int subchunkRenderDown = 1;

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
        public NativeList<byte> lightValues; // novo: luz por vértice (0..15)
        public NativeList<byte> subchunkIds; // novo: id do subchunk por vértice
        public NativeArray<int> surfaceSubY; // <-- adicionar isso
        public Vector2Int coord;
        public int expectedGen;
        // 🔥 NOVO – voxels do chunk (para collider)
        public NativeArray<byte> voxelBytes;
    }

    private Vector2Int lastPlayerChunk;

    private void Start()
    {
        Instance = this;



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
            if (!pm.handle.IsCompleted) continue;

            pm.handle.Complete();

            // --- Primeiro: obtemos o chunk ativo (se existir) ---
            Chunk activeChunk;
            bool chunkExists = activeChunks.TryGetValue(pm.coord, out activeChunk) && activeChunk.generation == pm.expectedGen;

            // --- 1) Sempre converta voxelBytes para voxelData e set no chunk ANTES de aplicar mesh ---
            if (pm.voxelBytes.IsCreated)
            {
                int sx = Chunk.SizeX;
                int sy = Chunk.SizeY;
                int sz = Chunk.SizeZ;

                // converte flattened -> 3D
                byte[,,] voxelData = new byte[sx, sy, sz];
                for (int x = 0; x < sx; x++)
                    for (int z = 0; z < sz; z++)
                        for (int y = 0; y < sy; y++)
                        {
                            int idx = x + y * sx + z * (sx * sy);
                            voxelData[x, y, z] = pm.voxelBytes[idx];
                        }

                if (chunkExists)
                {
                    activeChunk.SetVoxelData(voxelData);
                }
                // se chunk não existe (foi descarregado), ainda assim devemos descartar os dados em CPU (não há chunk para setar)
                pm.voxelBytes.Dispose();
            }

            // --- 2) Agora, se o chunk existe, aplique o mesh (split por subchunk) ---
            if (chunkExists)
            {
                int subCount = MeshGenerator.SubChunkCountY;

                List<Vector3>[] verts = new List<Vector3>[subCount];
                List<Vector2>[] uvs = new List<Vector2>[subCount];
                List<Vector3>[] norms = new List<Vector3>[subCount];
                List<byte>[] lights = new List<byte>[subCount];
                List<int>[] opaqueTris = new List<int>[subCount];
                List<int>[] waterTris = new List<int>[subCount];
                Dictionary<int, int>[] remap = new Dictionary<int, int>[subCount];

                for (int s = 0; s < subCount; s++)
                {
                    verts[s] = new List<Vector3>(); uvs[s] = new List<Vector2>(); norms[s] = new List<Vector3>(); lights[s] = new List<byte>();
                    opaqueTris[s] = new List<int>(); waterTris[s] = new List<int>(); remap[s] = new Dictionary<int, int>();
                }

                for (int vi = 0; vi < pm.vertices.Length; vi++)
                {
                    byte sid = pm.subchunkIds[vi];
                    int s = sid;
                    int newIndex = verts[s].Count;
                    remap[s][vi] = newIndex;
                    verts[s].Add(pm.vertices[vi]);
                    uvs[s].Add(pm.uvs[vi]);
                    norms[s].Add(pm.normals[vi]);
                    lights[s].Add(pm.lightValues[vi]);
                }

                for (int t = 0; t < pm.opaqueTriangles.Length; t += 3)
                {
                    int a = pm.opaqueTriangles[t + 0];
                    int b = pm.opaqueTriangles[t + 1];
                    int c = pm.opaqueTriangles[t + 2];
                    byte sid = pm.subchunkIds[a];
                    int s = sid;
                    int ra = remap[s][a];
                    int rb = remap[s][b];
                    int rc = remap[s][c];
                    opaqueTris[s].Add(ra); opaqueTris[s].Add(rb); opaqueTris[s].Add(rc);
                }

                for (int t = 0; t < pm.waterTriangles.Length; t += 3)
                {
                    int a = pm.waterTriangles[t + 0];
                    int b = pm.waterTriangles[t + 1];
                    int c = pm.waterTriangles[t + 2];
                    byte sid = pm.subchunkIds[a];
                    int s = sid;
                    int ra = remap[s][a];
                    int rb = remap[s][b];
                    int rc = remap[s][c];
                    waterTris[s].Add(ra); waterTris[s].Add(rb); waterTris[s].Add(rc);
                }

                bool anyActive = false;
                for (int s = 0; s < subCount; s++)
                {
                    if (verts[s].Count == 0)
                    {
                        activeChunk.ApplySubChunkMeshData(s, null, null, null, null, null, null);
                        continue;
                    }

                    anyActive = true;
                    Vector3[] aVerts = verts[s].ToArray();
                    Vector2[] aUVs = uvs[s].ToArray();
                    Vector3[] aNorms = norms[s].ToArray();
                    byte[] aLights = lights[s].ToArray();
                    int[] aOpaque = opaqueTris[s].ToArray();
                    int[] aWater = waterTris[s].ToArray();

                    activeChunk.ApplySubChunkMeshData(s, aVerts, aOpaque, aWater, aUVs, aNorms, aLights);
                }

                activeChunk.gameObject.SetActive(anyActive);
                if (pm.surfaceSubY.IsCreated)
                    activeChunk.surfaceSubY = pm.surfaceSubY[0];

                applied++;
            }

            // --- 3) dispose de todos os NativeLists/Arrays (sempre) ---
            pm.vertices.Dispose();
            pm.opaqueTriangles.Dispose();
            pm.waterTriangles.Dispose();
            pm.uvs.Dispose();
            pm.normals.Dispose();
            pm.lightValues.Dispose();
            pm.subchunkIds.Dispose();
            if (pm.surfaceSubY.IsCreated) pm.surfaceSubY.Dispose();

            // remover da lista
            pendingMeshes.RemoveAt(i);
        }
        Vector2Int currentChunk = WorldToChunk(player.position);

        if (currentChunk != lastPlayerChunk)
        {
            UpdateChunkColliders(player.position);
            lastPlayerChunk = currentChunk;
        }

    }


    private void LateUpdate()
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(Camera.main);

        int playerSubY = Mathf.FloorToInt(player.position.y / Chunk.SubChunkSize);

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;

            chunk.UpdateSubchunkVisibilityBedrock(
                playerSubY,
                chunk.surfaceSubY,
                subchunkRenderUp,
                subchunkRenderDown,
                planes
            );
        }
    }

    void Awake()
    {   // garante dicionário inicializado
        if (blockData != null) blockData.InitializeDictionary();
        InitBlockCaches();
    }
    void InitBlockCaches()
    {
        int count = System.Enum.GetValues(typeof(BlockType)).Length;

        BlockDataSO.IsSolidCache = new bool[count];
        BlockDataSO.IsEmptyCache = new bool[count];

        foreach (var mapping in blockData.blockTextures)
        {
            int id = (int)mapping.blockType;
            BlockDataSO.IsSolidCache[id] = mapping.isSolid;
            BlockDataSO.IsEmptyCache[id] = mapping.isEmpty;
        }
    }
    void UpdateChunkColliders(Vector3 playerPos)
    {
        Vector2Int playerChunk = WorldToChunk(playerPos);

        foreach (var chunk in activeChunks.Values)

        {
            int dist = DistanceInChunks(chunk.coord, playerChunk);

            if (dist <= collisionDistance)
            {
                chunk.EnableColliders();
            }
            else
            {
                chunk.DisableColliders();
            }
        }
    }
    Vector2Int WorldToChunk(Vector3 pos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(pos.x / Chunk.SizeX),
            Mathf.FloorToInt(pos.z / Chunk.SizeZ)
        );
    }

    private void UpdateChunks()
    {
        Vector2Int playerChunk = new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );

        HashSet<Vector2Int> needed = new HashSet<Vector2Int>();

        for (int x = -chunkRenderDistance; x <= chunkRenderDistance; x++)
        {
            for (int z = -chunkRenderDistance; z <= chunkRenderDistance; z++)
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
    int DistanceInChunks(Vector2Int a, Vector2Int b)
    {
        return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
    }
    // Em World.cs — adicione este método na classe World
    public bool IsSolidAtWorld(int wx, int wy, int wz)
    {
        // transforma coords de mundo (inteiras) para chunk + local
        int cx = Mathf.FloorToInt((float)wx / Chunk.SizeX);
        int cz = Mathf.FloorToInt((float)wz / Chunk.SizeZ);
        Vector2Int coord = new Vector2Int(cx, cz);

        if (!activeChunks.TryGetValue(coord, out var chunk))
            return false; // chunk não carregado -> assume vazio para evitar colisões inesperadas

        if (chunk.voxelData == null) return false;

        int localX = wx - cx * Chunk.SizeX;
        if (localX < 0) localX += Chunk.SizeX;

        int localZ = wz - cz * Chunk.SizeZ;
        if (localZ < 0) localZ += Chunk.SizeZ;

        int localY = wy;

        if (localX < 0 || localX >= Chunk.SizeX ||
            localY < 0 || localY >= Chunk.SizeY ||
            localZ < 0 || localZ >= Chunk.SizeZ)
            return false;

        byte block = chunk.voxelData[localX, localY, localZ];
        return BlockDataSO.IsSolidCache[block];
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
            out NativeList<Vector3> normals,
             out NativeList<byte> vertexLights, // novo out
             out NativeList<byte> vertexSubchunkIds,
             out NativeArray<int> surfaceSubY,
             out NativeArray<byte> voxelBytes   // 🔥 NOVO


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
            subchunkIds = vertexSubchunkIds,
            surfaceSubY = surfaceSubY, // <-- guardar aqui
            coord = coord,
            expectedGen = expectedGen,
            voxelBytes = voxelBytes, // 🔥 AQUI
        });
    }


}
