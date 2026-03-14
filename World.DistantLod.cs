using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public partial class World
{
    private struct DistantTerrainTileKey : IEquatable<DistantTerrainTileKey>
    {
        public readonly int lodLevel;
        public readonly Vector2Int coord;

        public DistantTerrainTileKey(int lodLevel, Vector2Int coord)
        {
            this.lodLevel = lodLevel;
            this.coord = coord;
        }

        public bool Equals(DistantTerrainTileKey other)
        {
            return lodLevel == other.lodLevel && coord == other.coord;
        }

        public override bool Equals(object obj)
        {
            return obj is DistantTerrainTileKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + lodLevel;
                hash = hash * 31 + coord.x;
                hash = hash * 31 + coord.y;
                return hash;
            }
        }
    }

    private struct DistantTerrainTileBuildCandidate
    {
        public DistantTerrainTileKey key;
        public float distanceSq;
    }

    private const int DistantTerrainSampleCacheSoftLimit = 250000;

    [Header("Distant Terrain LOD")]
    [Tooltip("Ativa o terreno distante em tiles LOD hierarquicos.")]
    public bool enableDistantTerrainLod = false;
    [Min(0)]
    [Tooltip("Distancia maxima em chunks para o LOD distante.")]
    public int distantTerrainRenderDistance = 12;
    [Range(1, Chunk.SizeX)]
    [Tooltip("Passo base de amostragem em voxels para o primeiro nivel de LOD.")]
    public int distantTerrainSampleStep = 4;
    [Min(1)]
    [Tooltip("Quantidade maxima de tiles LOD gerados por frame.")]
    public int maxDistantTerrainBuildsPerFrame = 1;
    [Tooltip("Material usado nos tiles LOD distantes. Se vazio, usa o primeiro material de chunk.")]
    public Material distantTerrainMaterial;
    [Tooltip("Quando ativo, o LOD distante nunca fica abaixo do nivel do mar.")]
    public bool distantTerrainSnapToSeaLevel = true;
    [Tooltip("Mostra bounds dos tiles LOD distantes no Gizmo.")]
    public bool debugDrawDistantLodBounds = false;
    public Color debugDistantLodColor = new Color(0.95f, 0.55f, 0.15f, 1f);

    [Header("Distant Terrain LOD Shape")]
    [Min(1)]
    [Tooltip("Distancia, em chunks alem do renderDistance, ate a proxima queda de detalhe.")]
    public int distantTerrainLodDistanceUnitInChunks = 2;
    [Range(1.25f, 4f)]
    [Tooltip("Base logaritmica usada para aumentar o tamanho dos tiles com a distancia.")]
    public float distantTerrainLodDistanceExponent = 2f;
    [Range(0, 8)]
    [Tooltip("Nivel maximo de LOD distante. 0 = tiles de 1 chunk, 1 = 2x2 chunks, etc.")]
    public int distantTerrainMaxLodLevel = 6;
    [Tooltip("Adiciona skirts verticais nas bordas para esconder rachaduras entre niveis.")]
    public bool distantTerrainUseEdgeSkirts = true;
    [Min(0.5f)]
    [Tooltip("Profundidade minima das skirts nas bordas.")]
    public float distantTerrainEdgeSkirtDepth = 12f;

    [Header("Distant Terrain LOD Textures")]
    [Tooltip("Usa o atlas de blocos nos tiles distantes, escolhendo um bloco representativo por celula do LOD.")]
    public bool distantTerrainUseAtlasTextures = true;

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

    private readonly Dictionary<DistantTerrainTileKey, GameObject> distantTerrainTiles = new Dictionary<DistantTerrainTileKey, GameObject>();
    private readonly HashSet<DistantTerrainTileKey> tempNeededDistantTiles = new HashSet<DistantTerrainTileKey>();
    private readonly List<DistantTerrainTileKey> tempDistantTilesToRemove = new List<DistantTerrainTileKey>();
    private readonly List<DistantTerrainTileBuildCandidate> tempDistantTilesToBuild = new List<DistantTerrainTileBuildCandidate>();
    private readonly Dictionary<long, int> distantTerrainHeightCache = new Dictionary<long, int>();
    private readonly Dictionary<long, TerrainColumnContext> distantTerrainColumnContextCache = new Dictionary<long, TerrainColumnContext>();

    private Transform distantTerrainRoot;
    private bool lastEnableDistantTerrainLod;
    private int lastDistantRenderDistance = -1;
    private int lastDistantSampleStep = -1;
    private bool lastDistantSnapToSeaLevel;
    private Material lastDistantTerrainMaterial;
    private int lastDistantLodDistanceUnitInChunks = -1;
    private float lastDistantLodDistanceExponent = -1f;
    private int lastDistantMaxLodLevel = -1;
    private bool lastDistantUseEdgeSkirts;
    private float lastDistantEdgeSkirtDepth = -1f;
    private bool lastDistantUseAtlasTextures;
    private bool lastDistantUseVertexColors;
    private bool lastDistantUseAverageTextureColor;
    private int lastDistantColorSamplesPerTile = -1;
    private float lastDistantColorBrightness = -1f;
    private float lastDistantColorNoise = -1f;
    private int lastDistantTerrainConfigHash = int.MinValue;

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
        lastDistantLodDistanceUnitInChunks = Mathf.Max(1, distantTerrainLodDistanceUnitInChunks);
        lastDistantLodDistanceExponent = Mathf.Max(1.25f, distantTerrainLodDistanceExponent);
        lastDistantMaxLodLevel = Mathf.Max(0, distantTerrainMaxLodLevel);
        lastDistantUseEdgeSkirts = distantTerrainUseEdgeSkirts;
        lastDistantEdgeSkirtDepth = Mathf.Max(0.5f, distantTerrainEdgeSkirtDepth);
        lastDistantUseAtlasTextures = distantTerrainUseAtlasTextures;
        lastDistantUseVertexColors = distantTerrainUseVertexColors;
        lastDistantUseAverageTextureColor = distantTerrainUseAverageTextureColor;
        lastDistantColorSamplesPerTile = Mathf.Clamp(distantTerrainColorSamplesPerTile, 1, 8);
        lastDistantColorBrightness = distantTerrainColorBrightness;
        lastDistantColorNoise = distantTerrainColorNoise;
        lastDistantTerrainConfigHash = ComputeDistantTerrainConfigHash();

        InvalidateDistantBlockColorCache();
        InvalidateDistantTerrainSamplingCache();

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
        int lodDistanceUnit = Mathf.Max(1, distantTerrainLodDistanceUnitInChunks);
        float lodExponent = Mathf.Max(1.25f, distantTerrainLodDistanceExponent);
        int maxLodLevel = Mathf.Max(0, distantTerrainMaxLodLevel);
        int colorSamples = Mathf.Clamp(distantTerrainColorSamplesPerTile, 1, 8);
        float skirtDepth = Mathf.Max(0.5f, distantTerrainEdgeSkirtDepth);
        int terrainConfigHash = ComputeDistantTerrainConfigHash();

        bool lodConfigChanged =
            lastDistantRenderDistance != lodDistance ||
            lastDistantSampleStep != lodStep ||
            lastDistantSnapToSeaLevel != distantTerrainSnapToSeaLevel ||
            lastDistantLodDistanceUnitInChunks != lodDistanceUnit ||
            !Mathf.Approximately(lastDistantLodDistanceExponent, lodExponent) ||
            lastDistantMaxLodLevel != maxLodLevel ||
            lastDistantUseEdgeSkirts != distantTerrainUseEdgeSkirts ||
            !Mathf.Approximately(lastDistantEdgeSkirtDepth, skirtDepth) ||
            lastDistantTerrainConfigHash != terrainConfigHash;

        bool colorConfigChanged =
            lastDistantUseAtlasTextures != distantTerrainUseAtlasTextures ||
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
            lastDistantLodDistanceUnitInChunks = lodDistanceUnit;
            lastDistantLodDistanceExponent = lodExponent;
            lastDistantMaxLodLevel = maxLodLevel;
            lastDistantUseEdgeSkirts = distantTerrainUseEdgeSkirts;
            lastDistantEdgeSkirtDepth = skirtDepth;
            lastDistantUseAtlasTextures = distantTerrainUseAtlasTextures;
            lastDistantUseVertexColors = distantTerrainUseVertexColors;
            lastDistantUseAverageTextureColor = distantTerrainUseAverageTextureColor;
            lastDistantColorSamplesPerTile = colorSamples;
            lastDistantColorBrightness = distantTerrainColorBrightness;
            lastDistantColorNoise = distantTerrainColorNoise;
            lastDistantTerrainConfigHash = terrainConfigHash;

            InvalidateDistantBlockColorCache();
            InvalidateDistantTerrainSamplingCache();
            ResetDistantTerrainCache();
        }

        EnsureDistantBlockColorCache();

        Vector2Int playerChunkCoord = GetChunkCoordFromWorldPosition(player.position);
        tempNeededDistantTiles.Clear();
        CollectNeededDistantTiles(playerChunkCoord, lodDistance, maxLodLevel);

        tempDistantTilesToRemove.Clear();
        foreach (var kv in distantTerrainTiles)
        {
            if (!tempNeededDistantTiles.Contains(kv.Key))
                tempDistantTilesToRemove.Add(kv.Key);
        }

        for (int i = 0; i < tempDistantTilesToRemove.Count; i++)
            RemoveDistantTerrainTile(tempDistantTilesToRemove[i]);

        tempDistantTilesToBuild.Clear();
        foreach (DistantTerrainTileKey key in tempNeededDistantTiles)
        {
            if (distantTerrainTiles.ContainsKey(key))
                continue;

            tempDistantTilesToBuild.Add(new DistantTerrainTileBuildCandidate
            {
                key = key,
                distanceSq = GetTileDistanceSqToPlayer(key, player.position)
            });
        }

        tempDistantTilesToBuild.Sort((a, b) => a.distanceSq.CompareTo(b.distanceSq));

        int buildBudget = Mathf.Max(1, maxDistantTerrainBuildsPerFrame);
        int buildCount = Mathf.Min(buildBudget, tempDistantTilesToBuild.Count);
        for (int i = 0; i < buildCount; i++)
            CreateOrUpdateDistantTerrainTile(tempDistantTilesToBuild[i].key);
    }

    private void DrawDistantTerrainLodGizmos()
    {
        if (!debugDrawDistantLodBounds)
            return;

        foreach (var kv in distantTerrainTiles)
            DrawBoundsGizmo(GetTileBoundsFromKey(kv.Key), debugDistantLodColor, false);
    }

    private void ResetDistantTerrainCache()
    {
        ClearAllDistantTerrainChunks();
        InvalidateDistantTerrainSamplingCache();

        if (enableDistantTerrainLod)
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
        foreach (var kv in distantTerrainTiles)
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
        foreach (var kv in distantTerrainTiles)
        {
            if (kv.Value != null)
                SafeDestroyObject(kv.Value);
        }

        distantTerrainTiles.Clear();
        tempNeededDistantTiles.Clear();
        tempDistantTilesToRemove.Clear();
        tempDistantTilesToBuild.Clear();

        if (distantTerrainRoot != null)
        {
            SafeDestroyObject(distantTerrainRoot.gameObject);
            distantTerrainRoot = null;
        }
    }

    private void RemoveDistantTerrainChunk(Vector2Int coord)
    {
        if (distantTerrainTiles.Count == 0)
            return;

        tempDistantTilesToRemove.Clear();
        foreach (var kv in distantTerrainTiles)
        {
            if (TileContainsChunk(kv.Key, coord))
                tempDistantTilesToRemove.Add(kv.Key);
        }

        for (int i = 0; i < tempDistantTilesToRemove.Count; i++)
            RemoveDistantTerrainTile(tempDistantTilesToRemove[i]);
    }

    private void RemoveDistantTerrainTile(DistantTerrainTileKey key)
    {
        if (!distantTerrainTiles.TryGetValue(key, out GameObject tileObj))
            return;

        if (tileObj != null)
            SafeDestroyObject(tileObj);

        distantTerrainTiles.Remove(key);
    }

    private void CreateOrUpdateDistantTerrainTile(DistantTerrainTileKey key)
    {
        if (!distantTerrainTiles.TryGetValue(key, out GameObject tileObj) || tileObj == null)
        {
            tileObj = CreateDistantTerrainTileObject(key);
            distantTerrainTiles[key] = tileObj;
        }

        GetTileBlockBounds(key, out int minBlockX, out int minBlockZ, out int spanBlocks);

        tileObj.transform.position = new Vector3(minBlockX, 0f, minBlockZ);
        tileObj.name = $"DistantLod_L{key.lodLevel}_{key.coord.x}_{key.coord.y}";

        MeshFilter mf = tileObj.GetComponent<MeshFilter>();
        if (mf.sharedMesh == null)
        {
            Mesh newMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            newMesh.MarkDynamic();
            mf.sharedMesh = newMesh;
        }

        BuildDistantTerrainMesh(key, mf.sharedMesh);
        ApplyDistantTerrainTileBiomeTint(key, tileObj.GetComponent<MeshRenderer>());
        tileObj.SetActive(mf.sharedMesh.vertexCount > 0);
    }

    private GameObject CreateDistantTerrainTileObject(DistantTerrainTileKey key)
    {
        EnsureDistantTerrainRoot();

        GameObject obj = new GameObject($"DistantLod_L{key.lodLevel}_{key.coord.x}_{key.coord.y}");
        obj.transform.SetParent(distantTerrainRoot, false);

        MeshFilter mf = obj.AddComponent<MeshFilter>();
        MeshRenderer mr = obj.AddComponent<MeshRenderer>();

        Mesh mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.MarkDynamic();
        mf.sharedMesh = mesh;

        mr.sharedMaterial = GetDistantTerrainMaterial();

        return obj;
    }

    private void BuildDistantTerrainMesh(DistantTerrainTileKey key, Mesh mesh)
    {
        int baseStep = Mathf.Clamp(distantTerrainSampleStep, 1, Chunk.SizeX);
        int cellStride = baseStep << key.lodLevel;

        GetTileBlockBounds(key, out int minBlockX, out int minBlockZ, out int spanBlocks);

        List<int> sampleXs = BuildSampleAxis(spanBlocks, cellStride);
        List<int> sampleZs = BuildSampleAxis(spanBlocks, cellStride);

        int vertCols = sampleXs.Count;
        int vertRows = sampleZs.Count;

        if (vertCols < 2 || vertRows < 2)
        {
            mesh.Clear();
            return;
        }

        if (CanUseTexturedDistantTerrain())
        {
            BuildDistantTerrainTexturedMesh(
                key,
                mesh,
                cellStride,
                minBlockX,
                minBlockZ,
                sampleXs,
                sampleZs,
                vertCols,
                vertRows);
            return;
        }

        BuildDistantTerrainColorMesh(
            mesh,
            cellStride,
            minBlockX,
            minBlockZ,
            spanBlocks,
            sampleXs,
            sampleZs,
            vertCols,
            vertRows);
    }

    private void BuildDistantTerrainColorMesh(
        Mesh mesh,
        int cellStride,
        int minBlockX,
        int minBlockZ,
        int spanBlocks,
        List<int> sampleXs,
        List<int> sampleZs,
        int vertCols,
        int vertRows)
    {
        List<Vector3> vertices = new List<Vector3>(vertCols * vertRows);
        List<Vector2> uv = new List<Vector2>(vertCols * vertRows);
        List<Color32> colors = new List<Color32>(vertCols * vertRows);
        List<int> triangles = new List<int>((vertCols - 1) * (vertRows - 1) * 6);
        int[,] vertexIndices = new int[vertCols, vertRows];

        for (int z = 0; z < vertRows; z++)
        {
            int localZ = sampleZs[z];
            int worldZ = minBlockZ + localZ;

            for (int x = 0; x < vertCols; x++)
            {
                int localX = sampleXs[x];
                int worldX = minBlockX + localX;

                int terrainHeight = GetCachedDistantTerrainHeight(worldX, worldZ);
                float y = terrainHeight;
                if (distantTerrainSnapToSeaLevel && y < seaLevel)
                    y = seaLevel;

                vertexIndices[x, z] = vertices.Count;
                vertices.Add(new Vector3(localX, y, localZ));
                uv.Add(new Vector2(spanBlocks > 0 ? (float)localX / spanBlocks : 0f, spanBlocks > 0 ? (float)localZ / spanBlocks : 0f));
                colors.Add((Color32)GetDistantTerrainVertexColor(worldX, worldZ, terrainHeight));
            }
        }

        for (int z = 0; z < vertRows - 1; z++)
        {
            for (int x = 0; x < vertCols - 1; x++)
            {
                int i0 = vertexIndices[x, z];
                int i1 = vertexIndices[x + 1, z];
                int i2 = vertexIndices[x, z + 1];
                int i3 = vertexIndices[x + 1, z + 1];

                triangles.Add(i0);
                triangles.Add(i2);
                triangles.Add(i1);

                triangles.Add(i1);
                triangles.Add(i2);
                triangles.Add(i3);
            }
        }

        if (distantTerrainUseEdgeSkirts)
        {
            float skirtDepth = Mathf.Max(distantTerrainEdgeSkirtDepth, cellStride * 0.75f);

            int[] northEdge = new int[vertCols];
            int[] southEdge = new int[vertCols];
            int[] westEdge = new int[vertRows];
            int[] eastEdge = new int[vertRows];

            for (int x = 0; x < vertCols; x++)
            {
                northEdge[x] = vertexIndices[x, 0];
                southEdge[x] = vertexIndices[x, vertRows - 1];
            }

            for (int z = 0; z < vertRows; z++)
            {
                westEdge[z] = vertexIndices[0, z];
                eastEdge[z] = vertexIndices[vertCols - 1, z];
            }

            AddDistantTerrainSkirt(vertices, uv, colors, triangles, northEdge, skirtDepth);
            AddDistantTerrainSkirt(vertices, uv, colors, triangles, southEdge, skirtDepth);
            AddDistantTerrainSkirt(vertices, uv, colors, triangles, westEdge, skirtDepth);
            AddDistantTerrainSkirt(vertices, uv, colors, triangles, eastEdge, skirtDepth);
        }

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uv);
        mesh.SetColors(colors);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private void BuildDistantTerrainTexturedMesh(
        DistantTerrainTileKey key,
        Mesh mesh,
        int cellStride,
        int minBlockX,
        int minBlockZ,
        List<int> sampleXs,
        List<int> sampleZs,
        int vertCols,
        int vertRows)
    {
        float[,] heights = new float[vertCols, vertRows];
        for (int z = 0; z < vertRows; z++)
        {
            int localZ = sampleZs[z];
            int worldZ = minBlockZ + localZ;

            for (int x = 0; x < vertCols; x++)
            {
                int localX = sampleXs[x];
                int worldX = minBlockX + localX;
                int terrainHeight = GetCachedDistantTerrainHeight(worldX, worldZ);
                float y = terrainHeight;
                if (distantTerrainSnapToSeaLevel && y < seaLevel)
                    y = seaLevel;

                heights[x, z] = y;
            }
        }

        Vector3[,] gridNormals = ComputeDistantTerrainGridNormals(sampleXs, sampleZs, heights, vertCols, vertRows);

        int cellCount = (vertCols - 1) * (vertRows - 1);
        int estimatedSkirtQuadCount = distantTerrainUseEdgeSkirts ? ((vertCols - 1) + (vertCols - 1) + (vertRows - 1) + (vertRows - 1)) : 0;
        int totalQuadCount = cellCount + estimatedSkirtQuadCount;

        List<Vector3> vertices = new List<Vector3>(totalQuadCount * 4);
        List<Vector3> normals = new List<Vector3>(totalQuadCount * 4);
        List<Vector2> uv0 = new List<Vector2>(totalQuadCount * 4);
        List<Vector2> uv1 = new List<Vector2>(totalQuadCount * 4);
        List<Vector4> extraUv = new List<Vector4>(totalQuadCount * 4);
        List<Color32> colors = new List<Color32>(totalQuadCount * 4);
        List<int> triangles = new List<int>(cellCount * 6 + estimatedSkirtQuadCount * 12);

        for (int z = 0; z < vertRows - 1; z++)
        {
            for (int x = 0; x < vertCols - 1; x++)
            {
                int localX0 = sampleXs[x];
                int localX1 = sampleXs[x + 1];
                int localZ0 = sampleZs[z];
                int localZ1 = sampleZs[z + 1];

                int centerWorldX = minBlockX + Mathf.FloorToInt((localX0 + localX1) * 0.5f);
                int centerWorldZ = minBlockZ + Mathf.FloorToInt((localZ0 + localZ1) * 0.5f);
                int cellMinWorldX = minBlockX + localX0;
                int cellMaxWorldX = minBlockX + localX1;
                int cellMinWorldZ = minBlockZ + localZ0;
                int cellMaxWorldZ = minBlockZ + localZ1;
                BlockType surfaceBlock = GetRepresentativeSurfaceBlockTypeForLodCell(cellMinWorldX, cellMaxWorldX, cellMinWorldZ, cellMaxWorldZ);

                AddDistantTerrainTopQuad(
                    vertices,
                    normals,
                    uv0,
                    uv1,
                    extraUv,
                    colors,
                    triangles,
                    new Vector3(localX0, heights[x, z], localZ0),
                    new Vector3(localX1, heights[x + 1, z], localZ0),
                    new Vector3(localX0, heights[x, z + 1], localZ1),
                    new Vector3(localX1, heights[x + 1, z + 1], localZ1),
                    gridNormals[x, z],
                    gridNormals[x + 1, z],
                    gridNormals[x, z + 1],
                    gridNormals[x + 1, z + 1],
                    surfaceBlock,
                    centerWorldX,
                    centerWorldZ);
            }
        }

        if (distantTerrainUseEdgeSkirts)
        {
            float skirtDepth = Mathf.Max(distantTerrainEdgeSkirtDepth, cellStride * 0.75f);

            for (int x = 0; x < vertCols - 1; x++)
            {
                int localX0 = sampleXs[x];
                int localX1 = sampleXs[x + 1];

                int northCenterWorldX = minBlockX + Mathf.FloorToInt((localX0 + localX1) * 0.5f);
                int northCenterWorldZ = minBlockZ + Mathf.FloorToInt(sampleZs[0] * 0.5f + sampleZs[1] * 0.5f);
                int northHeight = GetCachedDistantTerrainHeight(northCenterWorldX, northCenterWorldZ);
                BlockType northBlock = GetRepresentativeSurfaceBlockTypeForLodCell(northCenterWorldX, northCenterWorldZ, northHeight);
                AddDistantTerrainTexturedSkirt(
                    vertices,
                    normals,
                    uv0,
                    uv1,
                    extraUv,
                    colors,
                    triangles,
                    new Vector3(localX0, heights[x, 0], sampleZs[0]),
                    new Vector3(localX1, heights[x + 1, 0], sampleZs[0]),
                    Vector3.back,
                    skirtDepth,
                    northBlock,
                    northCenterWorldX,
                    northCenterWorldZ);

                int southRow = vertRows - 2;
                int southCenterWorldX = minBlockX + Mathf.FloorToInt((localX0 + localX1) * 0.5f);
                int southCenterWorldZ = minBlockZ + Mathf.FloorToInt((sampleZs[southRow] + sampleZs[southRow + 1]) * 0.5f);
                int southHeight = GetCachedDistantTerrainHeight(southCenterWorldX, southCenterWorldZ);
                BlockType southBlock = GetRepresentativeSurfaceBlockTypeForLodCell(southCenterWorldX, southCenterWorldZ, southHeight);
                AddDistantTerrainTexturedSkirt(
                    vertices,
                    normals,
                    uv0,
                    uv1,
                    extraUv,
                    colors,
                    triangles,
                    new Vector3(localX0, heights[x, vertRows - 1], sampleZs[vertRows - 1]),
                    new Vector3(localX1, heights[x + 1, vertRows - 1], sampleZs[vertRows - 1]),
                    Vector3.forward,
                    skirtDepth,
                    southBlock,
                    southCenterWorldX,
                    southCenterWorldZ);
            }

            for (int z = 0; z < vertRows - 1; z++)
            {
                int localZ0 = sampleZs[z];
                int localZ1 = sampleZs[z + 1];

                int westCenterWorldX = minBlockX + Mathf.FloorToInt(sampleXs[0] * 0.5f + sampleXs[1] * 0.5f);
                int westCenterWorldZ = minBlockZ + Mathf.FloorToInt((localZ0 + localZ1) * 0.5f);
                int westHeight = GetCachedDistantTerrainHeight(westCenterWorldX, westCenterWorldZ);
                BlockType westBlock = GetRepresentativeSurfaceBlockTypeForLodCell(westCenterWorldX, westCenterWorldZ, westHeight);
                AddDistantTerrainTexturedSkirt(
                    vertices,
                    normals,
                    uv0,
                    uv1,
                    extraUv,
                    colors,
                    triangles,
                    new Vector3(sampleXs[0], heights[0, z], localZ0),
                    new Vector3(sampleXs[0], heights[0, z + 1], localZ1),
                    Vector3.left,
                    skirtDepth,
                    westBlock,
                    westCenterWorldX,
                    westCenterWorldZ);

                int eastCol = vertCols - 2;
                int eastCenterWorldX = minBlockX + Mathf.FloorToInt((sampleXs[eastCol] + sampleXs[eastCol + 1]) * 0.5f);
                int eastCenterWorldZ = minBlockZ + Mathf.FloorToInt((localZ0 + localZ1) * 0.5f);
                int eastHeight = GetCachedDistantTerrainHeight(eastCenterWorldX, eastCenterWorldZ);
                BlockType eastBlock = GetRepresentativeSurfaceBlockTypeForLodCell(eastCenterWorldX, eastCenterWorldZ, eastHeight);
                AddDistantTerrainTexturedSkirt(
                    vertices,
                    normals,
                    uv0,
                    uv1,
                    extraUv,
                    colors,
                    triangles,
                    new Vector3(sampleXs[vertCols - 1], heights[vertCols - 1, z], localZ0),
                    new Vector3(sampleXs[vertCols - 1], heights[vertCols - 1, z + 1], localZ1),
                    Vector3.right,
                    skirtDepth,
                    eastBlock,
                    eastCenterWorldX,
                    eastCenterWorldZ);
            }
        }

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetUVs(2, extraUv);
        mesh.SetColors(colors);
        mesh.RecalculateBounds();
    }

    private void ApplyDistantTerrainTileBiomeTint(DistantTerrainTileKey key, MeshRenderer renderer)
    {
        if (renderer == null)
            return;

        GetTileChunkBounds(key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive);
        int centerChunkX = Mathf.FloorToInt((minChunkX + maxChunkXExclusive - 1) * 0.5f);
        int centerChunkZ = Mathf.FloorToInt((minChunkZ + maxChunkZExclusive - 1) * 0.5f);
        ApplyBiomeTintToRenderer(renderer, new Vector2Int(centerChunkX, centerChunkZ));
    }

    private bool CanUseTexturedDistantTerrain()
    {
        if (!distantTerrainUseAtlasTextures || blockData == null)
            return false;

        if (atlasTilesX <= 0 || atlasTilesY <= 0)
            return false;

        Material mat = GetDistantTerrainMaterial();
        if (mat == null)
            return false;

        return GetLodAtlasTexture2D() != null;
    }

    private Vector3[,] ComputeDistantTerrainGridNormals(
        List<int> sampleXs,
        List<int> sampleZs,
        float[,] heights,
        int vertCols,
        int vertRows)
    {
        Vector3[,] normals = new Vector3[vertCols, vertRows];

        for (int z = 0; z < vertRows; z++)
        {
            int zPrev = Mathf.Max(0, z - 1);
            int zNext = Mathf.Min(vertRows - 1, z + 1);

            for (int x = 0; x < vertCols; x++)
            {
                int xPrev = Mathf.Max(0, x - 1);
                int xNext = Mathf.Min(vertCols - 1, x + 1);

                Vector3 dx = new Vector3(
                    sampleXs[xNext] - sampleXs[xPrev],
                    heights[xNext, z] - heights[xPrev, z],
                    0f);
                Vector3 dz = new Vector3(
                    0f,
                    heights[x, zNext] - heights[x, zPrev],
                    sampleZs[zNext] - sampleZs[zPrev]);

                Vector3 normal = Vector3.Cross(dz, dx);
                normals[x, z] = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            }
        }

        return normals;
    }

    private void AddDistantTerrainTopQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> extraUv,
        List<Color32> colors,
        List<int> triangles,
        Vector3 p00,
        Vector3 p10,
        Vector3 p01,
        Vector3 p11,
        Vector3 n00,
        Vector3 n10,
        Vector3 n01,
        Vector3 n11,
        BlockType blockType,
        int worldX,
        int worldZ)
    {
        int baseIndex = vertices.Count;
        Vector2 atlasUv = GetDistantTerrainAtlasUv(blockType, BlockFace.Top);
        Vector4 extra = GetDistantTerrainExtraUv(blockType, BlockFace.Top);
        Color32 tintColor = GetDistantTerrainTextureTintColor(worldX, worldZ);

        vertices.Add(p00);
        vertices.Add(p10);
        vertices.Add(p01);
        vertices.Add(p11);

        normals.Add(n00);
        normals.Add(n10);
        normals.Add(n01);
        normals.Add(n11);

        uv0.Add(GetTerrainLikeTopUv(p00));
        uv0.Add(GetTerrainLikeTopUv(p10));
        uv0.Add(GetTerrainLikeTopUv(p01));
        uv0.Add(GetTerrainLikeTopUv(p11));

        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);

        extraUv.Add(extra);
        extraUv.Add(extra);
        extraUv.Add(extra);
        extraUv.Add(extra);

        colors.Add(tintColor);
        colors.Add(tintColor);
        colors.Add(tintColor);
        colors.Add(tintColor);

        triangles.Add(baseIndex + 0);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 1);

        triangles.Add(baseIndex + 1);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 3);
    }

    private void AddDistantTerrainTexturedSkirt(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> extraUv,
        List<Color32> colors,
        List<int> triangles,
        Vector3 top0,
        Vector3 top1,
        Vector3 outwardNormal,
        float skirtDepth,
        BlockType blockType,
        int worldX,
        int worldZ)
    {
        int baseIndex = vertices.Count;
        Vector3 bottom0 = top0;
        Vector3 bottom1 = top1;
        bottom0.y -= skirtDepth;
        bottom1.y -= skirtDepth;

        Vector2 atlasUv = GetDistantTerrainAtlasUv(blockType, BlockFace.Side);
        Vector4 extra = GetDistantTerrainExtraUv(blockType, BlockFace.Side);
        Color32 tintColor = GetDistantTerrainTextureTintColor(worldX, worldZ);

        vertices.Add(top0);
        vertices.Add(top1);
        vertices.Add(bottom0);
        vertices.Add(bottom1);

        normals.Add(outwardNormal);
        normals.Add(outwardNormal);
        normals.Add(outwardNormal);
        normals.Add(outwardNormal);

        uv0.Add(GetTerrainLikeSideUv(top0, outwardNormal));
        uv0.Add(GetTerrainLikeSideUv(top1, outwardNormal));
        uv0.Add(GetTerrainLikeSideUv(bottom0, outwardNormal));
        uv0.Add(GetTerrainLikeSideUv(bottom1, outwardNormal));

        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);

        extraUv.Add(extra);
        extraUv.Add(extra);
        extraUv.Add(extra);
        extraUv.Add(extra);

        colors.Add(tintColor);
        colors.Add(tintColor);
        colors.Add(tintColor);
        colors.Add(tintColor);

        AddDoubleSidedQuad(triangles, baseIndex + 0, baseIndex + 1, baseIndex + 2, baseIndex + 3);
    }

    private BlockType GetRepresentativeSurfaceBlockTypeForLodCell(int worldX, int worldZ, int surfaceHeight)
    {
        if (surfaceHeight < seaLevel)
            return waterBlock;

        int maxProbeY = Mathf.Clamp(surfaceHeight, 0, Chunk.SizeY - 1);
        int minProbeY = Mathf.Max(0, maxProbeY - 4);

        for (int y = maxProbeY; y >= minProbeY; y--)
        {
            BlockType blockType = GetBlockAt(new Vector3Int(worldX, y, worldZ));
            if (ShouldIgnoreDistantTerrainSurfaceBlock(blockType))
                continue;

            return blockType;
        }

        return GetSurfaceBlockTypeForLod(worldX, worldZ, surfaceHeight);
    }

    private BlockType GetRepresentativeSurfaceBlockTypeForLodCell(
        int minWorldXInclusive,
        int maxWorldXInclusive,
        int minWorldZInclusive,
        int maxWorldZInclusive)
    {
        if (minWorldXInclusive > maxWorldXInclusive)
        {
            int tmp = minWorldXInclusive;
            minWorldXInclusive = maxWorldXInclusive;
            maxWorldXInclusive = tmp;
        }

        if (minWorldZInclusive > maxWorldZInclusive)
        {
            int tmp = minWorldZInclusive;
            minWorldZInclusive = maxWorldZInclusive;
            maxWorldZInclusive = tmp;
        }

        int centerWorldX = Mathf.FloorToInt((minWorldXInclusive + maxWorldXInclusive) * 0.5f);
        int centerWorldZ = Mathf.FloorToInt((minWorldZInclusive + maxWorldZInclusive) * 0.5f);
        int centerHeight = GetCachedDistantTerrainHeight(centerWorldX, centerWorldZ);
        BlockType center = GetRepresentativeSurfaceBlockTypeForLodCell(centerWorldX, centerWorldZ, centerHeight);

        if (minWorldXInclusive == maxWorldXInclusive && minWorldZInclusive == maxWorldZInclusive)
            return center;

        int nwHeight = GetCachedDistantTerrainHeight(minWorldXInclusive, minWorldZInclusive);
        int neHeight = GetCachedDistantTerrainHeight(maxWorldXInclusive, minWorldZInclusive);
        int swHeight = GetCachedDistantTerrainHeight(minWorldXInclusive, maxWorldZInclusive);
        int seHeight = GetCachedDistantTerrainHeight(maxWorldXInclusive, maxWorldZInclusive);

        BlockType northWest = GetRepresentativeSurfaceBlockTypeForLodCell(minWorldXInclusive, minWorldZInclusive, nwHeight);
        BlockType northEast = GetRepresentativeSurfaceBlockTypeForLodCell(maxWorldXInclusive, minWorldZInclusive, neHeight);
        BlockType southWest = GetRepresentativeSurfaceBlockTypeForLodCell(minWorldXInclusive, maxWorldZInclusive, swHeight);
        BlockType southEast = GetRepresentativeSurfaceBlockTypeForLodCell(maxWorldXInclusive, maxWorldZInclusive, seHeight);

        BlockType best = center;
        int bestCount = CountBlockTypeMatches(best, center, northWest, northEast, southWest, southEast);

        int candidateCount = CountBlockTypeMatches(northWest, center, northWest, northEast, southWest, southEast);
        if (candidateCount > bestCount)
        {
            best = northWest;
            bestCount = candidateCount;
        }

        candidateCount = CountBlockTypeMatches(northEast, center, northWest, northEast, southWest, southEast);
        if (candidateCount > bestCount)
        {
            best = northEast;
            bestCount = candidateCount;
        }

        candidateCount = CountBlockTypeMatches(southWest, center, northWest, northEast, southWest, southEast);
        if (candidateCount > bestCount)
        {
            best = southWest;
            bestCount = candidateCount;
        }

        candidateCount = CountBlockTypeMatches(southEast, center, northWest, northEast, southWest, southEast);
        if (candidateCount > bestCount)
            best = southEast;

        return best;
    }

    private static int CountBlockTypeMatches(
        BlockType candidate,
        BlockType s0,
        BlockType s1,
        BlockType s2,
        BlockType s3,
        BlockType s4)
    {
        int count = 0;
        if (s0 == candidate) count++;
        if (s1 == candidate) count++;
        if (s2 == candidate) count++;
        if (s3 == candidate) count++;
        if (s4 == candidate) count++;
        return count;
    }

    private static Vector2 GetTerrainLikeTopUv(Vector3 localPos)
    {
        // Match the same UV grid convention used by the regular chunk mesher on top faces.
        return new Vector2(localPos.x + 1f, localPos.z + 1f);
    }

    private static Vector2 GetTerrainLikeSideUv(Vector3 localPos, Vector3 outwardNormal)
    {
        // For side faces, regular chunks map U to horizontal axis (x or z) and V to world Y.
        bool sideAlongZ = Mathf.Abs(outwardNormal.x) > Mathf.Abs(outwardNormal.z);
        float horizontal = sideAlongZ ? localPos.z : localPos.x;
        return new Vector2(horizontal + 1f, localPos.y);
    }

    private bool ShouldIgnoreDistantTerrainSurfaceBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return true;

        if (blockType == waterBlock)
            return false;

        if (TryGetDistantTerrainBlockMapping(blockType, out BlockTextureMapping mapping))
        {
            if (mapping.isEmpty)
                return true;

            if (!mapping.isSolid && !mapping.isLiquid)
                return true;
        }

        return false;
    }

    private bool TryGetDistantTerrainBlockMapping(BlockType blockType, out BlockTextureMapping mapping)
    {
        mapping = default(BlockTextureMapping);
        if (blockData == null)
            return false;

        BlockTextureMapping? maybeMapping = blockData.GetMapping(blockType);
        if (maybeMapping == null)
            return false;

        mapping = maybeMapping.Value;
        return true;
    }

    private Vector2 GetDistantTerrainAtlasUv(BlockType blockType, BlockFace face)
    {
        Vector2Int tile = blockData != null
            ? blockData.GetTileCoord(blockType, face)
            : Vector2Int.zero;

        float invAtlasTilesX = 1f / Mathf.Max(1, atlasTilesX);
        float invAtlasTilesY = 1f / Mathf.Max(1, atlasTilesY);
        return new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
    }

    private Vector4 GetDistantTerrainExtraUv(BlockType blockType, BlockFace face)
    {
        bool tint = false;
        if (TryGetDistantTerrainBlockMapping(blockType, out BlockTextureMapping mapping))
        {
            if (face == BlockFace.Top) tint = mapping.tintTop;
            else if (face == BlockFace.Bottom) tint = mapping.tintBottom;
            else tint = mapping.tintSide;
        }

        return new Vector4(1f, tint ? 1f : 0f, 1f, 0f);
    }

    private Color32 GetDistantTerrainTextureTintColor(int worldX, int worldZ)
    {
        if (!distantTerrainUseVertexColors)
            return new Color32(255, 255, 255, 255);

        float noise = Mathf.PerlinNoise((worldX + seed * 0.37f) * 0.09f, (worldZ - seed * 0.23f) * 0.09f);
        float variation = 1f + (noise * 2f - 1f) * Mathf.Max(0f, distantTerrainColorNoise);
        float brightness = Mathf.Max(0f, distantTerrainColorBrightness);
        byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(variation * brightness) * 255f), 0, 255);
        return new Color32(value, value, value, 255);
    }

    private void CollectNeededDistantTiles(Vector2Int playerChunkCoord, int outerDistance, int maxLodLevel)
    {
        if (outerDistance <= renderDistance)
            return;

        int rootLevel = Mathf.Max(0, maxLodLevel);
        int rootSpan = 1 << rootLevel;

        int minChunkX = playerChunkCoord.x - outerDistance;
        int maxChunkXExclusive = playerChunkCoord.x + outerDistance + 1;
        int minChunkZ = playerChunkCoord.y - outerDistance;
        int maxChunkZExclusive = playerChunkCoord.y + outerDistance + 1;

        int minTileX = FloorDiv(minChunkX, rootSpan);
        int maxTileX = FloorDiv(maxChunkXExclusive - 1, rootSpan);
        int minTileZ = FloorDiv(minChunkZ, rootSpan);
        int maxTileZ = FloorDiv(maxChunkZExclusive - 1, rootSpan);

        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        {
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                CollectNeededDistantTilesRecursive(
                    new DistantTerrainTileKey(rootLevel, new Vector2Int(tileX, tileZ)),
                    playerChunkCoord,
                    renderDistance,
                    outerDistance,
                    maxLodLevel);
            }
        }
    }

    private void CollectNeededDistantTilesRecursive(
        DistantTerrainTileKey key,
        Vector2Int playerChunkCoord,
        int innerDistance,
        int outerDistance,
        int maxLodLevel)
    {
        if (!TileIntersectsChunkSquare(key, playerChunkCoord, outerDistance))
            return;

        if (TileFullyInsideChunkSquare(key, playerChunkCoord, innerDistance))
            return;

        bool intersectsInnerRange = TileIntersectsChunkSquare(key, playerChunkCoord, innerDistance);
        if (intersectsInnerRange && key.lodLevel > 0)
        {
            int childLevel = key.lodLevel - 1;
            int childBaseX = key.coord.x * 2;
            int childBaseZ = key.coord.y * 2;

            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX, childBaseZ)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX + 1, childBaseZ)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX, childBaseZ + 1)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX + 1, childBaseZ + 1)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            return;
        }

        if (intersectsInnerRange)
            return;

        int distanceToTile = GetTileChunkDistanceToPlayer(key, playerChunkCoord);
        int expectedLodLevel = GetExpectedDistantLodLevel(distanceToTile, innerDistance, maxLodLevel);

        if (key.lodLevel > expectedLodLevel && key.lodLevel > 0)
        {
            int childLevel = key.lodLevel - 1;
            int childBaseX = key.coord.x * 2;
            int childBaseZ = key.coord.y * 2;

            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX, childBaseZ)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX + 1, childBaseZ)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX, childBaseZ + 1)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            CollectNeededDistantTilesRecursive(new DistantTerrainTileKey(childLevel, new Vector2Int(childBaseX + 1, childBaseZ + 1)), playerChunkCoord, innerDistance, outerDistance, maxLodLevel);
            return;
        }

        tempNeededDistantTiles.Add(key);
    }

    private int GetExpectedDistantLodLevel(int tileDistanceInChunks, int innerDistance, int maxLodLevel)
    {
        int lodDistance = Mathf.Max(1, tileDistanceInChunks - innerDistance + 1);
        float normalizedDistance = lodDistance / (float)Mathf.Max(1, distantTerrainLodDistanceUnitInChunks);
        if (normalizedDistance <= 1f)
            return 0;

        int level = Mathf.FloorToInt(Mathf.Log(normalizedDistance, Mathf.Max(1.25f, distantTerrainLodDistanceExponent)));
        return Mathf.Clamp(level, 0, maxLodLevel);
    }

    private static void GetTileChunkBounds(DistantTerrainTileKey key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive)
    {
        int spanChunks = 1 << key.lodLevel;
        minChunkX = key.coord.x * spanChunks;
        minChunkZ = key.coord.y * spanChunks;
        maxChunkXExclusive = minChunkX + spanChunks;
        maxChunkZExclusive = minChunkZ + spanChunks;
    }

    private static void GetTileBlockBounds(DistantTerrainTileKey key, out int minBlockX, out int minBlockZ, out int spanBlocks)
    {
        int spanChunks = 1 << key.lodLevel;
        spanBlocks = spanChunks * Chunk.SizeX;
        minBlockX = key.coord.x * spanBlocks;
        minBlockZ = key.coord.y * spanBlocks;
    }

    private static bool TileContainsChunk(DistantTerrainTileKey key, Vector2Int chunkCoord)
    {
        GetTileChunkBounds(key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive);
        return chunkCoord.x >= minChunkX && chunkCoord.x < maxChunkXExclusive &&
               chunkCoord.y >= minChunkZ && chunkCoord.y < maxChunkZExclusive;
    }

    private static bool TileIntersectsChunkSquare(DistantTerrainTileKey key, Vector2Int centerChunkCoord, int radiusInChunks)
    {
        GetTileChunkBounds(key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive);

        int squareMinX = centerChunkCoord.x - radiusInChunks;
        int squareMaxXExclusive = centerChunkCoord.x + radiusInChunks + 1;
        int squareMinZ = centerChunkCoord.y - radiusInChunks;
        int squareMaxZExclusive = centerChunkCoord.y + radiusInChunks + 1;

        return RangesIntersect(minChunkX, maxChunkXExclusive, squareMinX, squareMaxXExclusive) &&
               RangesIntersect(minChunkZ, maxChunkZExclusive, squareMinZ, squareMaxZExclusive);
    }

    private static bool TileFullyInsideChunkSquare(DistantTerrainTileKey key, Vector2Int centerChunkCoord, int radiusInChunks)
    {
        GetTileChunkBounds(key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive);

        int squareMinX = centerChunkCoord.x - radiusInChunks;
        int squareMaxXExclusive = centerChunkCoord.x + radiusInChunks + 1;
        int squareMinZ = centerChunkCoord.y - radiusInChunks;
        int squareMaxZExclusive = centerChunkCoord.y + radiusInChunks + 1;

        return minChunkX >= squareMinX &&
               maxChunkXExclusive <= squareMaxXExclusive &&
               minChunkZ >= squareMinZ &&
               maxChunkZExclusive <= squareMaxZExclusive;
    }

    private static int GetTileChunkDistanceToPlayer(DistantTerrainTileKey key, Vector2Int playerChunkCoord)
    {
        GetTileChunkBounds(key, out int minChunkX, out int minChunkZ, out int maxChunkXExclusive, out int maxChunkZExclusive);
        int dx = GetDistanceToExclusiveRange(playerChunkCoord.x, minChunkX, maxChunkXExclusive);
        int dz = GetDistanceToExclusiveRange(playerChunkCoord.y, minChunkZ, maxChunkZExclusive);
        return Mathf.Max(dx, dz);
    }

    private static int GetDistanceToExclusiveRange(int value, int minInclusive, int maxExclusive)
    {
        if (value < minInclusive)
            return minInclusive - value;
        if (value >= maxExclusive)
            return value - (maxExclusive - 1);
        return 0;
    }

    private float GetTileDistanceSqToPlayer(DistantTerrainTileKey key, Vector3 playerPosition)
    {
        GetTileBlockBounds(key, out int minBlockX, out int minBlockZ, out int spanBlocks);
        Vector3 center = new Vector3(minBlockX + spanBlocks * 0.5f, playerPosition.y, minBlockZ + spanBlocks * 0.5f);
        return (center - playerPosition).sqrMagnitude;
    }

    private Bounds GetTileBoundsFromKey(DistantTerrainTileKey key)
    {
        GetTileBlockBounds(key, out int minBlockX, out int minBlockZ, out int spanBlocks);

        return new Bounds(
            new Vector3(minBlockX + spanBlocks * 0.5f, Chunk.SizeY * 0.5f, minBlockZ + spanBlocks * 0.5f),
            new Vector3(spanBlocks, Chunk.SizeY, spanBlocks));
    }

    private static bool RangesIntersect(int minA, int maxAExclusive, int minB, int maxBExclusive)
    {
        return minA < maxBExclusive && maxAExclusive > minB;
    }

    private static void AddDistantTerrainSkirt(
        List<Vector3> vertices,
        List<Vector2> uv,
        List<Color32> colors,
        List<int> triangles,
        int[] edgeIndices,
        float skirtDepth)
    {
        if (edgeIndices == null || edgeIndices.Length < 2)
            return;

        int[] lowerIndices = new int[edgeIndices.Length];
        for (int i = 0; i < edgeIndices.Length; i++)
        {
            int topIndex = edgeIndices[i];
            Vector3 lowerVertex = vertices[topIndex];
            lowerVertex.y -= skirtDepth;

            lowerIndices[i] = vertices.Count;
            vertices.Add(lowerVertex);
            uv.Add(uv[topIndex]);
            colors.Add(colors[topIndex]);
        }

        for (int i = 0; i < edgeIndices.Length - 1; i++)
        {
            int top0 = edgeIndices[i];
            int top1 = edgeIndices[i + 1];
            int bottom0 = lowerIndices[i];
            int bottom1 = lowerIndices[i + 1];

            AddDoubleSidedQuad(triangles, top0, top1, bottom0, bottom1);
        }
    }

    private static void AddDoubleSidedQuad(List<int> triangles, int top0, int top1, int bottom0, int bottom1)
    {
        triangles.Add(top0);
        triangles.Add(bottom0);
        triangles.Add(top1);

        triangles.Add(top1);
        triangles.Add(bottom0);
        triangles.Add(bottom1);

        triangles.Add(top0);
        triangles.Add(top1);
        triangles.Add(bottom0);

        triangles.Add(top1);
        triangles.Add(bottom1);
        triangles.Add(bottom0);
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
        return GetCachedDistantTerrainContext(worldX, worldZ, surfaceHeight).surface.surfaceBlock;
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
            distantBlockTopColors[i] = GetFallbackBlockColor((BlockType)i);

        if (atlasTex != null && blockData != null)
        {
            if (blockData.mappings == null || blockData.mappings.Length == 0)
                blockData.InitializeDictionary();

            if (blockData.mappings != null)
            {
                for (int i = 0; i < blockData.mappings.Length; i++)
                {
                    BlockTextureMapping mapping = blockData.mappings[i];
                    if ((int)mapping.blockType != i)
                        continue;

                    if (TrySampleAverageTileColor(atlasTex, mapping.top, atlasTilesX, atlasTilesY, samples, out Color avg))
                        distantBlockTopColors[i] = avg;
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
            color = default(Color);
            return false;
        }

        if (tile.x < 0 || tile.x >= tilesX || tile.y < 0 || tile.y >= tilesY)
        {
            color = default(Color);
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
            color = default(Color);
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

    private void InvalidateDistantTerrainSamplingCache()
    {
        distantTerrainHeightCache.Clear();
        distantTerrainColumnContextCache.Clear();
    }

    private int GetCachedDistantTerrainHeight(int worldX, int worldZ)
    {
        long key = PackDistantTerrainSampleKey(worldX, worldZ);
        if (distantTerrainHeightCache.TryGetValue(key, out int height))
            return height;

        height = GetSurfaceHeight(worldX, worldZ);
        if (distantTerrainHeightCache.Count >= DistantTerrainSampleCacheSoftLimit)
            InvalidateDistantTerrainSamplingCache();
        distantTerrainHeightCache[key] = height;
        return height;
    }

    private TerrainColumnContext GetCachedDistantTerrainContext(int worldX, int worldZ, int surfaceHeight)
    {
        long key = PackDistantTerrainSampleKey(worldX, worldZ);
        if (distantTerrainColumnContextCache.TryGetValue(key, out TerrainColumnContext columnContext))
            return columnContext;

        int hN = GetCachedDistantTerrainHeight(worldX, worldZ + 1);
        int hS = GetCachedDistantTerrainHeight(worldX, worldZ - 1);
        int hE = GetCachedDistantTerrainHeight(worldX + 1, worldZ);
        int hW = GetCachedDistantTerrainHeight(worldX - 1, worldZ);

        columnContext = TerrainColumnSampler.CreateFromNeighborHeights(
            worldX,
            worldZ,
            surfaceHeight,
            hN,
            hS,
            hE,
            hW,
            CliffTreshold,
            baseHeight,
            seaLevel,
            GetBiomeNoiseSettings());

        if (distantTerrainColumnContextCache.Count >= DistantTerrainSampleCacheSoftLimit)
            InvalidateDistantTerrainSamplingCache();
        distantTerrainColumnContextCache[key] = columnContext;
        return columnContext;
    }

    private static long PackDistantTerrainSampleKey(int worldX, int worldZ)
    {
        return ((long)worldX << 32) ^ (uint)worldZ;
    }

    private int ComputeDistantTerrainConfigHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + seed;
            hash = hash * 31 + baseHeight;
            hash = hash * 31 + seaLevel;
            hash = hash * 31 + CliffTreshold;
            hash = hash * 31 + HashNoiseLayers(noiseLayers);
            hash = hash * 31 + HashWarpLayers(warpLayers);
            hash = hash * 31 + HashBiomeNoiseSettings(GetBiomeNoiseSettings());
            return hash;
        }
    }

    private static int HashNoiseLayers(NoiseLayer[] layers)
    {
        if (layers == null)
            return 0;

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + layers.Length;
            for (int i = 0; i < layers.Length; i++)
            {
                NoiseLayer layer = layers[i];
                hash = hash * 31 + (layer.enabled ? 1 : 0);
                hash = hash * 31 + (int)layer.role;
                hash = hash * 31 + layer.scale.GetHashCode();
                hash = hash * 31 + layer.amplitude.GetHashCode();
                hash = hash * 31 + layer.octaves;
                hash = hash * 31 + layer.persistence.GetHashCode();
                hash = hash * 31 + layer.lacunarity.GetHashCode();
                hash = hash * 31 + layer.offset.x.GetHashCode();
                hash = hash * 31 + layer.offset.y.GetHashCode();
                hash = hash * 31 + layer.maxAmp.GetHashCode();
                hash = hash * 31 + layer.redistributionModifier.GetHashCode();
                hash = hash * 31 + layer.exponent.GetHashCode();
                hash = hash * 31 + layer.verticalScale.GetHashCode();
                hash = hash * 31 + layer.ridgeFactor.GetHashCode();
            }
            return hash;
        }
    }

    private static int HashWarpLayers(WarpLayer[] layers)
    {
        if (layers == null)
            return 0;

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + layers.Length;
            for (int i = 0; i < layers.Length; i++)
            {
                WarpLayer layer = layers[i];
                hash = hash * 31 + (layer.enabled ? 1 : 0);
                hash = hash * 31 + layer.scale.GetHashCode();
                hash = hash * 31 + layer.amplitude.GetHashCode();
                hash = hash * 31 + layer.octaves;
                hash = hash * 31 + layer.persistence.GetHashCode();
                hash = hash * 31 + layer.lacunarity.GetHashCode();
                hash = hash * 31 + layer.offset.x.GetHashCode();
                hash = hash * 31 + layer.offset.y.GetHashCode();
                hash = hash * 31 + layer.maxAmp.GetHashCode();
            }
            return hash;
        }
    }

    private static int HashBiomeNoiseSettings(BiomeNoiseSettings settings)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + settings.temperatureScale.GetHashCode();
            hash = hash * 31 + settings.humidityScale.GetHashCode();
            hash = hash * 31 + settings.temperatureOffset.x.GetHashCode();
            hash = hash * 31 + settings.temperatureOffset.y.GetHashCode();
            hash = hash * 31 + settings.humidityOffset.x.GetHashCode();
            hash = hash * 31 + settings.humidityOffset.y.GetHashCode();
            hash = hash * 31 + settings.terrainBlendRange.GetHashCode();
            hash = hash * 31 + settings.desertMinTemperature.GetHashCode();
            hash = hash * 31 + settings.desertMaxHumidity.GetHashCode();
            hash = hash * 31 + settings.savannaMinTemperature.GetHashCode();
            hash = hash * 31 + settings.taigaMaxTemperature.GetHashCode();
            hash = hash * 31 + settings.taigaMinHumidity.GetHashCode();
            hash = hash * 31 + HashBiomeTerrainSettings(settings.desertTerrain);
            hash = hash * 31 + HashBiomeTerrainSettings(settings.savannaTerrain);
            hash = hash * 31 + HashBiomeTerrainSettings(settings.meadowTerrain);
            hash = hash * 31 + HashBiomeTerrainSettings(settings.taigaTerrain);
            hash = hash * 31 + settings.altitudeTemperatureFalloff.GetHashCode();
            hash = hash * 31 + settings.coldStoneStartHeightOffset.GetHashCode();
            hash = hash * 31 + settings.coldStoneBlendRange.GetHashCode();
            hash = hash * 31 + settings.coldSnowStartHeightOffset.GetHashCode();
            hash = hash * 31 + settings.coldSnowBlendRange.GetHashCode();
            hash = hash * 31 + settings.coldSnowTemperatureThreshold.GetHashCode();
            hash = hash * 31 + settings.coldSurfaceNoiseScale.GetHashCode();
            hash = hash * 31 + (int)settings.desertSurfaceBlock;
            hash = hash * 31 + (int)settings.desertSubsurfaceBlock;
            hash = hash * 31 + (int)settings.savannaSurfaceBlock;
            hash = hash * 31 + (int)settings.savannaSubsurfaceBlock;
            hash = hash * 31 + (int)settings.meadowSurfaceBlock;
            hash = hash * 31 + (int)settings.meadowSubsurfaceBlock;
            hash = hash * 31 + (int)settings.taigaSurfaceBlock;
            hash = hash * 31 + (int)settings.taigaSubsurfaceBlock;
            return hash;
        }
    }

    private static int HashBiomeTerrainSettings(BiomeTerrainSettings settings)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + settings.reliefMultiplier.GetHashCode();
            hash = hash * 31 + settings.hillsMultiplier.GetHashCode();
            hash = hash * 31 + settings.mountainMultiplier.GetHashCode();
            hash = hash * 31 + settings.erosionBias.GetHashCode();
            hash = hash * 31 + settings.erosionPower.GetHashCode();
            hash = hash * 31 + settings.flattenStrength.GetHashCode();
            hash = hash * 31 + settings.heightOffset.GetHashCode();
            return hash;
        }
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
            case BlockType.Cactus: return new Color32(70, 152, 68, 255);
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

    private static void SafeDestroyObject(UnityEngine.Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(obj);
        else
            UnityEngine.Object.DestroyImmediate(obj);
    }
}








