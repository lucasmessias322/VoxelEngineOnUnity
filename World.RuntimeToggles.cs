using System;
using UnityEngine;

public partial class World
{
    private void HandleBlockColliderToggle()
    {
        if (lastEnableBlockColliders == enableBlockColliders)
            return;

        lastEnableBlockColliders = enableBlockColliders;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk subchunk = chunk.subchunks[i];
                if (subchunk == null)
                    continue;

                subchunk.SetColliderSystemEnabled(chunkIsSimulated);

                if (chunkIsSimulated &&
                    chunk.hasVoxelData &&
                    subchunk.hasGeometry &&
                    subchunk.CanHaveColliders &&
                    !subchunk.HasColliderData)
                {
                    EnqueueColliderBuild(kv.Key, chunk.generation, i);
                }
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);

        if (!enableBlockColliders)
        {
            queuedColliderBuilds.Clear();
            queuedColliderBuildsByKey.Clear();
            return;
        }

        foreach (var kv in activeChunks)
            RequestHighBuildMeshRebuild(kv.Key);
    }

    private void HandleVisualFeatureToggle()
    {
        bool lightingChanged = lastEnableVoxelLighting != enableVoxelLighting;
        bool aoChanged = lastEnableAmbientOcclusion != enableAmbientOcclusion;
        ChunkOpaqueRenderBackendKind resolvedOpaqueBackend = GetActiveOpaqueRenderBackendKind();
        bool opaqueBackendChanged = lastResolvedOpaqueRenderBackendKind != resolvedOpaqueBackend;
        bool horizontalLightingChanged = enableVoxelLighting && lastEnableHorizontalSkylight != enableHorizontalSkylight;
        bool horizontalLightingParamsChanged =
            enableVoxelLighting &&
            enableHorizontalSkylight &&
            (lastHorizontalSkylightStepLoss != horizontalSkylightStepLoss ||
             lastSunlightSmoothingPadding != sunlightSmoothingPadding);

        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastResolvedOpaqueRenderBackendKind = resolvedOpaqueBackend;
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;

        if (!lightingChanged &&
            !aoChanged &&
            !opaqueBackendChanged &&
            !horizontalLightingChanged &&
            !horizontalLightingParamsChanged)
            return;

        foreach (var kv in activeChunks)
            RequestChunkRebuild(kv.Key, GetFullSubchunkMask(), false);
    }

    private void RefreshSimulationDistanceState(Vector2Int simulationCenter)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            bool chunkIsSimulated = enableBlockColliders && IsCoordInsideSimulationDistance(kv.Key, simulationCenter);
            for (int i = 0; i < chunk.subchunks.Length; i++)
            {
                Subchunk subchunk = chunk.subchunks[i];
                if (subchunk == null)
                    continue;

                if (!chunkIsSimulated)
                {
                    subchunk.SetColliderSystemEnabled(false);
                    continue;
                }

                if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated || !subchunk.hasGeometry || !subchunk.CanHaveColliders)
                {
                    subchunk.SetColliderSystemEnabled(false);
                    continue;
                }

                if (subchunk.HasColliderData)
                {
                    subchunk.SetColliderSystemEnabled(true);
                    continue;
                }

                EnqueueColliderBuild(kv.Key, chunk.generation, i);
            }
        }

        SetHighBuildCollidersEnabled(enableBlockColliders);
    }

    [Header("Platform Render Profile")]
    [Tooltip("Quando ativado, resolve automaticamente um conjunto de materiais mais pesado para desktop e um conjunto mais leve para mobile em runtime.")]
    public bool enableAutomaticPlatformRenderProfile = true;
    [Tooltip("Override opcional do conjunto completo de materiais usado em desktop. Se vazio, reutiliza o array Material configurado no inspector.")]
    public Material[] desktopMaterialOverride;
    [Tooltip("Override opcional do conjunto completo de materiais usado em mobile. Se vazio, o World cria uma variante runtime a partir do array Material.")]
    public Material[] mobileMaterialOverride;
    [Tooltip("Shader opaco leve usado para montar a variante runtime mobile quando mobileMaterialOverride estiver vazio.")]
    public Shader mobileOpaqueShader;
    [Tooltip("Desliga o ray extra da oclusao por secoes no perfil mobile para reduzir custo de CPU.")]
    public bool mobileDisableAdvancedOcclusionRay = true;
    [Min(32)]
    [Tooltip("Limite do BFS de oclusao por secoes no perfil mobile.")]
    public int mobileSectionOcclusionBudget = 96;
    [Min(1)]
    [Tooltip("Limite de jobs de dados processados por frame no perfil mobile.")]
    public int mobileMaxDataCompletionsPerFrame = 1;
    [Min(1)]
    [Tooltip("Limite de jobs de dados em voo no perfil mobile.")]
    public int mobileMaxPendingDataJobs = 1;
    [Min(1)]
    [Tooltip("Limite de rebuilds de chunk por frame no perfil mobile.")]
    public int mobileMaxChunkRebuildsPerFrame = 1;
    [Range(1f, 8f)]
    [Tooltip("Budget de tempo de frame reservado para concluir jobs no perfil mobile.")]
    public float mobileFrameTimeBudgetMS = 2.5f;

    private const string MobileOpaqueShaderName = "Voxel/URP/Voxel Blocks Mobile Classic";

    private Material[] baseInspectorChunkMaterials;
    private Material[] activeChunkMaterials;
    private Material[] runtimeDesktopChunkMaterials;
    private Material[] runtimeMobileChunkMaterials;
    private bool platformRenderProfileInitialized;
    private bool platformRenderProfileDirty = true;
    private bool lastAppliedMobileRenderProfile;
    private bool basePlatformQualityCaptured;
    private bool baseEnableMinecraftAdvancedOcclusionRay;
    private int baseSectionOcclusionPropagationBudgetPerFrame;
    private int baseMaxDataCompletionsPerFrame;
    private int baseMaxPendingDataJobs;
    private int baseMaxChunkRebuildsPerFrame;
    private float baseFrameTimeBudgetMS;

    public Material[] GetRuntimeChunkMaterials()
    {
        if (!platformRenderProfileInitialized)
            InitializePlatformRenderProfileState();

        return activeChunkMaterials != null && activeChunkMaterials.Length > 0
            ? activeChunkMaterials
            : (Material ?? Array.Empty<Material>());
    }

    public Material GetRuntimeChunkMaterial(int preferredIndex = 0)
    {
        Material[] materials = GetRuntimeChunkMaterials();
        if (materials == null || materials.Length == 0)
            return null;

        int clamped = Mathf.Clamp(preferredIndex, 0, materials.Length - 1);
        if (materials[clamped] != null)
            return materials[clamped];

        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] != null)
                return materials[i];
        }

        return null;
    }

    private void InitializePlatformRenderProfileState()
    {
        if (platformRenderProfileInitialized)
            return;

        baseInspectorChunkMaterials = CloneMaterialReferenceArray(Material);
        CaptureBasePlatformQualitySnapshot();
        platformRenderProfileInitialized = true;
        platformRenderProfileDirty = true;
    }

    private void MarkPlatformRenderProfileDirty()
    {
        baseInspectorChunkMaterials = CloneMaterialReferenceArray(Material);
        platformRenderProfileDirty = true;
    }

    private void ApplyPlatformRenderProfileIfNeeded(bool forceRebuild = false)
    {
        if (!platformRenderProfileInitialized)
            InitializePlatformRenderProfileState();

        bool useMobileProfile = enableAutomaticPlatformRenderProfile && Application.isMobilePlatform;
        if (!platformRenderProfileDirty &&
            activeChunkMaterials != null &&
            lastAppliedMobileRenderProfile == useMobileProfile)
        {
            return;
        }

        Material[] oldDesktopMaterials = runtimeDesktopChunkMaterials;
        Material[] oldMobileMaterials = runtimeMobileChunkMaterials;
        if (platformRenderProfileDirty)
        {
            runtimeDesktopChunkMaterials = null;
            runtimeMobileChunkMaterials = null;
        }

        ApplyPlatformQualityProfile(useMobileProfile);

        Material[] previousMaterials = activeChunkMaterials;
        activeChunkMaterials = ResolveActiveChunkMaterials(useMobileProfile);
        lastAppliedMobileRenderProfile = useMobileProfile;
        platformRenderProfileDirty = false;

        bool materialsChanged = !AreSameMaterialArrays(previousMaterials, activeChunkMaterials);
        if (!materialsChanged && !forceRebuild)
            return;

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null)
                continue;

            chunk.InitializeSubchunks(GetRuntimeChunkMaterials(), GetResolvedVisualSubchunksPerRendererForCoord(kv.Key));
            chunk.UpdateWorldBounds();
            RequestChunkRebuild(kv.Key, GetFullSubchunkMask(), false);
        }

        RefreshHighBuildMaterialSet();
        lastResolvedOpaqueRenderBackendKind = GetActiveOpaqueRenderBackendKind();

        if (!ReferenceEquals(oldDesktopMaterials, runtimeDesktopChunkMaterials))
            ReleaseRuntimeMaterialArray(ref oldDesktopMaterials);
        if (!ReferenceEquals(oldMobileMaterials, runtimeMobileChunkMaterials))
            ReleaseRuntimeMaterialArray(ref oldMobileMaterials);
    }

    private Material[] ResolveActiveChunkMaterials(bool useMobileProfile)
    {
        if (useMobileProfile)
        {
            if (runtimeMobileChunkMaterials == null)
                runtimeMobileChunkMaterials = CreateRuntimeChunkMaterials(useMobileProfile: true);

            return runtimeMobileChunkMaterials ?? baseInspectorChunkMaterials ?? Array.Empty<Material>();
        }

        if (runtimeDesktopChunkMaterials == null)
            runtimeDesktopChunkMaterials = CreateRuntimeChunkMaterials(useMobileProfile: false);

        return runtimeDesktopChunkMaterials ?? baseInspectorChunkMaterials ?? Array.Empty<Material>();
    }

    private Material[] CreateRuntimeChunkMaterials(bool useMobileProfile)
    {
        Material[] sourceMaterials = GetPreferredChunkMaterialSource(useMobileProfile);
        if (sourceMaterials == null || sourceMaterials.Length == 0)
            return Array.Empty<Material>();

        Material[] clones = new Material[sourceMaterials.Length];
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            Material source = sourceMaterials[i];
            if (source == null)
                continue;

            clones[i] = new Material(source)
            {
                name = useMobileProfile ? $"{source.name}_MobileRuntime" : $"{source.name}_DesktopRuntime"
            };
        }

        if (useMobileProfile &&
            (mobileMaterialOverride == null || mobileMaterialOverride.Length == 0) &&
            clones.Length > 0 &&
            clones[0] != null)
        {
            Shader resolvedMobileShader = ResolveMobileOpaqueShader();
            if (resolvedMobileShader != null)
            {
                clones[0].shader = resolvedMobileShader;
                clones[0].SetFloat("_UseVertexPulling", 0f);
                clones[0].SetFloat("_UseCompactOpaqueFaces", 0f);
                clones[0].SetFloat("_UseIndirectVertexPulling", 0f);
            }
        }

        return clones;
    }

    private Material[] GetPreferredChunkMaterialSource(bool useMobileProfile)
    {
        Material[] overrideMaterials = useMobileProfile ? mobileMaterialOverride : desktopMaterialOverride;
        if (overrideMaterials != null && overrideMaterials.Length > 0)
            return overrideMaterials;

        return baseInspectorChunkMaterials ?? Material;
    }

    private Shader ResolveMobileOpaqueShader()
    {
        if (mobileOpaqueShader != null)
            return mobileOpaqueShader;

        mobileOpaqueShader = Shader.Find(MobileOpaqueShaderName);
        return mobileOpaqueShader;
    }

    private void CaptureBasePlatformQualitySnapshot()
    {
        if (basePlatformQualityCaptured)
            return;

        baseEnableMinecraftAdvancedOcclusionRay = enableMinecraftAdvancedOcclusionRay;
        baseSectionOcclusionPropagationBudgetPerFrame = sectionOcclusionPropagationBudgetPerFrame;
        baseMaxDataCompletionsPerFrame = maxDataCompletionsPerFrame;
        baseMaxPendingDataJobs = maxPendingDataJobs;
        baseMaxChunkRebuildsPerFrame = maxChunkRebuildsPerFrame;
        baseFrameTimeBudgetMS = frameTimeBudgetMS;
        basePlatformQualityCaptured = true;
    }

    private void ApplyPlatformQualityProfile(bool useMobileProfile)
    {
        CaptureBasePlatformQualitySnapshot();

        enableMinecraftAdvancedOcclusionRay = baseEnableMinecraftAdvancedOcclusionRay;
        sectionOcclusionPropagationBudgetPerFrame = baseSectionOcclusionPropagationBudgetPerFrame;
        maxDataCompletionsPerFrame = baseMaxDataCompletionsPerFrame;
        maxPendingDataJobs = baseMaxPendingDataJobs;
        maxChunkRebuildsPerFrame = baseMaxChunkRebuildsPerFrame;
        frameTimeBudgetMS = baseFrameTimeBudgetMS;

        if (!useMobileProfile)
            return;

        if (mobileDisableAdvancedOcclusionRay)
            enableMinecraftAdvancedOcclusionRay = false;

        sectionOcclusionPropagationBudgetPerFrame = Mathf.Min(
            sectionOcclusionPropagationBudgetPerFrame,
            Mathf.Max(32, mobileSectionOcclusionBudget));
        maxDataCompletionsPerFrame = Mathf.Min(
            maxDataCompletionsPerFrame,
            Mathf.Max(1, mobileMaxDataCompletionsPerFrame));
        maxPendingDataJobs = Mathf.Min(
            maxPendingDataJobs,
            Mathf.Max(1, mobileMaxPendingDataJobs));
        maxChunkRebuildsPerFrame = Mathf.Min(
            maxChunkRebuildsPerFrame,
            Mathf.Max(1, mobileMaxChunkRebuildsPerFrame));
        frameTimeBudgetMS = Mathf.Min(
            frameTimeBudgetMS,
            Mathf.Max(1f, mobileFrameTimeBudgetMS));
    }

    private void RefreshHighBuildMaterialSet()
    {
        Material[] materials = GetRuntimeChunkMaterials();
        foreach (var kv in highBuildMeshes)
        {
            HighBuildMeshData data = kv.Value;
            if (data?.meshRenderer == null)
                continue;

            data.meshRenderer.sharedMaterials = materials;
            ApplyBiomeTintToRenderer(data.meshRenderer, new Vector2Int(kv.Key.x, kv.Key.z));
        }
    }

    private void ReleaseRuntimeChunkMaterialVariants()
    {
        ReleaseRuntimeMaterialArray(ref runtimeDesktopChunkMaterials);
        ReleaseRuntimeMaterialArray(ref runtimeMobileChunkMaterials);
        activeChunkMaterials = null;
    }

    private static void ReleaseRuntimeMaterialArray(ref Material[] materials)
    {
        if (materials == null)
            return;

        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] == null)
                continue;

            if (Application.isPlaying)
                Destroy(materials[i]);
            else
                DestroyImmediate(materials[i]);
        }

        materials = null;
    }

    private static Material[] CloneMaterialReferenceArray(Material[] materials)
    {
        if (materials == null || materials.Length == 0)
            return Array.Empty<Material>();

        Material[] clone = new Material[materials.Length];
        Array.Copy(materials, clone, materials.Length);
        return clone;
    }

    private static bool AreSameMaterialArrays(Material[] a, Material[] b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return a == b;
        if (a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
                return false;
        }

        return true;
    }
}
