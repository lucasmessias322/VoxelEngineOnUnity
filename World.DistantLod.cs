using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public partial class World
{
    [Header("Distant Terrain LOD")]
    [Tooltip("Ativa chunks distantes como malha simplificada baseada em heightmap.")]
    public bool enableDistantTerrainLod = false;
    [Min(0)]
    [Tooltip("Distancia maxima em chunks para o LOD distante (usa a mesma logica quadrada do renderDistance).")]
    public int distantTerrainRenderDistance = 12;
    [Range(1, Chunk.SizeX)]
    [Tooltip("Passo de amostragem em voxels para gerar a malha simplificada. Valores maiores = menos vertices.")]
    public int distantTerrainSampleStep = 4;
    [Min(1)]
    [Tooltip("Quantidade maxima de chunks LOD gerados por frame.")]
    public int maxDistantTerrainBuildsPerFrame = 1;
    [Tooltip("Material usado nos chunks LOD distantes. Se vazio, usa o primeiro material de chunk.")]
    public Material distantTerrainMaterial;
    [Tooltip("Quando ativo, o LOD distante nunca fica abaixo do nivel do mar.")]
    public bool distantTerrainSnapToSeaLevel = true;
    [Tooltip("Mostra bounds dos chunks LOD distantes no Gizmo.")]
    public bool debugDrawDistantLodBounds = false;
    public Color debugDistantLodColor = new Color(0.95f, 0.55f, 0.15f, 1f);

    [Header("Distant Terrain LOD Colors")]
    [Tooltip("Pinta a malha LOD usando vertex color para simular os blocos de superficie.")]
    public bool distantTerrainUseVertexColors = true;
    [Tooltip("Tenta usar a cor media do tile no atlas para cada bloco de superficie.")]
    public bool distantTerrainUseAverageTextureColor = true;
    [Range(1, 8)]
    [Tooltip("Quantidade de amostras por eixo para estimar a media de um tile no atlas.")]
    public int distantTerrainColorSamplesPerTile = 4;
    [Range(0.5f, 2f)]
    [Tooltip("Multiplicador final de brilho da cor dos vertices.")]
    public float distantTerrainColorBrightness = 1.0f;
    [Range(0f, 0.25f)]
    [Tooltip("Variacao sutil de cor para quebrar repeticao visual.")]
    public float distantTerrainColorNoise = 0.06f;

    private readonly Dictionary<Vector2Int, GameObject> distantTerrainChunks = new Dictionary<Vector2Int, GameObject>();
    private readonly HashSet<Vector2Int> tempNeededDistantCoords = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> tempDistantToRemove = new List<Vector2Int>();
    private readonly Queue<Vector2Int> pendingDistantBuilds = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> pendingDistantBuildsSet = new HashSet<Vector2Int>();

    private Transform distantTerrainRoot;
    private bool lastEnableDistantTerrainLod;
    private int lastDistantRenderDistance = -1;
    private int lastDistantSampleStep = -1;
    private bool lastDistantSnapToSeaLevel;
    private Material lastDistantTerrainMaterial;
    private bool lastDistantUseVertexColors;
    private bool lastDistantUseAverageTextureColor;
    private int lastDistantColorSamplesPerTile = -1;
    private float lastDistantColorBrightness = -1f;
    private float lastDistantColorNoise = -1f;

    private bool distantBlockColorCacheValid;
    private Texture2D cachedDistantColorAtlas;
    private int cachedDistantColorAtlasTilesX = -1;
    private int cachedDistantColorAtlasTilesY = -1;
    private Color[] distantBlockTopColors;

    private void InitializeDistantTerrainLodState()
    {
        lastEnableDistantTerrainLod = enableDistantTerrainLod;
        lastDistantRenderDistance = Mathf.Max(renderDistance + 1, distantTerrainRenderDistance);
        lastDistantSampleStep = Mathf.Clamp(distantTerrainSampleStep, 1, Chunk.SizeX);
        lastDistantSnapToSeaLevel = distantTerrainSnapToSeaLevel;
        lastDistantTerrainMaterial = distantTerrainMaterial;
        lastDistantUseVertexColors = distantTerrainUseVertexColors;
        lastDistantUseAverageTextureColor = distantTerrainUseAverageTextureColor;
        lastDistantColorSamplesPerTile = Mathf.Clamp(distantTerrainColorSamplesPerTile, 1, 8);
        lastDistantColorBrightness = distantTerrainColorBrightness;
        lastDistantColorNoise = distantTerrainColorNoise;
        InvalidateDistantBlockColorCache();

        if (enableDistantTerrainLod)
            EnsureDistantTerrainRoot();
    }

    private void UpdateDistantTerrainLod()
    {
        if (lastEnableDistantTerrainLod != enableDistantTerrainLod)
        {
            lastEnableDistantTerrainLod = enableDistantTerrainLod;
            if (!enableDistantTerrainLod)
            {
                ClearAllDistantTerrainChunks();
                return;
            }

            ResetDistantTerrainCache();
        }

        if (!enableDistantTerrainLod || player == null)
            return;

        EnsureDistantTerrainRoot();

        int lodDistance = Mathf.Max(renderDistance + 1, distantTerrainRenderDistance);
        int lodStep = Mathf.Clamp(distantTerrainSampleStep, 1, Chunk.SizeX);
        int colorSamples = Mathf.Clamp(distantTerrainColorSamplesPerTile, 1, 8);

        bool lodConfigChanged =
            lastDistantRenderDistance != lodDistance ||
            lastDistantSampleStep != lodStep ||
            lastDistantSnapToSeaLevel != distantTerrainSnapToSeaLevel;

        bool colorConfigChanged =
            lastDistantUseVertexColors != distantTerrainUseVertexColors ||
            lastDistantUseAverageTextureColor != distantTerrainUseAverageTextureColor ||
            lastDistantColorSamplesPerTile != colorSamples ||
            !Mathf.Approximately(lastDistantColorBrightness, distantTerrainColorBrightness) ||
            !Mathf.Approximately(lastDistantColorNoise, distantTerrainColorNoise);

        bool materialChanged = lastDistantTerrainMaterial != distantTerrainMaterial;

        if (lodConfigChanged || colorConfigChanged || materialChanged)
        {
            lastDistantRenderDistance = lodDistance;
            lastDistantSampleStep = lodStep;
            lastDistantSnapToSeaLevel = distantTerrainSnapToSeaLevel;
            lastDistantTerrainMaterial = distantTerrainMaterial;
            lastDistantUseVertexColors = distantTerrainUseVertexColors;
            lastDistantUseAverageTextureColor = distantTerrainUseAverageTextureColor;
            lastDistantColorSamplesPerTile = colorSamples;
            lastDistantColorBrightness = distantTerrainColorBrightness;
            lastDistantColorNoise = distantTerrainColorNoise;

            InvalidateDistantBlockColorCache();
            ResetDistantTerrainCache();
        }

        EnsureDistantBlockColorCache();

        Vector2Int playerChunkCoord = GetChunkCoordFromWorldPosition(player.position);
        tempNeededDistantCoords.Clear();

        for (int x = -lodDistance; x <= lodDistance; x++)
        {
            for (int z = -lodDistance; z <= lodDistance; z++)
            {
                int chebyshevDistance = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
                if (chebyshevDistance <= renderDistance)
                    continue;

                tempNeededDistantCoords.Add(new Vector2Int(playerChunkCoord.x + x, playerChunkCoord.y + z));
            }
        }

        tempDistantToRemove.Clear();
        foreach (var kv in distantTerrainChunks)
        {
            if (!tempNeededDistantCoords.Contains(kv.Key) || activeChunks.ContainsKey(kv.Key))
                tempDistantToRemove.Add(kv.Key);
        }

        for (int i = 0; i < tempDistantToRemove.Count; i++)
        {
            RemoveDistantTerrainChunk(tempDistantToRemove[i]);
        }

        foreach (Vector2Int coord in tempNeededDistantCoords)
        {
            if (activeChunks.ContainsKey(coord))
            {
                RemoveDistantTerrainChunk(coord);
                continue;
            }

            if (distantTerrainChunks.ContainsKey(coord))
                continue;

            if (pendingDistantBuildsSet.Add(coord))
                pendingDistantBuilds.Enqueue(coord);
        }

        int buildsThisFrame = 0;
        int buildBudget = Mathf.Max(1, maxDistantTerrainBuildsPerFrame);

        while (buildsThisFrame < buildBudget && pendingDistantBuilds.Count > 0)
        {
            Vector2Int coord = pendingDistantBuilds.Dequeue();
            pendingDistantBuildsSet.Remove(coord);

            if (activeChunks.ContainsKey(coord))
                continue;
            if (!tempNeededDistantCoords.Contains(coord))
                continue;

            CreateOrUpdateDistantTerrainChunk(coord);
            buildsThisFrame++;
        }
    }

    private void DrawDistantTerrainLodGizmos()
    {
        if (!debugDrawDistantLodBounds)
            return;

        foreach (var kv in distantTerrainChunks)
        {
            DrawBoundsGizmo(GetChunkBoundsFromCoord(kv.Key), debugDistantLodColor, false);
        }
    }

    private void ResetDistantTerrainCache()
    {
        ClearAllDistantTerrainChunks();
        EnsureDistantTerrainRoot();
    }

    private void EnsureDistantTerrainRoot()
    {
        if (distantTerrainRoot != null)
            return;

        GameObject root = new GameObject("DistantTerrainLOD");
        root.transform.SetParent(transform, false);
        distantTerrainRoot = root.transform;
    }

    private void RefreshDistantTerrainMaterials()
    {
        Material targetMaterial = GetDistantTerrainMaterial();
        foreach (var kv in distantTerrainChunks)
        {
            if (kv.Value == null)
                continue;

            MeshRenderer mr = kv.Value.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = targetMaterial;
        }
    }

    private Material GetDistantTerrainMaterial()
    {
        if (distantTerrainMaterial != null)
            return distantTerrainMaterial;
        if (Material != null && Material.Length > 0)
            return Material[0];
        return null;
    }

    private void ClearAllDistantTerrainChunks()
    {
        foreach (var kv in distantTerrainChunks)
        {
            if (kv.Value != null)
                SafeDestroyObject(kv.Value);
        }

        distantTerrainChunks.Clear();
        tempNeededDistantCoords.Clear();
        tempDistantToRemove.Clear();
        pendingDistantBuilds.Clear();
        pendingDistantBuildsSet.Clear();

        if (distantTerrainRoot != null)
        {
            SafeDestroyObject(distantTerrainRoot.gameObject);
            distantTerrainRoot = null;
        }
    }

    private void RemoveDistantTerrainChunk(Vector2Int coord)
    {
        if (!distantTerrainChunks.TryGetValue(coord, out GameObject chunkObj))
            return;

        if (chunkObj != null)
            SafeDestroyObject(chunkObj);

        distantTerrainChunks.Remove(coord);
    }

    private void CreateOrUpdateDistantTerrainChunk(Vector2Int coord)
    {
        if (!distantTerrainChunks.TryGetValue(coord, out GameObject chunkObj) || chunkObj == null)
        {
            chunkObj = CreateDistantTerrainChunkObject(coord);
            distantTerrainChunks[coord] = chunkObj;
        }

        Vector3 worldPos = new Vector3(coord.x * Chunk.SizeX, 0f, coord.y * Chunk.SizeZ);
        chunkObj.transform.position = worldPos;
        chunkObj.name = $"DistantLod_{coord.x}_{coord.y}";

        MeshFilter mf = chunkObj.GetComponent<MeshFilter>();
        if (mf.sharedMesh == null)
        {
            Mesh newMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            newMesh.MarkDynamic();
            mf.sharedMesh = newMesh;
        }

        BuildDistantTerrainMesh(coord, mf.sharedMesh);
        chunkObj.SetActive(mf.sharedMesh.vertexCount > 0);
    }

    private GameObject CreateDistantTerrainChunkObject(Vector2Int coord)
    {
        EnsureDistantTerrainRoot();

        GameObject obj = new GameObject($"DistantLod_{coord.x}_{coord.y}");
        obj.transform.SetParent(distantTerrainRoot, false);

        MeshFilter mf = obj.AddComponent<MeshFilter>();
        MeshRenderer mr = obj.AddComponent<MeshRenderer>();

        Mesh mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.MarkDynamic();
        mf.sharedMesh = mesh;

        mr.sharedMaterial = GetDistantTerrainMaterial();

        return obj;
    }

    private void BuildDistantTerrainMesh(Vector2Int coord, Mesh mesh)
    {
        int step = Mathf.Clamp(distantTerrainSampleStep, 1, Chunk.SizeX);

        List<int> sampleXs = BuildSampleAxis(Chunk.SizeX, step);
        List<int> sampleZs = BuildSampleAxis(Chunk.SizeZ, step);

        int vertCols = sampleXs.Count;
        int vertRows = sampleZs.Count;

        if (vertCols < 2 || vertRows < 2)
        {
            mesh.Clear();
            return;
        }

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        List<Vector3> vertices = new List<Vector3>(vertCols * vertRows);
        List<Vector2> uv = new List<Vector2>(vertCols * vertRows);
        List<Color> colors = new List<Color>(vertCols * vertRows);
        List<int> triangles = new List<int>((vertCols - 1) * (vertRows - 1) * 6);

        for (int z = 0; z < vertRows; z++)
        {
            int localZ = sampleZs[z];
            int worldZ = chunkMinZ + localZ;

            for (int x = 0; x < vertCols; x++)
            {
                int localX = sampleXs[x];
                int worldX = chunkMinX + localX;

                int terrainHeight = GetSurfaceHeight(worldX, worldZ);
                float y = terrainHeight;
                if (distantTerrainSnapToSeaLevel && y < seaLevel)
                    y = seaLevel;

                vertices.Add(new Vector3(localX, y, localZ));
                uv.Add(new Vector2((float)localX / Chunk.SizeX, (float)localZ / Chunk.SizeZ));
                colors.Add(GetDistantTerrainVertexColor(worldX, worldZ, terrainHeight));
            }
        }

        for (int z = 0; z < vertRows - 1; z++)
        {
            for (int x = 0; x < vertCols - 1; x++)
            {
                int i0 = x + z * vertCols;
                int i1 = i0 + 1;
                int i2 = i0 + vertCols;
                int i3 = i2 + 1;

                triangles.Add(i0);
                triangles.Add(i2);
                triangles.Add(i1);

                triangles.Add(i1);
                triangles.Add(i2);
                triangles.Add(i3);
            }
        }

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uv);
        mesh.SetColors(colors);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private Color GetDistantTerrainVertexColor(int worldX, int worldZ, int terrainHeight)
    {
        if (!distantTerrainUseVertexColors)
            return Color.white;

        BlockType surfaceType;
        if (terrainHeight < seaLevel)
        {
            surfaceType = waterBlock;
        }
        else
        {
            surfaceType = GetSurfaceBlockTypeForLod(worldX, worldZ, terrainHeight);
        }

        Color baseColor = GetBlockSurfaceColor(surfaceType);

        float noise = Mathf.PerlinNoise((worldX + seed * 0.37f) * 0.09f, (worldZ - seed * 0.23f) * 0.09f);
        float variation = 1f + (noise * 2f - 1f) * Mathf.Max(0f, distantTerrainColorNoise);
        float brightness = Mathf.Max(0f, distantTerrainColorBrightness);

        Color finalColor = baseColor * (variation * brightness);
        finalColor.a = 1f;
        return finalColor;
    }

    private BlockType GetSurfaceBlockTypeForLod(int worldX, int worldZ, int surfaceHeight)
    {
        bool isBeachArea = surfaceHeight <= seaLevel + 2;
        bool isCliff = IsCliff(worldX, worldZ, CliffTreshold);
        bool isHighMountain = surfaceHeight >= baseHeight + 70;

        if (isHighMountain || isCliff)
            return BlockType.Stone;

        return isBeachArea ? BlockType.Sand : GetBiomeSurfaceBlock(worldX, worldZ);
    }

    private Color GetBlockSurfaceColor(BlockType blockType)
    {
        EnsureDistantBlockColorCache();

        int index = (int)blockType;
        if (distantBlockTopColors == null || index < 0 || index >= distantBlockTopColors.Length)
            return GetFallbackBlockColor(blockType);

        return distantBlockTopColors[index];
    }

    private void EnsureDistantBlockColorCache()
    {
        int blockCount = System.Enum.GetValues(typeof(BlockType)).Length;
        if (distantBlockTopColors == null || distantBlockTopColors.Length != blockCount)
            distantBlockTopColors = new Color[blockCount];

        Texture2D atlasTex = distantTerrainUseAverageTextureColor ? GetLodAtlasTexture2D() : null;
        int samples = Mathf.Clamp(distantTerrainColorSamplesPerTile, 1, 8);

        bool needsRebuild =
            !distantBlockColorCacheValid ||
            cachedDistantColorAtlas != atlasTex ||
            cachedDistantColorAtlasTilesX != atlasTilesX ||
            cachedDistantColorAtlasTilesY != atlasTilesY;

        if (!needsRebuild)
            return;

        for (int i = 0; i < blockCount; i++)
        {
            distantBlockTopColors[i] = GetFallbackBlockColor((BlockType)i);
        }

        if (atlasTex != null && blockData != null)
        {
            if (blockData.mappings == null || blockData.mappings.Length == 0)
                blockData.InitializeDictionary();

            if (blockData.mappings != null)
            {
                for (int i = 0; i < blockData.mappings.Length; i++)
                {
                    BlockTextureMapping m = blockData.mappings[i];
                    if ((int)m.blockType != i)
                        continue;

                    if (TrySampleAverageTileColor(atlasTex, m.top, atlasTilesX, atlasTilesY, samples, out Color avg))
                    {
                        distantBlockTopColors[i] = avg;
                    }
                }
            }
        }

        cachedDistantColorAtlas = atlasTex;
        cachedDistantColorAtlasTilesX = atlasTilesX;
        cachedDistantColorAtlasTilesY = atlasTilesY;
        distantBlockColorCacheValid = true;
    }

    private Texture2D GetLodAtlasTexture2D()
    {
        Material mat = GetDistantTerrainMaterial();
        if (mat == null)
            return null;

        Texture tex = null;
        if (mat.HasProperty("_BaseMap"))
            tex = mat.GetTexture("_BaseMap");
        if (tex == null && mat.HasProperty("_MainTex"))
            tex = mat.GetTexture("_MainTex");

        return tex as Texture2D;
    }

    private static bool TrySampleAverageTileColor(
        Texture2D atlas,
        Vector2Int tile,
        int tilesX,
        int tilesY,
        int samplesPerAxis,
        out Color color)
    {
        if (atlas == null || tilesX <= 0 || tilesY <= 0 || samplesPerAxis <= 0)
        {
            color = default;
            return false;
        }

        if (tile.x < 0 || tile.x >= tilesX || tile.y < 0 || tile.y >= tilesY)
        {
            color = default;
            return false;
        }

        try
        {
            float tileMinU = tile.x / (float)tilesX;
            float tileMaxU = (tile.x + 1f) / tilesX;
            float tileMinV = tile.y / (float)tilesY;
            float tileMaxV = (tile.y + 1f) / tilesY;

            Color acc = Color.black;
            int count = samplesPerAxis * samplesPerAxis;

            for (int sy = 0; sy < samplesPerAxis; sy++)
            {
                for (int sx = 0; sx < samplesPerAxis; sx++)
                {
                    float tx = (sx + 0.5f) / samplesPerAxis;
                    float ty = (sy + 0.5f) / samplesPerAxis;
                    float u = Mathf.Lerp(tileMinU, tileMaxU, tx);
                    float v = Mathf.Lerp(tileMinV, tileMaxV, ty);
                    acc += atlas.GetPixelBilinear(u, v);
                }
            }

            color = acc / Mathf.Max(1, count);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private void InvalidateDistantBlockColorCache()
    {
        distantBlockColorCacheValid = false;
        cachedDistantColorAtlas = null;
        cachedDistantColorAtlasTilesX = -1;
        cachedDistantColorAtlasTilesY = -1;
    }

    private static Color GetFallbackBlockColor(BlockType blockType)
    {
        switch (blockType)
        {
            case BlockType.Grass: return new Color32(104, 165, 66, 255);
            case BlockType.Dirt: return new Color32(120, 84, 58, 255);
            case BlockType.Stone: return new Color32(127, 127, 127, 255);
            case BlockType.Sand: return new Color32(214, 202, 142, 255);
            case BlockType.Water: return new Color32(63, 110, 203, 255);
            case BlockType.Bedrock: return new Color32(70, 70, 70, 255);
            case BlockType.Leaves: return new Color32(74, 124, 54, 255);
            case BlockType.Log: return new Color32(102, 83, 61, 255);
            case BlockType.Deepslate: return new Color32(63, 63, 69, 255);
            case BlockType.Snow: return new Color32(235, 239, 242, 255);
            case BlockType.glass: return new Color32(184, 226, 238, 255);
            case BlockType.glowstone: return new Color32(215, 182, 85, 255);
            case BlockType.oak_planks: return new Color32(170, 128, 78, 255);
            case BlockType.short_grass4: return new Color32(97, 156, 62, 255);
            case BlockType.birch_log: return new Color32(198, 186, 148, 255);
            case BlockType.CoalOre: return new Color32(85, 85, 85, 255);
            case BlockType.IronOre: return new Color32(177, 147, 122, 255);
            case BlockType.GoldOre: return new Color32(212, 170, 66, 255);
            case BlockType.RedstoneOre: return new Color32(176, 52, 52, 255);
            case BlockType.DiamondOre: return new Color32(76, 195, 219, 255);
            case BlockType.EmeraldOre: return new Color32(55, 173, 91, 255);
            default: return new Color32(200, 200, 200, 255);
        }
    }

    private static List<int> BuildSampleAxis(int maxInclusive, int step)
    {
        List<int> samples = new List<int>();
        int clampedStep = Mathf.Max(1, step);

        for (int p = 0; p < maxInclusive; p += clampedStep)
            samples.Add(p);

        if (samples.Count == 0 || samples[samples.Count - 1] != maxInclusive)
            samples.Add(maxInclusive);

        return samples;
    }

    private static void SafeDestroyObject(Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(obj);
        else
            Object.DestroyImmediate(obj);
    }
}
