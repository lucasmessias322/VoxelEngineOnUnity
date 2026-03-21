// PlayerBlockBreaker.cs
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(BlockSelector))]
[RequireComponent(typeof(AudioSource))]
public class PlayerBlockBreaker : MonoBehaviour
{
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
    [Range(1.001f, 1.1f)] public float crackOverlayScale = 1.01f;

    private GameObject crackOverlayObject;
    private MeshRenderer crackOverlayRenderer;
    private Material crackOverlayRuntimeMaterial;
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

        if (!selector.TryGetSelectedBlock(out Vector3Int sel, out _))
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

            float breakDuration = GetBreakDurationSeconds(GetBillboardBreakType());
            breakProgress01 += Time.deltaTime / breakDuration;
            UpdateCrackOverlay(sel, breakProgress01);

            if (breakProgress01 < 1f)
                return;

            World.Instance.SuppressGrassBillboardAt(sel);
            if (breakBlockClip != null)
                audioSource.PlayOneShot(breakBlockClip);

            CancelBreak();
            return;
        }

        BlockType current = World.Instance.GetBlockAt(sel);
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
        UpdateCrackOverlay(sel, breakProgress01);

        if (breakProgress01 < 1f)
            return;

        bool shouldDrop = ShouldDropBlock(current);
        Vector3 throwDir = cam != null ? cam.transform.forward : transform.forward;
        bool spawnedDrop = shouldDrop && BlockDrop.Spawn(World.Instance, sel, current, throwDir);
        World.Instance.SetBlockAt(sel, BlockType.Air);

        if (shouldDrop && !spawnedDrop)
        {
            bool addedToInventory = PlayerInventory.Instance != null &&
                                    PlayerInventory.Instance.TryAddBlockDrop(current, 1);
            if (!addedToInventory)
            {
                Debug.LogWarning($"[PlayerBlockBreaker] Falha ao gerar drop de {current} em {sel}.");
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

    BlockType GetBillboardBreakType()
    {
        if (World.Instance != null)
            return World.Instance.grassBillboardBlockType;

        return BlockType.Leaves;
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
            case BlockType.RedstoneOre:
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
            case BlockType.RedstoneOre:
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

    void UpdateCrackOverlay(Vector3Int blockPos, float progress01)
    {
        if (crackOverlayObject == null || crackOverlayRenderer == null || crackOverlayRuntimeMaterial == null)
            return;

        crackOverlayObject.SetActive(true);

        World world = World.Instance;
        BlockType overlayType = breakingIsBillboard || world == null
            ? GetBillboardBreakType()
            : world.GetBlockAt(blockPos);

        Bounds overlayBounds = ResolveBlockBounds(blockPos, overlayType);
        crackOverlayObject.transform.position = overlayBounds.center;
        crackOverlayObject.transform.localScale = overlayBounds.size * crackOverlayScale;

        if (breakCrackStages == null || breakCrackStages.Length == 0)
            return;

        int stageCount = breakCrackStages.Length;
        int stage = Mathf.Clamp(Mathf.FloorToInt(progress01 * stageCount), 0, stageCount - 1);
        if (stage == lastCrackStage)
            return;

        Texture2D tex = breakCrackStages[stage];
        crackOverlayRuntimeMaterial.SetTexture("_CrackTex", tex);
        lastCrackStage = stage;
    }

    void CancelBreak()
    {
        breakProgress01 = 0f;
        lastCrackStage = -1;
        breakingBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        breakingIsBillboard = false;

        if (crackOverlayObject != null && crackOverlayObject.activeSelf)
            crackOverlayObject.SetActive(false);
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
    }

    void HandlePlaceBlock()
    {
        if (Input.GetMouseButtonDown(1))
        {
            if (CraftingStationUIController.Instance != null &&
                CraftingStationUIController.Instance.TryHandleCrafterInteraction(selector))
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
            if (blockAtPlacePos != BlockType.Air && !IsLiquid(blockAtPlacePos))
                return;

            if (TorchPlacementUtility.IsTorchLike(placedBlockType) && !CanPlaceTorchAt(placePos, placedBlockType))
                return;

            if (preventPlaceInsidePlayer && IsBlockIntersectingPlayer(placePos, placedBlockType))
                return;

            if (hotbar != null && !hotbar.TryConsumeSelected(1))
                return;

            World.Instance.SetBlockAt(placePos, placedBlockType, true);
            if (placeBlockClip != null)
                audioSource.PlayOneShot(placeBlockClip);
        }
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

    private bool IsBlockIntersectingPlayer(Vector3Int placePos, BlockType blockType)
    {
        Bounds blockBounds = ResolveBlockBounds(placePos, blockType);

        CharacterController characterController = GetComponent<CharacterController>();
        if (characterController != null)
        {
            Vector3 worldCenter = transform.TransformPoint(characterController.center);
            float height = Mathf.Max(characterController.height, 0.1f);
            float diameter = Mathf.Max(characterController.radius * 2f, 0.1f);
            Bounds playerBounds = new Bounds(worldCenter, new Vector3(diameter, height, diameter));
            return playerBounds.Intersects(blockBounds);
        }

        float clampedRadius = Mathf.Max(0.1f, fallbackPlayerRadius);
        float clampedHeight = Mathf.Max(0.5f, fallbackPlayerHeight);
        Vector3 fallbackCenter = transform.position + Vector3.up * (clampedHeight * 0.5f);
        Bounds fallbackBounds = new Bounds(fallbackCenter, new Vector3(clampedRadius * 2f, clampedHeight, clampedRadius * 2f));
        return fallbackBounds.Intersects(blockBounds);
    }

    private Bounds ResolveBlockBounds(Vector3Int blockPos, BlockType blockType)
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

        return BlockShapeUtility.GetWorldBounds(blockPos, blockType, value);
    }
}
