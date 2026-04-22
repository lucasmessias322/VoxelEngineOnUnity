using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;


[Serializable]
public struct TreeSettings
{
    public int minHeight;
    public int maxHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int trunkClearance;
    public int minSpacing;
    public float density;
    public float noiseScale;
    public int seed;
}

[Serializable]
public struct OreSpawnSettings
{
    public bool enabled;
    public BlockType blockType;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int veinsPerChunk;
    [Min(1)] public int minVeinSize;
    [Min(1)] public int maxVeinSize;
    [Min(0)] public int minSurfaceDepth;
    public bool replaceStone;
    public bool replaceDeepslate;
}

[Serializable]
public struct SpaghettiCaveSettings
{
    public bool enabled;
    [Min(0)] public int minY;
    [Min(0)] public int maxY;
    [Min(0)] public int minSurfaceDepth;
    [Min(0)] public int entranceSurfaceDepth;
    [Range(-0.35f, 0.35f)] public float densityBias;
    public int seedOffset;

    public static SpaghettiCaveSettings Default => new SpaghettiCaveSettings
    {
        enabled = true,
        minY = 4,
        maxY = 320,
        minSurfaceDepth = 6,
        entranceSurfaceDepth = 0,
        densityBias = 0f,
        seedOffset = 48271
    };

    public bool LooksUninitialized =>
        !enabled &&
        minY == 0 &&
        maxY == 0 &&
        minSurfaceDepth == 0 &&
        entranceSurfaceDepth == 0 &&
        densityBias == 0f &&
        seedOffset == 0;

    public bool LooksLikeInitialSurfaceClosedDefault =>
        enabled &&
        minY == 4 &&
        maxY == 320 &&
        minSurfaceDepth == 6 &&
        entranceSurfaceDepth == 1 &&
        densityBias == 0f &&
        seedOffset == 48271;
}

public enum TreeLeafQualityMode : byte
{
    Medium = 0,
    High = 1,
    Ultra = 2
}

public enum WorldMaterialProfile : byte
{
    PcLit = 0,
    MobileUnlit = 1
}



#region Utilities

public static class LightUtils
{
    // Junta as duas luzes (0-15) em um Ãºnico byte
    public static ushort PackLight(byte skyLight, byte blockLight)
    {
        return PackLightRgb(skyLight, blockLight, blockLight, blockLight);
    }

    // Extrai apenas a luz do cÃ©u (bits 4 a 7)
    public static ushort PackLightRgb(byte skyLight, byte blockRed, byte blockGreen, byte blockBlue)
    {
        return (ushort)(
            ((skyLight & 0x0F) << 12) |
            ((blockRed & 0x0F) << 8) |
            ((blockGreen & 0x0F) << 4) |
            (blockBlue & 0x0F));
    }

    public static ushort PackBlockLight(byte blockLight)
    {
        return PackLight(0, blockLight);
    }

    public static ushort PackBlockLightRgb(byte blockRed, byte blockGreen, byte blockBlue)
    {
        return PackLightRgb(0, blockRed, blockGreen, blockBlue);
    }

    public static ushort PackEmission(byte emission, Color color)
    {
        return PackEmission(emission, color.r, color.g, color.b);
    }

    public static ushort PackEmission(byte emission, float r, float g, float b)
    {
        emission = ClampNibble(emission);
        if (emission == 0)
            return 0;

        float maxComponent = math.max(r, math.max(g, b));
        if (maxComponent <= 0.001f)
        {
            r = 1f;
            g = 1f;
            b = 1f;
            maxComponent = 1f;
        }

        r = math.saturate(r / maxComponent);
        g = math.saturate(g / maxComponent);
        b = math.saturate(b / maxComponent);

        return PackBlockLightRgb(
            ScaleEmissionChannel(emission, r),
            ScaleEmissionChannel(emission, g),
            ScaleEmissionChannel(emission, b));
    }

    public static byte GetSkyLight(ushort packedLight)
    {
        return (byte)((packedLight >> 12) & 0x0F);
    }

    // Extrai apenas a luz dos blocos (bits 0 a 3)
    public static byte GetBlockLightR(ushort packedLight)
    {
        return (byte)((packedLight >> 8) & 0x0F);
    }

    public static byte GetBlockLightG(ushort packedLight)
    {
        return (byte)((packedLight >> 4) & 0x0F);
    }

    public static byte GetBlockLightB(ushort packedLight)
    {
        return (byte)(packedLight & 0x0F);
    }

    private static byte MaxLightComponent(byte a, byte b)
    {
        return a >= b ? a : b;
    }

    private static byte MinLightComponent(byte a, byte b)
    {
        return a <= b ? a : b;
    }

    public static byte GetBlockLight(ushort packedLight)
    {
        return MaxLightComponent(
            GetBlockLightR(packedLight),
            MaxLightComponent(GetBlockLightG(packedLight), GetBlockLightB(packedLight)));
    }

    public static ushort GetBlockLightPacked(ushort packedLight)
    {
        return PackBlockLightRgb(GetBlockLightR(packedLight), GetBlockLightG(packedLight), GetBlockLightB(packedLight));
    }

    public static bool HasBlockLight(ushort packedLight)
    {
        return GetBlockLight(packedLight) > 0;
    }

    public static ushort MaxBlockLight(ushort a, ushort b)
    {
        return PackBlockLightRgb(
            MaxLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MaxLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MaxLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static ushort MinBlockLight(ushort a, ushort b)
    {
        return PackBlockLightRgb(
            MinLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MinLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MinLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static ushort MaxPackedLight(ushort a, ushort b)
    {
        return PackLightRgb(
            MaxLightComponent(GetSkyLight(a), GetSkyLight(b)),
            MaxLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MaxLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MaxLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static bool IsBlockLightGreater(ushort candidate, ushort current)
    {
        return GetBlockLightR(candidate) > GetBlockLightR(current) ||
               GetBlockLightG(candidate) > GetBlockLightG(current) ||
               GetBlockLightB(candidate) > GetBlockLightB(current);
    }

    public static ushort AttenuateBlockLight(ushort packedBlockLight, int loss)
    {
        loss = math.max(0, loss);
        return PackBlockLightRgb(
            (byte)math.max(0, GetBlockLightR(packedBlockLight) - loss),
            (byte)math.max(0, GetBlockLightG(packedBlockLight) - loss),
            (byte)math.max(0, GetBlockLightB(packedBlockLight) - loss));
    }

    public static uint EncodeBlockLightColor32(ushort packedLight)
    {
        uint r = (uint)(GetBlockLightR(packedLight) * 17);
        uint g = (uint)(GetBlockLightG(packedLight) * 17);
        uint b = (uint)(GetBlockLightB(packedLight) * 17);
        return r | (g << 8) | (b << 16) | (255u << 24);
    }

    public static uint EncodeWhiteBlockLightColor32(float blockLight01)
    {
        uint value = (uint)math.clamp((int)math.round(math.saturate(blockLight01) * 255f), 0, 255);
        return value | (value << 8) | (value << 16) | (255u << 24);
    }

    private static byte ClampNibble(byte value)
    {
        return (byte)math.min(value, 15);
    }

    private static byte ScaleEmissionChannel(byte emission, float channel)
    {
        return (byte)math.clamp((int)math.round(emission * math.saturate(channel)), 0, 15);
    }
}

#endregion

public partial class World : MonoBehaviour
{
    #region Singleton

    public static World Instance { get; private set; }
    public const int MinRenderDistance = 2;
    public const int MaxRenderDistance = 32;
    internal bool IsShuttingDown => isShuttingDown;

    private bool isShuttingDown;
    private TorchFireParticleController torchFireParticleController;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        pendingChunkDistanceComparison = ComparePendingChunkByDistance;
        pendingDataDistanceComparison = ComparePendingDataByDistance;
        pendingMeshDistanceComparison = ComparePendingMeshByDistance;

        if (caveSpaghettiSettings.LooksUninitialized || caveSpaghettiSettings.LooksLikeInitialSurfaceClosedDefault)
            caveSpaghettiSettings = SpaghettiCaveSettings.Default;

        VoxelShaderFallbackBuffers.EnsureBound();
        EnsureLoadingBootstrapExists();
        EnsureTorchFireParticleControllerExists();
    }

    #endregion

    #region Inspector Fields - World Setup

    [Header("General")]
    public Transform player;
    public GameObject chunkPrefab;
    public int renderDistance = 4;
    [Tooltip("Raio em chunks usado pelos sistemas de simulacao. Limitado automaticamente pela renderDistance.")]
    [Min(0)]
    public int simulationDistance = 4;
    public int poolSize = 200;
    [Tooltip("Agrupa varias secoes 16x16x16 em um unico MeshRenderer. Valores maiores reduzem batches, mas deixam o culling vertical menos granular.")]
    [Min(1)]
    public int visualSubchunksPerRenderer = 4;
    [Tooltip("Cria subchunks logicos, render slices e meshes do pool no Start para evitar picos quando novas areas entram em cena.")]
    public bool prewarmPooledChunkVisuals = true;

    [Header("Atlas / Materials")]
    [Tooltip("Escolhe qual lista de materiais o World usa para chunks, high-build e renderers do terreno.")]
    [SerializeField] private WorldMaterialProfile materialProfile = WorldMaterialProfile.PcLit;
    [Tooltip("Materiais atuais/pesados para PC: blocos lit, folhas lit e agua lit, nessa ordem.")]
    [FormerlySerializedAs("Material")]
    [SerializeField] private Material[] pcMaterials = Array.Empty<Material>();
    [Tooltip("Materiais leves para mobile: Blocks Mobile, Folhas Mobile e Water Mobile, nessa ordem.")]
    [SerializeField] private Material[] mobileMaterials = Array.Empty<Material>();
    public int atlasTilesX = 4;
    public int atlasTilesY = 4;
    [Tooltip("Gerador do atlas de blocos usado para converter o mapeamento legacy por tiles para UV rects runtime.")]
    public TextureAtlasGenerator blockAtlasGenerator;

    [Header("Inventory Block Icons")]
    [Tooltip("Atlas opcional usado apenas para gerar os icones isometricos dos blocos no inventario. Se vazio, usa o atlas encontrado nos materiais do mundo.")]
    public Texture blockItemIconAtlasTexture;

    [Header("Terrain Layer Profile")]
    [Tooltip("Perfil com as camadas de terreno. Quando atribuido, o World usa as layers desse asset.")]
    public TerrainLayerProfileSO terrainLayerProfile;

    [Header("Noise Settings (Runtime)")]
    [Tooltip("Preenchido a partir do Terrain Layer Profile durante validacao/execucao.")]
    [SerializeField, HideInInspector] public NoiseLayer[] noiseLayers = Array.Empty<NoiseLayer>();
    [Tooltip("Shaper de terreno por splines inspirado no offset/factor/jaggedness do Minecraft moderno.")]
    public TerrainSplineShaperSettings terrainSplineShaper = TerrainSplineShaperSettings.MinecraftModernDefault;
    public int baseHeight = 64;
    public int heightVariation = 32;
    public int seed = 1337;

    [Header("Density Terrain")]
    [Tooltip("Configuracao de densidade base que ajusta o limiar de solido da superficie do terrain.")]
    public TerrainDensitySettings terrainDensity = TerrainDensitySettings.MinecraftLikeDefault;

    [Header("Block Data")]
    public BlockDataSO blockData;

    [Header("Torch Effects")]
    [Tooltip("Prefab opcional com um ou mais ParticleSystems para o fogo das tochas. Se vazio, o efeito e gerado por codigo.")]
    public GameObject torchFireEffectPrefab;

    [Header("Sea Settings")]
    public int seaLevel = 62;
    public BlockType waterBlock = BlockType.Water;
    [Tooltip("Debug: desativa a geracao e simulacao de agua no mundo.")]
    public bool enableWater = true;

    public int CliffTreshold = 2;

    [Header("Ore Settings")]
    public OreSpawnSettings[] oreSettings = new OreSpawnSettings[]
    {
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.CoalOre,
            minY = 8,
            maxY = 160,
            veinsPerChunk = 18,
            minVeinSize = 6,
            maxVeinSize = 18,
            minSurfaceDepth = 4,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.IronOre,
            minY = 8,
            maxY = 96,
            veinsPerChunk = 12,
            minVeinSize = 4,
            maxVeinSize = 11,
            minSurfaceDepth = 5,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.GoldOre,
            minY = 6,
            maxY = 48,
            veinsPerChunk = 7,
            minVeinSize = 3,
            maxVeinSize = 9,
            minSurfaceDepth = 6,
            replaceStone = true,
            replaceDeepslate = true
        },
       
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.Copper_ore,
            minY = 8,
            maxY = 64,
            veinsPerChunk = 12,
            minVeinSize = 4,
            maxVeinSize = 11,
            minSurfaceDepth = 5,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.DiamondOre,
            minY = 4,
            maxY = 20,
            veinsPerChunk = 4,
            minVeinSize = 3,
            maxVeinSize = 7,
            minSurfaceDepth = 8,
            replaceStone = true,
            replaceDeepslate = true
        },
        new OreSpawnSettings
        {
            enabled = true,
            blockType = BlockType.EmeraldOre,
            minY = 16,
            maxY = 80,
            veinsPerChunk = 2,
            minVeinSize = 2,
            maxVeinSize = 5,
            minSurfaceDepth = 6,
            replaceStone = true,
            replaceDeepslate = false
        }
    };

    [Header("Spaghetti Cave Settings")]
    public SpaghettiCaveSettings caveSpaghettiSettings = SpaghettiCaveSettings.Default;

    [Header("Performance Settings")]
    public int maxChunksPerFrame = 4;
    public int maxMeshAppliesPerFrame = 2;
    [Tooltip("Quando ativo, meshes prontas sao aplicadas por prioridade visual: slices visiveis, perto da camera/player e dentro do frustum entram primeiro.")]
    public bool enableSmartMeshApplyPrioritization = true;
    [Tooltip("Limite de meshes prontas avaliadas por apply. 0 avalia todas. Use para limitar custo quando houver backlog extremo.")]
    [Min(0)]
    public int meshApplyPriorityScanLimit = 64;
    [Tooltip("Quantidade maxima de chunks com dados/luz prontos que podem agendar jobs de mesh por frame.")]
    [Min(1)]
    public int maxMeshSchedulesPerFrame = 1;
    [Tooltip("Quantidade maxima de subchunks que podem reconstruir collider por frame.")]
    [Min(1)]
    public int maxColliderBuildsPerFrame = 1;
    [Tooltip("Orcamento de tempo (ms) para reconstruir colliders por frame. Use 0 para sem limite.")]
    [Min(0f)]
    public float colliderBuildTimeBudgetMS = 0.75f;
    [Tooltip("Quantidade maxima de jobs de dados concluidos e processados por frame.")]
    [Min(1)]
    public int maxDataCompletionsPerFrame = 2;
    [Tooltip("Quantidade maxima de jobs de iluminacao concluidos e processados por frame.")]
    [Min(1)]
    public int maxLightingCompletionsPerFrame = 2;
    public float frameTimeBudgetMS = 4f;
    [Tooltip("Orcamento total (ms) para os processos do Update. Use 0 para desativar o limite.")]
    [Min(0f)]
    public float updateWorkBudgetMS = 6f;
    [Tooltip("Orcamento (ms) para agendar novos jobs de dados de chunk. Use 0 para sem limite.")]
    [Min(0f)]
    public float chunkDataScheduleBudgetMS = 0.75f;
    [Tooltip("Orcamento (ms) para concluir a etapa de dados/terreno sem bloquear o frame. Use 0 para sem limite.")]
    [Min(0f)]
    public float chunkDataCompletionBudgetMS = 1f;
    [Tooltip("Orcamento (ms) para concluir a etapa de iluminacao e preparar o chunk para mesh. Use 0 para sem limite.")]
    [Min(0f)]
    public float chunkLightingCompletionBudgetMS = 1f;
    [Tooltip("Orcamento (ms) para agendar jobs de mesh a partir de chunks prontos. Use 0 para sem limite.")]
    [Min(0f)]
    public float chunkMeshScheduleBudgetMS = 1f;
    [Tooltip("Orcamento (ms) para aplicar meshes concluidas na main thread/GPU. Use 0 para sem limite.")]
    [Min(0f)]
    public float chunkMeshApplyBudgetMS = 1.5f;
    [Tooltip("Limite de jobs de geraÃ§Ã£o de dados (inclui iluminaÃ§Ã£o) simultÃ¢neos para evitar queda brusca de FPS.")]
    [Min(1)]
    public int maxPendingDataJobs = 2;
    [Tooltip("Quando ativo, novos chunks nao iniciam geracao enquanto houver mesh pronta esperando ApplyMeshData.")]
    public bool pauseChunkSchedulingWhenMeshesReady = true;
    [Tooltip("Quantidade de meshes prontas que podem ficar esperando ApplyMeshData antes de pausar novas geracoes. Use 0 para pausar com qualquer mesh pronta.")]
    [Min(0)]
    public int maxReadyMeshApplyBacklog = 0;
    [Tooltip("Quantidade maxima de chunks com dados/luz prontos aguardando agendamento de mesh antes de pausar novas geracoes.")]
    [Min(1)]
    public int maxMeshBuildRequestBacklog = 2;
    [Tooltip("Quantidade maxima de jobs de mesh em voo/aguardando apply antes de pausar novas geracoes.")]
    [Min(1)]
    public int maxPendingMeshJobBacklog = 8;
    [Tooltip("Quantidade maxima de pedidos de rebuild de chunk processados por frame.")]
    [Min(1)]
    public int maxChunkRebuildsPerFrame = 1;
    [Tooltip("Agrupa refreshes de mesh/luz apos edicoes de bloco para reduzir spikes quando o player constroi rapido.")]
    public bool smoothInteractiveBlockEdits = true;
    [Tooltip("Pequeno atraso maximo antes de reconstruir chunks editados. Mantem a resposta visual curta e permite coalescer varias edicoes.")]
    [Min(0f)]
    public float interactiveBlockEditRefreshDelaySeconds = 0.035f;
    [Tooltip("Quantidade maxima de atualizacoes de luz de bloco processadas por frame apos edicoes interativas.")]
    [Min(1)]
    public int maxInteractiveBlockLightRefreshesPerFrame = 1;
    [Tooltip("Orcamento de tempo (ms) para iniciar refreshes de luz de bloco apos edicoes interativas. Uma atualizacao ja iniciada sempre termina.")]
    [Min(0f)]
    public float interactiveBlockLightRefreshBudgetMS = 0.75f;

    [Header("Features Toggle")]
    [Tooltip("Liga/desliga o caminho de iluminacao realista dos shaders voxel. Quando desligado, usa uma iluminacao voxel simples.")]
    public bool enableRealisticShader = true;
    public bool enableTrees = true;
    [Tooltip("Quando ativo, quebrar um tronco quebra a arvore inteira de forma gradual (tipo treecapitator).")]
    public bool enableTreeCapitator = true;
    [Tooltip("Numero maximo de troncos quebrados por uma unica arvore para evitar cascatas gigantes.")]
    [Min(8)]
    public int treeCapitatorMaxLogsPerTree = 128;
    [Tooltip("Limite de troncos processados por frame para evitar lag spike.")]
    [Min(1)]
    public int treeCapitatorBreaksPerFrame = 10;
    [Tooltip("Orcamento de tempo (ms) para quebrar troncos do treecapitator por frame. Use 0 para sem limite.")]
    [Min(0f)]
    public float treeCapitatorTimeBudgetMS = 0.75f;

    [Header("Tree Leaves")]
    [Tooltip("Medium = folhas voxel padrao. High = camada detalhada com sobreposicao em todas as folhas (estilo Vintage Story). Ultra = folhas 100% billboard de 4 faces (estilo Hytale).")]
    public TreeLeafQualityMode treeLeafQuality = TreeLeafQualityMode.Medium;
    [Tooltip("Densidade dos billboards extras de folha no modo High. Para visual estilo Vintage Story use 1.0.")]
    [Range(0f, 1f)]
    public float treeLeafFoliageSpawnChance = 1f;
    [Tooltip("Altura minima do billboard extra de folha no modo High. Para visual estilo Vintage Story use valor > 1.")]
    [Range(0.2f, 2.0f)]
    public float treeLeafFoliageHeightMin = 1.08f;
    [Tooltip("Altura maxima do billboard extra de folha no modo High.")]
    [Range(0.2f, 2.0f)]
    public float treeLeafFoliageHeightMax = 1.08f;
    [Tooltip("Meia largura minima do billboard extra de folha. >0.5 cria sobreposicao alem do bloco (estilo Vintage Story).")]
    [Range(0.5f, 1.0f)]
    public float treeLeafFoliageHalfWidthMin = 0.72f;
    [Tooltip("Meia largura maxima do billboard extra de folha. Valores maiores deixam o volume mais encorpado.")]
    [Range(0.5f, 1.0f)]
    public float treeLeafFoliageHalfWidthMax = 0.72f;
    [Tooltip("Offset Y minimo da base do billboard extra de folha. Negativo estende para fora do bloco (estilo Vintage Story).")]
    [Range(-0.2f, 0.4f)]
    public float treeLeafFoliageBaseYOffsetMin = -0.04f;
    [Tooltip("Offset Y maximo da base do billboard extra de folha dentro do bloco.")]
    [Range(-0.2f, 0.4f)]
    public float treeLeafFoliageBaseYOffsetMax = -0.04f;
    [Tooltip("Jitter lateral do centro do billboard extra de folha. Para visual estilo Vintage Story use 0.")]
    [Range(0f, 0.2f)]
    public float treeLeafFoliageCenterJitter = 0f;

    [Header("Tree Leaves Ultra (4 Faces)")]
    [Tooltip("Altura do billboard de 4 faces usado no modo Ultra.")]
    [Range(0.4f, 2.5f)]
    public float treeLeafUltraBillboardHeight = 1.12f;
    [Tooltip("Meia largura do billboard de 4 faces no modo Ultra. >0.5 cria sobreposicao alem do voxel.")]
    [Range(0.5f, 1.6f)]
    public float treeLeafUltraBillboardHalfWidth = 0.78f;
    [Tooltip("Offset Y do centro do billboard de 4 faces no modo Ultra. 0 deixa pivot exatamente no centro do bloco.")]
    [Range(-0.4f, 0.4f)]
    public float treeLeafUltraBaseYOffset = 0f;
    [Tooltip("Jitter lateral do centro do billboard de 4 faces no modo Ultra.")]
    [Range(0f, 0.2f)]
    public float treeLeafUltraCenterJitter = 0f;
    [Tooltip("Rotacao base do conjunto de 4 faces no modo Ultra. 22.5 deixa mais proximo do visual Hytale.")]
    [Range(0f, 45f)]
    public float treeLeafUltraRotationOffsetDegrees = 22.5f;
    [Tooltip("Variacao aleatoria de rotacao por bloco no modo Ultra.")]
    [Range(0f, 30f)]
    public float treeLeafUltraRotationRandomDegrees = 12f;
    [Tooltip("Inclinacao base das faces do Ultra para manter volume quando visto de cima/baixo.")]
    [Range(0f, 60f)]
    public float treeLeafUltraFaceTiltDegrees = 34f;
    [Tooltip("Variacao aleatoria da inclinacao das faces do Ultra.")]
    [Range(0f, 30f)]
    public float treeLeafUltraFaceTiltRandomDegrees = 14f;

    [Header("Billboard Grass")]
    public bool enableGrassBillboards = true;
    [Range(0f, 1f)]
    public float grassBillboardChance = 0.22f;
    public BlockType grassBillboardBlockType = BlockType.short_grass4;
    [Range(0.2f, 2f)]
    public float grassBillboardHeight = 0.9f;
    [Range(0.01f, 1f)]
    public float grassBillboardNoiseScale = 0.12f;
    [Range(0f, 0.35f)]
    public float grassBillboardJitter = 0.16f;

    [Header("Ambient Occlusion")]
    [Tooltip("Liga/desliga o Ambient Occlusion da malha dos voxels para testes de performance.")]
    public bool enableAmbientOcclusion = true;
    [Tooltip("Forca do AO. 1 = padrao, >1 escurece mais os cantos, 0 desativa o AO.")]
    [Range(0f, 2.5f)]
    public float aoStrength = 1.35f;
    [Tooltip("Curva do AO. Valores maiores aumentam o contraste do escurecimento.")]
    [Range(0.5f, 3f)]
    public float aoCurveExponent = 1.25f;
    [Tooltip("Luz minima aplicada apos AO. Menor valor permite cantos mais escuros.")]
    [Range(0f, 1f)]
    public float aoMinLight = 0.08f;
    [Tooltip("Quando ativado, usa um greedy meshing rapido com validacao barata de AO/luz. Mantem o AO correto na maior parte dos casos sem o custo alto da busca exaustiva.")]
    public bool useFastBedrockStyleMeshing = true;

    [Header("Debug / Physics")]
    [Tooltip("Ativa ou desativa o sistema de colliders dos blocos. Quando desligado, novos chunks nao geram collider.")]
    public bool enableBlockColliders = true;

    [Header("Lighting")]
    [Tooltip("Liga/desliga o calculo de iluminacao voxel/skylight para testes de performance. Quando desligado, os chunks usam brilho uniforme.")]
    public bool enableVoxelLighting = true;
    [Tooltip("Liga/desliga a propagacao horizontal do skylight apos os raios verticais. Desative para manter apenas a luz direta por coluna e reduzir custo.")]
    public bool enableHorizontalSkylight = true;
    [Tooltip("Custo base de cada passo horizontal do skylight. 1 = comportamento atual; valores maiores encurtam o alcance lateral da luz.")]
    [Range(1, 15)]
    public int horizontalSkylightStepLoss = 1;
    [Tooltip("Padding horizontal em voxels usado pela propagacao lateral do skylight entre chunks. Valores altos melhoram costuras visuais, mas aumentam o custo do volume de luz. Ignorado quando a luz horizontal esta desligada.")]
    [Min(1)]
    public int sunlightSmoothingPadding = 16;
    [Tooltip("Multiplicador visual aplicado no shader apenas ao skylight. Nao reconstrói chunks.")]
    [Range(0f, 1f)]
    public float voxelSkyLightMultiplier = 1f;
    [Tooltip("Padding horizontal usado pelas etapas caras de geracao detalhada (arvores, cavernas e minerios). Mantido separado do padding de luz para reduzir custo.")]
    [Min(1)]
    public int detailedGenerationPadding = 1;

    [Header("Chunk Detail LOD")]
    [Tooltip("Quando ativo, chunks alem deste raio sao gerados em modo simplificado: sem cavernas spaghetti e sem billboards de grama. Ao se aproximar, chunks simplificados sao reconstruidos com detalhes completos.")]
    public bool enableChunkDetailLod = true;
    [Tooltip("Raio, em chunks, onde a geracao usa todos os detalhes. Fora dele, chunks novos usam geracao simplificada.")]
    [Min(0)]
    public int chunkDetailLodDistance = 10;
    [Tooltip("Raio mais proximo que sempre tenta nascer detalhado, mesmo durante movimento e backlog de geracao.")]
    [Min(0)]
    public int chunkImmediateDetailDistance = 4;
    [Tooltip("Tempo minimo sem trocar de chunk antes de promover chunks simplificados para detalhados.")]
    [Min(0f)]
    public float chunkDetailPromotionDelaySeconds = 0.25f;
    [Tooltip("Quantidade maxima de chunks simplificados promovidos para detalhados por frame.")]
    [Min(1)]
    public int maxChunkDetailPromotionsPerFrame = 1;

    #endregion

    #region Private State

    private const int InitialActiveChunkCollectionCapacity = 512;
    private const int InitialChunkPoolCapacity = 256;
    private const int InitialChunkWorkCollectionCapacity = 256;
    private const int InitialQueuedChunkWorkCapacity = 512;
    private const int InitialBlockEditCapacity = 8192;
    private const int InitialBlockEditChunkIndexCapacity = 512;
    private const int InitialPerChunkBlockEditCapacity = 32;
    private const int InitialInteractiveBlockLightRefreshCapacity = 1024;
    private const int InitialLightColumnCapacity = 4096;
    private const int InitialLightWorkCollectionCapacity = 1024;

    // Active chunks & pool
    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>(InitialActiveChunkCollectionCapacity);
    private Queue<Chunk> chunkPool = new Queue<Chunk>(InitialChunkPoolCapacity);

    // Pending work
    private List<(Vector2Int coord, float distSq)> pendingChunks = new List<(Vector2Int, float)>(InitialQueuedChunkWorkCapacity);
    private List<PendingMesh> pendingMeshes = new List<PendingMesh>(InitialQueuedChunkWorkCapacity);
    private List<PendingData> pendingDataJobs = new List<PendingData>(InitialChunkWorkCollectionCapacity);
    private List<PendingData> pendingMeshBuildRequests = new List<PendingData>(InitialChunkWorkCollectionCapacity);
    private readonly List<PendingChunkDataBufferReturn> pendingChunkDataBufferReturns = new List<PendingChunkDataBufferReturn>(64);
    private readonly List<Chunk> retiredChunksAwaitingRecycle = new List<Chunk>(64);
    private readonly Queue<Vector2Int> queuedChunkRebuilds = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkRebuildsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, int> queuedChunkRebuildMasks = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, bool> queuedChunkRebuildRequiresCollider = new Dictionary<Vector2Int, bool>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, float> queuedChunkRebuildEarliestProcessTime = new Dictionary<Vector2Int, float>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedLightingOnlyChunkRebuilds = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedLightingOnlyChunkRebuildsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector2Int, int> queuedLightingOnlyChunkRebuildMasks = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedChunkDetailPromotions = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkDetailPromotionsSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector2Int> queuedChunkJobTrackingRefreshes = new Queue<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> queuedChunkJobTrackingRefreshSet = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> queuedColliderBuilds = new Queue<Vector3Int>(InitialQueuedChunkWorkCapacity);
    private readonly Dictionary<Vector3Int, PendingColliderBuild> queuedColliderBuildsByKey = new Dictionary<Vector3Int, PendingColliderBuild>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> queuedInteractiveBlockLightRefreshes = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, PendingInteractiveBlockLightRefresh> queuedInteractiveBlockLightRefreshesByPosition = new Dictionary<Vector3Int, PendingInteractiveBlockLightRefresh>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> terrainOverridePositionsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>(InitialBlockEditChunkIndexCapacity);
    private readonly List<Vector3Int> relevantTerrainOverridePositions = new List<Vector3Int>(InitialQueuedChunkWorkCapacity);
    private bool terrainOverrideIndexInitialized = false;

    // Overrides and light
    private Dictionary<Vector3Int, BlockType> blockOverrides = new Dictionary<Vector3Int, BlockType>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector3Int, BlockPlacementAxis> blockPlacementAxes = new Dictionary<Vector3Int, BlockPlacementAxis>(InitialBlockEditCapacity);
    private HashSet<Vector3Int> suppressedGrassBillboards = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> permanentGrassBillboardSuppressions = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Dictionary<Vector2Int, HashSet<Vector3Int>> suppressedGrassBillboardsByChunk = new Dictionary<Vector2Int, HashSet<Vector3Int>>(InitialBlockEditChunkIndexCapacity);
    // private Dictionary<Vector3Int, byte> globalLightMap = new Dictionary<Vector3Int, byte>();
    private Dictionary<Vector2Int, ushort[]> globalLightColumns = new Dictionary<Vector2Int, ushort[]>(InitialLightColumnCapacity);
    // Misc
    private float offsetX, offsetZ;
    private int nextChunkGeneration = 0;
    private int meshesAppliedThisFrame = 0;
    private float frameTimeAccumulator = 0f;
    private bool lastEnableBlockColliders = true;
    private bool lastEnableRealisticShader = true;
    private bool lastEnableVoxelLighting = true;
    private bool lastEnableHorizontalSkylight = true;
    private bool lastEnableAmbientOcclusion = true;
    private bool lastEnableWater = true;
    private bool lastEnableChunkDetailLod = true;
    private int lastWorldMaterialProfileHash = int.MinValue;
    private int lastChunkDetailLodDistance = 10;
    private TreeLeafQualityMode lastTreeLeafQuality = TreeLeafQualityMode.Medium;
    private int lastTreeLeafFoliageSettingsHash = int.MinValue;
    private int lastHorizontalSkylightStepLoss = 1;
    private int lastSunlightSmoothingPadding = 16;
    private TreeSpawnRuleData[] cachedTreeSpawnRules = Array.Empty<TreeSpawnRuleData>();
    private VegetationBillboardRuleData[] cachedVegetationBillboardRules = Array.Empty<VegetationBillboardRuleData>();
    private bool treeSpawnRulesDirty = true;
    private bool vegetationBillboardRulesDirty = true;
    private NativeArray<NoiseLayer> cachedNativeNoiseLayers;
    private NativeArray<BlockTextureMapping> cachedNativeBlockMappings;
    private NativeArray<BlockModelCuboid> cachedNativeBlockModelCuboids;
    private NativeArray<byte> cachedNativeEffectiveLightOpacityByBlock;
    private NativeArray<ushort> cachedNativeLightEmissionByBlock;
    private NativeArray<OreSpawnSettings> cachedNativeOreSettings;
    private NativeArray<TreeSpawnRuleData> cachedNativeTreeSpawnRules;
    private NativeArray<VegetationBillboardRuleData> cachedNativeVegetationBillboardRules;
    private bool nativeGenerationConfigDirty = true;
    private int lastResolvedVisualSubchunksPerRenderer = int.MinValue;
    private float lastPlayerChunkCoordChangeTime = float.NegativeInfinity;

    // Vulkan requires every declared StructuredBuffer to be bound, even when that code path is disabled at runtime.
    private static readonly int PulledOpaqueFacesBufferPropertyId = Shader.PropertyToID("_PulledOpaqueFaces");
    private static readonly int CompactOpaqueFacesBufferPropertyId = Shader.PropertyToID("_CompactOpaqueFaces");
    private static readonly int OpaqueGpuSectionsBufferPropertyId = Shader.PropertyToID("_OpaqueGpuSections");
    private static readonly int OpaqueBlockMappingsBufferPropertyId = Shader.PropertyToID("_OpaqueBlockMappings");
    private static readonly int UnityIndirectDrawArgsBufferPropertyId = Shader.PropertyToID("unity_IndirectDrawArgs");
    private static readonly int EnableRealisticShaderPropertyId = Shader.PropertyToID("_EnableRealisticShader");
    private const int PulledOpaqueFaceStrideBytes = 112;   // 7 * float4
    private const int CompactOpaqueFaceStrideBytes = 16;   // 4 * uint
    private const int OpaqueGpuSectionStrideBytes = 32;    // 2 * float4
    private const int OpaqueBlockMappingStrideBytes = 32;  // 2 * float4
    private const int UnityIndirectDrawArgsWordCount = 4;  // IndirectDrawArgs = 4 uint (16 bytes)
    private ComputeBuffer fallbackPulledOpaqueFacesBuffer;
    private ComputeBuffer fallbackCompactOpaqueFacesBuffer;
    private ComputeBuffer fallbackOpaqueGpuSectionsBuffer;
    private ComputeBuffer fallbackOpaqueBlockMappingsBuffer;
    private ComputeBuffer fallbackUnityIndirectDrawArgsBuffer;


    // Optimization temporaries
    private Vector2Int _lastChunkCoord = new Vector2Int(-99999, -99999);
    private Vector2Int _lastSimulationCenter = new Vector2Int(int.MinValue, int.MinValue);
    private int _lastSimulationDistance = -1;
    private Vector2Int _lastPendingJobPriorityCenter = new Vector2Int(int.MinValue, int.MinValue);
    private bool pendingJobPrioritiesDirty = true;
    private Camera cachedMeshApplyPriorityCamera;
    private readonly Plane[] meshApplyPriorityFrustumPlanes = new Plane[6];
    private readonly HashSet<Vector2Int> _tempNeededCoords = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly List<Vector2Int> _tempToRemove = new List<Vector2Int>();
    private Comparison<(Vector2Int coord, float distSq)> pendingChunkDistanceComparison;
    private Comparison<PendingData> pendingDataDistanceComparison;
    private Comparison<PendingMesh> pendingMeshDistanceComparison;
    private readonly List<Vector2Int> loadedChunkCoordsBuffer = new List<Vector2Int>(256);
    private readonly List<int3> suppressedGrassBillboardInt3Buffer = new List<int3>(128);
    private readonly List<BlockEdit> requestChunkEditsBuffer = new List<BlockEdit>(128);
    private readonly List<BlockEdit> rebuildChunkEditsBuffer = new List<BlockEdit>(128);
    private readonly List<BlockEdit> fastRebuildOverrideEditsBuffer = new List<BlockEdit>(64);
    private readonly List<TreeSpawnRuleData> treeSpawnRuleBuildBuffer = new List<TreeSpawnRuleData>(12);
    private readonly List<VegetationBillboardRuleData> vegetationBillboardRuleBuildBuffer = new List<VegetationBillboardRuleData>(16);
    private readonly Queue<Vector3Int> propagateLightQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector2Int, int> propagateDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<(Vector3Int pos, ushort lightLevel)> removeLightDarkQueueBuffer = new Queue<(Vector3Int, ushort)>(InitialLightWorkCollectionCapacity);
    private readonly Queue<Vector3Int> removeLightRefillQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector3Int, ushort> removeLightAffectedContributionsBuffer = new Dictionary<Vector3Int, ushort>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector2Int, int> removeLightDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly Queue<Vector3Int> refillLightQueueBuffer = new Queue<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly HashSet<Vector3Int> refillLightEnqueuedBuffer = new HashSet<Vector3Int>(InitialLightWorkCollectionCapacity);
    private readonly Dictionary<Vector2Int, int> refillLightDirtyChunksBuffer = new Dictionary<Vector2Int, int>(InitialQueuedChunkWorkCapacity);
    private readonly HashSet<Vector2Int> cleanupLightColumnKeysBuffer = new HashSet<Vector2Int>(InitialQueuedChunkWorkCapacity);
    private readonly List<Vector2Int> cleanupLightColumnsRemoveBuffer = new List<Vector2Int>(128);

    private TerrainDensitySettings GetTerrainDensitySettings()
    {
        return terrainDensity.Sanitized();
    }

    public Material[] Material
    {
        get => ActiveWorldMaterials;
        set => pcMaterials = value;
    }

    private bool IsMobileMaterialProfileSelected => materialProfile == WorldMaterialProfile.MobileUnlit;

    private Material[] ActiveWorldMaterials
    {
        get
        {
            if (IsMobileMaterialProfileSelected && HasAnyMaterial(mobileMaterials))
                return mobileMaterials;

            return pcMaterials ?? Array.Empty<Material>();
        }
    }

    private static bool HasAnyMaterial(Material[] materials)
    {
        if (materials == null)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null)
                return true;
        }

        return false;
    }

    private static bool ContainsMaterial(Material[] materials, Material material)
    {
        if (materials == null || material == null)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            if (ReferenceEquals(materials[i], material))
                return true;
        }

        return false;
    }

    private bool IsWorldMaterial(Material material)
    {
        return ContainsMaterial(pcMaterials, material) || ContainsMaterial(mobileMaterials, material);
    }

    private int ComputeWorldMaterialProfileHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (int)materialProfile;

            Material[] activeMaterials = ActiveWorldMaterials;
            if (activeMaterials == null)
                return hash;

            hash = hash * 31 + activeMaterials.Length;
            for (int i = 0; i < activeMaterials.Length; i++)
            {
                Material material = activeMaterials[i];
                hash = hash * 31 + (material != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material) : 0);
            }

            return hash;
        }
    }

    private void RefreshWorldMaterialProfileOnRenderers()
    {
        Material[] activeMaterials = ActiveWorldMaterials;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.visualSlices == null)
                continue;

            for (int i = 0; i < chunk.visualSlices.Length; i++)
            {
                ChunkRenderSlice visualSlice = chunk.visualSlices[i];
                if (visualSlice != null)
                    visualSlice.UpdateSourceMaterials(activeMaterials);
            }

            ApplyChunkBiomeTint(chunk, kv.Key);
        }

        foreach (var kv in highBuildMeshes)
        {
            HighBuildMeshData data = kv.Value;
            RefreshHighBuildSourceMaterials(data, activeMaterials);
            if (data?.meshRenderer == null)
                continue;

            ApplyBiomeTintToRenderer(data.meshRenderer, new Vector2Int(kv.Key.x, kv.Key.z));
            ApplyRealisticShaderRendererSettings(data.meshRenderer);
        }
    }

    private void EnsureShaderFallbackBuffersBound()
    {
        if (fallbackPulledOpaqueFacesBuffer == null)
        {
            fallbackPulledOpaqueFacesBuffer = new ComputeBuffer(1, PulledOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackPulledOpaqueFacesBuffer.SetData(new float[28]);
        }

        if (fallbackCompactOpaqueFacesBuffer == null)
        {
            fallbackCompactOpaqueFacesBuffer = new ComputeBuffer(1, CompactOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackCompactOpaqueFacesBuffer.SetData(new uint[4]);
        }

        if (fallbackOpaqueGpuSectionsBuffer == null)
        {
            fallbackOpaqueGpuSectionsBuffer = new ComputeBuffer(1, OpaqueGpuSectionStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueGpuSectionsBuffer.SetData(new float[8]);
        }

        if (fallbackOpaqueBlockMappingsBuffer == null)
        {
            fallbackOpaqueBlockMappingsBuffer = new ComputeBuffer(1, OpaqueBlockMappingStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueBlockMappingsBuffer.SetData(new float[8]);
        }

        if (fallbackUnityIndirectDrawArgsBuffer == null)
        {
            fallbackUnityIndirectDrawArgsBuffer = new ComputeBuffer(UnityIndirectDrawArgsWordCount, sizeof(uint), ComputeBufferType.Raw);
            fallbackUnityIndirectDrawArgsBuffer.SetData(new uint[UnityIndirectDrawArgsWordCount]);
        }

        Shader.SetGlobalBuffer(PulledOpaqueFacesBufferPropertyId, fallbackPulledOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(CompactOpaqueFacesBufferPropertyId, fallbackCompactOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(OpaqueGpuSectionsBufferPropertyId, fallbackOpaqueGpuSectionsBuffer);
        Shader.SetGlobalBuffer(OpaqueBlockMappingsBufferPropertyId, fallbackOpaqueBlockMappingsBuffer);
        Shader.SetGlobalBuffer(UnityIndirectDrawArgsBufferPropertyId, fallbackUnityIndirectDrawArgsBuffer);
    }

    private void ReleaseShaderFallbackBuffers()
    {
        ReleaseComputeBuffer(ref fallbackPulledOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackCompactOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueGpuSectionsBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueBlockMappingsBuffer);
        ReleaseComputeBuffer(ref fallbackUnityIndirectDrawArgsBuffer);
    }

    private static void ReleaseComputeBuffer(ref ComputeBuffer buffer)
    {
        if (buffer == null)
            return;

        buffer.Release();
        buffer = null;
    }

    private BlockType ResolveWaterStateForDebug(BlockType blockType)
    {
        if (!enableWater && FluidBlockUtility.IsWater(blockType))
            return BlockType.Air;

        return blockType;
    }

    private int ComputeTreeLeafFoliageSettingsHash()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp01(treeLeafFoliageSpawnChance) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHeightMin, 0.2f, 2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHeightMax, 0.2f, 2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHalfWidthMin, 0.5f, 1f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageHalfWidthMax, 0.5f, 1f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageBaseYOffsetMin, -0.2f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageBaseYOffsetMax, -0.2f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafFoliageCenterJitter, 0f, 0.2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBillboardHeight, 0.4f, 2.5f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBillboardHalfWidth, 0.5f, 1.6f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraBaseYOffset, -0.4f, 0.4f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraCenterJitter, 0f, 0.2f) * 10000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraRotationOffsetDegrees, 0f, 45f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraRotationRandomDegrees, 0f, 30f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraFaceTiltDegrees, 0f, 60f) * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(Mathf.Clamp(treeLeafUltraFaceTiltRandomDegrees, 0f, 30f) * 1000f);
            return hash;
        }
    }

    private BlockType GetProceduralSeaBlockOrAir(int worldY)
    {
        if (!enableWater || worldY > seaLevel)
            return BlockType.Air;

        return BlockType.Water;
    }

    private Vector2Int GetCurrentPlayerChunkCoord()
    {
        if (player == null)
            return _lastChunkCoord;

        return new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );
    }

    private float GetChunkDistanceSqToPlayer(Vector2Int coord)
    {
        if (player == null)
        {
            float fallbackDx = coord.x - _lastChunkCoord.x;
            float fallbackDz = coord.y - _lastChunkCoord.y;
            return fallbackDx * fallbackDx + fallbackDz * fallbackDz;
        }

        // Usa distancia em coordenadas de chunk com posicao fracionaria do player.
        float playerChunkX = player.position.x / Chunk.SizeX;
        float playerChunkZ = player.position.z / Chunk.SizeZ;
        float centerX = coord.x + 0.5f;
        float centerZ = coord.y + 0.5f;
        float dx = centerX - playerChunkX;
        float dz = centerZ - playerChunkZ;
        return dx * dx + dz * dz;
    }

    private bool IsCoordInsideRenderDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideCircularDistance(coord, center, renderDistance);
    }

    private bool ShouldChunkUseDetailedGeneration(Vector2Int coord)
    {
        return ShouldChunkUseDetailedGeneration(coord, GetCurrentPlayerChunkCoord());
    }

    private bool ShouldChunkUseDetailedGeneration(Vector2Int coord, Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        return IsCoordInsideCircularDistance(coord, center, Mathf.Max(0, chunkDetailLodDistance));
    }

    private bool ShouldChunkStartDetailedGeneration(Vector2Int coord)
    {
        return ShouldChunkStartDetailedGeneration(coord, GetCurrentPlayerChunkCoord());
    }

    private bool ShouldChunkStartDetailedGeneration(Vector2Int coord, Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        if (!ShouldChunkUseDetailedGeneration(coord, center))
            return false;

        int immediateDistance = Mathf.Clamp(chunkImmediateDetailDistance, 0, Mathf.Max(0, chunkDetailLodDistance));
        if (immediateDistance > 0 && IsCoordInsideCircularDistance(coord, center, immediateDistance))
            return true;

        return !ShouldPauseDetailedChunkPromotions(center);
    }

    private SpaghettiCaveSettings GetSpaghettiCaveSettingsForChunk(bool useDetailedGeneration)
    {
        if (useDetailedGeneration)
            return caveSpaghettiSettings;

        SpaghettiCaveSettings simplifiedSettings = caveSpaghettiSettings;
        simplifiedSettings.enabled = false;
        return simplifiedSettings;
    }

    private bool ShouldGenerateGrassBillboardsForChunk(bool useDetailedGeneration)
    {
        return enableGrassBillboards && useDetailedGeneration;
    }

    private bool ShouldPauseDetailedChunkPromotions(Vector2Int center)
    {
        if (!enableChunkDetailLod)
            return true;

        if (Time.time - lastPlayerChunkCoordChangeTime < Mathf.Max(0f, chunkDetailPromotionDelaySeconds))
            return true;

        int pendingDataInRange = CountPendingDataJobsInRenderDistance(center);
        int pendingMeshBuildsInRange = CountPendingMeshBuildRequestsInRenderDistance(center);
        int pendingMeshesInRange = CountPendingMeshesInRenderDistance(center);
        int pendingDataLimit = Mathf.Max(1, maxPendingDataJobs * 2);
        int pendingMeshBuildLimit = Mathf.Max(1, maxMeshBuildRequestBacklog * 2);
        int pendingMeshLimit = Mathf.Max(1, maxPendingMeshJobBacklog);

        if (pendingDataInRange >= pendingDataLimit)
            return true;

        if (pendingMeshBuildsInRange >= pendingMeshBuildLimit)
            return true;

        if (pendingMeshesInRange >= pendingMeshLimit)
            return true;

        return false;
    }

    private void RefreshChunkDetailPromotionCandidates(Vector2Int center)
    {
        if (!enableChunkDetailLod || activeChunks == null || activeChunks.Count == 0)
            return;

        foreach (var kv in activeChunks)
        {
            Chunk activeChunk = kv.Value;
            if (activeChunk == null)
                continue;

            if (!ShouldChunkUseDetailedGeneration(kv.Key, center))
                continue;

            if (activeChunk.requestedDetailedGeneration || activeChunk.hasDetailedGenerationData)
                continue;

            EnqueueChunkDetailPromotion(kv.Key);
        }
    }

    private void EnqueueChunkDetailPromotion(Vector2Int coord)
    {
        if (!queuedChunkDetailPromotionsSet.Add(coord))
            return;

        queuedChunkDetailPromotions.Enqueue(coord);
    }

    private void ClearChunkDetailPromotionQueue()
    {
        queuedChunkDetailPromotions.Clear();
        queuedChunkDetailPromotionsSet.Clear();
    }

    private int GetEffectiveSimulationDistance()
    {
        return Mathf.Clamp(simulationDistance, 0, Mathf.Max(0, renderDistance));
    }

    public void SetRenderDistance(int value)
    {
        int clampedDistance = Mathf.Clamp(value, MinRenderDistance, MaxRenderDistance);
        if (renderDistance == clampedDistance)
            return;

        renderDistance = clampedDistance;
        simulationDistance = Mathf.Clamp(simulationDistance, 0, renderDistance);
        _lastChunkCoord = new Vector2Int(int.MinValue, int.MinValue);
        pendingJobPrioritiesDirty = true;
    }

    private static bool IsCoordInsideCircularDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = coord.x - center.x;
        int dz = coord.y - center.y;
        return dx * dx + dz * dz <= clampedDistance * clampedDistance;
    }

    private static bool IsCoordInsideDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = Mathf.Abs(coord.x - center.x);
        int dz = Mathf.Abs(coord.y - center.y);
        return dx <= clampedDistance && dz <= clampedDistance;
    }

    private bool IsCoordInsideSimulationDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideDistance(coord, center, GetEffectiveSimulationDistance());
    }

    private void InvalidateNativeGenerationCaches()
    {
        nativeGenerationConfigDirty = true;
    }

    private void DisposeNativeGenerationCaches()
    {
        if (cachedNativeNoiseLayers.IsCreated) cachedNativeNoiseLayers.Dispose();
        if (cachedNativeBlockMappings.IsCreated) cachedNativeBlockMappings.Dispose();
        if (cachedNativeBlockModelCuboids.IsCreated) cachedNativeBlockModelCuboids.Dispose();
        if (cachedNativeEffectiveLightOpacityByBlock.IsCreated) cachedNativeEffectiveLightOpacityByBlock.Dispose();
        if (cachedNativeLightEmissionByBlock.IsCreated) cachedNativeLightEmissionByBlock.Dispose();
        if (cachedNativeOreSettings.IsCreated) cachedNativeOreSettings.Dispose();
        if (cachedNativeTreeSpawnRules.IsCreated) cachedNativeTreeSpawnRules.Dispose();
        if (cachedNativeVegetationBillboardRules.IsCreated) cachedNativeVegetationBillboardRules.Dispose();
    }

    private void EnsureNativeGenerationCaches()
    {
        // Copia a configuracao viva do World para NativeArrays persistentes usados pelos jobs.
        bool cachesCreated = cachedNativeNoiseLayers.IsCreated &&
                             cachedNativeBlockMappings.IsCreated &&
                             cachedNativeBlockModelCuboids.IsCreated &&
                             cachedNativeEffectiveLightOpacityByBlock.IsCreated &&
                             cachedNativeLightEmissionByBlock.IsCreated &&
                             cachedNativeOreSettings.IsCreated &&
                             cachedNativeTreeSpawnRules.IsCreated &&
                             cachedNativeVegetationBillboardRules.IsCreated;

        if (!nativeGenerationConfigDirty && cachesCreated)
        {
            return;
        }

        if (nativeGenerationConfigDirty && cachesCreated &&
            (pendingDataJobs.Count > 0 || pendingMeshes.Count > 0))
        {
            // Evita trocar buffers no meio de jobs ainda em voo.
            return;
        }

        DisposeNativeGenerationCaches();

        NoiseLayer[] runtimeNoiseLayers = noiseLayers ?? Array.Empty<NoiseLayer>();
        BlockTextureMapping[] runtimeBlockMappings = blockData != null && blockData.mappings != null
            ? blockData.mappings
            : Array.Empty<BlockTextureMapping>();
        BlockModelCuboid[] runtimeBlockModelCuboids = blockData != null && blockData.runtimeMultiCuboidBoxes != null
            ? blockData.runtimeMultiCuboidBoxes
            : Array.Empty<BlockModelCuboid>();
        OreSpawnSettings[] runtimeOreSettings = oreSettings ?? Array.Empty<OreSpawnSettings>();
        TreeSpawnRuleData[] runtimeTreeSpawnRules = GetActiveTreeSpawnRules();
        VegetationBillboardRuleData[] runtimeVegetationBillboardRules = GetActiveVegetationBillboardRules();

        cachedNativeNoiseLayers = new NativeArray<NoiseLayer>(runtimeNoiseLayers, Allocator.Persistent);
        cachedNativeBlockMappings = new NativeArray<BlockTextureMapping>(runtimeBlockMappings, Allocator.Persistent);
        cachedNativeBlockModelCuboids = new NativeArray<BlockModelCuboid>(runtimeBlockModelCuboids, Allocator.Persistent);
        cachedNativeEffectiveLightOpacityByBlock = new NativeArray<byte>(runtimeBlockMappings.Length, Allocator.Persistent);
        cachedNativeLightEmissionByBlock = new NativeArray<ushort>(runtimeBlockMappings.Length, Allocator.Persistent);
        for (int i = 0; i < runtimeBlockMappings.Length; i++)
        {
            cachedNativeEffectiveLightOpacityByBlock[i] = ChunkLighting.GetEffectiveOpacity(runtimeBlockMappings[i]);
            cachedNativeLightEmissionByBlock[i] = LightUtils.PackEmission(runtimeBlockMappings[i].lightEmission, runtimeBlockMappings[i].lightColor);
        }
        cachedNativeOreSettings = new NativeArray<OreSpawnSettings>(runtimeOreSettings, Allocator.Persistent);
        cachedNativeTreeSpawnRules = new NativeArray<TreeSpawnRuleData>(runtimeTreeSpawnRules, Allocator.Persistent);
        cachedNativeVegetationBillboardRules = new NativeArray<VegetationBillboardRuleData>(runtimeVegetationBillboardRules, Allocator.Persistent);
        nativeGenerationConfigDirty = false;
    }

    private void RefreshSimulationDistanceStateIfNeeded()
    {
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int effectiveDistance = GetEffectiveSimulationDistance();
        if (simulationCenter == _lastSimulationCenter && effectiveDistance == _lastSimulationDistance)
            return;

        _lastSimulationCenter = simulationCenter;
        _lastSimulationDistance = effectiveDistance;
        RefreshSimulationDistanceState(simulationCenter);
    }

    private bool IsChunkInsideSimulationDistance(Vector2Int coord)
    {
        return IsCoordInsideSimulationDistance(coord, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos)
    {
        return IsWorldPositionInsideSimulationDistance(worldPos, GetCurrentPlayerChunkCoord());
    }

    private bool IsWorldPositionInsideSimulationDistance(Vector3Int worldPos, Vector2Int center)
    {
        return IsCoordInsideSimulationDistance(GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z), center);
    }

    private int CountPendingDataJobsInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingDataJobs[i].coord, center))
                count++;
        }

        return count;
    }

    private int CountPendingMeshesInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingMeshes[i].coord, center))
                count++;
        }

        return count;
    }

    private int CountReadyPendingMeshes()
    {
        int count = 0;
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pendingMesh = pendingMeshes[i];
            if (pendingMesh.jobCompleted || pendingMesh.handle.IsCompleted)
                count++;
        }

        return count;
    }

    private int CountPendingMeshBuildRequestsInRenderDistance(Vector2Int center)
    {
        int count = 0;
        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            if (IsCoordInsideRenderDistance(pendingMeshBuildRequests[i].coord, center))
                count++;
        }

        return count;
    }

    private bool ShouldPauseChunkDataScheduling(Vector2Int center)
    {
        int readyMeshBacklog = CountReadyPendingMeshes();
        if (pauseChunkSchedulingWhenMeshesReady && readyMeshBacklog > Mathf.Max(0, maxReadyMeshApplyBacklog))
            return true;

        int meshBuildBacklogLimit = Mathf.Max(1, maxMeshBuildRequestBacklog);
        if (CountPendingMeshBuildRequestsInRenderDistance(center) > meshBuildBacklogLimit)
            return true;

        int meshJobBacklogLimit = Mathf.Max(1, maxPendingMeshJobBacklog);
        if (CountPendingMeshesInRenderDistance(center) > meshJobBacklogLimit)
            return true;

        return false;
    }

    private void RefreshPendingChunkPriorities()
    {
        for (int i = 0; i < pendingChunks.Count; i++)
        {
            var item = pendingChunks[i];
            pendingChunks[i] = (item.coord, GetChunkDistanceSqToPlayer(item.coord));
        }

        if (pendingChunkDistanceComparison == null)
            pendingChunkDistanceComparison = ComparePendingChunkByDistance;

        pendingChunks.Sort(pendingChunkDistanceComparison);
    }

    private void PrioritizePendingJobsByDistance()
    {
        Vector2Int priorityCenter = GetCurrentPlayerChunkCoord();
        if (!pendingJobPrioritiesDirty && priorityCenter == _lastPendingJobPriorityCenter)
            return;

        _lastPendingJobPriorityCenter = priorityCenter;
        pendingJobPrioritiesDirty = false;

        if (pendingDataJobs.Count > 1)
        {
            if (pendingDataDistanceComparison == null)
                pendingDataDistanceComparison = ComparePendingDataByDistance;

            pendingDataJobs.Sort(pendingDataDistanceComparison);
        }

        if (pendingMeshBuildRequests.Count > 1)
        {
            if (pendingDataDistanceComparison == null)
                pendingDataDistanceComparison = ComparePendingDataByDistance;

            pendingMeshBuildRequests.Sort(pendingDataDistanceComparison);
        }

        if (pendingMeshes.Count > 1)
        {
            if (pendingMeshDistanceComparison == null)
                pendingMeshDistanceComparison = ComparePendingMeshByDistance;

            pendingMeshes.Sort(pendingMeshDistanceComparison);
        }
    }

    private static int ComparePendingChunkByDistance((Vector2Int coord, float distSq) a, (Vector2Int coord, float distSq) b)
    {
        return a.distSq.CompareTo(b.distSq);
    }

    private int ComparePendingDataByDistance(PendingData a, PendingData b)
    {
        return GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord));
    }

    private int ComparePendingMeshByDistance(PendingMesh a, PendingMesh b)
    {
        int distCmp = GetChunkDistanceSqToPlayer(a.coord).CompareTo(GetChunkDistanceSqToPlayer(b.coord));
        if (distCmp != 0)
            return distCmp;

        return a.visualSliceIndex.CompareTo(b.visualSliceIndex);
    }

    private Camera ResolveMeshApplyPriorityCamera()
    {
        if (cachedMeshApplyPriorityCamera != null && cachedMeshApplyPriorityCamera.isActiveAndEnabled)
            return cachedMeshApplyPriorityCamera;

        cachedMeshApplyPriorityCamera = null;
        if (player != null)
            cachedMeshApplyPriorityCamera = player.GetComponentInChildren<Camera>();

        if (cachedMeshApplyPriorityCamera == null || !cachedMeshApplyPriorityCamera.isActiveAndEnabled)
            cachedMeshApplyPriorityCamera = Camera.main;

        return cachedMeshApplyPriorityCamera != null && cachedMeshApplyPriorityCamera.isActiveAndEnabled
            ? cachedMeshApplyPriorityCamera
            : null;
    }

    private bool TryResolvePendingMeshApplyTarget(PendingMesh pm, out Chunk activeChunk, out ChunkRenderSlice visualSlice)
    {
        visualSlice = null;
        if (!activeChunks.TryGetValue(pm.coord, out activeChunk) || activeChunk == null)
            return false;

        if (activeChunk.generation != pm.expectedGen || HasQueuedChunkRebuild(pm.coord))
            return false;

        return activeChunk.TryGetVisualSlice(pm.visualSliceIndex, out visualSlice) && visualSlice != null;
    }

    private int SelectNextPendingMeshApplyIndex(Camera priorityCamera, bool hasPriorityCamera)
    {
        int bestIndex = -1;
        int staleReadyIndex = -1;
        float bestScore = float.NegativeInfinity;
        int readyCandidatesScanned = 0;
        int scanLimit = Mathf.Max(0, meshApplyPriorityScanLimit);

        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pm = pendingMeshes[i];
            if (!pm.jobCompleted && !pm.handle.IsCompleted)
                continue;

            if (!TryResolvePendingMeshApplyTarget(pm, out Chunk activeChunk, out ChunkRenderSlice visualSlice))
            {
                if (staleReadyIndex < 0)
                    staleReadyIndex = i;
                continue;
            }

            if (!enableSmartMeshApplyPrioritization)
                return i;

            float score = ComputePendingMeshApplyPriority(pm, activeChunk, visualSlice, priorityCamera, hasPriorityCamera);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }

            readyCandidatesScanned++;
            if (scanLimit > 0 && readyCandidatesScanned >= scanLimit)
                break;
        }

        return bestIndex >= 0 ? bestIndex : staleReadyIndex;
    }

    private float ComputePendingMeshApplyPriority(
        PendingMesh pm,
        Chunk activeChunk,
        ChunkRenderSlice visualSlice,
        Camera priorityCamera,
        bool hasPriorityCamera)
    {
        float score = pm.lightingOnlyRebuild ? 12000f : 0f;
        score -= GetChunkDistanceSqToPlayer(pm.coord) * 650f;

        Bounds sliceBounds = GetPendingMeshSliceWorldBounds(pm, activeChunk, visualSlice);
        if (visualSlice != null && visualSlice.meshRenderer != null)
        {
            if (visualSlice.meshRenderer.isVisible)
                score += 90000f;
            if (visualSlice.meshRenderer.enabled)
                score += 6000f;
        }

        if (!hasPriorityCamera || priorityCamera == null)
            return score - pm.visualSliceIndex;

        bool isInsideFrustum = GeometryUtility.TestPlanesAABB(meshApplyPriorityFrustumPlanes, sliceBounds);
        score += isInsideFrustum ? 55000f : -45000f;

        Transform cameraTransform = priorityCamera.transform;
        Vector3 toSlice = sliceBounds.center - cameraTransform.position;
        float distanceSq = toSlice.sqrMagnitude;
        score -= distanceSq * 0.018f;

        if (distanceSq > 0.001f)
        {
            float forwardDot = Vector3.Dot(cameraTransform.forward, toSlice / Mathf.Sqrt(distanceSq));
            score += forwardDot >= 0f ? forwardDot * 14000f : forwardDot * 28000f;
        }

        float cameraY = cameraTransform.position.y;
        if (cameraY >= sliceBounds.min.y - Chunk.SubchunkHeight &&
            cameraY <= sliceBounds.max.y + Chunk.SubchunkHeight)
        {
            score += 8000f;
        }

        return score;
    }

    private Bounds GetPendingMeshSliceWorldBounds(PendingMesh pm, Chunk activeChunk, ChunkRenderSlice visualSlice)
    {
        int startSubchunk = visualSlice != null
            ? visualSlice.StartSubchunkIndex
            : Mathf.Clamp(pm.visualSliceIndex * Mathf.Max(1, activeChunk.visualSubchunksPerRenderer), 0, Chunk.SubchunksPerColumn - 1);
        int endSubchunk = visualSlice != null
            ? visualSlice.EndSubchunkIndexExclusive
            : Mathf.Min(startSubchunk + Mathf.Max(1, activeChunk.visualSubchunksPerRenderer), Chunk.SubchunksPerColumn);

        float startY = startSubchunk * Chunk.SubchunkHeight;
        float endY = Mathf.Min(endSubchunk * Chunk.SubchunkHeight, Chunk.SizeY);
        float height = Mathf.Max(1f, endY - startY);
        Vector3 origin = activeChunk != null ? activeChunk.transform.position : new Vector3(pm.coord.x * Chunk.SizeX, 0f, pm.coord.y * Chunk.SizeZ);

        return new Bounds(
            origin + new Vector3(Chunk.SizeX * 0.5f, startY + height * 0.5f, Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX + 2f, height + 2f, Chunk.SizeZ + 2f));
    }

    private bool HasOtherPendingMeshJobs(Vector2Int coord, int expectedGen, int excludeIndex)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (i == excludeIndex)
                continue;

            PendingMesh pm = pendingMeshes[i];
            if (pm.coord == coord && pm.expectedGen == expectedGen)
                return true;
        }

        return false;
    }

    private int GetMeshNeighborPadding()
    {
        return 1;
    }

    private int GetDetailedGenerationBorderSize()
    {
        return Mathf.Max(GetMeshNeighborPadding(), detailedGenerationPadding);
    }

    private int GetLightSmoothingBorderSize()
    {
        if (!enableVoxelLighting || !enableHorizontalSkylight)
            return GetMeshNeighborPadding();

        return Mathf.Max(GetMeshNeighborPadding(), sunlightSmoothingPadding);
    }

    private int GetChunkBorderSize()
    {
        return Mathf.Max(GetDetailedGenerationBorderSize(), GetLightSmoothingBorderSize());
    }

    private int GetResolvedVisualSubchunksPerRenderer()
    {
        return Mathf.Clamp(visualSubchunksPerRenderer, 1, Chunk.SubchunksPerColumn);
    }

    private void ApplyResolvedVisualSubchunkRendererLayout()
    {
        int resolved = GetResolvedVisualSubchunksPerRenderer();
        if (resolved == lastResolvedVisualSubchunksPerRenderer)
            return;

        lastResolvedVisualSubchunksPerRenderer = resolved;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null)
                continue;

            chunk.InitializeSubchunks(ActiveWorldMaterials, resolved);
            chunk.UpdateWorldBounds();
            RequestFullChunkRebuild(kv.Key, false);
        }
    }

    private static bool CanChunkProvideVoxelSnapshot(Chunk chunk)
    {
        return chunk != null &&
               chunk.voxelData.IsCreated &&
               chunk.hasVoxelSnapshot;
    }

    private static Vector3Int GetColliderBuildKey(Vector2Int coord, int subchunkIndex)
    {
        return new Vector3Int(coord.x, subchunkIndex, coord.y);
    }

    private void EnqueueColliderBuild(Vector2Int coord, int expectedGen, int subchunkIndex)
    {
        Vector3Int key = GetColliderBuildKey(coord, subchunkIndex);
        PendingColliderBuild request = new PendingColliderBuild
        {
            coord = coord,
            expectedGen = expectedGen,
            subchunkIndex = subchunkIndex
        };

        if (queuedColliderBuildsByKey.ContainsKey(key))
        {
            queuedColliderBuildsByKey[key] = request;
            return;
        }

        queuedColliderBuildsByKey.Add(key, request);
        queuedColliderBuilds.Enqueue(key);
    }

    private void ProcessPendingColliderBuilds()
    {
        if (!enableBlockColliders || queuedColliderBuilds.Count == 0)
            return;

        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = colliderBuildTimeBudgetMS > 0f ? colliderBuildTimeBudgetMS / 1000f : 0f;
        int perFrameLimit = Mathf.Max(1, maxColliderBuildsPerFrame);
        int processed = 0;
        int attempts = queuedColliderBuilds.Count;
        BlockTextureMapping[] blockMappings = blockData != null ? blockData.mappings : null;
        BlockModelCuboid[] blockModelCuboids = blockData != null ? blockData.runtimeMultiCuboidBoxes : null;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        while (processed < perFrameLimit && attempts-- > 0 && queuedColliderBuilds.Count > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            Vector3Int key = queuedColliderBuilds.Dequeue();
            if (!queuedColliderBuildsByKey.TryGetValue(key, out PendingColliderBuild request))
                continue;

            queuedColliderBuildsByKey.Remove(key);

            if (!activeChunks.TryGetValue(request.coord, out Chunk chunk) ||
                chunk == null ||
                chunk.generation != request.expectedGen ||
                !chunk.hasVoxelData ||
                !chunk.voxelData.IsCreated ||
                request.subchunkIndex < 0 ||
                request.subchunkIndex >= chunk.SubchunkCount)
            {
                continue;
            }

            if (!IsCoordInsideSimulationDistance(request.coord, simulationCenter))
            {
                chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, false);
                EnqueueColliderBuild(request.coord, request.expectedGen, request.subchunkIndex);
                continue;
            }

            if (!chunk.HasSubchunkGeometry(request.subchunkIndex) ||
                !chunk.CanSubchunkHaveColliders(request.subchunkIndex))
            {
                chunk.ClearSubchunkColliderData(request.subchunkIndex);
                continue;
            }

            if (chunk.HasSubchunkColliderData(request.subchunkIndex))
            {
                chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, true);
                processed++;
                continue;
            }

            int startY = request.subchunkIndex * Chunk.SubchunkHeight;
            int endY = Mathf.Min(startY + Chunk.SubchunkHeight, Chunk.SizeY);
            chunk.RebuildSubchunkColliders(request.subchunkIndex, chunk.voxelData, blockMappings, blockModelCuboids, startY, endY);
            chunk.SetSubchunkColliderSystemEnabled(request.subchunkIndex, true);
            processed++;
        }
    }

    private void EnsureTerrainOverrideIndexBuilt()
    {
        if (terrainOverrideIndexInitialized)
            return;

        terrainOverridePositionsByChunk.Clear();
        foreach (var kv in blockOverrides)
        {
            Vector3Int worldPos = kv.Key;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;

            Vector2Int coord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
            if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
            {
                positions = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
                terrainOverridePositionsByChunk[coord] = positions;
            }

            positions.Add(worldPos);
        }

        terrainOverrideIndexInitialized = true;
    }

    private void IndexTerrainOverride(Vector3Int worldPos, Vector2Int coord)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return;

        if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
        {
            positions = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
            terrainOverridePositionsByChunk[coord] = positions;
        }

        positions.Add(worldPos);
    }

    private void CollectRelevantTerrainOverridePositions(Vector2Int coord, int borderSize, List<Vector3Int> output)
    {
        output.Clear();
        if (blockOverrides.Count == 0)
            return;

        EnsureTerrainOverrideIndexBuilt();

        int minX = coord.x * Chunk.SizeX - borderSize;
        int minZ = coord.y * Chunk.SizeZ - borderSize;
        int maxX = coord.x * Chunk.SizeX + Chunk.SizeX - 1 + borderSize;
        int maxZ = coord.y * Chunk.SizeZ + Chunk.SizeZ - 1 + borderSize;
        int chunkRadiusX = Mathf.CeilToInt(borderSize / (float)Chunk.SizeX);
        int chunkRadiusZ = Mathf.CeilToInt(borderSize / (float)Chunk.SizeZ);

        for (int dz = -chunkRadiusZ; dz <= chunkRadiusZ; dz++)
        {
            for (int dx = -chunkRadiusX; dx <= chunkRadiusX; dx++)
            {
                Vector2Int candidateCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                if (!terrainOverridePositionsByChunk.TryGetValue(candidateCoord, out HashSet<Vector3Int> positions))
                    continue;

                foreach (Vector3Int worldPos in positions)
                {
                    if (worldPos.x < minX || worldPos.x > maxX || worldPos.z < minZ || worldPos.z > maxZ)
                        continue;

                    output.Add(worldPos);
                }
            }
        }
    }

    public BlockPlacementAxis ResolvePlacementAxisForPlacement(
        BlockType blockType,
        Vector3Int hitNormal,
        Vector3 lookForward,
        Vector3 hitPoint)
    {
        if (blockType == BlockType.wire)
            return (BlockPlacementAxis)WirePlacementUtility.ResolvePlacementCode(hitNormal);

        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return BlockPlacementAxis.Y;

        return BlockPlacementRotationUtility.ResolvePlacementAxis(mapping, hitNormal, lookForward, hitPoint);
    }

    public BlockPlacementAxis GetPlacementAxisAt(Vector3Int worldPos, BlockType blockType)
    {
        return GetStoredPlacementAxis(worldPos, blockType);
    }

    private byte GetStoredPlacementAxisRawValue(Vector3Int worldPos, BlockType blockType)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return (byte)BlockPlacementAxis.Y;

        if (!blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis axis))
            return (byte)BlockPlacementAxis.Y;

        if (blockType == BlockType.wire)
            return ResolveStoredWirePlacementRaw(worldPos, (byte)axis);

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs &&
            StairPlacementUtility.IsEncodedState((byte)axis))
        {
            return (byte)axis;
        }

        return BlockPlacementRotationUtility.SanitizeStoredAxisByte((byte)axis);
    }

    private BlockPlacementAxis GetStoredPlacementAxis(Vector3Int worldPos, BlockType blockType)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return BlockPlacementAxis.Y;

        if (!blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis axis))
            return BlockPlacementAxis.Y;

        if (blockType == BlockType.wire)
            return ResolveStoredWirePlacementAxis(worldPos, (byte)axis);

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs &&
            StairPlacementUtility.IsEncodedState((byte)axis))
        {
            return axis;
        }

        BlockPlacementAxis sanitized = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);

        return sanitized;
    }

    private void UpdateStoredPlacementAxis(Vector3Int worldPos, BlockType blockType, BlockPlacementAxis axis)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        if (blockType == BlockType.wire)
        {
            UpdateStoredWirePlacementAxis(worldPos, (byte)axis);
            return;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs)
        {
            byte rawState = (byte)axis;
            if (!StairPlacementUtility.IsEncodedState(rawState))
            {
                blockPlacementAxes.Remove(worldPos);
                return;
            }

            blockPlacementAxes[worldPos] = (BlockPlacementAxis)rawState;
            return;
        }

        BlockPlacementAxis sanitized = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);
        if (sanitized == BlockPlacementAxis.Y)
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        blockPlacementAxes[worldPos] = sanitized;
    }

    private bool TryGetPlacementRotationMapping(BlockType blockType, out BlockTextureMapping mapping)
    {
        mapping = default;
        if (blockType == BlockType.Air || blockData == null)
            return false;

        BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
        if (mappingResult == null)
            return false;

        mapping = mappingResult.Value;
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        return mapping.usePlacementAxisRotation ||
               shape == BlockRenderShape.Stairs ||
               shape == BlockRenderShape.Ramp ||
               shape == BlockRenderShape.VerticalRamp;
    }

    private static BlockPlacementAxis ResolveWirePlacementAxis(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return BlockPlacementAxis.XNegative;

        if (Mathf.Abs(horizontal.z) > Mathf.Abs(horizontal.x))
            return BlockPlacementAxis.ZNegative;

        return BlockPlacementAxis.XNegative;
    }

    private static BlockPlacementAxis ResolveWirePlacementAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        return (BlockPlacementAxis)WirePlacementUtility.ResolvePlacementCode(hitNormal);
    }

    private BlockPlacementAxis ResolveStoredWirePlacementAxis(Vector3Int worldPos, byte rawValue)
    {
        rawValue = ResolveStoredWirePlacementRaw(worldPos, rawValue);
        if (WirePlacementUtility.TryGetWall(rawValue, out BlockPlacementAxis encodedWallAxis, out int encodedAttachmentSide))
        {
            if (HasWireSupportOnSide(worldPos, encodedWallAxis, encodedAttachmentSide))
                return encodedWallAxis;

            return WirePlacementUtility.HasTop(rawValue) ? BlockPlacementAxis.Y : encodedWallAxis;
        }

        BlockPlacementAxis axis = NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
        if (axis == BlockPlacementAxis.X || axis == BlockPlacementAxis.Z)
        {
            if (HasWireSupportOnAxis(worldPos, axis))
                return axis;

            return BlockPlacementAxis.Y;
        }

        return BlockPlacementAxis.Y;
    }

    private byte ResolveStoredWirePlacementRaw(Vector3Int worldPos, byte rawValue)
    {
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return rawValue;

        BlockPlacementAxis axis = NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
        return axis switch
        {
            BlockPlacementAxis.X when HasWireSupportOnSide(worldPos, BlockPlacementAxis.X, -1) => WirePlacementUtility.SideWest,
            BlockPlacementAxis.X when HasWireSupportOnSide(worldPos, BlockPlacementAxis.X, 1) => WirePlacementUtility.SideEast,
            BlockPlacementAxis.Z when HasWireSupportOnSide(worldPos, BlockPlacementAxis.Z, -1) => WirePlacementUtility.SideSouth,
            BlockPlacementAxis.Z when HasWireSupportOnSide(worldPos, BlockPlacementAxis.Z, 1) => WirePlacementUtility.SideNorth,
            _ => (byte)BlockPlacementAxis.Y
        };
    }

    private bool HasWireSupportOnAxis(Vector3Int worldPos, BlockPlacementAxis axis)
    {
        axis = BlockPlacementRotationUtility.SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => IsWireSupportBlock(worldPos + Vector3Int.left) || IsWireSupportBlock(worldPos + Vector3Int.right),
            BlockPlacementAxis.Z => IsWireSupportBlock(worldPos + new Vector3Int(0, 0, -1)) || IsWireSupportBlock(worldPos + new Vector3Int(0, 0, 1)),
            _ => false
        };
    }

    private bool HasWireSupportOnSide(Vector3Int worldPos, BlockPlacementAxis axis, int attachmentSide)
    {
        return axis switch
        {
            BlockPlacementAxis.X => IsWireSupportBlock(worldPos + (attachmentSide < 0 ? Vector3Int.left : Vector3Int.right)),
            BlockPlacementAxis.Z => IsWireSupportBlock(worldPos + (attachmentSide < 0 ? new Vector3Int(0, 0, -1) : new Vector3Int(0, 0, 1))),
            _ => false
        };
    }

    private bool IsWireSupportBlock(Vector3Int worldPos)
    {
        BlockType supportType = GetBlockAt(worldPos);
        if (supportType == BlockType.Air || FluidBlockUtility.IsWater(supportType) || blockData == null)
            return false;

        BlockTextureMapping? mapping = blockData.GetMapping(supportType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty && !value.isLiquid;
    }

    private static BlockPlacementAxis NormalizeWirePlacementAxis(BlockPlacementAxis axis)
    {
        byte rawValue = (byte)axis;
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return (BlockPlacementAxis)rawValue;

        axis = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.XNegative,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.ZNegative,
            _ => BlockPlacementAxis.Y
        };
    }

    private void UpdateStoredWirePlacementAxis(Vector3Int worldPos, byte rawValue)
    {
        rawValue = NormalizeStoredWirePlacementRaw(rawValue);
        if (rawValue == (byte)BlockPlacementAxis.Y)
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        blockPlacementAxes[worldPos] = (BlockPlacementAxis)rawValue;
    }

    private static byte NormalizeStoredWirePlacementRaw(byte rawValue)
    {
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return rawValue;

        return (byte)NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
    }

    private static NativeArray<byte> CreateDefaultPlacementAxisArray(int length)
    {
        return MeshGenerator.RentByteBuffer(length, true);
    }

    private static void ApplyPlacementAxesFromBlockEdits(
        NativeArray<BlockEdit> edits,
        NativeArray<byte> blockPlacementAxes,
        int chunkMinX,
        int chunkMinZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (!edits.IsCreated || edits.Length == 0 || !blockPlacementAxes.IsCreated)
            return;

        for (int i = 0; i < edits.Length; i++)
        {
            BlockEdit edit = edits[i];
            if (edit.y < 0 || edit.y >= Chunk.SizeY)
                continue;

            int ix = edit.x - chunkMinX + borderSize;
            int iz = edit.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
            if ((uint)idx >= (uint)blockPlacementAxes.Length)
                continue;

            blockPlacementAxes[idx] = edit.placementAxis;
        }
    }

    private void AppendRelevantBlockEdits(Vector2Int coord, int borderSize, List<BlockEdit> editsList)
    {
        if (editsList == null)
            return;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            editsList.Add(new BlockEdit
            {
                x = worldPos.x,
                y = worldPos.y,
                z = worldPos.z,
                type = (int)overrideType,
                placementAxis = GetStoredPlacementAxisRawValue(worldPos, overrideType)
            });
        }
    }

    private TreeSpawnRuleData[] GetActiveTreeSpawnRules()
    {
        if (treeSpawnRulesDirty)
            RebuildTreeSpawnRuleCache();

        return cachedTreeSpawnRules;
    }

    private VegetationBillboardRuleData[] GetActiveVegetationBillboardRules()
    {
        if (vegetationBillboardRulesDirty)
            RebuildVegetationBillboardRuleCache();

        return cachedVegetationBillboardRules;
    }

    public bool TryResolveVegetationBillboardAt(Vector3Int billboardPos, out BlockType billboardBlockType, out uint variationHash)
    {
        billboardBlockType = grassBillboardBlockType;
        variationHash = 0u;

        if (!enableGrassBillboards || grassBillboardChance <= 0f || billboardPos.y <= 0)
            return false;
        if (IsGrassBillboardSuppressed(billboardPos))
            return false;
        if (GetBlockAt(billboardPos) != BlockType.Air)
            return false;

        BlockType groundBlockType = GetBlockAt(new Vector3Int(billboardPos.x, billboardPos.y - 1, billboardPos.z));
        return TryResolveVegetationBillboardRule(
            billboardPos.x,
            billboardPos.y,
            billboardPos.z,
            groundBlockType,
            out billboardBlockType,
            out variationHash);
    }

    public bool TryResolveVegetationBillboardRule(
        int worldX,
        int worldY,
        int worldZ,
        BlockType groundBlockType,
        out BlockType billboardBlockType,
        out uint variationHash)
    {
        return VegetationBillboardUtility.TryResolveBillboardRule(
            GetBiomeNoiseSettings(),
            GetActiveVegetationBillboardRules(),
            worldX,
            worldY,
            worldZ,
            groundBlockType,
            grassBillboardChance,
            grassBillboardNoiseScale,
            grassBillboardBlockType,
            out billboardBlockType,
            out variationHash);
    }

    private int GetMaxTreeCanopyRadiusForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 0;

        int maxRadius = 0;
        for (int i = 0; i < rules.Length; i++)
            maxRadius = Mathf.Max(maxRadius, TreeGenerationMetrics.GetHorizontalReach(rules[i].treeStyle, rules[i].settings));

        return maxRadius;
    }

    private int GetMaxTreeRadiusForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 0;

        int maxRadius = 0;
        for (int i = 0; i < rules.Length; i++)
            maxRadius = Mathf.Max(maxRadius, TreeGenerationMetrics.GetPlacementSpacingRadius(rules[i].treeStyle, rules[i].settings));

        return maxRadius;
    }

    private int GetMaxTreeMarginForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 1;

        int maxMargin = 1;
        for (int i = 0; i < rules.Length; i++)
        {
            TreeSettings s = rules[i].settings;
            maxMargin = Mathf.Max(maxMargin, Mathf.Max(1, TreeGenerationMetrics.GetVerticalMargin(rules[i].treeStyle, s)));
        }

        return maxMargin;
    }

    private void RebuildTreeSpawnRuleCache()
    {
        treeSpawnRulesDirty = false;

        // As regras de spawn sao derivadas dos biomas para centralizar a configuracao.
        List<TreeSpawnRuleData> rules = treeSpawnRuleBuildBuffer;
        rules.Clear();
        AddTreeRulesFromBiomeDefinitions(rules);
        SortTreeSpawnRules(rules);

        cachedTreeSpawnRules = rules.Count > 0 ? rules.ToArray() : Array.Empty<TreeSpawnRuleData>();
    }

    private void RebuildVegetationBillboardRuleCache()
    {
        vegetationBillboardRulesDirty = false;

        List<VegetationBillboardRuleData> rules = vegetationBillboardRuleBuildBuffer;
        rules.Clear();
        AddVegetationRulesFromBiomeDefinitions(rules);

        if (rules.Count > 1)
        {
            rules.Sort((a, b) =>
            {
                int biomeCompare = a.biome.CompareTo(b.biome);
                if (biomeCompare != 0)
                    return biomeCompare;

                int groundCompare = a.groundBlock.CompareTo(b.groundBlock);
                if (groundCompare != 0)
                    return groundCompare;

                int weightCompare = b.weight.CompareTo(a.weight);
                if (weightCompare != 0)
                    return weightCompare;

                return a.billboardBlock.CompareTo(b.billboardBlock);
            });
        }

        cachedVegetationBillboardRules = rules.Count > 0
            ? rules.ToArray()
            : Array.Empty<VegetationBillboardRuleData>();
    }

    private void AddTreeRulesFromBiomeDefinitions(List<TreeSpawnRuleData> rules)
    {
        BiomeDefinitionSO[] definitions = GetConfiguredBiomeDefinitions();
        if (definitions == null || definitions.Length == 0)
            return;

        for (int i = 0; i < definitions.Length; i++)
        {
            BiomeDefinitionSO definition = definitions[i];
            if (definition == null || !definition.hasTrees)
                continue;
            if (definition.treeConfigs == null || definition.treeConfigs.Length == 0)
                continue;

            for (int j = 0; j < definition.treeConfigs.Length; j++)
            {
                BiomeTreeConfig treeConfig = definition.treeConfigs[j];
                if (!treeConfig.enabled)
                    continue;

                TreeSettings sanitized = SanitizeTreeSettings(treeConfig.treeStyle, treeConfig.settings);
                rules.Add(new TreeSpawnRuleData
                {
                    biome = definition.biomeType,
                    treeStyle = treeConfig.treeStyle,
                    settings = sanitized
                });
            }
        }
    }

    private void AddVegetationRulesFromBiomeDefinitions(List<VegetationBillboardRuleData> rules)
    {
        BiomeDefinitionSO[] definitions = GetConfiguredBiomeDefinitions();
        if (definitions == null || definitions.Length == 0)
            return;

        for (int i = 0; i < definitions.Length; i++)
        {
            BiomeDefinitionSO definition = definitions[i];
            if (definition == null || !definition.hasVegetationBillboards)
                continue;

            float chanceMultiplier = Mathf.Max(0f, definition.vegetationChanceMultiplier);
            BiomeVegetationBillboardConfig[] configs = definition.vegetationBillboards;
            bool addedBiomeRule = false;

            if (configs != null)
            {
                for (int j = 0; j < configs.Length; j++)
                {
                    BiomeVegetationBillboardConfig config = configs[j];
                    if (!config.enabled || config.blockType == BlockType.Air)
                        continue;

                    rules.Add(new VegetationBillboardRuleData
                    {
                        biome = definition.biomeType,
                        groundBlock = config.groundBlockType == BlockType.Air ? BlockType.Grass : config.groundBlockType,
                        billboardBlock = config.blockType,
                        weight = config.weight > 0f ? config.weight : 1f,
                        chanceMultiplier = chanceMultiplier
                    });
                    addedBiomeRule = true;
                }
            }

            // Compatibilidade: sem regras explicitas no bioma, usa o billboard global somente sobre Grass.
            if (!addedBiomeRule && grassBillboardBlockType != BlockType.Air)
            {
                rules.Add(new VegetationBillboardRuleData
                {
                    biome = definition.biomeType,
                    groundBlock = BlockType.Grass,
                    billboardBlock = grassBillboardBlockType,
                    weight = 1f,
                    chanceMultiplier = chanceMultiplier
                });
            }
        }
    }

    private static void SortTreeSpawnRules(List<TreeSpawnRuleData> rules)
    {
        if (rules == null || rules.Count <= 1)
            return;

        // Reserve space for larger canopies first so mixed-tree biomes behave more like Minecraft feature placement.
        rules.Sort((a, b) =>
        {
            int biomeCompare = a.biome.CompareTo(b.biome);
            if (biomeCompare != 0)
                return biomeCompare;

            int spacingCompare = TreeGenerationMetrics.GetPlacementSpacingRadius(b.treeStyle, b.settings)
                .CompareTo(TreeGenerationMetrics.GetPlacementSpacingRadius(a.treeStyle, a.settings));
            if (spacingCompare != 0)
                return spacingCompare;

            int densityCompare = a.settings.density.CompareTo(b.settings.density);
            if (densityCompare != 0)
                return densityCompare;

            return a.treeStyle.CompareTo(b.treeStyle);
        });
    }

    private TreeSettings SanitizeTreeSettings(TreeStyle treeStyle, TreeSettings raw)
    {
        TreeSettings s = raw;
        s.minHeight = Mathf.Max(1, s.minHeight);
        s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
        s.canopyRadius = Mathf.Max(0, s.canopyRadius);
        s.canopyHeight = Mathf.Max(1, s.canopyHeight);
        s.trunkClearance = Mathf.Max(0, s.trunkClearance);
        s.minSpacing = Mathf.Max(1, s.minSpacing);
        s.density = Mathf.Clamp01(s.density);
        s.noiseScale = Mathf.Max(0.0001f, s.noiseScale);

        switch (treeStyle)
        {
            case TreeStyle.TaigaSpruce:
                s.canopyRadius = Mathf.Max(2, s.canopyRadius);
                s.canopyHeight = Mathf.Max(5, s.canopyHeight);
                break;

            case TreeStyle.SavannaAcacia:
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(3, s.canopyHeight);
                s.minSpacing = Mathf.Max(6, s.minSpacing);
                break;

            case TreeStyle.FancyOak:
                s.minHeight = Mathf.Max(9, s.minHeight);
                s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(4, s.canopyHeight);
                s.minSpacing = Mathf.Max(9, s.minSpacing);
                break;

            case TreeStyle.Cactus:
                s.canopyRadius = Mathf.Max(1, s.canopyRadius);
                break;
        }

        if (s.seed == 0)
            s.seed = seed;

        return s;
    }

    #endregion

    #region Unity Callbacks

    private void OnValidate()
    {
        RefreshTerrainGenerationRuntimeState();

        if (!Application.isPlaying || isShuttingDown || activeChunks == null || activeChunks.Count == 0)
            return;

        int currentMaterialProfileHash = ComputeWorldMaterialProfileHash();
        if (lastWorldMaterialProfileHash != currentMaterialProfileHash)
        {
            lastWorldMaterialProfileHash = currentMaterialProfileHash;
            RefreshWorldMaterialProfileOnRenderers();
        }

        loadedChunkCoordsBuffer.Clear();
        foreach (var kv in activeChunks)
            loadedChunkCoordsBuffer.Add(kv.Key);

        for (int i = 0; i < loadedChunkCoordsBuffer.Count; i++)
            RequestFullChunkRebuild(loadedChunkCoordsBuffer[i]);
    }

    private void Start()
    {
        if (blockData != null)
        {
            blockData.InitializeDictionary();
            RebuildBlockAtlasCompatibility();
        }
        RefreshTerrainGenerationRuntimeState();
        lastWorldMaterialProfileHash = ComputeWorldMaterialProfileHash();

        // Pre-instantiate pool
        for (int i = 0; i < poolSize; i++)
        {
            Chunk chunk = CreateChunkPoolEntry();
            chunkPool.Enqueue(chunk);
        }

        lastEnableBlockColliders = enableBlockColliders;
        lastEnableRealisticShader = enableRealisticShader;
        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastEnableWater = enableWater;
        lastEnableChunkDetailLod = enableChunkDetailLod;
        lastChunkDetailLodDistance = chunkDetailLodDistance;
        lastTreeLeafQuality = treeLeafQuality;
        lastTreeLeafFoliageSettingsHash = ComputeTreeLeafFoliageSettingsHash();
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;
    }

    [ContextMenu("Rebuild Block Atlas Compatibility")]
    public void RebuildBlockAtlasCompatibility()
    {
        if (blockData == null)
            return;

        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (generator == null)
            return;

        // In builds, relying on the imported saved atlas can diverge from the
        // runtime-compatible UV data used by the world. Rebuild a fresh runtime
        // atlas during play so the terrain and inventory icons sample the same texture.
        if (Application.isPlaying && generator.HasConfiguredTextureEntries())
            generator.GenerateAtlas();

        Vector2Int legacyAtlasTiles = new Vector2Int(
            Mathf.Max(1, atlasTilesX),
            Mathf.Max(1, atlasTilesY));

        if (!VoxelAtlasCompatibility.Apply(
                generator,
                blockData,
                legacyAtlasTiles,
                blockData.atlasCoordinatesStartTopLeft))
        {
            return;
        }

        ApplyGeneratedAtlasToWorldMaterials(generator.GeneratedAtlas);

        if (blockItemIconAtlasTexture == null && generator.GeneratedAtlas != null)
            blockItemIconAtlasTexture = generator.GeneratedAtlas;

        InvalidateNativeGenerationCaches();
    }

    private TextureAtlasGenerator ResolveBlockAtlasGenerator()
    {
        if (blockAtlasGenerator != null)
            return blockAtlasGenerator;

        TextureAtlasGenerator[] generators = FindObjectsOfType<TextureAtlasGenerator>(true);
        TextureAtlasGenerator bestMatch = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < generators.Length; i++)
        {
            TextureAtlasGenerator candidate = generators[i];
            if (candidate == null)
                continue;

            int score = ScoreAtlasGenerator(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }

        blockAtlasGenerator = bestMatch;
        return blockAtlasGenerator;
    }

    private int ScoreAtlasGenerator(TextureAtlasGenerator generator)
    {
        if (generator == null)
            return int.MinValue;

        int score = 0;
        if (generator == blockAtlasGenerator)
            score += 1000;

        if (generator.targetMaterials != null)
        {
            for (int i = 0; i < generator.targetMaterials.Count; i++)
            {
                Material targetMaterial = generator.targetMaterials[i];
                if (IsWorldMaterial(targetMaterial))
                    score += 100;
            }
        }

        if (generator.targetRenderer != null)
        {
            Material[] rendererMaterials = generator.targetRenderer.sharedMaterials;
            for (int i = 0; i < rendererMaterials.Length; i++)
            {
                Material rendererMaterial = rendererMaterials[i];
                if (IsWorldMaterial(rendererMaterial))
                    score += 50;
            }
        }

        if (generator.GeneratedAtlas != null)
            score += 10;
        if (generator.generateOnStart)
            score += 5;

        return score;
    }

    private void ApplyGeneratedAtlasToWorldMaterials(Texture atlasTexture)
    {
        if (atlasTexture == null)
            return;

        ApplyGeneratedAtlasToMaterials(atlasTexture, pcMaterials);
        ApplyGeneratedAtlasToMaterials(atlasTexture, mobileMaterials);
    }

    private void ApplyGeneratedAtlasToMaterials(Texture atlasTexture, Material[] materials)
    {
        if (materials == null)
            return;

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
                continue;

            float atlasPaddingUv = ResolveAtlasShaderPaddingUv(atlasTexture, material);

            if (material.HasProperty("_Atlas"))
                material.SetTexture("_Atlas", atlasTexture);
            else if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", atlasTexture);
            else if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", atlasTexture);
            else
                material.mainTexture = atlasTexture;

            if (material.HasProperty("_AtlasOriginTopLeft"))
                material.SetFloat("_AtlasOriginTopLeft", 0f);
            if (material.HasProperty("_PaddingUV"))
                material.SetFloat("_PaddingUV", atlasPaddingUv);
        }
    }

    private float ResolveAtlasShaderPaddingUv(Texture atlasTexture, Material material)
    {
        if (atlasTexture == null)
            return 0f;

        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (generator != null)
            return generator.ComputeShaderPaddingUv(atlasTexture, material);

        int referenceSize = Mathf.Max(atlasTexture.width, atlasTexture.height);
        if (referenceSize <= 0)
            return 0f;

        // Fallback conservador quando o atlas vem de fora do generator.
        float fallbackPaddingPixels = 1f;
        if (material != null &&
            material.HasProperty("_AlphaClip") &&
            material.GetFloat("_AlphaClip") > 0.5f)
        {
            fallbackPaddingPixels = 2f;
        }

        return fallbackPaddingPixels / referenceSize;
    }

    private void OnEnable()
    {
        TerrainLayerProfileSO.ProfileChanged += HandleTerrainLayerProfileChanged;
        BiomeDefinitionSO.DefinitionChanged += HandleBiomeDefinitionChanged;
    }

    private void OnDisable()
    {
        TerrainLayerProfileSO.ProfileChanged -= HandleTerrainLayerProfileChanged;
        BiomeDefinitionSO.DefinitionChanged -= HandleBiomeDefinitionChanged;
    }

    private void OnDestroy()
    {
        isShuttingDown = true;

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            PendingData pd = pendingDataJobs[i];
            pd.handle.Complete();
            DisposeDataJobResources(ref pd);
        }
        pendingDataJobs.Clear();

        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            PendingData pd = pendingMeshBuildRequests[i];
            pd.handle.Complete();
            DisposeDataJobResources(ref pd);
        }
        pendingMeshBuildRequests.Clear();

        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pm = pendingMeshes[i];
            pm.handle.Complete();
            DisposePendingMesh(pm);
        }
        pendingMeshes.Clear();

        CompletePendingChunkDataBufferReturns();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk != null)
                chunk.CompleteTrackedJob();
        }

        MeshGenerator.ClearSpaghettiCarveMaskNeighborCache();
        MeshGenerator.ClearDataJobTempBufferPool();
        DisposeNativeGenerationCaches();

        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        ProcessRetiredChunksAwaitingRecycle();

        float updateFrameStartTime = Time.realtimeSinceStartup;
        float updateBudgetSeconds = updateWorkBudgetMS > 0f ? updateWorkBudgetMS / 1000f : 0f;

        HandleBlockColliderToggle();
        HandleWorldMaterialProfileToggle();
        HandleRealisticShaderToggle();
        HandleVisualFeatureToggle();
        ApplyResolvedVisualSubchunkRendererLayout();
        meshesAppliedThisFrame = 0;

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            UpdateSectionOcclusionVisibility();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedWaterUpdates();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedTreeCapitatorBreaks();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedInteractiveBlockLightRefreshes();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedChunkRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedLightingOnlyChunkRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedHighBuildMeshRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedLeafDecay();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            UpdateChunks();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            RefreshSimulationDistanceStateIfNeeded();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessChunkQueue(GetRemainingUpdateBudgetSeconds(updateFrameStartTime, updateBudgetSeconds));

        ProcessQueuedChunkDetailPromotions();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedChunkJobTrackingRefreshes();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessPendingColliderBuilds();

        ProcessPendingChunkDataBufferReturns();

    }

    private static bool HasUpdateBudgetRemaining(float frameStartTime, float budgetSeconds)
    {
        if (budgetSeconds <= 0f)
            return true;

        return (Time.realtimeSinceStartup - frameStartTime) <= budgetSeconds;
    }

    private static float GetRemainingUpdateBudgetSeconds(float frameStartTime, float budgetSeconds)
    {
        if (budgetSeconds <= 0f)
            return float.PositiveInfinity;

        return Mathf.Max(0f, budgetSeconds - (Time.realtimeSinceStartup - frameStartTime));
    }

    #endregion

    #region Initialization Helpers

    private void RefreshTerrainGenerationRuntimeState()
    {
        ApplyTerrainLayerProfileIfAssigned();
        EnsureTerrainLayerArraysInitialized();
        EnsureTerrainSplineShaperInitialized();

        offsetX = seed * 17.123f;
        offsetZ = seed * -9.753f;

        InitializeBiomeNoiseOffsets();
        InitializeNoiseLayers();
        MarkBiomeCachesDirty();
        MeshGenerator.ClearSpaghettiCarveMaskNeighborCache();
        MeshGenerator.ClearDataJobTempBufferPool();
    }

    private void EnsureTorchFireParticleControllerExists()
    {
        torchFireParticleController = GetComponent<TorchFireParticleController>();
        if (torchFireParticleController == null)
            torchFireParticleController = gameObject.AddComponent<TorchFireParticleController>();
    }

    private void HandleTerrainLayerProfileChanged(TerrainLayerProfileSO changedProfile)
    {
        if (changedProfile == null || changedProfile != terrainLayerProfile)
            return;

        RefreshTerrainGenerationRuntimeState();

        if (!Application.isPlaying || isShuttingDown)
            return;

        foreach (Vector2Int coord in activeChunks.Keys)
            RequestFullChunkRebuild(coord);
    }

    private void HandleBiomeDefinitionChanged(BiomeDefinitionSO changedDefinition)
    {
        if (changedDefinition == null)
            return;

        bool usesResourceDefinitions = biomeDefinitions == null || biomeDefinitions.Length == 0;
        bool referencesDefinition = usesResourceDefinitions;
        if (!referencesDefinition && biomeDefinitions != null)
        {
            for (int i = 0; i < biomeDefinitions.Length; i++)
            {
                if (biomeDefinitions[i] != changedDefinition)
                    continue;

                referencesDefinition = true;
                break;
            }
        }

        if (!referencesDefinition)
            return;

        MarkBiomeCachesDirty();

        if (!Application.isPlaying || isShuttingDown)
            return;

        foreach (Vector2Int coord in activeChunks.Keys)
            RequestFullChunkRebuild(coord);
    }

    private void ApplyTerrainLayerProfileIfAssigned()
    {
        if (terrainLayerProfile == null)
            return;

        noiseLayers = terrainLayerProfile.CloneNoiseLayers();
        terrainSplineShaper = terrainLayerProfile.CloneTerrainSplines();
    }

    private void EnsureTerrainLayerArraysInitialized()
    {
        if (noiseLayers == null)
            noiseLayers = Array.Empty<NoiseLayer>();
    }

    private void EnsureTerrainSplineShaperInitialized()
    {
        if (terrainSplineShaper.enabled || terrainSplineShaper.HasAnyControlPoints)
            return;

        if (!HasAnyModernTerrainRole())
            return;

        terrainSplineShaper = TerrainSplineShaperSettings.MinecraftModernDefault;
    }

    private bool HasAnyModernTerrainRole()
    {
        if (noiseLayers == null)
            return false;

        for (int i = 0; i < noiseLayers.Length; i++)
        {
            if (!noiseLayers[i].enabled)
                continue;

            if (noiseLayers[i].role != TerrainNoiseRole.LegacyAdditive)
                return true;
        }

        return false;
    }

    private void InitializeNoiseLayers()
    {
        if (noiseLayers == null) return;

        for (int i = 0; i < noiseLayers.Length; i++)
        {
            NoiseLayer layer = noiseLayers[i];
            if (!layer.enabled) continue;

            if (layer.scale <= 0f) layer.scale = 45f + i * 10f;
            if (layer.amplitude <= 0f) layer.amplitude = math.pow(0.55f, i);
            if (layer.octaves <= 0) layer.octaves = 3 + i;
            if (layer.lacunarity <= 0f) layer.lacunarity = 2.2f;
            if (layer.persistence <= 0f || layer.persistence > 1f) layer.persistence = 0.55f;
            if (layer.redistributionModifier == 0f) layer.redistributionModifier = 1.1f + i * 0.05f;
            if (layer.exponent == 0f) layer.exponent = 1.1f;
            if (layer.ridgeFactor <= 0f) layer.ridgeFactor = 1f + i * 0.2f;
            if (layer.domainWarpStrength <= 0f) layer.domainWarpStrength = MyNoise.GetDefaultDomainWarpStrength(layer.role);
            if (layer.domainWarpScale <= 0f) layer.domainWarpScale = 0.88f;
            if (layer.domainWarpOctaves <= 0) layer.domainWarpOctaves = 3;
            if (layer.domainWarpGain <= 0f || layer.domainWarpGain >= 1f) layer.domainWarpGain = 0.5f;
            if (layer.domainWarpLacunarity <= 1f) layer.domainWarpLacunarity = 2.03f;

            if (layer.offset == Vector2.zero)
                layer.offset = new Vector2(i * 13.37f, i * 7.53f);

            float amp = 1f;
            layer.maxAmp = 0f;
            for (int o = 0; o < layer.octaves; o++)
            {
                layer.maxAmp += amp;
                amp *= layer.persistence;
            }
            if (layer.maxAmp <= 0f) layer.maxAmp = 1f;

            noiseLayers[i] = layer;
        }
    }

    #endregion

    #region Chunk Queue & Processing

    private void ProcessChunkQueue(float budgetSecondsOverride = -1f)
    {
        float pipelineStartTime = Time.realtimeSinceStartup;
        float pipelineBudgetSeconds = GetBudgetSeconds(frameTimeBudgetMS);
        if (budgetSecondsOverride >= 0f && !float.IsPositiveInfinity(budgetSecondsOverride))
        {
            pipelineBudgetSeconds = pipelineBudgetSeconds > 0f
                ? Mathf.Min(pipelineBudgetSeconds, budgetSecondsOverride)
                : budgetSecondsOverride;
        }

        PrioritizePendingJobsByDistance();

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessPendingChunkRequests(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessCompletedDataStage(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessCompletedLightingStage(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessPendingMeshScheduleStage(pipelineStartTime, pipelineBudgetSeconds);

        if (HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds))
            ProcessCompletedMeshApplyStage(pipelineStartTime, pipelineBudgetSeconds);
    }

    private void ProcessQueuedChunkDetailPromotions()
    {
        if (!enableChunkDetailLod)
            return;

        Vector2Int center = GetCurrentPlayerChunkCoord();
        RefreshChunkDetailPromotionCandidates(center);
        if (queuedChunkDetailPromotions.Count == 0)
            return;

        if (ShouldPauseDetailedChunkPromotions(center))
            return;

        int processed = 0;
        int perFrameLimit = Mathf.Max(1, maxChunkDetailPromotionsPerFrame);
        int attempts = queuedChunkDetailPromotions.Count;

        while (processed < perFrameLimit && attempts-- > 0 && queuedChunkDetailPromotions.Count > 0)
        {
            Vector2Int coord = queuedChunkDetailPromotions.Dequeue();
            queuedChunkDetailPromotionsSet.Remove(coord);

            if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
                continue;

            if (!ShouldChunkUseDetailedGeneration(coord, center))
                continue;

            if (chunk.hasDetailedGenerationData || chunk.requestedDetailedGeneration)
                continue;

            if (HasQueuedChunkRebuild(coord) || IsChunkJobPending(coord))
            {
                EnqueueChunkDetailPromotion(coord);
                continue;
            }

            RequestChunkRebuildImmediate(coord, GetFullSubchunkMask(), true);
            processed++;
        }
    }

    private static float GetBudgetSeconds(float budgetMS)
    {
        return budgetMS > 0f ? budgetMS / 1000f : 0f;
    }

    private static bool HasPipelineAndStageBudgetRemaining(
        float pipelineStartTime,
        float pipelineBudgetSeconds,
        float stageStartTime,
        float stageBudgetSeconds)
    {
        return HasUpdateBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds) &&
               HasUpdateBudgetRemaining(stageStartTime, stageBudgetSeconds);
    }

    private void ProcessPendingChunkRequests(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingChunks.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkDataScheduleBudgetMS);
        int processed = 0;
        int subchunksPerChunk = Mathf.Max(1, Chunk.SubchunksPerColumn);
        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();
        if (ShouldPauseChunkDataScheduling(currentChunkCoord))
            return;

        int pendingDataInRange = CountPendingDataJobsInRenderDistance(currentChunkCoord);
        int pendingMeshBuildsInRange = CountPendingMeshBuildRequestsInRenderDistance(currentChunkCoord);
        int pendingMeshesInRange = CountPendingMeshesInRenderDistance(currentChunkCoord);
        int realPending = pendingDataInRange +
                          pendingMeshBuildsInRange +
                          Mathf.CeilToInt(pendingMeshesInRange / (float)subchunksPerChunk);
        int hardPendingDataLimit = Mathf.Max(maxPendingDataJobs, maxPendingDataJobs * 3);
        bool jobsCongested = realPending > maxChunksPerFrame * 4;

        if (jobsCongested)
            return;

        while (processed < maxChunksPerFrame && pendingChunks.Count > 0)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;

            if (pendingDataInRange >= maxPendingDataJobs || pendingDataJobs.Count >= hardPendingDataLimit)
                break;

            var item = pendingChunks[0];
            pendingChunks.RemoveAt(0);

            if (activeChunks.ContainsKey(item.coord) || IsChunkJobPending(item.coord))
                continue;

            RequestChunk(item.coord);
            pendingDataInRange++;
            processed++;
        }
    }

    private void ProcessCompletedDataStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingDataJobs.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkDataCompletionBudgetMS);
        int dataProcessedThisFrame = 0;
        int dataCompletionsLimit = Mathf.Max(1, maxDataCompletionsPerFrame);
        int i = 0;

        while (i < pendingDataJobs.Count)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (dataProcessedThisFrame >= dataCompletionsLimit)
                break;

            var pd = pendingDataJobs[i];
            if (pd.terrainStageCompleted)
            {
                i++;
                continue;
            }

            if (!pd.terrainHandle.IsCompleted)
            {
                i++;
                continue;
            }

            pd.terrainHandle.Complete();
            MeshGenerator.ReleaseDataJobTempBuffers(ref pd.tempBuffers);
            pd.terrainStageCompleted = true;
            pendingDataJobs[i] = pd;
            dataProcessedThisFrame++;
            i++;
        }
    }

    private void ProcessCompletedLightingStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingDataJobs.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkLightingCompletionBudgetMS);
        int lightingProcessedThisFrame = 0;
        int lightingCompletionsLimit = Mathf.Max(1, maxLightingCompletionsPerFrame);
        int i = 0;

        while (i < pendingDataJobs.Count)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (lightingProcessedThisFrame >= lightingCompletionsLimit)
                break;

            var pd = pendingDataJobs[i];
            if (!pd.terrainStageCompleted)
            {
                i++;
                continue;
            }

            if (!pd.lightingStageCompleted)
            {
                if (!pd.lightingHandle.IsCompleted)
                {
                    i++;
                    continue;
                }

                pd.lightingHandle.Complete();
                pd.lightingStageCompleted = true;
                lightingProcessedThisFrame++;
            }

            bool completedPostOverrideRefresh = pd.postOverrideRefreshScheduled;
            if (completedPostOverrideRefresh)
                DisposePostCompletionOverrideInputs(ref pd);
            else
                DisposeCompletedDataJobInputs(ref pd);

            bool hasActiveChunk = activeChunks.TryGetValue(pd.coord, out Chunk activeChunk);
            bool isLatestGeneration = hasActiveChunk && activeChunk.generation == pd.expectedGen;
            bool hasNewerRebuildQueued = HasQueuedChunkRebuild(pd.coord);

            if (isLatestGeneration)
            {
                if (hasNewerRebuildQueued)
                {
                    DisposeDataJobResources(ref pd);
                }
                else
                {
                    bool hadVoxelSnapshot = activeChunk.hasVoxelSnapshot;

                    if (!completedPostOverrideRefresh &&
                        TrySchedulePostCompletionOverrideRefresh(ref pd, activeChunk))
                    {
                        pendingDataJobs[i] = pd;
                        i++;
                        continue;
                    }

                    int resolvedVisualSubchunksPerRenderer = GetResolvedVisualSubchunksPerRenderer();
                    if (!activeChunk.HasInitializedSubchunks ||
                        activeChunk.visualSubchunksPerRenderer != resolvedVisualSubchunksPerRenderer)
                        activeChunk.InitializeSubchunks(ActiveWorldMaterials, resolvedVisualSubchunksPerRenderer);
                    else
                        activeChunk.UpdateWorldBounds();
                    ApplyChunkBiomeTint(activeChunk, pd.coord);
                    activeChunk.hasVoxelData = true;
                    activeChunk.hasDetailedGenerationData = activeChunk.requestedDetailedGeneration;
                    activeChunk.state = Chunk.ChunkState.MeshReady;
                    if (completedPostOverrideRefresh)
                        SyncCurrentBlockOverridesToVoxelSnapshot(pd.coord, pd.borderSize, activeChunk.voxelData);
                    activeChunk.hasVoxelSnapshot = true;
                    activeChunk.UpdateLightSnapshot(pd.light, pd.borderSize);
                    if (enableVoxelLighting)
                    {
                        SyncChunkBlockLightColumns(pd.coord, pd.light, pd.borderSize);
                        if (!hadVoxelSnapshot && ChunkHasBoundaryBlockLight(pd.light, pd.borderSize))
                            RequestNeighborChunkLightingRefresh(pd.coord);
                    }

                    pendingMeshBuildRequests.Add(pd);
                    pendingJobPrioritiesDirty = true;
                }
            }
            else
            {
                DisposeDataJobResources(ref pd);
            }

            pendingDataJobs[i] = pd;
            RemovePendingDataJobAtSwapBack(i);
            if (hasActiveChunk)
                RefreshChunkJobTracking(pd.coord, activeChunk);
        }
    }

    private void ProcessPendingMeshScheduleStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingMeshBuildRequests.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkMeshScheduleBudgetMS);
        int processed = 0;
        int perFrameLimit = Mathf.Max(1, maxMeshSchedulesPerFrame);
        int i = 0;

        while (i < pendingMeshBuildRequests.Count)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (processed >= perFrameLimit)
                break;

            PendingData pd = pendingMeshBuildRequests[i];
            bool hasActiveChunk = activeChunks.TryGetValue(pd.coord, out Chunk activeChunk);
            bool canScheduleMesh = hasActiveChunk &&
                                   activeChunk.generation == pd.expectedGen &&
                                   !HasQueuedChunkRebuild(pd.coord);

            if (canScheduleMesh)
                ScheduleSubchunkMeshJobs(ref pd, activeChunk);
            else
                DisposeDataJobResources(ref pd);

            pendingMeshBuildRequests[i] = pd;
            RemovePendingMeshBuildRequestAtSwapBack(i);
            processed++;

            if (hasActiveChunk)
                RefreshChunkJobTracking(pd.coord, activeChunk);
        }
    }

    private void ProcessCompletedMeshApplyStage(float pipelineStartTime, float pipelineBudgetSeconds)
    {
        if (pendingMeshes.Count == 0)
            return;

        float stageStartTime = Time.realtimeSinceStartup;
        float stageBudgetSeconds = GetBudgetSeconds(chunkMeshApplyBudgetMS);
        Camera priorityCamera = enableSmartMeshApplyPrioritization ? ResolveMeshApplyPriorityCamera() : null;
        bool hasPriorityCamera = priorityCamera != null;
        if (hasPriorityCamera)
            GeometryUtility.CalculateFrustumPlanes(priorityCamera, meshApplyPriorityFrustumPlanes);

        while (pendingMeshes.Count > 0)
        {
            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;
            if (meshesAppliedThisFrame >= maxMeshAppliesPerFrame)
                break;

            int selectedIndex = SelectNextPendingMeshApplyIndex(priorityCamera, hasPriorityCamera);
            if (selectedIndex < 0)
                break;

            var pm = pendingMeshes[selectedIndex];
            if (!pm.jobCompleted)
            {
                pm.handle.Complete();
                pm.jobCompleted = true;
                pendingMeshes[selectedIndex] = pm;
            }

            if (!HasPipelineAndStageBudgetRemaining(pipelineStartTime, pipelineBudgetSeconds, stageStartTime, stageBudgetSeconds))
                break;

            bool hasActiveChunk = activeChunks.TryGetValue(pm.coord, out Chunk activeChunk);
            bool canApplyMesh = hasActiveChunk &&
                                activeChunk.generation == pm.expectedGen &&
                                !HasQueuedChunkRebuild(pm.coord);
            if (canApplyMesh)
            {
                if (activeChunk.TryGetVisualSlice(pm.visualSliceIndex, out ChunkRenderSlice visualSlice))
                {
                    if (pm.lightingOnlyRebuild)
                    {
                        if (visualSlice.ApplyRelitVertexData(pm.vertices))
                            meshesAppliedThisFrame++;
                    }
                    else
                    {
                        bool updatedSectionVisibility = false;
                        int sliceMask = activeChunk.GetVisualSliceMask(pm.visualSliceIndex);
                        for (int subchunkIndex = 0; subchunkIndex < Chunk.SubchunksPerColumn; subchunkIndex++)
                        {
                            int subchunkBit = 1 << subchunkIndex;
                            if ((sliceMask & subchunkBit) == 0 || (pm.dirtySubchunkMask & subchunkBit) == 0)
                                continue;

                            MeshGenerator.SubchunkMeshRange range = pm.subchunkRanges[subchunkIndex];
                            activeChunk.SetSubchunkLightingOnlyRebuildSupport(
                                subchunkIndex,
                                range.supportsLightingOnlyRebuild != 0);

                            if (activeChunk.SetSubchunkVisibilityData(subchunkIndex, pm.subchunkVisibilityMasks[subchunkIndex]))
                                updatedSectionVisibility = true;

                            bool hasSolidColliderGeometry = activeChunk.HasSubchunkColliderOccupancy(subchunkIndex);
                            if (range.vertexCount > 0)
                            {
                                activeChunk.SetSubchunkMeshState(subchunkIndex, true, hasSolidColliderGeometry);
                                ApplyCachedSectionVisibility(pm.coord, subchunkIndex, activeChunk);

                                if (pm.buildColliders)
                                {
                                    if (hasSolidColliderGeometry && IsChunkInsideSimulationDistance(pm.coord))
                                    {
                                        if (!activeChunk.TryActivateCachedSubchunkColliders(subchunkIndex))
                                        {
                                            activeChunk.MarkSubchunkColliderDataDirty(subchunkIndex);
                                            EnqueueColliderBuild(pm.coord, pm.expectedGen, subchunkIndex);
                                        }
                                        else
                                            activeChunk.SetSubchunkColliderSystemEnabled(subchunkIndex, true);
                                    }
                                    else
                                        activeChunk.ClearSubchunkColliderData(subchunkIndex);
                                }
                            }

                            else
                            {
                                activeChunk.ClearSubchunkMesh(subchunkIndex);
                            }
                        }

                        visualSlice.ApplyMeshData(
                            pm.vertices,
                            pm.opaqueTriangles,
                            pm.transparentTriangles,
                            pm.billboardTriangles,
                            pm.waterTriangles,
                            pm.subchunkRanges,
                            activeChunk);
                        activeChunk.RefreshVisualSliceVisibility(pm.visualSliceIndex);
                        meshesAppliedThisFrame++;

                        if (updatedSectionVisibility)
                            InvalidateSectionOcclusionGraph();
                    }
                }
            }

            DisposePendingMesh(pm);
            RemovePendingMeshAtSwapBack(selectedIndex);
            if (hasActiveChunk)
            {
                RefreshChunkJobTracking(pm.coord, activeChunk);
            }
        }
    }

    #endregion

    #region Voxel Data Copy & Mesh Scheduling

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache)
    {
        ApplyCurrentBlockOverridesToChunkData(
            coord,
            blockTypes,
            solids,
            subchunkNonEmpty,
            heightCache,
            InferBorderSizeFromChunkArrays(blockTypes, heightCache),
            default);
    }

    private Chunk CreateChunkPoolEntry()
    {
        GameObject obj = Instantiate(chunkPrefab, Vector3.zero, Quaternion.identity, transform);
        obj.SetActive(false);

        Chunk chunk = obj.GetComponent<Chunk>();
        if (chunk != null && prewarmPooledChunkVisuals)
        {
            chunk.InitializeSubchunks(ActiveWorldMaterials, GetResolvedVisualSubchunksPerRenderer());
            chunk.ResetChunk();
        }

        return chunk;
    }

    private static NativeArray<byte> CreateFullyKnownVoxelMask(int length)
    {
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(length);
        for (int i = 0; i < knownVoxelData.Length; i++)
            knownVoxelData[i] = 1;

        return knownVoxelData;
    }

    private static NativeArray<byte> CreateKnownVoxelPlaceholder()
    {
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(1);
        knownVoxelData[0] = 1;
        return knownVoxelData;
    }

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize,
        NativeArray<byte> voxelSnapshot)
    {
        if (blockOverrides.Count == 0 ||
            !blockTypes.IsCreated ||
            !solids.IsCreated ||
            !subchunkNonEmpty.IsCreated ||
            !heightCache.IsCreated ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0)
        {
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        if (relevantTerrainOverridePositions.Count == 0)
            return;

        bool hasRelevantOverrides = false;
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            if (idx < 0 || idx >= blockTypes.Length)
                continue;

            blockTypes[idx] = (byte)overrideType;
            UpdateVoxelSnapshotCell(voxelSnapshot, chunkMinX, chunkMinZ, worldPos, overrideType);
            hasRelevantOverrides = true;
        }

        if (!hasRelevantOverrides)
            return;

        RefreshChunkDerivedData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize);
    }

    private bool TrySchedulePostCompletionOverrideRefresh(ref PendingData pd, Chunk chunk)
    {
        if (pd.postOverrideRefreshScheduled ||
            blockOverrides.Count == 0 ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0 ||
            !pd.blockTypes.IsCreated ||
            !pd.solids.IsCreated ||
            !pd.heightCache.IsCreated ||
            !pd.subchunkNonEmpty.IsCreated ||
            !pd.subchunkColliderOccupancy.IsCreated ||
            chunk == null ||
            !chunk.voxelData.IsCreated)
        {
            return false;
        }

        NativeArray<BlockEdit> currentOverrides = BuildFastRebuildOverrideArray(pd.coord, pd.borderSize);
        if (!currentOverrides.IsCreated || currentOverrides.Length == 0)
        {
            SafeDisposeNativeArray(ref currentOverrides);
            return false;
        }

        int voxelSizeX = Chunk.SizeX + 2 * pd.borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * pd.borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> dirtyColumns = MeshGenerator.RentByteBuffer(voxelSizeX * voxelSizeZ, true);

        var overrideRefreshJob = new PostApplyCurrentOverridesJob
        {
            overrides = currentOverrides,
            blockMappings = cachedNativeBlockMappings,
            blockTypes = pd.blockTypes,
            blockPlacementAxes = pd.blockPlacementAxes,
            solids = pd.solids,
            heightCache = pd.heightCache,
            subchunkNonEmpty = pd.subchunkNonEmpty,
            subchunkColliderOccupancy = pd.subchunkColliderOccupancy,
            dirtyColumns = dirtyColumns,
            chunkMinX = pd.coord.x * Chunk.SizeX,
            chunkMinZ = pd.coord.y * Chunk.SizeZ,
            borderSize = pd.borderSize,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            voxelPlaneSize = voxelPlaneSize
        };

        pd.handle = overrideRefreshJob.Schedule();
        pd.terrainHandle = pd.handle;
        pd.lightingHandle = pd.handle;
        pd.postCompletionOverrides = currentOverrides;
        pd.postCompletionDirtyColumns = dirtyColumns;
        pd.postOverrideRefreshScheduled = true;
        pd.terrainStageCompleted = false;
        pd.lightingStageCompleted = false;

        chunk.currentJob = pd.handle;
        chunk.jobScheduled = true;
        pendingJobPrioritiesDirty = true;
        return true;
    }

    private void SyncCurrentBlockOverridesToVoxelSnapshot(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> voxelSnapshot)
    {
        if (blockOverrides.Count == 0 || !voxelSnapshot.IsCreated)
            return;

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            UpdateVoxelSnapshotCell(voxelSnapshot, chunkMinX, chunkMinZ, worldPos, overrideType);
        }
    }

    private static void UpdateVoxelSnapshotCell(
        NativeArray<byte> voxelSnapshot,
        int chunkMinX,
        int chunkMinZ,
        Vector3Int worldPos,
        BlockType blockType)
    {
        if (!voxelSnapshot.IsCreated)
            return;

        int localX = worldPos.x - chunkMinX;
        int localZ = worldPos.z - chunkMinZ;
        int localY = worldPos.y;
        if (localX < 0 || localX >= Chunk.SizeX ||
            localZ < 0 || localZ >= Chunk.SizeZ ||
            localY < 0 || localY >= Chunk.SizeY)
        {
            return;
        }

        int snapshotIndex = localX + localZ * Chunk.SizeX + localY * Chunk.SizeX * Chunk.SizeZ;
        if (snapshotIndex < 0 || snapshotIndex >= voxelSnapshot.Length)
            return;

        voxelSnapshot[snapshotIndex] = (byte)blockType;
    }

    private static int InferBorderSizeFromChunkArrays(NativeArray<byte> blockTypes, NativeArray<int> heightCache)
    {
        if (heightCache.IsCreated && heightCache.Length > 0)
        {
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(heightCache.Length));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        if (blockTypes.IsCreated && blockTypes.Length > 0 && Chunk.SizeY > 0)
        {
            int paddedArea = blockTypes.Length / Chunk.SizeY;
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(paddedArea));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        return 0;
    }

    private void RefreshChunkDerivedData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int heightStride = voxelSizeX;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
            subchunkNonEmpty[s] = false;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    BlockType bt = (BlockType)blockTypes[idx];
                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid)
                        highestSolidY = y;

                    if (worldX >= chunkMinX &&
                        worldX < chunkMinX + Chunk.SizeX &&
                        worldZ >= chunkMinZ &&
                        worldZ < chunkMinZ + Chunk.SizeZ &&
                        bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }
    }

    private void ScheduleSubchunkMeshJobs(ref PendingData pd, Chunk activeChunk)
    {
        int borderSize = Mathf.Max(1, pd.borderSize);
        int dirtySubchunkMask = SanitizeDirtySubchunkMask(pd.dirtySubchunkMask);
        activeChunk.UpdateSubchunkColliderOccupancy(pd.subchunkColliderOccupancy, dirtySubchunkMask);
        MeshGenerator.ReturnUlongBuffer(ref pd.subchunkColliderOccupancy);
        suppressedGrassBillboardInt3Buffer.Clear();
        CollectSuppressedGrassBillboardsForChunk(pd.coord, suppressedGrassBillboardInt3Buffer);
        NativeArray<int3> nativeSuppressedBillboards = new NativeArray<int3>(suppressedGrassBillboardInt3Buffer.Count, Allocator.Persistent);
        for (int s = 0; s < suppressedGrassBillboardInt3Buffer.Count; s++)
            nativeSuppressedBillboards[s] = suppressedGrassBillboardInt3Buffer[s];

        int nonEmptySubchunkMask = 0;
        int affectedVisualSliceMask = 0;
        bool updatedEmptySectionVisibility = false;
        for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
        {
            if (pd.subchunkNonEmpty[sub])
                nonEmptySubchunkMask |= 1 << sub;

            if ((dirtySubchunkMask & (1 << sub)) == 0)
                continue;

            affectedVisualSliceMask |= 1 << activeChunk.GetVisualSliceIndexForSubchunk(sub);

            if (!pd.subchunkNonEmpty[sub])
            {
                activeChunk.ClearSubchunkMesh(sub);
                if (activeChunk.SetSubchunkVisibilityData(sub, SubchunkOcclusion.AllVisibleMask))
                    updatedEmptySectionVisibility = true;
                continue;
            }
        }

        if (updatedEmptySectionVisibility)
            InvalidateSectionOcclusionGraph();

        JobHandle combinedMeshHandle = default;
        bool hasScheduledMeshJobs = false;
        if (affectedVisualSliceMask != 0)
        {
            bool useDetailedGeneration = activeChunk.requestedDetailedGeneration;
            float effectiveAoStrength = enableAmbientOcclusion ? aoStrength : 0f;
            float leafFoliageSpawnChance = Mathf.Clamp01(treeLeafFoliageSpawnChance);
            float leafFoliageHeightMin = Mathf.Clamp(treeLeafFoliageHeightMin, 0.2f, 2f);
            float leafFoliageHeightMax = Mathf.Max(leafFoliageHeightMin, Mathf.Clamp(treeLeafFoliageHeightMax, 0.2f, 2f));
            float leafFoliageHalfWidthMin = Mathf.Clamp(treeLeafFoliageHalfWidthMin, 0.5f, 1f);
            float leafFoliageHalfWidthMax = Mathf.Max(leafFoliageHalfWidthMin, Mathf.Clamp(treeLeafFoliageHalfWidthMax, 0.5f, 1f));
            float leafFoliageBaseYOffsetMin = Mathf.Clamp(treeLeafFoliageBaseYOffsetMin, -0.2f, 0.4f);
            float leafFoliageBaseYOffsetMax = Mathf.Max(leafFoliageBaseYOffsetMin, Mathf.Clamp(treeLeafFoliageBaseYOffsetMax, -0.2f, 0.4f));
            float leafFoliageCenterJitter = Mathf.Clamp(treeLeafFoliageCenterJitter, 0f, 0.2f);
            float leafUltraHeight = Mathf.Clamp(treeLeafUltraBillboardHeight, 0.4f, 2.5f);
            float leafUltraHalfWidth = Mathf.Clamp(treeLeafUltraBillboardHalfWidth, 0.5f, 1.6f);
            float leafUltraBaseYOffset = Mathf.Clamp(treeLeafUltraBaseYOffset, -0.4f, 0.4f);
            float leafUltraCenterJitter = Mathf.Clamp(treeLeafUltraCenterJitter, 0f, 0.2f);
            float leafUltraRotationOffsetDegrees = Mathf.Clamp(treeLeafUltraRotationOffsetDegrees, 0f, 45f);
            float leafUltraRotationRandomDegrees = Mathf.Clamp(treeLeafUltraRotationRandomDegrees, 0f, 30f);
            float leafUltraFaceTiltDegrees = Mathf.Clamp(treeLeafUltraFaceTiltDegrees, 0f, 60f);
            float leafUltraFaceTiltRandomDegrees = Mathf.Clamp(treeLeafUltraFaceTiltRandomDegrees, 0f, 30f);

            int visualSliceCount = activeChunk.visualSlices != null
                ? activeChunk.visualSlices.Length
                : Chunk.GetVisualSliceCount(activeChunk.visualSubchunksPerRenderer);

            for (int sliceIndex = 0; sliceIndex < visualSliceCount; sliceIndex++)
            {
                int visualSliceBit = 1 << sliceIndex;
                if ((affectedVisualSliceMask & visualSliceBit) == 0)
                    continue;

                int scheduledSubchunkMask = activeChunk.GetVisualSliceMask(sliceIndex) & nonEmptySubchunkMask;
                if (scheduledSubchunkMask == 0)
                {
                    if (activeChunk.TryGetVisualSlice(sliceIndex, out ChunkRenderSlice emptySlice))
                        emptySlice.ClearMesh();
                    activeChunk.RefreshVisualSliceVisibility(sliceIndex);
                    continue;
                }

                MeshGenerator.ScheduleMeshJob(
                    pd.heightCache, pd.blockTypes, pd.blockPlacementAxes, pd.solids, pd.light, cachedNativeBlockMappings, cachedNativeBlockModelCuboids, nativeSuppressedBillboards,
                    pd.subchunkNonEmpty, pd.knownVoxelData, pd.useKnownVoxelData,
                    atlasTilesX, atlasTilesY, true, borderSize,
                    pd.coord.x, pd.coord.y,
                    scheduledSubchunkMask,
                    ShouldGenerateGrassBillboardsForChunk(useDetailedGeneration), grassBillboardChance, grassBillboardBlockType, grassBillboardHeight,
                    grassBillboardNoiseScale, grassBillboardJitter, cachedNativeVegetationBillboardRules, GetBiomeNoiseSettings(),
                    effectiveAoStrength, aoCurveExponent, aoMinLight, useFastBedrockStyleMeshing,
                    treeLeafQuality == TreeLeafQualityMode.High,
                    treeLeafQuality == TreeLeafQualityMode.Ultra,
                    leafFoliageSpawnChance, leafFoliageHeightMin, leafFoliageHeightMax,
                    leafFoliageHalfWidthMin, leafFoliageHalfWidthMax,
                    leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax,
                    leafFoliageCenterJitter,
                    leafUltraHeight, leafUltraHalfWidth, leafUltraBaseYOffset, leafUltraCenterJitter,
                    leafUltraRotationOffsetDegrees, leafUltraRotationRandomDegrees,
                    leafUltraFaceTiltDegrees, leafUltraFaceTiltRandomDegrees,
                    out JobHandle meshHandle,
                    out NativeList<MeshGenerator.PackedChunkVertex> vertices,
                    out NativeList<int> opaqueTriangles,
                    out NativeList<int> transparentTriangles,
                    out NativeList<int> billboardTriangles,
                    out NativeList<int> waterTriangles,
                    out NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
                    out NativeArray<ulong> subchunkVisibilityMasks
                );

                combinedMeshHandle = hasScheduledMeshJobs
                    ? JobHandle.CombineDependencies(combinedMeshHandle, meshHandle)
                    : meshHandle;
                hasScheduledMeshJobs = true;

                pendingMeshes.Add(new PendingMesh
                {
                    handle = meshHandle,
                    jobCompleted = false,
                    vertices = vertices,
                    opaqueTriangles = opaqueTriangles,
                    transparentTriangles = transparentTriangles,
                    billboardTriangles = billboardTriangles,
                    waterTriangles = waterTriangles,
                    coord = pd.coord,
                    expectedGen = pd.expectedGen,
                    parentChunk = activeChunk,
                    subchunkRanges = subchunkRanges,
                    subchunkVisibilityMasks = subchunkVisibilityMasks,
                    dirtySubchunkMask = scheduledSubchunkMask,
                    visualSliceIndex = sliceIndex,
                    heightCache = default,
                    blockTypes = default,
                    solids = default,
                    light = default,
                    suppressedBillboards = default,
                    buildColliders = pd.rebuildColliders
                });
                pendingJobPrioritiesDirty = true;
            }
        }

        var disposeSuppressedBillboardsJob = new MeshGenerator.DisposeSuppressedBillboardsJob
        {
            suppressedGrassBillboards = nativeSuppressedBillboards
        };
        JobHandle suppressedDisposeHandle = disposeSuppressedBillboardsJob.Schedule(combinedMeshHandle);

        QueueChunkDataBufferReturn(combinedMeshHandle, ref pd);
        activeChunk.currentJob = JobHandle.CombineDependencies(combinedMeshHandle, suppressedDisposeHandle);
    }

    private void CollectSuppressedGrassBillboardsForChunk(Vector2Int chunkCoord, List<int3> output)
    {
        if (output == null)
            return;

        output.Clear();
        if (!suppressedGrassBillboardsByChunk.TryGetValue(chunkCoord, out HashSet<Vector3Int> positions) || positions.Count == 0)
            return;

        foreach (Vector3Int pos in positions)
        {
            if (pos.y >= 0 && pos.y < Chunk.SizeY)
                output.Add(new int3(pos.x, pos.y, pos.z));
        }
    }

    private static Vector2Int GetChunkCoordFromWorldXZ(int worldX, int worldZ)
    {
        return new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );
    }

    private void IndexSuppressedGrassBillboard(Vector3Int pos)
    {
        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (!suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
            suppressedGrassBillboardsByChunk[coord] = set;
        }

        set.Add(pos);
    }

    private bool RemoveSuppressedGrassBillboard(Vector3Int pos, bool allowPermanentRemoval = false)
    {
        if (allowPermanentRemoval)
            permanentGrassBillboardSuppressions.Remove(pos);
        else if (permanentGrassBillboardSuppressions.Contains(pos))
            return false;

        if (!suppressedGrassBillboards.Remove(pos))
            return false;

        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set.Remove(pos);
            if (set.Count == 0)
                suppressedGrassBillboardsByChunk.Remove(coord);
        }

        return true;
    }

    private void InjectGlobalLightColumns(
        NativeArray<ushort> chunkLightData,
        int chunkMinX,
        int chunkMinZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (globalLightColumns.Count == 0) return;

        int minWX = chunkMinX - borderSize;
        int maxWX = chunkMinX + Chunk.SizeX + borderSize - 1;
        int minWZ = chunkMinZ - borderSize;
        int maxWZ = chunkMinZ + Chunk.SizeZ + borderSize - 1;

        int areaColumns = voxelSizeX * voxelSizeZ;

        // Sparse mode: iterate only existing global columns (better when emissive lights are sparse).
        if (globalLightColumns.Count < areaColumns)
        {
            foreach (var kv in globalLightColumns)
            {
                int wx = kv.Key.x;
                int wz = kv.Key.y;
                if (wx < minWX || wx > maxWX || wz < minWZ || wz > maxWZ) continue;

                ushort[] column = kv.Value;
                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
            return;
        }

        // Dense mode: bounded grid lookup (better when many lit columns exist).
        for (int wx = minWX; wx <= maxWX; wx++)
        {
            for (int wz = minWZ; wz <= maxWZ; wz++)
            {
                var key = new Vector2Int(wx, wz);
                if (!globalLightColumns.TryGetValue(key, out ushort[] column)) continue;

                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
        }
    }

    #endregion

    #region Requesting Chunks & Rebuilds

    private void ProcessRetiredChunksAwaitingRecycle()
    {
        for (int i = retiredChunksAwaitingRecycle.Count - 1; i >= 0; i--)
        {
            Chunk chunk = retiredChunksAwaitingRecycle[i];
            if (chunk == null)
            {
                retiredChunksAwaitingRecycle.RemoveAt(i);
                continue;
            }

            if (!TryRecycleChunkWithoutBlocking(chunk))
                continue;

            retiredChunksAwaitingRecycle.RemoveAt(i);
        }
    }

    private void RetireChunkWithoutBlocking(Chunk chunk)
    {
        if (chunk == null)
            return;

        if (TryRecycleChunkWithoutBlocking(chunk))
            return;

        if (chunk.pendingRecycle)
            return;

        chunk.pendingRecycle = true;
        chunk.state = Chunk.ChunkState.Inactive;
        if (chunk.gameObject.activeSelf)
            chunk.gameObject.SetActive(false);
        retiredChunksAwaitingRecycle.Add(chunk);
    }

    private bool TryRecycleChunkWithoutBlocking(Chunk chunk)
    {
        if (chunk == null)
            return false;

        if (HasPendingJobReferencesForChunk(chunk))
            return false;

        if (chunk.jobScheduled && !IsChunkJobCompletedWithoutBlocking(chunk))
            return false;

        chunk.ResetChunk();
        chunkPool.Enqueue(chunk);
        return true;
    }

    private bool HasPendingJobReferencesForChunk(Chunk chunk)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (ReferenceEquals(pendingMeshes[i].parentChunk, chunk))
                return true;
        }

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (ReferenceEquals(pendingDataJobs[i].chunk, chunk))
                return true;
        }

        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            if (ReferenceEquals(pendingMeshBuildRequests[i].chunk, chunk))
                return true;
        }

        return false;
    }

    private static bool IsChunkJobCompletedWithoutBlocking(Chunk chunk)
    {
        if (chunk == null || !chunk.jobScheduled)
            return true;

        try
        {
            return chunk.currentJob.IsCompleted;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void UpdateChunks()
    {
        if (player == null) return;

        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();

        if (currentChunkCoord != _lastChunkCoord)
        {
            _lastChunkCoord = currentChunkCoord;
            lastPlayerChunkCoordChangeTime = Time.time;
            _tempNeededCoords.Clear();
            bool activeSectionSetChanged = false;

            for (int x = -renderDistance; x <= renderDistance; x++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector2Int coord = new Vector2Int(currentChunkCoord.x + x, currentChunkCoord.y + z);
                    if (!IsCoordInsideRenderDistance(coord, currentChunkCoord))
                        continue;

                    _tempNeededCoords.Add(coord);
                }
            }

            // A. Remover chunks distantes
            _tempToRemove.Clear();
            foreach (var kv in activeChunks)
            {
                if (!_tempNeededCoords.Contains(kv.Key))
                    _tempToRemove.Add(kv.Key);
            }

            for (int i = 0; i < _tempToRemove.Count; i++)
            {
                Vector2Int coord = _tempToRemove[i];
                if (activeChunks.TryGetValue(coord, out Chunk chunk))
                {
                    InvalidateChunkBiomeTintCache(coord);
                    activeChunks.Remove(coord);
                    RetireChunkWithoutBlocking(chunk);
                    activeSectionSetChanged = true;

                    RemoveHighBuildMesh(coord);
                }
            }

            if (activeSectionSetChanged)
                InvalidateSectionOcclusionGraph();

            // B. Limpar pendentes desnecessÃ¡rios
            for (int i = pendingChunks.Count - 1; i >= 0; i--)
            {
                if (!_tempNeededCoords.Contains(pendingChunks[i].coord))
                    pendingChunks.RemoveAt(i);
            }

            // C. Encontrar novos chunks para gerar
            foreach (Vector2Int coord in _tempNeededCoords)
            {
                if (activeChunks.ContainsKey(coord)) continue;
                if (IsChunkJobPending(coord)) continue;

                float distSq = GetChunkDistanceSqToPlayer(coord);
                pendingChunks.Add((coord, distSq));
            }

            // D. Reordenar fila por distÃ¢ncia
            foreach (var kv in activeChunks)
            {
                Chunk activeChunk = kv.Value;
                if (activeChunk == null)
                    continue;

                if (!ShouldChunkUseDetailedGeneration(kv.Key, currentChunkCoord))
                    continue;

                if (activeChunk.requestedDetailedGeneration || activeChunk.hasDetailedGenerationData)
                    continue;

                EnqueueChunkDetailPromotion(kv.Key);
            }

            RefreshPendingChunkPriorities();
        }

        // O scheduler central consome pendingChunks com orcamento proprio em ProcessChunkQueue.
    }

    private void RequestChunk(Vector2Int coord)
    {
        if (chunkPool.Count == 0)
            ProcessRetiredChunksAwaitingRecycle();

        // Reuse or create chunk
        Chunk chunk;
        if (chunkPool.Count > 0)
        {
            chunk = chunkPool.Dequeue();
            try { chunk.ResetChunk(); } catch { }
            chunk.pendingRecycle = false;
            chunk.jobScheduled = false;
            chunk.hasVoxelData = false;
            chunk.currentJob = default;
            chunk.state = Chunk.ChunkState.Inactive;
        }
        else
        {
            chunk = CreateChunkPoolEntry();
        }

        Vector3 pos = new Vector3(coord.x * Chunk.SizeX, 0, coord.y * Chunk.SizeZ);
        chunk.transform.position = pos;
        chunk.UpdateWorldBounds(); // garante bounds atualizado
        chunk.SetCoord(coord);

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;
        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();
        bool useDetailedGeneration = ShouldChunkUseDetailedGeneration(coord, currentChunkCoord);
        chunk.requestedDetailedGeneration = useDetailedGeneration;

        if (!chunk.voxelData.IsCreated)
        {
            chunk.voxelData = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);
            chunk.hasVoxelData = false;
        }

        activeChunks.Add(coord, chunk);
        RequestHighBuildMeshRebuild(coord);

        // Coleta overrides do jogador antes da geracao para que o chunk ja nasca consistente.
        requestChunkEditsBuffer.Clear();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        PrimeNearbyOverrideLightSources(coord, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, requestChunkEditsBuffer);

        NativeArray<BlockEdit> nativeEdits;
        if (requestChunkEditsBuffer.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(requestChunkEditsBuffer.Count, Allocator.Persistent);
            for (int i = 0; i < requestChunkEditsBuffer.Count; i++)
                nativeEdits[i] = requestChunkEditsBuffer[i];
        }
        else
        {
            nativeEdits = new NativeArray<BlockEdit>(0, Allocator.Persistent);
        }

        int treeMargin = GetMaxTreeMarginForGeneration();
        int detailBorderSize = GetDetailedGenerationBorderSize();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        EnsureNativeGenerationCaches();

        // InjeÃ§Ã£o da luz global
        // Light injection corrected for rebuild (uses borderSize)
        // O volume de luz precisa do mesmo recorte padded do chunk para manter costura
        // com vizinhos e considerar colunas globais ja conhecidas.
        int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<ushort> chunkLightData = default;
        if (enableVoxelLighting)
        {
            chunkLightData = MeshGenerator.RentUshortBuffer(voxelSizeX * Chunk.SizeY * voxelSizeZ, true);
            if (globalLightColumns.Count > 0)
                InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }
        if (chunk.jobScheduled)
        {
            chunk.CompleteTrackedJob();
            chunk.jobScheduled = false;
            chunk.currentJob = default;
        }

        // Agendamento do data job
        // A partir daqui a geracao entra na pipeline Burst/Jobs:
        // heightmap, superficie, cavernas, minerios, agua, arvores, edits e iluminacao.
        MeshGenerator.ScheduleDataJob(
            coord, cachedNativeNoiseLayers, cachedNativeBlockMappings, cachedNativeEffectiveLightOpacityByBlock, cachedNativeLightEmissionByBlock,
            baseHeight, offsetX, offsetZ, seaLevel, enableWater,
            GetBiomeNoiseSettings(),
            GetTerrainDensitySettings(),
            seed,
            nativeEdits, treeMargin, dataBorderSize, lightBorderSize, detailBorderSize,
            GetMaxTreeRadiusForGeneration(), CliffTreshold, enableTrees,
            cachedNativeOreSettings,
            cachedNativeTreeSpawnRules,
            GetSpaghettiCaveSettingsForChunk(useDetailedGeneration),
            enableVoxelLighting,
            enableHorizontalSkylight,
            horizontalSkylightStepLoss,
            chunkLightData,
            chunk.voxelData,
            out JobHandle dataHandle,
            out JobHandle terrainDataHandle,
            out JobHandle lightingHandle,
            out NativeArray<int> heightCache,
            out NativeArray<byte> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<ushort> light,
            out NativeArray<ushort> blockEmissionData,
            out NativeArray<byte> lightOpacityData,
            out NativeArray<bool> subchunkNonEmpty,
            out NativeArray<ulong> subchunkColliderOccupancy,
            out MeshGenerator.DataJobTempBuffers dataJobTempBuffers
        );
        NativeArray<byte> knownVoxelData = CreateKnownVoxelPlaceholder();
        int dataVoxelSizeX = Chunk.SizeX + 2 * dataBorderSize;
        int dataVoxelSizeZ = Chunk.SizeZ + 2 * dataBorderSize;
        int dataVoxelPlaneSize = dataVoxelSizeX * Chunk.SizeY;
        NativeArray<byte> blockPlacementAxes = CreateDefaultPlacementAxisArray(blockTypes.Length);
        ApplyPlacementAxesFromBlockEdits(
            nativeEdits,
            blockPlacementAxes,
            chunkMinX,
            chunkMinZ,
            dataBorderSize,
            dataVoxelSizeX,
            dataVoxelSizeZ,
            dataVoxelPlaneSize);

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            terrainHandle = terrainDataHandle,
            lightingHandle = lightingHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = false,
            solids = solids,
            light = light,
            borderSize = dataBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            blockEmissionData = blockEmissionData,
            lightOpacityData = lightOpacityData,
            edits = nativeEdits,
            fastRebuildSnapshotVoxelData = default,
            fastRebuildSnapshotLoadedChunks = default,
            fastRebuildOverrides = default,
            tempBuffers = dataJobTempBuffers,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = GetFullSubchunkMask(),
            rebuildColliders = enableBlockColliders,
            terrainStageCompleted = false,
            lightingStageCompleted = false
        });
        pendingJobPrioritiesDirty = true;

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
        chunk.gameObject.SetActive(true);
    }

    private bool TryScheduleFastChunkRebuild(Vector2Int coord, Chunk chunk, int expectedGen, int dirtySubchunkMask, bool rebuildColliders)
    {
        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return false;
        if (!CanChunkProvideVoxelSnapshot(chunk))
            return false;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return false;

        int copyBorderSize = GetMeshNeighborPadding();
        int lightBorderSize = GetLightSmoothingBorderSize();
        int copyVoxelSizeX = Chunk.SizeX + 2 * copyBorderSize;
        int copyVoxelSizeZ = Chunk.SizeZ + 2 * copyBorderSize;
        int copyVoxelPlaneSize = copyVoxelSizeX * Chunk.SizeY;
        int copyTotalVoxels = copyVoxelSizeX * Chunk.SizeY * copyVoxelSizeZ;
        int copyTotalHeightPoints = copyVoxelSizeX * copyVoxelSizeZ;

        int lightVoxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int lightVoxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int lightVoxelPlaneSize = lightVoxelSizeX * Chunk.SizeY;
        int lightTotalVoxels = lightVoxelSizeX * Chunk.SizeY * lightVoxelSizeZ;

        int maxSnapshotBorder = Mathf.Max(copyBorderSize, lightBorderSize);
        int snapshotChunkRadius = Mathf.Max(1, Mathf.CeilToInt(maxSnapshotBorder / (float)Chunk.SizeX));
        int snapshotChunkDiameter = snapshotChunkRadius * 2 + 1;
        int snapshotChunkCount = snapshotChunkDiameter * snapshotChunkDiameter;

        EnsureNativeGenerationCaches();

        NativeArray<int> heightCache = MeshGenerator.RentIntBuffer(copyTotalHeightPoints);
        NativeArray<byte> blockTypes = MeshGenerator.RentByteBuffer(copyTotalVoxels);
        NativeArray<byte> blockPlacementAxes = CreateDefaultPlacementAxisArray(copyTotalVoxels);
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(copyTotalVoxels);
        NativeArray<bool> solids = MeshGenerator.RentBoolBuffer(copyTotalVoxels);
        NativeArray<ushort> light = MeshGenerator.RentUshortBuffer(copyTotalVoxels);
        NativeArray<bool> subchunkNonEmpty = MeshGenerator.RentBoolBuffer(Chunk.SubchunksPerColumn);
        NativeArray<ulong> subchunkColliderOccupancy = MeshGenerator.RentUlongBuffer(Chunk.SubchunksPerColumn * Chunk.ColliderOccupancyWordsPerSubchunk);
        NativeArray<byte> lightOpacityData = default;
        NativeArray<ushort> blockLightData = default;
        NativeArray<ushort> blockEmissionData = default;
        NativeArray<byte> snapshotVoxelData = MeshGenerator.RentByteBuffer(snapshotChunkCount * FastRebuildChunkVoxelCount);
        NativeArray<byte> snapshotLoadedChunks = MeshGenerator.RentByteBuffer(snapshotChunkCount);
        NativeArray<BlockEdit> nativeOverrides = BuildFastRebuildOverrideArray(coord, maxSnapshotBorder);

        CaptureFastRebuildSnapshot(coord, snapshotChunkRadius, snapshotVoxelData, snapshotLoadedChunks);

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        ApplyPlacementAxesFromBlockEdits(
            nativeOverrides,
            blockPlacementAxes,
            chunkMinX,
            chunkMinZ,
            copyBorderSize,
            copyVoxelSizeX,
            copyVoxelSizeZ,
            copyVoxelPlaneSize);
        if (enableVoxelLighting)
        {
            lightOpacityData = MeshGenerator.RentByteBuffer(lightTotalVoxels);
            blockLightData = MeshGenerator.RentUshortBuffer(lightTotalVoxels, true);
            blockEmissionData = MeshGenerator.RentUshortBuffer(lightTotalVoxels, true);
            if (globalLightColumns.Count > 0)
                InjectGlobalLightColumns(blockLightData, chunkMinX, chunkMinZ, lightBorderSize, lightVoxelSizeX, lightVoxelSizeZ, lightVoxelPlaneSize);
        }

        var copyPopulateJob = new FastRebuildPopulateBlocksJob
        {
            snapshotVoxelData = snapshotVoxelData,
            snapshotLoadedChunks = snapshotLoadedChunks,
            blockTypes = blockTypes,
            knownVoxelData = knownVoxelData,
            disableWater = !enableWater,
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelPlaneSize = copyVoxelPlaneSize,
            snapshotChunkRadius = snapshotChunkRadius,
            snapshotChunkDiameter = snapshotChunkDiameter
        };
        JobHandle copyPopulateHandle = copyPopulateJob.Schedule(copyTotalVoxels, 128);

        JobHandle copyOverrideHandle = copyPopulateHandle;
        if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
        {
            var copyOverrideJob = new FastRebuildApplyBlockOverridesJob
            {
                overrides = nativeOverrides,
                blockTypes = blockTypes,
                knownVoxelData = knownVoxelData,
                chunkMinX = chunkMinX,
                chunkMinZ = chunkMinZ,
                borderSize = copyBorderSize,
                voxelSizeX = copyVoxelSizeX,
                voxelSizeZ = copyVoxelSizeZ,
                voxelPlaneSize = copyVoxelPlaneSize
            };
            copyOverrideHandle = copyOverrideJob.Schedule(copyPopulateHandle);
        }

        var deriveDataJob = new FastRebuildDerivedDataJob
        {
            blockTypes = blockTypes,
            blockMappings = cachedNativeBlockMappings,
            solids = solids,
            heightCache = heightCache,
            subchunkNonEmpty = subchunkNonEmpty,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelSizeZ = copyVoxelSizeZ,
            voxelPlaneSize = copyVoxelPlaneSize
        };
        JobHandle deriveDataHandle = deriveDataJob.Schedule(copyOverrideHandle);

        JobHandle visualDataHandle;
        if (enableVoxelLighting)
        {
            var opacityPopulateJob = new FastRebuildPopulateOpacityJob
            {
                snapshotVoxelData = snapshotVoxelData,
                snapshotLoadedChunks = snapshotLoadedChunks,
                effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                opacity = lightOpacityData,
                disableWater = !enableWater,
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                snapshotChunkRadius = snapshotChunkRadius,
                snapshotChunkDiameter = snapshotChunkDiameter
            };
            JobHandle opacityPopulateHandle = opacityPopulateJob.Schedule(lightTotalVoxels, 128);

            JobHandle opacityOverrideHandle = opacityPopulateHandle;
            if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
            {
                var opacityOverrideJob = new FastRebuildApplyOpacityOverridesJob
                {
                    overrides = nativeOverrides,
                    effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                    opacity = lightOpacityData,
                    chunkMinX = chunkMinX,
                    chunkMinZ = chunkMinZ,
                    borderSize = lightBorderSize,
                    voxelSizeX = lightVoxelSizeX,
                    voxelSizeZ = lightVoxelSizeZ,
                    voxelPlaneSize = lightVoxelPlaneSize
                };
                opacityOverrideHandle = opacityOverrideJob.Schedule(opacityPopulateHandle);
            }

            var emissionPopulateJob = new FastRebuildPopulateEmissionJob
            {
                snapshotVoxelData = snapshotVoxelData,
                snapshotLoadedChunks = snapshotLoadedChunks,
                lightEmissionByBlock = cachedNativeLightEmissionByBlock,
                blockEmission = blockEmissionData,
                disableWater = !enableWater,
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                snapshotChunkRadius = snapshotChunkRadius,
                snapshotChunkDiameter = snapshotChunkDiameter
            };
            JobHandle emissionPopulateHandle = emissionPopulateJob.Schedule(lightTotalVoxels, 128);

            JobHandle emissionOverrideHandle = emissionPopulateHandle;
            if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
            {
                var emissionOverrideJob = new FastRebuildApplyEmissionOverridesJob
                {
                    overrides = nativeOverrides,
                    lightEmissionByBlock = cachedNativeLightEmissionByBlock,
                    blockEmission = blockEmissionData,
                    chunkMinX = chunkMinX,
                    chunkMinZ = chunkMinZ,
                    borderSize = lightBorderSize,
                    voxelSizeX = lightVoxelSizeX,
                    voxelSizeZ = lightVoxelSizeZ,
                    voxelPlaneSize = lightVoxelPlaneSize
                };
                emissionOverrideHandle = emissionOverrideJob.Schedule(emissionPopulateHandle);
            }

            var lightJob = new ChunkLighting.CroppedChunkLightingJob
            {
                opacity = lightOpacityData,
                light = light,
                blockLightData = blockLightData,
                blockEmissionData = blockEmissionData,
                enableHorizontalSkylight = enableHorizontalSkylight,
                horizontalSkylightStepLoss = horizontalSkylightStepLoss,
                inputVoxelSizeX = lightVoxelSizeX,
                inputVoxelSizeZ = lightVoxelSizeZ,
                inputTotalVoxels = lightTotalVoxels,
                inputVoxelPlaneSize = lightVoxelPlaneSize,
                outputVoxelSizeX = copyVoxelSizeX,
                outputVoxelSizeZ = copyVoxelSizeZ,
                outputVoxelPlaneSize = copyVoxelPlaneSize,
                outputOffsetX = lightBorderSize - copyBorderSize,
                outputOffsetZ = lightBorderSize - copyBorderSize,
                SizeY = Chunk.SizeY,
            };

            visualDataHandle = lightJob.Schedule(JobHandle.CombineDependencies(deriveDataHandle, opacityOverrideHandle, emissionOverrideHandle));
        }
        else
        {
            ushort fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            visualDataHandle = deriveDataHandle;
        }

        pendingDataJobs.Add(new PendingData
        {
            handle = visualDataHandle,
            terrainHandle = deriveDataHandle,
            lightingHandle = visualDataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = true,
            solids = solids,
            light = light,
            borderSize = copyBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = blockLightData,
            blockEmissionData = blockEmissionData,
            lightOpacityData = lightOpacityData,
            edits = default,
            fastRebuildSnapshotVoxelData = snapshotVoxelData,
            fastRebuildSnapshotLoadedChunks = snapshotLoadedChunks,
            fastRebuildOverrides = nativeOverrides,
            tempBuffers = default,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders,
            terrainStageCompleted = false,
            lightingStageCompleted = false
        });
        pendingJobPrioritiesDirty = true;

        chunk.currentJob = visualDataHandle;
        chunk.jobScheduled = true;
        chunk.state = Chunk.ChunkState.MeshReady;
        return true;
    }

    private void CaptureFastRebuildSnapshot(
        Vector2Int coord,
        int chunkRadius,
        NativeArray<byte> snapshotVoxelData,
        NativeArray<byte> snapshotLoadedChunks)
    {
        if (!snapshotVoxelData.IsCreated || !snapshotLoadedChunks.IsCreated)
            return;

        int slot = 0;
        for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++, slot++)
            {
                Vector2Int sourceCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                bool isLoaded = activeChunks.TryGetValue(sourceCoord, out Chunk sourceChunk) &&
                                CanChunkProvideVoxelSnapshot(sourceChunk);

                snapshotLoadedChunks[slot] = isLoaded ? (byte)1 : (byte)0;
                if (!isLoaded)
                    continue;

                NativeArray<byte>.Copy(
                    sourceChunk.voxelData,
                    0,
                    snapshotVoxelData,
                    slot * FastRebuildChunkVoxelCount,
                    FastRebuildChunkVoxelCount);
            }
        }
    }

    private NativeArray<BlockEdit> BuildFastRebuildOverrideArray(Vector2Int coord, int borderSize)
    {
        if (blockOverrides.Count == 0)
            return new NativeArray<BlockEdit>(0, Allocator.Persistent);

        fastRebuildOverrideEditsBuffer.Clear();
        AppendRelevantBlockEdits(coord, borderSize, fastRebuildOverrideEditsBuffer);
        NativeArray<BlockEdit> nativeOverrides = new NativeArray<BlockEdit>(fastRebuildOverrideEditsBuffer.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < fastRebuildOverrideEditsBuffer.Count; i++)
            nativeOverrides[i] = fastRebuildOverrideEditsBuffer[i];

        return nativeOverrides;
    }

    private void FillFastRebuildArraysFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int heightStride = voxelSizeX;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
            subchunkNonEmpty[s] = false;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = ResolveWaterStateForDebug((BlockType)srcChunk.voxelData[srcIdx]);
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    blockTypes[idx] = (byte)bt;

                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid) highestSolidY = y;

                    if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }

        ApplyCurrentBlockOverridesToChunkData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize, default);
    }

    private void FillFastRebuildLightOpacityFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (!opacity.IsCreated || blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = ResolveWaterStateForDebug((BlockType)srcChunk.voxelData[srcIdx]);
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    opacity[idx] = ChunkLighting.GetEffectiveOpacity(mappings[(int)bt]);
                }
            }
        }

        ApplyCurrentBlockOverridesToLightOpacity(coord, borderSize, opacity);
    }

    private void ApplyCurrentBlockOverridesToLightOpacity(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (blockOverrides.Count == 0 ||
            !opacity.IsCreated ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0)
        {
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            opacity[idx] = ChunkLighting.GetEffectiveOpacity(blockData.mappings[(int)overrideType]);
        }
    }

    private bool TryResolveLoadedColumn(int worldX, int worldZ, out Chunk chunk, out int localX, out int localZ)
    {
        Vector2Int coord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(coord, out chunk) && CanChunkProvideVoxelSnapshot(chunk))
        {
            localX = worldX - coord.x * Chunk.SizeX;
            localZ = worldZ - coord.y * Chunk.SizeZ;

            if (localX >= 0 && localX < Chunk.SizeX && localZ >= 0 && localZ < Chunk.SizeZ)
                return true;
        }

        chunk = null;
        localX = 0;
        localZ = 0;
        return false;
    }




    #endregion


}




