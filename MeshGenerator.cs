
using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;



public static class MeshGenerator
{
    private static readonly Vector3Int[] FaceChecks =
    {
        new Vector3Int( 0,  0,  1), // Frente
        new Vector3Int( 0,  0, -1), // Trás
        new Vector3Int( 0,  1,  0), // Cima
        new Vector3Int( 0, -1,  0), // Baixo
        new Vector3Int( 1,  0,  0), // Direita
        new Vector3Int(-1,  0,  0), // Esquerda
    };

    private static readonly Vector3[,] FaceVertices = new Vector3[6, 4]
    {
        { new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) }, // Frente
        { new Vector3(1,0,0), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0) }, // Trás
        { new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(0,1,0) }, // Cima
        { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1) }, // Baixo
        { new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1) }, // Direita
        { new Vector3(0,0,0), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0) }, // Esquerda
    };

    public static MeshBuildResult BuildChunkMesh(
        Vector2Int coord,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight, int heightVariation,
        float globalOffsetX, float globalOffsetZ,
        NativeArray<BlockTextureMapping> blockMappings,
        int atlasTilesX, int atlasTilesY,
        bool generateSides = true, float seaLevel = 32,
        NativeArray<WarpLayer> warpLayers = default,
        NativeArray<NoiseLayer> caveLayers = default,  // Novo
        float caveThreshold = 0.5f,
        int caveStride = 1,  // NOVO
    int maxCaveDepthMultiplier = 1)  // NOVO

    {
        // 1) Gerar cache de alturas
        int[,] heightCache = GenerateHeightCache(coord, noiseLayers, baseHeight, heightVariation, globalOffsetX, globalOffsetZ, warpLayers);

        // 2) Popular voxels (blockType e solid)
        var (blockType, solid) = PopulateVoxels(heightCache, seaLevel, blockMappings, coord, caveLayers, caveThreshold, globalOffsetX, globalOffsetZ,maxCaveDepthMultiplier,caveStride);  // Atualize chamada

        // 3) Gerar mesh (faces)
        var (vertices, opaqueTriangles, waterTriangles, uvs, normals) = GenerateMesh(heightCache, blockType, solid, blockMappings, atlasTilesX, atlasTilesY, generateSides, seaLevel);

        return new MeshBuildResult(coord, vertices, opaqueTriangles, waterTriangles, uvs, normals);
    }

    private static int[,] GenerateHeightCache(
        Vector2Int coord,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight, int heightVariation,
        float globalOffsetX, float globalOffsetZ,
        NativeArray<WarpLayer> warpLayers)
    {
        int[,] heightCache = new int[Chunk.SizeX + 2, Chunk.SizeZ + 2]; // +2 para bordas externas
        int baseWorldX = coord.x * Chunk.SizeX;
        int baseWorldZ = coord.y * Chunk.SizeZ;

        for (int lx = -1; lx <= Chunk.SizeX; lx++)
        {
            for (int lz = -1; lz <= Chunk.SizeZ; lz++)
            {
                int worldX = baseWorldX + lx;
                int worldZ = baseWorldZ + lz;

                // Compute domain warping (distorção)
                float warpX = 0f;
                float warpZ = 0f;
                float sumWarpAmp = 0f;

                if (warpLayers.Length > 0)
                {
                    for (int i = 0; i < warpLayers.Length; i++)
                    {
                        var layer = warpLayers[i];
                        if (!layer.enabled) continue;

                        // Amostra dois ruídos separados para X e Z (use offsets ligeiramente diferentes para evitar correlação)
                        float baseNx = worldX + layer.offset.x;
                        float baseNz = worldZ + layer.offset.y;

                        // Warp para X: use um canal (ex.: Perlin com shift)
                        float sampleX = MyNoise.OctavePerlin(baseNx + 100f, baseNz, layer); // Shift arbitrário para diferenciar
                        warpX += sampleX * layer.amplitude;

                        // Warp para Z: outro canal
                        float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);
                        warpZ += sampleZ * layer.amplitude;

                        sumWarpAmp += math.max(1e-5f, layer.amplitude);
                    }
                    if (sumWarpAmp > 0f)
                    {
                        warpX /= sumWarpAmp; // Normaliza
                        warpZ /= sumWarpAmp;
                    }
                    warpX = (warpX - 0.5f) * 2f; // Centra em [-1,1] para distorção simétrica
                    warpZ = (warpZ - 0.5f) * 2f;
                }

                // Agora aplique o warping às coordenadas das noiseLayers principais
                float totalNoise = 0f;
                float sumAmp = 0f;

                if (noiseLayers.Length > 0)
                {
                    for (int i = 0; i < noiseLayers.Length; i++)
                    {
                        var layer = noiseLayers[i];
                        if (!layer.enabled) continue;

                        // Aplique warp aqui: distorça nx/nz
                        float nx = (worldX + warpX) + layer.offset.x;  // Warp aplicado
                        float nz = (worldZ + warpZ) + layer.offset.y;

                        float sample = MyNoise.OctavePerlin(nx, nz, layer);
                        if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                        {
                            sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                        }
                        totalNoise += sample * layer.amplitude;
                        sumAmp += math.max(1e-5f, layer.amplitude);
                    }
                    if (sumAmp > 0f) totalNoise /= sumAmp;
                }
                else
                {
                    // Fallback noise (com warping aplicado)
                    float nx = (worldX + warpX) * 0.05f + globalOffsetX;
                    float nz = (worldZ + warpZ) * 0.05f + globalOffsetZ;
                    totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
                }

                // Compute height from noise
                heightCache[lx + 1, lz + 1] = GetHeightFromNoise(totalNoise, baseHeight, heightVariation);
            }
        }

        return heightCache;
    }

    private static (BlockType[,,] blockType, bool[,,] solid) PopulateVoxels(
        int[,] heightCache,
        float seaLevel,
        NativeArray<BlockTextureMapping> blockMappings,
        Vector2Int coord,
        NativeArray<NoiseLayer> caveLayers,
        float caveThreshold,
        float globalOffsetX, float globalOffsetZ, int maxCaveDepthMultiplier,int caveStride)
    {
        const int border = 1; // +1 em cada lado
        BlockType[,,] blockType = new BlockType[Chunk.SizeX + 2 * border, Chunk.SizeY, Chunk.SizeZ + 2 * border];
        bool[,,] solid = new bool[Chunk.SizeX + 2 * border, Chunk.SizeY, Chunk.SizeZ + 2 * border];

        int baseWorldX = coord.x * Chunk.SizeX;
        int baseWorldZ = coord.y * Chunk.SizeZ;

        for (int lx = -border; lx < Chunk.SizeX + border; lx++)
        {
            for (int lz = -border; lz < Chunk.SizeZ + border; lz++)
            {
                int cacheX = lx + border; // Ajuste para indexar heightCache corretamente (0 a SizeX+1)
                int cacheZ = lz + border;
                int h = heightCache[cacheX, cacheZ]; // heightCache é [SizeX+2, SizeZ+2]
                bool isBeachArea = (h <= seaLevel + 2);

                // Preencher sólidos iniciais
                for (int y = 0; y <= h; y++)
                {
                    BlockType bt;
                    if (y == h)
                    {
                        bt = isBeachArea ? BlockType.Sand : BlockType.Grass;
                    }
                    else if (y > h - 4)
                    {
                        bt = isBeachArea ? BlockType.Sand : BlockType.Dirt;
                    }
                    else
                    {
                        bt = BlockType.Stone;
                    }
                    blockType[lx + border, y, lz + border] = bt; // Index ajustado para array com border
                    var mapping = blockMappings[(int)bt];
                    solid[lx + border, y, lz + border] = mapping.isSolid;
                }

                // Aplicar cavernas (agora nas bordas também)
        if (caveLayers.Length > 0)
        {
            // int cacheX = lx + border;
            // int cacheZ = lz + border;
            // int h = heightCache[cacheX, cacheZ];
            int maxCaveY = Mathf.Min(h, (int)seaLevel * maxCaveDepthMultiplier);

            if (maxCaveY < 2) continue; // Sem cavernas se muito raso

            int stride = Mathf.Max(1, caveStride);
            int numCoarse = (maxCaveY / stride) + 2;
            float[] coarseNoise = new float[numCoarse]; // ~70 floats/coluna, negligible

            float worldX = baseWorldX + lx; // SEM +globalOffsetX (bug fix: offsets já têm)
            float worldZ = baseWorldZ + lz;

            // COMPUTE COARSE SAMPLES (apenas 1/stride das amostras!)
            for (int ci = 0; ci < numCoarse; ci++)
            {
                float cy = (ci + 0.5f) * stride; // Centro da célula para melhor interp
                if (cy > maxCaveY) cy = maxCaveY;

                float totalCaveNoise = 0f;
                float sumAmp = 0f;
                for (int i = 0; i < caveLayers.Length; i++)
                {
                    var layer = caveLayers[i];
                    if (!layer.enabled) continue;

                    float nx = worldX + layer.offset.x;
                    float ny = cy;
                    float nz = worldZ + layer.offset.y;

                    float sample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
                    if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                    {
                        sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                    }
                    totalCaveNoise += sample * layer.amplitude;
                    sumAmp += layer.amplitude;
                }
                coarseNoise[ci] = (sumAmp > 0f) ? totalCaveNoise / sumAmp : 0f;
            }

            // CARVE COM INTERPOLAÇÃO LINEAR (suave!)
            for (int y = 1; y <= h; y++)
            {
                if (y > maxCaveY) break;

                int ci = y / stride;
                int ci1 = ci + 1;
                if (ci1 >= numCoarse) ci1 = numCoarse - 1;

                float t = (y % stride) / (float)stride;
                float interpNoise = Mathf.Lerp(coarseNoise[ci], coarseNoise[ci1], t);

                if (interpNoise > caveThreshold)
                {
                    int ix = lx + border;
                    int iz = lz + border;
                    blockType[ix, y, iz] = BlockType.Air;
                    solid[ix, y, iz] = false;
                }
            }
        }
                // Preencher água (acima de h até seaLevel)
                for (int y = h + 1; y <= seaLevel; y++)
                {
                    blockType[lx + border, y, lz + border] = BlockType.Water;
                    var mapping = blockMappings[(int)BlockType.Water];
                    solid[lx + border, y, lz + border] = mapping.isSolid;
                }
            }
        }

        return (blockType, solid);
    }



    private static (List<Vector3> vertices, List<int> opaqueTriangles, List<int> waterTriangles, List<Vector2> uvs, List<Vector3> normals) GenerateMesh(
        int[,] heightCache,
        BlockType[,,] blockType,
        bool[,,] solid,
        NativeArray<BlockTextureMapping> blockMappings,
        int atlasTilesX, int atlasTilesY,
        bool generateSides,
        float seaLevel)
    {
        const int border = 1;
        List<Vector3> vertices = new List<Vector3>(4096);
        List<Vector2> uvs = new List<Vector2>(4096);
        List<Vector3> normals = new List<Vector3>(4096);
        List<int> opaqueTriangles = new List<int>(4096 * 3);
        List<int> waterTriangles = new List<int>(4096 * 3);

        for (int x = 0; x < Chunk.SizeX; x++)
        {
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int h = heightCache[x + 1, z + 1]; // heightCache ainda é +2
                int maxY = Mathf.Max(h, (int)seaLevel);

                for (int y = 0; y <= maxY; y++)
                {
                    int internalX = x + border; // Ajuste para array com border
                    int internalZ = z + border;
                    if (!solid[internalX, y, internalZ] && blockType[internalX, y, internalZ] != BlockType.Water)
                        continue;

                    for (int dir = 0; dir < 6; dir++)
                    {
                        if (!generateSides && (dir == 4 || dir == 5)) continue;

                        Vector3Int check = FaceChecks[dir];
                        int nx = internalX + check.x; // Use índices internos com border
                        int ny = y + check.y;
                        int nz = internalZ + check.z;

                        bool neighborSolid = true;

                        // Agora, como arrays têm border, nx/nz/ny sempre estarão dentro (não precisa de check fora)
                        if (nx >= 0 && nx < Chunk.SizeX + 2 * border && ny >= 0 && ny < Chunk.SizeY && nz >= 0 && nz < Chunk.SizeZ + 2 * border)
                        {
                            BlockType nbType = blockType[nx, ny, nz];
                            var nbMap = blockMappings[(int)nbType];
                            var curMap = blockMappings[(int)blockType[internalX, y, internalZ]];

                            if (curMap.isEmpty && nbMap.isEmpty)
                            {
                                neighborSolid = true;
                            }
                            else if (curMap.isEmpty && !nbMap.isEmpty)
                            {
                                neighborSolid = nbMap.isSolid;
                            }
                            else if (!curMap.isEmpty && nbMap.isEmpty)
                            {
                                neighborSolid = false;
                            }
                            else
                            {
                                neighborSolid = nbMap.isSolid;
                            }
                        }
                        // Não precisa mais do else para "fora do chunk" — bordas cobrem

                        if (!neighborSolid)
                        {
                            BlockType currentType = blockType[internalX, y, internalZ];
                            int vIndex = AddFace(vertices, uvs, normals, x, y, z, dir, currentType, blockMappings, atlasTilesX, atlasTilesY); // Use x,y,z originais (sem border) para posições

                            List<int> targetTris = (currentType == BlockType.Water) ? waterTriangles : opaqueTriangles;
                            targetTris.Add(vIndex + 0); targetTris.Add(vIndex + 1); targetTris.Add(vIndex + 2);
                            targetTris.Add(vIndex + 2); targetTris.Add(vIndex + 3); targetTris.Add(vIndex + 0);
                        }
                    }
                }
            }
        }

        return (vertices, opaqueTriangles, waterTriangles, uvs, normals);
    }
    static int GetHeightFromNoise(float noise, int baseHeight, int heightVariation)
    {
        return Mathf.Clamp(baseHeight + Mathf.FloorToInt((noise - 0.5f) * 2f * heightVariation),
                           1, Chunk.SizeY - 1);
    }

    private static int AddFace(List<Vector3> verts, List<Vector2> uvs, List<Vector3> norms,
        int x, int y, int z, int dir, BlockType tileType,
        NativeArray<BlockTextureMapping> blockMappings, int atlasTilesX, int atlasTilesY)
    {
        int vIndex = verts.Count;

        // Configuração do rebaixamento da água (0.1f a 0.2f costuma ser o ideal)
        float waterOffset = 0.15f;

        for (int i = 0; i < 4; i++)
        {
            Vector3 vertPos = new Vector3(x, y, z) + FaceVertices[dir, i];

            // Se o bloco atual for água, aplicamos o rebaixamento
            if (tileType == BlockType.Water)
            {
                // Se for a face de CIMA (dir == 2), baixamos todos os 4 vértices
                if (dir == 2)
                {
                    vertPos.y -= waterOffset;
                }
                // Se forem as faces LATERAIS (0, 1, 4, 5), baixamos apenas os vértices do topo
                else if (dir != 3) // Ignora a face de baixo (dir 3)
                {
                    if (FaceVertices[dir, i].y >= 1f)
                    {
                        vertPos.y -= waterOffset;
                    }
                }
            }

            verts.Add(vertPos);
        }

        // Determina qual tile usar para essa face
        BlockFace face = (dir == 2) ? BlockFace.Top : (dir == 3) ? BlockFace.Bottom : BlockFace.Side;
        BlockTextureMapping m = blockMappings[(int)tileType];
        Vector2Int tileCoord;
        switch (face)
        {
            case BlockFace.Top: tileCoord = m.top; break;
            case BlockFace.Bottom: tileCoord = m.bottom; break;
            default: tileCoord = m.side; break;
        }

        float tileW = 1f / atlasTilesX;
        float tileH = 1f / atlasTilesY;

        float padU = tileW * 0.001f;
        float padV = tileH * 0.001f;

        float uMin = tileCoord.x * tileW + padU;
        float vMin = tileCoord.y * tileH + padV;
        float uRange = tileW - 2f * padU;
        float vRange = tileH - 2f * padV;

        for (int i = 0; i < 4; i++)
        {
            Vector3 lv = FaceVertices[dir, i];
            float pu = 0f, pv = 0f;
            switch (dir)
            {
                case 0: pu = lv.x; pv = lv.y; break; // Frente
                case 1: pu = 1f - lv.x; pv = lv.y; break; // Trás
                case 2: pu = lv.x; pv = 1f - lv.z; break; // Cima
                case 3: pu = lv.x; pv = lv.z; break; // Baixo
                case 4: pu = 1f - lv.z; pv = lv.y; break; // Direita
                case 5: pu = lv.z; pv = lv.y; break; // Esquerda
            }

            float u = uMin + pu * uRange;
            float v = vMin + pv * vRange;
            uvs.Add(new Vector2(u, v));
        }

        Vector3 normal = (Vector3)FaceChecks[dir];
        norms.Add(normal); norms.Add(normal);
        norms.Add(normal); norms.Add(normal);

        return vIndex;
    }
}

public class MeshBuildResult
{
    public Vector2Int coord;
    public int expectedGen;
    public List<Vector3> vertices;
    public List<int> opaqueTriangles;
    public List<int> waterTriangles;
    public List<Vector2> uvs;
    public List<Vector3> normals;

    public MeshBuildResult(Vector2Int coord, List<Vector3> v, List<int> opaqueT, List<int> waterT, List<Vector2> u, List<Vector3> n)
    {
        this.coord = coord;
        vertices = v;
        opaqueTriangles = opaqueT;
        waterTriangles = waterT;
        uvs = u;
        normals = n;
    }
}