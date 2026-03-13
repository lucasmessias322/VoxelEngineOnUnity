using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
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

    [Header("Biome Grass Tint")]
    public Color desertGrassTint = new Color(0.84f, 0.79f, 0.40f, 1f);
    public Color savannaGrassTint = new Color(0.66f, 0.73f, 0.33f, 1f);
    public Color meadowGrassTint = new Color(0.38039216f, 0.5176471f, 0.24705882f, 1f);
    public Color taigaGrassTint = new Color(0.31f, 0.44f, 0.34f, 1f);
    [Min(0)]
    public int biomeTintBlendRadius = 16;
    [Min(1)]
    public int biomeTintBlendStep = 2;
    [Min(0.01f)]
    public float biomeTintBlendExponent = 0.25f;

    private static readonly int GrassTintPropertyId = Shader.PropertyToID("_GrassTint");
    private MaterialPropertyBlock biomeTintPropertyBlock;

    private void InitializeBiomeNoiseOffsets()
    {
        if (biomeTemperatureOffset == Vector2.zero)
            biomeTemperatureOffset = new Vector2(seed * 0.173f, seed * -0.241f);

        if (biomeHumidityOffset == Vector2.zero)
            biomeHumidityOffset = new Vector2(seed * -0.137f, seed * 0.197f);
    }

    private BiomeNoiseSettings GetBiomeNoiseSettings()
    {
        return new BiomeNoiseSettings
        {
            temperatureScale = math.max(0.0001f, biomeTemperatureScale),
            humidityScale = math.max(0.0001f, biomeHumidityScale),
            temperatureOffset = new float2(biomeTemperatureOffset.x, biomeTemperatureOffset.y),
            humidityOffset = new float2(biomeHumidityOffset.x, biomeHumidityOffset.y),
            desertMinTemperature = math.saturate(desertMinTemperature),
            desertMaxHumidity = math.saturate(desertMaxHumidity),
            savannaMinTemperature = math.saturate(savannaMinTemperature),
            taigaMaxTemperature = math.saturate(taigaMaxTemperature),
            taigaMinHumidity = math.saturate(taigaMinHumidity)
        };
    }

    private BiomeType GetBiomeAt(int worldX, int worldZ)
    {
        return BiomeUtility.GetBiomeType(worldX, worldZ, GetBiomeNoiseSettings());
    }

    private BlockType GetBiomeSurfaceBlock(int worldX, int worldZ)
    {
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        return BiomeUtility.GetSurfaceBlock(biome);
    }

    private BlockType GetBiomeSubsurfaceBlock(int worldX, int worldZ)
    {
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        return BiomeUtility.GetSubsurfaceBlock(biome);
    }

    private Color GetGrassTintForBiome(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return desertGrassTint;
            case BiomeType.Savanna:
                return savannaGrassTint;
            case BiomeType.Taiga:
                return taigaGrassTint;
            case BiomeType.Meadow:
            default:
                return meadowGrassTint;
        }
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
        int radius = Mathf.Max(0, biomeTintBlendRadius);
        int step = Mathf.Max(1, biomeTintBlendStep);
        float blendExponent = Mathf.Max(0.01f, biomeTintBlendExponent);

        if (radius <= 0)
            return GetGrassTintForBiome(GetBiomeAt(centerX, centerZ));

        Color sum = Color.black;
        float weightSum = 0f;
        float radiusSq = radius * radius;

        for (int dz = -radius; dz <= radius; dz += step)
        {
            for (int dx = -radius; dx <= radius; dx += step)
            {
                float distSq = dx * dx + dz * dz;
                if (distSq > radiusSq)
                    continue;

                int sampleX = centerX + dx;
                int sampleZ = centerZ + dz;
                BiomeType biome = GetBiomeAt(sampleX, sampleZ);
                Color tint = GetGrassTintForBiome(biome);

                float distance01 = math.sqrt(distSq) / math.max(1f, radius);
                float weight = math.pow(math.saturate(1f - distance01), blendExponent);

                sum += tint * weight;
                weightSum += weight;
            }
        }

        if (weightSum <= 1e-5f)
            return GetGrassTintForBiome(GetBiomeAt(centerX, centerZ));

        return sum / weightSum;
    }
}
