using Unity.Burst;
using Unity.Mathematics;

public enum BiomeType : byte
{
    Desert = 0,
    Savanna = 1,
    Meadow = 2,
    Taiga = 3
}

public struct BiomeNoiseSettings
{
    public float temperatureScale;
    public float humidityScale;
    public float2 temperatureOffset;
    public float2 humidityOffset;
    public float desertMinTemperature;
    public float desertMaxHumidity;
    public float savannaMinTemperature;
    public float taigaMaxTemperature;
    public float taigaMinHumidity;
}

public static class BiomeUtility
{
    [BurstCompile]
    public static BiomeType GetBiomeType(int worldX, int worldZ, in BiomeNoiseSettings settings)
    {
        float temperature = Sample01(worldX, worldZ, settings.temperatureScale, settings.temperatureOffset);
        float humidity = Sample01(worldX, worldZ, settings.humidityScale, settings.humidityOffset);
        return GetBiomeType(temperature, humidity, settings);
    }

    [BurstCompile]
    public static BiomeType GetBiomeType(float temperature, float humidity, in BiomeNoiseSettings settings)
    {
        if (temperature >= settings.desertMinTemperature && humidity <= settings.desertMaxHumidity)
            return BiomeType.Desert;

        if (temperature <= settings.taigaMaxTemperature && humidity >= settings.taigaMinHumidity)
            return BiomeType.Taiga;

        if (temperature >= settings.savannaMinTemperature)
            return BiomeType.Savanna;

        return BiomeType.Meadow;
    }

    [BurstCompile]
    public static BlockType GetSurfaceBlock(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return BlockType.Sand;
            case BiomeType.Taiga:

            case BiomeType.Savanna:
            case BiomeType.Meadow:
            default:
                return BlockType.Grass;
        }
    }

    [BurstCompile]
    public static BlockType GetSubsurfaceBlock(BiomeType biome)
    {
        switch (biome)
        {
            case BiomeType.Desert:
                return BlockType.Sand;
            case BiomeType.Taiga:
                return BlockType.Dirt;
            case BiomeType.Savanna:
            case BiomeType.Meadow:
            default:
                return BlockType.Dirt;
        }
    }

    [BurstCompile]
    private static float Sample01(int worldX, int worldZ, float scale, float2 offset)
    {
        float safeScale = math.max(0.0001f, scale);
        float nx = worldX * safeScale + offset.x;
        float nz = worldZ * safeScale + offset.y;
        return math.saturate(noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f);
    }
}
