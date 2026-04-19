using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public enum BiomeTintQualityMode : byte
{
    Ultra = 0,
    High = 1,
    Fast = 2
}

public partial class World : MonoBehaviour
{
    private const int MaxChunkBiomeTintCacheEntries = 4096;
    private const float DefaultBiomeTerrainBlendRange = 0.08f;
    private const float DefaultAltitudeTemperatureFalloff = 0.0045f;
    private const float DefaultColdStoneStartHeightOffset = 24f;
    private const float DefaultColdStoneBlendRange = 18f;
    private const float DefaultColdSnowStartHeightOffset = 34f;
    private const float DefaultColdSnowBlendRange = 22f;
    private const float DefaultColdSnowTemperatureThreshold = 0.30f;
    private const float DefaultColdSurfaceNoiseScale = 0.045f;
    private const float BiomeTemperatureSeedOffsetX = 0.173f;
    private const float BiomeTemperatureSeedOffsetY = -0.241f;
    private const float BiomeHumiditySeedOffsetX = -0.137f;
    private const float BiomeHumiditySeedOffsetY = 0.197f;

    [Header("Biome Definitions")]
    [Tooltip("Dados do bioma via ScriptableObject. Se vazio, o World tenta carregar automaticamente de Resources/Biomes.")]
    public BiomeDefinitionSO[] biomeDefinitions;

    [Header("Biome Settings")]
    [Min(0.0001f)]
    public float biomeTemperatureScale = 0.0024f;
    [Min(0.0001f)]
    public float biomeHumidityScale = 0.0021f;
    public Vector2 biomeTemperatureOffset = new Vector2(291.7f, -163.4f);
    public Vector2 biomeHumidityOffset = new Vector2(-147.2f, 238.9f);
    [Range(0f, 1f)]
    public float desertMinTemperature = 0.68f;
    [Range(0f, 1f)]
    public float desertMaxHumidity = 0.42f;
    [Range(0f, 1f)]
    public float savannaMinTemperature = 0.55f;
    [Range(0f, 1f)]
    public float taigaMaxTemperature = 0.34f;
    [Range(0f, 1f)]
    public float taigaMinHumidity = 0.52f;

    [Header("Biome Relief")]
    [Range(0.01f, 0.25f)]
    public float biomeTerrainBlendRange = DefaultBiomeTerrainBlendRange;
    public BiomeTerrainSettings desertTerrain = BiomeTerrainSettings.DesertDefault;
    public BiomeTerrainSettings savannaTerrain = BiomeTerrainSettings.SavannaDefault;
    public BiomeTerrainSettings meadowTerrain = BiomeTerrainSettings.MeadowDefault;
    public BiomeTerrainSettings taigaTerrain = BiomeTerrainSettings.TaigaDefault;

    [Header("Cold Mountain Surface")]
    [Min(0.0001f)]
    public float altitudeTemperatureFalloff = DefaultAltitudeTemperatureFalloff;
    public float coldStoneStartHeightOffset = DefaultColdStoneStartHeightOffset;
    [Min(1f)]
    public float coldStoneBlendRange = DefaultColdStoneBlendRange;
    public float coldSnowStartHeightOffset = DefaultColdSnowStartHeightOffset;
    [Min(1f)]
    public float coldSnowBlendRange = DefaultColdSnowBlendRange;
    [Range(0f, 1f)]
    public float coldSnowTemperatureThreshold = DefaultColdSnowTemperatureThreshold;
    [Min(0.001f)]
    public float coldSurfaceNoiseScale = DefaultColdSurfaceNoiseScale;

    [Header("Coast / Underwater Surface")]
    [Tooltip("Thresholds usados pelas surface rules de costa e fundo submerso (ajuste em runtime).")]
    public CoastSurfaceThresholdSettings coastSurface = CoastSurfaceThresholdSettings.Default;

    [Header("Biome Tint Blending")]
    [Tooltip("Liga/desliga o sistema de transicao suave de tint entre biomas.")]
    public bool enableBiomeTintBlending = true;
    [Tooltip("Ultra = blend mais fiel (mais custo), High = equilibrado, Fast = mais leve com boa aparencia.")]
    public BiomeTintQualityMode biomeTintQuality = BiomeTintQualityMode.High;
    [Tooltip("0 = usa cor uniforme por chunk, 1 = usa gradiente por cantos do chunk.")]
    [Range(0f, 1f)]
    public float biomeTintGradientStrength = 1f;

    private static readonly int GrassTintPropertyId = Shader.PropertyToID("_GrassTint");
    private static readonly int FolliageTintPropertyId = Shader.PropertyToID("_FolliageTint");
    private static readonly int BiomeTintBlendEnabledPropertyId = Shader.PropertyToID("_BiomeTintBlendEnabled");
    private static readonly int BiomeTintOriginPropertyId = Shader.PropertyToID("_BiomeTintOriginXZ");
    private static readonly int BiomeTintInvSizePropertyId = Shader.PropertyToID("_BiomeTintInvSizeXZ");
    private static readonly int GrassTintCorner00PropertyId = Shader.PropertyToID("_GrassTintCorner00");
    private static readonly int GrassTintCorner10PropertyId = Shader.PropertyToID("_GrassTintCorner10");
    private static readonly int GrassTintCorner01PropertyId = Shader.PropertyToID("_GrassTintCorner01");
    private static readonly int GrassTintCorner11PropertyId = Shader.PropertyToID("_GrassTintCorner11");
    private static readonly int FolliageTintCorner00PropertyId = Shader.PropertyToID("_FolliageTintCorner00");
    private static readonly int FolliageTintCorner10PropertyId = Shader.PropertyToID("_FolliageTintCorner10");
    private static readonly int FolliageTintCorner01PropertyId = Shader.PropertyToID("_FolliageTintCorner01");
    private static readonly int FolliageTintCorner11PropertyId = Shader.PropertyToID("_FolliageTintCorner11");
    private MaterialPropertyBlock biomeTintPropertyBlock;
    private readonly Dictionary<Vector2Int, ChunkBiomeTints> chunkBiomeTintCache = new Dictionary<Vector2Int, ChunkBiomeTints>(256);
    private bool biomeTintCacheSettingsInitialized;
    private bool cachedEnableBiomeTintBlending;
    private BiomeTintQualityMode cachedBiomeTintQuality;
    private readonly Dictionary<BiomeType, BiomeDefinitionSO> biomeDefinitionsByType = new Dictionary<BiomeType, BiomeDefinitionSO>();
    private BiomeDefinitionSO[] cachedBiomeDefinitions = Array.Empty<BiomeDefinitionSO>();
    private bool biomeDefinitionsDirty = true;
    private bool biomeNoiseSettingsDirty = true;
    private BiomeNoiseSettings cachedBiomeNoiseSettings;

    private void MarkBiomeCachesDirty()
    {
        biomeDefinitionsDirty = true;
        biomeNoiseSettingsDirty = true;
        chunkBiomeTintCache.Clear();
        biomeTintCacheSettingsInitialized = false;
        treeSpawnRulesDirty = true;
        vegetationBillboardRulesDirty = true;
        InvalidateNativeGenerationCaches();
    }

    private void InvalidateChunkBiomeTintCache(Vector2Int coord)
    {
        chunkBiomeTintCache.Remove(coord);
    }

    private void EnsureBiomeTintCacheMatchesSettings()
    {
        if (!biomeTintCacheSettingsInitialized ||
            cachedEnableBiomeTintBlending != enableBiomeTintBlending ||
            cachedBiomeTintQuality != biomeTintQuality)
        {
            chunkBiomeTintCache.Clear();
            cachedEnableBiomeTintBlending = enableBiomeTintBlending;
            cachedBiomeTintQuality = biomeTintQuality;
            biomeTintCacheSettingsInitialized = true;
        }
    }

    private void EnsureBiomeDefinitionCache()
    {
        if (!biomeDefinitionsDirty)
            return;

        RebuildBiomeDefinitionCache();
    }

    private void RebuildBiomeDefinitionCache()
    {
        biomeDefinitionsDirty = false;
        biomeDefinitionsByType.Clear();

        BiomeDefinitionSO[] source = biomeDefinitions;
        if (source == null || source.Length == 0)
            source = Resources.LoadAll<BiomeDefinitionSO>("Biomes");

        if (source == null || source.Length == 0)
        {
            cachedBiomeDefinitions = Array.Empty<BiomeDefinitionSO>();
            return;
        }

        // Mantemos uma tabela unica por tipo para evitar buscas e conflitos durante a geracao.
        List<BiomeDefinitionSO> uniqueDefinitions = new List<BiomeDefinitionSO>(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            BiomeDefinitionSO definition = source[i];
            if (definition == null)
                continue;

            if (biomeDefinitionsByType.ContainsKey(definition.biomeType))
            {
                Debug.LogWarning($"BiomeDefinition duplicado para {definition.biomeType}. Mantendo o primeiro.", this);
                continue;
            }

            biomeDefinitionsByType.Add(definition.biomeType, definition);
            uniqueDefinitions.Add(definition);
        }

        cachedBiomeDefinitions = uniqueDefinitions.Count > 0
            ? uniqueDefinitions.ToArray()
            : Array.Empty<BiomeDefinitionSO>();
    }

    private bool TryGetBiomeDefinition(BiomeType biome, out BiomeDefinitionSO definition)
    {
        EnsureBiomeDefinitionCache();
        return biomeDefinitionsByType.TryGetValue(biome, out definition);
    }

    private BiomeDefinitionSO[] GetConfiguredBiomeDefinitions()
    {
        EnsureBiomeDefinitionCache();
        return cachedBiomeDefinitions;
    }

    private BlockType GetSurfaceBlockForBiome(BiomeType biome)
    {
        if (TryGetBiomeDefinition(biome, out BiomeDefinitionSO definition))
            return definition.surfaceBlock;

        return BiomeUtility.GetDefaultSurfaceBlock(biome);
    }

    private BlockType GetSubsurfaceBlockForBiome(BiomeType biome)
    {
        if (TryGetBiomeDefinition(biome, out BiomeDefinitionSO definition))
            return definition.subsurfaceBlock;

        return BiomeUtility.GetDefaultSubsurfaceBlock(biome);
    }

    private void InitializeBiomeNoiseOffsets()
    {
        biomeTemperatureOffset = SanitizeBiomeOffset(biomeTemperatureOffset);
        biomeHumidityOffset = SanitizeBiomeOffset(biomeHumidityOffset);
    }

    private BiomeNoiseSettings GetBiomeNoiseSettings()
    {
        if (!biomeNoiseSettingsDirty)
            return cachedBiomeNoiseSettings;

        // Consolida o estado do inspector + ScriptableObjects em um snapshot seguro para jobs.
        Vector2 effectiveTemperatureOffset = GetSeededBiomeOffset(
            biomeTemperatureOffset,
            BiomeTemperatureSeedOffsetX,
            BiomeTemperatureSeedOffsetY);
        Vector2 effectiveHumidityOffset = GetSeededBiomeOffset(
            biomeHumidityOffset,
            BiomeHumiditySeedOffsetX,
            BiomeHumiditySeedOffsetY);
        BiomeTerrainSettings desertTerrainSettings = SanitizeBiomeTerrainSettings(desertTerrain, BiomeTerrainSettings.DesertDefault);
        BiomeTerrainSettings savannaTerrainSettings = SanitizeBiomeTerrainSettings(savannaTerrain, BiomeTerrainSettings.SavannaDefault);
        BiomeTerrainSettings meadowTerrainSettings = SanitizeBiomeTerrainSettings(meadowTerrain, BiomeTerrainSettings.MeadowDefault);
        BiomeTerrainSettings taigaTerrainSettings = SanitizeBiomeTerrainSettings(taigaTerrain, BiomeTerrainSettings.TaigaDefault);
        CoastSurfaceThresholdSettings coastSurfaceSettings = coastSurface.Sanitized();
        BiomeDensityMultipliers desertDensitySettings = ResolveBiomeDensityMultipliers(BiomeType.Desert);
        BiomeDensityMultipliers savannaDensitySettings = ResolveBiomeDensityMultipliers(BiomeType.Savanna);
        BiomeDensityMultipliers meadowDensitySettings = ResolveBiomeDensityMultipliers(BiomeType.Meadow);
        BiomeDensityMultipliers taigaDensitySettings = ResolveBiomeDensityMultipliers(BiomeType.Taiga);

        bool coldSettingsUninitialized = AreColdMountainSettingsUninitialized();
        cachedBiomeNoiseSettings = new BiomeNoiseSettings
        {
            temperatureScale = math.max(0.0001f, biomeTemperatureScale),
            humidityScale = math.max(0.0001f, biomeHumidityScale),
            temperatureOffset = new float2(effectiveTemperatureOffset.x, effectiveTemperatureOffset.y),
            humidityOffset = new float2(effectiveHumidityOffset.x, effectiveHumidityOffset.y),
            terrainBlendRange = math.max(0.01f, coldSettingsUninitialized ? DefaultBiomeTerrainBlendRange : biomeTerrainBlendRange),
            desertMinTemperature = math.saturate(desertMinTemperature),
            desertMaxHumidity = math.saturate(desertMaxHumidity),
            savannaMinTemperature = math.saturate(savannaMinTemperature),
            taigaMaxTemperature = math.saturate(taigaMaxTemperature),
            taigaMinHumidity = math.saturate(taigaMinHumidity),
            desertTerrain = desertTerrainSettings,
            savannaTerrain = savannaTerrainSettings,
            meadowTerrain = meadowTerrainSettings,
            taigaTerrain = taigaTerrainSettings,
            desertDensity = desertDensitySettings,
            savannaDensity = savannaDensitySettings,
            meadowDensity = meadowDensitySettings,
            taigaDensity = taigaDensitySettings,
            altitudeTemperatureFalloff = coldSettingsUninitialized ? DefaultAltitudeTemperatureFalloff : math.max(0.0001f, altitudeTemperatureFalloff),
            coldStoneStartHeightOffset = coldSettingsUninitialized ? DefaultColdStoneStartHeightOffset : coldStoneStartHeightOffset,
            coldStoneBlendRange = coldSettingsUninitialized ? DefaultColdStoneBlendRange : math.max(1f, coldStoneBlendRange),
            coldSnowStartHeightOffset = coldSettingsUninitialized ? DefaultColdSnowStartHeightOffset : coldSnowStartHeightOffset,
            coldSnowBlendRange = coldSettingsUninitialized ? DefaultColdSnowBlendRange : math.max(1f, coldSnowBlendRange),
            coldSnowTemperatureThreshold = coldSettingsUninitialized ? DefaultColdSnowTemperatureThreshold : math.saturate(coldSnowTemperatureThreshold),
            coldSurfaceNoiseScale = coldSettingsUninitialized ? DefaultColdSurfaceNoiseScale : math.max(0.001f, coldSurfaceNoiseScale),
            desertSurfaceBlock = GetSurfaceBlockForBiome(BiomeType.Desert),
            desertSubsurfaceBlock = GetSubsurfaceBlockForBiome(BiomeType.Desert),
            savannaSurfaceBlock = GetSurfaceBlockForBiome(BiomeType.Savanna),
            savannaSubsurfaceBlock = GetSubsurfaceBlockForBiome(BiomeType.Savanna),
            meadowSurfaceBlock = GetSurfaceBlockForBiome(BiomeType.Meadow),
            meadowSubsurfaceBlock = GetSubsurfaceBlockForBiome(BiomeType.Meadow),
            taigaSurfaceBlock = GetSurfaceBlockForBiome(BiomeType.Taiga),
            taigaSubsurfaceBlock = GetSubsurfaceBlockForBiome(BiomeType.Taiga),
            coastSurface = coastSurfaceSettings,
            terrainShaper = terrainSplineShaper.Sanitized()
        };

        biomeNoiseSettingsDirty = false;
        return cachedBiomeNoiseSettings;
    }

    private static BiomeTerrainSettings SanitizeBiomeTerrainSettings(BiomeTerrainSettings raw, BiomeTerrainSettings fallback)
    {
        if (IsBiomeTerrainSettingsUninitialized(raw))
            raw = fallback;

        raw.reliefMultiplier = Mathf.Max(0.05f, raw.reliefMultiplier);
        raw.hillsMultiplier = Mathf.Max(0f, raw.hillsMultiplier);
        raw.mountainMultiplier = Mathf.Max(0f, raw.mountainMultiplier);
        raw.erosionBias = Mathf.Clamp(raw.erosionBias, -0.45f, 0.45f);
        raw.erosionPower = Mathf.Max(0.1f, raw.erosionPower);
        raw.flattenStrength = Mathf.Clamp01(raw.flattenStrength);
        raw.heightOffset = Mathf.Clamp(raw.heightOffset, -12f, 12f);
        raw.surfaceDepth = raw.surfaceDepth > 0f ? raw.surfaceDepth : fallback.surfaceDepth;
        raw.steepSurfaceDepth = raw.steepSurfaceDepth > 0f ? raw.steepSurfaceDepth : fallback.steepSurfaceDepth;
        raw.surfaceDepth = Mathf.Clamp(raw.surfaceDepth, 1f, 12f);
        raw.steepSurfaceDepth = Mathf.Clamp(raw.steepSurfaceDepth, 1f, raw.surfaceDepth);
        return raw;
    }

    private static BiomeDensityMultipliers SanitizeBiomeDensityMultipliers(BiomeDensityMultipliers raw)
    {
        return raw.Sanitized();
    }

    private BiomeDensityMultipliers ResolveBiomeDensityMultipliers(BiomeType biome)
    {
        BiomeDensityMultipliers resolved = BiomeDensityMultipliers.Identity;
        if (TryGetBiomeDefinition(biome, out BiomeDefinitionSO definition) && definition.overrideDensityMultipliers)
            resolved = definition.densityMultipliers;

        return SanitizeBiomeDensityMultipliers(resolved);
    }

    private static bool IsBiomeTerrainSettingsUninitialized(BiomeTerrainSettings settings)
    {
        return settings.reliefMultiplier == 0f &&
               settings.hillsMultiplier == 0f &&
               settings.mountainMultiplier == 0f &&
               settings.erosionBias == 0f &&
               settings.erosionPower == 0f &&
               settings.flattenStrength == 0f &&
               settings.heightOffset == 0f &&
               settings.surfaceDepth == 0f &&
               settings.steepSurfaceDepth == 0f;
    }

    private bool AreColdMountainSettingsUninitialized()
    {
        return biomeTerrainBlendRange == 0f &&
               altitudeTemperatureFalloff == 0f &&
               coldStoneStartHeightOffset == 0f &&
               coldStoneBlendRange == 0f &&
               coldSnowStartHeightOffset == 0f &&
               coldSnowBlendRange == 0f &&
               coldSnowTemperatureThreshold == 0f &&
               coldSurfaceNoiseScale == 0f;
    }

    private Vector2 GetSeededBiomeOffset(Vector2 configuredOffset, float xMultiplier, float yMultiplier)
    {
        return configuredOffset + new Vector2(seed * xMultiplier, seed * yMultiplier);
    }

    private static Vector2 SanitizeBiomeOffset(Vector2 offset)
    {
        if (float.IsNaN(offset.x) || float.IsInfinity(offset.x))
            offset.x = 0f;

        if (float.IsNaN(offset.y) || float.IsInfinity(offset.y))
            offset.y = 0f;

        return offset;
    }

    private BiomeType GetBiomeAt(int worldX, int worldZ)
    {
        return BiomeUtility.GetBiomeType(worldX, worldZ, GetBiomeNoiseSettings());
    }

    private BlockType GetBiomeSurfaceBlock(int worldX, int worldZ)
    {
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        return GetSurfaceBlockForBiome(biome);
    }

    private BlockType GetBiomeSubsurfaceBlock(int worldX, int worldZ)
    {
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        return GetSubsurfaceBlockForBiome(biome);
    }

    private Color GetGrassTintForBiome(BiomeType biome)
    {
        if (TryGetBiomeDefinition(biome, out BiomeDefinitionSO definition))
            return definition.grassTint;

        return Color.white;
    }

    private Color GetFoliageTintForBiome(BiomeType biome)
    {
        if (TryGetBiomeDefinition(biome, out BiomeDefinitionSO definition))
        {
            Color configuredTint = definition.foliageTint;
            if (configuredTint.maxColorComponent > 0f || configuredTint.a > 0f)
                return configuredTint;

            return definition.grassTint;
        }

        return Color.white;
    }

    private readonly struct ChunkBiomeTints
    {
        public readonly Color grassTint;
        public readonly Color foliageTint;
        public readonly Color grassCorner00;
        public readonly Color grassCorner10;
        public readonly Color grassCorner01;
        public readonly Color grassCorner11;
        public readonly Color foliageCorner00;
        public readonly Color foliageCorner10;
        public readonly Color foliageCorner01;
        public readonly Color foliageCorner11;
        public readonly Vector2 originXZ;
        public readonly Vector2 invSizeXZ;

        public ChunkBiomeTints(
            Color grassTint,
            Color foliageTint,
            Color grassCorner00,
            Color grassCorner10,
            Color grassCorner01,
            Color grassCorner11,
            Color foliageCorner00,
            Color foliageCorner10,
            Color foliageCorner01,
            Color foliageCorner11,
            Vector2 originXZ,
            Vector2 invSizeXZ)
        {
            this.grassTint = grassTint;
            this.foliageTint = foliageTint;
            this.grassCorner00 = grassCorner00;
            this.grassCorner10 = grassCorner10;
            this.grassCorner01 = grassCorner01;
            this.grassCorner11 = grassCorner11;
            this.foliageCorner00 = foliageCorner00;
            this.foliageCorner10 = foliageCorner10;
            this.foliageCorner01 = foliageCorner01;
            this.foliageCorner11 = foliageCorner11;
            this.originXZ = originXZ;
            this.invSizeXZ = invSizeXZ;
        }
    }

    private readonly struct BiomeTintSample
    {
        public readonly Color grassTint;
        public readonly Color foliageTint;

        public BiomeTintSample(Color grassTint, Color foliageTint)
        {
            this.grassTint = grassTint;
            this.foliageTint = foliageTint;
        }
    }

    private readonly struct BiomeTintPalette
    {
        public readonly Color desertGrass;
        public readonly Color savannaGrass;
        public readonly Color meadowGrass;
        public readonly Color taigaGrass;
        public readonly Color desertFoliage;
        public readonly Color savannaFoliage;
        public readonly Color meadowFoliage;
        public readonly Color taigaFoliage;

        public BiomeTintPalette(
            Color desertGrass,
            Color savannaGrass,
            Color meadowGrass,
            Color taigaGrass,
            Color desertFoliage,
            Color savannaFoliage,
            Color meadowFoliage,
            Color taigaFoliage)
        {
            this.desertGrass = desertGrass;
            this.savannaGrass = savannaGrass;
            this.meadowGrass = meadowGrass;
            this.taigaGrass = taigaGrass;
            this.desertFoliage = desertFoliage;
            this.savannaFoliage = savannaFoliage;
            this.meadowFoliage = meadowFoliage;
            this.taigaFoliage = taigaFoliage;
        }
    }

    private void ApplyChunkBiomeTint(Chunk chunk, Vector2Int coord)
    {
        if (chunk == null || chunk.subRenderers == null)
            return;

        ChunkBiomeTints tints = EvaluateChunkBiomeTints(coord);
        for (int i = 0; i < chunk.subRenderers.Length; i++)
        {
            Renderer renderer = chunk.subRenderers[i];
            ApplyBiomeTintToRenderer(renderer, tints);
            ApplyRealisticShaderRendererSettings(renderer);
        }
    }

    private void ApplyBiomeTintToRenderer(Renderer renderer, Vector2Int coord)
    {
        if (renderer == null)
            return;

        ApplyBiomeTintToRenderer(renderer, EvaluateChunkBiomeTints(coord));
    }

    public void ApplyBiomeTintToRendererAt(Renderer renderer, Vector3Int worldPos)
    {
        if (renderer == null)
            return;

        ApplyBiomeTintToRenderer(renderer, GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z));
    }

    private void ApplyBiomeTintToRenderer(Renderer renderer, ChunkBiomeTints tints)
    {
        if (renderer == null)
            return;

        if (biomeTintPropertyBlock == null)
            biomeTintPropertyBlock = new MaterialPropertyBlock();

        renderer.GetPropertyBlock(biomeTintPropertyBlock);
        biomeTintPropertyBlock.SetColor(GrassTintPropertyId, tints.grassTint);
        biomeTintPropertyBlock.SetColor(FolliageTintPropertyId, tints.foliageTint);
        float blendStrength = enableBiomeTintBlending
            ? Mathf.Clamp01(biomeTintGradientStrength)
            : 0f;
        biomeTintPropertyBlock.SetFloat(BiomeTintBlendEnabledPropertyId, blendStrength);
        biomeTintPropertyBlock.SetVector(BiomeTintOriginPropertyId, new Vector4(tints.originXZ.x, tints.originXZ.y, 0f, 0f));
        biomeTintPropertyBlock.SetVector(BiomeTintInvSizePropertyId, new Vector4(tints.invSizeXZ.x, tints.invSizeXZ.y, 0f, 0f));
        biomeTintPropertyBlock.SetColor(GrassTintCorner00PropertyId, tints.grassCorner00);
        biomeTintPropertyBlock.SetColor(GrassTintCorner10PropertyId, tints.grassCorner10);
        biomeTintPropertyBlock.SetColor(GrassTintCorner01PropertyId, tints.grassCorner01);
        biomeTintPropertyBlock.SetColor(GrassTintCorner11PropertyId, tints.grassCorner11);
        biomeTintPropertyBlock.SetColor(FolliageTintCorner00PropertyId, tints.foliageCorner00);
        biomeTintPropertyBlock.SetColor(FolliageTintCorner10PropertyId, tints.foliageCorner10);
        biomeTintPropertyBlock.SetColor(FolliageTintCorner01PropertyId, tints.foliageCorner01);
        biomeTintPropertyBlock.SetColor(FolliageTintCorner11PropertyId, tints.foliageCorner11);
        biomeTintPropertyBlock.SetFloat(EnableRealisticShaderPropertyId, enableRealisticShader ? 1f : 0f);
        renderer.SetPropertyBlock(biomeTintPropertyBlock);
    }

    private void ApplyRealisticShaderRendererSettings(Renderer renderer)
    {
        if (renderer == null)
            return;

        bool useRealtimeRendererShadows = enableRealisticShader && !IsMobileMaterialProfileSelected;
        renderer.shadowCastingMode = useRealtimeRendererShadows
            ? UnityEngine.Rendering.ShadowCastingMode.On
            : UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = useRealtimeRendererShadows;
    }

    private ChunkBiomeTints EvaluateChunkBiomeTints(Vector2Int coord)
    {
        EnsureBiomeTintCacheMatchesSettings();

        if (chunkBiomeTintCache.TryGetValue(coord, out ChunkBiomeTints cached))
            return cached;

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ;
        int centerX = chunkMinX + Chunk.SizeX / 2;
        int centerZ = chunkMinZ + Chunk.SizeZ / 2;
        BiomeNoiseSettings settings = GetBiomeNoiseSettings();
        BiomeTintPalette palette = BuildBiomeTintPalette();
        ChunkBiomeTints tints;

        if (!enableBiomeTintBlending)
        {
            BiomeTintSample center = EvaluateDominantBiomeTintSample(centerX, centerZ, settings, palette);
            tints = BuildChunkBiomeTints(center, center, center, center, center, chunkMinX, chunkMinZ);
        }
        else
        {
            switch (biomeTintQuality)
            {
                case BiomeTintQualityMode.Ultra:
                    tints = EvaluateChunkBiomeTintsUltra(chunkMinX, chunkMinZ, chunkMaxX, chunkMaxZ, centerX, centerZ, settings, palette);
                    break;

                case BiomeTintQualityMode.Fast:
                    tints = EvaluateChunkBiomeTintsFast(chunkMinX, chunkMinZ, chunkMaxX, chunkMaxZ, settings, palette);
                    break;

                case BiomeTintQualityMode.High:
                default:
                    tints = EvaluateChunkBiomeTintsHigh(chunkMinX, chunkMinZ, chunkMaxX, chunkMaxZ, settings, palette);
                    break;
            }
        }

        if (chunkBiomeTintCache.Count >= MaxChunkBiomeTintCacheEntries)
            chunkBiomeTintCache.Clear();

        chunkBiomeTintCache[coord] = tints;
        return tints;
    }

    private BiomeTintPalette BuildBiomeTintPalette()
    {
        return new BiomeTintPalette(
            GetGrassTintForBiome(BiomeType.Desert),
            GetGrassTintForBiome(BiomeType.Savanna),
            GetGrassTintForBiome(BiomeType.Meadow),
            GetGrassTintForBiome(BiomeType.Taiga),
            GetFoliageTintForBiome(BiomeType.Desert),
            GetFoliageTintForBiome(BiomeType.Savanna),
            GetFoliageTintForBiome(BiomeType.Meadow),
            GetFoliageTintForBiome(BiomeType.Taiga));
    }

    private static ChunkBiomeTints BuildChunkBiomeTints(
        BiomeTintSample center,
        BiomeTintSample corner00,
        BiomeTintSample corner10,
        BiomeTintSample corner01,
        BiomeTintSample corner11,
        int chunkMinX,
        int chunkMinZ)
    {
        float safeSizeX = Mathf.Max(1f, Chunk.SizeX);
        float safeSizeZ = Mathf.Max(1f, Chunk.SizeZ);
        return new ChunkBiomeTints(
            center.grassTint,
            center.foliageTint,
            corner00.grassTint,
            corner10.grassTint,
            corner01.grassTint,
            corner11.grassTint,
            corner00.foliageTint,
            corner10.foliageTint,
            corner01.foliageTint,
            corner11.foliageTint,
            new Vector2(chunkMinX, chunkMinZ),
            new Vector2(1f / safeSizeX, 1f / safeSizeZ));
    }

    private static BiomeTintSample AverageBiomeTintSamples(
        BiomeTintSample a,
        BiomeTintSample b,
        BiomeTintSample c,
        BiomeTintSample d)
    {
        Color centerGrass = (a.grassTint + b.grassTint + c.grassTint + d.grassTint) * 0.25f;
        Color centerFoliage = (a.foliageTint + b.foliageTint + c.foliageTint + d.foliageTint) * 0.25f;
        centerGrass.a = 1f;
        centerFoliage.a = 1f;
        return new BiomeTintSample(centerGrass, centerFoliage);
    }

    private static ChunkBiomeTints EvaluateChunkBiomeTintsHigh(
        int chunkMinX,
        int chunkMinZ,
        int chunkMaxX,
        int chunkMaxZ,
        in BiomeNoiseSettings settings,
        in BiomeTintPalette palette)
    {
        BiomeTintSample corner00 = EvaluateBiomeTintSample(chunkMinX, chunkMinZ, settings, palette);
        BiomeTintSample corner10 = EvaluateBiomeTintSample(chunkMaxX, chunkMinZ, settings, palette);
        BiomeTintSample corner01 = EvaluateBiomeTintSample(chunkMinX, chunkMaxZ, settings, palette);
        BiomeTintSample corner11 = EvaluateBiomeTintSample(chunkMaxX, chunkMaxZ, settings, palette);
        BiomeTintSample center = AverageBiomeTintSamples(corner00, corner10, corner01, corner11);
        return BuildChunkBiomeTints(center, corner00, corner10, corner01, corner11, chunkMinX, chunkMinZ);
    }

    private static ChunkBiomeTints EvaluateChunkBiomeTintsUltra(
        int chunkMinX,
        int chunkMinZ,
        int chunkMaxX,
        int chunkMaxZ,
        int centerX,
        int centerZ,
        in BiomeNoiseSettings settings,
        in BiomeTintPalette palette)
    {
        BiomeTintSample corner00 = EvaluateBiomeTintSample(chunkMinX, chunkMinZ, settings, palette);
        BiomeTintSample corner10 = EvaluateBiomeTintSample(chunkMaxX, chunkMinZ, settings, palette);
        BiomeTintSample corner01 = EvaluateBiomeTintSample(chunkMinX, chunkMaxZ, settings, palette);
        BiomeTintSample corner11 = EvaluateBiomeTintSample(chunkMaxX, chunkMaxZ, settings, palette);
        BiomeTintSample center = EvaluateBiomeTintSample(centerX, centerZ, settings, palette);
        return BuildChunkBiomeTints(center, corner00, corner10, corner01, corner11, chunkMinX, chunkMinZ);
    }

    private static ChunkBiomeTints EvaluateChunkBiomeTintsFast(
        int chunkMinX,
        int chunkMinZ,
        int chunkMaxX,
        int chunkMaxZ,
        in BiomeNoiseSettings settings,
        in BiomeTintPalette palette)
    {
        BiomeTintSample corner00 = EvaluateDominantBiomeTintSample(chunkMinX, chunkMinZ, settings, palette);
        BiomeTintSample corner10 = EvaluateDominantBiomeTintSample(chunkMaxX, chunkMinZ, settings, palette);
        BiomeTintSample corner01 = EvaluateDominantBiomeTintSample(chunkMinX, chunkMaxZ, settings, palette);
        BiomeTintSample corner11 = EvaluateDominantBiomeTintSample(chunkMaxX, chunkMaxZ, settings, palette);
        BiomeTintSample center = AverageBiomeTintSamples(corner00, corner10, corner01, corner11);
        return BuildChunkBiomeTints(center, corner00, corner10, corner01, corner11, chunkMinX, chunkMinZ);
    }

    private static BiomeTintSample EvaluateBiomeTintSample(int worldX, int worldZ, in BiomeNoiseSettings settings, in BiomeTintPalette palette)
    {
        BiomeClimateSample climate = BiomeUtility.SampleClimate(worldX, worldZ, settings);
        BiomeTerrainBlendWeights blendWeights = BiomeUtility.GetTerrainBlendWeights(climate.temperature, climate.humidity, settings);

        Color grass = BlendBiomeTint(blendWeights, palette, foliageTint: false);
        Color foliage = BlendBiomeTint(blendWeights, palette, foliageTint: true);
        grass.a = 1f;
        foliage.a = 1f;
        return new BiomeTintSample(grass, foliage);
    }

    private static BiomeTintSample EvaluateDominantBiomeTintSample(int worldX, int worldZ, in BiomeNoiseSettings settings, in BiomeTintPalette palette)
    {
        BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, settings);
        Color grass = GetBiomeColorFromPalette(biome, palette, foliageTint: false);
        Color foliage = GetBiomeColorFromPalette(biome, palette, foliageTint: true);
        grass.a = 1f;
        foliage.a = 1f;
        return new BiomeTintSample(grass, foliage);
    }

    private static Color GetBiomeColorFromPalette(BiomeType biome, in BiomeTintPalette palette, bool foliageTint)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return foliageTint ? palette.desertFoliage : palette.desertGrass;

            case BiomeType.Savanna:
                return foliageTint ? palette.savannaFoliage : palette.savannaGrass;

            case BiomeType.Taiga:
                return foliageTint ? palette.taigaFoliage : palette.taigaGrass;

            case BiomeType.Meadow:
            default:
                return foliageTint ? palette.meadowFoliage : palette.meadowGrass;
        }
    }

    private static Color BlendBiomeTint(BiomeTerrainBlendWeights blendWeights, in BiomeTintPalette palette, bool foliageTint)
    {
        Color desert = foliageTint ? palette.desertFoliage : palette.desertGrass;
        Color savanna = foliageTint ? palette.savannaFoliage : palette.savannaGrass;
        Color meadow = foliageTint ? palette.meadowFoliage : palette.meadowGrass;
        Color taiga = foliageTint ? palette.taigaFoliage : palette.taigaGrass;

        return desert * blendWeights.desert +
               savanna * blendWeights.savanna +
               meadow * blendWeights.meadow +
               taiga * blendWeights.taiga;
    }
}

