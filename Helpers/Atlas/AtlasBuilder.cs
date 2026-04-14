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
            try
            {
                pixels = readableTexture.GetPixels32();
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
                ownsTextureCopy = ownsCopy
            });
        }

        if (prepared.Count == 0)
            throw new ArgumentException("Nenhuma entrada valida foi informada para construir o atlas.");

        return prepared;
    }

    private static Texture2D EnsureReadable(Texture2D source, out bool ownsCopy)
    {
        ownsCopy = false;
        if (source == null)
            return null;

        try
        {
            source.GetPixels32();
            return source;
        }
        catch (UnityException)
        {
            Texture2D copy = CreateReadableCopy(source);
            ownsCopy = copy != null;
            return copy;
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
            Texture2D texture = entries[i].texture;
            int paddedWidth = texture.width + (paddingPixels * 2);
            int paddedHeight = texture.height + (paddingPixels * 2);
            largestSide = Mathf.Max(largestSide, paddedWidth, paddedHeight);
        }

        return largestSide;
    }

    private bool TryPack(List<PreparedEntry> entries, int paddingPixels, int atlasSize, out List<TexturePacker.PackedItem> packedItems)
    {
        List<TexturePacker.Input> packInputs = new List<TexturePacker.Input>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Texture2D texture = entries[i].texture;
            packInputs.Add(new TexturePacker.Input
            {
                id = entries[i].id,
                width = texture.width + (paddingPixels * 2),
                height = texture.height + (paddingPixels * 2)
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
            Texture2D texture = entry.texture;
            TexturePacker.PackedItem packed = packedLookup[entry.id];

            RectInt paddedRect = packed.rect;
            RectInt pixelRect = new RectInt(
                paddedRect.x + settings.paddingPixels,
                paddedRect.y + settings.paddingPixels,
                texture.width,
                texture.height);

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

        atlas.Apply(settings.generateMipmaps, false);
        return result;
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
