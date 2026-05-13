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

            if (TransportTubeUtility.IsTransportTubeBlock(blockType))
            {
                AddTransportTubeShape(
                    origin,
                    mapping,
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01);
                return;
            }

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

            if (blockType == BlockType.conveyorBelt_45deg)
            {
                AddSlopedConveyorMultiCuboidShape(
                    origin,
                    mapping,
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
                return;
            }

            Vector3 transportTubeFilterVerticalOffset = ResolveTransportTubeFilterRenderPlacement(
                ref mapping,
                blockType,
                ref placementAxis,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            Vector3 visualOffset = ResolveSurfaceAlignedVisualOffset(mapping, blockType, placementAxis, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            visualOffset += transportTubeFilterVerticalOffset;

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

        private Vector3 ResolveTransportTubeFilterRenderPlacement(
            ref BlockTextureMapping mapping,
            BlockType blockType,
            ref BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (blockType != BlockType.TransportTubeFilter)
                return Vector3.zero;

            BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            if (axis == BlockPlacementAxis.Y && HasMultiCuboidBlockAt(voxelX, voxelY - 1, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, BlockType.chest))
            {
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Both;
                placementAxis = BlockPlacementAxis.Z;
                return new Vector3(0f, 0f, -0.3125f);
            }

            if (axis == BlockPlacementAxis.YNegative && HasMultiCuboidBlockAt(voxelX, voxelY + 1, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, BlockType.chest))
            {
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Both;
                placementAxis = BlockPlacementAxis.ZNegative;
                return new Vector3(0f, 0f, 0.3125f);
            }

            return Vector3.zero;
        }

        private bool HasMultiCuboidBlockAt(
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            BlockType blockType)
        {
            if (!blockTypes.IsCreated ||
                voxelX < 0 || voxelX >= voxelSizeX ||
                voxelY < 0 || voxelY >= Chunk.SizeY ||
                voxelZ < 0 || voxelZ >= voxelSizeZ)
            {
                return false;
            }

            int idx = voxelX + voxelY * voxelSizeX + voxelZ * voxelPlaneSize;
            return (uint)idx < (uint)blockTypes.Length && (BlockType)blockTypes[idx] == blockType;
        }

        private void AddSlopedConveyorMultiCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis conveyorAxis,
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
            BlockPlacementAxis rampAxis = ResolveSlopedConveyorRampPlacementAxis(
                blockTypes,
                conveyorAxis,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            Vector3 visualOffset = ResolveSurfaceAlignedVisualOffset(
                mapping,
                BlockType.conveyorBelt_45deg,
                conveyorAxis,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            FixedList512Bytes<ShapeBox> shapeBoxes = default;
            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                ShapeBox bounds = GetSlopedConveyorMultiCuboidBounds(cuboid, mapping, conveyorAxis, rampAxis, visualOffset);
                shapeBoxes.Add(bounds);
                AddSlopedConveyorMultiCuboidFaces(
                    origin + visualOffset,
                    mapping,
                    cuboid,
                    conveyorAxis,
                    rampAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    light01,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris,
                    shapeBoxes);
            }

            if (appendedAnyCuboid)
                return;

            ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
            BlockModelCuboid fallbackCuboid = new BlockModelCuboid(fallbackMin, fallbackMax);
            ShapeBox fallbackBounds = GetSlopedConveyorMultiCuboidBounds(fallbackCuboid, mapping, conveyorAxis, rampAxis, visualOffset);
            shapeBoxes.Add(fallbackBounds);
            AddSlopedConveyorMultiCuboidFaces(
                origin + visualOffset,
                mapping,
                fallbackCuboid,
                conveyorAxis,
                rampAxis,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                shapeBoxes);
        }

        private void AddSlopedConveyorMultiCuboidFaces(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            BlockPlacementAxis conveyorAxis,
            BlockPlacementAxis rampAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            bool hasCuboidRotation = IsModelCuboidRotated(cuboid);
            quaternion rotation = hasCuboidRotation ? CreateCuboidRotation(cuboid.eulerRotation) : quaternion.identity;

            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Right, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Left, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Top, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Bottom, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Front, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
            AddSlopedConveyorMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, hasCuboidRotation, conveyorAxis, rampAxis, BlockFace.Back, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris, shapeBoxes);
        }

        private void AddSlopedConveyorMultiCuboidFace(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            ShapeBox box,
            Vector3 center,
            quaternion rotation,
            bool hasCuboidRotation,
            BlockPlacementAxis conveyorAxis,
            BlockPlacementAxis rampAxis,
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
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(box, localFace, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out _);
            Vector2 uv0 = ResolveShapeProjectedUv(localFace, p0);
            Vector2 uv1 = ResolveShapeProjectedUv(localFace, p1);
            Vector2 uv2 = ResolveShapeProjectedUv(localFace, p2);
            Vector2 uv3 = ResolveShapeProjectedUv(localFace, p3);
            NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            p0 = TransformSlopedConveyorCuboidPoint(p0, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis);
            p1 = TransformSlopedConveyorCuboidPoint(p1, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis);
            p2 = TransformSlopedConveyorCuboidPoint(p2, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis);
            p3 = TransformSlopedConveyorCuboidPoint(p3, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis);

            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            BlockFace worldFace = BlockShapeUtility.TransformFaceForPlacement(localFace, mapping, conveyorAxis);
            AddAmbientOccludedShapeFace(
                origin + p0,
                origin + p1,
                origin + p2,
                origin + p3,
                normal,
                worldFace,
                normal,
                (p1 - p0).normalized,
                (p3 - p0).normalized,
                mapping,
                worldFace,
                localFace,
                default,
                true,
                cuboid.GetTileCoord(localFace, mapping),
                cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default,
                mapping.GetTint(localFace),
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
                false,
                shapeBoxes,
                BlockRenderShape.MultiCuboid,
                conveyorAxis,
                RampShapeVariant.Straight);
        }

        private static ShapeBox GetSlopedConveyorMultiCuboidBounds(
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis conveyorAxis,
            BlockPlacementAxis rampAxis,
            Vector3 visualOffset)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            bool hasCuboidRotation = IsModelCuboidRotated(cuboid);
            quaternion rotation = hasCuboidRotation ? CreateCuboidRotation(cuboid.eulerRotation) : quaternion.identity;
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            EncapsulateSlopedConveyorCuboidPoint(box.min.x, box.min.y, box.min.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.max.x, box.min.y, box.min.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.min.x, box.max.y, box.min.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.max.x, box.max.y, box.min.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.min.x, box.min.y, box.max.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.max.x, box.min.y, box.max.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.min.x, box.max.y, box.max.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);
            EncapsulateSlopedConveyorCuboidPoint(box.max.x, box.max.y, box.max.z, center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis, visualOffset, ref min, ref max);

            return new ShapeBox(min, max);
        }

        private static void EncapsulateSlopedConveyorCuboidPoint(
            float x,
            float y,
            float z,
            Vector3 center,
            quaternion rotation,
            bool hasCuboidRotation,
            BlockTextureMapping mapping,
            BlockPlacementAxis conveyorAxis,
            BlockPlacementAxis rampAxis,
            Vector3 visualOffset,
            ref Vector3 min,
            ref Vector3 max)
        {
            Vector3 point = TransformSlopedConveyorCuboidPoint(new Vector3(x, y, z), center, rotation, hasCuboidRotation, mapping, conveyorAxis, rampAxis);
            point += visualOffset;
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static Vector3 TransformSlopedConveyorCuboidPoint(
            Vector3 point,
            Vector3 center,
            quaternion rotation,
            bool hasCuboidRotation,
            BlockTextureMapping mapping,
            BlockPlacementAxis conveyorAxis,
            BlockPlacementAxis rampAxis)
        {
            if (hasCuboidRotation)
                point = RotateCuboidPoint(point, center, rotation);

            point = BlockShapeUtility.TransformPointForPlacement(point, mapping, conveyorAxis);
            return TransformPointForSlopedConveyor(point, rampAxis);
        }

        private static Vector3 TransformPointForSlopedConveyor(Vector3 point, BlockPlacementAxis rampAxis)
        {
            float heightOffset = ResolveSlopedConveyorHeightOffset(point, rampAxis);
            return new Vector3(point.x, point.y + heightOffset, point.z);
        }

        private static float ResolveSlopedConveyorHeightOffset(Vector3 point, BlockPlacementAxis rampAxis)
        {
            switch (BlockPlacementRotationUtility.SanitizeStoredAxis(rampAxis))
            {
                case BlockPlacementAxis.X:
                    return point.x;

                case BlockPlacementAxis.XNegative:
                    return 1f - point.x;

                case BlockPlacementAxis.ZNegative:
                    return 1f - point.z;

                default:
                    return point.z;
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
