
using UnityEngine;
using System.Collections.Generic;
using System.IO;

public class TextureAtlasGenerator : MonoBehaviour
{
    [Header("Padding (extrusão real)")]
    public int paddingPixels = 2; // use 2 ou 4 (recomendado)

    [Header("Configuração do Atlas")]
    public List<Texture2D> blockTextures; // Texturas individuais
    public int blocksX = 9;               // Número de blocos no eixo X
    public int blocksY = 10;              // Número de blocos no eixo Y
    public int atlasWidth = 1152;         // Largura total do atlas
    public int atlasHeight = 1280;        // Altura total do atlas
    [Range(0.1f, 1f)]
    public float blockScale = 1f;         // Escala do bloco dentro da célula

    [Header("Opções")]
    public bool saveToFile = true;
    public string savePath = "Assets/AtlasGerado.png";

    public void GenerateAtlas()
    {
        if (blockTextures == null || blockTextures.Count == 0)
        {
            Debug.LogError("Nenhuma textura de bloco foi atribuída!");
            return;
        }

        int cellWidth = atlasWidth / blocksX;
        int cellHeight = atlasHeight / blocksY;

        Texture2D atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
        atlas.filterMode = FilterMode.Point;
        atlas.wrapMode = TextureWrapMode.Clamp;

        // Limpa o atlas
        Color[] clearPixels = new Color[atlasWidth * atlasHeight];
        for (int i = 0; i < clearPixels.Length; i++)
            clearPixels[i] = new Color(0, 0, 0, 0);
        atlas.SetPixels(clearPixels);

        for (int y = 0; y < blocksY; y++)
        {
            for (int x = 0; x < blocksX; x++)
            {
                int index = y * blocksX + x;
                if (index >= blockTextures.Count)
                    break;

                Texture2D blockTex = blockTextures[index];

                int targetWidth = Mathf.RoundToInt(cellWidth * blockScale);
                int targetHeight = Mathf.RoundToInt(cellHeight * blockScale);

                if (blockTex.width != targetWidth || blockTex.height != targetHeight)
                    blockTex = ResizeTexture(blockTex, targetWidth, targetHeight);

                int offsetX = x * cellWidth + (cellWidth - targetWidth) / 2;
                int offsetY = y * cellHeight + (cellHeight - targetHeight) / 2;

                BlitWithPadding(
                    atlas,
                    blockTex,
                    offsetX,
                    offsetY,
                    targetWidth,
                    targetHeight,
                    paddingPixels
                );
            }
        }

        atlas.Apply(false, false);

        if (saveToFile)
        {
            byte[] pngData = atlas.EncodeToPNG();
            File.WriteAllBytes(savePath, pngData);
            Debug.Log($"Atlas salvo em: {savePath}");
        }

        var rend = GetComponent<Renderer>();
        if (rend != null)
            rend.sharedMaterial.mainTexture = atlas;
    }

    private void BlitWithPadding(
        Texture2D atlas,
        Texture2D src,
        int dstX,
        int dstY,
        int width,
        int height,
        int padding)
    {
        Color[] pixels = src.GetPixels();

        // Centro
        atlas.SetPixels(dstX, dstY, width, height, pixels);

        // Bordas esquerda e direita
        for (int y = 0; y < height; y++)
        {
            Color left = pixels[y * width];
            Color right = pixels[y * width + (width - 1)];

            for (int p = 1; p <= padding; p++)
            {
                atlas.SetPixel(dstX - p, dstY + y, left);
                atlas.SetPixel(dstX + width - 1 + p, dstY + y, right);
            }
        }

        // Bordas inferior e superior
        for (int x = 0; x < width; x++)
        {
            Color bottom = pixels[x];
            Color top = pixels[(height - 1) * width + x];

            for (int p = 1; p <= padding; p++)
            {
                atlas.SetPixel(dstX + x, dstY - p, bottom);
                atlas.SetPixel(dstX + x, dstY + height - 1 + p, top);
            }
        }

        // Cantos
        Color bl = pixels[0];
        Color br = pixels[width - 1];
        Color tl = pixels[(height - 1) * width];
        Color tr = pixels[(height - 1) * width + (width - 1)];

        for (int px = 1; px <= padding; px++)
        {
            for (int py = 1; py <= padding; py++)
            {
                atlas.SetPixel(dstX - px, dstY - py, bl);
                atlas.SetPixel(dstX + width - 1 + px, dstY - py, br);
                atlas.SetPixel(dstX - px, dstY + height - 1 + py, tl);
                atlas.SetPixel(dstX + width - 1 + px, dstY + height - 1 + py, tr);
            }
        }
    }

    private Texture2D ResizeTexture(Texture2D source, int width, int height)
    {
        source.filterMode = FilterMode.Point;

        RenderTexture rt = new RenderTexture(width, height, 0);
        rt.filterMode = FilterMode.Point;

        Graphics.Blit(source, rt);
        RenderTexture.active = rt;

        Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        result.filterMode = FilterMode.Point;
        result.wrapMode = TextureWrapMode.Clamp;

        result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        result.Apply();

        RenderTexture.active = null;
        rt.Release();

        return result;
    }
}
