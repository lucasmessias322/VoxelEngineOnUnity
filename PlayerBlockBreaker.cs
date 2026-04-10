// PlayerBlockBreaker.cs
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(BlockSelector))]
[RequireComponent(typeof(AudioSource))]
public class PlayerBlockBreaker : MonoBehaviour
{
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
        UpdateBreakShaderEffect(sel, current, breakProgress01);
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

    void UpdateBreakShaderEffect(Vector3Int blockPos, BlockType blockType, float progress01)
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

        UpdateBreakVisualOverlay(blockPos, blockType, bounds, shakeOffset, shakeScale);

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
            breakVisualMesh.vertexCount == 0)
        {
            BuildBreakVisualCubeMesh(blockPos, blockType, mapping, placementAxis, bounds.size);
            breakVisualBlockType = blockType;
            breakVisualPlacementAxis = placementAxis;
            breakVisualSubchunkIndex = subchunkIndex;
            breakVisualBlockPosition = blockPos;
        }

        if (breakVisualRenderer.sharedMaterial != overlayMaterial)
            breakVisualRenderer.sharedMaterial = overlayMaterial;

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

    void BuildBreakVisualCubeMesh(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Vector3 size)
    {
        if (breakVisualMesh == null)
            return;

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
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(max.x, min.y, max.z),
            Vector3.right,
            Vector3Int.right,
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
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, min.y, min.z),
            Vector3.left,
            Vector3Int.left,
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
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(max.x, max.y, min.z),
            Vector3.up,
            Vector3Int.up,
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
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            Vector3.down,
            Vector3Int.down,
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
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(min.x, min.y, max.z),
            Vector3.forward,
            Vector3Int.forward,
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
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, min.y, min.z),
            Vector3.back,
            Vector3Int.back,
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
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 normal,
        Vector3Int normalOffset,
        BlockFace worldFace,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        float invAtlasTilesX,
        float invAtlasTilesY,
        int faceSubchunkIndex)
    {
        BlockFace sampledFace = BlockPlacementRotationUtility.ResolveFaceForPlacement(mapping, worldFace, placementAxis);
        Vector2Int tile = mapping.GetTileCoord(sampledFace);
        Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
        float tintMask = mapping.GetTint(sampledFace) ? 1f : 0f;
        bool grassSideOverlay = blockType == BlockType.Grass &&
                                sampledFace != BlockFace.Top &&
                                sampledFace != BlockFace.Bottom;
        float packedSubchunkAndOverlay = faceSubchunkIndex + (grassSideOverlay ? 0.25f : 0f);
        World world = World.Instance;
        float faceLight01 = ResolveBreakVisualFaceLight01(world, blockPos, blockType, normalOffset);
        float ao0 = ResolveBreakVisualVertexAO01(world, blockPos, mapping, normalOffset, p0);
        float ao1 = ResolveBreakVisualVertexAO01(world, blockPos, mapping, normalOffset, p1);
        float ao2 = ResolveBreakVisualVertexAO01(world, blockPos, mapping, normalOffset, p2);
        float ao3 = ResolveBreakVisualVertexAO01(world, blockPos, mapping, normalOffset, p3);
        Vector4 extra0 = new Vector4(faceLight01, tintMask, ao0, packedSubchunkAndOverlay);
        Vector4 extra1 = new Vector4(faceLight01, tintMask, ao1, packedSubchunkAndOverlay);
        Vector4 extra2 = new Vector4(faceLight01, tintMask, ao2, packedSubchunkAndOverlay);
        Vector4 extra3 = new Vector4(faceLight01, tintMask, ao3, packedSubchunkAndOverlay);

        vertices[vertexStart + 0] = p0;
        vertices[vertexStart + 1] = p1;
        vertices[vertexStart + 2] = p2;
        vertices[vertexStart + 3] = p3;

        normals[vertexStart + 0] = normal;
        normals[vertexStart + 1] = normal;
        normals[vertexStart + 2] = normal;
        normals[vertexStart + 3] = normal;

        uv0[vertexStart + 0] = new Vector2(0f, 0f);
        uv0[vertexStart + 1] = new Vector2(0f, 1f);
        uv0[vertexStart + 2] = new Vector2(1f, 1f);
        uv0[vertexStart + 3] = new Vector2(1f, 0f);

        uv1[vertexStart + 0] = atlasUv;
        uv1[vertexStart + 1] = atlasUv;
        uv1[vertexStart + 2] = atlasUv;
        uv1[vertexStart + 3] = atlasUv;

        uv2[vertexStart + 0] = extra0;
        uv2[vertexStart + 1] = extra1;
        uv2[vertexStart + 2] = extra2;
        uv2[vertexStart + 3] = extra3;

        triangles[triangleStart + 0] = vertexStart + 0;
        triangles[triangleStart + 1] = vertexStart + 1;
        triangles[triangleStart + 2] = vertexStart + 2;
        triangles[triangleStart + 3] = vertexStart + 0;
        triangles[triangleStart + 4] = vertexStart + 2;
        triangles[triangleStart + 5] = vertexStart + 3;

        vertexStart += 4;
        triangleStart += 6;
    }

    float ResolveBreakVisualFaceLight01(World world, Vector3Int blockPos, BlockType blockType, Vector3Int normalOffset)
    {
        if (world == null || !world.enableVoxelLighting)
            return 1f;

        Vector3Int samplePos = blockPos + normalOffset;
        int blockLight = world.GetGlobalBlockLightAt(samplePos);
        int skyLight = EstimateBreakVisualSkyLight(world, samplePos);
        int emission = world.GetBlockEmission(blockType);
        int resolvedLight = Mathf.Max(blockLight, skyLight, emission);
        return Mathf.Clamp01(resolvedLight / 15f);
    }

    int EstimateBreakVisualSkyLight(World world, Vector3Int samplePos)
    {
        if (samplePos.y >= Chunk.SizeY)
            return 15;
        if (samplePos.y < 0)
            return 0;
        if (HasDirectBreakVisualSkyAccess(world, samplePos))
            return 15;
        if (!world.enableHorizontalSkylight)
            return 0;

        int best = 0;
        int stepLoss = Mathf.Max(1, world.horizontalSkylightStepLoss);
        const int maxHorizontalSearchRadius = 2;

        for (int radius = 1; radius <= maxHorizontalSearchRadius; radius++)
        {
            foreach (Vector3Int direction in BreakVisualHorizontalLightDirections)
            {
                Vector3Int candidate = samplePos + direction * radius;
                if (!HasTransparentBreakVisualPath(world, samplePos, direction, radius) ||
                    !HasDirectBreakVisualSkyAccess(world, candidate))
                {
                    continue;
                }

                best = Mathf.Max(best, Mathf.Max(0, 15 - stepLoss * radius));
            }
        }

        return best;
    }

    bool HasDirectBreakVisualSkyAccess(World world, Vector3Int samplePos)
    {
        if (samplePos.y >= Chunk.SizeY)
            return true;
        if (samplePos.y < 0)
            return false;

        for (int y = samplePos.y; y < Chunk.SizeY; y++)
        {
            if (!IsBreakVisualLightTransparent(world, new Vector3Int(samplePos.x, y, samplePos.z)))
                return false;
        }

        return true;
    }

    bool HasTransparentBreakVisualPath(World world, Vector3Int origin, Vector3Int direction, int distance)
    {
        for (int step = 0; step <= distance; step++)
        {
            if (!IsBreakVisualLightTransparent(world, origin + direction * step))
                return false;
        }

        return true;
    }

    bool IsBreakVisualLightTransparent(World world, Vector3Int pos)
    {
        if (pos.y < 0 || pos.y >= Chunk.SizeY)
            return true;

        BlockType blockType = world.GetBlockAt(pos);
        return world.GetBlockOpacity(blockType) < 15;
    }

    float ResolveBreakVisualVertexAO01(
        World world,
        Vector3Int blockPos,
        BlockTextureMapping mapping,
        Vector3Int normalOffset,
        Vector3 localVertex)
    {
        if (world == null ||
            !world.enableAmbientOcclusion ||
            world.aoStrength <= 0f ||
            IsBreakVisualEmissive(mapping))
        {
            return 1f;
        }

        ResolveBreakVisualCornerDirections(normalOffset, localVertex, out Vector3Int d1, out Vector3Int d2);
        int rawAO = ResolveBreakVisualVertexAO(world, blockPos + normalOffset, d1, d2);
        float aoBase = Mathf.Clamp01(rawAO / 3f);
        float aoCurve = world.aoCurveExponent > 0f ? world.aoCurveExponent : BreakVisualDefaultAOCurveExponent;
        float aoCurved = Mathf.Pow(aoBase, aoCurve);
        float aoDarkened = 1f - (1f - aoCurved) * Mathf.Max(0f, world.aoStrength);
        return Mathf.Max(Mathf.Clamp01(world.aoMinLight), Mathf.Clamp01(aoDarkened));
    }

    int ResolveBreakVisualVertexAO(World world, Vector3Int pos, Vector3Int d1, Vector3Int d2)
    {
        bool s1 = IsBreakVisualAOOccluder(world, pos + d1);
        bool s2 = IsBreakVisualAOOccluder(world, pos + d2);
        bool corner = IsBreakVisualAOOccluder(world, pos + d1 + d2);

        if (s1 && s2)
            return 0;

        return 3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (corner ? 1 : 0);
    }

    void ResolveBreakVisualCornerDirections(
        Vector3Int normalOffset,
        Vector3 localVertex,
        out Vector3Int d1,
        out Vector3Int d2)
    {
        if (normalOffset.x != 0)
        {
            d1 = localVertex.y >= 0f ? Vector3Int.up : Vector3Int.down;
            d2 = localVertex.z >= 0f ? Vector3Int.forward : Vector3Int.back;
            return;
        }

        if (normalOffset.y != 0)
        {
            d1 = localVertex.x >= 0f ? Vector3Int.right : Vector3Int.left;
            d2 = localVertex.z >= 0f ? Vector3Int.forward : Vector3Int.back;
            return;
        }

        d1 = localVertex.x >= 0f ? Vector3Int.right : Vector3Int.left;
        d2 = localVertex.y >= 0f ? Vector3Int.up : Vector3Int.down;
    }

    bool IsBreakVisualAOOccluder(World world, Vector3Int pos)
    {
        if (world == null || world.blockData == null || pos.y < 0 || pos.y >= Chunk.SizeY)
            return false;

        BlockType blockType = world.GetBlockAt(pos);
        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return world.GetBlockOpacity(blockType) >= 15;

        BlockTextureMapping mapping = mappingResult.Value;
        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube ||
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

    void HideBreakVisualOverlay()
    {
        if (breakVisualObject != null && breakVisualObject.activeSelf)
            breakVisualObject.SetActive(false);

        breakVisualBlockPosition = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
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
            BlockPlacementAxis placementAxis = World.Instance.ResolvePlacementAxisForPlacement(
                placedBlockType,
                hitNormal,
                lookForward);

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
        Bounds blockBounds = ResolveBlockBounds(placePos, blockType, placementAxis);

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

        return BlockShapeUtility.GetWorldBounds(blockPos, blockType, value, placementAxis);
    }
}
