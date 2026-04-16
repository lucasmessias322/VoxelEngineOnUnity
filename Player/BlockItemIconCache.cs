using System.Collections.Generic;
using UnityEngine;

public static class BlockItemIconCache
{
    private const float LeftShade = 0.82f;
    private const float RightShade = 0.62f;
    private const float TopShade = 1f;

    private static readonly Dictionary<BlockType, Sprite> Cache = new Dictionary<BlockType, Sprite>();
    private static readonly Dictionary<int, Texture2D> ReadableAtlasCopies = new Dictionary<int, Texture2D>();
    private static int cachedBlockDataInstanceId;
    private static int cachedAtlasTextureInstanceId;
    private static Vector2Int cachedAtlasTiles = Vector2Int.zero;
    private static Vector2Int cachedConfiguredTileSize = Vector2Int.zero;
    private static bool cachedAtlasCoordinatesStartTopLeft = true;

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

    private enum IconTextureSlot : byte
    {
        Top = 0,
        Front = 1,
        Right = 2
    }

    private struct IconVertex3D
    {
        public Vector3 position;
        public Vector2 uv;

        public IconVertex3D(Vector3 position, Vector2 uv)
        {
            this.position = position;
            this.uv = uv;
        }
    }

    private struct IconFace3D
    {
        public IconTextureSlot textureSlot;
        public Color32[] sourcePixels;
        public int sourceWidth;
        public int sourceHeight;
        public float shade;
        public IconVertex3D[] vertices;
        public float sortKey;

        public IconFace3D(IconTextureSlot textureSlot, float shade, IconVertex3D[] vertices)
            : this(textureSlot, shade, vertices, null, 0, 0)
        {
        }

        public IconFace3D(
            IconTextureSlot textureSlot,
            float shade,
            IconVertex3D[] vertices,
            Color32[] sourcePixels,
            int sourceWidth,
            int sourceHeight)
        {
            this.textureSlot = textureSlot;
            this.sourcePixels = sourcePixels;
            this.sourceWidth = sourceWidth;
            this.sourceHeight = sourceHeight;
            this.shade = shade;
            this.vertices = vertices;

            float accumulatedDepth = 0f;
            for (int i = 0; i < vertices.Length; i++)
                accumulatedDepth += vertices[i].position.x + vertices[i].position.y + vertices[i].position.z;

            sortKey = vertices.Length > 0 ? accumulatedDepth / vertices.Length : 0f;
        }
    }

    private struct ProjectedFace
    {
        public FaceVertex[] vertices;
        public Color32[] sourcePixels;
        public int sourceWidth;
        public int sourceHeight;
        public float shade;
        public float sortKey;
    }

    public static bool TryGetIcon(BlockType blockType, out Sprite sprite)
    {
        sprite = null;

        if (blockType == BlockType.Air)
            return false;

        InvalidateCacheIfAtlasChanged();

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
        if (!TryResolveBlockContext(blockType, out BlockDataSO blockData, out Texture atlasTexture, out Vector2Int atlasTiles))
            return null;

        blockData.InitializeDictionary();
        BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
        if (mappingResult == null)
            return null;

        BlockTextureMapping mapping = mappingResult.Value;
        bool atlasCoordinatesStartTopLeft = blockData.atlasCoordinatesStartTopLeft;

        if (!TryExtractFacePixels(
                atlasTexture,
                atlasTiles,
                mapping,
                BlockFace.Top,
                atlasCoordinatesStartTopLeft,
                out Color32[] topPixels,
                out int tileWidth,
                out int tileHeight))
            return null;

        if (!TryExtractFacePixels(
                atlasTexture,
                atlasTiles,
                mapping,
                BlockFace.Front,
                atlasCoordinatesStartTopLeft,
                out Color32[] frontPixels,
                out int frontWidth,
                out int frontHeight))
            return null;

        if (!TryExtractFacePixels(
                atlasTexture,
                atlasTiles,
                mapping,
                BlockFace.Right,
                atlasCoordinatesStartTopLeft,
                out Color32[] rightPixels,
                out int rightWidth,
                out int rightHeight))
            return null;

        int faceSize = Mathf.Max(1, Mathf.Min(
            Mathf.Min(tileWidth, tileHeight),
            Mathf.Min(Mathf.Min(frontWidth, frontHeight), Mathf.Min(rightWidth, rightHeight))));
        int iconSize = Mathf.Clamp(faceSize * 4, 48, 128);
        Color32[] iconPixels = new Color32[iconSize * iconSize];

        BuildShapeIconPixels(
            iconPixels,
            iconSize,
            topPixels,
            tileWidth,
            tileHeight,
            frontPixels,
            frontWidth,
            frontHeight,
            rightPixels,
            rightWidth,
            rightHeight,
            mapping,
            atlasTexture,
            atlasTiles,
            atlasCoordinatesStartTopLeft);

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

    private static bool TryResolveBlockContext(BlockType blockType, out BlockDataSO blockData, out Texture atlasTexture, out Vector2Int atlasTiles)
    {
        blockData = null;
        atlasTexture = null;
        atlasTiles = Vector2Int.one;

        World world = World.Instance;
        if (world == null)
            return false;

        blockData = world.blockData;
        if (blockData == null)
            return false;

        blockData.InitializeDictionary();

        if (world.atlasTilesX > 0 && world.atlasTilesY > 0)
        {
            atlasTiles = new Vector2Int(world.atlasTilesX, world.atlasTilesY);
        }
        else if (blockData != null)
        {
            atlasTiles = new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.x)),
                Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.y))
            );
        }

        if (world.blockItemIconAtlasTexture != null)
        {
            atlasTexture = world.blockItemIconAtlasTexture;
            return true;
        }

        Material[] materials = world.Material;
        if (materials == null || materials.Length == 0)
            return false;

        int preferredMaterialIndex = 0;
        BlockTextureMapping? mapping = blockData.GetMapping(blockType);
        if (mapping != null)
            preferredMaterialIndex = Mathf.Clamp(mapping.Value.materialIndex, 0, materials.Length - 1);

        if (TryGetAtlasTextureFromMaterial(materials[preferredMaterialIndex], out atlasTexture))
            return true;

        for (int i = 0; i < materials.Length; i++)
        {
            if (i == preferredMaterialIndex)
                continue;

            if (TryGetAtlasTextureFromMaterial(materials[i], out atlasTexture))
                return true;
        }

        return false;
    }

    private static bool TryGetAtlasTextureFromMaterial(Material material, out Texture atlasTexture)
    {
        atlasTexture = null;
        if (material == null)
            return false;

        atlasTexture = material.HasProperty("_Atlas") ? material.GetTexture("_Atlas") : null;
        if (atlasTexture == null && material.HasProperty("_BaseMap"))
            atlasTexture = material.GetTexture("_BaseMap");
        if (atlasTexture == null && material.HasProperty("_MainTex"))
            atlasTexture = material.GetTexture("_MainTex");
        if (atlasTexture == null)
            atlasTexture = material.mainTexture;

        return atlasTexture != null;
    }

    private static bool TryExtractFacePixels(
        Texture atlasTexture,
        Vector2Int atlasTiles,
        BlockTextureMapping mapping,
        BlockFace face,
        bool atlasCoordinatesStartTopLeft,
        out Color32[] tilePixels,
        out int tileWidth,
        out int tileHeight)
    {
        tilePixels = null;
        tileWidth = 0;
        tileHeight = 0;

        if (atlasTexture == null)
            return false;

        Vector4 uvRectData = BlockAtlasUvUtility.ResolveUvRectData(
            mapping,
            face,
            atlasTiles,
            atlasCoordinatesStartTopLeft);
        return TryExtractFacePixels(atlasTexture, uvRectData, out tilePixels, out tileWidth, out tileHeight);
    }

    private static bool TryExtractFacePixels(
        Texture atlasTexture,
        Vector2Int atlasTiles,
        BlockModelCuboid cuboid,
        BlockTextureMapping fallbackMapping,
        BlockFace face,
        bool atlasCoordinatesStartTopLeft,
        out Color32[] tilePixels,
        out int tileWidth,
        out int tileHeight)
    {
        tilePixels = null;
        tileWidth = 0;
        tileHeight = 0;

        if (atlasTexture == null)
            return false;

        Vector4 uvRectData = BlockAtlasUvUtility.ResolveUvRectData(
            cuboid,
            face,
            fallbackMapping,
            atlasTiles,
            atlasCoordinatesStartTopLeft);
        return TryExtractFacePixels(atlasTexture, uvRectData, out tilePixels, out tileWidth, out tileHeight);
    }

    private static bool TryExtractFacePixels(
        Texture atlasTexture,
        Vector4 uvRectData,
        out Color32[] tilePixels,
        out int tileWidth,
        out int tileHeight)
    {
        tilePixels = null;
        tileWidth = 0;
        tileHeight = 0;

        if (!BlockAtlasUvUtility.IsValidUvRectData(uvRectData))
            return false;

        RectInt pixelRect = ResolvePixelRect(atlasTexture, uvRectData);
        tileWidth = pixelRect.width;
        tileHeight = pixelRect.height;
        if (tileWidth <= 0 || tileHeight <= 0)
            return false;

        if (atlasTexture is Texture2D readableAtlas && readableAtlas.isReadable)
            return TryExtractFromReadableTexture(readableAtlas, pixelRect.x, pixelRect.y, tileWidth, tileHeight, out tilePixels);

        if (TryGetReadableAtlasCopy(atlasTexture, out Texture2D readableAtlasCopy))
            return TryExtractFromReadableTexture(readableAtlasCopy, pixelRect.x, pixelRect.y, tileWidth, tileHeight, out tilePixels);

        return TryExtractFromGpu(atlasTexture, pixelRect, out tilePixels);
    }

    private static RectInt ResolvePixelRect(Texture atlasTexture, Vector4 uvRectData)
    {
        if (atlasTexture == null)
            return new RectInt(0, 0, 0, 0);

        int atlasWidth = Mathf.Max(1, atlasTexture.width);
        int atlasHeight = Mathf.Max(1, atlasTexture.height);
        int pixelX = Mathf.Clamp(Mathf.RoundToInt(uvRectData.x * atlasWidth), 0, atlasWidth - 1);
        int pixelY = Mathf.Clamp(Mathf.RoundToInt(uvRectData.y * atlasHeight), 0, atlasHeight - 1);
        int pixelWidth = Mathf.Max(1, Mathf.RoundToInt(uvRectData.z * atlasWidth));
        int pixelHeight = Mathf.Max(1, Mathf.RoundToInt(uvRectData.w * atlasHeight));

        pixelWidth = Mathf.Min(pixelWidth, atlasWidth - pixelX);
        pixelHeight = Mathf.Min(pixelHeight, atlasHeight - pixelY);

        return new RectInt(pixelX, pixelY, pixelWidth, pixelHeight);
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
        RectInt pixelRect,
        out Color32[] tilePixels)
    {
        tilePixels = null;
        int tileWidth = pixelRect.width;
        int tileHeight = pixelRect.height;
        if (atlasTexture == null || tileWidth <= 0 || tileHeight <= 0)
            return false;

        RenderTexture rt = RenderTexture.GetTemporary(tileWidth, tileHeight, 0, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Point;
        RenderTexture previous = RenderTexture.active;

        float tileScaleX = pixelRect.width / (float)atlasTexture.width;
        float tileScaleY = pixelRect.height / (float)atlasTexture.height;
        float texelInsetX = 0.5f / atlasTexture.width;
        float texelInsetY = 0.5f / atlasTexture.height;
        Vector2 scale = new Vector2(
            Mathf.Max(0f, tileScaleX - texelInsetX * 2f),
            Mathf.Max(0f, tileScaleY - texelInsetY * 2f));
        Vector2 offset = new Vector2(
            pixelRect.x / (float)atlasTexture.width + texelInsetX,
            pixelRect.y / (float)atlasTexture.height + texelInsetY);

        try
        {
            Graphics.Blit(atlasTexture, rt, scale, offset);
            RenderTexture.active = rt;

            Texture2D cpuReadable = new Texture2D(tileWidth, tileHeight, TextureFormat.RGBA32, false, false);
            cpuReadable.filterMode = FilterMode.Point;
            cpuReadable.wrapMode = TextureWrapMode.Clamp;
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

    private static void BuildShapeIconPixels(
        Color32[] destination,
        int iconSize,
        Color32[] topPixels,
        int topWidth,
        int topHeight,
        Color32[] frontPixels,
        int frontWidth,
        int frontHeight,
        Color32[] rightPixels,
        int rightWidth,
        int rightHeight,
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasCoordinatesStartTopLeft)
    {
        List<IconFace3D> faces = BuildFacesForShape(mapping, atlasTexture, atlasTiles, atlasCoordinatesStartTopLeft);
        if (faces.Count == 0)
            AddVisibleBoxFaces(faces, Vector3.zero, Vector3.one);

        RenderFaces(
            destination,
            iconSize,
            topPixels,
            topWidth,
            topHeight,
            frontPixels,
            frontWidth,
            frontHeight,
            rightPixels,
            rightWidth,
            rightHeight,
            faces);
    }

    private static List<IconFace3D> BuildFacesForShape(
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasCoordinatesStartTopLeft)
    {
        List<IconFace3D> faces = new List<IconFace3D>(12);
        switch (BlockShapeUtility.GetEffectiveRenderShape(mapping))
        {
            case BlockRenderShape.Cuboid:
                BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 cuboidMin, out Vector3 cuboidMax);
                AddVisibleBoxFaces(faces, cuboidMin, cuboidMax);
                break;

            case BlockRenderShape.MultiCuboid:
                AppendMultiCuboidFaces(faces, mapping, atlasTexture, atlasTiles, atlasCoordinatesStartTopLeft);
                break;

            case BlockRenderShape.Stairs:
                AppendStairFaces(faces);
                break;

            case BlockRenderShape.Ramp:
                AppendRampFaces(faces);
                break;

            case BlockRenderShape.VerticalRamp:
                AppendVerticalRampFaces(faces);
                break;

            case BlockRenderShape.Fence:
                AppendFenceFaces(faces);
                break;

            default:
                AddVisibleBoxFaces(faces, Vector3.zero, Vector3.one);
                break;
        }

        return faces;
    }

    private static void AppendMultiCuboidFaces(
        List<IconFace3D> faces,
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasCoordinatesStartTopLeft)
    {
        World world = World.Instance;
        BlockModelCuboid[] cuboids = world != null && world.blockData != null
            ? world.blockData.runtimeMultiCuboidBoxes
            : null;

        int boxCount = BlockShapeUtility.GetMultiCuboidBoxCount(mapping, cuboids);
        if (boxCount <= 0)
        {
            BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
            AddVisibleBoxFaces(faces, fallbackMin, fallbackMax);
            return;
        }

        for (int i = 0; i < boxCount; i++)
        {
            if (!BlockShapeUtility.TryGetMultiCuboidBox(mapping, cuboids, i, BlockPlacementAxis.Y, out ShapeBox box))
                continue;

            if (BlockShapeUtility.TryGetMultiCuboidModelCuboid(mapping, cuboids, i, out BlockModelCuboid cuboid))
            {
                AddVisibleBoxFaces(faces, box.min, box.max, cuboid, mapping, atlasTexture, atlasTiles, atlasCoordinatesStartTopLeft);
                continue;
            }

            AddVisibleBoxFaces(faces, box.min, box.max);
        }
    }

    private static bool TryGetReadableAtlasCopy(Texture atlasTexture, out Texture2D readableAtlas)
    {
        readableAtlas = null;
        if (atlasTexture == null || atlasTexture.width <= 0 || atlasTexture.height <= 0)
            return false;

        int atlasTextureInstanceId = atlasTexture.GetInstanceID();
        if (ReadableAtlasCopies.TryGetValue(atlasTextureInstanceId, out Texture2D cachedCopy) && cachedCopy != null)
        {
            readableAtlas = cachedCopy;
            return true;
        }

        Texture2D createdCopy = CreateReadableAtlasCopy(atlasTexture);
        if (createdCopy == null)
            return false;

        ReadableAtlasCopies[atlasTextureInstanceId] = createdCopy;
        readableAtlas = createdCopy;
        return true;
    }

    private static Texture2D CreateReadableAtlasCopy(Texture atlasTexture)
    {
        if (atlasTexture == null || atlasTexture.width <= 0 || atlasTexture.height <= 0)
            return null;

        RenderTexture rt = RenderTexture.GetTemporary(
            atlasTexture.width,
            atlasTexture.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default);
        rt.filterMode = FilterMode.Point;

        RenderTexture previous = RenderTexture.active;
        try
        {
            Graphics.Blit(atlasTexture, rt);
            RenderTexture.active = rt;

            Texture2D readable = new Texture2D(atlasTexture.width, atlasTexture.height, TextureFormat.RGBA32, false, false);
            readable.name = atlasTexture.name + "_ReadableIconCopy";
            readable.filterMode = FilterMode.Point;
            readable.wrapMode = TextureWrapMode.Clamp;
            readable.hideFlags = HideFlags.HideAndDontSave;
            readable.ReadPixels(new Rect(0f, 0f, atlasTexture.width, atlasTexture.height), 0, 0, false);
            readable.Apply(false, false);
            return readable;
        }
        catch
        {
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static void RenderFaces(
        Color32[] destination,
        int iconSize,
        Color32[] topPixels,
        int topWidth,
        int topHeight,
        Color32[] frontPixels,
        int frontWidth,
        int frontHeight,
        Color32[] rightPixels,
        int rightWidth,
        int rightHeight,
        List<IconFace3D> faces)
    {
        if (faces == null || faces.Count == 0)
            return;

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        for (int i = 0; i < faces.Count; i++)
        {
            IconFace3D face = faces[i];
            for (int vertexIndex = 0; vertexIndex < face.vertices.Length; vertexIndex++)
            {
                Vector2 projected = ProjectIsometric(face.vertices[vertexIndex].position, 1f);
                minX = Mathf.Min(minX, projected.x);
                minY = Mathf.Min(minY, projected.y);
                maxX = Mathf.Max(maxX, projected.x);
                maxY = Mathf.Max(maxY, projected.y);
            }
        }

        float margin = Mathf.Max(2f, iconSize * 0.08f);
        float spanX = Mathf.Max(0.001f, maxX - minX);
        float spanY = Mathf.Max(0.001f, maxY - minY);
        float scale = Mathf.Min((iconSize - margin * 2f) / spanX, (iconSize - margin * 2f) / spanY);

        List<ProjectedFace> projectedFaces = new List<ProjectedFace>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            IconFace3D face = faces[i];
            Color32[] sourcePixels = face.sourcePixels;
            int sourceWidth = face.sourceWidth;
            int sourceHeight = face.sourceHeight;
            if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                if (!TryResolveFaceTexture(
                        face.textureSlot,
                        topPixels,
                        topWidth,
                        topHeight,
                        frontPixels,
                        frontWidth,
                        frontHeight,
                        rightPixels,
                        rightWidth,
                        rightHeight,
                        out sourcePixels,
                        out sourceWidth,
                        out sourceHeight))
                {
                    continue;
                }
            }

            FaceVertex[] projectedVertices = new FaceVertex[face.vertices.Length];
            for (int vertexIndex = 0; vertexIndex < face.vertices.Length; vertexIndex++)
            {
                IconVertex3D vertex = face.vertices[vertexIndex];
                projectedVertices[vertexIndex] = new FaceVertex(
                    ToIconSpace(ProjectIsometric(vertex.position, 1f), minX, maxY, scale, margin),
                    vertex.uv);
            }

            projectedFaces.Add(new ProjectedFace
            {
                vertices = projectedVertices,
                sourcePixels = sourcePixels,
                sourceWidth = sourceWidth,
                sourceHeight = sourceHeight,
                shade = face.shade,
                sortKey = face.sortKey
            });
        }

        projectedFaces.Sort((a, b) => a.sortKey.CompareTo(b.sortKey));

        for (int i = 0; i < projectedFaces.Count; i++)
            DrawFacePolygon(destination, iconSize, projectedFaces[i]);
    }

    private static void AppendStairFaces(List<IconFace3D> faces)
    {
        byte rawState = (byte)StairPlacementUtility.Encode(StairFacing.South, false);
        StairShapeUtility.ResolveBoxes(rawState, StairShapeVariant.Straight, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);

        if (boxCount > 0) AddVisibleBoxFaces(faces, box0.min, box0.max);
        if (boxCount > 1) AddVisibleBoxFaces(faces, box1.min, box1.max);
        if (boxCount > 2) AddVisibleBoxFaces(faces, box2.min, box2.max);
        if (boxCount > 3) AddVisibleBoxFaces(faces, box3.min, box3.max);
        if (boxCount > 4) AddVisibleBoxFaces(faces, box4.min, box4.max);
    }

    private static void AppendRampFaces(List<IconFace3D> faces)
    {
        const BlockPlacementAxis axis = BlockPlacementAxis.ZNegative;
        const RampShapeVariant variant = RampShapeVariant.Straight;

        RampShapeUtility.ResolveTopTriangles(axis, variant, out Vector3 tri0a, out Vector3 tri0b, out Vector3 tri0c, out Vector3 tri1a, out Vector3 tri1b, out Vector3 tri1c);
        AddFace(faces, IconTextureSlot.Top, TopShade,
            MakeVertex(tri0a, ResolveFaceUv(BlockFace.Top, tri0a)),
            MakeVertex(tri0b, ResolveFaceUv(BlockFace.Top, tri0b)),
            MakeVertex(tri0c, ResolveFaceUv(BlockFace.Top, tri0c)));
        AddFace(faces, IconTextureSlot.Top, TopShade,
            MakeVertex(tri1a, ResolveFaceUv(BlockFace.Top, tri1a)),
            MakeVertex(tri1b, ResolveFaceUv(BlockFace.Top, tri1b)),
            MakeVertex(tri1c, ResolveFaceUv(BlockFace.Top, tri1c)));

        AppendRampEdgeFace(faces, axis, variant, RampEdge.Left);
        AppendRampEdgeFace(faces, axis, variant, RampEdge.Back);
    }

    private static void AppendRampEdgeFace(List<IconFace3D> faces, BlockPlacementAxis axis, RampShapeVariant variant, RampEdge edge)
    {
        if (!RampShapeUtility.ResolveEdgeSurface(axis, variant, edge, out int vertexCount, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out BlockFace sampledFace))
            return;

        if (!TryResolveTextureSlot(sampledFace, out IconTextureSlot textureSlot, out float shade))
            return;

        if (vertexCount == 4)
        {
            AddFace(faces, textureSlot, shade,
                MakeVertex(p0, ResolveFaceUv(sampledFace, p0)),
                MakeVertex(p1, ResolveFaceUv(sampledFace, p1)),
                MakeVertex(p2, ResolveFaceUv(sampledFace, p2)),
                MakeVertex(p3, ResolveFaceUv(sampledFace, p3)));
            return;
        }

        if (vertexCount == 3)
        {
            AddFace(faces, textureSlot, shade,
                MakeVertex(p0, ResolveFaceUv(sampledFace, p0)),
                MakeVertex(p1, ResolveFaceUv(sampledFace, p1)),
                MakeVertex(p2, ResolveFaceUv(sampledFace, p2)));
        }
    }

    private static void AppendVerticalRampFaces(List<IconFace3D> faces)
    {
        const BlockPlacementAxis axis = BlockPlacementAxis.ZNegative;

        VerticalRampShapeUtility.ResolveTopTriangle(axis, out Vector3 top0, out Vector3 top1, out Vector3 top2);
        AddFace(faces, IconTextureSlot.Top, TopShade,
            MakeVertex(top0, ResolveFaceUv(BlockFace.Top, top0)),
            MakeVertex(top1, ResolveFaceUv(BlockFace.Top, top1)),
            MakeVertex(top2, ResolveFaceUv(BlockFace.Top, top2)));

        VerticalRampShapeUtility.ResolveSideQuad(axis, out Vector3 side0, out Vector3 side1, out Vector3 side2, out Vector3 side3, out BlockFace sideFace);
        AppendVerticalRampQuadFace(faces, sideFace, side0, side1, side2, side3);

        VerticalRampShapeUtility.ResolveFrontQuad(axis, out Vector3 front0, out Vector3 front1, out Vector3 front2, out Vector3 front3, out BlockFace frontFace);
        AppendVerticalRampQuadFace(faces, frontFace, front0, front1, front2, front3);

        VerticalRampShapeUtility.ResolveSlopeQuad(axis, out Vector3 slope0, out Vector3 slope1, out Vector3 slope2, out Vector3 slope3, out BlockFace slopeFace, out _);
        AppendVerticalRampQuadFace(faces, slopeFace, slope0, slope1, slope2, slope3);
    }

    private static void AppendVerticalRampQuadFace(List<IconFace3D> faces, BlockFace sampledFace, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        if (!TryResolveTextureSlot(sampledFace, out IconTextureSlot textureSlot, out float shade))
            return;

        AddFace(faces, textureSlot, shade,
            MakeVertex(p0, ResolveFaceUv(sampledFace, p0)),
            MakeVertex(p1, ResolveFaceUv(sampledFace, p1)),
            MakeVertex(p2, ResolveFaceUv(sampledFace, p2)),
            MakeVertex(p3, ResolveFaceUv(sampledFace, p3)));
    }

    private static void AppendFenceFaces(List<IconFace3D> faces)
    {
        ShapeBox centerPost = FenceShapeUtility.GetCenterPostVisualBox();
        AddVisibleBoxFaces(faces, centerPost.min, centerPost.max);

        AppendFenceRailFaces(faces, FenceShapeUtility.ConnectWest);
        AppendFenceRailFaces(faces, FenceShapeUtility.ConnectEast);
    }

    private static void AppendFenceRailFaces(List<IconFace3D> faces, byte directionFlag)
    {
        ShapeBox lowerRail = FenceShapeUtility.GetRailVisualBox(directionFlag, false);
        ShapeBox upperRail = FenceShapeUtility.GetRailVisualBox(directionFlag, true);
        AddVisibleBoxFaces(faces, lowerRail.min, lowerRail.max);
        AddVisibleBoxFaces(faces, upperRail.min, upperRail.max);
    }

    private static void AddVisibleBoxFaces(List<IconFace3D> faces, Vector3 min, Vector3 max)
    {
        Vector3 top0 = new Vector3(min.x, max.y, min.z);
        Vector3 top1 = new Vector3(max.x, max.y, min.z);
        Vector3 top2 = new Vector3(max.x, max.y, max.z);
        Vector3 top3 = new Vector3(min.x, max.y, max.z);
        AddFace(faces, IconTextureSlot.Top, TopShade,
            MakeVertex(top0, new Vector2(0f, 0f)),
            MakeVertex(top1, new Vector2(1f, 0f)),
            MakeVertex(top2, new Vector2(1f, 1f)),
            MakeVertex(top3, new Vector2(0f, 1f)));

        Vector3 front0 = new Vector3(min.x, max.y, max.z);
        Vector3 front1 = new Vector3(max.x, max.y, max.z);
        Vector3 front2 = new Vector3(max.x, min.y, max.z);
        Vector3 front3 = new Vector3(min.x, min.y, max.z);
        AddFace(faces, IconTextureSlot.Front, LeftShade,
            MakeVertex(front0, new Vector2(0f, 1f)),
            MakeVertex(front1, new Vector2(1f, 1f)),
            MakeVertex(front2, new Vector2(1f, 0f)),
            MakeVertex(front3, new Vector2(0f, 0f)));

        Vector3 right0 = new Vector3(max.x, max.y, min.z);
        Vector3 right1 = new Vector3(max.x, max.y, max.z);
        Vector3 right2 = new Vector3(max.x, min.y, max.z);
        Vector3 right3 = new Vector3(max.x, min.y, min.z);
        AddFace(faces, IconTextureSlot.Right, RightShade,
            MakeVertex(right0, new Vector2(0f, 1f)),
            MakeVertex(right1, new Vector2(1f, 1f)),
            MakeVertex(right2, new Vector2(1f, 0f)),
            MakeVertex(right3, new Vector2(0f, 0f)));
    }

    private static void AddVisibleBoxFaces(
        List<IconFace3D> faces,
        Vector3 min,
        Vector3 max,
        BlockModelCuboid cuboid,
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasCoordinatesStartTopLeft)
    {
        Vector3 top0 = new Vector3(min.x, max.y, min.z);
        Vector3 top1 = new Vector3(max.x, max.y, min.z);
        Vector3 top2 = new Vector3(max.x, max.y, max.z);
        Vector3 top3 = new Vector3(min.x, max.y, max.z);
        AddVisibleBoxFace(
            faces,
            cuboid,
            mapping,
            atlasTexture,
            atlasTiles,
            atlasCoordinatesStartTopLeft,
            BlockFace.Top,
            IconTextureSlot.Top,
            TopShade,
            MakeVertex(top0, new Vector2(0f, 0f)),
            MakeVertex(top1, new Vector2(1f, 0f)),
            MakeVertex(top2, new Vector2(1f, 1f)),
            MakeVertex(top3, new Vector2(0f, 1f)));

        Vector3 front0 = new Vector3(min.x, max.y, max.z);
        Vector3 front1 = new Vector3(max.x, max.y, max.z);
        Vector3 front2 = new Vector3(max.x, min.y, max.z);
        Vector3 front3 = new Vector3(min.x, min.y, max.z);
        AddVisibleBoxFace(
            faces,
            cuboid,
            mapping,
            atlasTexture,
            atlasTiles,
            atlasCoordinatesStartTopLeft,
            BlockFace.Front,
            IconTextureSlot.Front,
            LeftShade,
            MakeVertex(front0, new Vector2(0f, 1f)),
            MakeVertex(front1, new Vector2(1f, 1f)),
            MakeVertex(front2, new Vector2(1f, 0f)),
            MakeVertex(front3, new Vector2(0f, 0f)));

        Vector3 right0 = new Vector3(max.x, max.y, min.z);
        Vector3 right1 = new Vector3(max.x, max.y, max.z);
        Vector3 right2 = new Vector3(max.x, min.y, max.z);
        Vector3 right3 = new Vector3(max.x, min.y, min.z);
        AddVisibleBoxFace(
            faces,
            cuboid,
            mapping,
            atlasTexture,
            atlasTiles,
            atlasCoordinatesStartTopLeft,
            BlockFace.Right,
            IconTextureSlot.Right,
            RightShade,
            MakeVertex(right0, new Vector2(0f, 1f)),
            MakeVertex(right1, new Vector2(1f, 1f)),
            MakeVertex(right2, new Vector2(1f, 0f)),
            MakeVertex(right3, new Vector2(0f, 0f)));
    }

    private static void AddVisibleBoxFace(
        List<IconFace3D> faces,
        BlockModelCuboid cuboid,
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasCoordinatesStartTopLeft,
        BlockFace face,
        IconTextureSlot textureSlot,
        float shade,
        params IconVertex3D[] vertices)
    {
        if (!cuboid.HasFace(face))
            return;

        if (TryExtractFacePixels(
                atlasTexture,
                atlasTiles,
                cuboid,
                mapping,
                face,
                atlasCoordinatesStartTopLeft,
                out Color32[] facePixels,
                out int faceWidth,
                out int faceHeight))
        {
            AddFace(faces, textureSlot, shade, facePixels, faceWidth, faceHeight, vertices);
            return;
        }

        AddFace(faces, textureSlot, shade, vertices);
    }

    private static void AddFace(List<IconFace3D> faces, IconTextureSlot textureSlot, float shade, params IconVertex3D[] vertices)
    {
        if (faces == null || vertices == null || vertices.Length < 3)
            return;

        faces.Add(new IconFace3D(textureSlot, shade, vertices));
    }

    private static void AddFace(
        List<IconFace3D> faces,
        IconTextureSlot textureSlot,
        float shade,
        Color32[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        params IconVertex3D[] vertices)
    {
        if (faces == null || vertices == null || vertices.Length < 3)
            return;

        faces.Add(new IconFace3D(textureSlot, shade, vertices, sourcePixels, sourceWidth, sourceHeight));
    }

    private static IconVertex3D MakeVertex(Vector3 position, Vector2 uv)
    {
        return new IconVertex3D(position, uv);
    }

    private static bool TryResolveFaceTexture(
        IconTextureSlot slot,
        Color32[] topPixels,
        int topWidth,
        int topHeight,
        Color32[] frontPixels,
        int frontWidth,
        int frontHeight,
        Color32[] rightPixels,
        int rightWidth,
        int rightHeight,
        out Color32[] sourcePixels,
        out int sourceWidth,
        out int sourceHeight)
    {
        switch (slot)
        {
            case IconTextureSlot.Top:
                sourcePixels = topPixels;
                sourceWidth = topWidth;
                sourceHeight = topHeight;
                return sourcePixels != null;

            case IconTextureSlot.Front:
                sourcePixels = frontPixels;
                sourceWidth = frontWidth;
                sourceHeight = frontHeight;
                return sourcePixels != null;

            default:
                sourcePixels = rightPixels;
                sourceWidth = rightWidth;
                sourceHeight = rightHeight;
                return sourcePixels != null;
        }
    }

    private static bool TryResolveTextureSlot(BlockFace face, out IconTextureSlot textureSlot, out float shade)
    {
        switch (face)
        {
            case BlockFace.Top:
                textureSlot = IconTextureSlot.Top;
                shade = TopShade;
                return true;

            case BlockFace.Front:
            case BlockFace.Left:
                textureSlot = IconTextureSlot.Front;
                shade = LeftShade;
                return true;

            case BlockFace.Right:
            case BlockFace.Back:
                textureSlot = IconTextureSlot.Right;
                shade = RightShade;
                return true;

            default:
                textureSlot = IconTextureSlot.Front;
                shade = LeftShade;
                return false;
        }
    }

    private static Vector2 ResolveFaceUv(BlockFace face, Vector3 position)
    {
        switch (face)
        {
            case BlockFace.Top:
                return new Vector2(Mathf.Clamp01(position.x), Mathf.Clamp01(position.z));

            case BlockFace.Front:
                return new Vector2(Mathf.Clamp01(position.x), Mathf.Clamp01(position.y));

            case BlockFace.Back:
                return new Vector2(1f - Mathf.Clamp01(position.x), Mathf.Clamp01(position.y));

            case BlockFace.Right:
                return new Vector2(Mathf.Clamp01(position.z), Mathf.Clamp01(position.y));

            case BlockFace.Left:
                return new Vector2(1f - Mathf.Clamp01(position.z), Mathf.Clamp01(position.y));

            default:
                return new Vector2(Mathf.Clamp01(position.x), Mathf.Clamp01(position.y));
        }
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

    private static void DrawFacePolygon(
        Color32[] destination,
        int destinationSize,
        ProjectedFace face)
    {
        if (face.vertices == null || face.vertices.Length < 3)
            return;

        for (int vertexIndex = 1; vertexIndex < face.vertices.Length - 1; vertexIndex++)
        {
            DrawFaceTriangle(
                destination,
                destinationSize,
                face.sourcePixels,
                face.sourceWidth,
                face.sourceHeight,
                face.vertices[0],
                face.vertices[vertexIndex],
                face.vertices[vertexIndex + 1],
                face.shade);
        }
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

    private static void InvalidateCacheIfAtlasChanged()
    {
        if (!TryResolveCacheContext(
            out int blockDataInstanceId,
            out int atlasTextureInstanceId,
            out Vector2Int atlasTiles,
            out Vector2Int configuredTileSize,
            out bool atlasCoordinatesStartTopLeft))
            return;

        if (cachedBlockDataInstanceId == blockDataInstanceId &&
            cachedAtlasTextureInstanceId == atlasTextureInstanceId &&
            cachedAtlasTiles == atlasTiles &&
            cachedConfiguredTileSize == configuredTileSize &&
            cachedAtlasCoordinatesStartTopLeft == atlasCoordinatesStartTopLeft)
        {
            return;
        }

        ClearCache();
        cachedBlockDataInstanceId = blockDataInstanceId;
        cachedAtlasTextureInstanceId = atlasTextureInstanceId;
        cachedAtlasTiles = atlasTiles;
        cachedConfiguredTileSize = configuredTileSize;
        cachedAtlasCoordinatesStartTopLeft = atlasCoordinatesStartTopLeft;
    }

    private static bool TryResolveCacheContext(
        out int blockDataInstanceId,
        out int atlasTextureInstanceId,
        out Vector2Int atlasTiles,
        out Vector2Int configuredTileSize,
        out bool atlasCoordinatesStartTopLeft)
    {
        blockDataInstanceId = 0;
        atlasTextureInstanceId = 0;
        atlasTiles = Vector2Int.zero;
        configuredTileSize = Vector2Int.zero;
        atlasCoordinatesStartTopLeft = true;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        blockDataInstanceId = world.blockData.GetInstanceID();
        atlasCoordinatesStartTopLeft = world.blockData.atlasCoordinatesStartTopLeft;
        configuredTileSize = new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(world.blockData.atlasSize.x)),
            Mathf.Max(1, Mathf.RoundToInt(world.blockData.atlasSize.y))
        );
        atlasTiles = new Vector2Int(
            Mathf.Max(1, world.atlasTilesX > 0 ? world.atlasTilesX : configuredTileSize.x),
            Mathf.Max(1, world.atlasTilesY > 0 ? world.atlasTilesY : configuredTileSize.y)
        );

        if (world.blockItemIconAtlasTexture != null)
        {
            atlasTextureInstanceId = world.blockItemIconAtlasTexture.GetInstanceID();
            return true;
        }

        Material[] materials = world.Material;
        if (materials == null || materials.Length == 0)
            return true;

        for (int i = 0; i < materials.Length; i++)
        {
            if (!TryGetAtlasTextureFromMaterial(materials[i], out Texture texture) || texture == null)
                continue;

            atlasTextureInstanceId = texture.GetInstanceID();
            break;
        }

        return true;
    }

    private static void ClearCache()
    {
        foreach (KeyValuePair<BlockType, Sprite> pair in Cache)
        {
            if (pair.Value == null)
                continue;

            Texture texture = pair.Value.texture;
            DestroyTempTexture(pair.Value);
            DestroyTempTexture(texture);
        }

        Cache.Clear();

        foreach (KeyValuePair<int, Texture2D> pair in ReadableAtlasCopies)
            DestroyTempTexture(pair.Value);

        ReadableAtlasCopies.Clear();
    }
}
