using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public partial class World
{
    private static readonly int TreecutterCrackTextureId = Shader.PropertyToID("_CrackTex");
    private static readonly Vector3Int InvalidTreecutterCrackBlock = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

    private PlayerBlockBreaker treecutterCrackSource;
    private GameObject treecutterCrackOverlayObject;
    private MeshRenderer treecutterCrackOverlayRenderer;
    private Material treecutterCrackOverlayRuntimeMaterial;
    private Material treecutterCrackOverlayBaseMaterial;
    private Vector3Int treecutterCrackOverlayBlock = InvalidTreecutterCrackBlock;
    private int treecutterCrackOverlayStage = -1;

    private void UpdateTreecutterBreakCrackVisual(float now)
    {
        if (treecutterBreakStates.Count == 0)
        {
            HideTreecutterBreakCrackVisual();
            return;
        }

        foreach (KeyValuePair<Vector3Int, TreecutterBreakState> pair in treecutterBreakStates)
        {
            TreecutterBreakState state = pair.Value;
            if (!IsTreecutterBreakStateValid(state))
                continue;

            UpdateTreecutterBreakCrackVisual(
                state.targetLogPos,
                state.targetLogType,
                GetTreecutterBreakProgress01(state, now));
            return;
        }

        HideTreecutterBreakCrackVisual();
    }

    private void UpdateTreecutterBreakCrackVisual(Vector3Int blockPos, BlockType blockType, float progress01)
    {
        PlayerBlockBreaker source = ResolveTreecutterCrackSource();
        if (source == null)
        {
            HideTreecutterBreakCrackVisual();
            return;
        }

        Texture2D[] activeStages = ResolveTreecutterCrackStages(source, blockType);
        if (!HasTreecutterCrackStages(activeStages) || !EnsureTreecutterCrackOverlay(source))
        {
            HideTreecutterBreakCrackVisual();
            return;
        }

        float clampedProgress = Mathf.Clamp01(progress01);
        Bounds overlayBounds = ResolveTreecutterCrackBounds(blockPos, blockType);
        float shakeScale = 1f + Mathf.Sin((Time.time * 32f) + overlayBounds.center.sqrMagnitude) *
            0.01f *
            (0.35f + clampedProgress * 0.65f);

        treecutterCrackOverlayObject.SetActive(true);
        treecutterCrackOverlayObject.transform.position = overlayBounds.center;
        treecutterCrackOverlayObject.transform.localScale =
            overlayBounds.size * Mathf.Max(1.001f, source.crackOverlayScale) * shakeScale;

        int stageCount = activeStages.Length;
        int stage = Mathf.Clamp(Mathf.FloorToInt(clampedProgress * stageCount), 0, stageCount - 1);
        if (stage == treecutterCrackOverlayStage && blockPos == treecutterCrackOverlayBlock)
            return;

        Texture2D tex = activeStages[stage];
        if (tex == null && activeStages != source.breakCrackStages && HasTreecutterCrackStages(source.breakCrackStages))
        {
            int fallbackStage = Mathf.Clamp(
                Mathf.FloorToInt(clampedProgress * source.breakCrackStages.Length),
                0,
                source.breakCrackStages.Length - 1);
            tex = source.breakCrackStages[fallbackStage];
        }

        if (tex == null)
            return;

        treecutterCrackOverlayRuntimeMaterial.SetTexture(TreecutterCrackTextureId, tex);
        treecutterCrackOverlayStage = stage;
        treecutterCrackOverlayBlock = blockPos;
    }

    private PlayerBlockBreaker ResolveTreecutterCrackSource()
    {
        if (treecutterCrackSource != null)
            return treecutterCrackSource;

        treecutterCrackSource = FindAnyObjectByType<PlayerBlockBreaker>();
        return treecutterCrackSource;
    }

    private bool EnsureTreecutterCrackOverlay(PlayerBlockBreaker source)
    {
        if (source == null || source.breakCrackMaterial == null)
            return false;

        if (treecutterCrackOverlayObject == null)
        {
            treecutterCrackOverlayObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            treecutterCrackOverlayObject.name = "TreecutterBreakCrackOverlay";
            treecutterCrackOverlayObject.SetActive(false);
            treecutterCrackOverlayObject.transform.SetParent(transform, true);

            Collider col = treecutterCrackOverlayObject.GetComponent<Collider>();
            if (col != null)
                DestroyTreecutterCrackRuntimeObject(col);

            treecutterCrackOverlayRenderer = treecutterCrackOverlayObject.GetComponent<MeshRenderer>();
            treecutterCrackOverlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            treecutterCrackOverlayRenderer.receiveShadows = false;
            treecutterCrackOverlayRenderer.lightProbeUsage = LightProbeUsage.Off;
            treecutterCrackOverlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        if (treecutterCrackOverlayRuntimeMaterial == null ||
            treecutterCrackOverlayBaseMaterial != source.breakCrackMaterial)
        {
            if (treecutterCrackOverlayRuntimeMaterial != null)
                DestroyTreecutterCrackRuntimeObject(treecutterCrackOverlayRuntimeMaterial);

            treecutterCrackOverlayBaseMaterial = source.breakCrackMaterial;
            treecutterCrackOverlayRuntimeMaterial = new Material(source.breakCrackMaterial);
            treecutterCrackOverlayRenderer.sharedMaterial = treecutterCrackOverlayRuntimeMaterial;
            treecutterCrackOverlayStage = -1;
            treecutterCrackOverlayBlock = InvalidTreecutterCrackBlock;
        }

        return treecutterCrackOverlayRenderer != null && treecutterCrackOverlayRuntimeMaterial != null;
    }

    private static Texture2D[] ResolveTreecutterCrackStages(PlayerBlockBreaker source, BlockType blockType)
    {
        Texture2D[] preferredStages = IsTreeLogBlock(blockType)
            ? source.woodBreakCrackStages
            : source.breakCrackStages;

        return HasTreecutterCrackStages(preferredStages)
            ? preferredStages
            : source.breakCrackStages;
    }

    private static bool HasTreecutterCrackStages(Texture2D[] stages)
    {
        return stages != null && stages.Length > 0;
    }

    private Bounds ResolveTreecutterCrackBounds(Vector3Int blockPos, BlockType blockType)
    {
        if (blockData == null)
            return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);

        BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
        if (mappingResult == null)
            return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);

        BlockPlacementAxis placementAxis = GetPlacementAxisAt(blockPos, blockType);
        return BlockShapeUtility.GetWorldBounds(blockPos, blockType, mappingResult.Value, placementAxis);
    }

    private void HideTreecutterBreakCrackVisual()
    {
        treecutterCrackOverlayStage = -1;
        treecutterCrackOverlayBlock = InvalidTreecutterCrackBlock;

        if (treecutterCrackOverlayObject != null && treecutterCrackOverlayObject.activeSelf)
            treecutterCrackOverlayObject.SetActive(false);
    }

    private void DestroyTreecutterBreakCrackVisuals()
    {
        if (treecutterCrackOverlayRuntimeMaterial != null)
            DestroyTreecutterCrackRuntimeObject(treecutterCrackOverlayRuntimeMaterial);

        if (treecutterCrackOverlayObject != null)
            DestroyTreecutterCrackRuntimeObject(treecutterCrackOverlayObject);

        treecutterCrackSource = null;
        treecutterCrackOverlayObject = null;
        treecutterCrackOverlayRenderer = null;
        treecutterCrackOverlayRuntimeMaterial = null;
        treecutterCrackOverlayBaseMaterial = null;
        treecutterCrackOverlayStage = -1;
        treecutterCrackOverlayBlock = InvalidTreecutterCrackBlock;
    }

    private static void DestroyTreecutterCrackRuntimeObject(Object target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(target);
        else
            Object.DestroyImmediate(target);
    }
}
