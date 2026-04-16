using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class AtlasBuilder
{
    private struct PreparedEntry
    {
        public string id;
        public Texture2D texture;
        public Color32[] pixels;
        public int width;
        public int height;
        public bool ownsTextureCopy;
    }

    private readonly TexturePacker packer = new TexturePacker();

    public AtlasBuildResult Build(IList<AtlasTextureEntry> entries, AtlasBuildSettings settings)
    {
        if (entries == null || entries.Count == 0)
            throw new ArgumentException("A lista de texturas do atlas esta vazia.");

        settings.Sanitize();

        List<PreparedEntry> preparedEntries = PrepareEntries(entries);
        try
        {
            int minRequiredDimension = ComputeMinimumRequiredDimension(preparedEntries, settings.paddingPixels);
            int currentSize = Mathf.NextPowerOfTwo(Mathf.Max(settings.initialSize, minRequiredDimension));
            int maxSize = Mathf.Max(1, settings.maxSize);

            if (currentSize > maxSize)
            {
                throw new InvalidOperationException(
                    $"A maior textura nao cabe no limite maximo do atlas ({maxSize}px).");
            }

            List<TexturePacker.PackedItem> packedItems = null;
            bool packed = false;
            while (currentSize <= maxSize)
            {
                if (TryPack(preparedEntries, settings.paddingPixels, currentSize, out packedItems))
                {
                    packed = true;
                    break;
                }

                if (currentSize == int.MaxValue / 2)
                    break;

                currentSize *= 2;
            }

            if (!packed || packedItems == null)
            {
                throw new InvalidOperationException(
                    $"Nao foi possivel empacotar as texturas no limite maximo do atlas ({maxSize}px).");
            }

            return BuildAtlasTexture(preparedEntries, packedItems, settings, currentSize);
        }
        finally
        {
            CleanupPreparedEntries(preparedEntries);
        }
    }

    private List<PreparedEntry> PrepareEntries(IList<AtlasTextureEntry> entries)
    {
        List<PreparedEntry> prepared = new List<PreparedEntry>(entries.Count);
        HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < entries.Count; i++)
        {
            AtlasTextureEntry sourceEntry = entries[i];
            if (sourceEntry == null || sourceEntry.texture == null)
                continue;

            string id = (sourceEntry.id ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException($"Entrada de atlas sem ID na posicao {i}.");

            if (!usedIds.Add(id))
                throw new ArgumentException($"ID duplicado no atlas: '{id}'.");

            Texture2D readableTexture = EnsureReadable(sourceEntry.texture, out bool ownsCopy);
            if (readableTexture == null)
                throw new InvalidOperationException($"Nao foi possivel ler a textura '{sourceEntry.texture.name}' (ID '{id}').");

            Color32[] pixels;
            int entryWidth;
            int entryHeight;
            try
            {
                Color32[] sourcePixels = readableTexture.GetPixels32();
                if (sourceEntry.useUvSampling)
                {
                    pixels = ExtractPixelsByUvSampling(
                        sourcePixels,
                        readableTexture.width,
                        readableTexture.height,
                        sourceEntry.sampledUvRect,
                        out entryWidth,
                        out entryHeight);
                }
                else
                {
                    RectInt sourceRect = ResolveSourceRect(sourceEntry, readableTexture);
                    pixels = ExtractPixels(sourcePixels, readableTexture.width, readableTexture.height, sourceRect);
                    entryWidth = sourceRect.width;
                    entryHeight = sourceRect.height;
                }
            }
            catch (Exception ex)
            {
                if (ownsCopy && readableTexture != null)
                    UnityEngine.Object.Destroy(readableTexture);

                throw new InvalidOperationException(
                    $"Falha ao obter pixels da textura '{sourceEntry.texture.name}' (ID '{id}').", ex);
            }

            prepared.Add(new PreparedEntry
            {
                id = id,
                texture = readableTexture,
                pixels = pixels,
                width = entryWidth,
                height = entryHeight,
                ownsTextureCopy = ownsCopy
            });
        }

        if (prepared.Count == 0)
            throw new ArgumentException("Nenhuma entrada valida foi informada para construir o atlas.");

        return prepared;
    }

    private static RectInt ResolveSourceRect(AtlasTextureEntry entry, Texture2D texture)
    {
        if (entry == null || texture == null || !entry.useSourceRect)
            return new RectInt(0, 0, texture != null ? texture.width : 0, texture != null ? texture.height : 0);

        RectInt requestedRect = entry.sourceRect;
        if (requestedRect.width <= 0 || requestedRect.height <= 0)
        {
            throw new InvalidOperationException(
                $"A entrada '{entry.id}' usa sourceRect invalido ({requestedRect.width}x{requestedRect.height}).");
        }

        int safeXMin = Mathf.Clamp(requestedRect.xMin, 0, texture.width - 1);
        int safeYMin = Mathf.Clamp(requestedRect.yMin, 0, texture.height - 1);
        int safeXMax = Mathf.Clamp(requestedRect.xMax, safeXMin + 1, texture.width);
        int safeYMax = Mathf.Clamp(requestedRect.yMax, safeYMin + 1, texture.height);
        return new RectInt(
            safeXMin,
            safeYMin,
            safeXMax - safeXMin,
            safeYMax - safeYMin);
    }

    private static Color32[] ExtractPixels(
        Color32[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        RectInt sourceRect)
    {
        if (sourcePixels == null)
            return Array.Empty<Color32>();

        if (sourceRect.x == 0 &&
            sourceRect.y == 0 &&
            sourceRect.width == sourceWidth &&
            sourceRect.height == sourceHeight)
        {
            return sourcePixels;
        }

        Color32[] result = new Color32[sourceRect.width * sourceRect.height];
        for (int y = 0; y < sourceRect.height; y++)
        {
            int sourceRowStart = (sourceRect.y + y) * sourceWidth + sourceRect.x;
            int resultRowStart = y * sourceRect.width;
            Array.Copy(sourcePixels, sourceRowStart, result, resultRowStart, sourceRect.width);
        }

        return result;
    }

    private static Color32[] ExtractPixelsByUvSampling(
        Color32[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        Vector4 sampledUvRect,
        out int outputWidth,
        out int outputHeight)
    {
        outputWidth = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sampledUvRect.z - sampledUvRect.x)));
        outputHeight = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(sampledUvRect.w - sampledUvRect.y)));

        Color32[] targetPixels = new Color32[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        {
            float localVBottom = (y + 0.5f) / outputHeight;
            float localVTop = 1f - localVBottom;

            for (int x = 0; x < outputWidth; x++)
            {
                float localU = (x + 0.5f) / outputWidth;
                float sampleU = Mathf.Lerp(sampledUvRect.x, sampledUvRect.z, localU);
                float sampleVFromTop = Mathf.Lerp(sampledUvRect.y, sampledUvRect.w, localVTop);

                int pixelX = Mathf.Clamp(Mathf.FloorToInt(sampleU), 0, sourceWidth - 1);
                int pixelYFromTop = Mathf.Clamp(Mathf.FloorToInt(sampleVFromTop), 0, sourceHeight - 1);
                int pixelY = sourceHeight - 1 - pixelYFromTop;
                targetPixels[y * outputWidth + x] = sourcePixels[pixelY * sourceWidth + pixelX];
            }
        }

        return targetPixels;
    }

    private static Texture2D EnsureReadable(Texture2D source, out bool ownsCopy)
    {
        ownsCopy = false;
        if (source == null)
            return null;

        if (source.width <= 0 || source.height <= 0)
            return null;

        if (source.isReadable)
            return source;

        try
        {
            Texture2D copy = CreateReadableCopy(source);
            ownsCopy = copy != null;
            return copy;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Nao foi possivel criar uma copia legivel da textura '{source.name}'.",
                ex);
        }
    }

    private static Texture2D CreateReadableCopy(Texture2D source)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default);

        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readable.name = source.name + "_ReadableCopy";
            readable.filterMode = source.filterMode;
            readable.wrapMode = source.wrapMode;
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply(false, false);
            return readable;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static int ComputeMinimumRequiredDimension(List<PreparedEntry> entries, int paddingPixels)
    {
        int largestSide = 1;
        for (int i = 0; i < entries.Count; i++)
        {
            int paddedWidth = entries[i].width + (paddingPixels * 2);
            int paddedHeight = entries[i].height + (paddingPixels * 2);
            largestSide = Mathf.Max(largestSide, paddedWidth, paddedHeight);
        }

        return largestSide;
    }

    private bool TryPack(List<PreparedEntry> entries, int paddingPixels, int atlasSize, out List<TexturePacker.PackedItem> packedItems)
    {
        List<TexturePacker.Input> packInputs = new List<TexturePacker.Input>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            packInputs.Add(new TexturePacker.Input
            {
                id = entries[i].id,
                width = entries[i].width + (paddingPixels * 2),
                height = entries[i].height + (paddingPixels * 2)
            });
        }

        return packer.TryPack(packInputs, atlasSize, atlasSize, out packedItems);
    }

    private static AtlasBuildResult BuildAtlasTexture(
        List<PreparedEntry> preparedEntries,
        List<TexturePacker.PackedItem> packedItems,
        AtlasBuildSettings settings,
        int atlasSize)
    {
        Dictionary<string, TexturePacker.PackedItem> packedLookup = new Dictionary<string, TexturePacker.PackedItem>(StringComparer.Ordinal);
        for (int i = 0; i < packedItems.Count; i++)
            packedLookup[packedItems[i].id] = packedItems[i];

        Texture2D atlas = new Texture2D(atlasSize, atlasSize, settings.format, settings.generateMipmaps, false);
        atlas.name = $"RuntimeAtlas_{atlasSize}";
        atlas.filterMode = settings.filterMode;
        atlas.wrapMode = settings.wrapMode;

        atlas.SetPixels32(new Color32[atlasSize * atlasSize]);

        AtlasBuildResult result = new AtlasBuildResult
        {
            atlasTexture = atlas,
            atlasWidth = atlasSize,
            atlasHeight = atlasSize
        };

        float invAtlasSize = 1f / atlasSize;
        for (int i = 0; i < preparedEntries.Count; i++)
        {
            PreparedEntry entry = preparedEntries[i];
            TexturePacker.PackedItem packed = packedLookup[entry.id];

            RectInt paddedRect = packed.rect;
            RectInt pixelRect = new RectInt(
                paddedRect.x + settings.paddingPixels,
                paddedRect.y + settings.paddingPixels,
                entry.width,
                entry.height);

            atlas.SetPixels32(pixelRect.x, pixelRect.y, pixelRect.width, pixelRect.height, entry.pixels);
            ExtrudePadding(
                atlas,
                entry.pixels,
                pixelRect.x,
                pixelRect.y,
                pixelRect.width,
                pixelRect.height,
                settings.paddingPixels);

            Rect uvRect = new Rect(
                pixelRect.x * invAtlasSize,
                pixelRect.y * invAtlasSize,
                pixelRect.width * invAtlasSize,
                pixelRect.height * invAtlasSize);

            result.uvMap[entry.id] = uvRect;
            result.pixelRectMap[entry.id] = pixelRect;
            result.paddedPixelRectMap[entry.id] = paddedRect;
        }

        if (settings.generateMipmaps && settings.generateMipmapsPerTile)
            BuildPerTileMipmaps(atlas, preparedEntries, packedLookup, settings);

        atlas.Apply(settings.generateMipmaps && !settings.generateMipmapsPerTile, false);
        return result;
    }

    private static void BuildPerTileMipmaps(
        Texture2D atlas,
        List<PreparedEntry> preparedEntries,
        Dictionary<string, TexturePacker.PackedItem> packedLookup,
        AtlasBuildSettings settings)
    {
        if (atlas == null || preparedEntries == null || preparedEntries.Count == 0)
            return;

        int mipCount = atlas.mipmapCount;
        if (mipCount <= 1)
            return;

        int atlasSize = atlas.width;
        for (int mipLevel = 1; mipLevel < mipCount; mipLevel++)
        {
            int mipAtlasSize = Mathf.Max(1, atlasSize >> mipLevel);
            Color32[] mipPixels = new Color32[mipAtlasSize * mipAtlasSize];
            float scale = 1f / (1 << mipLevel);

            for (int i = 0; i < preparedEntries.Count; i++)
            {
                PreparedEntry entry = preparedEntries[i];
                if (!packedLookup.TryGetValue(entry.id, out TexturePacker.PackedItem packed))
                    continue;

                RectInt basePaddedRect = packed.rect;
                RectInt basePixelRect = new RectInt(
                    basePaddedRect.x + settings.paddingPixels,
                    basePaddedRect.y + settings.paddingPixels,
                    entry.width,
                    entry.height);

                RectInt mipPixelRect = ScaleRectForMip(basePixelRect, scale, mipAtlasSize, mipAtlasSize);
                if (mipPixelRect.width <= 0 || mipPixelRect.height <= 0)
                    continue;

                Color32[] scaledPixels = DownsamplePixelsBox(
                    entry.pixels,
                    entry.width,
                    entry.height,
                    mipPixelRect.width,
                    mipPixelRect.height);

                WritePixels(
                    mipPixels,
                    mipAtlasSize,
                    mipAtlasSize,
                    mipPixelRect.x,
                    mipPixelRect.y,
                    mipPixelRect.width,
                    mipPixelRect.height,
                    scaledPixels);

                int mipPadding = Mathf.Max(1, Mathf.CeilToInt(settings.paddingPixels * scale));
                ExtrudePadding(
                    mipPixels,
                    mipAtlasSize,
                    mipAtlasSize,
                    scaledPixels,
                    mipPixelRect.x,
                    mipPixelRect.y,
                    mipPixelRect.width,
                    mipPixelRect.height,
                    mipPadding);
            }

            atlas.SetPixels32(mipPixels, mipLevel);
        }
    }

    private static RectInt ScaleRectForMip(RectInt rect, float scale, int atlasWidth, int atlasHeight)
    {
        int xMin = Mathf.Clamp(Mathf.FloorToInt(rect.xMin * scale), 0, atlasWidth - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(rect.yMin * scale), 0, atlasHeight - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(rect.xMax * scale), xMin + 1, atlasWidth);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(rect.yMax * scale), yMin + 1, atlasHeight);

        return new RectInt(
            xMin,
            yMin,
            xMax - xMin,
            yMax - yMin);
    }

    private static Color32[] DownsamplePixelsBox(
        Color32[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourcePixels == null || sourcePixels.Length == 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return Array.Empty<Color32>();

        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return sourcePixels;

        Color32[] result = new Color32[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceYMin = Mathf.Clamp(Mathf.FloorToInt((float)(y * sourceHeight) / targetHeight), 0, sourceHeight - 1);
            int sourceYMax = Mathf.Clamp(Mathf.CeilToInt((float)((y + 1) * sourceHeight) / targetHeight), sourceYMin + 1, sourceHeight);

            for (int x = 0; x < targetWidth; x++)
            {
                int sourceXMin = Mathf.Clamp(Mathf.FloorToInt((float)(x * sourceWidth) / targetWidth), 0, sourceWidth - 1);
                int sourceXMax = Mathf.Clamp(Mathf.CeilToInt((float)((x + 1) * sourceWidth) / targetWidth), sourceXMin + 1, sourceWidth);

                int sumR = 0;
                int sumG = 0;
                int sumB = 0;
                int sumA = 0;
                int count = 0;

                for (int sy = sourceYMin; sy < sourceYMax; sy++)
                {
                    int rowStart = sy * sourceWidth;
                    for (int sx = sourceXMin; sx < sourceXMax; sx++)
                    {
                        Color32 color = sourcePixels[rowStart + sx];
                        sumR += color.r;
                        sumG += color.g;
                        sumB += color.b;
                        sumA += color.a;
                        count++;
                    }
                }

                if (count <= 0)
                    continue;

                result[y * targetWidth + x] = new Color32(
                    (byte)(sumR / count),
                    (byte)(sumG / count),
                    (byte)(sumB / count),
                    (byte)(sumA / count));
            }
        }

        return result;
    }

    private static void WritePixels(
        Color32[] targetPixels,
        int targetWidth,
        int targetHeight,
        int dstX,
        int dstY,
        int width,
        int height,
        Color32[] sourcePixels)
    {
        if (targetPixels == null || sourcePixels == null || width <= 0 || height <= 0)
            return;

        for (int y = 0; y < height; y++)
        {
            int targetY = dstY + y;
            if (targetY < 0 || targetY >= targetHeight)
                continue;

            int targetRowStart = targetY * targetWidth;
            int sourceRowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int targetX = dstX + x;
                if (targetX < 0 || targetX >= targetWidth)
                    continue;

                targetPixels[targetRowStart + targetX] = sourcePixels[sourceRowStart + x];
            }
        }
    }

    private static void ExtrudePadding(
        Texture2D atlas,
        Color32[] sourcePixels,
        int dstX,
        int dstY,
        int width,
        int height,
        int padding)
    {
        if (atlas == null || sourcePixels == null || width <= 0 || height <= 0 || padding <= 0)
            return;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            Color32 left = sourcePixels[rowStart];
            Color32 right = sourcePixels[rowStart + width - 1];

            for (int p = 1; p <= padding; p++)
            {
                atlas.SetPixel(dstX - p, dstY + y, left);
                atlas.SetPixel(dstX + width - 1 + p, dstY + y, right);
            }
        }

        for (int x = 0; x < width; x++)
        {
            Color32 bottom = sourcePixels[x];
            Color32 top = sourcePixels[(height - 1) * width + x];

            for (int p = 1; p <= padding; p++)
            {
                atlas.SetPixel(dstX + x, dstY - p, bottom);
                atlas.SetPixel(dstX + x, dstY + height - 1 + p, top);
            }
        }

        Color32 bottomLeft = sourcePixels[0];
        Color32 bottomRight = sourcePixels[width - 1];
        Color32 topLeft = sourcePixels[(height - 1) * width];
        Color32 topRight = sourcePixels[(height - 1) * width + (width - 1)];

        for (int px = 1; px <= padding; px++)
        {
            for (int py = 1; py <= padding; py++)
            {
                atlas.SetPixel(dstX - px, dstY - py, bottomLeft);
                atlas.SetPixel(dstX + width - 1 + px, dstY - py, bottomRight);
                atlas.SetPixel(dstX - px, dstY + height - 1 + py, topLeft);
                atlas.SetPixel(dstX + width - 1 + px, dstY + height - 1 + py, topRight);
            }
        }
    }

    private static void ExtrudePadding(
        Color32[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        Color32[] sourcePixels,
        int dstX,
        int dstY,
        int width,
        int height,
        int padding)
    {
        if (atlasPixels == null || sourcePixels == null || width <= 0 || height <= 0 || padding <= 0)
            return;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            Color32 left = sourcePixels[rowStart];
            Color32 right = sourcePixels[rowStart + width - 1];

            for (int p = 1; p <= padding; p++)
            {
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX - p, dstY + y, left);
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX + width - 1 + p, dstY + y, right);
            }
        }

        for (int x = 0; x < width; x++)
        {
            Color32 bottom = sourcePixels[x];
            Color32 top = sourcePixels[(height - 1) * width + x];

            for (int p = 1; p <= padding; p++)
            {
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX + x, dstY - p, bottom);
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX + x, dstY + height - 1 + p, top);
            }
        }

        Color32 bottomLeft = sourcePixels[0];
        Color32 bottomRight = sourcePixels[width - 1];
        Color32 topLeft = sourcePixels[(height - 1) * width];
        Color32 topRight = sourcePixels[(height - 1) * width + (width - 1)];

        for (int px = 1; px <= padding; px++)
        {
            for (int py = 1; py <= padding; py++)
            {
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX - px, dstY - py, bottomLeft);
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX + width - 1 + px, dstY - py, bottomRight);
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX - px, dstY + height - 1 + py, topLeft);
                SetPixelSafe(atlasPixels, atlasWidth, atlasHeight, dstX + width - 1 + px, dstY + height - 1 + py, topRight);
            }
        }
    }

    private static void SetPixelSafe(
        Color32[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        int x,
        int y,
        Color32 color)
    {
        if (atlasPixels == null || x < 0 || y < 0 || x >= atlasWidth || y >= atlasHeight)
            return;

        atlasPixels[y * atlasWidth + x] = color;
    }

    private static void CleanupPreparedEntries(List<PreparedEntry> preparedEntries)
    {
        for (int i = 0; i < preparedEntries.Count; i++)
        {
            PreparedEntry entry = preparedEntries[i];
            if (entry.ownsTextureCopy && entry.texture != null)
                UnityEngine.Object.Destroy(entry.texture);
        }
    }
}
