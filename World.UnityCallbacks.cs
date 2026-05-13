using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Unity Callbacks

    private void OnValidate()
    {
        RefreshTerrainGenerationRuntimeState();

        if (!Application.isPlaying || isShuttingDown || activeChunks == null || activeChunks.Count == 0)
            return;

        int currentMaterialProfileHash = ComputeWorldMaterialProfileHash();
        if (lastWorldMaterialProfileHash != currentMaterialProfileHash)
        {
            lastWorldMaterialProfileHash = currentMaterialProfileHash;
            RefreshWorldMaterialProfileOnRenderers();
            RefreshRuntimeMaterialProfileConsumers();
        }

        loadedChunkCoordsBuffer.Clear();
        foreach (var kv in activeChunks)
            loadedChunkCoordsBuffer.Add(kv.Key);

        for (int i = 0; i < loadedChunkCoordsBuffer.Count; i++)
            RequestFullChunkRebuild(loadedChunkCoordsBuffer[i]);
    }

    private void Start()
    {
        GameSettingsStorage.ApplyToWorld(this);

        if (blockData != null)
        {
            blockData.InitializeDictionary();
            RebuildBlockAtlasCompatibility();
        }
        RefreshTerrainGenerationRuntimeState();
        lastWorldMaterialProfileHash = ComputeWorldMaterialProfileHash();

        // Pre-instantiate pool
        colliderPrewarmedChunkCount = 0;
        int initialChunkPoolTarget = GetResolvedChunkPoolTargetSize();
        for (int i = 0; i < initialChunkPoolTarget; i++)
        {
            Chunk chunk = CreateChunkPoolEntry();
            chunkPool.Enqueue(chunk);
        }

        lastEnableBlockColliders = enableBlockColliders;
        lastEnableRealisticShader = enableRealisticShader;
        lastEnableVoxelLighting = enableVoxelLighting;
        lastEnableHorizontalSkylight = enableHorizontalSkylight;
        lastEnableAmbientOcclusion = enableAmbientOcclusion;
        lastEnableWater = enableWater;
        lastEnableChunkDetailLod = enableChunkDetailLod;
        lastTerrainMode = terrainMode;
        lastFlatWorldHeight = GetResolvedFlatWorldHeight();
        lastFlatWorldBiome = GetResolvedFlatWorldBiome();
        lastChunkDetailLodDistance = chunkDetailLodDistance;
        lastTreeLeafQuality = treeLeafQuality;
        lastTreeLeafFoliageSettingsHash = ComputeTreeLeafFoliageSettingsHash();
        lastHorizontalSkylightStepLoss = horizontalSkylightStepLoss;
        lastSunlightSmoothingPadding = sunlightSmoothingPadding;
        lastChunkDetailPromotionQueueCenter = InvalidChunkCoord;
        lastPlayerChunkDetailMovementTime = Time.time;
    }

    [ContextMenu("Rebuild Block Atlas Compatibility")]
    public void RebuildBlockAtlasCompatibility()
    {
        if (blockData == null)
            return;

        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (generator == null)
            return;

        // Prefer the imported saved atlas in builds so its TextureImporter mipmap
        // settings are kept. Fall back to a runtime rebuild only when the saved
        // atlas/UV state is missing.
        if (Application.isPlaying &&
            generator.HasConfiguredTextureEntries() &&
            !generator.TryApplyPersistedAtlasWithoutRebuild())
        {
            generator.GenerateAtlas();
        }

        Vector2Int legacyAtlasTiles = new Vector2Int(
            Mathf.Max(1, atlasTilesX),
            Mathf.Max(1, atlasTilesY));

        if (!VoxelAtlasCompatibility.Apply(
                generator,
                blockData,
                legacyAtlasTiles,
                blockData.atlasCoordinatesStartTopLeft))
        {
            return;
        }

        ApplyGeneratedAtlasToWorldMaterials(generator.GeneratedAtlas);

        if (blockItemIconAtlasTexture == null && generator.GeneratedAtlas != null)
            blockItemIconAtlasTexture = generator.GeneratedAtlas;

        InvalidateNativeGenerationCaches();
    }

    private TextureAtlasGenerator ResolveBlockAtlasGenerator()
    {
        if (blockAtlasGenerator != null)
            return blockAtlasGenerator;

        TextureAtlasGenerator[] generators = FindObjectsOfType<TextureAtlasGenerator>(true);
        TextureAtlasGenerator bestMatch = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < generators.Length; i++)
        {
            TextureAtlasGenerator candidate = generators[i];
            if (candidate == null)
                continue;

            int score = ScoreAtlasGenerator(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }

        blockAtlasGenerator = bestMatch;
        return blockAtlasGenerator;
    }

    private int ScoreAtlasGenerator(TextureAtlasGenerator generator)
    {
        if (generator == null)
            return int.MinValue;

        int score = 0;
        if (generator == blockAtlasGenerator)
            score += 1000;

        if (generator.targetMaterials != null)
        {
            for (int i = 0; i < generator.targetMaterials.Count; i++)
            {
                Material targetMaterial = generator.targetMaterials[i];
                if (IsWorldMaterial(targetMaterial))
                    score += 100;
            }
        }

        if (generator.targetRenderer != null)
        {
            Material[] rendererMaterials = generator.targetRenderer.sharedMaterials;
            for (int i = 0; i < rendererMaterials.Length; i++)
            {
                Material rendererMaterial = rendererMaterials[i];
                if (IsWorldMaterial(rendererMaterial))
                    score += 50;
            }
        }

        if (generator.GeneratedAtlas != null)
            score += 10;
        if (generator.generateOnStart)
            score += 5;

        return score;
    }

    private void ApplyGeneratedAtlasToWorldMaterials(Texture atlasTexture)
    {
        if (atlasTexture == null)
            return;

        ApplyGeneratedAtlasToMaterials(atlasTexture, pcMaterials);
        ApplyGeneratedAtlasToMaterials(atlasTexture, mobileMaterials);
    }

    private void ApplyGeneratedAtlasToMaterials(Texture atlasTexture, Material[] materials)
    {
        if (materials == null)
            return;

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
                continue;

            float atlasPaddingUv = ResolveAtlasShaderPaddingUv(atlasTexture, material);

            if (material.HasProperty("_Atlas"))
                material.SetTexture("_Atlas", atlasTexture);
            else if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", atlasTexture);
            else if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", atlasTexture);
            else
                material.mainTexture = atlasTexture;

            if (material.HasProperty("_AtlasOriginTopLeft"))
                material.SetFloat("_AtlasOriginTopLeft", 0f);
            if (material.HasProperty("_PaddingUV"))
                material.SetFloat("_PaddingUV", atlasPaddingUv);
        }
    }

    private float ResolveAtlasShaderPaddingUv(Texture atlasTexture, Material material)
    {
        if (atlasTexture == null)
            return 0f;

        TextureAtlasGenerator generator = ResolveBlockAtlasGenerator();
        if (generator != null)
            return generator.ComputeShaderPaddingUv(atlasTexture, material);

        int referenceSize = Mathf.Max(atlasTexture.width, atlasTexture.height);
        if (referenceSize <= 0)
            return 0f;

        // Fallback conservador quando o atlas vem de fora do generator.
        float fallbackPaddingPixels = 1f;
        if (material != null &&
            material.HasProperty("_AlphaClip") &&
            material.GetFloat("_AlphaClip") > 0.5f)
        {
            fallbackPaddingPixels = 2f;
        }

        return fallbackPaddingPixels / referenceSize;
    }

    private void OnEnable()
    {
        TerrainLayerProfileSO.ProfileChanged += HandleTerrainLayerProfileChanged;
        BiomeDefinitionSO.DefinitionChanged += HandleBiomeDefinitionChanged;
    }

    private void OnDisable()
    {
        TerrainLayerProfileSO.ProfileChanged -= HandleTerrainLayerProfileChanged;
        BiomeDefinitionSO.DefinitionChanged -= HandleBiomeDefinitionChanged;
        HideTreecutterBreakCrackVisual();
    }

    private void OnDestroy()
    {
        isShuttingDown = true;

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            PendingData pd = pendingDataJobs[i];
            pd.handle.Complete();
            DisposeDataJobResources(ref pd);
        }
        pendingDataJobs.Clear();

        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            PendingData pd = pendingMeshBuildRequests[i];
            pd.handle.Complete();
            DisposeDataJobResources(ref pd);
        }
        pendingMeshBuildRequests.Clear();

        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            PendingMesh pm = pendingMeshes[i];
            pm.handle.Complete();
            DisposePendingMesh(pm);
        }
        pendingMeshes.Clear();

        CompletePendingChunkDataBufferReturns();

        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk != null)
                chunk.CompleteTrackedJob();
        }

        MeshGenerator.ClearSpaghettiCarveMaskNeighborCache();
        MeshGenerator.ClearDataJobTempBufferPool();
        DisposeNativeGenerationCaches();
        DestroyAutoMinerAreaVisuals();
        DestroyTreecutterBreakCrackVisuals();

        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        ProcessRetiredChunksAwaitingRecycle(Mathf.Max(1, maxRetiredChunkRecyclesPerFrame));

        float updateFrameStartTime = Time.realtimeSinceStartup;
        float updateBudgetSeconds = updateWorkBudgetMS > 0f ? updateWorkBudgetMS / 1000f : 0f;
        chunkPoolCreatesThisFrame = 0;
        UpdateChunkDetailPromotionMovementState();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            MaintainChunkPool();

        HandleBlockColliderToggle();
        HandleWorldMaterialProfileToggle();
        HandleRealisticShaderToggle();
        HandleVisualFeatureToggle();
        ApplyResolvedVisualSubchunkRendererLayout();
        meshesAppliedThisFrame = 0;

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            UpdateSectionOcclusionVisibility();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedWaterUpdates();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedSupportDependentBlockChecks();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedSaplingGrowth();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessElectricitySimulation();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessTreecutterMachines();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessAutoMinerMachines();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessTransportTubeFilters();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            UpdateAutoMinerLaserVisuals();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedTreeCapitatorBreaks();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedInteractiveBlockLightRefreshes();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedChunkRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedLightingOnlyChunkRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedHighBuildMeshRebuilds();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedLeafDecay();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            UpdateChunks();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            RefreshColliderDistanceStateIfNeeded();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessChunkQueue(GetRemainingUpdateBudgetSeconds(updateFrameStartTime, updateBudgetSeconds));

        ProcessQueuedChunkDetailPromotions(updateFrameStartTime, updateBudgetSeconds);

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessQueuedChunkJobTrackingRefreshes();

        EnsurePlayerChunkColliderSafety();

        if (HasUpdateBudgetRemaining(updateFrameStartTime, updateBudgetSeconds))
            ProcessPendingColliderBuilds();

        ProcessPendingChunkDataBufferReturns();

    }

    private static bool HasUpdateBudgetRemaining(float frameStartTime, float budgetSeconds)
    {
        if (budgetSeconds <= 0f)
            return true;

        return (Time.realtimeSinceStartup - frameStartTime) <= budgetSeconds;
    }

    private static float GetRemainingUpdateBudgetSeconds(float frameStartTime, float budgetSeconds)
    {
        if (budgetSeconds <= 0f)
            return float.PositiveInfinity;

        return Mathf.Max(0f, budgetSeconds - (Time.realtimeSinceStartup - frameStartTime));
    }

    #endregion

}
