using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
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

    private static readonly int GrassTintPropertyId = Shader.PropertyToID("_GrassTint");
    private MaterialPropertyBlock biomeTintPropertyBlock;
    private readonly Dictionary<BiomeType, BiomeDefinitionSO> biomeDefinitionsByType = new Dictionary<BiomeType, BiomeDefinitionSO>();
    private BiomeDefinitionSO[] cachedBiomeDefinitions = Array.Empty<BiomeDefinitionSO>();
    private bool biomeDefinitionsDirty = true;
    private bool biomeNoiseSettingsDirty = true;
    private BiomeNoiseSettings cachedBiomeNoiseSettings;

    private void MarkBiomeCachesDirty()
    {
        biomeDefinitionsDirty = true;
        biomeNoiseSettingsDirty = true;
        treeSpawnRulesDirty = true;
        InvalidateNativeGenerationCaches();
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

    private void ApplyChunkBiomeTint(Chunk chunk, Vector2Int coord)
    {
        if (chunk == null || chunk.subRenderers == null)
            return;

        Color grassTint = EvaluateChunkGrassTint(coord);
        for (int i = 0; i < chunk.subRenderers.Length; i++)
            ApplyBiomeTintToRenderer(chunk.subRenderers[i], grassTint);
    }

    private void ApplyBiomeTintToRenderer(Renderer renderer, Vector2Int coord)
    {
        if (renderer == null)
            return;

        ApplyBiomeTintToRenderer(renderer, EvaluateChunkGrassTint(coord));
    }

    private void ApplyBiomeTintToRenderer(Renderer renderer, Color grassTint)
    {
        if (renderer == null)
            return;

        if (biomeTintPropertyBlock == null)
            biomeTintPropertyBlock = new MaterialPropertyBlock();

        renderer.GetPropertyBlock(biomeTintPropertyBlock);
        biomeTintPropertyBlock.SetColor(GrassTintPropertyId, grassTint);
        renderer.SetPropertyBlock(biomeTintPropertyBlock);
    }

    private Color EvaluateChunkGrassTint(Vector2Int coord)
    {
        int centerX = coord.x * Chunk.SizeX + Chunk.SizeX / 2;
        int centerZ = coord.y * Chunk.SizeZ + Chunk.SizeZ / 2;
        return GetGrassTintForBiome(GetBiomeAt(centerX, centerZ));
    }
}

