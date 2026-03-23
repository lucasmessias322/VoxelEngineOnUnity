using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(World))]
public class WorldLoadingBootstrap : MonoBehaviour
{
    [Header("Bootstrap")]
    [SerializeField] private World world;
    [SerializeField] private bool runInitialWorldBootstrap = true;
    [SerializeField, Min(0)] private int initialSpawnSearchRadius = 48;
    [SerializeField] private float initialSpawnYOffset = 0.08f;

    [Header("Loading Screen")]
    [SerializeField] private bool enableLoadingScreen = true;
    [SerializeField] private bool autoCreateLoadingOverlay = true;
    [SerializeField] private GameObject loadingScreenRoot;
    [SerializeField] private TMP_Text loadingStatusText;
    [SerializeField] private TMP_Text loadingPhaseText;
    [SerializeField] private Slider loadingProgressBar;
    [SerializeField] private Image loadingProgressFill;
    [SerializeField] private TMP_Text loadingProgressPercentText;

    private FPSController cachedPlayerController;
    private CharacterController cachedPlayerCharacterController;
    private Transform controlledPlayerTransform;
    private Vector3 initialLoadingAnchorPosition;
    private Vector2Int initialLoadCenterChunk;
    private bool initialBootstrapActive;
    private bool initialWorldReady = true;
    private bool runtimeLoadingScreenCreated;
    private float initialLoadProgress01 = 1f;
    private bool bootstrapInitialized;

    public bool IsInitialWorldReady => !runInitialWorldBootstrap || initialWorldReady;
    public bool IsInitialWorldLoading => initialBootstrapActive;
    public float InitialLoadProgress01 => initialLoadProgress01;

    private void Reset()
    {
        world = GetComponent<World>();
    }

    private void OnValidate()
    {
        if (world == null)
            world = GetComponent<World>();
    }

    private void Awake()
    {
        ResolveWorldReference();

        if (!runInitialWorldBootstrap)
        {
            initialWorldReady = true;
            initialLoadProgress01 = 1f;
            SetLoadingPhaseText("Mundo carregado...");
            SetChunkLoadingText(0, 0);
            SetLoadingProgress(1f);
            SetLoadingScreenVisible(false);
            return;
        }

        initialWorldReady = false;
        initialLoadProgress01 = 0f;

        ResolveControlledPlayerReferences();
        SetControlledPlayerLoadingState(true);
        EnsureLoadingScreenExists();
        SetLoadingPhaseText("Carregando mundo...");
        SetChunkLoadingText(0, GetInitialLoadTargetChunkCount());
        SetLoadingProgress(0f);
        SetLoadingScreenVisible(true);
    }

    private IEnumerator Start()
    {
        if (!runInitialWorldBootstrap)
            yield break;

        yield return null;
        BeginInitialBootstrap();
    }

    private void LateUpdate()
    {
        if (!initialBootstrapActive)
            return;

        UpdateInitialWorldLoading();
    }

    private void OnDestroy()
    {
        if (runtimeLoadingScreenCreated && loadingScreenRoot != null)
            Destroy(loadingScreenRoot);
    }

    private void ResolveWorldReference()
    {
        if (world == null)
            world = GetComponent<World>();
    }

    private void BeginInitialBootstrap()
    {
        if (bootstrapInitialized)
            return;

        bootstrapInitialized = true;

        if (world == null)
        {
            CompleteBootstrapWithoutWorld();
            return;
        }

        ResolveControlledPlayerReferences();

        Transform anchor = GetInitialLoadingAnchorTransform();
        if (anchor == null)
        {
            CompleteBootstrapWithoutWorld();
            return;
        }

        initialLoadingAnchorPosition = anchor.position;
        initialLoadCenterChunk = GetChunkCoordFromWorldPosition(initialLoadingAnchorPosition);
        initialBootstrapActive = true;
        initialWorldReady = false;
        initialLoadProgress01 = 0f;

        SetLoadingPhaseText("Carregando mundo...");
        UpdateInitialLoadingUi(0, GetInitialLoadTargetChunkCount());
    }

    private void UpdateInitialWorldLoading()
    {
        int totalChunks = GetInitialLoadTargetChunkCount();
        int readyChunks = CountReadyInitialChunks();

        initialLoadProgress01 = totalChunks > 0
            ? Mathf.Clamp01(readyChunks / (float)totalChunks)
            : 1f;

        UpdateInitialLoadingUi(readyChunks, totalChunks);

        if (readyChunks < totalChunks)
            return;

        ResolveControlledPlayerReferences();

        Vector3 spawnPosition = TryFindValidInitialSpawnPosition(out Vector3 validSpawnPosition)
            ? validSpawnPosition
            : BuildFallbackSpawnPosition();

        TeleportControlledPlayerTo(spawnPosition);
        SetControlledPlayerLoadingState(false);

        initialBootstrapActive = false;
        initialWorldReady = true;
        initialLoadProgress01 = 1f;
        SetLoadingPhaseText("Mundo carregado...");
        UpdateInitialLoadingUi(totalChunks, totalChunks);
        SetLoadingScreenVisible(false);
    }

    private void CompleteBootstrapWithoutWorld()
    {
        initialBootstrapActive = false;
        initialWorldReady = true;
        initialLoadProgress01 = 1f;
        SetControlledPlayerLoadingState(false);
        SetLoadingPhaseText("Mundo carregado...");
        SetChunkLoadingText(0, 0);
        SetLoadingProgress(1f);
        SetLoadingScreenVisible(false);
    }

    private void ResolveControlledPlayerReferences()
    {
        if (world == null || world.player == null)
        {
            cachedPlayerController = null;
            cachedPlayerCharacterController = null;
            controlledPlayerTransform = null;
            return;
        }

        if (cachedPlayerController == null)
        {
            cachedPlayerController = world.player.GetComponent<FPSController>();
            if (cachedPlayerController == null)
                cachedPlayerController = world.player.GetComponentInParent<FPSController>();
            if (cachedPlayerController == null)
                cachedPlayerController = world.player.GetComponentInChildren<FPSController>();
        }

        if (cachedPlayerCharacterController == null)
        {
            cachedPlayerCharacterController = world.player.GetComponent<CharacterController>();
            if (cachedPlayerCharacterController == null)
                cachedPlayerCharacterController = world.player.GetComponentInParent<CharacterController>();
            if (cachedPlayerCharacterController == null)
                cachedPlayerCharacterController = world.player.GetComponentInChildren<CharacterController>();
        }

        if (cachedPlayerController != null)
            controlledPlayerTransform = cachedPlayerController.transform;
        else if (cachedPlayerCharacterController != null)
            controlledPlayerTransform = cachedPlayerCharacterController.transform;
        else
            controlledPlayerTransform = world.player;
    }

    private Transform GetInitialLoadingAnchorTransform()
    {
        if (controlledPlayerTransform != null)
            return controlledPlayerTransform;

        return world != null ? world.player : null;
    }

    private int GetInitialLoadTargetChunkCount()
    {
        if (world == null)
            return 0;

        int count = 0;
        for (int x = -world.renderDistance; x <= world.renderDistance; x++)
        {
            for (int z = -world.renderDistance; z <= world.renderDistance; z++)
            {
                Vector2Int coord = new Vector2Int(initialLoadCenterChunk.x + x, initialLoadCenterChunk.y + z);
                if (IsCoordInsideRenderDistance(coord, initialLoadCenterChunk))
                    count++;
            }
        }

        return Mathf.Max(1, count);
    }

    private int CountReadyInitialChunks()
    {
        if (world == null)
            return 0;

        int readyChunks = 0;

        for (int x = -world.renderDistance; x <= world.renderDistance; x++)
        {
            for (int z = -world.renderDistance; z <= world.renderDistance; z++)
            {
                Vector2Int coord = new Vector2Int(initialLoadCenterChunk.x + x, initialLoadCenterChunk.y + z);
                if (world.IsChunkReady(coord))
                    readyChunks++;
            }
        }

        return readyChunks;
    }

    private bool TryFindValidInitialSpawnPosition(out Vector3 spawnPosition)
    {
        int originX = Mathf.RoundToInt(initialLoadingAnchorPosition.x);
        int originZ = Mathf.RoundToInt(initialLoadingAnchorPosition.z);

        if (TryBuildSpawnPosition(originX, originZ, out spawnPosition))
            return true;

        int maxRadius = Mathf.Max(0, initialSpawnSearchRadius);
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            int minX = originX - radius;
            int maxX = originX + radius;
            int minZ = originZ - radius;
            int maxZ = originZ + radius;

            for (int x = minX; x <= maxX; x++)
            {
                if (TryBuildSpawnPosition(x, minZ, out spawnPosition))
                    return true;

                if (TryBuildSpawnPosition(x, maxZ, out spawnPosition))
                    return true;
            }

            for (int z = minZ + 1; z < maxZ; z++)
            {
                if (TryBuildSpawnPosition(minX, z, out spawnPosition))
                    return true;

                if (TryBuildSpawnPosition(maxX, z, out spawnPosition))
                    return true;
            }
        }

        spawnPosition = default;
        return false;
    }

    private bool TryBuildSpawnPosition(int worldX, int worldZ, out Vector3 spawnPosition)
    {
        spawnPosition = default;

        if (world == null)
            return false;

        Vector2Int coord = GetChunkCoordFromWorldXZ(worldX, worldZ);
        if (!IsCoordInsideRenderDistance(coord, initialLoadCenterChunk))
            return false;

        if (!world.IsChunkReady(coord))
            return false;

        int surfaceY = Mathf.Clamp(world.SampleSurfaceHeight(worldX, worldZ), 1, Chunk.SizeY - 3);
        int feetY = surfaceY + 1;

        if (!IsValidSurfaceSpawn(worldX, feetY, worldZ))
            return false;

        spawnPosition = new Vector3(worldX + 0.5f, feetY + initialSpawnYOffset, worldZ + 0.5f);
        return true;
    }

    private bool IsValidSurfaceSpawn(int worldX, int feetY, int worldZ)
    {
        if (world == null || feetY < 1 || feetY >= Chunk.SizeY - 2)
            return false;

        BlockType supportBlock = world.GetBlockAt(new Vector3Int(worldX, feetY - 1, worldZ));
        if (!IsSafeSpawnSupportBlock(supportBlock))
            return false;

        for (int offset = 0; offset < 2; offset++)
        {
            BlockType spaceBlock = world.GetBlockAt(new Vector3Int(worldX, feetY + offset, worldZ));
            if (!IsPassableSpawnBlock(spaceBlock))
                return false;
        }

        return true;
    }

    private bool IsSafeSpawnSupportBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air || blockType == BlockType.Leaves || blockType == BlockType.short_grass4)
            return false;

        if (blockType == BlockType.Cactus)
            return false;

        return world != null && world.IsSolidBlock(blockType) && !world.IsLiquidBlock(blockType);
    }

    private bool IsPassableSpawnBlock(BlockType blockType)
    {
        return world != null && !world.IsSolidBlock(blockType) && !world.IsLiquidBlock(blockType);
    }

    private Vector3 BuildFallbackSpawnPosition()
    {
        if (world == null)
            return initialLoadingAnchorPosition;

        int worldX = Mathf.RoundToInt(initialLoadingAnchorPosition.x);
        int worldZ = Mathf.RoundToInt(initialLoadingAnchorPosition.z);
        int feetY = Mathf.Clamp(world.SampleSurfaceHeight(worldX, worldZ) + 1, 2, Chunk.SizeY - 2);

        return new Vector3(worldX + 0.5f, feetY + initialSpawnYOffset, worldZ + 0.5f);
    }

    private void SetControlledPlayerLoadingState(bool isLoading)
    {
        if (cachedPlayerController != null)
        {
            cachedPlayerController.SetWorldLoadingState(isLoading);
            return;
        }

        if (cachedPlayerCharacterController == null)
            return;

        if (isLoading && cachedPlayerCharacterController.enabled)
            cachedPlayerCharacterController.Move(Vector3.zero);
    }

    private void TeleportControlledPlayerTo(Vector3 worldPosition)
    {
        if (cachedPlayerController != null)
        {
            cachedPlayerController.TeleportTo(worldPosition);
            return;
        }

        if (controlledPlayerTransform == null)
            return;

        if (cachedPlayerCharacterController != null && cachedPlayerCharacterController.enabled)
        {
            cachedPlayerCharacterController.enabled = false;
            controlledPlayerTransform.position = worldPosition;
            cachedPlayerCharacterController.enabled = true;
            return;
        }

        controlledPlayerTransform.position = worldPosition;
    }

    private void EnsureLoadingScreenExists()
    {
        if (!enableLoadingScreen)
            return;

        if (loadingScreenRoot != null || !autoCreateLoadingOverlay)
            return;

        GameObject canvasObject = new GameObject(
            "WorldLoadingOverlay",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject backgroundObject = new GameObject("Background", typeof(RectTransform), typeof(Image));
        backgroundObject.transform.SetParent(canvasObject.transform, false);

        RectTransform backgroundRect = backgroundObject.GetComponent<RectTransform>();
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        Image backgroundImage = backgroundObject.GetComponent<Image>();
        backgroundImage.color = new Color(0.05f, 0.07f, 0.09f, 1f);

        GameObject phaseTextObject = new GameObject("PhaseText", typeof(RectTransform), typeof(TextMeshProUGUI));
        phaseTextObject.transform.SetParent(backgroundObject.transform, false);

        RectTransform phaseTextRect = phaseTextObject.GetComponent<RectTransform>();
        phaseTextRect.anchorMin = new Vector2(0.5f, 0.5f);
        phaseTextRect.anchorMax = new Vector2(0.5f, 0.5f);
        phaseTextRect.sizeDelta = new Vector2(960f, 90f);
        phaseTextRect.anchoredPosition = new Vector2(0f, 120f);

        TextMeshProUGUI phaseLabel = phaseTextObject.GetComponent<TextMeshProUGUI>();
        phaseLabel.alignment = TextAlignmentOptions.Center;
        phaseLabel.fontSize = 42f;
        phaseLabel.textWrappingMode = TextWrappingModes.Normal;
        phaseLabel.color = Color.white;
        phaseLabel.text = "Carregando mundo...";

        GameObject chunksTextObject = new GameObject("ChunksText", typeof(RectTransform), typeof(TextMeshProUGUI));
        chunksTextObject.transform.SetParent(backgroundObject.transform, false);

        RectTransform chunksTextRect = chunksTextObject.GetComponent<RectTransform>();
        chunksTextRect.anchorMin = new Vector2(0.5f, 0.5f);
        chunksTextRect.anchorMax = new Vector2(0.5f, 0.5f);
        chunksTextRect.sizeDelta = new Vector2(960f, 80f);
        chunksTextRect.anchoredPosition = new Vector2(0f, 50f);

        TextMeshProUGUI chunksLabel = chunksTextObject.GetComponent<TextMeshProUGUI>();
        chunksLabel.alignment = TextAlignmentOptions.Center;
        chunksLabel.fontSize = 28f;
        chunksLabel.textWrappingMode = TextWrappingModes.Normal;
        chunksLabel.color = new Color(1f, 1f, 1f, 0.92f);
        chunksLabel.text = "Chunks carregados: 0/0";

        GameObject progressBarObject = new GameObject("ProgressBar", typeof(RectTransform), typeof(Image), typeof(Slider));
        progressBarObject.transform.SetParent(backgroundObject.transform, false);

        RectTransform progressBarRect = progressBarObject.GetComponent<RectTransform>();
        progressBarRect.anchorMin = new Vector2(0.5f, 0.5f);
        progressBarRect.anchorMax = new Vector2(0.5f, 0.5f);
        progressBarRect.sizeDelta = new Vector2(720f, 32f);
        progressBarRect.anchoredPosition = new Vector2(0f, -24f);

        Image progressBarBackground = progressBarObject.GetComponent<Image>();
        progressBarBackground.color = new Color(1f, 1f, 1f, 0.12f);

        Slider progressSlider = progressBarObject.GetComponent<Slider>();
        progressSlider.minValue = 0f;
        progressSlider.maxValue = 1f;
        progressSlider.wholeNumbers = false;
        progressSlider.interactable = false;
        progressSlider.transition = Selectable.Transition.None;
        progressSlider.direction = Slider.Direction.LeftToRight;
        progressSlider.targetGraphic = progressBarBackground;

        GameObject fillAreaObject = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaObject.transform.SetParent(progressBarObject.transform, false);

        RectTransform fillAreaRect = fillAreaObject.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = Vector2.zero;
        fillAreaRect.anchorMax = Vector2.one;
        fillAreaRect.offsetMin = new Vector2(4f, 4f);
        fillAreaRect.offsetMax = new Vector2(-4f, -4f);

        GameObject fillObject = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillObject.transform.SetParent(fillAreaObject.transform, false);

        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        Image fillImage = fillObject.GetComponent<Image>();
        fillImage.color = new Color(0.34f, 0.82f, 0.5f, 1f);

        progressSlider.fillRect = fillRect;
        progressSlider.handleRect = null;

        GameObject percentObject = new GameObject("PercentText", typeof(RectTransform), typeof(TextMeshProUGUI));
        percentObject.transform.SetParent(backgroundObject.transform, false);

        RectTransform percentRect = percentObject.GetComponent<RectTransform>();
        percentRect.anchorMin = new Vector2(0.5f, 0.5f);
        percentRect.anchorMax = new Vector2(0.5f, 0.5f);
        percentRect.sizeDelta = new Vector2(300f, 50f);
        percentRect.anchoredPosition = new Vector2(0f, -72f);

        TextMeshProUGUI percentLabel = percentObject.GetComponent<TextMeshProUGUI>();
        percentLabel.alignment = TextAlignmentOptions.Center;
        percentLabel.fontSize = 28f;
        percentLabel.color = new Color(1f, 1f, 1f, 0.9f);
        percentLabel.text = "0%";

        loadingScreenRoot = canvasObject;
        loadingStatusText = chunksLabel;
        loadingPhaseText = phaseLabel;
        loadingProgressBar = progressSlider;
        loadingProgressFill = fillImage;
        loadingProgressPercentText = percentLabel;
        runtimeLoadingScreenCreated = true;
    }

    private void UpdateInitialLoadingUi(int readyChunks, int totalChunks)
    {
        if (!enableLoadingScreen)
            return;

        int safeTotal = Mathf.Max(1, totalChunks);
        float progress01 = Mathf.Clamp01(readyChunks / (float)safeTotal);

        SetChunkLoadingText(readyChunks, totalChunks);
        SetLoadingProgress(progress01);
    }

    private void SetChunkLoadingText(int readyChunks, int totalChunks)
    {
        if (!enableLoadingScreen)
            return;

        if (loadingStatusText == null)
            return;

        int clampedTotal = Mathf.Max(0, totalChunks);
        int clampedReady = Mathf.Clamp(readyChunks, 0, clampedTotal);
        loadingStatusText.text = $"Chunks carregados: {clampedReady}/{clampedTotal}";
    }

    private void SetLoadingPhaseText(string status)
    {
        if (!enableLoadingScreen)
            return;

        if (loadingPhaseText != null)
            loadingPhaseText.text = status;
    }

    private void SetLoadingProgress(float progress01)
    {
        if (!enableLoadingScreen)
            return;

        float clampedProgress = Mathf.Clamp01(progress01);

        if (loadingProgressBar != null)
            loadingProgressBar.SetValueWithoutNotify(clampedProgress);

        if (loadingProgressFill != null)
            loadingProgressFill.fillAmount = clampedProgress;

        if (loadingProgressPercentText != null)
            loadingProgressPercentText.text = $"{Mathf.RoundToInt(clampedProgress * 100f)}%";
    }

    private void SetLoadingScreenVisible(bool isVisible)
    {
        if (!enableLoadingScreen)
        {
            if (loadingScreenRoot != null)
                loadingScreenRoot.SetActive(false);
            return;
        }

        if (loadingScreenRoot != null)
            loadingScreenRoot.SetActive(isVisible);
    }

    private bool IsCoordInsideRenderDistance(Vector2Int coord, Vector2Int center)
    {
        if (world == null)
            return false;

        int dx = coord.x - center.x;
        int dz = coord.y - center.y;
        int renderRadius = Mathf.Max(0, world.renderDistance);
        return dx * dx + dz * dz <= renderRadius * renderRadius;
    }

    private static Vector2Int GetChunkCoordFromWorldPosition(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt(worldPos.z / Chunk.SizeZ)
        );
    }

    private static Vector2Int GetChunkCoordFromWorldXZ(int worldX, int worldZ)
    {
        return new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );
    }
}
