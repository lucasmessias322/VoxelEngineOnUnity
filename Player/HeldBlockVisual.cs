using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class HeldBlockVisual : MonoBehaviour
{
    private const string FollowRootName = "HeldItemFollowRoot";
    private const string VisualRootName = "HeldItemVisual";
    private const string RotationXRootName = "HeldItemRotationX";
    private const string RotationYRootName = "HeldItemRotationY";
    private const string RotationZRootName = "HeldItemRotationZ";
    private const string BlockVisualName = "BlockVisual";
    private const string FlatItemVisualName = "FlatItemVisual";
    private const string HeldPrefabRootName = "HeldPrefabRoot";
    private const int MaxGeneratedItemPixels = 4096;

    private enum HeldVisualKind
    {
        None,
        Block,
        FlatItem,
        Prefab
    }

    private enum ViewVisibilityMode
    {
        Always,
        FirstPersonOnly,
        ThirdPersonOnly
    }

    [Header("References")]
    [SerializeField] private HotbarMirror hotbar;
    [SerializeField] private Transform handAnchor;
    [SerializeField] private World world;
    [SerializeField] private ItemAtlasDataSO itemAtlasData;

    [Header("Visibility")]
    [SerializeField] private ViewVisibilityMode visibilityMode = ViewVisibilityMode.FirstPersonOnly;
    [SerializeField] private bool applyGameObjectLayerToVisuals;

    [Header("Default View")]
    [Tooltip("Fallback/shared local position (used by prefab and when separate positions are disabled).")]
    [SerializeField] private Vector3 localPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private bool useSeparateLocalPositions = true;
    [SerializeField] private Vector3 blockLocalPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private Vector3 flatItemLocalPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private bool useSeparateLocalRotations = true;
    [SerializeField] private Vector3 localEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private Vector3 blockLocalEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private Vector3 flatItemLocalEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private float heldScale = 0.28f;
    [SerializeField] private float flatItemScale = 0.85f;
    [SerializeField] private bool hideWhenInventoryOpen = true;
    [SerializeField] private bool castShadows = false;
    [SerializeField] private bool receiveShadows = false;
    [SerializeField] private bool applyItemHeldTransformOverrides = true;
    [SerializeField] private bool useSplitRotationAxes;

    [Header("Flat Item Rendering")]
    [Tooltip("Material usado para renderizar itens planos na mao. A textura do item sera aplicada por renderer em runtime.")]
    [SerializeField] private Material flatItemMaterial;
    [Tooltip("Quando ligado, itens 2D usam uma extrusao fina por alpha, parecida com o modelo generated do Minecraft.")]
    [SerializeField] private bool useGeneratedItemDepth = true;
    [Min(0.001f)] [SerializeField] private float generatedItemThickness = 0.0625f;
    [Range(0f, 1f)] [SerializeField] private float generatedItemAlphaThreshold = 0.1f;

    [Header("Player Arm")]
    [Tooltip("Renderer da malha do braco do player. Apenas a mesh sera ativada/desativada.")]
    [SerializeField] private Renderer playerArmRenderer;
    [SerializeField] private bool hideArmMeshWhenHolding = true;

    [Header("First-Person Mining Motion")]
    [Tooltip("Root que move o braco e o item juntos na visao de primeira pessoa. Se vazio, usa o pai do Hand Anchor.")]
    [SerializeField] private Transform firstPersonRigRoot;
    [SerializeField] private PlayerBlockBreaker blockBreaker;
    [SerializeField] private FPSController fpsController;
    [SerializeField] private bool enableMiningMotion = true;
    [SerializeField, Min(0f)] private float miningMotionBlendSpeed = 18f;
    [SerializeField, Min(0.01f)] private float miningSwingFrequency = 4.75f;
    [SerializeField] private Vector3 miningLocalPositionOffset = new Vector3(0.035f, -0.08f, 0.045f);
    [SerializeField] private Vector3 miningLocalEulerAngles = new Vector3(-24f, 10f, 12f);
    [SerializeField] private bool enablePlaceMotion = true;
    [SerializeField, Min(0.01f)] private float placeSwingDuration = 0.18f;
    [SerializeField] private Vector3 placeLocalPositionOffset = new Vector3(0.022f, -0.055f, 0.035f);
    [SerializeField] private Vector3 placeLocalEulerAngles = new Vector3(-18f, 8f, 10f);

    private readonly Dictionary<BlockType, Mesh> blockMeshCache = new Dictionary<BlockType, Mesh>();
    private readonly Dictionary<BlockType, int> blockMaterialIndexCache = new Dictionary<BlockType, int>();
    private readonly Dictionary<Item, Mesh> atlasFlatItemMeshCache = new Dictionary<Item, Mesh>();
    private readonly Dictionary<Item, Mesh> spriteFlatItemMeshCache = new Dictionary<Item, Mesh>();
    private readonly Dictionary<Material, Material> flatItemMaterialCloneCache = new Dictionary<Material, Material>();

    private GameObject followRoot;
    private GameObject visualRoot;
    private GameObject rotationXRoot;
    private GameObject rotationYRoot;
    private GameObject rotationZRoot;
    private GameObject blockVisualObject;
    private MeshFilter blockMeshFilter;
    private MeshRenderer blockMeshRenderer;
    private GameObject flatItemVisualObject;
    private MeshFilter flatItemMeshFilter;
    private MeshRenderer flatItemMeshRenderer;
    private MaterialPropertyBlock flatItemPropertyBlock;
    private Material runtimeFlatItemMaterial;
    private GameObject heldPrefabInstance;
    private GameObject heldPrefabVisualObject;
    private GameObject heldPrefabSource;
    private Item shownItem;
    private BlockType shownBlockType = BlockType.Air;
    private HeldVisualKind shownVisualKind;
    private bool isVisible;
    private Transform capturedMiningRigRoot;
    private Vector3 miningRigIdleLocalPosition;
    private Quaternion miningRigIdleLocalRotation = Quaternion.identity;
    private bool miningRigPoseCaptured;
    private float miningMotionWeight;
    private float miningSwingTimer;
    private int lastSeenPlaceActionVersion = -1;
    private float placeSwingElapsed = float.PositiveInfinity;

    private void Awake()
    {
        ResolveReferences();
        CaptureMiningRigPose(force: true);
        EnsureVisualObject();
        UpdateFollowRootTransform();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void LateUpdate()
    {
        ResolveReferences();
        EnsureVisualObject();
        UpdateFollowRootTransform();
        RefreshHeldBlock(forceRefresh: false);
        ApplyMiningMotion();
    }

    [ContextMenu("Refresh Held Visual")]
    public void RefreshNow()
    {
        ResolveReferences();
        CaptureMiningRigPose(force: true);
        EnsureVisualObject();
        UpdateFollowRootTransform();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
        ApplyMiningMotion();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
            return;

        ResolveReferences();
        CaptureMiningRigPose(force: capturedMiningRigRoot != firstPersonRigRoot);
        ApplyViewTransform(shownItem, shownVisualKind);
        UpdateFollowRootTransform();
        ApplyRendererSettings();
        UpdateArmMeshVisibility(isVisible);
        InvalidateFlatItemMeshCaches();
        RefreshHeldBlock(forceRefresh: true);
        ApplyMiningMotion();
    }

    private void OnDestroy()
    {
        RestoreMiningRigPose();
        UpdateArmMeshVisibility(false);
        DestroyCachedMeshes(blockMeshCache);
        blockMaterialIndexCache.Clear();
        DestroyCachedMeshes(atlasFlatItemMeshCache);
        DestroyCachedMeshes(spriteFlatItemMeshCache);
        DestroyRuntimeMaterials(flatItemMaterialCloneCache);

        if (runtimeFlatItemMaterial != null)
            Destroy(runtimeFlatItemMaterial);

        ClearHeldPrefabInstance();

        if (followRoot != null)
            Destroy(followRoot);

        followRoot = null;
        visualRoot = null;
        rotationXRoot = null;
        rotationYRoot = null;
        rotationZRoot = null;
        blockVisualObject = null;
        flatItemVisualObject = null;
        heldPrefabInstance = null;
    }

    private void OnDisable()
    {
        RestoreMiningRigPose();
        if (followRoot != null)
            followRoot.SetActive(false);

        UpdateArmMeshVisibility(false);
    }

    private void OnEnable()
    {
        ResolveReferences();
        CaptureMiningRigPose(force: true);
        if (followRoot != null)
            followRoot.SetActive(true);

        UpdateArmMeshVisibility(isVisible);
    }

    private void ResolveReferences()
    {
        if (hotbar == null)
            hotbar = FindAnyObjectByType<HotbarMirror>();

        if (world == null)
            world = World.Instance != null ? World.Instance : FindAnyObjectByType<World>();

        if (handAnchor == null)
            handAnchor = transform;

        if (fpsController == null)
            fpsController = FindAnyObjectByType<FPSController>();

        if (blockBreaker == null)
            blockBreaker = FindAnyObjectByType<PlayerBlockBreaker>();

        if (firstPersonRigRoot == null && handAnchor != null)
            firstPersonRigRoot = handAnchor.parent;

        if (miningRigPoseCaptured && capturedMiningRigRoot != firstPersonRigRoot)
            CaptureMiningRigPose(force: true);
    }

    private void EnsureVisualObject()
    {
        Transform stableParent = handAnchor != null ? handAnchor : transform;

        if (followRoot == null && stableParent != null)
            followRoot = FindDirectChildByName(stableParent, FollowRootName)?.gameObject;

        if (followRoot == null && transform != null && transform != stableParent)
            followRoot = FindDirectChildByName(transform, FollowRootName)?.gameObject;

        if (followRoot == null)
        {
            followRoot = new GameObject(FollowRootName);
            followRoot.transform.SetParent(stableParent, false);
        }
        else if (followRoot.transform.parent != stableParent)
        {
            followRoot.transform.SetParent(stableParent, false);
        }

        DestroyDuplicateChildren(stableParent, FollowRootName, followRoot.transform);

        if (transform != null && transform != stableParent)
            DestroyDuplicateChildren(transform, FollowRootName, followRoot.transform.parent == transform ? followRoot.transform : null);

        if (visualRoot == null && followRoot != null)
            visualRoot = FindDirectChildByName(followRoot.transform, VisualRootName)?.gameObject;

        if (visualRoot == null)
        {
            visualRoot = new GameObject(VisualRootName);
            visualRoot.transform.SetParent(followRoot.transform, false);
            visualRoot.SetActive(false);
        }
        else if (visualRoot.transform.parent != followRoot.transform)
        {
            visualRoot.transform.SetParent(followRoot.transform, false);
        }

        DestroyDuplicateChildren(followRoot.transform, VisualRootName, visualRoot.transform);

        Transform visualContentParent = EnsureVisualContentParent();

        if (blockVisualObject == null && visualRoot != null)
            blockVisualObject = FindDirectChildByName(visualContentParent, BlockVisualName)?.gameObject;

        if (blockVisualObject == null)
        {
            blockVisualObject = new GameObject(BlockVisualName);
            blockVisualObject.transform.SetParent(visualContentParent, false);
            blockMeshFilter = blockVisualObject.AddComponent<MeshFilter>();
            blockMeshRenderer = blockVisualObject.AddComponent<MeshRenderer>();
            blockVisualObject.SetActive(false);
        }
        else if (blockVisualObject.transform.parent != visualContentParent)
        {
            blockVisualObject.transform.SetParent(visualContentParent, false);
        }

        if (flatItemVisualObject == null && visualRoot != null)
            flatItemVisualObject = FindDirectChildByName(visualContentParent, FlatItemVisualName)?.gameObject;

        if (flatItemVisualObject == null)
        {
            flatItemVisualObject = new GameObject(FlatItemVisualName);
            flatItemVisualObject.transform.SetParent(visualContentParent, false);
            flatItemMeshFilter = flatItemVisualObject.AddComponent<MeshFilter>();
            flatItemMeshRenderer = flatItemVisualObject.AddComponent<MeshRenderer>();
            flatItemVisualObject.SetActive(false);
        }
        else if (flatItemVisualObject.transform.parent != visualContentParent)
        {
            flatItemVisualObject.transform.SetParent(visualContentParent, false);
        }

        if (blockMeshFilter == null)
            blockMeshFilter = blockVisualObject.GetComponent<MeshFilter>();
        if (blockMeshRenderer == null)
            blockMeshRenderer = blockVisualObject.GetComponent<MeshRenderer>();
        if (flatItemMeshFilter == null)
            flatItemMeshFilter = flatItemVisualObject.GetComponent<MeshFilter>();
        if (flatItemMeshRenderer == null)
            flatItemMeshRenderer = flatItemVisualObject.GetComponent<MeshRenderer>();

        if (heldPrefabInstance == null)
        {
            heldPrefabInstance = new GameObject(HeldPrefabRootName);
            heldPrefabInstance.transform.SetParent(visualContentParent, false);
            heldPrefabInstance.SetActive(false);
        }
        else if (heldPrefabInstance.transform.parent != visualContentParent)
        {
            heldPrefabInstance.transform.SetParent(visualContentParent, false);
        }

        if (flatItemPropertyBlock == null)
            flatItemPropertyBlock = new MaterialPropertyBlock();

        ApplyRendererSettings();
        ApplyVisualHierarchyLayer();
    }

    private void UpdateFollowRootTransform()
    {
        if (followRoot == null)
            return;

        Transform stableParent = handAnchor != null ? handAnchor : transform;
        if (followRoot.transform.parent != stableParent)
            followRoot.transform.SetParent(stableParent, false);

        // Keep a clean transform baseline so per-item overrides are applied consistently.
        followRoot.transform.localPosition = Vector3.zero;
        followRoot.transform.localRotation = Quaternion.identity;
        followRoot.transform.localScale = GetNeutralizedLocalScale();
    }

    private Vector3 GetNeutralizedLocalScale()
    {
        Transform parentTransform = followRoot != null ? followRoot.transform.parent : null;
        if (parentTransform == null)
            return Vector3.one;

        Vector3 parentLossyScale = parentTransform.lossyScale;
        return new Vector3(
            GetSafeInverseScale(parentLossyScale.x),
            GetSafeInverseScale(parentLossyScale.y),
            GetSafeInverseScale(parentLossyScale.z));
    }

    private void ApplyViewTransform(Item selectedItem, HeldVisualKind visualKind)
    {
        if (visualRoot == null)
            return;

        Vector3 targetPosition = GetDefaultLocalPositionFor(visualKind);
        Vector3 targetEulerAngles = GetDefaultLocalEulerAnglesFor(visualKind);
        Vector3 targetScale = GetDefaultRootScaleFor(visualKind);

        if (selectedItem != null && applyItemHeldTransformOverrides)
        {
            // Legacy behavior has absolute priority.
            if (selectedItem.overrideHeldTransform)
            {
                targetPosition = selectedItem.heldLocalPosition;
                targetEulerAngles = selectedItem.heldLocalEulerAngles;

                if (visualKind != HeldVisualKind.Prefab)
                    targetScale = SanitizeScale(selectedItem.heldLocalScale);
            }
            else
            {
                if (selectedItem.overrideHeldPosition)
                    targetPosition = selectedItem.heldLocalPosition;

                if (selectedItem.overrideHeldRotation)
                    targetEulerAngles = selectedItem.heldLocalEulerAngles;

                if (visualKind != HeldVisualKind.Prefab && selectedItem.overrideHeldScale)
                    targetScale = SanitizeScale(selectedItem.heldLocalScale);
            }
        }

        visualRoot.transform.localPosition = targetPosition;
        visualRoot.transform.localScale = targetScale;

        if (useSplitRotationAxes)
        {
            EnsureRotationHierarchy();
            visualRoot.transform.localRotation = Quaternion.identity;

            if (rotationXRoot != null)
                rotationXRoot.transform.localRotation = Quaternion.AngleAxis(targetEulerAngles.x, Vector3.right);

            if (rotationYRoot != null)
                rotationYRoot.transform.localRotation = Quaternion.AngleAxis(targetEulerAngles.y, Vector3.up);

            if (rotationZRoot != null)
                rotationZRoot.transform.localRotation = Quaternion.AngleAxis(targetEulerAngles.z, Vector3.forward);

            return;
        }

        ResetSplitRotationAxes();
        visualRoot.transform.localRotation = Quaternion.Euler(targetEulerAngles);
    }

    private Transform EnsureVisualContentParent()
    {
        if (!useSplitRotationAxes)
            return visualRoot != null ? visualRoot.transform : null;

        EnsureRotationHierarchy();
        return rotationZRoot != null ? rotationZRoot.transform : (visualRoot != null ? visualRoot.transform : null);
    }

    private void EnsureRotationHierarchy()
    {
        if (visualRoot == null)
            return;

        if (rotationXRoot == null)
            rotationXRoot = FindDirectChildByName(visualRoot.transform, RotationXRootName)?.gameObject;

        if (rotationXRoot == null)
        {
            rotationXRoot = new GameObject(RotationXRootName);
            rotationXRoot.transform.SetParent(visualRoot.transform, false);
        }
        else if (rotationXRoot.transform.parent != visualRoot.transform)
        {
            rotationXRoot.transform.SetParent(visualRoot.transform, false);
        }

        if (rotationYRoot == null)
            rotationYRoot = FindDirectChildByName(rotationXRoot.transform, RotationYRootName)?.gameObject;

        if (rotationYRoot == null)
        {
            rotationYRoot = new GameObject(RotationYRootName);
            rotationYRoot.transform.SetParent(rotationXRoot.transform, false);
        }
        else if (rotationYRoot.transform.parent != rotationXRoot.transform)
        {
            rotationYRoot.transform.SetParent(rotationXRoot.transform, false);
        }

        if (rotationZRoot == null)
            rotationZRoot = FindDirectChildByName(rotationYRoot.transform, RotationZRootName)?.gameObject;

        if (rotationZRoot == null)
        {
            rotationZRoot = new GameObject(RotationZRootName);
            rotationZRoot.transform.SetParent(rotationYRoot.transform, false);
        }
        else if (rotationZRoot.transform.parent != rotationYRoot.transform)
        {
            rotationZRoot.transform.SetParent(rotationYRoot.transform, false);
        }

        ResetTransform(rotationXRoot.transform);
        ResetTransform(rotationYRoot.transform);
        ResetTransform(rotationZRoot.transform);
    }

    private void ResetSplitRotationAxes()
    {
        if (rotationXRoot != null)
            rotationXRoot.transform.localRotation = Quaternion.identity;

        if (rotationYRoot != null)
            rotationYRoot.transform.localRotation = Quaternion.identity;

        if (rotationZRoot != null)
            rotationZRoot.transform.localRotation = Quaternion.identity;
    }

    private Vector3 GetDefaultLocalPositionFor(HeldVisualKind visualKind)
    {
        if (!useSeparateLocalPositions)
            return localPosition;

        switch (visualKind)
        {
            case HeldVisualKind.Block:
                return blockLocalPosition;
            case HeldVisualKind.FlatItem:
                return flatItemLocalPosition;
            case HeldVisualKind.Prefab:
            case HeldVisualKind.None:
            default:
                return localPosition;
        }
    }

    private Vector3 GetDefaultLocalEulerAnglesFor(HeldVisualKind visualKind)
    {
        if (!useSeparateLocalRotations)
            return localEulerAngles;

        switch (visualKind)
        {
            case HeldVisualKind.Block:
                return blockLocalEulerAngles;
            case HeldVisualKind.FlatItem:
                return flatItemLocalEulerAngles;
            case HeldVisualKind.Prefab:
            case HeldVisualKind.None:
            default:
                return localEulerAngles;
        }
    }

    private Vector3 GetDefaultRootScaleFor(HeldVisualKind visualKind)
    {
        switch (visualKind)
        {
            case HeldVisualKind.FlatItem:
                return Vector3.one * Mathf.Max(0.01f, flatItemScale);
            case HeldVisualKind.Prefab:
                return Vector3.one;
            case HeldVisualKind.Block:
            case HeldVisualKind.None:
            default:
                return Vector3.one * Mathf.Max(0.01f, heldScale);
        }
    }

    private void ApplyRendererSettings()
    {
        if (blockMeshRenderer != null)
        {
            blockMeshRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            blockMeshRenderer.receiveShadows = receiveShadows;
        }

        if (flatItemMeshRenderer != null)
        {
            flatItemMeshRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            flatItemMeshRenderer.receiveShadows = receiveShadows;
        }
    }

    private void RefreshHeldBlock(bool forceRefresh)
    {
        if (visualRoot == null)
            return;

        if (!ShouldDisplayInCurrentView())
        {
            UpdateArmMeshVisibility(false);
            ClearShownState();
            SetVisible(false);
            return;
        }

        if (hideWhenInventoryOpen && PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
        {
            UpdateArmMeshVisibility(false);
            ClearShownState();
            SetVisible(false);
            return;
        }

        if (hotbar == null || !hotbar.TryGetSelectedItem(out Item selectedItem) || selectedItem == null)
        {
            UpdateArmMeshVisibility(false);
            ClearShownState();
            SetVisible(false);
            return;
        }

        bool hasBlockSelection = TryResolveHeldBlockType(selectedItem, out BlockType selectedBlockType);
        HeldVisualKind desiredKind = GetDesiredVisualKind(selectedItem, hasBlockSelection);
        if (desiredKind == HeldVisualKind.None)
        {
            UpdateArmMeshVisibility(false);
            ClearShownState();
            SetVisible(false);
            return;
        }

        UpdateArmMeshVisibility(true);
        ApplyViewTransform(selectedItem, desiredKind);

        if (!forceRefresh &&
            isVisible &&
            shownItem == selectedItem &&
            shownBlockType == selectedBlockType &&
            shownVisualKind == desiredKind)
        {
            return;
        }

        bool shownSuccessfully = false;

        switch (desiredKind)
        {
            case HeldVisualKind.Prefab:
                shownSuccessfully = ShowHeldPrefab(selectedItem);
                break;
            case HeldVisualKind.Block:
                shownSuccessfully = ShowBlock(selectedBlockType);
                break;
            case HeldVisualKind.FlatItem:
                shownSuccessfully = ShowFlatItem(selectedItem);
                break;
        }

        if (!shownSuccessfully)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        shownItem = selectedItem;
        shownBlockType = selectedBlockType;
        shownVisualKind = desiredKind;
        SetVisible(true);
    }

    private HeldVisualKind GetDesiredVisualKind(Item selectedItem, bool hasBlockSelection)
    {
        if (selectedItem == null)
            return HeldVisualKind.None;

        if (selectedItem.heldPrefab != null)
            return HeldVisualKind.Prefab;

        if (HasDirectFlatItemSprite(selectedItem))
            return HeldVisualKind.FlatItem;

        if (hasBlockSelection)
            return HeldVisualKind.Block;

        if (HasAtlasFlatItem(selectedItem))
            return HeldVisualKind.FlatItem;

        return HeldVisualKind.None;
    }

    private bool TryResolveHeldBlockType(Item selectedItem, out BlockType blockType)
    {
        blockType = BlockType.Air;
        if (selectedItem == null || selectedItem.inventoryIconMode == InventoryIconMode.ItemIconOnly)
            return false;

        PlayerInventory inventory = PlayerInventory.Instance != null
            ? PlayerInventory.Instance
            : FindAnyObjectByType<PlayerInventory>();

        return inventory != null &&
               inventory.TryGetBlockForItem(selectedItem, out blockType) &&
               blockType != BlockType.Air;
    }

    private bool ShowBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        if (world == null)
            world = World.Instance;

        if (world == null || world.Material == null || world.Material.Length == 0)
            return false;

        Mesh heldMesh = GetOrCreateBlockMesh(blockType, out int materialIndex);
        Material heldMaterial = ResolveHeldBlockMaterial(materialIndex);
        if (heldMesh == null || heldMaterial == null)
            return false;

        HideAllVisualChildren();
        blockMeshFilter.sharedMesh = heldMesh;
        blockMeshRenderer.sharedMaterial = heldMaterial;
        blockVisualObject.SetActive(true);
        return true;
    }

    private bool ShowFlatItem(Item selectedItem)
    {
        if (selectedItem == null || flatItemMeshFilter == null || flatItemMeshRenderer == null)
            return false;

        Mesh flatMesh = GetOrCreateFlatItemMesh(selectedItem, out Texture renderTexture);
        Material renderMaterial = ResolveFlatItemMaterial(selectedItem);
        if (flatMesh == null || renderMaterial == null || renderTexture == null)
            return false;

        HideAllVisualChildren();
        flatItemMeshFilter.sharedMesh = flatMesh;
        flatItemMeshRenderer.sharedMaterial = renderMaterial;
        ApplyFlatItemTexture(renderTexture);
        flatItemVisualObject.SetActive(true);
        return true;
    }

    private bool ShowHeldPrefab(Item selectedItem)
    {
        if (selectedItem == null || selectedItem.heldPrefab == null)
            return false;

        GameObject instance = EnsureHeldPrefabInstance(selectedItem.heldPrefab);
        if (instance == null)
            return false;

        HideAllVisualChildren();
        ApplyHeldPrefabScale(instance.transform, selectedItem);
        instance.SetActive(true);
        return true;
    }

    private GameObject EnsureHeldPrefabInstance(GameObject prefab)
    {
        if (prefab == null || heldPrefabInstance == null)
            return null;

        if (heldPrefabVisualObject != null && heldPrefabSource == prefab)
            return heldPrefabInstance;

        ClearHeldPrefabInstance();

        heldPrefabVisualObject = Instantiate(prefab, heldPrefabInstance.transform, false);
        heldPrefabVisualObject.name = prefab.name + "_Held";
        heldPrefabVisualObject.transform.localPosition = Vector3.zero;
        heldPrefabVisualObject.transform.localRotation = Quaternion.identity;
        heldPrefabVisualObject.transform.localScale = SanitizeScale(prefab.transform.localScale);
        ApplyVisualHierarchyLayer();
        heldPrefabSource = prefab;
        return heldPrefabInstance;
    }

    private void ApplyHeldPrefabScale(Transform prefabTransform, Item selectedItem)
    {
        if (prefabTransform == null || selectedItem == null || selectedItem.heldPrefab == null || heldPrefabVisualObject == null)
            return;

        Vector3 sourceScale = SanitizeScale(selectedItem.heldPrefab.transform.localScale);
        if (!applyItemHeldTransformOverrides || !ShouldOverrideHeldScale(selectedItem))
        {
            prefabTransform.localScale = Vector3.one;
            heldPrefabVisualObject.transform.localScale = sourceScale;
            return;
        }

        Vector3 scaleMultiplier = SanitizeScale(selectedItem.heldLocalScale);
        prefabTransform.localScale = Vector3.one;
        heldPrefabVisualObject.transform.localScale = new Vector3(
            sourceScale.x * scaleMultiplier.x,
            sourceScale.y * scaleMultiplier.y,
            sourceScale.z * scaleMultiplier.z);
    }

    private static bool ShouldOverrideHeldScale(Item item)
    {
        return item != null && (item.overrideHeldTransform || item.overrideHeldScale);
    }

    private void ClearHeldPrefabInstance()
    {
        if (heldPrefabVisualObject != null)
            Destroy(heldPrefabVisualObject);

        heldPrefabVisualObject = null;
        heldPrefabSource = null;
    }

    private void HideAllVisualChildren()
    {
        if (blockVisualObject != null && blockVisualObject.activeSelf)
            blockVisualObject.SetActive(false);

        if (flatItemVisualObject != null && flatItemVisualObject.activeSelf)
            flatItemVisualObject.SetActive(false);

        if (heldPrefabInstance != null && heldPrefabInstance.activeSelf)
            heldPrefabInstance.SetActive(false);
    }

    private Mesh GetOrCreateBlockMesh(BlockType blockType, out int materialIndex)
    {
        materialIndex = 0;
        if (blockMeshCache.TryGetValue(blockType, out Mesh cachedMesh) && cachedMesh != null)
        {
            if (blockMaterialIndexCache.TryGetValue(blockType, out int cachedMaterialIndex))
                materialIndex = cachedMaterialIndex;

            return cachedMesh;
        }

        Mesh mesh = BlockDrop.BuildBlockMesh(world, blockType, out materialIndex);
        if (mesh == null)
            return null;

        CollapseToSingleSubmesh(mesh, materialIndex);
        mesh.name = $"HeldMesh_{blockType}";
        blockMeshCache[blockType] = mesh;
        blockMaterialIndexCache[blockType] = materialIndex;
        return mesh;
    }

    private Material ResolveHeldBlockMaterial(int preferredMaterialIndex)
    {
        if (world == null || world.Material == null || world.Material.Length == 0)
            return null;

        int clampedIndex = Mathf.Clamp(preferredMaterialIndex, 0, world.Material.Length - 1);
        if (world.Material[clampedIndex] != null)
            return world.Material[clampedIndex];

        for (int i = 0; i < world.Material.Length; i++)
        {
            if (world.Material[i] != null)
                return world.Material[i];
        }

        return null;
    }

    private static void CollapseToSingleSubmesh(Mesh mesh, int submeshIndex)
    {
        if (mesh == null || mesh.subMeshCount <= 1)
            return;

        int clampedIndex = Mathf.Clamp(submeshIndex, 0, mesh.subMeshCount - 1);
        int[] triangles = mesh.GetTriangles(clampedIndex);
        mesh.subMeshCount = 1;
        mesh.SetTriangles(triangles, 0, false);
    }

    private bool HasAtlasFlatItem(Item item)
    {
        if (item == null || !TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData))
            return false;

        return atlasData.HasMapping(item);
    }

    private bool HasDirectFlatItemSprite(Item item)
    {
        return TryGetDirectFlatItemSprite(item, out _);
    }

    private static bool TryGetDirectFlatItemSprite(Item item, out Sprite sprite)
    {
        sprite = null;
        return item != null &&
               item.inventoryIconMode != InventoryIconMode.IsometricBlockOnly &&
               ItemIconResolver.TryGetDirectSprite(item, out sprite);
    }

    private bool TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData)
    {
        atlasData = itemAtlasData;
        if (atlasData == null && PlayerInventory.Instance != null)
            PlayerInventory.Instance.TryGetItemAtlasData(out atlasData);

        return atlasData != null;
    }

    private Mesh GetOrCreateFlatItemMesh(Item item, out Texture renderTexture)
    {
        renderTexture = null;
        if (item == null)
            return null;

        if (TryGetDirectFlatItemSprite(item, out Sprite directSprite))
        {
            Mesh spriteMesh = GetOrCreateSpriteFlatItemMesh(item, directSprite);
            if (spriteMesh != null && directSprite.texture != null)
            {
                renderTexture = directSprite.texture;
                return spriteMesh;
            }
        }

        if (TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData) &&
            atlasData.TryGetUvRect(item, out Rect atlasUvRect, applyInset: false) &&
            atlasData.TryGetAspect(item, out float atlasAspect))
        {
            if (atlasData.TryGetTexture(out Texture2D atlasTexture) && atlasTexture != null)
            {
                Rect atlasPixelRect = default;
                atlasData.TryGetPixelRect(item, out atlasPixelRect, applyInset: false);

                Mesh atlasMesh = GetOrCreateAtlasFlatItemMesh(item, atlasTexture, atlasPixelRect, atlasUvRect, atlasAspect);
                if (atlasMesh == null)
                    return null;

                renderTexture = atlasTexture;
                return atlasMesh;
            }
        }

        return null;
    }

    private Material ResolveFlatItemMaterial(Item item)
    {
        if (flatItemMaterial != null)
            return GetOrCreateFlatItemMaterialClone(flatItemMaterial);

        if (item != null &&
            TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData) &&
            atlasData.TryGetMaterial(out Material atlasMaterial) &&
            atlasMaterial != null)
        {
            return GetOrCreateFlatItemMaterialClone(atlasMaterial);
        }

        return GetOrCreateRuntimeFlatItemMaterial();
    }

    private Mesh GetOrCreateAtlasFlatItemMesh(Item item, Texture2D texture, Rect pixelRect, Rect uvRect, float aspect)
    {
        if (item == null)
            return null;

        if (atlasFlatItemMeshCache.TryGetValue(item, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BuildPreferredFlatItemMesh(texture, pixelRect, uvRect, aspect);
        if (mesh == null)
            return null;

        mesh.name = $"HeldFlatItemAtlas_{item.name}";
        atlasFlatItemMeshCache[item] = mesh;
        return mesh;
    }

    private Mesh GetOrCreateSpriteFlatItemMesh(Item item, Sprite sprite)
    {
        if (item == null || sprite == null)
            return null;

        if (spriteFlatItemMeshCache.TryGetValue(item, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        if (!TryGetSpriteUvRect(sprite, out Rect uvRect))
            return null;

        Rect pixelRect = sprite.textureRect;
        float aspect = Mathf.Max(0.01f, pixelRect.width / Mathf.Max(1f, pixelRect.height));
        Mesh mesh = BuildFlatItemMesh(aspect, uvRect);
        if (mesh == null)
            return null;

        mesh.name = $"HeldFlatItemSprite2D_{item.name}";
        spriteFlatItemMeshCache[item] = mesh;
        return mesh;
    }

    private static bool TryGetSpriteUvRect(Sprite sprite, out Rect uvRect)
    {
        uvRect = default;
        if (sprite == null || sprite.texture == null)
            return false;

        Rect textureRect = sprite.textureRect;
        Texture texture = sprite.texture;
        if (texture.width <= 0 || texture.height <= 0 || textureRect.width <= 0f || textureRect.height <= 0f)
            return false;

        uvRect = new Rect(
            textureRect.x / texture.width,
            textureRect.y / texture.height,
            textureRect.width / texture.width,
            textureRect.height / texture.height);
        return true;
    }

    private Mesh BuildPreferredFlatItemMesh(Texture2D texture, Rect pixelRect, Rect uvRect, float aspect)
    {
        if (useGeneratedItemDepth)
        {
            Mesh generatedMesh = BuildGeneratedItemMesh(texture, pixelRect, uvRect, aspect);
            if (generatedMesh != null)
                return generatedMesh;
        }

        return BuildFlatItemMesh(aspect, uvRect);
    }

    private Mesh BuildGeneratedItemMesh(Texture2D texture, Rect pixelRect, Rect uvRect, float aspect)
    {
        if (texture == null)
            return null;

        int textureWidth = texture.width;
        int textureHeight = texture.height;
        if (textureWidth <= 0 || textureHeight <= 0)
            return null;

        int pixelX = Mathf.Clamp(Mathf.RoundToInt(pixelRect.xMin), 0, Mathf.Max(0, textureWidth - 1));
        int pixelY = Mathf.Clamp(Mathf.RoundToInt(pixelRect.yMin), 0, Mathf.Max(0, textureHeight - 1));
        int pixelWidth = Mathf.Clamp(Mathf.RoundToInt(pixelRect.width), 1, textureWidth - pixelX);
        int pixelHeight = Mathf.Clamp(Mathf.RoundToInt(pixelRect.height), 1, textureHeight - pixelY);
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return null;

        if (pixelWidth * pixelHeight > MaxGeneratedItemPixels)
            return null;

        if (!TryGetTexturePixels(texture, out Color32[] texturePixels))
            return null;

        byte alphaThreshold = (byte)Mathf.Clamp(Mathf.RoundToInt(generatedItemAlphaThreshold * 255f), 0, 255);
        bool[] opaqueMask = new bool[pixelWidth * pixelHeight];
        bool hasOpaquePixels = false;

        for (int y = 0; y < pixelHeight; y++)
        {
            int sourceY = pixelY + y;
            int sourceRow = sourceY * textureWidth;
            int targetRow = y * pixelWidth;
            for (int x = 0; x < pixelWidth; x++)
            {
                Color32 pixel = texturePixels[sourceRow + pixelX + x];
                bool isOpaque = pixel.a > alphaThreshold;
                opaqueMask[targetRow + x] = isOpaque;
                hasOpaquePixels |= isOpaque;
            }
        }

        if (!hasOpaquePixels)
            return null;

        float fullWidth = Mathf.Max(0.01f, aspect);
        float fullHeight = 1f;
        float halfWidth = fullWidth * 0.5f;
        float halfHeight = fullHeight * 0.5f;
        float pixelWorldWidth = fullWidth / pixelWidth;
        float pixelWorldHeight = fullHeight / pixelHeight;
        float halfThickness = Mathf.Max(0.001f, generatedItemThickness) * 0.5f;
        float pixelUvWidth = uvRect.width / pixelWidth;
        float pixelUvHeight = uvRect.height / pixelHeight;

        int estimatedFaceCount = Mathf.Min(MaxGeneratedItemPixels, pixelWidth * pixelHeight) * 4;
        List<Vector3> vertices = new List<Vector3>(estimatedFaceCount);
        List<Vector3> normals = new List<Vector3>(estimatedFaceCount);
        List<Vector2> uvs = new List<Vector2>(estimatedFaceCount);
        List<int> triangles = new List<int>(estimatedFaceCount * 2);

        for (int y = 0; y < pixelHeight; y++)
        {
            int row = y * pixelWidth;
            for (int x = 0; x < pixelWidth; x++)
            {
                if (!opaqueMask[row + x])
                    continue;

                float x0 = -halfWidth + x * pixelWorldWidth;
                float x1 = x0 + pixelWorldWidth;
                float y0 = -halfHeight + y * pixelWorldHeight;
                float y1 = y0 + pixelWorldHeight;
                Rect pixelUv = new Rect(
                    uvRect.xMin + x * pixelUvWidth,
                    uvRect.yMin + y * pixelUvHeight,
                    pixelUvWidth,
                    pixelUvHeight);

                AddFrontBackItemFaces(vertices, normals, uvs, triangles, x0, x1, y0, y1, halfThickness, pixelUv);

                if (!IsMaskOpaque(opaqueMask, pixelWidth, pixelHeight, x - 1, y))
                {
                    AddQuad(
                        vertices, normals, uvs, triangles,
                        new Vector3(x0, y0, -halfThickness),
                        new Vector3(x0, y1, -halfThickness),
                        new Vector3(x0, y1, halfThickness),
                        new Vector3(x0, y0, halfThickness),
                        Vector3.left,
                        pixelUv,
                        flipWinding: false);
                }

                if (!IsMaskOpaque(opaqueMask, pixelWidth, pixelHeight, x + 1, y))
                {
                    AddQuad(
                        vertices, normals, uvs, triangles,
                        new Vector3(x1, y0, halfThickness),
                        new Vector3(x1, y1, halfThickness),
                        new Vector3(x1, y1, -halfThickness),
                        new Vector3(x1, y0, -halfThickness),
                        Vector3.right,
                        pixelUv,
                        flipWinding: false);
                }

                if (!IsMaskOpaque(opaqueMask, pixelWidth, pixelHeight, x, y + 1))
                {
                    AddQuad(
                        vertices, normals, uvs, triangles,
                        new Vector3(x0, y1, halfThickness),
                        new Vector3(x0, y1, -halfThickness),
                        new Vector3(x1, y1, -halfThickness),
                        new Vector3(x1, y1, halfThickness),
                        Vector3.up,
                        pixelUv,
                        flipWinding: false);
                }

                if (!IsMaskOpaque(opaqueMask, pixelWidth, pixelHeight, x, y - 1))
                {
                    AddQuad(
                        vertices, normals, uvs, triangles,
                        new Vector3(x0, y0, -halfThickness),
                        new Vector3(x0, y0, halfThickness),
                        new Vector3(x1, y0, halfThickness),
                        new Vector3(x1, y0, -halfThickness),
                        Vector3.down,
                        pixelUv,
                        flipWinding: false);
                }
            }
        }

        Mesh mesh = new Mesh
        {
            indexFormat = IndexFormat.UInt32
        };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static bool TryGetTexturePixels(Texture2D texture, out Color32[] pixels)
    {
        pixels = null;
        if (texture == null)
            return false;

        try
        {
            pixels = texture.GetPixels32();
            return pixels != null && pixels.Length == texture.width * texture.height;
        }
        catch (UnityException)
        {
            return false;
        }
    }

    private static bool IsMaskOpaque(bool[] mask, int width, int height, int x, int y)
    {
        if (mask == null || x < 0 || y < 0 || x >= width || y >= height)
            return false;

        return mask[y * width + x];
    }

    private static void AddFrontBackItemFaces(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<int> triangles,
        float x0,
        float x1,
        float y0,
        float y1,
        float halfThickness,
        Rect uvRect)
    {
        AddQuad(
            vertices, normals, uvs, triangles,
            new Vector3(x0, y0, halfThickness),
            new Vector3(x0, y1, halfThickness),
            new Vector3(x1, y1, halfThickness),
            new Vector3(x1, y0, halfThickness),
            Vector3.forward,
            uvRect,
            flipWinding: false);

        AddQuad(
            vertices, normals, uvs, triangles,
            new Vector3(x1, y0, -halfThickness),
            new Vector3(x1, y1, -halfThickness),
            new Vector3(x0, y1, -halfThickness),
            new Vector3(x0, y0, -halfThickness),
            Vector3.back,
            uvRect,
            flipWinding: false);
    }

    private static void AddQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<int> triangles,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3,
        Vector3 normal,
        Rect uvRect,
        bool flipWinding)
    {
        int start = vertices.Count;
        vertices.Add(v0);
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        uvs.Add(new Vector2(uvRect.xMin, uvRect.yMin));
        uvs.Add(new Vector2(uvRect.xMin, uvRect.yMax));
        uvs.Add(new Vector2(uvRect.xMax, uvRect.yMax));
        uvs.Add(new Vector2(uvRect.xMax, uvRect.yMin));

        if (flipWinding)
        {
            triangles.Add(start + 0);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start + 0);
            triangles.Add(start + 3);
            triangles.Add(start + 2);
            return;
        }

        triangles.Add(start + 0);
        triangles.Add(start + 1);
        triangles.Add(start + 2);
        triangles.Add(start + 0);
        triangles.Add(start + 2);
        triangles.Add(start + 3);
    }

    private Material GetOrCreateRuntimeFlatItemMaterial()
    {
        if (runtimeFlatItemMaterial != null)
            return runtimeFlatItemMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");
        if (shader == null)
            return null;

        runtimeFlatItemMaterial = new Material(shader)
        {
            name = "HeldFlatItemRuntimeMaterial",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = (int)RenderQueue.AlphaTest
        };

        ConfigureHeldFlatItemMaterial(runtimeFlatItemMaterial);

        return runtimeFlatItemMaterial;
    }

    private static Mesh BuildFlatItemMesh(float aspect, Rect uvRect)
    {
        float halfWidth = Mathf.Max(0.01f, aspect) * 0.5f;
        float halfHeight = 0.5f;

        Vector2[] uv =
        {
            new Vector2(uvRect.xMin, uvRect.yMin),
            new Vector2(uvRect.xMin, uvRect.yMax),
            new Vector2(uvRect.xMax, uvRect.yMax),
            new Vector2(uvRect.xMax, uvRect.yMin)
        };

        Mesh mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-halfWidth, -halfHeight, 0f),
            new Vector3(-halfWidth, halfHeight, 0f),
            new Vector3(halfWidth, halfHeight, 0f),
            new Vector3(halfWidth, -halfHeight, 0f)
        };
        mesh.uv = uv;
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.normals = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.forward,
            Vector3.forward
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ApplyFlatItemTexture(Texture texture)
    {
        if (flatItemMeshRenderer == null || texture == null)
            return;

        ConfigurePixelArtTexture(texture);

        if (flatItemPropertyBlock == null)
            flatItemPropertyBlock = new MaterialPropertyBlock();

        flatItemPropertyBlock.Clear();
        flatItemPropertyBlock.SetTexture("_BaseMap", texture);
        flatItemPropertyBlock.SetTexture("_MainTex", texture);
        flatItemPropertyBlock.SetColor("_BaseColor", Color.white);
        flatItemPropertyBlock.SetColor("_Color", Color.white);
        flatItemMeshRenderer.SetPropertyBlock(flatItemPropertyBlock);
    }

    private static void ConfigurePixelArtTexture(Texture texture)
    {
        if (texture == null)
            return;

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.anisoLevel = 0;
    }

    private static void DestroyCachedMeshes<T>(Dictionary<T, Mesh> cache)
    {
        foreach (KeyValuePair<T, Mesh> pair in cache)
        {
            if (pair.Value != null)
                Destroy(pair.Value);
        }

        cache.Clear();
    }

    private void InvalidateFlatItemMeshCaches()
    {
        DestroyCachedMeshes(atlasFlatItemMeshCache);
        DestroyCachedMeshes(spriteFlatItemMeshCache);
    }

    private Material GetOrCreateFlatItemMaterialClone(Material source)
    {
        if (source == null)
            return null;

        if (flatItemMaterialCloneCache.TryGetValue(source, out Material cachedMaterial) && cachedMaterial != null)
            return cachedMaterial;

        Material clone = new Material(source)
        {
            name = source.name + "_HeldRuntime",
            hideFlags = HideFlags.HideAndDontSave
        };
        ConfigureHeldFlatItemMaterial(clone);
        flatItemMaterialCloneCache[source] = clone;
        return clone;
    }

    private void DestroyRuntimeMaterials(Dictionary<Material, Material> cache)
    {
        foreach (KeyValuePair<Material, Material> pair in cache)
        {
            if (pair.Value != null)
                Destroy(pair.Value);
        }

        cache.Clear();
    }

    private void ConfigureHeldFlatItemMaterial(Material material)
    {
        if (material == null)
            return;

        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", (float)CullMode.Off);

        if (material.HasProperty("_AlphaClip"))
            material.SetFloat("_AlphaClip", 1f);

        if (material.HasProperty("_Cutoff"))
            material.SetFloat("_Cutoff", Mathf.Clamp01(generatedItemAlphaThreshold));

        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 0f);

        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 1f);

        material.renderQueue = (int)RenderQueue.AlphaTest;
        material.SetOverrideTag("RenderType", "TransparentCutout");
    }

    private void SetVisible(bool visible)
    {
        if (visualRoot != null && visualRoot.activeSelf != visible)
            visualRoot.SetActive(visible);

        isVisible = visible;
    }

    private bool ShouldDisplayInCurrentView()
    {
        if (visibilityMode == ViewVisibilityMode.Always)
            return true;

        bool isThirdPerson = fpsController != null && fpsController.IsThirdPerson();
        switch (visibilityMode)
        {
            case ViewVisibilityMode.ThirdPersonOnly:
                return isThirdPerson;
            case ViewVisibilityMode.FirstPersonOnly:
            default:
                return !isThirdPerson;
        }
    }

    private void ApplyVisualHierarchyLayer()
    {
        if (!applyGameObjectLayerToVisuals || followRoot == null)
            return;

        ApplyLayerRecursively(followRoot, gameObject.layer);
    }

    private void UpdateArmMeshVisibility(bool isHoldingSomething)
    {
        if (playerArmRenderer == null)
            return;

        bool shouldShowArmMesh = !hideArmMeshWhenHolding || !isHoldingSomething;
        if (playerArmRenderer.enabled != shouldShowArmMesh)
            playerArmRenderer.enabled = shouldShowArmMesh;
    }

    private void CaptureMiningRigPose(bool force = false)
    {
        if (!CanDriveRigMotion() || firstPersonRigRoot == null)
            return;

        if (!force && miningRigPoseCaptured && capturedMiningRigRoot == firstPersonRigRoot)
            return;

        if (miningRigPoseCaptured && capturedMiningRigRoot != null && capturedMiningRigRoot != firstPersonRigRoot)
            RestoreMiningRigPose();

        capturedMiningRigRoot = firstPersonRigRoot;
        miningRigIdleLocalPosition = firstPersonRigRoot.localPosition;
        miningRigIdleLocalRotation = firstPersonRigRoot.localRotation;
        miningRigPoseCaptured = true;
        miningMotionWeight = 0f;
        miningSwingTimer = 0f;
        placeSwingElapsed = float.PositiveInfinity;

        if (blockBreaker != null)
            lastSeenPlaceActionVersion = blockBreaker.PlaceActionVersion;
    }

    private void RestoreMiningRigPose()
    {
        if (!miningRigPoseCaptured || capturedMiningRigRoot == null)
            return;

        capturedMiningRigRoot.localPosition = miningRigIdleLocalPosition;
        capturedMiningRigRoot.localRotation = miningRigIdleLocalRotation;
        miningMotionWeight = 0f;
        miningSwingTimer = 0f;
        placeSwingElapsed = float.PositiveInfinity;
    }

    private void ApplyMiningMotion()
    {
        if (!CanDriveRigMotion())
        {
            if (miningRigPoseCaptured)
            {
                RestoreMiningRigPose();
                miningRigPoseCaptured = false;
                capturedMiningRigRoot = null;
            }
            return;
        }

        if (firstPersonRigRoot == null)
            return;

        CaptureMiningRigPose();
        UpdatePlaceMotionTrigger();

        float blendFactor = miningMotionBlendSpeed <= 0f
            ? 1f
            : 1f - Mathf.Exp(-miningMotionBlendSpeed * Time.deltaTime);

        bool allowFirstPersonMotion = fpsController == null || !fpsController.IsThirdPerson();
        bool shouldAnimate = allowFirstPersonMotion && blockBreaker != null && blockBreaker.IsBreakInProgress;

        float targetWeight = shouldAnimate ? 1f : 0f;
        miningMotionWeight = blendFactor >= 1f
            ? targetWeight
            : Mathf.Lerp(miningMotionWeight, targetWeight, blendFactor);

        if (shouldAnimate)
        {
            miningSwingTimer += Time.deltaTime * miningSwingFrequency;
        }
        else if (miningMotionWeight <= 0.001f)
        {
            miningSwingTimer = 0f;
        }

        float phase = miningSwingTimer * Mathf.PI * 2f;
        float swing = 0.5f - (0.5f * Mathf.Cos(phase));
        float sway = Mathf.Sin(phase);
        float progressMultiplier = shouldAnimate && blockBreaker != null
            ? Mathf.Lerp(0.9f, 1f, blockBreaker.BreakProgressNormalized)
            : 1f;
        float miningWeight = miningMotionWeight * progressMultiplier;

        float placeWeight = 0f;
        if (enablePlaceMotion && allowFirstPersonMotion && placeSwingElapsed < float.PositiveInfinity)
        {
            placeSwingElapsed += Time.deltaTime;
            float placeDuration = Mathf.Max(0.01f, placeSwingDuration);
            float normalizedPlaceTime = Mathf.Clamp01(placeSwingElapsed / placeDuration);
            placeWeight = Mathf.Sin(normalizedPlaceTime * Mathf.PI);

            if (normalizedPlaceTime >= 1f)
                placeSwingElapsed = float.PositiveInfinity;
        }
        else if (!enablePlaceMotion || !allowFirstPersonMotion)
        {
            placeSwingElapsed = float.PositiveInfinity;
        }

        Vector3 targetPosition = miningRigIdleLocalPosition + new Vector3(
            miningLocalPositionOffset.x * sway * miningWeight,
            miningLocalPositionOffset.y * swing * miningWeight,
            miningLocalPositionOffset.z * swing * miningWeight);
        targetPosition += placeLocalPositionOffset * placeWeight;

        Quaternion miningRotation = Quaternion.Euler(
            miningLocalEulerAngles.x * swing * miningWeight,
            miningLocalEulerAngles.y * sway * miningWeight,
            miningLocalEulerAngles.z * sway * miningWeight);
        Quaternion placeRotation = Quaternion.Euler(
            placeLocalEulerAngles.x * placeWeight,
            placeLocalEulerAngles.y * placeWeight,
            placeLocalEulerAngles.z * placeWeight);
        Quaternion targetRotation = miningRigIdleLocalRotation * miningRotation * placeRotation;

        firstPersonRigRoot.localPosition = blendFactor >= 1f
            ? targetPosition
            : Vector3.Lerp(firstPersonRigRoot.localPosition, targetPosition, blendFactor);
        firstPersonRigRoot.localRotation = blendFactor >= 1f
            ? targetRotation
            : Quaternion.Slerp(firstPersonRigRoot.localRotation, targetRotation, blendFactor);
    }

    private void UpdatePlaceMotionTrigger()
    {
        if (blockBreaker == null)
            return;

        int currentPlaceActionVersion = blockBreaker.PlaceActionVersion;
        if (lastSeenPlaceActionVersion < 0)
        {
            lastSeenPlaceActionVersion = currentPlaceActionVersion;
            return;
        }

        if (currentPlaceActionVersion == lastSeenPlaceActionVersion)
            return;

        lastSeenPlaceActionVersion = currentPlaceActionVersion;
        placeSwingElapsed = 0f;
    }

    private bool CanDriveRigMotion()
    {
        return enableMiningMotion && visibilityMode != ViewVisibilityMode.ThirdPersonOnly;
    }

    private void ClearShownState()
    {
        shownItem = null;
        shownBlockType = BlockType.Air;
        shownVisualKind = HeldVisualKind.None;
        HideAllVisualChildren();
    }

    private static Vector3 SanitizeScale(Vector3 scale)
    {
        return new Vector3(
            SanitizeScaleAxis(scale.x),
            SanitizeScaleAxis(scale.y),
            SanitizeScaleAxis(scale.z));
    }

    private static float SanitizeScaleAxis(float value)
    {
        return Mathf.Approximately(value, 0f) ? 0.01f : value;
    }

    private static float GetSafeInverseScale(float value)
    {
        return Mathf.Approximately(value, 0f) ? 1f : 1f / value;
    }

    private static Transform FindDirectChildByName(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child != null && child.name == childName)
                return child;
        }

        return null;
    }

    private static void DestroyDuplicateChildren(Transform parent, string childName, Transform keep)
    {
        if (parent == null)
            return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child == null || child == keep || child.name != childName)
                continue;

            Destroy(child.gameObject);
        }
    }

    private static void ResetTransform(Transform target)
    {
        if (target == null)
            return;

        target.localPosition = Vector3.zero;
        target.localRotation = Quaternion.identity;
        target.localScale = Vector3.one;
    }

    private static void ApplyLayerRecursively(GameObject target, int layer)
    {
        if (target == null)
            return;

        target.layer = layer;
        Transform root = target.transform;
        for (int i = 0; i < root.childCount; i++)
            ApplyLayerRecursively(root.GetChild(i).gameObject, layer);
    }
}
