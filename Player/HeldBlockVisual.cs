using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class HeldBlockVisual : MonoBehaviour
{
    private enum HeldVisualKind
    {
        None,
        Block,
        Sprite,
        Prefab
    }

    [Header("References")]
    [SerializeField] private HotbarMirror hotbar;
    [SerializeField] private Transform handAnchor;
    [SerializeField] private World world;

    [Header("Default View")]
    [SerializeField] private Vector3 localPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private Vector3 localEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private float heldScale = 0.28f;
    [SerializeField] private bool hideWhenInventoryOpen = true;
    [SerializeField] private bool castShadows = false;
    [SerializeField] private bool receiveShadows = false;

    private readonly Dictionary<BlockType, Mesh> meshCache = new Dictionary<BlockType, Mesh>();

    private GameObject visualRoot;
    private GameObject blockVisualObject;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private GameObject spriteVisualObject;
    private SpriteRenderer spriteRenderer;
    private GameObject heldPrefabInstance;
    private GameObject heldPrefabSource;
    private Item shownItem;
    private BlockType shownBlockType = BlockType.Air;
    private HeldVisualKind shownVisualKind;
    private bool isVisible;

    private void Awake()
    {
        ResolveReferences();
        EnsureVisualObject();
        ApplyViewTransform(null);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void LateUpdate()
    {
        RefreshHeldBlock(forceRefresh: false);
    }

    [ContextMenu("Refresh Held Visual")]
    public void RefreshNow()
    {
        ResolveReferences();
        EnsureVisualObject();
        ApplyViewTransform(null);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
            return;

        ApplyViewTransform(shownItem);
        ApplyRendererSettings();
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnDestroy()
    {
        foreach (KeyValuePair<BlockType, Mesh> pair in meshCache)
        {
            if (pair.Value != null)
                Destroy(pair.Value);
        }

        meshCache.Clear();
        ClearHeldPrefabInstance();
    }

    private void ResolveReferences()
    {
        if (hotbar == null)
            hotbar = FindAnyObjectByType<HotbarMirror>();

        if (world == null)
            world = World.Instance != null ? World.Instance : FindAnyObjectByType<World>();

        if (handAnchor == null)
            handAnchor = transform;
    }

    private void EnsureVisualObject()
    {
        Transform parent = handAnchor != null ? handAnchor : transform;

        if (visualRoot == null)
        {
            visualRoot = new GameObject("HeldItemVisual");
            visualRoot.transform.SetParent(parent, false);
            visualRoot.SetActive(false);
        }
        else if (visualRoot.transform.parent != parent)
        {
            visualRoot.transform.SetParent(parent, false);
        }

        if (blockVisualObject == null)
        {
            blockVisualObject = new GameObject("BlockVisual");
            blockVisualObject.transform.SetParent(visualRoot.transform, false);
            meshFilter = blockVisualObject.AddComponent<MeshFilter>();
            meshRenderer = blockVisualObject.AddComponent<MeshRenderer>();
            blockVisualObject.SetActive(false);
        }

        if (spriteVisualObject == null)
        {
            spriteVisualObject = new GameObject("SpriteVisual");
            spriteVisualObject.transform.SetParent(visualRoot.transform, false);
            spriteRenderer = spriteVisualObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = null;
            spriteVisualObject.SetActive(false);
        }

        if (meshFilter == null)
            meshFilter = blockVisualObject.GetComponent<MeshFilter>();
        if (meshRenderer == null)
            meshRenderer = blockVisualObject.GetComponent<MeshRenderer>();
        if (spriteRenderer == null)
            spriteRenderer = spriteVisualObject.GetComponent<SpriteRenderer>();

        ApplyRendererSettings();
    }

    private void ApplyViewTransform(Item selectedItem)
    {
        if (visualRoot == null)
            return;

        Vector3 targetPosition = localPosition;
        Vector3 targetEulerAngles = localEulerAngles;
        Vector3 targetScale = Vector3.one * Mathf.Max(0.01f, heldScale);

        if (selectedItem != null && selectedItem.overrideHeldTransform)
        {
            targetPosition = selectedItem.heldLocalPosition;
            targetEulerAngles = selectedItem.heldLocalEulerAngles;
            targetScale = SanitizeScale(selectedItem.heldLocalScale);
        }

        visualRoot.transform.localPosition = targetPosition;
        visualRoot.transform.localRotation = Quaternion.Euler(targetEulerAngles);
        visualRoot.transform.localScale = targetScale;
    }

    private void ApplyRendererSettings()
    {
        if (meshRenderer != null)
        {
            meshRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            meshRenderer.receiveShadows = receiveShadows;
        }

        if (spriteRenderer != null)
        {
            spriteRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            spriteRenderer.receiveShadows = receiveShadows;
        }
    }

    private void RefreshHeldBlock(bool forceRefresh)
    {
        if (visualRoot == null)
            return;

        if (hideWhenInventoryOpen && PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        if (hotbar == null || !hotbar.TryGetSelectedItem(out Item selectedItem) || selectedItem == null)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        BlockType selectedBlockType = BlockType.Air;
        bool hasBlockSelection = hotbar.TryGetSelectedBlockType(out selectedBlockType) && selectedBlockType != BlockType.Air;
        HeldVisualKind desiredKind = GetDesiredVisualKind(selectedItem, hasBlockSelection);
        if (desiredKind == HeldVisualKind.None)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        ApplyViewTransform(selectedItem);

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
            case HeldVisualKind.Sprite:
                shownSuccessfully = ShowSprite(selectedItem.icon);
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

        if (hasBlockSelection)
            return HeldVisualKind.Block;

        if (selectedItem.icon != null)
            return HeldVisualKind.Sprite;

        return HeldVisualKind.None;
    }

    private bool ShowBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        if (world == null)
            world = World.Instance;

        if (world == null || world.Material == null || world.Material.Length == 0)
            return false;

        Mesh heldMesh = GetOrCreateMesh(blockType);
        if (heldMesh == null)
            return false;

        HideAllVisualChildren();
        meshFilter.sharedMesh = heldMesh;
        meshRenderer.materials = world.Material;
        blockVisualObject.SetActive(true);
        return true;
    }

    private bool ShowSprite(Sprite iconSprite)
    {
        if (iconSprite == null || spriteRenderer == null)
            return false;

        HideAllVisualChildren();
        spriteRenderer.sprite = iconSprite;
        spriteRenderer.color = Color.white;
        spriteVisualObject.SetActive(true);
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
        instance.SetActive(true);
        return true;
    }

    private GameObject EnsureHeldPrefabInstance(GameObject prefab)
    {
        if (prefab == null || visualRoot == null)
            return null;

        if (heldPrefabInstance != null && heldPrefabSource == prefab)
            return heldPrefabInstance;

        ClearHeldPrefabInstance();

        heldPrefabInstance = Instantiate(prefab, visualRoot.transform, false);
        heldPrefabInstance.name = prefab.name + "_Held";
        heldPrefabInstance.transform.localPosition = Vector3.zero;
        heldPrefabInstance.transform.localRotation = Quaternion.identity;
        heldPrefabInstance.transform.localScale = Vector3.one;
        heldPrefabSource = prefab;
        return heldPrefabInstance;
    }

    private void ClearHeldPrefabInstance()
    {
        if (heldPrefabInstance != null)
            Destroy(heldPrefabInstance);

        heldPrefabInstance = null;
        heldPrefabSource = null;
    }

    private void HideAllVisualChildren()
    {
        if (blockVisualObject != null && blockVisualObject.activeSelf)
            blockVisualObject.SetActive(false);

        if (spriteVisualObject != null && spriteVisualObject.activeSelf)
        {
            spriteVisualObject.SetActive(false);
            if (spriteRenderer != null)
                spriteRenderer.sprite = null;
        }

        if (heldPrefabInstance != null && heldPrefabInstance.activeSelf)
            heldPrefabInstance.SetActive(false);
    }

    private Mesh GetOrCreateMesh(BlockType blockType)
    {
        if (meshCache.TryGetValue(blockType, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BlockDrop.BuildBlockMesh(world, blockType, out _);
        if (mesh == null)
            return null;

        mesh.name = $"HeldMesh_{blockType}";
        meshCache[blockType] = mesh;
        return mesh;
    }

    private void SetVisible(bool visible)
    {
        if (visualRoot != null && visualRoot.activeSelf != visible)
            visualRoot.SetActive(visible);

        isVisible = visible;
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
}
