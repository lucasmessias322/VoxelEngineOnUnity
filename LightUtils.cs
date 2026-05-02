using Unity.Mathematics;
using UnityEngine;

#region Utilities

public static class LightUtils
{
    // Junta as duas luzes (0-15) em um Ãºnico byte
    public static ushort PackLight(byte skyLight, byte blockLight)
    {
        return PackLightRgb(skyLight, blockLight, blockLight, blockLight);
    }

    // Extrai apenas a luz do cÃ©u (bits 4 a 7)
    public static ushort PackLightRgb(byte skyLight, byte blockRed, byte blockGreen, byte blockBlue)
    {
        return (ushort)(
            ((skyLight & 0x0F) << 12) |
            ((blockRed & 0x0F) << 8) |
            ((blockGreen & 0x0F) << 4) |
            (blockBlue & 0x0F));
    }

    public static ushort PackBlockLight(byte blockLight)
    {
        return PackLight(0, blockLight);
    }

    public static ushort PackBlockLightRgb(byte blockRed, byte blockGreen, byte blockBlue)
    {
        return PackLightRgb(0, blockRed, blockGreen, blockBlue);
    }

    public static ushort PackEmission(byte emission, Color color)
    {
        return PackEmission(emission, color.r, color.g, color.b);
    }

    public static ushort PackEmission(byte emission, float r, float g, float b)
    {
        emission = ClampNibble(emission);
        if (emission == 0)
            return 0;

        float maxComponent = math.max(r, math.max(g, b));
        if (maxComponent <= 0.001f)
        {
            r = 1f;
            g = 1f;
            b = 1f;
            maxComponent = 1f;
        }

        r = math.saturate(r / maxComponent);
        g = math.saturate(g / maxComponent);
        b = math.saturate(b / maxComponent);

        return PackBlockLightRgb(
            ScaleEmissionChannel(emission, r),
            ScaleEmissionChannel(emission, g),
            ScaleEmissionChannel(emission, b));
    }

    public static byte GetSkyLight(ushort packedLight)
    {
        return (byte)((packedLight >> 12) & 0x0F);
    }

    // Extrai apenas a luz dos blocos (bits 0 a 3)
    public static byte GetBlockLightR(ushort packedLight)
    {
        return (byte)((packedLight >> 8) & 0x0F);
    }

    public static byte GetBlockLightG(ushort packedLight)
    {
        return (byte)((packedLight >> 4) & 0x0F);
    }

    public static byte GetBlockLightB(ushort packedLight)
    {
        return (byte)(packedLight & 0x0F);
    }

    private static byte MaxLightComponent(byte a, byte b)
    {
        return a >= b ? a : b;
    }

    private static byte MinLightComponent(byte a, byte b)
    {
        return a <= b ? a : b;
    }

    public static byte GetBlockLight(ushort packedLight)
    {
        return MaxLightComponent(
            GetBlockLightR(packedLight),
            MaxLightComponent(GetBlockLightG(packedLight), GetBlockLightB(packedLight)));
    }

    public static ushort GetBlockLightPacked(ushort packedLight)
    {
        return PackBlockLightRgb(GetBlockLightR(packedLight), GetBlockLightG(packedLight), GetBlockLightB(packedLight));
    }

    public static bool HasBlockLight(ushort packedLight)
    {
        return GetBlockLight(packedLight) > 0;
    }

    public static ushort MaxBlockLight(ushort a, ushort b)
    {
        return PackBlockLightRgb(
            MaxLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MaxLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MaxLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static ushort MinBlockLight(ushort a, ushort b)
    {
        return PackBlockLightRgb(
            MinLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MinLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MinLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static ushort MaxPackedLight(ushort a, ushort b)
    {
        return PackLightRgb(
            MaxLightComponent(GetSkyLight(a), GetSkyLight(b)),
            MaxLightComponent(GetBlockLightR(a), GetBlockLightR(b)),
            MaxLightComponent(GetBlockLightG(a), GetBlockLightG(b)),
            MaxLightComponent(GetBlockLightB(a), GetBlockLightB(b)));
    }

    public static bool IsBlockLightGreater(ushort candidate, ushort current)
    {
        return GetBlockLightR(candidate) > GetBlockLightR(current) ||
               GetBlockLightG(candidate) > GetBlockLightG(current) ||
               GetBlockLightB(candidate) > GetBlockLightB(current);
    }

    public static ushort AttenuateBlockLight(ushort packedBlockLight, int loss)
    {
        loss = math.max(0, loss);
        return PackBlockLightRgb(
            (byte)math.max(0, GetBlockLightR(packedBlockLight) - loss),
            (byte)math.max(0, GetBlockLightG(packedBlockLight) - loss),
            (byte)math.max(0, GetBlockLightB(packedBlockLight) - loss));
    }

    public static uint EncodeBlockLightColor32(ushort packedLight)
    {
        uint r = (uint)(GetBlockLightR(packedLight) * 17);
        uint g = (uint)(GetBlockLightG(packedLight) * 17);
        uint b = (uint)(GetBlockLightB(packedLight) * 17);
        return r | (g << 8) | (b << 16) | (255u << 24);
    }

    public static uint EncodeWhiteBlockLightColor32(float blockLight01)
    {
        uint value = (uint)math.clamp((int)math.round(math.saturate(blockLight01) * 255f), 0, 255);
        return value | (value << 8) | (value << 16) | (255u << 24);
    }

    private static byte ClampNibble(byte value)
    {
        return (byte)math.min(value, 15);
    }

    private static byte ScaleEmissionChannel(byte emission, float channel)
    {
        return (byte)math.clamp((int)math.round(emission * math.saturate(channel)), 0, 15);
    }
}

#endregion
