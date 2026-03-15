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

            breakProgress01 += Time.deltaTime / Mathf.Max(0.05f, breakDurationSeconds);
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

        breakProgress01 += Time.deltaTime / Mathf.Max(0.05f, breakDurationSeconds);
        UpdateCrackOverlay(sel, breakProgress01);

        if (breakProgress01 < 1f)
            return;

        Vector3 throwDir = cam != null ? cam.transform.forward : transform.forward;
        bool spawnedDrop = BlockDrop.Spawn(World.Instance, sel, current, throwDir);
        World.Instance.SetBlockAt(sel, BlockType.Air);

        if (!spawnedDrop)
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

    bool IsLiquid(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return blockType == BlockType.Water;

        return world.blockData.IsLiquid(blockType);
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
        crackOverlayObject.transform.position = blockPos + Vector3.one * 0.5f;
        crackOverlayObject.transform.localScale = Vector3.one * crackOverlayScale;

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

            BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);
            if (blockAtPlacePos != BlockType.Air && !IsLiquid(blockAtPlacePos))
                return;

            if (preventPlaceInsidePlayer && IsBlockIntersectingPlayer(placePos))
                return;

            if (hotbar != null && !hotbar.TryConsumeSelected(1))
                return;

            World.Instance.SetBlockAt(placePos, selectedBlockType);
            if (placeBlockClip != null)
                audioSource.PlayOneShot(placeBlockClip);
        }
    }

    private bool IsBlockIntersectingPlayer(Vector3Int placePos)
    {
        Bounds blockBounds = new Bounds(placePos + Vector3.one * 0.5f, Vector3.one);

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
}
