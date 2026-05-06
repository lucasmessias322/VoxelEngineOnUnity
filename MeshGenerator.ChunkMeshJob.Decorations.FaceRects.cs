using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static partial class MeshGenerator
{
    private partial struct ChunkMeshJob
    {
        private struct ShapeFaceRect
        {
            public BlockFace face;
            public BlockFace uvFace;
            public float plane;
            public float minA;
            public float maxA;
            public float minB;
            public float maxB;
            public float sourceMinU;
            public float sourceMaxU;
            public float sourceMinV;
            public float sourceMaxV;
            public int tileX;
            public int tileY;
            public bool tint;
            public bool usesExplicitAppearance;
            public Vector4 explicitUvRectData;
        }


        private static void AppendShapeFaceRects(ref FixedList4096Bytes<ShapeFaceRect> faceRects, ShapeBox box)
        {
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Right);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Left);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Top);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Bottom);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Front);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Back);
        }

        private static void AppendModelCuboidFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Right);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Left);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Top);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Bottom);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Front);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Back);
        }

        private static void AppendModelCuboidFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            BlockFace localFace)
        {
            if (!cuboid.HasFace(localFace))
                return;

            BlockFace worldFace = BlockShapeUtility.TransformFaceForPlacement(localFace, mapping, placementAxis);
            Vector2Int tile = cuboid.GetTileCoord(localFace, mapping);
            bool tint = mapping.GetTint(localFace);
            Vector4 explicitUvRectData = default;
            if (cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData))
                explicitUvRectData = cuboidUvRectData;

            AppendShapeFaceRect(ref faceRects, box, worldFace, tile, tint, true, explicitUvRectData, localFace);
        }

        private static void AppendShapeFaceRect(ref FixedList4096Bytes<ShapeFaceRect> faceRects, ShapeBox box, BlockFace face)
        {
            AppendShapeFaceRect(ref faceRects, box, face, default, false, false, default);
        }

        private static void AppendShapeFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockFace face,
            Vector2Int tile,
            bool tint,
            bool usesExplicitAppearance,
            Vector4 explicitUvRectData,
            BlockFace uvFace = BlockFace.Side)
        {
            if (uvFace == BlockFace.Side)
                uvFace = face;

            switch (face)
            {
                case BlockFace.Right:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Right,
                        uvFace = uvFace,
                        plane = box.max.x,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }

                case BlockFace.Left:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Left,
                        uvFace = uvFace,
                        plane = box.min.x,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }

                case BlockFace.Top:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Top,
                        uvFace = uvFace,
                        plane = box.max.y,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }

                case BlockFace.Bottom:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Bottom,
                        uvFace = uvFace,
                        plane = box.min.y,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }

                case BlockFace.Front:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Front,
                        uvFace = uvFace,
                        plane = box.max.z,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }

                case BlockFace.Back:
                {
                    ShapeFaceRect rect = new ShapeFaceRect
                    {
                        face = BlockFace.Back,
                        uvFace = uvFace,
                        plane = box.min.z,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    };
                    AddShapeFaceRectWithSourceUv(ref faceRects, rect);
                    return;
                }
            }
        }

        private static void AddShapeFaceRectWithSourceUv(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeFaceRect rect)
        {
            ResolveShapeFaceRectProjectedUvBounds(rect, out rect.sourceMinU, out rect.sourceMaxU, out rect.sourceMinV, out rect.sourceMaxV);
            faceRects.Add(rect);
        }

        private static void ResolveShapeFaceRectProjectedUvBounds(
            ShapeFaceRect rect,
            out float minU,
            out float maxU,
            out float minV,
            out float maxV)
        {
            Vector2 uv0;
            Vector2 uv1;
            Vector2 uv2;
            Vector2 uv3;
            switch (rect.face)
            {
                case BlockFace.Right:
                    uv0 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.minA, rect.minB));
                    uv1 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.maxA, rect.minB));
                    uv2 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.maxA, rect.maxB));
                    uv3 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.minA, rect.maxB));
                    break;

                case BlockFace.Left:
                    uv0 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.minA, rect.maxB));
                    uv1 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.maxA, rect.maxB));
                    uv2 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.maxA, rect.minB));
                    uv3 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.plane, rect.minA, rect.minB));
                    break;

                case BlockFace.Top:
                    uv0 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.plane, rect.maxB));
                    uv1 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.plane, rect.maxB));
                    uv2 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.plane, rect.minB));
                    uv3 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.plane, rect.minB));
                    break;

                case BlockFace.Bottom:
                    uv0 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.plane, rect.minB));
                    uv1 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.plane, rect.minB));
                    uv2 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.plane, rect.maxB));
                    uv3 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.plane, rect.maxB));
                    break;

                case BlockFace.Front:
                    uv0 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.minB, rect.plane));
                    uv1 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.maxA, rect.maxB, rect.plane));
                    uv2 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.maxB, rect.plane));
                    uv3 = ResolveShapeProjectedUv(rect.face, new Vector3(rect.minA, rect.minB, rect.plane));
                    break;

                default:
                    uv0 = ResolveShapeProjectedUv(BlockFace.Back, new Vector3(rect.minA, rect.minB, rect.plane));
                    uv1 = ResolveShapeProjectedUv(BlockFace.Back, new Vector3(rect.minA, rect.maxB, rect.plane));
                    uv2 = ResolveShapeProjectedUv(BlockFace.Back, new Vector3(rect.maxA, rect.maxB, rect.plane));
                    uv3 = ResolveShapeProjectedUv(BlockFace.Back, new Vector3(rect.maxA, rect.minB, rect.plane));
                    break;
            }

            minU = math.min(math.min(uv0.x, uv1.x), math.min(uv2.x, uv3.x));
            maxU = math.max(math.max(uv0.x, uv1.x), math.max(uv2.x, uv3.x));
            minV = math.min(math.min(uv0.y, uv1.y), math.min(uv2.y, uv3.y));
            maxV = math.max(math.max(uv0.y, uv1.y), math.max(uv2.y, uv3.y));
        }

        private static void MergeShapeFaceRects(ref FixedList4096Bytes<ShapeFaceRect> faceRects)
        {
            bool mergedAny;
            do
            {
                mergedAny = false;
                for (int i = 0; i < faceRects.Length && !mergedAny; i++)
                {
                    for (int j = i + 1; j < faceRects.Length; j++)
                    {
                        if (!TryMergeShapeFaceRects(faceRects[i], faceRects[j], out ShapeFaceRect merged))
                            continue;

                        faceRects[i] = merged;
                        faceRects.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
            }
            while (mergedAny);
        }

        private static bool TryMergeShapeFaceRects(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect merged)
        {
            const float epsilon = 0.0001f;
            merged = default;

            if (a.face != b.face ||
                math.abs(a.plane - b.plane) > epsilon ||
                !HasSameAppearance(a, b) ||
                !HasSameSourceUvBounds(a, b))
                return false;

            bool sameA = math.abs(a.minA - b.minA) <= epsilon && math.abs(a.maxA - b.maxA) <= epsilon;
            bool sameB = math.abs(a.minB - b.minB) <= epsilon && math.abs(a.maxB - b.maxB) <= epsilon;

            if (sameA && (math.abs(a.maxB - b.minB) <= epsilon || math.abs(b.maxB - a.minB) <= epsilon))
            {
                merged = a;
                merged.minB = math.min(a.minB, b.minB);
                merged.maxB = math.max(a.maxB, b.maxB);
                return true;
            }

            if (sameB && (math.abs(a.maxA - b.minA) <= epsilon || math.abs(b.maxA - a.minA) <= epsilon))
            {
                merged = a;
                merged.minA = math.min(a.minA, b.minA);
                merged.maxA = math.max(a.maxA, b.maxA);
                return true;
            }

            return false;
        }

        private static bool HasSameAppearance(ShapeFaceRect a, ShapeFaceRect b)
        {
            return a.usesExplicitAppearance == b.usesExplicitAppearance &&
                   (!a.usesExplicitAppearance ||
                    (a.tint == b.tint &&
                     a.uvFace == b.uvFace &&
                     a.tileX == b.tileX &&
                     a.tileY == b.tileY &&
                     math.lengthsq(a.explicitUvRectData - b.explicitUvRectData) <= 1e-10f));
        }

        private static bool HasSameSourceUvBounds(ShapeFaceRect a, ShapeFaceRect b)
        {
            const float epsilon = 0.0001f;
            return math.abs(a.sourceMinU - b.sourceMinU) <= epsilon &&
                   math.abs(a.sourceMaxU - b.sourceMaxU) <= epsilon &&
                   math.abs(a.sourceMinV - b.sourceMinV) <= epsilon &&
                   math.abs(a.sourceMaxV - b.sourceMaxV) <= epsilon;
        }

        private static void CullHiddenShapeFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < faceRects.Length && !changed; i++)
                {
                    ShapeFaceRect rect = faceRects[i];
                    for (int boxIndex = 0; boxIndex < shapeBoxes.Length; boxIndex++)
                    {
                        if (!TryGetShapeBoxCoverage(rect, shapeBoxes[boxIndex], out ShapeFaceRect coverage))
                            continue;

                        if (!SubtractShapeFaceRectAt(ref faceRects, i, coverage))
                            continue;

                        changed = true;
                        break;
                    }
                }
            }
            while (changed);

            do
            {
                changed = false;
                for (int i = 0; i < faceRects.Length && !changed; i++)
                {
                    for (int j = i + 1; j < faceRects.Length; j++)
                    {
                        if (!TryGetSameFaceOverlap(faceRects[i], faceRects[j], out ShapeFaceRect overlap))
                            continue;

                        if (!SubtractShapeFaceRectAt(ref faceRects, j, overlap))
                            continue;

                        changed = true;
                        break;
                    }
                }
            }
            while (changed);
        }

        private static bool TryGetShapeBoxCoverage(ShapeFaceRect rect, ShapeBox box, out ShapeFaceRect coverage)
        {
            const float epsilon = 0.0001f;
            coverage = default;

            switch (rect.face)
            {
                case BlockFace.Right:
                    if (box.min.x > rect.plane + epsilon || box.max.x <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Left:
                    if (box.max.x < rect.plane - epsilon || box.min.x >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Top:
                    if (box.min.y > rect.plane + epsilon || box.max.y <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Bottom:
                    if (box.max.y < rect.plane - epsilon || box.min.y >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Front:
                    if (box.min.z > rect.plane + epsilon || box.max.z <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y
                    };
                    break;

                case BlockFace.Back:
                    if (box.max.z < rect.plane - epsilon || box.min.z >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y
                    };
                    break;

                default:
                    return false;
            }

            return TryGetSameFaceOverlap(rect, coverage, out coverage);
        }

        private static bool TryGetSameFaceOverlap(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect overlap)
        {
            const float epsilon = 0.0001f;
            overlap = default;

            if (a.face != b.face || math.abs(a.plane - b.plane) > epsilon)
                return false;

            float minA = math.max(a.minA, b.minA);
            float maxA = math.min(a.maxA, b.maxA);
            float minB = math.max(a.minB, b.minB);
            float maxB = math.min(a.maxB, b.maxB);
            if (maxA <= minA + epsilon || maxB <= minB + epsilon)
                return false;

            overlap = new ShapeFaceRect
            {
                face = a.face,
                plane = a.plane,
                minA = minA,
                maxA = maxA,
                minB = minB,
                maxB = maxB
            };
            return true;
        }

        private static bool SubtractShapeFaceRectAt(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            int index,
            ShapeFaceRect clip)
        {
            if (index < 0 || index >= faceRects.Length)
                return false;

            ShapeFaceRect source = faceRects[index];
            if (!TryGetSameFaceOverlap(source, clip, out ShapeFaceRect overlap))
                return false;

            faceRects.RemoveAt(index);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, source.maxA, source.minB, overlap.minB);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, source.maxA, overlap.maxB, source.maxB);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, overlap.minA, overlap.minB, overlap.maxB);
            AddShapeFaceRectFragment(ref faceRects, source, overlap.maxA, source.maxA, overlap.minB, overlap.maxB);
            return true;
        }

        private static void AddShapeFaceRectFragment(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeFaceRect source,
            float minA,
            float maxA,
            float minB,
            float maxB)
        {
            const float epsilon = 0.0001f;
            if (maxA <= minA + epsilon || maxB <= minB + epsilon)
                return;

            if (faceRects.Length >= faceRects.Capacity)
                return;

            faceRects.Add(new ShapeFaceRect
            {
                face = source.face,
                uvFace = source.uvFace,
                plane = source.plane,
                minA = minA,
                maxA = maxA,
                minB = minB,
                maxB = maxB,
                sourceMinU = source.sourceMinU,
                sourceMaxU = source.sourceMaxU,
                sourceMinV = source.sourceMinV,
                sourceMaxV = source.sourceMaxV,
                tileX = source.tileX,
                tileY = source.tileY,
                tint = source.tint,
                usesExplicitAppearance = source.usesExplicitAppearance,
                explicitUvRectData = source.explicitUvRectData
            });
        }

        private static BlockFace ResolveShapeTextureFace(
            BlockTextureMapping mapping,
            BlockFace worldFace,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis)
        {
            if (currentShape != BlockRenderShape.MultiCuboid)
                return worldFace;

            return BlockPlacementRotationUtility.ResolveFaceForPlacement(mapping, worldFace, currentPlacementAxis);
        }

        private static void ResolveShapeRectAppearance(
            ShapeFaceRect rect,
            BlockTextureMapping mapping,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            out BlockFace textureFace,
            out BlockFace uvFace,
            out Vector2Int tile,
            out bool tint,
            out Vector4 explicitUvRectData)
        {
            if (rect.usesExplicitAppearance)
            {
                textureFace = rect.face;
                uvFace = rect.uvFace;
                tile = new Vector2Int(rect.tileX, rect.tileY);
                tint = rect.tint;
                explicitUvRectData = rect.explicitUvRectData;
                return;
            }

            textureFace = ResolveShapeTextureFace(mapping, rect.face, currentShape, currentPlacementAxis);
            uvFace = rect.face;
            tile = mapping.GetTileCoord(textureFace);
            tint = mapping.GetTint(textureFace);
            explicitUvRectData = default;
        }

    }
}
