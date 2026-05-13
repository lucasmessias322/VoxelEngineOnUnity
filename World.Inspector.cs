using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Inspector Fields - World Setup

    [Header("General")]
    public Transform player;
    public GameObject chunkPrefab;
    public int renderDistance = 4;
    [Tooltip("Raio em chunks usado pelos sistemas de simulacao. Limitado automaticamente pela renderDistance.")]
    [Min(0)]
    public int simulationDistance = 4;
    [Tooltip("Raio em chunks usado apenas para gerar e ativar colliders dos chunks. Limitado automaticamente pela renderDistance.")]
    [Min(0)]
    public int colliderDistance = 3;
    public int poolSize = 200;
    [Tooltip("Aumenta automaticamente o pool minimo de chunks com base no renderDistance para evitar Instantiate/AddComponent durante o gameplay.")]
    public bool autoSizeChunkPool = true;
    [Tooltip("Raio extra usado no calculo automatico do pool para cobrir movimento rapido e chunks aguardando recycle.")]
    [Min(0)]
    public int chunkPoolExtraRadius = 2;
    [Tooltip("Buffer extra de entradas do pool acima da cobertura minima calculada.")]
    [Min(0)]
    public int chunkPoolSafetyBuffer = 24;
    [Tooltip("Orcamento em ms para crescer o pool aos poucos durante o Update quando ele ficar abaixo do alvo.")]
    [Min(0f)]
    public float chunkPoolWarmupBudgetMS = 0.35f;
    [Tooltip("Quantidade maxima de novas entradas do pool criadas por frame no warmup incremental.")]
    [Min(1)]
    public int maxChunkPoolEntriesCreatedPerFrame = 1;
    [Tooltip("Quantidade maxima de criacoes emergenciais permitidas no mesmo frame se o pool secar completamente.")]
    [Min(0)]
    public int maxEmergencyChunkPoolCreatesPerFrame = 1;
    [Tooltip("Agrupa varias secoes 16x16x16 em um unico MeshRenderer. Valores maiores reduzem batches, mas deixam o culling vertical menos granular.")]
    [Min(1)]
    public int visualSubchunksPerRenderer = 4;
    [Tooltip("Cria subchunks logicos, render slices e meshes do pool no Start para evitar picos quando novas areas entram em cena.")]
    public bool prewarmPooledChunkVisuals = true;
    [Tooltip("Prealoca BoxColliders nos primeiros chunks do pool para reduzir spikes quando os colliders de subchunk aparecem pela primeira vez.")]
    public bool prewarmPooledChunkColliders = true;
    [Tooltip("Quantidade de BoxColliders prealocados por subchunk nos chunks prewarmed do pool. Valores baixos reduzem spikes sem inflar muita memoria.")]
    [Min(0)]
    public int prewarmColliderBoxesPerSubchunk = 2;
    [Tooltip("Quantidade maxima de chunks do pool que recebem prewarm de colliders. 0 usa automaticamente a cobertura do colliderDistance atual.")]
    [Min(0)]
    public int prewarmColliderChunkCount = 0;

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
    [Header("Terrain Mode")]
    [Tooltip("Normal usa relevo procedural. Flat gera um mundo plano estilo Minecraft com altura fixa.")]
    public WorldTerrainMode terrainMode = WorldTerrainMode.Normal;
    [Tooltip("Altura Y do bloco de superficie no modo Flat. Se ficar vazio/zerado, usa baseHeight ou 64.")]
    [Min(3)]
    public int flatWorldHeight = 64;
    [Tooltip("Bioma usado em todo o mundo quando o modo de terreno esta em Flat.")]
    public BiomeType flatWorldBiome = BiomeType.Meadow;
    [SerializeField, HideInInspector] private bool flatWorldBiomeInitialized;
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
    [Tooltip("Quantidade maxima de chunks aposentados que podem ser reciclados por frame no Update. Valores baixos suavizam faxinas de pool apos caminhada longa.")]
    [Min(1)]
    public int maxRetiredChunkRecyclesPerFrame = 2;
    [Tooltip("Quantidade maxima de retornos de buffers de chunk processados por frame. Reduz picos quando muitos meshes terminam quase juntos.")]
    [Min(1)]
    public int maxChunkDataBufferReturnsPerFrame = 4;
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
    [Tooltip("Quando ativo, chunks alem deste raio usam visual simplificado. Billboards de grama continuam reservados para chunks detalhados; cavernas spaghetti nos voxels podem ser mantidas pela opcao abaixo para acelerar a promocao.")]
    public bool enableChunkDetailLod = true;
    [Tooltip("Raio, em chunks, onde a geracao usa todos os detalhes. Fora dele, chunks novos usam geracao simplificada.")]
    [Min(0)]
    public int chunkDetailLodDistance = 10;
    [Tooltip("Quando ativo, chunks LOD distantes ainda geram cavernas spaghetti no voxel data. Isso reduz o custo da promocao para chunk detalhado, mas aumenta o custo inicial dos chunks distantes.")]
    public bool generateSpaghettiCavesInLodChunks = true;
    [Tooltip("Raio mais proximo que sempre tenta nascer detalhado, mesmo durante movimento e backlog de geracao.")]
    [Min(0)]
    public int chunkImmediateDetailDistance = 4;
    [Tooltip("Tempo minimo sem trocar de chunk antes de promover chunks simplificados para detalhados.")]
    [Min(0f)]
    public float chunkDetailPromotionDelaySeconds = 0.25f;
    [Tooltip("Quantidade maxima de chunks simplificados promovidos para detalhados por frame.")]
    [Min(1)]
    public int maxChunkDetailPromotionsPerFrame = 1;
    [Tooltip("Velocidade horizontal maxima do player para permitir promocoes de LOD. Use 0 para ignorar movimento real e considerar apenas a troca de chunk.")]
    [Min(0f)]
    public float chunkDetailPromotionMaxPlayerSpeed = 0.4f;
    [Tooltip("Intervalo minimo entre duas promocoes de chunk LOD. Ajuda a espalhar o custo quando o player para de andar.")]
    [Min(0f)]
    public float chunkDetailPromotionIntervalSeconds = 0.08f;
    [Tooltip("Quando ativo, promocoes de chunk LOD priorizam chunks dentro do frustum e na frente da camera do player.")]
    public bool enableChunkDetailPromotionCameraPrioritization = true;
    [Tooltip("Quando o backlog de promocoes fica alto, chunks fora da visao da camera ficam na fila ate os chunks visiveis serem atendidos primeiro.")]
    public bool deferOutOfViewChunkDetailPromotions = true;
    [Tooltip("Quantidade de promocoes enfileiradas a partir da qual chunks fora da visao passam a esperar os visiveis. 0 desativa esse atraso.")]
    [Min(0)]
    public int chunkDetailPromotionOutOfViewBacklogThreshold = 12;
    [Tooltip("Limite de candidatos avaliados por frame ao priorizar promocoes de chunk LOD. 0 avalia toda a fila.")]
    [Min(0)]
    public int chunkDetailPromotionPriorityScanLimit = 0;

    #endregion

}
