// PlayerBlockBreaker.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(BlockSelector))]
[RequireComponent(typeof(AudioSource))]
public class PlayerBlockBreaker : MonoBehaviour
{
    private enum BreakCrackStyle
    {
        Generic = 0,
        Dirt = 1,
        Stone = 2,
        Wood = 3
    }

    private const float BreakVisualDefaultAOCurveExponent = 1.12f;
    private static readonly Vector3Int[] BreakVisualHorizontalLightDirections =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.forward,
        Vector3Int.back
    };

    public BlockSelector selector;
    public Camera cam;
    public HotbarMirror hotbar;

    [Header("Place settings")]
    public BlockType placeBlockType = BlockType.Stone; // fallback se nao houver hotbar configurada
    [SerializeField] private bool preventPlaceInsidePlayer = true;
    [SerializeField] private float fallbackPlayerRadius = 0.35f;
    [SerializeField] private float fallbackPlayerHeight = 1.8f;

    private AudioSource audioSource;
    public AudioClip placeBlockClip;
    public AudioClip breakBlockClip;

    [Header("Break settings")]
    [Min(0.05f)] public float breakDurationSeconds = 0.45f;
    [Tooltip("Material transparente para desenhar as rachaduras sobre o bloco.")]
    public Material breakCrackMaterial;
    [Tooltip("Texturas em ordem de dano (0..N-1). Ex.: crack_0 ate crack_9.")]
    public Texture2D[] breakCrackStages;
    [Tooltip("Opcional: stages de terra/grama/areia/neve no estilo Hytale. Se vazio, usa Break Crack Stages.")]
    public Texture2D[] dirtBreakCrackStages;
    [Tooltip("Opcional: stages de pedra/minerios/vidro no estilo Hytale. Se vazio, usa Break Crack Stages.")]
    public Texture2D[] stoneBreakCrackStages;
    [Tooltip("Opcional: stages de madeira/troncos/tabuas no estilo Hytale. Se vazio, usa Break Crack Stages.")]
    public Texture2D[] woodBreakCrackStages;
    [Range(1.001f, 1.1f)] public float crackOverlayScale = 1.01f;

    [Header("Break shader shake")]
    [SerializeField] private bool enableBreakShaderShake = true;
    [SerializeField, Min(0f)] private float breakShakeStrength = 0.035f;
    [SerializeField, Min(0.001f)] private float breakShakeFrequency = 28f;
    [SerializeField, Min(0f)] private float breakShakeScaleStrength = 0.025f;
    [SerializeField, Min(0f)] private float breakShaderBoundsPadding = 0.004f;
    [SerializeField, Range(1f, 1.08f)] private float breakVisualOverlayScale = 1.015f;

    private static readonly int VoxelBreakBlockCenterId = Shader.PropertyToID("_VoxelBreakBlockCenterWS");
    private static readonly int VoxelBreakBlockHalfExtentsId = Shader.PropertyToID("_VoxelBreakBlockHalfExtents");
    private static readonly int VoxelBreakShakeId = Shader.PropertyToID("_VoxelBreakShake");

    private GameObject crackOverlayObject;
    private MeshRenderer crackOverlayRenderer;
    private Material crackOverlayRuntimeMaterial;
    private GameObject breakVisualObject;
    private MeshFilter breakVisualMeshFilter;
    private MeshRenderer breakVisualRenderer;
    private Mesh breakVisualMesh;
    private BlockType breakVisualBlockType = BlockType.Air;
    private BlockPlacementAxis breakVisualPlacementAxis = BlockPlacementAxis.Y;
    private int breakVisualSubchunkIndex = -1;
    private Vector3Int breakVisualBlockPosition = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private Vector3Int breakVisualHitNormal = Vector3Int.zero;
    private readonly Dictionary<Vector3Int, int> breakVisualLightSampleCache = new Dictionary<Vector3Int, int>();
    private readonly Dictionary<Vector3Int, bool> breakVisualDirectSkyAccessCache = new Dictionary<Vector3Int, bool>();
    private readonly Dictionary<Vector3Int, int> breakVisualSkyLightEstimateCache = new Dictionary<Vector3Int, int>();
    private readonly Dictionary<Vector3Int, int> breakVisualSkyPropagationLight = new Dictionary<Vector3Int, int>();
    private readonly Queue<Vector3Int> breakVisualSkyPropagationQueue = new Queue<Vector3Int>();
    private Vector3Int breakingBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private bool breakingIsBillboard;
    private float breakProgress01;
    private int lastCrackStage = -1;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (selector == null) selector = GetComponent<BlockSelector>();
        if (cam == null && selector != null) cam = selector.cam;
        if (hotbar == null) hotbar = FindAnyObjectByType<HotbarMirror>();

        CreateCrackOverlay();
        CreateBreakVisualOverlay();
        ClearBreakShaderEffect();
    }

    void Update()
    {
        if (World.Instance != null && !World.Instance.IsInitialWorldReady)
        {
            CancelBreak();
            return;
        }

        if (PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
        {
            CancelBreak();
            return;
        }

        HandleBreakBlock();
        HandlePlaceBlock();
    }

    void HandleBreakBlock()
    {
        if (!Input.GetMouseButton(0))
        {
            CancelBreak();
            return;
        }

        if (!selector.TryGetSelectedBlock(out Vector3Int sel, out Vector3Int hitNormal))
        {
            CancelBreak();
            return;
        }

        if (selector.IsBillboardHit)
        {
            // Billboard de grama nao existe como voxel real, mas usa o mesmo fluxo de "minerar".
            if (sel != breakingBlock || !breakingIsBillboard)
            {
                breakingBlock = sel;
                breakingIsBillboard = true;
                breakProgress01 = 0f;
                lastCrackStage = -1;
            }

            float breakDuration = GetBreakDurationSeconds(GetBillboardBreakType(sel));
            breakProgress01 += Time.deltaTime / breakDuration;
            ClearBreakShaderEffect();
            UpdateCrackOverlay(sel, breakProgress01);

            if (breakProgress01 < 1f)
                return;

            World.Instance.SuppressGrassBillboardAt(sel);
            if (breakBlockClip != null)
                audioSource.PlayOneShot(breakBlockClip);

            CancelBreak();
            return;
        }

        World world = World.Instance;
        if (world == null)
        {
            CancelBreak();
            return;
        }

        BlockType current = world.GetBlockAt(sel);
        if (!CanBreak(current))
        {
            CancelBreak();
            return;
        }

        if (sel != breakingBlock || breakingIsBillboard)
        {
            breakingBlock = sel;
            breakingIsBillboard = false;
            breakProgress01 = 0f;
            lastCrackStage = -1;
        }

        float currentBreakDuration = GetBreakDurationSeconds(current);
        breakProgress01 += Time.deltaTime / currentBreakDuration;
        UpdateBreakShaderEffect(sel, current, hitNormal, breakProgress01);
        UpdateCrackOverlay(sel, breakProgress01);

        if (breakProgress01 < 1f)
            return;

        bool shouldDrop = ShouldDropBlock(current);
        Vector3 throwDir = cam != null ? cam.transform.forward : transform.forward;
        bool treeCapitatorTriggered = world.TryQueueTreeCapitatorBreak(sel, current, shouldDrop, throwDir);
        if (!treeCapitatorTriggered)
        {
            bool spawnedDrop = shouldDrop && BlockDrop.Spawn(world, sel, current, throwDir);
            world.SetBlockAt(sel, BlockType.Air);

            if (shouldDrop && !spawnedDrop)
            {
                bool addedToInventory = PlayerInventory.Instance != null &&
                                        PlayerInventory.Instance.TryAddBlockDrop(current, 1);
                if (!addedToInventory)
                {
                    Debug.LogWarning($"[PlayerBlockBreaker] Falha ao gerar drop de {current} em {sel}.");
                }
            }
        }

        if (breakBlockClip != null)
            audioSource.PlayOneShot(breakBlockClip);

        CancelBreak();
    }

    bool CanBreak(BlockType blockType)
    {
        return blockType != BlockType.Bedrock &&
               blockType != BlockType.Air &&
               !IsLiquid(blockType);
    }

    bool ShouldDropBlock(BlockType blockType)
    {
        return blockType != BlockType.Leaves;
    }

    bool IsLiquid(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return FluidBlockUtility.IsWater(blockType);

        return world.blockData.IsLiquid(blockType);
    }

    float GetBreakDurationSeconds(BlockType blockType)
    {
        float duration = Mathf.Max(0.05f, breakDurationSeconds);

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return duration;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null)
            return duration;

        BlockTextureMapping blockMapping = mapping.Value;
        float hardnessMultiplier = ResolveBreakTimeMultiplier(blockType, blockMapping);
        ToolType preferredTool = ResolvePreferredTool(blockType, blockMapping);

        duration *= hardnessMultiplier;

        if (preferredTool == ToolType.None)
            return Mathf.Max(0.05f, duration);

        if (!TryGetSelectedTool(out ToolType selectedTool, out float toolEfficiency))
            return Mathf.Max(0.05f, duration);

        if (selectedTool != preferredTool)
            return Mathf.Max(0.05f, duration);

        return Mathf.Max(0.05f, duration / Mathf.Max(1f, toolEfficiency));
    }

    bool TryGetSelectedTool(out ToolType toolType, out float toolEfficiency)
    {
        toolType = ToolType.None;
        toolEfficiency = 1f;

        if (hotbar == null || !hotbar.TryGetSelectedItem(out Item selectedItem) || selectedItem == null)
            return false;

        toolType = selectedItem.toolType;
        toolEfficiency = Mathf.Max(1f, selectedItem.toolEfficiency);
        return toolType != ToolType.None;
    }

    BlockType GetBillboardBreakType(Vector3Int billboardPos)
    {
        World world = World.Instance;
        if (world != null &&
            world.TryResolveVegetationBillboardAt(billboardPos, out BlockType resolvedType, out _))
        {
            return resolvedType;
        }

        if (world != null)
            return world.grassBillboardBlockType;

        return BlockType.short_grass4;
    }

    ToolType ResolvePreferredTool(BlockType blockType, BlockTextureMapping mapping)
    {
        if (mapping.preferredTool != ToolType.None)
            return mapping.preferredTool;

        return GetDefaultPreferredTool(blockType);
    }

    float ResolveBreakTimeMultiplier(BlockType blockType, BlockTextureMapping mapping)
    {
        if (mapping.breakTimeMultiplier > 0f)
            return mapping.breakTimeMultiplier;

        return GetDefaultBreakTimeMultiplier(blockType);
    }

    ToolType GetDefaultPreferredTool(BlockType blockType)
    {
        switch (blockType)
        {
            case BlockType.Stone:
            case BlockType.Deepslate:
            case BlockType.CoalOre:
            case BlockType.IronOre:
            case BlockType.GoldOre:
            case BlockType.Copper_ore:
            case BlockType.DiamondOre:
            case BlockType.EmeraldOre:
            case BlockType.glass:
            case BlockType.glowstone:
            case BlockType.Crafter:
                return ToolType.Pickaxe;

            case BlockType.Log:
            case BlockType.birch_log:
            case BlockType.acacia_log:
            case BlockType.oak_planks:
            case BlockType.Cactus:
                return ToolType.Axe;

            case BlockType.Grass:
            case BlockType.Dirt:
            case BlockType.Sand:
            case BlockType.Snow:
                return ToolType.Shovel;

            case BlockType.Leaves:
            case BlockType.short_grass4:
                return ToolType.Hoe;

            default:
                return ToolType.None;
        }
    }

    float GetDefaultBreakTimeMultiplier(BlockType blockType)
    {
        switch (blockType)
        {
            case BlockType.Stone:
                return 1.25f;
            case BlockType.Deepslate:
                return 1.75f;
            case BlockType.CoalOre:
            case BlockType.IronOre:
            case BlockType.GoldOre:
            case BlockType.Copper_ore:
            case BlockType.DiamondOre:
            case BlockType.EmeraldOre:
                return 1.5f;
            case BlockType.Log:
            case BlockType.birch_log:
            case BlockType.acacia_log:
            case BlockType.oak_planks:
            case BlockType.Cactus:
                return 1.1f;
            case BlockType.Grass:
            case BlockType.Dirt:
            case BlockType.Sand:
            case BlockType.Snow:
                return 0.85f;
            case BlockType.Leaves:
            case BlockType.short_grass4:
                return 0.35f;
            default:
                return 1f;
        }
    }

    static bool HasBreakCrackStages(Texture2D[] stages)
    {
        return stages != null && stages.Length > 0;
    }

    Texture2D[] ResolveBreakCrackStages(BlockType blockType)
    {
        Texture2D[] fallbackStages = breakCrackStages;
        Texture2D[] preferredStages = ResolveBreakCrackStyle(blockType) switch
        {
            BreakCrackStyle.Dirt => dirtBreakCrackStages,
            BreakCrackStyle.Stone => stoneBreakCrackStages,
            BreakCrackStyle.Wood => woodBreakCrackStages,
            _ => breakCrackStages
        };

        return HasBreakCrackStages(preferredStages) ? preferredStages : fallbackStages;
    }

    BreakCrackStyle ResolveBreakCrackStyle(BlockType blockType)
    {
        World world = World.Instance;
        ToolType preferredTool = ToolType.None;

        if (world != null && world.blockData != null)
        {
            BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
            if (mapping != null)
                preferredTool = ResolvePreferredTool(blockType, mapping.Value);
        }

        if (preferredTool == ToolType.None)
            preferredTool = GetDefaultPreferredTool(blockType);

        return preferredTool switch
        {
            ToolType.Shovel => BreakCrackStyle.Dirt,
            ToolType.Pickaxe => BreakCrackStyle.Stone,
            ToolType.Axe => BreakCrackStyle.Wood,
            _ => BreakCrackStyle.Generic
        };
    }

    void CreateCrackOverlay()
    {
        if (breakCrackMaterial == null)
            return;

        crackOverlayObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        crackOverlayObject.name = "BreakCrackOverlay";
        crackOverlayObject.SetActive(false);
        if (World.Instance != null)
            crackOverlayObject.transform.SetParent(World.Instance.transform, true);

        Collider col = crackOverlayObject.GetComponent<Collider>();
        if (col != null) Destroy(col);

        crackOverlayRenderer = crackOverlayObject.GetComponent<MeshRenderer>();
        crackOverlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
        crackOverlayRenderer.receiveShadows = false;
        crackOverlayRenderer.lightProbeUsage = LightProbeUsage.Off;
        crackOverlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        crackOverlayRuntimeMaterial = new Material(breakCrackMaterial);
        crackOverlayRenderer.sharedMaterial = crackOverlayRuntimeMaterial;
    }

    void CreateBreakVisualOverlay()
    {
        breakVisualObject = new GameObject("BreakBlockVisualOverlay");
        breakVisualObject.SetActive(false);
        if (World.Instance != null)
            breakVisualObject.transform.SetParent(World.Instance.transform, true);

        breakVisualMeshFilter = breakVisualObject.AddComponent<MeshFilter>();
        breakVisualRenderer = breakVisualObject.AddComponent<MeshRenderer>();
        breakVisualRenderer.shadowCastingMode = ShadowCastingMode.Off;
        breakVisualRenderer.receiveShadows = false;
        breakVisualRenderer.lightProbeUsage = LightProbeUsage.Off;
        breakVisualRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        breakVisualMesh = new Mesh { name = "BreakBlockVisualOverlayMesh" };
        breakVisualMesh.MarkDynamic();
        breakVisualMeshFilter.sharedMesh = breakVisualMesh;
    }

    void UpdateCrackOverlay(Vector3Int blockPos, float progress01)
    {
        if (crackOverlayObject == null || crackOverlayRenderer == null || crackOverlayRuntimeMaterial == null)
            return;

        crackOverlayObject.SetActive(true);

        World world = World.Instance;
        BlockType overlayType = breakingIsBillboard || world == null
            ? GetBillboardBreakType(blockPos)
            : world.GetBlockAt(blockPos);

        Bounds overlayBounds = ResolveBlockBounds(blockPos, overlayType);
        Vector3 shakeOffset = Vector3.zero;
        float shakeScale = 1f;
        if (enableBreakShaderShake && !breakingIsBillboard)
        {
            shakeOffset = ComputeBreakShakeOffset(overlayBounds.center, progress01);
            shakeScale = ComputeBreakShakeScale(overlayBounds.center, progress01);
        }

        crackOverlayObject.transform.position = overlayBounds.center + shakeOffset;
        crackOverlayObject.transform.localScale = overlayBounds.size * crackOverlayScale * shakeScale;

        Texture2D[] activeStages = ResolveBreakCrackStages(overlayType);
        if (!HasBreakCrackStages(activeStages))
            return;

        float clampedProgress = Mathf.Clamp01(progress01);
        int stageCount = activeStages.Length;
        int stage = Mathf.Clamp(Mathf.FloorToInt(clampedProgress * stageCount), 0, stageCount - 1);
        if (stage == lastCrackStage)
            return;

        Texture2D tex = activeStages[stage];
        if (tex == null && activeStages != breakCrackStages && HasBreakCrackStages(breakCrackStages))
        {
            int fallbackStage = Mathf.Clamp(
                Mathf.FloorToInt(clampedProgress * breakCrackStages.Length),
                0,
                breakCrackStages.Length - 1);
            tex = breakCrackStages[fallbackStage];
        }

        if (tex == null)
            return;

        crackOverlayRuntimeMaterial.SetTexture("_CrackTex", tex);
        lastCrackStage = stage;
    }

    void UpdateBreakShaderEffect(Vector3Int blockPos, BlockType blockType, Vector3Int hitNormal, float progress01)
    {
        if (!enableBreakShaderShake)
        {
            ClearBreakShaderEffect();
            HideBreakVisualOverlay();
            return;
        }

        Bounds bounds = ResolveBlockBounds(blockPos, blockType);
        Vector3 center = bounds.center;
        Vector3 halfExtents = bounds.extents + Vector3.one * breakShaderBoundsPadding;
        Vector3 shakeOffset = ComputeBreakShakeOffset(center, progress01);
        float shakeScale = ComputeBreakShakeScale(center, progress01);

        UpdateBreakVisualOverlay(blockPos, blockType, bounds, hitNormal, shakeOffset, shakeScale);

        Shader.SetGlobalVector(
            VoxelBreakBlockCenterId,
            new Vector4(center.x, center.y, center.z, 1f));
        Shader.SetGlobalVector(
            VoxelBreakBlockHalfExtentsId,
            new Vector4(halfExtents.x, halfExtents.y, halfExtents.z, 0f));
        Shader.SetGlobalVector(
            VoxelBreakShakeId,
            new Vector4(
                Mathf.Clamp01(progress01),
                Mathf.Max(0f, breakShakeStrength),
                Mathf.Max(0.001f, breakShakeFrequency),
                Mathf.Max(0f, breakShakeScaleStrength)));
    }

    void ClearBreakShaderEffect()
    {
        Shader.SetGlobalVector(VoxelBreakBlockCenterId, Vector4.zero);
        Shader.SetGlobalVector(VoxelBreakBlockHalfExtentsId, Vector4.zero);
        Shader.SetGlobalVector(VoxelBreakShakeId, Vector4.zero);
        HideBreakVisualOverlay();
    }

    void UpdateBreakVisualOverlay(
        Vector3Int blockPos,
        BlockType blockType,
        Bounds bounds,
        Vector3Int hitNormal,
        Vector3 shakeOffset,
        float shakeScale)
    {
        if (breakVisualObject == null || breakVisualMesh == null || breakVisualRenderer == null)
            return;

        World world = World.Instance;
        if (world == null || world.blockData == null)
        {
            HideBreakVisualOverlay();
            return;
        }

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
        {
            HideBreakVisualOverlay();
            return;
        }

        BlockTextureMapping mapping = mappingResult.Value;
        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube)
        {
            HideBreakVisualOverlay();
            return;
        }

        Material overlayMaterial = ResolveBreakVisualMaterial(blockType, mapping);
        if (overlayMaterial == null)
        {
            HideBreakVisualOverlay();
            return;
        }

        BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(blockPos, blockType);
        int subchunkIndex = Mathf.Clamp(blockPos.y / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
        if (breakVisualBlockType != blockType ||
            breakVisualPlacementAxis != placementAxis ||
            breakVisualSubchunkIndex != subchunkIndex ||
            breakVisualBlockPosition != blockPos ||
            breakVisualHitNormal != NormalizeBreakVisualHitNormal(hitNormal) ||
            breakVisualMesh.vertexCount == 0)
        {
            BuildBreakVisualCubeMesh(blockPos, blockType, mapping, placementAxis, hitNormal, bounds.size);
            breakVisualBlockType = blockType;
            breakVisualPlacementAxis = placementAxis;
            breakVisualSubchunkIndex = subchunkIndex;
            breakVisualBlockPosition = blockPos;
            breakVisualHitNormal = NormalizeBreakVisualHitNormal(hitNormal);
        }

        if (breakVisualRenderer.sharedMaterial != overlayMaterial)
            breakVisualRenderer.sharedMaterial = overlayMaterial;

        world.ApplyBiomeTintToRendererAt(breakVisualRenderer, blockPos);

        breakVisualObject.transform.position = bounds.center + shakeOffset;
        breakVisualObject.transform.rotation = Quaternion.identity;
        breakVisualObject.transform.localScale = Vector3.one * (Mathf.Max(1f, breakVisualOverlayScale) * shakeScale);

        if (!breakVisualObject.activeSelf)
            breakVisualObject.SetActive(true);
    }

    Material ResolveBreakVisualMaterial(BlockType blockType, BlockTextureMapping mapping)
    {
        World world = World.Instance;
        if (world == null || world.Material == null || world.Material.Length == 0)
            return null;

        int materialIndex = 0;
        if (FluidBlockUtility.IsWater(blockType))
            materialIndex = 2;
        else if (mapping.isTransparent)
            materialIndex = 1;

        materialIndex = Mathf.Clamp(materialIndex, 0, world.Material.Length - 1);
        Material material = world.Material[materialIndex];
        return material != null ? material : world.Material[0];
    }

    private struct BreakVisualFaceLighting
    {
        public float light0;
        public float light1;
        public float light2;
        public float light3;
        public float ao0;
        public float ao1;
        public float ao2;
        public float ao3;
        public bool flipTriangle;

        public float GetLight(int corner)
        {
            return corner switch
            {
                1 => light1,
                2 => light2,
                3 => light3,
                _ => light0
            };
        }

        public float GetAO(int corner)
        {
            return corner switch
            {
                1 => ao1,
                2 => ao2,
                3 => ao3,
                _ => ao0
            };
        }
    }

    BreakVisualFaceLighting ResolveBreakVisualFaceLighting(
        World world,
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockFace worldFace)
    {
        BreakVisualFaceLighting result = new BreakVisualFaceLighting
        {
            light0 = 1f,
            light1 = 1f,
            light2 = 1f,
            light3 = 1f,
            ao0 = 1f,
            ao1 = 1f,
            ao2 = 1f,
            ao3 = 1f
        };

        if (!TryGetBreakVisualFaceAxes(
                worldFace,
                out _,
                out _,
                out Vector3Int normalOffset,
                out Vector3Int stepU,
                out Vector3Int stepV))
        {
            return result;
        }

        Vector3Int faceSamplePos = blockPos + normalOffset;
        int faceLight = SampleBreakVisualLightValue(world, faceSamplePos);
        bool disableAO =
            world == null ||
            !world.enableAmbientOcclusion ||
            world.aoStrength <= 0f ||
            IsBreakVisualEmissive(mapping);

        int rawAO0 = 3;
        int rawAO1 = 3;
        int rawAO2 = 3;
        int rawAO3 = 3;
        if (!disableAO)
        {
            rawAO0 = ResolveBreakVisualVertexAO(world, faceSamplePos, -stepU, -stepV);
            rawAO1 = ResolveBreakVisualVertexAO(world, faceSamplePos, stepU, -stepV);
            rawAO2 = ResolveBreakVisualVertexAO(world, faceSamplePos, stepU, stepV);
            rawAO3 = ResolveBreakVisualVertexAO(world, faceSamplePos, -stepU, stepV);
        }

        int light0 = ResolveBreakVisualVertexLight(world, faceSamplePos, -stepU, -stepV);
        int light1 = ResolveBreakVisualVertexLight(world, faceSamplePos, stepU, -stepV);
        int light2 = ResolveBreakVisualVertexLight(world, faceSamplePos, stepU, stepV);
        int light3 = ResolveBreakVisualVertexLight(world, faceSamplePos, -stepU, stepV);
        int emission = world != null ? world.GetBlockEmission(blockType) : mapping.lightEmission;

        light0 = Mathf.Max(light0, faceLight, emission);
        light1 = Mathf.Max(light1, faceLight, emission);
        light2 = Mathf.Max(light2, faceLight, emission);
        light3 = Mathf.Max(light3, faceLight, emission);

        result.light0 = Mathf.Clamp01(light0 / 15f);
        result.light1 = Mathf.Clamp01(light1 / 15f);
        result.light2 = Mathf.Clamp01(light2 / 15f);
        result.light3 = Mathf.Clamp01(light3 / 15f);
        result.ao0 = ResolveBreakVisualAO01(world, rawAO0);
        result.ao1 = ResolveBreakVisualAO01(world, rawAO1);
        result.ao2 = ResolveBreakVisualAO01(world, rawAO2);
        result.ao3 = ResolveBreakVisualAO01(world, rawAO3);
        result.flipTriangle = (rawAO0 + rawAO2) > (rawAO1 + rawAO3);
        return result;
    }

    void BuildBreakVisualCubeMesh(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Vector3Int hitNormal,
        Vector3 size)
    {
        if (breakVisualMesh == null)
            return;

        breakVisualLightSampleCache.Clear();
        breakVisualDirectSkyAccessCache.Clear();
        breakVisualSkyLightEstimateCache.Clear();

        World world = World.Instance;
        Vector2 atlasSize = world != null && world.blockData != null
            ? world.blockData.atlasSize
            : Vector2.one;
        float invAtlasTilesX = 1f / Mathf.Max(1f, atlasSize.x);
        float invAtlasTilesY = 1f / Mathf.Max(1f, atlasSize.y);
        int faceSubchunkIndex = Mathf.Clamp(blockPos.y / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);

        Vector3 extents = size * 0.5f;
        Vector3 min = -extents;
        Vector3 max = extents;

        Vector3[] vertices = new Vector3[24];
        Vector3[] normals = new Vector3[24];
        Vector2[] uv0 = new Vector2[24];
        Vector2[] uv1 = new Vector2[24];
        Vector4[] uv2 = new Vector4[24];
        int[] triangles = new int[36];

        int vertexStart = 0;
        int triangleStart = 0;
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Right,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Left,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Top,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Bottom,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Front,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);
        AddBreakVisualFace(
            vertices, normals, uv0, uv1, uv2, triangles,
            ref vertexStart, ref triangleStart,
            blockPos,
            min,
            max,
            BlockFace.Back,
            blockType,
            mapping,
            placementAxis,
            invAtlasTilesX,
            invAtlasTilesY,
            faceSubchunkIndex);

        breakVisualMesh.Clear();
        breakVisualMesh.vertices = vertices;
        breakVisualMesh.normals = normals;
        breakVisualMesh.uv = uv0;
        breakVisualMesh.uv2 = uv1;
        breakVisualMesh.SetUVs(2, uv2);
        breakVisualMesh.triangles = triangles;
        breakVisualMesh.RecalculateBounds();
    }

    void AddBreakVisualFace(
        Vector3[] vertices,
        Vector3[] normals,
        Vector2[] uv0,
        Vector2[] uv1,
        Vector4[] uv2,
        int[] triangles,
        ref int vertexStart,
        ref int triangleStart,
        Vector3Int blockPos,
        Vector3 min,
        Vector3 max,
        BlockFace worldFace,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        float invAtlasTilesX,
        float invAtlasTilesY,
        int faceSubchunkIndex)
    {
        if (!TryGetBreakVisualFaceAxes(
                worldFace,
                out int axis,
                out int normalSign,
                out Vector3Int normalOffset,
                out _,
                out _))
        {
            return;
        }

        BlockFace sampledFace = BlockPlacementRotationUtility.ResolveFaceForPlacement(mapping, worldFace, placementAxis);
        BlockPlacementAxis uvPlacementAxis = ResolveBreakVisualUvPlacementAxis(mapping, placementAxis);
        BlockFace uvSamplingFace = ResolveBreakVisualUvSamplingFace(mapping, worldFace, sampledFace);
        Vector2Int tile = mapping.GetTileCoord(sampledFace);
        Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
        float tintMask = mapping.GetTint(sampledFace) ? 1f : 0f;
        bool grassSideOverlay = blockType == BlockType.Grass &&
                                sampledFace != BlockFace.Top &&
                                sampledFace != BlockFace.Bottom;
        float packedSubchunkAndOverlay = faceSubchunkIndex + (grassSideOverlay ? 0.25f : 0f);
        BreakVisualFaceLighting faceLighting =
            ResolveBreakVisualFaceLighting(World.Instance, blockPos, blockType, mapping, worldFace);
        Vector3 normal = new Vector3(normalOffset.x, normalOffset.y, normalOffset.z);

        for (int corner = 0; corner < 4; corner++)
        {
            Vector3 vertex = GetBreakVisualFaceLocalVertex(axis, normalSign, corner, min, max);
            int index = vertexStart + corner;

            vertices[index] = vertex;
            normals[index] = normal;
            uv0[index] = ComputeBreakVisualPlacementAwareUv(blockPos, vertex, uvSamplingFace, uvPlacementAxis);
            uv1[index] = atlasUv;
            uv2[index] = new Vector4(
                faceLighting.GetLight(corner),
                tintMask,
                faceLighting.GetAO(corner),
                packedSubchunkAndOverlay);
        }

        WriteBreakVisualFaceTriangles(triangles, triangleStart, vertexStart, normalSign, faceLighting.flipTriangle);

        vertexStart += 4;
        triangleStart += 6;
    }

    static Vector3 GetBreakVisualFaceLocalVertex(int axis, int normalSign, int corner, Vector3 min, Vector3 max)
    {
        int u = (axis + 1) % 3;
        int v = (axis + 2) % 3;
        bool useMaxU = corner == 1 || corner == 2;
        bool useMaxV = corner == 2 || corner == 3;

        Vector3 vertex = Vector3.zero;
        SetBreakVisualAxisValue(ref vertex, axis, normalSign > 0 ? GetBreakVisualAxisValue(max, axis) : GetBreakVisualAxisValue(min, axis));
        SetBreakVisualAxisValue(ref vertex, u, useMaxU ? GetBreakVisualAxisValue(max, u) : GetBreakVisualAxisValue(min, u));
        SetBreakVisualAxisValue(ref vertex, v, useMaxV ? GetBreakVisualAxisValue(max, v) : GetBreakVisualAxisValue(min, v));
        return vertex;
    }

    static float GetBreakVisualAxisValue(Vector3 value, int axis)
    {
        return axis switch
        {
            0 => value.x,
            1 => value.y,
            _ => value.z
        };
    }

    static void SetBreakVisualAxisValue(ref Vector3 value, int axis, float axisValue)
    {
        switch (axis)
        {
            case 0:
                value.x = axisValue;
                break;
            case 1:
                value.y = axisValue;
                break;
            default:
                value.z = axisValue;
                break;
        }
    }

    static void WriteBreakVisualFaceTriangles(
        int[] triangles,
        int triangleStart,
        int vertexStart,
        int normalSign,
        bool flipTriangle)
    {
        if (normalSign > 0)
        {
            if (flipTriangle)
            {
                triangles[triangleStart + 0] = vertexStart + 0;
                triangles[triangleStart + 1] = vertexStart + 1;
                triangles[triangleStart + 2] = vertexStart + 3;
                triangles[triangleStart + 3] = vertexStart + 1;
                triangles[triangleStart + 4] = vertexStart + 2;
                triangles[triangleStart + 5] = vertexStart + 3;
            }
            else
            {
                triangles[triangleStart + 0] = vertexStart + 0;
                triangles[triangleStart + 1] = vertexStart + 1;
                triangles[triangleStart + 2] = vertexStart + 2;
                triangles[triangleStart + 3] = vertexStart + 0;
                triangles[triangleStart + 4] = vertexStart + 2;
                triangles[triangleStart + 5] = vertexStart + 3;
            }

            return;
        }

        if (flipTriangle)
        {
            triangles[triangleStart + 0] = vertexStart + 0;
            triangles[triangleStart + 1] = vertexStart + 3;
            triangles[triangleStart + 2] = vertexStart + 1;
            triangles[triangleStart + 3] = vertexStart + 1;
            triangles[triangleStart + 4] = vertexStart + 3;
            triangles[triangleStart + 5] = vertexStart + 2;
        }
        else
        {
            triangles[triangleStart + 0] = vertexStart + 0;
            triangles[triangleStart + 1] = vertexStart + 3;
            triangles[triangleStart + 2] = vertexStart + 2;
            triangles[triangleStart + 3] = vertexStart + 0;
            triangles[triangleStart + 4] = vertexStart + 2;
            triangles[triangleStart + 5] = vertexStart + 1;
        }
    }

    static BlockPlacementAxis ResolveBreakVisualUvPlacementAxis(BlockTextureMapping mapping, BlockPlacementAxis placementAxis)
    {
        if (!mapping.usePlacementAxisRotation)
            return BlockPlacementAxis.Y;

        if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
            BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
        {
            return BlockPlacementAxis.Y;
        }

        return placementAxis;
    }

    static BlockFace ResolveBreakVisualUvSamplingFace(
        BlockTextureMapping mapping,
        BlockFace worldFace,
        BlockFace sampledFace)
    {
        if (!mapping.usePlacementAxisRotation)
            return worldFace;

        if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
            BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
        {
            return worldFace;
        }

        return sampledFace;
    }

    static Vector2 ComputeBreakVisualPlacementAwareUv(
        Vector3Int blockPos,
        Vector3 localVertex,
        BlockFace sampledFace,
        BlockPlacementAxis placementAxis)
    {
        Vector3 worldCoords = (Vector3)blockPos + Vector3.one * 0.5f + localVertex;
        Vector3 canonicalCoords = ToBreakVisualCanonicalCoords(worldCoords, placementAxis);
        return sampledFace switch
        {
            BlockFace.Top => new Vector2(canonicalCoords.x, canonicalCoords.z),
            BlockFace.Bottom => new Vector2(canonicalCoords.x, canonicalCoords.z),
            BlockFace.Right => new Vector2(canonicalCoords.z, canonicalCoords.y),
            BlockFace.Left => new Vector2(canonicalCoords.z, canonicalCoords.y),
            BlockFace.Front => new Vector2(canonicalCoords.x, canonicalCoords.y),
            BlockFace.Back => new Vector2(canonicalCoords.x, canonicalCoords.y),
            _ => new Vector2(canonicalCoords.x, canonicalCoords.y)
        };
    }

    static Vector3 ToBreakVisualCanonicalCoords(Vector3 worldCoords, BlockPlacementAxis placementAxis)
    {
        return BlockPlacementRotationUtility.SanitizeAxis(placementAxis) switch
        {
            BlockPlacementAxis.X => new Vector3(-worldCoords.y, worldCoords.x, worldCoords.z),
            BlockPlacementAxis.Z => new Vector3(worldCoords.x, worldCoords.z, -worldCoords.y),
            _ => worldCoords
        };
    }

    int SampleBreakVisualLightValue(World world, Vector3Int samplePos)
    {
        if (breakVisualLightSampleCache.TryGetValue(samplePos, out int cachedLight))
            return cachedLight;

        if (world == null || !world.enableVoxelLighting)
            return 15;
        if (samplePos.y < 0)
            return 0;
        if (samplePos.y >= Chunk.SizeY)
            return 15;

        if (world.TryGetRenderedBlockLightAt(samplePos, out byte renderedPackedLight))
        {
            int renderedLight = Mathf.Max(
                LightUtils.GetBlockLight(renderedPackedLight),
                LightUtils.GetSkyLight(renderedPackedLight));
            breakVisualLightSampleCache[samplePos] = renderedLight;
            return renderedLight;
        }

        byte packedLight = world.GetGlobalBlockLightAt(samplePos);
        int blockLight = LightUtils.GetBlockLight(packedLight);
        int columnSkyLight = LightUtils.GetSkyLight(packedLight);
        int estimatedSkyLight = EstimateBreakVisualSkyLight(world, samplePos);
        int resolvedLight = Mathf.Max(blockLight, columnSkyLight, estimatedSkyLight);
        breakVisualLightSampleCache[samplePos] = resolvedLight;
        return resolvedLight;
    }

    int ResolveBreakVisualVertexLight(World world, Vector3Int pos, Vector3Int d1, Vector3Int d2)
    {
        int l0 = SampleBreakVisualLightValue(world, pos);
        int l1 = SampleBreakVisualLightValue(world, pos + d1);
        int l2 = SampleBreakVisualLightValue(world, pos + d2);
        int l3 = SampleBreakVisualLightValue(world, pos + d1 + d2);
        return (l0 + l1 + l2 + l3 + 2) / 4;
    }

    int ResolveBreakVisualVertexAO(World world, Vector3Int pos, Vector3Int d1, Vector3Int d2)
    {
        bool side1 = IsBreakVisualAOOccluder(world, pos + d1);
        bool side2 = IsBreakVisualAOOccluder(world, pos + d2);
        bool corner = IsBreakVisualAOOccluder(world, pos + d1 + d2);

        if (side1 && side2)
            return 0;

        return 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0);
    }

    float ResolveBreakVisualAO01(World world, int rawAO)
    {
        float aoBase = Mathf.Clamp01(rawAO / 3f);
        float aoCurve = world != null && world.aoCurveExponent > 0f
            ? world.aoCurveExponent
            : BreakVisualDefaultAOCurveExponent;
        float aoStrength = world != null && world.enableAmbientOcclusion ? Mathf.Max(0f, world.aoStrength) : 0f;
        float aoCurved = Mathf.Pow(aoBase, aoCurve);
        float aoDarkened = 1f - (1f - aoCurved) * aoStrength;
        float aoMinLight = world != null ? Mathf.Clamp01(world.aoMinLight) : 0f;
        return Mathf.Max(aoMinLight, Mathf.Clamp01(aoDarkened));
    }

    bool IsBreakVisualAOOccluder(World world, Vector3Int pos)
    {
        if (world == null || world.blockData == null || pos.y < 0 || pos.y >= Chunk.SizeY)
            return false;

        BlockType blockType = world.GetBlockAt(pos);
        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return world.GetBlockOpacity(blockType) >= 15;

        return CastsBreakVisualAmbientOcclusion(blockType, mappingResult.Value);
    }

    static bool CastsBreakVisualAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
    {
        if (!mapping.isSolid ||
            BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube ||
            mapping.isEmpty ||
            mapping.isLiquid ||
            mapping.lightOpacity == 0)
        {
            return false;
        }

        return !mapping.isTransparent || blockType == BlockType.Leaves;
    }

    static bool IsBreakVisualEmissive(BlockTextureMapping mapping)
    {
        return mapping.isLightSource || mapping.lightEmission > 0;
    }

    static bool TryGetBreakVisualFaceAxes(
        BlockFace worldFace,
        out int axis,
        out int normalSign,
        out Vector3Int normalOffset,
        out Vector3Int stepU,
        out Vector3Int stepV)
    {
        switch (worldFace)
        {
            case BlockFace.Right:
                axis = 0;
                normalSign = 1;
                break;
            case BlockFace.Left:
                axis = 0;
                normalSign = -1;
                break;
            case BlockFace.Top:
                axis = 1;
                normalSign = 1;
                break;
            case BlockFace.Bottom:
                axis = 1;
                normalSign = -1;
                break;
            case BlockFace.Front:
                axis = 2;
                normalSign = 1;
                break;
            case BlockFace.Back:
                axis = 2;
                normalSign = -1;
                break;
            default:
                axis = 0;
                normalSign = 0;
                normalOffset = Vector3Int.zero;
                stepU = Vector3Int.zero;
                stepV = Vector3Int.zero;
                return false;
        }

        int u = (axis + 1) % 3;
        int v = (axis + 2) % 3;
        normalOffset = GetBreakVisualAxisStep(axis) * normalSign;
        stepU = GetBreakVisualAxisStep(u);
        stepV = GetBreakVisualAxisStep(v);
        return true;
    }

    static Vector3Int GetBreakVisualAxisStep(int axis)
    {
        return axis switch
        {
            0 => Vector3Int.right,
            1 => Vector3Int.up,
            _ => Vector3Int.forward
        };
    }

    int EstimateBreakVisualSkyLight(World world, Vector3Int samplePos)
    {
        if (breakVisualSkyLightEstimateCache.TryGetValue(samplePos, out int cachedSkyLight))
            return cachedSkyLight;

        if (world == null || !world.enableVoxelLighting)
            return 15;
        if (samplePos.y >= Chunk.SizeY)
            return 15;
        if (samplePos.y < 0)
            return 0;
        if (HasDirectBreakVisualSkyAccess(world, samplePos))
            return 15;
        if (!world.enableHorizontalSkylight)
            return 0;

        int stepLoss = Mathf.Max(1, world.horizontalSkylightStepLoss);
        int maxSearchRadius = Mathf.Clamp(world.sunlightSmoothingPadding, 2, 32);
        int minX = samplePos.x - maxSearchRadius;
        int maxX = samplePos.x + maxSearchRadius;
        int minZ = samplePos.z - maxSearchRadius;
        int maxZ = samplePos.z + maxSearchRadius;
        int minY = samplePos.y;
        int maxY = Mathf.Min(Chunk.SizeY - 1, samplePos.y + maxSearchRadius);

        breakVisualSkyPropagationLight.Clear();
        breakVisualSkyPropagationQueue.Clear();

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                int horizontalDistance = Mathf.Abs(x - samplePos.x) + Mathf.Abs(z - samplePos.z);
                if (horizontalDistance > maxSearchRadius)
                    continue;

                for (int y = minY; y <= maxY; y++)
                {
                    Vector3Int candidate = new Vector3Int(x, y, z);
                    if (!HasDirectBreakVisualSkyAccess(world, candidate))
                        continue;

                    breakVisualSkyPropagationLight[candidate] = 15;
                    breakVisualSkyPropagationQueue.Enqueue(candidate);
                }
            }
        }

        while (breakVisualSkyPropagationQueue.Count > 0)
        {
            Vector3Int current = breakVisualSkyPropagationQueue.Dequeue();
            int currentLight = breakVisualSkyPropagationLight[current];
            if (current == samplePos)
                break;
            if (currentLight <= 1)
                continue;

            for (int i = 0; i < BreakVisualHorizontalLightDirections.Length; i++)
                TryPropagateBreakVisualSkyLight(current, BreakVisualHorizontalLightDirections[i], stepLoss, minX, maxX, minY, maxY, minZ, maxZ, world);

            TryPropagateBreakVisualSkyLight(current, Vector3Int.down, 1, minX, maxX, minY, maxY, minZ, maxZ, world);
        }

        int estimated = breakVisualSkyPropagationLight.TryGetValue(samplePos, out int propagatedSkyLight)
            ? Mathf.Clamp(propagatedSkyLight, 0, 15)
            : 0;
        breakVisualSkyLightEstimateCache[samplePos] = estimated;
        return estimated;
    }

    void TryPropagateBreakVisualSkyLight(
        Vector3Int current,
        Vector3Int direction,
        int baseLoss,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ,
        World world)
    {
        Vector3Int next = current + direction;
        if (next.x < minX || next.x > maxX ||
            next.y < minY || next.y > maxY ||
            next.z < minZ || next.z > maxZ)
        {
            return;
        }

        int currentLight = breakVisualSkyPropagationLight[current];
        int opacity = GetBreakVisualLightOpacity(world, next);
        int propagatedLight = Mathf.Max(0, currentLight - baseLoss - opacity);
        if (propagatedLight <= 0)
            return;

        if (breakVisualSkyPropagationLight.TryGetValue(next, out int existingLight) &&
            existingLight >= propagatedLight)
        {
            return;
        }

        breakVisualSkyPropagationLight[next] = propagatedLight;
        breakVisualSkyPropagationQueue.Enqueue(next);
    }

    int GetBreakVisualLightOpacity(World world, Vector3Int pos)
    {
        if (world == null || pos.y < 0 || pos.y >= Chunk.SizeY)
            return 0;

        return world.GetBlockOpacity(world.GetBlockAt(pos));
    }

    bool HasDirectBreakVisualSkyAccess(World world, Vector3Int samplePos)
    {
        if (breakVisualDirectSkyAccessCache.TryGetValue(samplePos, out bool cached))
            return cached;

        if (samplePos.y >= Chunk.SizeY)
            return true;
        if (samplePos.y < 0)
            return false;

        for (int y = samplePos.y; y < Chunk.SizeY; y++)
        {
            if (!IsBreakVisualLightTransparent(world, new Vector3Int(samplePos.x, y, samplePos.z)))
            {
                breakVisualDirectSkyAccessCache[samplePos] = false;
                return false;
            }
        }

        breakVisualDirectSkyAccessCache[samplePos] = true;
        return true;
    }

    bool IsBreakVisualLightTransparent(World world, Vector3Int pos)
    {
        if (world == null || pos.y < 0 || pos.y >= Chunk.SizeY)
            return true;

        BlockType blockType = world.GetBlockAt(pos);
        return world.GetBlockOpacity(blockType) < 15;
    }

    static Vector3Int NormalizeBreakVisualHitNormal(Vector3Int hitNormal)
    {
        int absX = Mathf.Abs(hitNormal.x);
        int absY = Mathf.Abs(hitNormal.y);
        int absZ = Mathf.Abs(hitNormal.z);

        if (absX >= absY && absX >= absZ && absX > 0)
            return new Vector3Int(hitNormal.x > 0 ? 1 : -1, 0, 0);

        if (absY >= absX && absY >= absZ && absY > 0)
            return new Vector3Int(0, hitNormal.y > 0 ? 1 : -1, 0);

        if (absZ > 0)
            return new Vector3Int(0, 0, hitNormal.z > 0 ? 1 : -1);

        return Vector3Int.zero;
    }

    void HideBreakVisualOverlay()
    {
        if (breakVisualObject != null && breakVisualObject.activeSelf)
            breakVisualObject.SetActive(false);

        if (breakVisualRenderer != null)
            breakVisualRenderer.SetPropertyBlock(null);

        breakVisualBlockPosition = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        breakVisualHitNormal = Vector3Int.zero;
    }

    Vector3 ComputeBreakShakeOffset(Vector3 center, float progress01)
    {
        float phase = Vector3.Dot(center, new Vector3(12.9898f, 78.233f, 37.719f));
        float t = Time.time * Mathf.Max(0.001f, breakShakeFrequency) + phase;
        Vector3 direction = new Vector3(
            Mathf.Sin(t * 1.31f),
            Mathf.Sin(t * 1.73f + 2f),
            Mathf.Cos(t * 1.11f + 0.6f));

        if (direction.sqrMagnitude <= 0.00001f)
            return Vector3.zero;

        float amplitude = Mathf.Max(0f, breakShakeStrength) * (0.45f + Mathf.Clamp01(progress01) * 0.75f);
        return direction.normalized * amplitude;
    }

    float ComputeBreakShakeScale(Vector3 center, float progress01)
    {
        float phase = Vector3.Dot(center, new Vector3(12.9898f, 78.233f, 37.719f));
        float t = Time.time * Mathf.Max(0.001f, breakShakeFrequency) + phase;
        float pulse = Mathf.Sin(t * 2.6f) * 0.5f + 0.5f;
        return 1f + Mathf.Max(0f, breakShakeScaleStrength) *
            (0.4f + Mathf.Clamp01(progress01) * 0.6f) *
            (0.65f + pulse * 0.35f);
    }

    void CancelBreak()
    {
        breakProgress01 = 0f;
        lastCrackStage = -1;
        breakingBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        breakingIsBillboard = false;

        if (crackOverlayObject != null && crackOverlayObject.activeSelf)
            crackOverlayObject.SetActive(false);

        ClearBreakShaderEffect();
    }

    void OnDisable()
    {
        CancelBreak();
    }

    void OnDestroy()
    {
        if (crackOverlayRuntimeMaterial != null)
            Destroy(crackOverlayRuntimeMaterial);

        if (crackOverlayObject != null)
            Destroy(crackOverlayObject);

        if (breakVisualMesh != null)
            Destroy(breakVisualMesh);

        if (breakVisualObject != null)
            Destroy(breakVisualObject);
    }

    void HandlePlaceBlock()
    {
        if (Input.GetMouseButtonDown(1))
        {
            FurnaceUIController furnaceUI = FurnaceUIController.Instance != null
                ? FurnaceUIController.Instance
                : FindAnyObjectByType<FurnaceUIController>();
            if (furnaceUI != null &&
                furnaceUI.TryHandleFurnaceInteraction(selector))
            {
                CancelBreak();
                return;
            }

            CraftingStationUIController craftingStationUI = CraftingStationUIController.EnsureInstance();
            if (craftingStationUI != null &&
                craftingStationUI.TryHandleCrafterInteraction(selector))
            {
                CancelBreak();
                return;
            }

            CancelBreak();

            if (!selector.TryGetSelectedBlock(out Vector3Int targetBlock, out Vector3Int hitNormal))
                return;

            BlockType targetType = World.Instance.GetBlockAt(targetBlock);

            BlockType selectedBlockType = placeBlockType;
            if (hotbar != null && !hotbar.TryGetSelectedBlockType(out selectedBlockType))
                return;

            bool replaceTarget = selector.IsBillboardHit || IsLiquid(targetType);

            // Billboard e liquidos: substitui exatamente a celula alvo (estilo Minecraft).
            Vector3Int placePos = replaceTarget ? targetBlock : targetBlock + hitNormal;

            if (placePos.y <= 2)
                return;

            BlockType placedBlockType = TorchPlacementUtility.GetPlacementBlockType(selectedBlockType, hitNormal);
            BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);

            Vector3 lookForward = ResolvePlacementLookForward();
            Vector3 hitPoint = selector != null ? selector.CurrentHitPoint : targetBlock + Vector3.one * 0.5f;
            BlockPlacementAxis placementAxis = World.Instance.ResolvePlacementAxisForPlacement(
                placedBlockType,
                hitNormal,
                lookForward,
                hitPoint);

            bool canMergeWireState = placedBlockType == BlockType.wire &&
                                     blockAtPlacePos == BlockType.wire &&
                                     World.Instance.CanPlaceWireStateAt(placePos, placementAxis);

            if (blockAtPlacePos != BlockType.Air && !IsLiquid(blockAtPlacePos) && !canMergeWireState)
                return;

            if (TorchPlacementUtility.IsTorchLike(placedBlockType) && !CanPlaceTorchAt(placePos, placedBlockType))
                return;

            if (placedBlockType == BlockType.wire && !CanPlaceWireAt(placePos, hitNormal))
                return;

            if (preventPlaceInsidePlayer &&
                ShouldPreventPlacementInsidePlayer(placedBlockType) &&
                IsBlockIntersectingPlayer(placePos, placedBlockType, placementAxis))
            {
                return;
            }

            if (hotbar != null && !hotbar.TryConsumeSelected(1))
                return;

            if (placedBlockType == BlockType.wire)
            {
                if (!World.Instance.TryPlaceWireStateAt(placePos, placementAxis, true))
                    return;
            }
            else
            {
                World.Instance.SetBlockAt(placePos, placedBlockType, true, placementAxis);
            }

            if (placeBlockClip != null)
                audioSource.PlayOneShot(placeBlockClip);
        }
    }

    private Vector3 ResolvePlacementLookForward()
    {
        // Use player body yaw as the primary facing source.
        // This keeps placement rotation stable when camera pitch is near vertical.
        Vector3 bodyForward = transform.forward;
        Vector3 horizontalLook = new Vector3(bodyForward.x, 0f, bodyForward.z);
        if (horizontalLook.sqrMagnitude >= 0.0001f)
            return horizontalLook.normalized;

        // Fallback for unusual rigs where player root forward is degenerate.
        if (cam != null)
        {
            Vector3 cameraForward = cam.transform.forward;
            Vector3 cameraHorizontal = new Vector3(cameraForward.x, 0f, cameraForward.z);
            if (cameraHorizontal.sqrMagnitude >= 0.0001f)
                return cameraHorizontal.normalized;
        }

        return Vector3.forward;
    }

    private bool CanPlaceTorchAt(Vector3Int placePos, BlockType placedBlockType)
    {
        if (!TorchPlacementUtility.IsTorchLike(placedBlockType))
            return true;

        Vector3Int supportPos = placePos + TorchPlacementUtility.GetSupportDirection(placedBlockType);
        BlockType supportType = World.Instance.GetBlockAt(supportPos);
        return CanTorchAttachTo(supportType);
    }

    private bool CanTorchAttachTo(BlockType supportType)
    {
        if (supportType == BlockType.Air || IsLiquid(supportType))
            return false;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return supportType != BlockType.Air && !FluidBlockUtility.IsWater(supportType);

        BlockTextureMapping? mapping = world.blockData.GetMapping(supportType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty;
    }

    private bool ShouldPreventPlacementInsidePlayer(BlockType blockType)
    {
        if (blockType == BlockType.Air || IsLiquid(blockType))
            return false;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return true;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null)
            return true;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty;
    }

    private bool CanPlaceWireAt(Vector3Int placePos, Vector3Int hitNormal)
    {
        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        Vector3Int supportPos = placePos - hitNormal;
        BlockType supportType = world.GetBlockAt(supportPos);
        if (supportType == BlockType.wire || supportType == BlockType.Air || IsLiquid(supportType))
            return false;

        BlockTextureMapping? mapping = world.blockData.GetMapping(supportType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty && !value.isLiquid;
    }

    private bool IsBlockIntersectingPlayer(Vector3Int placePos, BlockType blockType, BlockPlacementAxis placementAxis)
    {
        CharacterController characterController = GetComponent<CharacterController>();
        if (characterController != null)
        {
            Vector3 worldCenter = transform.TransformPoint(characterController.center);
            float height = Mathf.Max(characterController.height, 0.1f);
            float diameter = Mathf.Max(characterController.radius * 2f, 0.1f);
            Bounds playerBounds = new Bounds(worldCenter, new Vector3(diameter, height, diameter));
            return DoesBlockIntersectBounds(placePos, blockType, placementAxis, playerBounds);
        }

        float clampedRadius = Mathf.Max(0.1f, fallbackPlayerRadius);
        float clampedHeight = Mathf.Max(0.5f, fallbackPlayerHeight);
        Vector3 fallbackCenter = transform.position + Vector3.up * (clampedHeight * 0.5f);
        Bounds fallbackBounds = new Bounds(fallbackCenter, new Vector3(clampedRadius * 2f, clampedHeight, clampedRadius * 2f));
        return DoesBlockIntersectBounds(placePos, blockType, placementAxis, fallbackBounds);
    }

    private bool DoesBlockIntersectBounds(Vector3Int blockPos, BlockType blockType, BlockPlacementAxis placementAxis, Bounds testBounds)
    {
        World world = World.Instance;
        if (world == null || world.blockData == null)
            return testBounds.Intersects(new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one));

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return testBounds.Intersects(new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one));

        BlockTextureMapping mapping = mappingResult.Value;
        switch (BlockShapeUtility.GetEffectiveRenderShape(mapping))
        {
            case BlockRenderShape.Stairs:
            {
                StairShapeVariant variant = StairShapeRuntimeUtility.ResolveShapeVariant(world, blockPos, (byte)placementAxis);
                StairShapeUtility.ResolveBoxes((byte)placementAxis, variant, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
                if (testBounds.Intersects(box0.ToWorldBounds(blockPos)))
                    return true;
                if (boxCount > 1 && testBounds.Intersects(box1.ToWorldBounds(blockPos)))
                    return true;
                if (boxCount > 2 && testBounds.Intersects(box2.ToWorldBounds(blockPos)))
                    return true;
                if (boxCount > 3 && testBounds.Intersects(box3.ToWorldBounds(blockPos)))
                    return true;
                if (boxCount > 4 && testBounds.Intersects(box4.ToWorldBounds(blockPos)))
                    return true;
                return false;
            }

            case BlockRenderShape.Ramp:
            {
                BlockPlacementAxis rampAxis = RampShapeUtility.SanitizeAxis(placementAxis);
                RampShapeVariant rampVariant = RampShapeRuntimeUtility.ResolveShapeVariant(world, blockPos, rampAxis);
                var rampBoxes = RampShapeUtility.BuildColliderBoxes(rampAxis, rampVariant);
                for (int i = 0; i < rampBoxes.Length; i++)
                {
                    if (testBounds.Intersects(rampBoxes[i].ToWorldBounds(blockPos)))
                        return true;
                }

                return false;
            }

            case BlockRenderShape.VerticalRamp:
            {
                BlockPlacementAxis verticalRampAxis = VerticalRampShapeUtility.SanitizeAxis(placementAxis);
                var verticalRampBoxes = VerticalRampShapeUtility.BuildColliderBoxes(verticalRampAxis);
                for (int i = 0; i < verticalRampBoxes.Length; i++)
                {
                    if (testBounds.Intersects(verticalRampBoxes[i].ToWorldBounds(blockPos)))
                        return true;
                }

                return false;
            }

            case BlockRenderShape.Fence:
            {
                byte connectionMask = FenceShapeUtility.ResolveConnectionMask(world, blockPos);
                if (testBounds.Intersects(FenceShapeUtility.GetCenterPostColliderBox().ToWorldBounds(blockPos)))
                    return true;

                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest) &&
                    testBounds.Intersects(FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectWest).ToWorldBounds(blockPos)))
                    return true;
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast) &&
                    testBounds.Intersects(FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectEast).ToWorldBounds(blockPos)))
                    return true;
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth) &&
                    testBounds.Intersects(FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectSouth).ToWorldBounds(blockPos)))
                    return true;
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth) &&
                    testBounds.Intersects(FenceShapeUtility.GetArmColliderBox(FenceShapeUtility.ConnectNorth).ToWorldBounds(blockPos)))
                    return true;
                return false;
            }

            default:
                return testBounds.Intersects(ResolveBlockBounds(blockPos, blockType, placementAxis));
        }
    }

    private Bounds ResolveBlockBounds(Vector3Int blockPos, BlockType blockType)
    {
        World world = World.Instance;
        BlockPlacementAxis placementAxis = world != null
            ? world.GetPlacementAxisAt(blockPos, blockType)
            : BlockPlacementAxis.Y;
        return ResolveBlockBounds(blockPos, blockType, placementAxis);
    }

    private Bounds ResolveBlockBounds(Vector3Int blockPos, BlockType blockType, BlockPlacementAxis placementAxis)
    {
        World world = World.Instance;
        if (world == null || world.blockData == null)
            return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null)
            return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);

        BlockTextureMapping value = mapping.Value;
        if (!BlockShapeUtility.UsesCustomMesh(value))
            return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);

        switch (BlockShapeUtility.GetEffectiveRenderShape(value))
        {
            case BlockRenderShape.Stairs:
            {
                StairShapeVariant variant = StairShapeRuntimeUtility.ResolveShapeVariant(world, blockPos, (byte)placementAxis);
                StairShapeUtility.ResolveBoxes((byte)placementAxis, variant, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
                Bounds bounds = box0.ToWorldBounds(blockPos);
                if (boxCount > 1) bounds.Encapsulate(box1.ToWorldBounds(blockPos));
                if (boxCount > 2) bounds.Encapsulate(box2.ToWorldBounds(blockPos));
                if (boxCount > 3) bounds.Encapsulate(box3.ToWorldBounds(blockPos));
                if (boxCount > 4) bounds.Encapsulate(box4.ToWorldBounds(blockPos));
                return bounds;
            }

            case BlockRenderShape.Ramp:
            {
                BlockPlacementAxis rampAxis = RampShapeUtility.SanitizeAxis(placementAxis);
                RampShapeVariant rampVariant = RampShapeRuntimeUtility.ResolveShapeVariant(world, blockPos, rampAxis);
                var rampBoxes = RampShapeUtility.BuildColliderBoxes(rampAxis, rampVariant);
                Bounds bounds = rampBoxes[0].ToWorldBounds(blockPos);
                for (int i = 1; i < rampBoxes.Length; i++)
                    bounds.Encapsulate(rampBoxes[i].ToWorldBounds(blockPos));
                return bounds;
            }

            case BlockRenderShape.VerticalRamp:
            {
                BlockPlacementAxis verticalRampAxis = VerticalRampShapeUtility.SanitizeAxis(placementAxis);
                var verticalRampBoxes = VerticalRampShapeUtility.BuildColliderBoxes(verticalRampAxis);
                Bounds bounds = verticalRampBoxes[0].ToWorldBounds(blockPos);
                for (int i = 1; i < verticalRampBoxes.Length; i++)
                    bounds.Encapsulate(verticalRampBoxes[i].ToWorldBounds(blockPos));
                return bounds;
            }

            case BlockRenderShape.Fence:
            {
                byte connectionMask = FenceShapeUtility.ResolveConnectionMask(world, blockPos);
                Bounds bounds = FenceShapeUtility.GetCenterPostVisualBox().ToWorldBounds(blockPos);
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, false).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, true).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, false).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, true).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, false).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, true).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, false).ToWorldBounds(blockPos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, true).ToWorldBounds(blockPos));
                return bounds;
            }
        }

        return BlockShapeUtility.GetWorldBounds(blockPos, blockType, value, placementAxis);
    }
}
