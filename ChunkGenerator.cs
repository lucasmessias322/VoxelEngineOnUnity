
// ChunkGenerator.cs
using System;
using Unity.Collections;
using UnityEngine;

public class ChunkGenerator
{
    private Biome[] biomes;
    private float biomeNoiseScale;
    private int biomeSeed;
    private int chunkWidth;
    private int chunkHeight;
    private int chunkDepth;
    private int bedrockLayers;
    private int dirtLayers;
    private int seed;
    private NoiseSettings noiseSettings;
    private int seaLevel;

    // Tree settings
    private int treePadding;            // padding em blocos (quanto expandir o padded para conter árvores)
    private int treeSeed;
    private float treeSpawnChance;      // probabilidade por posição (0..1)
    private int treeMinHeight;
    private int treeMaxHeight;
    private int treeLeafRadius;
    private BlockType woodBlock;
    private BlockType leavesBlock;
    private bool useFixedTreeHeight;
    private int fixedTreeHeight;

    public ChunkGenerator(
     Biome[] biomes, float biomeNoiseScale, int biomeSeed, int chunkWidth, int chunkHeight, int chunkDepth,
     int bedrockLayers, int dirtLayers, int seed, NoiseSettings noiseSettings, int seaLevel,
     int treePadding = 3, int treeSeed = 54321, float treeSpawnChance = 0.02f,
     int treeMinHeight = 4, int treeMaxHeight = 6, int treeLeafRadius = 2,
     BlockType woodBlock = BlockType.Placeholder, BlockType leavesBlock = BlockType.Placeholder,
     bool useFixedTreeHeight = false, int fixedTreeHeight = 5)
    {
        this.biomes = biomes;
        this.biomeNoiseScale = biomeNoiseScale;
        this.biomeSeed = biomeSeed;
        this.chunkWidth = chunkWidth;
        this.chunkHeight = chunkHeight;
        this.chunkDepth = chunkDepth;
        this.bedrockLayers = bedrockLayers;
        this.dirtLayers = dirtLayers;
        this.seed = seed;
        this.noiseSettings = noiseSettings;
        this.seaLevel = seaLevel;

        // tree
        this.treePadding = Mathf.Max(1, treePadding);
        this.treeSeed = treeSeed;
        this.treeSpawnChance = Mathf.Clamp01(treeSpawnChance);
        this.treeMinHeight = Mathf.Max(1, treeMinHeight);
        this.treeMaxHeight = Mathf.Max(treeMinHeight, treeMaxHeight);
        this.treeLeafRadius = Mathf.Max(1, treeLeafRadius);
        this.woodBlock = woodBlock;
        this.leavesBlock = leavesBlock;

        this.useFixedTreeHeight = useFixedTreeHeight;
        this.fixedTreeHeight = Mathf.Max(1, fixedTreeHeight);
    }



    // Substituir o método existente por este
    public NativeArray<int> GenerateFlattenedPadded_Native(Vector2Int chunkCoord, out int pw, out int ph, out int pd, Allocator allocator = Allocator.TempJob)
    {
        int w = chunkWidth, h = chunkHeight, d = chunkDepth;
        int pad = treePadding;

        pw = w + 2 * pad;
        ph = h;
        pd = d + 2 * pad;

        int lpw = pw, lph = ph, lpd = pd;

        int total = lpw * lph * lpd;

        // NativeArray retornado (alocado com o allocator solicitado)
        var paddedFlat = new NativeArray<int>(total, allocator);

        // **BUILD IN A MANAGED ARRAY FIRST** (muito mais rápido para escrever elemento-a-elemento)
        var flatManaged = new int[total];

        // helper local (usa as cópias)
        int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

        // 1) Precompute heightMap (lpw x lpd)
        int startWorldX = chunkCoord.x * chunkWidth - pad;
        int startWorldZ = chunkCoord.y * chunkDepth - pad;
        var heightMap = new int[lpw, lpd];
        for (int lx = 0; lx < lpw; lx++)
        {
            for (int lz = 0; lz < lpd; lz++)
            {
                int worldX = startWorldX + lx;
                int worldZ = startWorldZ + lz;
                heightMap[lx, lz] = GetHeightAt(worldX, worldZ);
            }
        }

        // 2) Preencher flatManaged diretamente (sem criar BlockType[,,] e sem NativeArray.Set_Item por elemento)
        for (int lx = 0; lx < lpw; lx++)
        {
            for (int lz = 0; lz < lpd; lz++)
            {
                int worldX = chunkCoord.x * chunkWidth + lx - pad;
                int worldZ = chunkCoord.y * chunkDepth + lz - pad;
                int groundLevel = heightMap[lx, lz];

                for (int y = 0; y < lph; y++)
                {
                    var bt = DetermineBlockType(y, groundLevel, worldX, worldZ, heightMap, lx, lz, lpw, lpd);
                    int idx = FlattenIndex(lx, y, lz);
                    flatManaged[idx] = (int)bt;
                }
            }
        }

        // 3) Colocar árvores diretamente no flatManaged (versão que opera em int[])
        PlaceTreesInFlattened(flatManaged, heightMap, chunkCoord, lpw, lph, lpd, pad);

        // Finalmente copia o bloco inteiro para o NativeArray (memcpy interno, muito rápido)
        paddedFlat.CopyFrom(flatManaged);

        return paddedFlat;
    }

    // Substituir/Adicionar overload que aceita int[] em vez de NativeArray<int>
    private void PlaceTreesInFlattened(int[] paddedFlat, int[,] heightMap, Vector2Int chunkCoord, int pw, int ph, int pd, int pad)
    {
        int lpw = pw, lph = ph, lpd = pd;
        int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

        int startWorldX = chunkCoord.x * chunkWidth - pad;
        int startWorldZ = chunkCoord.y * chunkDepth - pad;

        for (int lx = 0; lx < lpw; lx++)
        {
            for (int lz = 0; lz < lpd; lz++)
            {
                int worldX = startWorldX + lx;
                int worldZ = startWorldZ + lz;
                int groundLevel = heightMap[lx, lz];

                if (groundLevel <= seaLevel) continue;

                if (groundLevel < 0 || groundLevel >= lph) continue;
                int topIdx = FlattenIndex(lx, groundLevel, lz);
                int topInt = paddedFlat[topIdx];
                if (!(topInt == (int)BlockType.Grass || topInt == (int)BlockType.Dirt)) continue;

                int hseed = worldX * 73856093 ^ worldZ * 19349663 ^ (seed + treeSeed);
                var rng = new System.Random(hseed);
                if (rng.NextDouble() >= treeSpawnChance) continue;

                int leftH = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
                int rightH = (lx + 1 < lpw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
                int frontH = (lz + 1 < lpd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
                int backH = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
                if (Mathf.Abs(leftH - groundLevel) > 1 || Mathf.Abs(rightH - groundLevel) > 1 ||
                    Mathf.Abs(frontH - groundLevel) > 1 || Mathf.Abs(backH - groundLevel) > 1)
                    continue;

                int trunkH = useFixedTreeHeight ? fixedTreeHeight : rng.Next(treeMinHeight, treeMaxHeight + 1);
                int topY = groundLevel + trunkH;

                for (int y = groundLevel + 1; y <= topY; y++)
                {
                    if (y < 0 || y >= lph) continue;
                    int idx = FlattenIndex(lx, y, lz);
                    int cur = paddedFlat[idx];
                    if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
                        paddedFlat[idx] = (int)BlockTypeFrom(woodBlock);
                }

                int leafR = treeLeafRadius;
                for (int layer = 0; layer < 4; layer++)
                {
                    int by = topY + layer;
                    if (by < 0 || by >= lph) continue;
                    int radius = (layer == 1) ? leafR : Mathf.Max(1, leafR - 1);

                    for (int ox = -radius; ox <= radius; ox++)
                    {
                        for (int oz = -radius; oz <= radius; oz++)
                        {
                            int bx = lx + ox;
                            int bz = lz + oz;
                            if (bx < 0 || bx >= lpw || bz < 0 || bz >= lpd) continue;

                            if (Mathf.Abs(ox) == radius && Mathf.Abs(oz) == radius && layer != 0) continue;

                            int idx = FlattenIndex(bx, by, bz);
                            int cur = paddedFlat[idx];
                            if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
                                paddedFlat[idx] = (int)BlockTypeFrom(leavesBlock);
                        }
                    }
                }
            }
        }
    }



    private void PlaceTreesInFlattened(NativeArray<int> paddedFlat, int[,] heightMap, Vector2Int chunkCoord, int pw, int ph, int pd, int pad)
    {
        // cria cópias locais — seguro e evita problemas se a função for usada em outros contextos
        int lpw = pw, lph = ph, lpd = pd;
        int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

        int startWorldX = chunkCoord.x * chunkWidth - pad;
        int startWorldZ = chunkCoord.y * chunkDepth - pad;

        for (int lx = 0; lx < lpw; lx++)
        {
            for (int lz = 0; lz < lpd; lz++)
            {
                int worldX = startWorldX + lx;
                int worldZ = startWorldZ + lz;
                int groundLevel = heightMap[lx, lz];

                if (groundLevel <= seaLevel) continue;

                // top block
                if (groundLevel < 0 || groundLevel >= lph) continue;
                int topIdx = FlattenIndex(lx, groundLevel, lz);
                int topInt = paddedFlat[topIdx];
                if (!(topInt == (int)BlockType.Grass || topInt == (int)BlockType.Dirt)) continue;

                // RNG determinístico por coluna (mesma abordagem existente)
                int hseed = worldX * 73856093 ^ worldZ * 19349663 ^ (seed + treeSeed);
                var rng = new System.Random(hseed);
                if (rng.NextDouble() >= treeSpawnChance) continue;

                // evita encostas muito íngremes: verificar vizinhos via heightMap (ou GetHeightAt quando necessário)
                int leftH = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
                int rightH = (lx + 1 < lpw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
                int frontH = (lz + 1 < lpd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
                int backH = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
                if (Mathf.Abs(leftH - groundLevel) > 1 || Mathf.Abs(rightH - groundLevel) > 1 ||
                    Mathf.Abs(frontH - groundLevel) > 1 || Mathf.Abs(backH - groundLevel) > 1)
                    continue;

                int trunkH = useFixedTreeHeight ? fixedTreeHeight : rng.Next(treeMinHeight, treeMaxHeight + 1);
                int topY = groundLevel + trunkH;

                // tronco
                for (int y = groundLevel + 1; y <= topY; y++)
                {
                    if (y < 0 || y >= lph) continue;
                    int idx = FlattenIndex(lx, y, lz);
                    int cur = paddedFlat[idx];
                    if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
                        paddedFlat[idx] = (int)BlockTypeFrom(woodBlock);
                }


                int leafR = treeLeafRadius;

                int minLayers = 3;
                int maxLayers = 6;
                int layerCount = Mathf.Clamp(leafR + 2, minLayers, maxLayers); // quantas camadas de folhas
                int mid = layerCount / 2;

                // folhas (forma simples em camadas)


                for (int layer = 0; layer < 4; layer++)
                {
                    int by = topY + layer;

                    if (by < 0 || by >= lph) continue;
                    int radius = (layer == 1) ? leafR : Mathf.Max(1, leafR - 1);

                    for (int ox = -radius; ox <= radius; ox++)
                    {
                        for (int oz = -radius; oz <= radius; oz++)
                        {
                            int bx = lx + ox;
                            int bz = lz + oz;
                            if (bx < 0 || bx >= lpw || bz < 0 || bz >= lpd) continue;

                            // remover cantos (simula oak)
                            if (Mathf.Abs(ox) == radius && Mathf.Abs(oz) == radius && layer != 0) continue;

                            int idx = FlattenIndex(bx, by, bz);
                            int cur = paddedFlat[idx];
                            if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
                                paddedFlat[idx] = (int)BlockTypeFrom(leavesBlock);
                        }
                    }
                }
            }
        }
    }



    // // // Agora padded tem padding em ambos os lados igual a treePadding
    // public BlockType[,,] GenerateBlocksForChunkPadded(Vector2Int chunkCoord)
    // {
    //     int w = chunkWidth, h = chunkHeight, d = chunkDepth;
    //     int pad = treePadding;

    //     // padded dims: w + 2*pad, h, d + 2*pad
    //     var padded = new BlockType[w + 2 * pad, h, d + 2 * pad];

    //     int pw = w + 2 * pad, pd = d + 2 * pad;

    //     // precompute heightMap para padded área (size pw x pd)
    //     int[,] heightMap = ComputeHeightMap(chunkCoord, pw, pd, pad);

    //     FillPaddedBlocks(padded, heightMap, chunkCoord, pw, pd, pad);

    //     // Agora adicionar árvores determinísticas dentro da área padded (bases que caibam no padded)
    //     PlaceTreesInPadded(padded, heightMap, chunkCoord, pw, pd, pad);

    //     return padded;
    // }


    // private int[,] ComputeHeightMap(Vector2Int chunkCoord, int pw, int pd, int pad)
    // {
    //     int startWorldX = chunkCoord.x * chunkWidth - pad;
    //     int startWorldZ = chunkCoord.y * chunkDepth - pad;
    //     int[,] heightMap = new int[pw, pd];

    //     for (int lx = 0; lx < pw; lx++)
    //     {
    //         for (int lz = 0; lz < pd; lz++)
    //         {
    //             int worldX = startWorldX + lx;
    //             int worldZ = startWorldZ + lz;
    //             heightMap[lx, lz] = GetHeightAt(worldX, worldZ);
    //         }
    //     }

    //     return heightMap;
    // }

    // private void FillPaddedBlocks(BlockType[,,] padded, int[,] heightMap, Vector2Int chunkCoord, int pw, int pd, int pad)
    // {
    //     int h = chunkHeight;
    //     for (int lx = 0; lx < pw; lx++)
    //     {
    //         for (int lz = 0; lz < pd; lz++)
    //         {
    //             int worldX = chunkCoord.x * chunkWidth + lx - pad;
    //             int worldZ = chunkCoord.y * chunkDepth + lz - pad;
    //             int groundLevel = heightMap[lx, lz];

    //             for (int y = 0; y < h; y++)
    //             {
    //                 padded[lx, y, lz] = DetermineBlockType(y, groundLevel, worldX, worldZ, heightMap, lx, lz, pw, pd);
    //             }
    //         }
    //     }
    // }

    private BlockType DetermineBlockType(int y, int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
    {
        if (y < bedrockLayers)
        {
            return BlockType.Bedrock;
        }

        if (y > groundLevel)
        {
            if (y <= seaLevel) return BlockType.Water;
            else return BlockType.Air;
        }

        if (y == groundLevel)
        {
            return DetermineSurfaceBlock(groundLevel, worldX, worldZ, heightMap, lx, lz, pw, pd);
        }

        if (y >= (groundLevel - dirtLayers))
        {
            return DetermineSubSurfaceBlock(worldX, worldZ);
        }
        else if (y >= 1 && y <= 20)
        {
            return BlockType.Deepslate;
        }
        else
        {
            return BlockType.Stone;
        }
    }

    private BlockType DetermineSurfaceBlock(int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
    {
        if (groundLevel <= seaLevel + 1) return BlockType.Sand;

        GetBiomeBlendAt(worldX, worldZ, out int bi0, out int bi1, out float bt);

        Biome B0 = biomes != null && biomes.Length > 0 ? biomes[bi0] : null;
        Biome B1 = biomes != null && biomes.Length > 0 ? biomes[bi1] : null;
        Biome dominant = (bt < 0.5f) ? B0 : B1;
        BlockType top = dominant != null ? dominant.topBlock : BlockType.Grass;

        int hL = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
        int hR = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
        int hF = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
        int hB = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
        int maxNeighbor = Mathf.Max(hL, hR, hF, hB);
        bool isCliff = (groundLevel - maxNeighbor) <= -2 ||
                       (Mathf.Abs(hL - groundLevel) >= 3 || Mathf.Abs(hR - groundLevel) >= 3 ||
                        Mathf.Abs(hF - groundLevel) >= 3 || Mathf.Abs(hB - groundLevel) >= 3);

        if (isCliff)
        {
            return dominant != null ? dominant.fillerBlock : BlockType.Stone;
        }
        else
        {
            return top;
        }
    }

    private BlockType DetermineSubSurfaceBlock(int worldX, int worldZ)
    {
        GetBiomeBlendAt(worldX, worldZ, out int bi0, out int bi1, out float bt);
        Biome B0 = biomes != null && biomes.Length > 0 ? biomes[bi0] : null;
        Biome B1 = biomes != null && biomes.Length > 0 ? biomes[bi1] : null;
        Biome dominant = (bt < 0.5f) ? B0 : B1;
        return dominant != null ? dominant.subSurfaceBlock : BlockType.Dirt;
    }

    public int GetHeightAt(int worldX, int worldZ)
    {
        // 1) Continental shape (macro: continentes, oceanos)
        Vector2 warped = MyNoise.DomainWarp(worldX, worldZ, noiseSettings.domainWarpStrength, noiseSettings.warpNoiseScale, seed);
        float continental = MyNoise.FractalNoise2D(
            warped.x, warped.y,
            noiseSettings.continentalOctaves,
            noiseSettings.continentalPersistence,
            noiseSettings.continentalLacunarity,
            noiseSettings.continentalScale,
            seed
        );

        // 2) Definir altura base continental
        float h = noiseSettings.baseGroundLevel;
        h += (continental - 0.5f) * noiseSettings.continentalStrength;

        // 3) Biomas (igual estava antes)
        GetBiomeBlendAt(worldX, worldZ, out int bi0, out int bi1, out float bt);

        Biome B0 = biomes != null && biomes.Length > 0 ? biomes[bi0] : null;
        Biome B1 = biomes != null && biomes.Length > 0 ? biomes[bi1] : null;

        int octaves = Mathf.RoundToInt(Mathf.Lerp(B0?.octaves ?? 4, B1?.octaves ?? 4, bt));
        float persistence = Mathf.Lerp(B0?.persistence ?? 0.5f, B1?.persistence ?? 0.5f, bt);
        float lacunarity = Mathf.Lerp(B0?.lacunarity ?? 2f, B1?.lacunarity ?? 2f, bt);
        float bScale = Mathf.Lerp(B0?.scale ?? 0.02f, B1?.scale ?? 0.02f, bt);
        float heightOffset = Mathf.Lerp(B0?.heightOffset ?? 0f, B1?.heightOffset ?? 0f, bt);
        float heightMultiplier = Mathf.Lerp(B0?.heightMultiplier ?? 10f, B1?.heightMultiplier ?? 10f, bt);

        // 4) Ruído do bioma
        float biomeNoise = MyNoise.FractalNoise2D(worldX, worldZ, octaves, persistence, lacunarity, bScale, seed + biomeSeed);

        // 5) Somar ajuste do bioma
        h += (biomeNoise - 0.5f) * heightMultiplier + heightOffset;

        return Mathf.Clamp(Mathf.RoundToInt(h), 0, chunkHeight - 1);
    }

    private void GetBiomeBlendAt(int worldX, int worldZ, out int i0, out int i1, out float t)
    {
        if (biomes == null || biomes.Length == 0)
        {
            i0 = i1 = 0; t = 0f;
            return;
        }

        // mapa de biomas 0..1
        float b = MyNoise.FractalNoise2D(worldX, worldZ, 3, 0.5f, 2f, biomeNoiseScale, biomeSeed);

        // escala para número de biomas e separa dois vizinhos para blend
        float scaled = b * biomes.Length;
        i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, biomes.Length - 1);
        i1 = Mathf.Clamp(i0 + 1, 0, biomes.Length - 1);
        t = Mathf.Clamp01(scaled - i0);
    }

    // --- NOVO: funções para árvores ---

    private void PlaceTreesInPadded(BlockType[,,] padded, int[,] heightMap, Vector2Int chunkCoord, int pw, int pd, int pad)
    {
        // Percorre todas as posições do padded (cada coluna de bloco) e decide se nasce uma árvore no topo
        int startWorldX = chunkCoord.x * chunkWidth - pad;
        int startWorldZ = chunkCoord.y * chunkDepth - pad;

        for (int lx = 0; lx < pw; lx++)
        {
            for (int lz = 0; lz < pd; lz++)
            {
                int worldX = startWorldX + lx;
                int worldZ = startWorldZ + lz;
                int groundLevel = heightMap[lx, lz];

                // checa superfície válida (ex.: não na água, não em areia)
                if (groundLevel <= seaLevel) continue;

                // top block no padded:
                BlockType topBlock = padded[lx, groundLevel, lz];
                // somente gerar em top blocks típicos (usar Grass ou topo do bioma)
                if (!(topBlock == BlockType.Grass || topBlock == BlockType.Dirt)) continue;

                // Deterministic RNG por posição
                int hseed = worldX * 73856093 ^ worldZ * 19349663 ^ (seed + treeSeed);
                var rng = new System.Random(hseed);

                if (rng.NextDouble() >= treeSpawnChance) continue;

                // evita gerar em encostas muito íngremes: checa vizinhos imediatos
                int leftH = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
                int rightH = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
                int frontH = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
                int backH = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
                if (Mathf.Abs(leftH - groundLevel) > 1 || Mathf.Abs(rightH - groundLevel) > 1 ||
                    Mathf.Abs(frontH - groundLevel) > 1 || Mathf.Abs(backH - groundLevel) > 1)
                {
                    continue; // evita encostas
                }

                // altura do tronco aleatória
                int trunkH;
                if (useFixedTreeHeight)
                {
                    trunkH = fixedTreeHeight;
                }
                else
                {
                    trunkH = rng.Next(treeMinHeight, treeMaxHeight + 1);
                }


                // posição do topo
                int topY = groundLevel + trunkH;

                // Colocar tronco (não sobrescrever bedrock nem stone muito profundo)
                for (int y = groundLevel + 1; y <= topY; y++)
                {
                    SetPaddedBlockIfInRange(padded, lx, y, lz, BlockTypeFrom(woodBlock));
                }

                // Colocar folhas: esfera/cubo simples ao redor do topo
                // --- colocar folhas estilo "Minecraft-like" em camadas (substitui a versão esférica) ---
                int leafR = treeLeafRadius;

                // parâmetros de aparência (ajuste se quiser)
                int minLayers = 3;
                int maxLayers = 6;
                int layerCount = Mathf.Clamp(leafR + 2, minLayers, maxLayers); // quantas camadas de folhas
                int mid = layerCount / 2;


                // Colocar folhas estilo Minecraft Oak (3 camadas fixas acima do topo do tronco)
                for (int layer = 0; layer < 4; layer++)
                {
                    int by = topY + layer; // camada começa no topo do tronco

                    // raio lateral varia por camada (central maior, superiores menores)
                    int radius = (layer == 1) ? treeLeafRadius : treeLeafRadius - 1;
                    if (radius < 1) radius = 1;

                    for (int ox = -radius; ox <= radius; ox++)
                    {
                        for (int oz = -radius; oz <= radius; oz++)
                        {
                            int bx = lx + ox;
                            int bz = lz + oz;

                            // remover cantos nas camadas maiores para imitar o formato do oak
                            if (Mathf.Abs(ox) == radius && Mathf.Abs(oz) == radius)
                            {
                                if (layer != 0) // deixa base mais cheia, remove só nos níveis superiores
                                    continue;
                            }

                            SetPaddedBlockIfInRange(padded, bx, by, bz, BlockTypeFrom(leavesBlock));
                        }
                    }
                }

            }
        }
    }

    // checa limites do padded e escreve o bloco somente se a posição estiver dentro do array e se o bloco atual permitir (air ou water)
    private void SetPaddedBlockIfInRange(BlockType[,,] padded, int px, int py, int pz, BlockType bt)
    {
        if (px < 0 || px >= padded.GetLength(0) ||
            py < 0 || py >= padded.GetLength(1) ||
            pz < 0 || pz >= padded.GetLength(2)) return;

        var cur = padded[px, py, pz];
        // Só sobrescrever se for ar ou água (assim não remove terreno)
        if (cur == BlockType.Air)
        {
            padded[px, py, pz] = bt;
        }
    }

    // In case the wood/leaves were passed as placeholder or such, garantimos o tipo
    private BlockType BlockTypeFrom(BlockType configured)
    {
        return configured;
    }







}
