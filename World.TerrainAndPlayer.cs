using UnityEngine;

public partial class World
{
    public event System.Action<Vector3Int, BlockType, BlockType> BlockChanged;

    [Header("Leaf Decay")]
    [SerializeField] private bool enableLeafDecay = true;
    [SerializeField, Min(1)] private int leafDecaySupportDistance = 4;
    [SerializeField, Min(1)] private int leafDecayChecksPerFrame = 8;
    [SerializeField, Min(1)] private int leafDecayQueueScansPerFrame = 32;
    [SerializeField, Min(1)] private int leafDecaySupportScansPerFrame = 1;
    [SerializeField, Min(0f)] private float leafDecayTimeBudgetMS = 0.5f;
    [SerializeField, Min(0.05f)] private float leafDecayStepInterval = 0.15f;
    [SerializeField, Min(0f)] private float leafDecayGraceSeconds = 1.2f;
    [SerializeField, Min(0f)] private float leafDecayRebuildDelaySeconds = 0.06f;
    [SerializeField, Min(0.02f)] private float leafDecayVisualFlushIntervalSeconds = 0.25f;
    [SerializeField] private bool leafDecayRefreshBlockLighting = false;
    [SerializeField] private bool leafDecayRebuildColliders = false;
    [Tooltip("Chance de uma folha de oak dropar oakTreeSapling. 20 = Minecraft vanilla (1/20). Valores maiores deixam mais raro.")]
    [SerializeField, Min(1)] private int oakLeafSaplingDropOneIn = 40;

    [Header("Support Dependent Blocks")]
    [SerializeField, Min(1)] private int supportDependentBlockChecksPerFrame = 24;
    [SerializeField, Min(16)] private int supportDependencySearchLimit = 256;

    [Header("Fluid Simulation")]
    [SerializeField] private bool enableWaterSimulation = true;
    [SerializeField, Min(1)] private int waterUpdatesPerFrame = 16;
    [SerializeField, Min(0f)] private float waterUpdateTimeBudgetMS = 1f;
    [SerializeField, Min(0.01f)] private float waterTickInterval = 0.25f;
    [SerializeField, Min(1)] private int waterSlopeSearchDistance = 4;

    [Header("Electricity")]
    [SerializeField] private bool enableElectricitySystem = true;
    [SerializeField, Min(0.05f)] private float electricityTickInterval = 0.25f;
    [SerializeField, Min(0f)] private float solarPanelEnergyPerSecond = 24f;
    [SerializeField, Min(0f)] private float windMillEnergyPerSecond = 18f;
    [SerializeField, Min(0f)] private float steamEngineEnergyPerSecond = 36f;
    [SerializeField, Min(0.1f)] private float steamEngineCoalBurnDuration = 30f;
    [SerializeField, Range(0f, 1f)] private float steamEngineIdleBurnMultiplier = 0.25f;
    [SerializeField, Min(0.0001f)] private float steamEngineWaterPerEnergy = 0.02777778f;
    [SerializeField, Min(0.1f)] private float steamEngineWaterCapacity = 5f;
    [SerializeField, Min(0f)] private float defaultPoweredLightEnergyPerSecond = 0.5f;
    [SerializeField, Min(0f)] private float roboticArmIdleEnergyPerSecond = 1.5f;
    [SerializeField, Min(0f)] private float electricalVisualRefreshDelaySeconds = 0f;
    [SerializeField, Min(0)] private int windMillMinimumGroundClearanceBlocks = 7;
    [SerializeField, Min(1f)] private float batteryCapacity = 240f;
    [SerializeField, Min(0f)] private float directSolarBufferSeconds = 1f;
    [SerializeField, Min(0.01f)] private float waterPumpWaterPerSecond = 1f;
    [SerializeField, Min(1)] private int waterPumpRequiredWaterBlocks = 2;
    [SerializeField, Min(1)] private int waterPumpWaterSearchHorizontalRadius = 2;
    [SerializeField, Min(0)] private int waterPumpWaterSearchBelowBlocks = 1;

    [Header("Treecutter")]
    [SerializeField] private bool enableTreecutterMachines = true;
    [SerializeField, Min(0.05f)] private float treecutterTickInterval = 0.5f;
    [SerializeField, Min(1)] private int treecutterMachinesPerTick = 8;
    [SerializeField, Min(1)] private int treecutterFrontSearchDistanceBlocks = 2;
    [SerializeField, Min(0)] private int treecutterFrontVerticalSearchBelow = 1;
    [SerializeField, Min(0)] private int treecutterFrontVerticalSearchAbove = 2;
    [SerializeField, Min(1)] private int treecutterMaxLogsPerTree = 96;
    [SerializeField, Min(1)] private int treecutterMaxLeavesPerTree = 384;
    [SerializeField, Min(1)] private int treecutterLeafSearchDistance = 7;
    [SerializeField, Min(0f)] private float treecutterEnergyPerTree = 80f;
    [SerializeField, Min(0.05f)] private float treecutterBreakDurationSeconds = 2f;

    [Header("Auto Miner")]
    [SerializeField] private bool enableAutoMinerMachines = true;
    [SerializeField, Min(0.05f)] private float autoMinerTickInterval = 0.5f;
    [SerializeField, Min(1)] private int autoMinerMachinesPerTick = 4;
    [SerializeField, Min(1)] private int autoMinerBlocksPerTick = 1;
    [SerializeField, Min(1)] private int autoMinerAreaSize = 16;
    [SerializeField, Min(3)] private int autoMinerMinimumY = 3;
    [SerializeField, Min(0f)] private float autoMinerEnergyPerBlock = 48f;
    [SerializeField] private bool autoMinerMineOnlyLoadedColumns = true;
    [SerializeField, Min(0f)] private float autoMinerDropTopOffset = 0.32f;
    [SerializeField, Min(0f)] private float autoMinerDropConveyorTopOffset = 0.28f;
    [SerializeField, Min(16)] private int autoMinerTransportTubeSearchLimit = 512;
    [SerializeField, Min(0f)] private float autoMinerTransportTubeExitOffset = 0.18f;
    [SerializeField] private bool showAutoMinerMiningArea = true;
    [SerializeField] private Color autoMinerAreaLineColor = new Color(1f, 0.78f, 0.05f, 0.9f);
    [SerializeField, Min(0.001f)] private float autoMinerAreaLineWidth = 0.035f;
    [SerializeField, Min(0f)] private float autoMinerAreaLineTopOffset = 0.04f;
    [SerializeField] private VoxelLineRendererStyle autoMinerAreaLineStyle = new VoxelLineRendererStyle
    {
        color = new Color(1f, 0.78f, 0.05f, 0.9f),
        width = 0.035f,
        capVertices = 4,
        cornerVertices = 4
    };
    [SerializeField] private bool showAutoMinerMiningLaser = true;
    [SerializeField] private Color autoMinerLaserColor = new Color(1f, 0.05f, 0.02f, 1f);
    [SerializeField, Min(0.001f)] private float autoMinerLaserWidth = 0.055f;
    [SerializeField] private VoxelLineRendererStyle autoMinerLaserLineStyle = new VoxelLineRendererStyle
    {
        color = new Color(1f, 0.05f, 0.02f, 1f),
        width = 0.055f,
        capVertices = 4,
        cornerVertices = 4,
        enableEmission = true,
        emissionColor = new Color(1f, 0.05f, 0.02f, 1f),
        emissionIntensity = 1.6f
    };
    [SerializeField, Min(0.02f)] private float autoMinerLaserDurationSeconds = 0.22f;

    [Header("Machine Block Break Audio")]
    [SerializeField] private AudioClip machineBreakBlockClip;
    [SerializeField, Range(0f, 1f)] private float machineBreakBlockVolume = 1f;
    [SerializeField, Min(0.01f)] private float machineBreakBlockMinDistance = 1.5f;
    [SerializeField, Min(0.01f)] private float machineBreakBlockMaxDistance = 16f;

    [Header("Transport Tubes")]
    [SerializeField] private bool enableTransportTubeFilters = true;
    [SerializeField, Min(0.05f)] private float transportTubeFilterTickInterval = 0.35f;
    [SerializeField, Min(1)] private int transportTubeFiltersPerTick = 8;
    [SerializeField, Min(1)] private int transportTubeFilterItemsPerTick = 1;
    [SerializeField, Min(16)] private int transportTubeFilterSearchLimit = 512;
    [SerializeField, Min(0f)] private float transportTubeExitOffset = 0.18f;
}
