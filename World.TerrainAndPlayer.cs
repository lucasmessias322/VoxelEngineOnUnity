using UnityEngine;

public partial class World
{
    public event System.Action<Vector3Int, BlockType, BlockType> BlockChanged;

    [Header("Leaf Decay")]
    [SerializeField] private bool enableLeafDecay = true;
    [SerializeField, Min(1)] private int leafDecaySupportDistance = 4;
    [SerializeField, Min(1)] private int leafDecayChecksPerFrame = 8;
    [SerializeField, Min(0f)] private float leafDecayTimeBudgetMS = 0.5f;
    [SerializeField, Min(0.05f)] private float leafDecayStepInterval = 0.15f;
    [SerializeField, Min(0f)] private float leafDecayGraceSeconds = 1.2f;

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
    [SerializeField, Min(0)] private int windMillMinimumGroundClearanceBlocks = 7;
    [SerializeField, Min(1f)] private float batteryCapacity = 240f;
    [SerializeField, Min(0f)] private float directSolarBufferSeconds = 1f;
}
