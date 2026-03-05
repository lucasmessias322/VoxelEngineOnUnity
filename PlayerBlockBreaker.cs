// PlayerBlockBreaker.cs
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(BlockSelector))]
[RequireComponent(typeof(AudioSource))]
public class PlayerBlockBreaker : MonoBehaviour
{
    public BlockSelector selector;
    public Camera cam;
    [Header("Place settings")]
    public BlockType placeBlockType = BlockType.Stone; // tipo a ser colocado (ajuste no Inspector)

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
    private float breakProgress01;
    private int lastCrackStage = -1;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (selector == null) selector = GetComponent<BlockSelector>();
        if (cam == null && selector != null) cam = selector.cam;

        CreateCrackOverlay();
    }

    void Update()
    {
        for (int i = 1; i <= 9; i++)
        {
            if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + (i - 1))))
            {
                if (i == 1) placeBlockType = BlockType.Stone;
                else if (i == 2) placeBlockType = BlockType.Dirt;
                else if (i == 3) placeBlockType = BlockType.Grass;
                else if (i == 4) placeBlockType = BlockType.oak_planks;
                else if (i == 5) placeBlockType = BlockType.Log;
                else if (i == 6) placeBlockType = BlockType.glowstone;
                else if (i == 7) placeBlockType = BlockType.glass;
                else if (i == 8) placeBlockType = BlockType.Snow;
                else if (i == 9) placeBlockType = BlockType.Leaves;

                Debug.Log($"Selected block type for placing: {placeBlockType}");
            }
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
            // Billboard de grama nao existe como voxel real.
            // Suprime apenas o billboard nessa celula, mantendo o bloco de grama-base.
            if (Input.GetMouseButtonDown(0))
            {
                World.Instance.SuppressGrassBillboardAt(sel);
                if (breakBlockClip != null)
                    audioSource.PlayOneShot(breakBlockClip);
            }

            CancelBreak();
            return;
        }

        BlockType current = World.Instance.GetBlockAt(sel);
        if (!CanBreak(current))
        {
            CancelBreak();
            return;
        }

        if (sel != breakingBlock)
        {
            breakingBlock = sel;
            breakProgress01 = 0f;
            lastCrackStage = -1;
        }

        breakProgress01 += Time.deltaTime / Mathf.Max(0.05f, breakDurationSeconds);
        UpdateCrackOverlay(sel, breakProgress01);

        if (breakProgress01 < 1f)
            return;

        World.Instance.SetBlockAt(sel, BlockType.Air);
        Debug.Log($"Break request at {sel} -> success");
        Debug.Log($"Break request at {sel} -> queued");

        if (breakBlockClip != null)
            audioSource.PlayOneShot(breakBlockClip);

        CancelBreak();
    }

    bool CanBreak(BlockType blockType)
    {
        return blockType != BlockType.Bedrock &&
               blockType != BlockType.Air &&
               blockType != BlockType.Water;
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
        crackOverlayRuntimeMaterial.mainTexture = tex;
        lastCrackStage = stage;
    }

    void CancelBreak()
    {
        breakProgress01 = 0f;
        lastCrackStage = -1;
        breakingBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

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

            // Se o alvo for billboard, coloca exatamente na celula do billboard (substitui).
            Vector3Int placePos = selector.IsBillboardHit
                ? targetBlock
                : targetBlock + hitNormal;

            if (placePos.y <= 2)
                return;

            BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);
            if (blockAtPlacePos != BlockType.Air && blockAtPlacePos != BlockType.Water)
                return;

            Vector3 blockCenter = placePos + Vector3.one * 0.5f;
            Vector3 halfExtents = Vector3.one * 0.5f;

            Collider[] hits = Physics.OverlapBox(blockCenter, halfExtents);

            foreach (var col in hits)
            {
                if (col.transform == transform)
                {
                    return;
                }
            }

            World.Instance.SetBlockAt(placePos, placeBlockType);
            if (placeBlockClip != null)
                audioSource.PlayOneShot(placeBlockClip);
        }
    }
}
