using System.Collections.Generic;
using UnityEngine;

public static class BlockItemIconCache
{
    private const float LeftShade = 0.82f;
    private const float RightShade = 0.62f;
    private const float TopShade = 1f;

    private static readonly Dictionary<BlockType, Sprite> Cache = new Dictionary<BlockType, Sprite>();

    private struct FaceVertex
    {
        public Vector2 position;
        public Vector2 uv;

        public FaceVertex(Vector2 position, Vector2 uv)
        {
            this.position = position;
            this.uv = uv;
        }
    }

    public static bool TryGetIcon(BlockType blockType, out Sprite sprite)
    {
        sprite = null;

        if (blockType == BlockType.Air)
            return false;

        if (Cache.TryGetValue(blockType, out Sprite cached) && cached != null)
        {
            sprite = cached;
            return true;
        }

        Sprite generated = BuildIsometricIcon(blockType);
        if (generated == null)
            return false;

        Cache[blockType] = generated;
        sprite = generated;
        return true;
    }

    private static Sprite BuildIsometricIcon(BlockType blockType)
    {
        if (!TryResolveBlockContext(out BlockDataSO blockData, out Texture atlasTexture))
            return null;

        blockData.InitializeDictionary();

        Vector2Int atlasTiles = new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.x)),
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.y))
        );

        Vector2Int topCoord = blockData.GetTileCoord(blockType, BlockFace.Top);
        Vector2Int sideCoord = blockData.GetTileCoord(blockType, BlockFace.Side);

        if (!TryExtractTilePixels(atlasTexture, atlasTiles, topCoord, out Color32[] topPixels, out int tileWidth, out int tileHeight))
            return null;

        if (!TryExtractTilePixels(atlasTexture, atlasTiles, sideCoord, out Color32[] sidePixels, out int sideWidth, out int sideHeight))
            return null;

        int faceSize = Mathf.Max(1, Mathf.Min(Mathf.Min(tileWidth, tileHeight), Mathf.Min(sideWidth, sideHeight)));
        int iconSize = Mathf.Clamp(faceSize * 4, 48, 128);
        Color32[] iconPixels = new Color32[iconSize * iconSize];

        BuildCubeIconPixels(
            iconPixels,
            iconSize,
            topPixels,
            tileWidth,
            tileHeight,
            sidePixels,
            sideWidth,
            sideHeight,
            faceSize);

        Texture2D iconTexture = new Texture2D(iconSize, iconSize, TextureFormat.RGBA32, false, false);
        iconTexture.name = $"BlockIcon_{blockType}";
        iconTexture.filterMode = FilterMode.Point;
        iconTexture.wrapMode = TextureWrapMode.Clamp;
        iconTexture.SetPixels32(iconPixels);
        iconTexture.Apply(false, false);
        iconTexture.hideFlags = HideFlags.HideAndDontSave;

        Sprite sprite = Sprite.Create(iconTexture, new Rect(0f, 0f, iconSize, iconSize), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = $"BlockIcon_{blockType}_Sprite";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static bool TryResolveBlockContext(out BlockDataSO blockData, out Texture atlasTexture)
    {
        blockData = null;
        atlasTexture = null;

        World world = World.Instance;
        if (world == null)
            return false;

        blockData = world.blockData;
        if (blockData == null)
            return false;

        Material[] materials = world.Material;
        if (materials == null || materials.Length == 0)
            return false;

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
                continue;

            Texture candidate = material.HasProperty("_Atlas") ? material.GetTexture("_Atlas") : null;
            if (candidate == null && material.HasProperty("_BaseMap"))
                candidate = material.GetTexture("_BaseMap");
            if (candidate == null && material.HasProperty("_MainTex"))
                candidate = material.GetTexture("_MainTex");
            if (candidate == null)
                candidate = material.mainTexture;

            if (candidate != null)
            {
                atlasTexture = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractTilePixels(
        Texture atlasTexture,
        Vector2Int atlasTiles,
        Vector2Int tileCoord,
        out Color32[] tilePixels,
        out int tileWidth,
        out int tileHeight)
    {
        tilePixels = null;
        tileWidth = 0;
        tileHeight = 0;

        if (atlasTexture == null || atlasTiles.x <= 0 || atlasTiles.y <= 0)
            return false;

        tileWidth = atlasTexture.width / atlasTiles.x;
        tileHeight = atlasTexture.height / atlasTiles.y;
        if (tileWidth <= 0 || tileHeight <= 0)
            return false;

        int safeTileX = Mathf.Clamp(tileCoord.x, 0, atlasTiles.x - 1);
        int safeTileY = Mathf.Clamp(tileCoord.y, 0, atlasTiles.y - 1);
        int pixelX = safeTileX * tileWidth;
        int pixelY = safeTileY * tileHeight;

        if (atlasTexture is Texture2D readableAtlas && readableAtlas.isReadable)
            return TryExtractFromReadableTexture(readableAtlas, pixelX, pixelY, tileWidth, tileHeight, out tilePixels);

        return TryExtractFromGpu(atlasTexture, atlasTiles, safeTileX, safeTileY, tileWidth, tileHeight, out tilePixels);
    }

    private static bool TryExtractFromReadableTexture(
        Texture2D atlasTexture,
        int pixelX,
        int pixelY,
        int tileWidth,
        int tileHeight,
        out Color32[] tilePixels)
    {
        tilePixels = null;

        Color32[] atlasPixels;
        try
        {
            atlasPixels = atlasTexture.GetPixels32();
        }
        catch
        {
            return false;
        }

        int atlasWidth = atlasTexture.width;
        tilePixels = new Color32[tileWidth * tileHeight];

        for (int y = 0; y < tileHeight; y++)
        {
            int srcRowStart = (pixelY + y) * atlasWidth + pixelX;
            int dstRowStart = y * tileWidth;
            for (int x = 0; x < tileWidth; x++)
            {
                tilePixels[dstRowStart + x] = atlasPixels[srcRowStart + x];
            }
        }

        return true;
    }

    private static bool TryExtractFromGpu(
        Texture atlasTexture,
        Vector2Int atlasTiles,
        int safeTileX,
        int safeTileY,
        int tileWidth,
        int tileHeight,
        out Color32[] tilePixels)
    {
        tilePixels = null;

        RenderTexture rt = RenderTexture.GetTemporary(tileWidth, tileHeight, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;

        Vector2 scale = new Vector2(1f / atlasTiles.x, 1f / atlasTiles.y);
        Vector2 offset = new Vector2((float)safeTileX / atlasTiles.x, (float)safeTileY / atlasTiles.y);

        try
        {
            Graphics.Blit(atlasTexture, rt, scale, offset);
            RenderTexture.active = rt;

            Texture2D cpuReadable = new Texture2D(tileWidth, tileHeight, TextureFormat.RGBA32, false, false);
            cpuReadable.ReadPixels(new Rect(0f, 0f, tileWidth, tileHeight), 0, 0, false);
            cpuReadable.Apply(false, false);
            tilePixels = cpuReadable.GetPixels32();
            DestroyTempTexture(cpuReadable);
            return tilePixels != null && tilePixels.Length == tileWidth * tileHeight;
        }
        catch
        {
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static void BuildCubeIconPixels(
        Color32[] destination,
        int iconSize,
        Color32[] topPixels,
        int topWidth,
        int topHeight,
        Color32[] sidePixels,
        int sideWidth,
        int sideHeight,
        int faceSize)
    {
        float cubeSize = faceSize;

        Vector3 p100 = new Vector3(cubeSize, 0f, 0f);
        Vector3 p101 = new Vector3(cubeSize, 0f, cubeSize);
        Vector3 p001 = new Vector3(0f, 0f, cubeSize);
        Vector3 p110 = new Vector3(cubeSize, cubeSize, 0f);
        Vector3 p111 = new Vector3(cubeSize, cubeSize, cubeSize);
        Vector3 p011 = new Vector3(0f, cubeSize, cubeSize);
        Vector3 p010 = new Vector3(0f, cubeSize, 0f);

        Vector3[] visiblePoints =
        {
            p100, p101, p001, p110, p111, p011, p010
        };

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        for (int i = 0; i < visiblePoints.Length; i++)
        {
            Vector2 projected = ProjectIsometric(visiblePoints[i], 1f);
            minX = Mathf.Min(minX, projected.x);
            minY = Mathf.Min(minY, projected.y);
            maxX = Mathf.Max(maxX, projected.x);
            maxY = Mathf.Max(maxY, projected.y);
        }

        float margin = Mathf.Max(2f, iconSize * 0.08f);
        float spanX = Mathf.Max(0.001f, maxX - minX);
        float spanY = Mathf.Max(0.001f, maxY - minY);
        float scale = Mathf.Min((iconSize - margin * 2f) / spanX, (iconSize - margin * 2f) / spanY);

        FaceVertex[] top = BuildFace(
            p010, p110, p111, p011,
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            minX, maxY, scale, margin);

        FaceVertex[] left = BuildFace(
            p011, p111, p101, p001,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            minX, maxY, scale, margin);

        FaceVertex[] right = BuildFace(
            p110, p111, p101, p100,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            minX, maxY, scale, margin);

        DrawFaceQuad(destination, iconSize, sidePixels, sideWidth, sideHeight, right, RightShade);
        DrawFaceQuad(destination, iconSize, sidePixels, sideWidth, sideHeight, left, LeftShade);
        DrawFaceQuad(destination, iconSize, topPixels, topWidth, topHeight, top, TopShade);
    }

    private static FaceVertex[] BuildFace(
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        float minX, float maxY, float scale, float margin)
    {
        return new[]
        {
            new FaceVertex(ToIconSpace(ProjectIsometric(p0, 1f), minX, maxY, scale, margin), uv0),
            new FaceVertex(ToIconSpace(ProjectIsometric(p1, 1f), minX, maxY, scale, margin), uv1),
            new FaceVertex(ToIconSpace(ProjectIsometric(p2, 1f), minX, maxY, scale, margin), uv2),
            new FaceVertex(ToIconSpace(ProjectIsometric(p3, 1f), minX, maxY, scale, margin), uv3),
        };
    }

    private static Vector2 ProjectIsometric(Vector3 position, float scale)
    {
        float x = (position.x - position.z) * scale;
        float y = ((position.x + position.z) * 0.5f - position.y) * scale;
        return new Vector2(x, y);
    }

    private static Vector2 ToIconSpace(Vector2 projected, float minX, float maxY, float scale, float margin)
    {
        return new Vector2(
            (projected.x - minX) * scale + margin,
            (maxY - projected.y) * scale + margin
        );
    }

    private static void DrawFaceQuad(
        Color32[] destination,
        int destinationSize,
        Color32[] source,
        int sourceWidth,
        int sourceHeight,
        FaceVertex[] quad,
        float shade)
    {
        DrawFaceTriangle(destination, destinationSize, source, sourceWidth, sourceHeight, quad[0], quad[1], quad[2], shade);
        DrawFaceTriangle(destination, destinationSize, source, sourceWidth, sourceHeight, quad[0], quad[2], quad[3], shade);
    }

    private static void DrawFaceTriangle(
        Color32[] destination,
        int destinationSize,
        Color32[] source,
        int sourceWidth,
        int sourceHeight,
        FaceVertex a,
        FaceVertex b,
        FaceVertex c,
        float shade)
    {
        float minX = Mathf.Min(a.position.x, b.position.x, c.position.x);
        float maxX = Mathf.Max(a.position.x, b.position.x, c.position.x);
        float minY = Mathf.Min(a.position.y, b.position.y, c.position.y);
        float maxY = Mathf.Max(a.position.y, b.position.y, c.position.y);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(minX), 0, destinationSize - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, destinationSize - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, destinationSize - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, destinationSize - 1);

        float area = Edge(a.position, b.position, c.position);
        if (Mathf.Abs(area) < 0.0001f)
            return;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);

                float w0 = Edge(b.position, c.position, p) / area;
                float w1 = Edge(c.position, a.position, p) / area;
                float w2 = 1f - w0 - w1;

                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                Vector2 uv = a.uv * w0 + b.uv * w1 + c.uv * w2;
                Color32 sampled = SampleNearest(source, sourceWidth, sourceHeight, uv);
                if (sampled.a == 0)
                    continue;

                sampled = MultiplyColor(sampled, shade);

                int index = y * destinationSize + x;
                destination[index] = AlphaBlend(destination[index], sampled);
            }
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p)
    {
        return (p.x - a.x) * (b.y - a.y) - (p.y - a.y) * (b.x - a.x);
    }

    private static Color32 SampleNearest(Color32[] pixels, int width, int height, Vector2 uv)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (width - 1)), 0, width - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * (height - 1)), 0, height - 1);
        return pixels[y * width + x];
    }

    private static Color32 MultiplyColor(Color32 color, float multiplier)
    {
        byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * multiplier), 0, 255);
        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * multiplier), 0, 255);
        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * multiplier), 0, 255);
        return new Color32(r, g, b, color.a);
    }

    private static Color32 AlphaBlend(Color32 dst, Color32 src)
    {
        float srcA = src.a / 255f;
        if (srcA <= 0f)
            return dst;

        float dstA = dst.a / 255f;
        float outA = srcA + dstA * (1f - srcA);
        if (outA <= 0f)
            return new Color32(0, 0, 0, 0);

        float outR = (src.r * srcA + dst.r * dstA * (1f - srcA)) / outA;
        float outG = (src.g * srcA + dst.g * dstA * (1f - srcA)) / outA;
        float outB = (src.b * srcA + dst.b * dstA * (1f - srcA)) / outA;

        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(outR), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(outG), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(outB), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(outA * 255f), 0, 255));
    }

    private static void DestroyTempTexture(Object texture)
    {
        if (texture == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(texture);
        else
            Object.DestroyImmediate(texture);
    }
}
