
// // ChunkGenerator.cs
// using System;
// using Unity.Collections;
// using UnityEngine;

// public class ChunkGenerator
// {
//     private Biome[] biomes;
//     private float biomeNoiseScale;
//     private int biomeSeed;
//     private int chunkWidth;
//     private int chunkHeight;
//     private int chunkDepth;
//     private int bedrockLayers;
//     private int dirtLayers;
//     private int seed;
//     private NoiseSettings noiseSettings;
//     private int seaLevel;

//     // Tree settings
//     private int treePadding;            // padding em blocos (quanto expandir o padded para conter árvores)
//     private int treeSeed;
//     private float treeSpawnChance;      // probabilidade por posição (0..1)
//     private int treeMinHeight;
//     private int treeMaxHeight;
//     private int treeLeafRadius;
//     private BlockType woodBlock;
//     private BlockType leavesBlock;
//     private bool useFixedTreeHeight;
//     private int fixedTreeHeight;
//     private NoisePreset[] noisePresets;
//     private float presetNoiseScale;
//     private int presetSeed;
//     private bool biomesOnlyForColor = true; // quando true, biomas só determinam cor
//     private BlockType defaultTopBlock = BlockType.Grass;
//     private BlockType defaultSubSurfaceBlock = BlockType.Dirt;
//     private BlockType defaultFillerBlock = BlockType.Stone;

//     public ChunkGenerator(
//      Biome[] biomes, float biomeNoiseScale, int biomeSeed, int chunkWidth, int chunkHeight, int chunkDepth,
//      int bedrockLayers, int dirtLayers, int seed, NoiseSettings noiseSettings, int seaLevel,
//      int treePadding = 3, int treeSeed = 54321, float treeSpawnChance = 0.02f,
//      int treeMinHeight = 4, int treeMaxHeight = 6, int treeLeafRadius = 2,
//      BlockType woodBlock = BlockType.Placeholder, BlockType leavesBlock = BlockType.Placeholder,
//      bool useFixedTreeHeight = false, int fixedTreeHeight = 5)
//     {
//         this.biomes = biomes;
//         this.biomeNoiseScale = biomeNoiseScale;
//         this.biomeSeed = biomeSeed;
//         this.chunkWidth = chunkWidth;
//         this.chunkHeight = chunkHeight;
//         this.chunkDepth = chunkDepth;
//         this.bedrockLayers = bedrockLayers;
//         this.dirtLayers = dirtLayers;
//         this.seed = seed;
//         this.noiseSettings = noiseSettings;
//         this.seaLevel = seaLevel;

//         // tree
//         this.treePadding = Mathf.Max(1, treePadding);
//         this.treeSeed = treeSeed;
//         this.treeSpawnChance = Mathf.Clamp01(treeSpawnChance);
//         this.treeMinHeight = Mathf.Max(1, treeMinHeight);
//         this.treeMaxHeight = Mathf.Max(treeMinHeight, treeMaxHeight);
//         this.treeLeafRadius = Mathf.Max(1, treeLeafRadius);
//         this.woodBlock = woodBlock;
//         this.leavesBlock = leavesBlock;

//         this.useFixedTreeHeight = useFixedTreeHeight;
//         this.fixedTreeHeight = Mathf.Max(1, fixedTreeHeight);
//     }



//     // Substituir o método existente por este
//     public NativeArray<int> GenerateFlattenedPadded_Native(Vector2Int chunkCoord, out int pw, out int ph, out int pd, Allocator allocator = Allocator.TempJob, bool SpawnTrees = true)
//     {
//         int w = chunkWidth, h = chunkHeight, d = chunkDepth;
//         int pad = treePadding;

//         pw = w + 2 * pad;
//         ph = h;
//         pd = d + 2 * pad;

//         int lpw = pw, lph = ph, lpd = pd;

//         int total = lpw * lph * lpd;

//         // NativeArray retornado (alocado com o allocator solicitado)
//         var paddedFlat = new NativeArray<int>(total, allocator);

//         // **BUILD IN A MANAGED ARRAY FIRST** (muito mais rápido para escrever elemento-a-elemento)
//         var flatManaged = new int[total];

//         // helper local (usa as cópias)
//         int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

//         // 1) Precompute heightMap (lpw x lpd)
//         int startWorldX = chunkCoord.x * chunkWidth - pad;
//         int startWorldZ = chunkCoord.y * chunkDepth - pad;
//         var heightMap = new int[lpw, lpd];
//         for (int lx = 0; lx < lpw; lx++)
//         {
//             for (int lz = 0; lz < lpd; lz++)
//             {
//                 int worldX = startWorldX + lx;
//                 int worldZ = startWorldZ + lz;
//                 heightMap[lx, lz] = GetHeightAt(worldX, worldZ);
//             }
//         }

//         // 2) Preencher flatManaged diretamente (sem criar BlockType[,,] e sem NativeArray.Set_Item por elemento)
//         for (int lx = 0; lx < lpw; lx++)
//         {
//             for (int lz = 0; lz < lpd; lz++)
//             {
//                 int worldX = chunkCoord.x * chunkWidth + lx - pad;
//                 int worldZ = chunkCoord.y * chunkDepth + lz - pad;
//                 int groundLevel = heightMap[lx, lz];

//                 for (int y = 0; y < lph; y++)
//                 {
//                     var bt = DetermineBlockType(y, groundLevel, worldX, worldZ, heightMap, lx, lz, lpw, lpd);
//                     int idx = FlattenIndex(lx, y, lz);
//                     flatManaged[idx] = (int)bt;
//                 }
//             }
//         }


//         if (SpawnTrees)
//         {
//             // 3) Colocar árvores diretamente no flatManaged (versão que opera em int[])
//             PlaceTreesInFlattened(flatManaged, heightMap, chunkCoord, lpw, lph, lpd, pad);
//         }


//         // Finalmente copia o bloco inteiro para o NativeArray (memcpy interno, muito rápido)
//         paddedFlat.CopyFrom(flatManaged);

//         return paddedFlat;
//     }

//     // Substituir/Adicionar overload que aceita int[] em vez de NativeArray<int>
//     private void PlaceTreesInFlattened(int[] paddedFlat, int[,] heightMap, Vector2Int chunkCoord, int pw, int ph, int pd, int pad)
//     {
//         int lpw = pw, lph = ph, lpd = pd;
//         int FlattenIndex(int x, int y, int z) => (x * lph + y) * lpd + z;

//         int startWorldX = chunkCoord.x * chunkWidth - pad;
//         int startWorldZ = chunkCoord.y * chunkDepth - pad;

//         for (int lx = 0; lx < lpw; lx++)
//         {
//             for (int lz = 0; lz < lpd; lz++)
//             {
//                 int worldX = startWorldX + lx;
//                 int worldZ = startWorldZ + lz;
//                 int groundLevel = heightMap[lx, lz];

//                 if (groundLevel <= seaLevel) continue;

//                 if (groundLevel < 0 || groundLevel >= lph) continue;
//                 int topIdx = FlattenIndex(lx, groundLevel, lz);
//                 int topInt = paddedFlat[topIdx];
//                 if (!(topInt == (int)BlockType.Grass || topInt == (int)BlockType.Dirt)) continue;

//                 int hseed = worldX * 73856093 ^ worldZ * 19349663 ^ (seed + treeSeed);
//                 var rng = new System.Random(hseed);
//                 if (rng.NextDouble() >= treeSpawnChance) continue;

//                 int leftH = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
//                 int rightH = (lx + 1 < lpw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
//                 int frontH = (lz + 1 < lpd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
//                 int backH = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
//                 if (Mathf.Abs(leftH - groundLevel) > 1 || Mathf.Abs(rightH - groundLevel) > 1 ||
//                     Mathf.Abs(frontH - groundLevel) > 1 || Mathf.Abs(backH - groundLevel) > 1)
//                     continue;

//                 int trunkH = useFixedTreeHeight ? fixedTreeHeight : rng.Next(treeMinHeight, treeMaxHeight + 1);
//                 int topY = groundLevel + trunkH;

//                 for (int y = groundLevel + 1; y <= topY; y++)
//                 {
//                     if (y < 0 || y >= lph) continue;
//                     int idx = FlattenIndex(lx, y, lz);
//                     int cur = paddedFlat[idx];
//                     if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
//                         paddedFlat[idx] = (int)BlockTypeFrom(woodBlock);
//                 }

//                 int leafR = treeLeafRadius;
//                 for (int layer = 0; layer < 4; layer++)
//                 {
//                     int by = topY + layer;
//                     if (by < 0 || by >= lph) continue;
//                     int radius = (layer == 1) ? leafR : Mathf.Max(1, leafR - 1);

//                     for (int ox = -radius; ox <= radius; ox++)
//                     {
//                         for (int oz = -radius; oz <= radius; oz++)
//                         {
//                             int bx = lx + ox;
//                             int bz = lz + oz;
//                             if (bx < 0 || bx >= lpw || bz < 0 || bz >= lpd) continue;

//                             if (Mathf.Abs(ox) == radius && Mathf.Abs(oz) == radius && layer != 0) continue;

//                             int idx = FlattenIndex(bx, by, bz);
//                             int cur = paddedFlat[idx];
//                             if (cur == (int)BlockType.Air || cur == (int)BlockType.Water)
//                                 paddedFlat[idx] = (int)BlockTypeFrom(leavesBlock);
//                         }
//                     }
//                 }
//             }
//         }
//     }


//     private BlockType DetermineBlockType(int y, int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
//     {
//         if (y < bedrockLayers)
//         {
//             return BlockType.Bedrock;
//         }

//         if (y > groundLevel)
//         {
//             if (y <= seaLevel) return BlockType.Water;
//             else return BlockType.Air;
//         }

//         if (y == groundLevel)
//         {
//             return DetermineSurfaceBlock(groundLevel, worldX, worldZ, heightMap, lx, lz, pw, pd);
//         }

//         if (y >= (groundLevel - dirtLayers))
//         {
//             return DetermineSubSurfaceBlock(worldX, worldZ);
//         }
//         else if (y >= 1 && y <= 20)
//         {
//             return BlockType.Deepslate;
//         }
//         else
//         {
//             return BlockType.Stone;
//         }
//     }
//     private BlockType DetermineSurfaceBlock(int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
//     {
//         if (groundLevel <= seaLevel + 1) return BlockType.Sand;

//         // cliff detection (mantém igual)
//         int hL = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
//         int hR = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
//         int hF = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
//         int hB = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
//         int maxNeighbor = Mathf.Max(hL, hR, hF, hB);
//         bool isCliff = (groundLevel - maxNeighbor) <= -2 ||
//                        (Mathf.Abs(hL - groundLevel) >= 3 || Mathf.Abs(hR - groundLevel) >= 3 ||
//                         Mathf.Abs(hF - groundLevel) >= 3 || Mathf.Abs(hB - groundLevel) >= 3);

//         if (isCliff)
//         {
//             return defaultFillerBlock; // ex: Stone
//         }
//         else
//         {
//             return defaultTopBlock; // ex: Grass
//         }
//     }

//     private BlockType DetermineSubSurfaceBlock(int worldX, int worldZ)
//     {
//         return defaultSubSurfaceBlock; // ex: Dirt
//     }


//     public int GetHeightAt(int worldX, int worldZ)
//     {
//         // 1) Continental shape (macro: continentes, oceanos)
//         Vector2 warped = MyNoise.DomainWarp(worldX, worldZ, noiseSettings.domainWarpStrength, noiseSettings.warpNoiseScale, seed);
//         float continental = MyNoise.FractalNoise2D(
//             warped.x, warped.y,
//             noiseSettings.continentalOctaves,
//             noiseSettings.continentalPersistence,
//             noiseSettings.continentalLacunarity,
//             noiseSettings.continentalScale,
//             seed
//         );

//         // 2) Definir altura base continental
//         float h = noiseSettings.baseGroundLevel;
//         h += (continental - 0.5f) * noiseSettings.continentalStrength;

//         // 3) Biomas (igual estava antes)
//         GetBiomeBlendAt(worldX, worldZ, out int bi0, out int bi1, out float bt);

//         Biome B0 = biomes != null && biomes.Length > 0 ? biomes[bi0] : null;
//         Biome B1 = biomes != null && biomes.Length > 0 ? biomes[bi1] : null;
//         // escolha do preset (por um noise separado, ou por alguma regra)
//         // aqui uso um noise "preset selector" (pode ser aleatório por seed/config)
//         int presetIndex = 0;
//         if (noisePresets != null && noisePresets.Length > 0)
//         {
//             float p = MyNoise.FractalNoise2D(worldX, worldZ, 3, 0.5f, 2f, presetNoiseScale, presetSeed);
//             presetIndex = Mathf.Clamp(Mathf.FloorToInt(p * noisePresets.Length), 0, noisePresets.Length - 1);
//         }
//         var preset = (noisePresets != null && noisePresets.Length > 0) ? noisePresets[presetIndex] : null;

//         // pegar parâmetros do preset (fallbacks caso null)
//         int octaves = preset != null ? preset.octaves : 4;
//         float persistence = preset != null ? preset.persistence : 0.5f;
//         float lacunarity = preset != null ? preset.lacunarity : 2f;
//         float bScale = preset != null ? preset.scale : 0.02f;
//         float heightOffset = preset != null ? preset.heightOffset : 0f;
//         float heightMultiplier = preset != null ? preset.heightMultiplier : 10f;

//         // 4) Ruído local (agora usando preset)
//         float biomeNoise = MyNoise.FractalNoise2D(worldX, worldZ, octaves, persistence, lacunarity, bScale, seed + biomeSeed);

//         // 5) Somar ajuste do preset
//         h += (biomeNoise - 0.5f) * heightMultiplier + heightOffset;



//         return Mathf.Clamp(Mathf.RoundToInt(h), 0, chunkHeight - 1);
//     }

//     private void GetBiomeBlendAt(int worldX, int worldZ, out int i0, out int i1, out float t)
//     {
//         if (biomes == null || biomes.Length == 0)
//         {
//             i0 = i1 = 0; t = 0f;
//             return;
//         }

//         // mapa de biomas 0..1
//         float b = MyNoise.FractalNoise2D(worldX, worldZ, 3, 0.5f, 2f, biomeNoiseScale, biomeSeed);

//         // escala para número de biomas e separa dois vizinhos para blend
//         float scaled = b * biomes.Length;
//         i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, biomes.Length - 1);
//         i1 = Mathf.Clamp(i0 + 1, 0, biomes.Length - 1);
//         t = Mathf.Clamp01(scaled - i0);
//     }


//     // In case the wood/leaves were passed as placeholder or such, garantimos o tipo
//     private BlockType BlockTypeFrom(BlockType configured)
//     {
//         return configured;
//     }







// }

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
    private float treeSpawnChance;      // probabilidade por posição (0.1)
    private int treeMinHeight;
    private int treeMaxHeight;
    private int treeLeafRadius;
    private BlockType woodBlock;
    private BlockType leavesBlock;
    private bool useFixedTreeHeight;
    private int fixedTreeHeight;

    // Novos: presets de noise (controlam forma do terreno, separados dos biomas)
    private NoisePreset[] noisePresets;
    private float presetNoiseScale;
    private int presetSeed;
    private BlockType defaultTopBlock;
    private BlockType defaultSubSurfaceBlock;
    private BlockType defaultFillerBlock;

    public ChunkGenerator(
    int chunkWidth, int chunkHeight, int chunkDepth,
     int bedrockLayers, int dirtLayers, int seed, NoiseSettings noiseSettings, int seaLevel,
     int treePadding = 3, float treeSpawnChance = 0.02f,
     int treeMinHeight = 4, int treeMaxHeight = 6, int treeLeafRadius = 2,
     BlockType woodBlock = BlockType.Placeholder, BlockType leavesBlock = BlockType.Placeholder,


     // Novos parâmetros (posicionados com defaults para compatibilidade)
     BlockType defaultTopBlock = BlockType.Grass,
     BlockType defaultSubSurfaceBlock = BlockType.Dirt,
     BlockType defaultFillerBlock = BlockType.Stone
    )
    {



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

        this.treeSpawnChance = Mathf.Clamp01(treeSpawnChance);
        this.treeMinHeight = Mathf.Max(1, treeMinHeight);
        this.treeMaxHeight = Mathf.Max(treeMinHeight, treeMaxHeight);
        this.treeLeafRadius = Mathf.Max(1, treeLeafRadius);
        this.woodBlock = woodBlock;
        this.leavesBlock = leavesBlock;





        this.defaultTopBlock = defaultTopBlock;
        this.defaultSubSurfaceBlock = defaultSubSurfaceBlock;
        this.defaultFillerBlock = defaultFillerBlock;
    }

    // Substituir o método existente por este
    public NativeArray<int> GenerateFlattenedPadded_Native(Vector2Int chunkCoord, out int pw, out int ph, out int pd, Allocator allocator = Allocator.TempJob, bool SpawnTrees = true)
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

        // 2) Preencher flatManaged diretamente (sem criar BlockType[,] e sem NativeArray.Set_Item por elemento)
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

        if (SpawnTrees)
        {
            // 3) Colocar árvores diretamente no flatManaged (versão que opera em int[])
            PlaceTreesInFlattened(flatManaged, heightMap, chunkCoord, lpw, lph, lpd, pad);
            
        }

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

    // private BlockType DetermineBlockType(int y, int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
    // {
    //     if (y < bedrockLayers)
    //     {
    //         return BlockType.Bedrock;
    //     }

    //     if (y > groundLevel)
    //     {
    //         if (y <= seaLevel) return BlockType.Water;
    //         else return BlockType.Air;
    //     }

    //     if (y == groundLevel)
    //     {
    //         return DetermineSurfaceBlock(groundLevel, worldX, worldZ, heightMap, lx, lz, pw, pd);
    //     }

    //     if (y >= (groundLevel - dirtLayers))
    //     {
    //         return DetermineSubSurfaceBlock(worldX, worldZ);
    //     }
    //     else if (y >= 1 && y <= 20)
    //     {
    //         return BlockType.Deepslate;
    //     }
    //     else
    //     {
    //         return BlockType.Stone;
    //     }
    // }

    // private BlockType DetermineSurfaceBlock(int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
    // {
    //     if (groundLevel <= seaLevel + 1) return BlockType.Sand;

    //     // cliff detection (mantém igual)
    //     int hL = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
    //     int hR = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
    //     int hF = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
    //     int hB = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
    //     int maxNeighbor = Mathf.Max(hL, hR, hF, hB);
    //     bool isCliff = (groundLevel - maxNeighbor) <= -2 ||
    //                    (Mathf.Abs(hL - groundLevel) >= 3 || Mathf.Abs(hR - groundLevel) >= 3 ||
    //                     Mathf.Abs(hF - groundLevel) >= 3 || Mathf.Abs(hB - groundLevel) >= 3);

    //     if (isCliff)
    //     {
    //         return defaultFillerBlock; // ex: Stone
    //     }
    //     else
    //     {
    //         return defaultTopBlock; // ex: Grass
    //     }
    // }

    // private BlockType DetermineSubSurfaceBlock(int worldX, int worldZ)
    // {
    //     return defaultSubSurfaceBlock; // ex: Dirt
    // }

    // substitua a função GetHeightAt existente (em ChunkGenerator.cs) por esta versão:

    // Adicione (se quiser expor a profundidade como parâmetro) um campo no topo da classe:
    private int cliffStoneDepth = 6; // quantos blocos abaixo do topo devem ser 'stone' nas falésias

    // Substitua a função DetermineBlockType por esta versão:
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
            // superfície: mantemos topo como topBlock (grama etc). A lógica de "cliff -> pedra"
            // passa a ser aplicada nas camadas abaixo (sub-surface).
            return DetermineSurfaceBlock(groundLevel, worldX, worldZ, heightMap, lx, lz, pw, pd);
        }

        if (y >= (groundLevel - dirtLayers))
        {
            // subsuperfície — agora com informação de y/groundLevel para aplicar pedra nas faces de cliff
            return DetermineSubSurfaceBlock(worldX, worldZ, groundLevel, y, heightMap, lx, lz, pw, pd);
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

    // Substitua DetermineSurfaceBlock por esta (top mantém grama; praias continuam areia)
    private BlockType DetermineSurfaceBlock(int groundLevel, int worldX, int worldZ, int[,] heightMap, int lx, int lz, int pw, int pd)
    {
        if (groundLevel <= seaLevel + 1) return BlockType.Sand;

        // Detectar cliff (mesma detecção que você já usa)
        int hL = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
        int hR = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
        int hF = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
        int hB = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
        int maxNeighbor = Mathf.Max(hL, hR, hF, hB);
        bool isCliff = (groundLevel - maxNeighbor) <= -2 ||
                       (Mathf.Abs(hL - groundLevel) >= 3 || Mathf.Abs(hR - groundLevel) >= 3 ||
                        Mathf.Abs(hF - groundLevel) >= 3 || Mathf.Abs(hB - groundLevel) >= 3);

        // Em vez de devolver pedra no topo, mantemos o top block (grama).
        // Isso faz com que o topo continue gramado e as faces verticais sejam tratadas
        // pela subsuperfície que agora pode devolver stone quando apropriado.
        return defaultTopBlock;
    }

    // Substitua DetermineSubSurfaceBlock por esta versão (leva em conta cliff e y)
    private BlockType DetermineSubSurfaceBlock(int worldX, int worldZ, int groundLevel, int y, int[,] heightMap, int lx, int lz, int pw, int pd)
    {
        // depthFromTop: quantos blocos abaixo do topo estamos
        int depthFromTop = groundLevel - y;

        // recalcular se é cliff (mesma lógica pra consistência)
        int hL = (lx - 1 >= 0) ? heightMap[lx - 1, lz] : GetHeightAt(worldX - 1, worldZ);
        int hR = (lx + 1 < pw) ? heightMap[lx + 1, lz] : GetHeightAt(worldX + 1, worldZ);
        int hF = (lz + 1 < pd) ? heightMap[lx, lz + 1] : GetHeightAt(worldX, worldZ + 1);
        int hB = (lz - 1 >= 0) ? heightMap[lx, lz - 1] : GetHeightAt(worldX, worldZ - 1);
        int maxNeighbor = Mathf.Max(hL, hR, hF, hB);
        bool isCliff = (groundLevel - maxNeighbor) <= -2 ||
                       (Mathf.Abs(hL - groundLevel) >= 3 || Mathf.Abs(hR - groundLevel) >= 3 ||
                        Mathf.Abs(hF - groundLevel) >= 3 || Mathf.Abs(hB - groundLevel) >= 3);

        // Se for cliff e estamos dentro de uma profundidade curta abaixo do topo,
        // devolvemos pedra (expondo faces verticais como em Minecraft).
        int useDepth = Mathf.Max(dirtLayers, cliffStoneDepth); // pode ajustar
        if (isCliff && depthFromTop < useDepth)
        {
            return defaultFillerBlock; // Stone (exposed face)
        }

        // Caso normal: se estiver dentro das camadas de dirt, retorna subsurface (dirt)
        if (depthFromTop < dirtLayers)
            return defaultSubSurfaceBlock;

        // fallback (manter compatibilidade com deepslate/stone)
        if (y >= 1 && y <= 20) return BlockType.Deepslate;
        return BlockType.Stone;
    }


    public int GetHeightAt(int worldX, int worldZ)
    {
        // --- 1) Continental (macro) com domain-warping ---
        Vector2 warped = MyNoise.DomainWarp(worldX, worldZ, noiseSettings.domainWarpStrength, noiseSettings.warpNoiseScale, seed);
        float continental = MyNoise.FractalNoise2D(
            warped.x, warped.y,
            noiseSettings.continentalOctaves,
            noiseSettings.continentalPersistence,
            noiseSettings.continentalLacunarity,
            noiseSettings.continentalScale,
            seed
        ); // [0,1]

        // base height a partir da continentalidade
        float h = noiseSettings.baseGroundLevel + (continental - 0.5f) * noiseSettings.continentalStrength;

        // --- 2) máscara de montanha (controla onde as montanhas aparecem) ---
        // usamos continental como mapa base de onde podem existir montanhas (alto continental -> montanha)
        float mountainMaskRaw = Mathf.Clamp01((continental - noiseSettings.mountainMaskBias) / (1f - noiseSettings.mountainMaskBias));
        float mountainMask = Mathf.Pow(mountainMaskRaw, noiseSettings.mountainMaskExponent);

        // --- 3) Ruído de montanha (ridged) para picos/cordilheiras ---
        float mountain = MyNoise.RidgedFractalNoise2D(
            worldX, worldZ,
            noiseSettings.mountainOctaves,
            noiseSettings.mountainPersistence,
            noiseSettings.mountainLacunarity,
            noiseSettings.mountainScale,
            seed + 98765
        ); // normalmente [0,1], com picos
        h += (mountain - 0.5f) * noiseSettings.mountainStrength * mountainMask;

        // --- 4) Erosão / valley carving (pequena redução/variação para criar vales) ---
        float erosion = MyNoise.FractalNoise2D(
            worldX + 1000, worldZ + 1000,
            noiseSettings.erosionOctaves,
            0.5f,
            2f,
            noiseSettings.erosionScale,
            seed + 4242
        );
        // interpretamos erosion como mapa que "subtrai" um pouco a altura para criar vales
        h -= (erosion - 0.5f) * noiseSettings.erosionStrength * (1f - mountainMask); // efeitos maiores fora de montanhas

        // --- 5) detalhe fino ---
        float detail = MyNoise.FractalNoise2D(
            worldX + 5000, worldZ + 5000,
            noiseSettings.detailOctaves,
            0.5f,
            2f,
            noiseSettings.detailScale,
            seed + 2468
        );
        h += (detail - 0.5f) * noiseSettings.detailStrength;

        // --- 6) Biome / preset local (se você deseja que biomas forcadamente modifiquem a altura) ---
        // Se noisePresets estiver preenchido, escolhe um preset por um selector noise (ou você pode mapear por bioma)
        if (noisePresets != null && noisePresets.Length > 0)
        {
            float sel = MyNoise.FractalNoise2D(worldX + 20000, worldZ + 20000, 3, 0.5f, 2f, presetNoiseScale, presetSeed);
            int pIdx = Mathf.Clamp(Mathf.FloorToInt(sel * noisePresets.Length), 0, noisePresets.Length - 1);
            var preset = noisePresets[pIdx];
            if (preset != null)
            {
                float presetNoise = MyNoise.FractalNoise2D(worldX - 30000, worldZ - 30000, preset.octaves, preset.persistence, preset.lacunarity, Mathf.Max(1e-6f, preset.scale), seed + biomeSeed);
                h += (presetNoise - 0.5f) * preset.heightMultiplier + preset.heightOffset;
            }
        }
        else
        {
            // fallback compatível com sua versão anterior: ainda usa os parametros de bioma
            GetBiomeBlendAt(worldX, worldZ, out int bi0, out int bi1, out float bt);
            Biome B0 = biomes != null && biomes.Length > 0 ? biomes[bi0] : null;
            Biome B1 = biomes != null && biomes.Length > 0 ? biomes[bi1] : null;


        }

        // --- 7) aplicar clamp e transformar para int (altura em blocos) ---
        int finalH = Mathf.RoundToInt(Mathf.Clamp(h, 0f, chunkHeight - 1f));

        return finalH;
    }

    private void GetBiomeBlendAt(int worldX, int worldZ, out int i0, out int i1, out float t)
    {
        if (biomes == null || biomes.Length == 0)
        {
            i0 = i1 = 0; t = 0f;
            return;
        }

        // mapa de biomas (humidity-like). Continua a usar o mesmo noise scale/seed que você já configurou.
        float b = MyNoise.FractalNoise2D(worldX, worldZ, 3, 0.5f, 2f, biomeNoiseScale, biomeSeed);

        float scaled = b * biomes.Length;
        i0 = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, biomes.Length - 1);
        i1 = Mathf.Clamp(i0 + 1, 0, biomes.Length - 1);
        t = Mathf.Clamp01(scaled - i0);
    }

    // In case the wood/leaves were passed as placeholder or such, garantimos o tipo
    private BlockType BlockTypeFrom(BlockType configured)
    {
        return configured;
    }
}
