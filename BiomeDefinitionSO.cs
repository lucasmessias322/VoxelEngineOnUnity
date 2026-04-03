using System;
using UnityEngine;

[Serializable]
public struct BiomeTreeConfig
{
    public bool enabled;
    public TreeStyle treeStyle;
    public TreeSettings settings;
}

[CreateAssetMenu(fileName = "BiomeDefinition", menuName = "ScriptableObjects/Biome Definition", order = 2)]
public class BiomeDefinitionSO : ScriptableObject
{
    public static event Action<BiomeDefinitionSO> DefinitionChanged;

    [Header("Biome")]
    public BiomeType biomeType = BiomeType.Meadow;
    public BlockType surfaceBlock = BlockType.Grass;
    public BlockType subsurfaceBlock = BlockType.Dirt;
    public Color grassTint = new Color(0.38f, 0.52f, 0.25f, 1f);

    [Header("3D Density")]
    [Tooltip("Quando ativo, este asset aplica os multiplicadores de densidade 3D deste bioma.")]
    public bool overrideDensityMultipliers = false;
    public BiomeDensityMultipliers densityMultipliers = BiomeDensityMultipliers.Identity;

    [Header("Trees")]
    public bool hasTrees = true;
    public BiomeTreeConfig[] treeConfigs = Array.Empty<BiomeTreeConfig>();

    private void OnValidate()
    {
        DefinitionChanged?.Invoke(this);
    }
}
