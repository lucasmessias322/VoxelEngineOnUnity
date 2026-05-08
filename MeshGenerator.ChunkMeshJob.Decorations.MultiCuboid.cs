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
        private void AddMultiCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            if (IsWallTorch(blockType))
            {
                float resolvedLight01 = math.max(light01, mapping.lightEmission / 15f);
                AddWallTorchMultiCuboidShape(
                    origin,
                    mapping,
                    blockType,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    resolvedLight01,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris);
                return;
            }

            Vector3 visualOffset = ResolveSurfaceAlignedVisualOffset(mapping, blockType, placementAxis, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            FixedList512Bytes<ShapeBox> shapeBoxes = default;
            FixedList4096Bytes<ShapeFaceRect> faceRects = default;

            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                if (IsModelCuboidRotated(cuboid))
                {
                    AddRotatedMultiCuboid(
                        origin + visualOffset,
                        mapping,
                        cuboid,
                        placementAxis,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        light01,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris);
                    continue;
                }

                ShapeBox box = BlockShapeUtility.TransformShapeBoxForPlacement(cuboid.ToShapeBox(), mapping, placementAxis);
                box = OffsetShapeBox(box, visualOffset);
                shapeBoxes.Add(box);
                AppendModelCuboidFaceRects(ref faceRects, box, cuboid, mapping, placementAxis);
            }

            if (!appendedAnyCuboid)
            {
                ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                ShapeBox fallback = OffsetShapeBox(new ShapeBox(fallbackMin, fallbackMax), visualOffset);
                shapeBoxes.Add(fallback);
                AppendShapeFaceRects(ref faceRects, fallback);
            }

            if (shapeBoxes.Length == 0)
                return;

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    mapping,
                    faceRects[i],
                    light01,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris,
                    shapeBoxes,
                    BlockRenderShape.MultiCuboid,
                    placementAxis,
                    RampShapeVariant.Straight);
            }
        }

        private static ShapeBox OffsetShapeBox(ShapeBox box, Vector3 offset)
        {
            if (offset == Vector3.zero)
                return box;

            return new ShapeBox(box.min + offset, box.max + offset);
        }

        private void AddWallTorchMultiCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            origin += ResolveTorchVisualOffset(blockType, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                AddWallTorchMultiCuboidFaces(origin, mapping, blockType, cuboid, light01, invAtlasTilesX, invAtlasTilesY, tris);
            }

            if (appendedAnyCuboid)
                return;

            ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
            AddWallTorchMultiCuboidFaces(
                origin,
                mapping,
                blockType,
                new BlockModelCuboid(fallbackMin, fallbackMax),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddWallTorchMultiCuboidFaces(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockModelCuboid cuboid,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            bool hasCuboidRotation = IsModelCuboidRotated(cuboid);
            quaternion rotation = hasCuboidRotation
                ? CreateCuboidRotation(cuboid.eulerRotation)
                : quaternion.identity;

            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Right, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Left, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Top, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Bottom, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Front, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Back, light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddWallTorchMultiCuboidFace(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockModelCuboid cuboid,
            ShapeBox box,
            Vector3 center,
            quaternion rotation,
            bool hasCuboidRotation,
            BlockFace localFace,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(box, localFace, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out _);
            if (hasCuboidRotation)
            {
                p0 = RotateCuboidPoint(p0, center, rotation);
                p1 = RotateCuboidPoint(p1, center, rotation);
                p2 = RotateCuboidPoint(p2, center, rotation);
                p3 = RotateCuboidPoint(p3, center, rotation);
            }

            Vector3 t0 = TransformTorchVoxelPoint(blockType, p0);
            Vector3 t1 = TransformTorchVoxelPoint(blockType, p1);
            Vector3 t2 = TransformTorchVoxelPoint(blockType, p2);
            Vector3 t3 = TransformTorchVoxelPoint(blockType, p3);
            Vector3 transformedNormal = Vector3.Normalize(Vector3.Cross(t1 - t0, t2 - t0));
            if (transformedNormal.sqrMagnitude < 0.0001f)
                transformedNormal = Vector3.up;

            Vector2 uv0 = ResolveShapeProjectedUv(localFace, p0);
            Vector2 uv1 = ResolveShapeProjectedUv(localFace, p1);
            Vector2 uv2 = ResolveShapeProjectedUv(localFace, p2);
            Vector2 uv3 = ResolveShapeProjectedUv(localFace, p3);
            NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            Vector2Int tile = cuboid.GetTileCoord(localFace, mapping);
            bool tint = mapping.GetTint(localFace);
            Vector4 explicitUvRectData = cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData)
                ? cuboidUvRectData
                : default;

            AddStaticLitCustomQuad(
                origin + t0,
                origin + t1,
                origin + t2,
                origin + t3,
                uv0,
                uv1,
                uv2,
                uv3,
                transformedNormal,
                tile,
                tint,
                explicitUvRectData,
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddRotatedMultiCuboid(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            quaternion rotation = CreateCuboidRotation(cuboid.eulerRotation);

            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Right, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Left, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Top, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Bottom, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Front, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Back, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddRotatedMultiCuboidFace(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            ShapeBox box,
            Vector3 center,
            quaternion rotation,
            BlockPlacementAxis placementAxis,
            BlockFace localFace,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(box, localFace, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out Vector3 normal);
            Vector2 uv0 = ResolveShapeProjectedUv(localFace, p0);
            Vector2 uv1 = ResolveShapeProjectedUv(localFace, p1);
            Vector2 uv2 = ResolveShapeProjectedUv(localFace, p2);
            Vector2 uv3 = ResolveShapeProjectedUv(localFace, p3);
            NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            p0 = RotateCuboidPoint(p0, center, rotation);
            p1 = RotateCuboidPoint(p1, center, rotation);
            p2 = RotateCuboidPoint(p2, center, rotation);
            p3 = RotateCuboidPoint(p3, center, rotation);
            normal = RotateCuboidDirection(normal, rotation).normalized;

            p0 = BlockShapeUtility.TransformPointForPlacement(p0, mapping, placementAxis);
            p1 = BlockShapeUtility.TransformPointForPlacement(p1, mapping, placementAxis);
            p2 = BlockShapeUtility.TransformPointForPlacement(p2, mapping, placementAxis);
            p3 = BlockShapeUtility.TransformPointForPlacement(p3, mapping, placementAxis);
            normal = BlockShapeUtility.TransformDirectionForPlacement(normal, mapping, placementAxis).normalized;

            BlockFace worldFace = BlockShapeUtility.TransformFaceForPlacement(localFace, mapping, placementAxis);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                worldFace,
                p0,
                p1,
                p2,
                p3,
                uv0,
                uv1,
                uv2,
                uv3,
                normal,
                normal,
                (p1 - p0).normalized,
                (p3 - p0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.MultiCuboid,
                placementAxis,
                RampShapeVariant.Straight,
                false,
                hasExplicitAppearance: true,
                explicitTile: cuboid.GetTileCoord(localFace, mapping),
                explicitTint: mapping.GetTint(localFace),
                explicitUvRectData: cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default);
        }

        private int GetNativeMultiCuboidBoxCount(BlockTextureMapping mapping)
        {
            if (!blockModelCuboids.IsCreated || blockModelCuboids.Length == 0 || mapping.multiCuboidCount <= 0)
                return 0;

            if (mapping.multiCuboidStartIndex < 0 || mapping.multiCuboidStartIndex >= blockModelCuboids.Length)
                return 0;

            return math.min(mapping.multiCuboidCount, blockModelCuboids.Length - mapping.multiCuboidStartIndex);
        }

        private bool TryGetNativeMultiCuboid(BlockTextureMapping mapping, int localIndex, out BlockModelCuboid cuboid)
        {
            cuboid = default;
            int count = GetNativeMultiCuboidBoxCount(mapping);
            if (localIndex < 0 || localIndex >= count)
                return false;

            cuboid = blockModelCuboids[mapping.multiCuboidStartIndex + localIndex];
            return true;
        }

        private static bool IsModelCuboidRotated(BlockModelCuboid cuboid)
        {
            const float epsilon = 0.0001f;
            return math.abs(cuboid.eulerRotation.x) > epsilon ||
                   math.abs(cuboid.eulerRotation.y) > epsilon ||
                   math.abs(cuboid.eulerRotation.z) > epsilon;
        }

        private static quaternion CreateCuboidRotation(Vector3 eulerRotation)
        {
            return quaternion.EulerZXY(math.radians(new float3(eulerRotation.x, eulerRotation.y, eulerRotation.z)));
        }

        private static Vector3 RotateCuboidPoint(Vector3 point, Vector3 center, quaternion rotation)
        {
            float3 offset = new float3(point.x - center.x, point.y - center.y, point.z - center.z);
            float3 rotated = math.mul(rotation, offset);
            return new Vector3(center.x + rotated.x, center.y + rotated.y, center.z + rotated.z);
        }

        private static Vector3 RotateCuboidDirection(Vector3 direction, quaternion rotation)
        {
            float3 rotated = math.mul(rotation, new float3(direction.x, direction.y, direction.z));
            return new Vector3(rotated.x, rotated.y, rotated.z);
        }

        private static Vector3 TransformTorchVoxelPoint(BlockType blockType, Vector3 voxelPoint)
        {
            return TorchPlacementUtility.TransformVoxelPoint(blockType, voxelPoint);
        }

        private static ShapeBox GetRotatedMultiCuboidBounds(
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            quaternion rotation = CreateCuboidRotation(cuboid.eulerRotation);
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);

            return new ShapeBox(min, max);
        }

        private static void EncapsulateRotatedMultiCuboidPoint(
            float x,
            float y,
            float z,
            Vector3 center,
            quaternion rotation,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            ref Vector3 min,
            ref Vector3 max)
        {
            Vector3 point = RotateCuboidPoint(new Vector3(x, y, z), center, rotation);
            point = BlockShapeUtility.TransformPointForPlacement(point, mapping, placementAxis);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static void ResolveCuboidFace(
            ShapeBox box,
            BlockFace face,
            out Vector3 p0,
            out Vector3 p1,
            out Vector3 p2,
            out Vector3 p3,
            out Vector3 normal)
        {
            Vector3 min = box.min;
            Vector3 max = box.max;
            switch (face)
            {
                case BlockFace.Right:
                    p0 = new Vector3(max.x, min.y, min.z);
                    p1 = new Vector3(max.x, max.y, min.z);
                    p2 = new Vector3(max.x, max.y, max.z);
                    p3 = new Vector3(max.x, min.y, max.z);
                    normal = Vector3.right;
                    return;

                case BlockFace.Left:
                    p0 = new Vector3(min.x, min.y, max.z);
                    p1 = new Vector3(min.x, max.y, max.z);
                    p2 = new Vector3(min.x, max.y, min.z);
                    p3 = new Vector3(min.x, min.y, min.z);
                    normal = Vector3.left;
                    return;

                case BlockFace.Top:
                    p0 = new Vector3(min.x, max.y, max.z);
                    p1 = new Vector3(max.x, max.y, max.z);
                    p2 = new Vector3(max.x, max.y, min.z);
                    p3 = new Vector3(min.x, max.y, min.z);
                    normal = Vector3.up;
                    return;

                case BlockFace.Bottom:
                    p0 = new Vector3(min.x, min.y, min.z);
                    p1 = new Vector3(max.x, min.y, min.z);
                    p2 = new Vector3(max.x, min.y, max.z);
                    p3 = new Vector3(min.x, min.y, max.z);
                    normal = Vector3.down;
                    return;

                case BlockFace.Front:
                    p0 = new Vector3(max.x, min.y, max.z);
                    p1 = new Vector3(max.x, max.y, max.z);
                    p2 = new Vector3(min.x, max.y, max.z);
                    p3 = new Vector3(min.x, min.y, max.z);
                    normal = Vector3.forward;
                    return;

                default:
                    p0 = new Vector3(min.x, min.y, min.z);
                    p1 = new Vector3(min.x, max.y, min.z);
                    p2 = new Vector3(max.x, max.y, min.z);
                    p3 = new Vector3(max.x, min.y, min.z);
                    normal = Vector3.back;
                    return;
            }
        }

        private FixedList512Bytes<ShapeBox> BuildNativeMultiCuboidShapeBoxes(
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            FixedList512Bytes<ShapeBox> boxes = default;
            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                ShapeBox box = IsModelCuboidRotated(cuboid)
                    ? GetRotatedMultiCuboidBounds(cuboid, mapping, placementAxis)
                    : BlockShapeUtility.TransformShapeBoxForPlacement(cuboid.ToShapeBox(), mapping, placementAxis);
                boxes.Add(box);
            }

            if (boxes.Length == 0)
            {
                ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                boxes.Add(new ShapeBox(fallbackMin, fallbackMax));
            }

            return boxes;
        }

    }
}
