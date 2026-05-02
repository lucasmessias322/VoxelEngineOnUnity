using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Vegetation Rules

    private TreeSpawnRuleData[] GetActiveTreeSpawnRules()
    {
        if (treeSpawnRulesDirty)
            RebuildTreeSpawnRuleCache();

        return cachedTreeSpawnRules;
    }

    private VegetationBillboardRuleData[] GetActiveVegetationBillboardRules()
    {
        if (vegetationBillboardRulesDirty)
            RebuildVegetationBillboardRuleCache();

        return cachedVegetationBillboardRules;
    }

    public bool TryResolveVegetationBillboardAt(Vector3Int billboardPos, out BlockType billboardBlockType, out uint variationHash)
    {
        billboardBlockType = grassBillboardBlockType;
        variationHash = 0u;

        if (!enableGrassBillboards || IsFlatWorldMode() || grassBillboardChance <= 0f || billboardPos.y <= 0)
            return false;
        if (IsGrassBillboardSuppressed(billboardPos))
            return false;
        if (GetBlockAt(billboardPos) != BlockType.Air)
            return false;

        BlockType groundBlockType = GetBlockAt(new Vector3Int(billboardPos.x, billboardPos.y - 1, billboardPos.z));
        return TryResolveVegetationBillboardRule(
            billboardPos.x,
            billboardPos.y,
            billboardPos.z,
            groundBlockType,
            out billboardBlockType,
            out variationHash);
    }

    public bool TryResolveVegetationBillboardRule(
        int worldX,
        int worldY,
        int worldZ,
        BlockType groundBlockType,
        out BlockType billboardBlockType,
        out uint variationHash)
    {
        return VegetationBillboardUtility.TryResolveBillboardRule(
            GetBiomeNoiseSettings(),
            GetActiveVegetationBillboardRules(),
            worldX,
            worldY,
            worldZ,
            groundBlockType,
            grassBillboardChance,
            grassBillboardNoiseScale,
            grassBillboardBlockType,
            out billboardBlockType,
            out variationHash);
    }

    private int GetMaxTreeCanopyRadiusForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 0;

        int maxRadius = 0;
        for (int i = 0; i < rules.Length; i++)
            maxRadius = Mathf.Max(maxRadius, TreeGenerationMetrics.GetHorizontalReach(rules[i].treeStyle, rules[i].settings));

        return maxRadius;
    }

    private int GetMaxTreeRadiusForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 0;

        int maxRadius = 0;
        for (int i = 0; i < rules.Length; i++)
            maxRadius = Mathf.Max(maxRadius, TreeGenerationMetrics.GetPlacementSpacingRadius(rules[i].treeStyle, rules[i].settings));

        return maxRadius;
    }

    private int GetMaxTreeMarginForGeneration()
    {
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        if (rules.Length == 0)
            return 1;

        int maxMargin = 1;
        for (int i = 0; i < rules.Length; i++)
        {
            TreeSettings s = rules[i].settings;
            maxMargin = Mathf.Max(maxMargin, Mathf.Max(1, TreeGenerationMetrics.GetVerticalMargin(rules[i].treeStyle, s)));
        }

        return maxMargin;
    }

    private void RebuildTreeSpawnRuleCache()
    {
        treeSpawnRulesDirty = false;

        // As regras de spawn sao derivadas dos biomas para centralizar a configuracao.
        List<TreeSpawnRuleData> rules = treeSpawnRuleBuildBuffer;
        rules.Clear();
        AddTreeRulesFromBiomeDefinitions(rules);
        SortTreeSpawnRules(rules);

        cachedTreeSpawnRules = rules.Count > 0 ? rules.ToArray() : Array.Empty<TreeSpawnRuleData>();
    }

    private void RebuildVegetationBillboardRuleCache()
    {
        vegetationBillboardRulesDirty = false;

        List<VegetationBillboardRuleData> rules = vegetationBillboardRuleBuildBuffer;
        rules.Clear();
        AddVegetationRulesFromBiomeDefinitions(rules);

        if (rules.Count > 1)
        {
            rules.Sort((a, b) =>
            {
                int biomeCompare = a.biome.CompareTo(b.biome);
                if (biomeCompare != 0)
                    return biomeCompare;

                int groundCompare = a.groundBlock.CompareTo(b.groundBlock);
                if (groundCompare != 0)
                    return groundCompare;

                int weightCompare = b.weight.CompareTo(a.weight);
                if (weightCompare != 0)
                    return weightCompare;

                return a.billboardBlock.CompareTo(b.billboardBlock);
            });
        }

        cachedVegetationBillboardRules = rules.Count > 0
            ? rules.ToArray()
            : Array.Empty<VegetationBillboardRuleData>();
    }

    private void AddTreeRulesFromBiomeDefinitions(List<TreeSpawnRuleData> rules)
    {
        BiomeDefinitionSO[] definitions = GetConfiguredBiomeDefinitions();
        if (definitions == null || definitions.Length == 0)
            return;

        for (int i = 0; i < definitions.Length; i++)
        {
            BiomeDefinitionSO definition = definitions[i];
            if (definition == null || !definition.hasTrees)
                continue;
            if (definition.treeConfigs == null || definition.treeConfigs.Length == 0)
                continue;

            for (int j = 0; j < definition.treeConfigs.Length; j++)
            {
                BiomeTreeConfig treeConfig = definition.treeConfigs[j];
                if (!treeConfig.enabled)
                    continue;

                TreeSettings sanitized = SanitizeTreeSettings(treeConfig.treeStyle, treeConfig.settings);
                rules.Add(new TreeSpawnRuleData
                {
                    biome = definition.biomeType,
                    treeStyle = treeConfig.treeStyle,
                    settings = sanitized
                });
            }
        }
    }

    private void AddVegetationRulesFromBiomeDefinitions(List<VegetationBillboardRuleData> rules)
    {
        BiomeDefinitionSO[] definitions = GetConfiguredBiomeDefinitions();
        if (definitions == null || definitions.Length == 0)
            return;

        for (int i = 0; i < definitions.Length; i++)
        {
            BiomeDefinitionSO definition = definitions[i];
            if (definition == null || !definition.hasVegetationBillboards)
                continue;

            float chanceMultiplier = Mathf.Max(0f, definition.vegetationChanceMultiplier);
            BiomeVegetationBillboardConfig[] configs = definition.vegetationBillboards;
            bool addedBiomeRule = false;

            if (configs != null)
            {
                for (int j = 0; j < configs.Length; j++)
                {
                    BiomeVegetationBillboardConfig config = configs[j];
                    if (!config.enabled || config.blockType == BlockType.Air)
                        continue;

                    rules.Add(new VegetationBillboardRuleData
                    {
                        biome = definition.biomeType,
                        groundBlock = config.groundBlockType == BlockType.Air ? BlockType.Grass : config.groundBlockType,
                        billboardBlock = config.blockType,
                        weight = config.weight > 0f ? config.weight : 1f,
                        chanceMultiplier = chanceMultiplier
                    });
                    addedBiomeRule = true;
                }
            }

            // Compatibilidade: sem regras explicitas no bioma, usa o billboard global somente sobre Grass.
            if (!addedBiomeRule && grassBillboardBlockType != BlockType.Air)
            {
                rules.Add(new VegetationBillboardRuleData
                {
                    biome = definition.biomeType,
                    groundBlock = BlockType.Grass,
                    billboardBlock = grassBillboardBlockType,
                    weight = 1f,
                    chanceMultiplier = chanceMultiplier
                });
            }
        }
    }

    private static void SortTreeSpawnRules(List<TreeSpawnRuleData> rules)
    {
        if (rules == null || rules.Count <= 1)
            return;

        // Reserve space for larger canopies first so mixed-tree biomes behave more like Minecraft feature placement.
        rules.Sort((a, b) =>
        {
            int biomeCompare = a.biome.CompareTo(b.biome);
            if (biomeCompare != 0)
                return biomeCompare;

            int spacingCompare = TreeGenerationMetrics.GetPlacementSpacingRadius(b.treeStyle, b.settings)
                .CompareTo(TreeGenerationMetrics.GetPlacementSpacingRadius(a.treeStyle, a.settings));
            if (spacingCompare != 0)
                return spacingCompare;

            int densityCompare = a.settings.density.CompareTo(b.settings.density);
            if (densityCompare != 0)
                return densityCompare;

            return a.treeStyle.CompareTo(b.treeStyle);
        });
    }

    private TreeSettings SanitizeTreeSettings(TreeStyle treeStyle, TreeSettings raw)
    {
        TreeSettings s = raw;
        s.minHeight = Mathf.Max(1, s.minHeight);
        s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
        s.canopyRadius = Mathf.Max(0, s.canopyRadius);
        s.canopyHeight = Mathf.Max(1, s.canopyHeight);
        s.trunkClearance = Mathf.Max(0, s.trunkClearance);
        s.minSpacing = Mathf.Max(1, s.minSpacing);
        s.density = Mathf.Clamp01(s.density);
        s.noiseScale = Mathf.Max(0.0001f, s.noiseScale);

        switch (treeStyle)
        {
            case TreeStyle.TaigaSpruce:
                s.canopyRadius = Mathf.Max(2, s.canopyRadius);
                s.canopyHeight = Mathf.Max(5, s.canopyHeight);
                break;

            case TreeStyle.SavannaAcacia:
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(3, s.canopyHeight);
                s.minSpacing = Mathf.Max(6, s.minSpacing);
                break;

            case TreeStyle.FancyOak:
                s.minHeight = Mathf.Max(9, s.minHeight);
                s.maxHeight = Mathf.Max(s.minHeight, s.maxHeight);
                s.canopyRadius = Mathf.Max(4, s.canopyRadius);
                s.canopyHeight = Mathf.Max(4, s.canopyHeight);
                s.minSpacing = Mathf.Max(9, s.minSpacing);
                break;

            case TreeStyle.Cactus:
                s.canopyRadius = Mathf.Max(1, s.canopyRadius);
                break;
        }

        if (s.seed == 0)
            s.seed = seed;

        return s;
    }

    #endregion

}
