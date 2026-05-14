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
        private void AddTransportTubeShape(
            Vector3 origin,
            BlockTextureMapping mapping,
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
            byte connectionMask = TransportTubeUtility.ResolveConnectionMask(
                voxelX,
                voxelY,
                voxelZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
            FixedList512Bytes<ShapeBox> shapeBoxes =
                TransportTubeUtility.BuildVisualBoxes(connectionMask, placementAxis);

            if (TryAddProceduralTransportTubeShapeWithImportedUv(
                    origin,
                    shapeBoxes,
                    connectionMask,
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris))
            {
                return;
            }

            if (TryAddBlockbenchTransportTubeShape(
                    origin,
                    connectionMask,
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris))
            {
                return;
            }

            AddAmbientOccludedShapeBoxes(
                origin,
                mapping,
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
                shapeBoxes);
        }

        private bool TryAddBlockbenchTransportTubeShape(
            Vector3 origin,
            byte connectionMask,
            BlockPlacementAxis fallbackAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            TransportTubeUtility.TubeState state = TransportTubeUtility.ResolveState(connectionMask, fallbackAxis);

            if (state.blockType == BlockType.TransportTube)
            {
                if (state.placementAxis == BlockPlacementAxis.YNegative)
                {
                    return TryAddVerticalStraightTransportTubeShape(
                        origin,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        light01,
                        tris);
                }

                return TryAddImportedTransportTubeModel(
                    origin,
                    BlockType.TransportTube,
                    state.placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris);
            }

            return false;
        }

        private void AddFluidPipeShape(
            Vector3 origin,
            BlockTextureMapping mapping,
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
            byte connectionMask = FluidPipeUtility.ResolveConnectionMask(
                voxelX,
                voxelY,
                voxelZ,
                blockTypes,
                blockPlacementAxes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
            FixedList512Bytes<ShapeBox> shapeBoxes =
                FluidPipeUtility.BuildVisualBoxes(connectionMask, placementAxis);

            if (TryAddProceduralFluidPipeShapeWithImportedUv(
                    origin,
                    shapeBoxes,
                    connectionMask,
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris))
            {
                return;
            }

            if (TryAddBlockbenchFluidPipeShape(
                    origin,
                    connectionMask,
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris))
            {
                return;
            }

            AddAmbientOccludedShapeBoxes(
                origin,
                mapping,
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
                shapeBoxes);
        }

        private bool TryAddBlockbenchFluidPipeShape(
            Vector3 origin,
            byte connectionMask,
            BlockPlacementAxis fallbackAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            FluidPipeUtility.PipeState state = FluidPipeUtility.ResolveState(connectionMask, fallbackAxis);
            int connectionCount = TransportTubeUtility.CountConnections(connectionMask);

            if (state.placementAxis == BlockPlacementAxis.YNegative ||
                TransportTubeUtility.HasVerticalConnection(connectionMask) ||
                connectionCount > 3)
            {
                return false;
            }

            if (state.blockType != BlockType.FluidPipe)
                return false;

            return TryAddImportedTransportTubeModel(
                origin,
                state.blockType,
                state.placementAxis,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                light01,
                tris);
        }

        private bool TryAddImportedTransportTubeModel(
            Vector3 origin,
            BlockType visualBlockType,
            BlockPlacementAxis visualAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            BlockTextureMapping visualMapping = GetLogicalBlockMapping(visualBlockType);
            int boxCount = GetNativeMultiCuboidBoxCount(visualMapping);
            if (boxCount <= 0)
                return false;

            FixedList512Bytes<ShapeBox> shapeBoxes = default;
            FixedList4096Bytes<ShapeFaceRect> faceRects = default;
            bool appendedAnyCuboid = false;

            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(visualMapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                if (IsModelCuboidRotated(cuboid))
                {
                    AddRotatedMultiCuboid(
                        origin,
                        visualMapping,
                        cuboid,
                        visualAxis,
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

                ShapeBox box = BlockShapeUtility.TransformShapeBoxForPlacement(
                    cuboid.ToShapeBox(),
                    visualMapping,
                    visualAxis);
                shapeBoxes.Add(box);
                AppendModelCuboidFaceRects(ref faceRects, box, cuboid, visualMapping, visualAxis);
            }

            if (!appendedAnyCuboid || shapeBoxes.Length == 0)
                return appendedAnyCuboid;

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    visualMapping,
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
                    visualAxis,
                    RampShapeVariant.Straight);
            }

            return true;
        }

        private static bool IsFluidPipePlanarCrossConnectionMask(byte connectionMask)
        {
            return CountFluidPipeOppositeConnectionPairs(connectionMask) == 2;
        }

        private static int CountFluidPipeOppositeConnectionPairs(byte connectionMask)
        {
            int count = 0;
            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectWest, TransportTubeUtility.ConnectEast))
                count++;

            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectSouth, TransportTubeUtility.ConnectNorth))
                count++;

            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectDown, TransportTubeUtility.ConnectUp))
                count++;

            return count;
        }

        private static bool HasFluidPipeConnectionPair(byte connectionMask, byte negativeFlag, byte positiveFlag)
        {
            return TransportTubeUtility.IsConnectionActive(connectionMask, negativeFlag) &&
                   TransportTubeUtility.IsConnectionActive(connectionMask, positiveFlag);
        }

        private bool TryAddProceduralFluidPipeShapeWithImportedUv(
            Vector3 origin,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            byte connectionMask,
            BlockPlacementAxis fallbackAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            if (shapeBoxes.Length <= 0)
                return false;

            BlockTextureMapping visualMapping = GetLogicalBlockMapping(BlockType.FluidPipe);
            BlockPlacementAxis visualAxis = BlockPlacementAxis.Z;

            if (GetNativeMultiCuboidBoxCount(visualMapping) <= 0)
                return false;

            FixedList4096Bytes<ShapeFaceRect> faceRects = default;
            AppendAxisAwareFluidPipeProceduralFaceRects(ref faceRects, shapeBoxes, visualMapping, connectionMask);

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    visualMapping,
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
                    visualAxis,
                    RampShapeVariant.Straight);
            }

            return true;
        }

        private void AppendAxisAwareFluidPipeProceduralFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockTextureMapping visualMapping,
            byte connectionMask)
        {
            if (!TryGetNativeMultiCuboid(visualMapping, 0, out BlockModelCuboid sourceCuboid))
                return;

            for (int i = 0; i < shapeBoxes.Length; i++)
            {
                ShapeBox box = shapeBoxes[i];
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Right);
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Left);
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Top);
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Bottom);
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Front);
                AppendAxisAwareFluidPipeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Back);
            }
        }

        private static void AppendAxisAwareFluidPipeProceduralFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid sourceCuboid,
            BlockTextureMapping visualMapping,
            byte connectionMask,
            BlockFace face)
        {
            ShapeBox sourceBox = sourceCuboid.ToShapeBox();
            int axis = ResolveFluidPipeArmAxis(box);
            if (axis == 0)
                axis = ResolveFluidPipeCenterUvAxis(connectionMask);

            if (IsFluidPipeAxisCapFace(axis, face) &&
                !IsFluidPipeExternalExitFace(box, axis, face))
            {
                return;
            }

            BlockFace textureFace = ResolveAxisAwareFluidPipeTextureFace(axis, face);
            if (!sourceCuboid.HasFace(textureFace))
                return;

            ResolveShapeBoxProjectedUvBounds(
                sourceBox,
                textureFace,
                out float sourceMinU,
                out float sourceMaxU,
                out float sourceMinV,
                out float sourceMaxV);
            ResolveFluidPipeAxisRange(box, axis, out float axisMin, out float axisMax);

            AppendShapeFaceRect(
                ref faceRects,
                box,
                face,
                sourceCuboid.GetTileCoord(textureFace, visualMapping),
                visualMapping.GetTint(textureFace),
                true,
                sourceCuboid.TryGetUvRectData(textureFace, visualMapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default,
                textureFace,
                true,
                new Vector4(sourceMinU, sourceMaxU, sourceMinV, sourceMaxV),
                usesFluidPipeImportedUv: true,
                fluidPipeAxis: axis,
                fluidPipeAxisMin: axisMin,
                fluidPipeAxisMax: axisMax);
        }

        private static void ResolveFluidPipeAxisRange(ShapeBox box, int axis, out float axisMin, out float axisMax)
        {
            switch (axis)
            {
                case 1:
                    axisMin = box.min.x;
                    axisMax = box.max.x;
                    return;

                case 2:
                    axisMin = box.min.y;
                    axisMax = box.max.y;
                    return;

                default:
                    axisMin = box.min.z;
                    axisMax = box.max.z;
                    return;
            }
        }

        private static BlockFace ResolveAxisAwareFluidPipeTextureFace(int axis, BlockFace face)
        {
            switch (axis)
            {
                case 1:
                    return face switch
                    {
                        BlockFace.Right => BlockFace.Front,
                        BlockFace.Left => BlockFace.Back,
                        BlockFace.Front => BlockFace.Right,
                        BlockFace.Back => BlockFace.Left,
                        _ => face
                    };

                case 2:
                    return face switch
                    {
                        BlockFace.Top => BlockFace.Front,
                        BlockFace.Bottom => BlockFace.Back,
                        BlockFace.Front => BlockFace.Top,
                        BlockFace.Back => BlockFace.Bottom,
                        _ => face
                    };

                case 3:
                    return face;

                default:
                    return ResolveFluidPipeSideTextureFace(face);
            }
        }

        private static bool IsFluidPipeAxisCapFace(int axis, BlockFace face)
        {
            switch (axis)
            {
                case 1:
                    return face == BlockFace.Right || face == BlockFace.Left;

                case 2:
                    return face == BlockFace.Top || face == BlockFace.Bottom;

                case 3:
                    return face == BlockFace.Front || face == BlockFace.Back;

                default:
                    return false;
            }
        }

        private static bool IsFluidPipeExternalExitFace(ShapeBox box, int axis, BlockFace face)
        {
            const float epsilon = 0.0001f;
            switch (axis)
            {
                case 1:
                    return (face == BlockFace.Left && box.min.x <= epsilon) ||
                           (face == BlockFace.Right && box.max.x >= 1f - epsilon);

                case 2:
                    return (face == BlockFace.Bottom && box.min.y <= epsilon) ||
                           (face == BlockFace.Top && box.max.y >= 1f - epsilon);

                case 3:
                    return (face == BlockFace.Back && box.min.z <= epsilon) ||
                           (face == BlockFace.Front && box.max.z >= 1f - epsilon);

                default:
                    return false;
            }
        }

        private static int ResolveFluidPipeArmAxis(ShapeBox box)
        {
            Vector3 size = box.max - box.min;
            const float epsilon = 0.0001f;

            if (size.x > size.y + epsilon && size.x > size.z + epsilon)
                return 1;

            if (size.y > size.x + epsilon && size.y > size.z + epsilon)
                return 2;

            if (size.z > size.x + epsilon && size.z > size.y + epsilon)
                return 3;

            return 0;
        }

        private static int ResolveFluidPipeCenterUvAxis(byte connectionMask)
        {
            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectWest, TransportTubeUtility.ConnectEast))
                return 1;

            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectDown, TransportTubeUtility.ConnectUp))
                return 2;

            if (HasFluidPipeConnectionPair(connectionMask, TransportTubeUtility.ConnectSouth, TransportTubeUtility.ConnectNorth))
                return 3;

            if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest) ||
                TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast))
                return 1;

            if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown) ||
                TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectUp))
                return 2;

            return 3;
        }

        private static Vector3 TransformFluidPipeImportedUvPoint(
            Vector3 localPoint,
            int fluidPipeAxis,
            float axisMin,
            float axisMax)
        {
            switch (fluidPipeAxis)
            {
                case 1:
                    return new Vector3(
                        localPoint.z,
                        localPoint.y,
                        localPoint.x);

                case 2:
                    return new Vector3(
                        localPoint.x,
                        localPoint.z,
                        localPoint.y);

                default:
                    return new Vector3(
                        localPoint.x,
                        localPoint.y,
                        localPoint.z);
            }
        }

        private static BlockFace ResolveFluidPipeSideTextureFace(BlockFace face)
        {
            if (face == BlockFace.Front)
                return BlockFace.Right;

            if (face == BlockFace.Back)
                return BlockFace.Left;

            return face;
        }

        private void AppendFluidPipeProceduralFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis)
        {
            int sourceCount = GetNativeMultiCuboidBoxCount(visualMapping);
            if (sourceCount <= 0)
                return;

            FixedList512Bytes<ShapeBox> canonicalBoxes = default;
            FixedList512Bytes<ShapeBox> renderedBoxes = default;
            FixedList512Bytes<int> sourceIndices = default;
            FixedList512Bytes<ShapeBox> sourceUvBoxes = default;
            FixedList512Bytes<int> sourceUvBoxInitialized = default;

            int cappedSourceCount = math.min(sourceCount, 16);
            for (int i = 0; i < cappedSourceCount; i++)
            {
                sourceUvBoxes.Add(default);
                sourceUvBoxInitialized.Add(0);
            }

            for (int i = 0; i < shapeBoxes.Length; i++)
            {
                ShapeBox canonicalBox = InverseTransformTransportTubeBox(shapeBoxes[i], visualMapping, visualAxis);
                if (!TryGetFluidPipeSourceCuboidIndex(visualMapping, canonicalBox, out int sourceIndex))
                    continue;

                canonicalBoxes.Add(canonicalBox);
                renderedBoxes.Add(shapeBoxes[i]);
                sourceIndices.Add(sourceIndex);

                if (sourceUvBoxInitialized[sourceIndex] == 0)
                {
                    sourceUvBoxes[sourceIndex] = canonicalBox;
                    sourceUvBoxInitialized[sourceIndex] = 1;
                    continue;
                }

                ShapeBox sourceUvBox = sourceUvBoxes[sourceIndex];
                sourceUvBoxes[sourceIndex] = new ShapeBox(
                    Vector3.Min(sourceUvBox.min, canonicalBox.min),
                    Vector3.Max(sourceUvBox.max, canonicalBox.max));
            }

            for (int i = 0; i < canonicalBoxes.Length; i++)
            {
                int sourceIndex = sourceIndices[i];
                if (!TryGetNativeMultiCuboid(visualMapping, sourceIndex, out BlockModelCuboid sourceCuboid))
                    continue;

                ShapeBox sourceUvBox = sourceUvBoxes[sourceIndex];
                ShapeBox box = renderedBoxes[i];
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Right);
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Left);
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Top);
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Bottom);
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Front);
                AppendTransportTubeProceduralFaceRect(ref faceRects, box, sourceUvBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Back);
            }
        }

        private bool TryAddProceduralTransportTubeShapeWithImportedUv(
            Vector3 origin,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            byte connectionMask,
            BlockPlacementAxis fallbackAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            if (shapeBoxes.Length <= 0)
                return false;

            BlockTextureMapping visualMapping = GetLogicalBlockMapping(BlockType.TransportTube);
            BlockPlacementAxis visualAxis = BlockPlacementAxis.Z;
            if (GetNativeMultiCuboidBoxCount(visualMapping) <= 0)
                return false;

            FixedList4096Bytes<ShapeFaceRect> faceRects = default;
            AppendAxisAwareTransportTubeProceduralFaceRects(ref faceRects, shapeBoxes, visualMapping, connectionMask);

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    visualMapping,
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
                    visualAxis,
                    RampShapeVariant.Straight);
            }

            return true;
        }

        private void AppendAxisAwareTransportTubeProceduralFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockTextureMapping visualMapping,
            byte connectionMask)
        {
            if (!TryGetNativeMultiCuboid(visualMapping, 0, out BlockModelCuboid sourceCuboid))
                return;

            for (int i = 0; i < shapeBoxes.Length; i++)
            {
                ShapeBox box = shapeBoxes[i];
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Right);
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Left);
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Top);
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Bottom);
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Front);
                AppendAxisAwareTransportTubeProceduralFaceRect(ref faceRects, box, sourceCuboid, visualMapping, connectionMask, BlockFace.Back);
            }
        }

        private static void AppendAxisAwareTransportTubeProceduralFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid sourceCuboid,
            BlockTextureMapping visualMapping,
            byte connectionMask,
            BlockFace face)
        {
            ShapeBox sourceBox = sourceCuboid.ToShapeBox();
            int axis = ResolveTransportTubeVisualAxis(box, connectionMask);
            if (IsFluidPipeAxisCapFace(axis, face) &&
                !IsFluidPipeExternalExitFace(box, axis, face))
            {
                return;
            }

            BlockFace textureFace = ResolveAxisAwareFluidPipeTextureFace(axis, face);
            if (!sourceCuboid.HasFace(textureFace))
                return;

            ResolveShapeBoxProjectedUvBounds(
                sourceBox,
                textureFace,
                out float sourceMinU,
                out float sourceMaxU,
                out float sourceMinV,
                out float sourceMaxV);
            ResolveFluidPipeAxisRange(box, axis, out float axisMin, out float axisMax);

            AppendShapeFaceRect(
                ref faceRects,
                box,
                face,
                sourceCuboid.GetTileCoord(textureFace, visualMapping),
                visualMapping.GetTint(textureFace),
                true,
                sourceCuboid.TryGetUvRectData(textureFace, visualMapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default,
                textureFace,
                true,
                new Vector4(sourceMinU, sourceMaxU, sourceMinV, sourceMaxV),
                usesTransportTubeImportedUv: true,
                fluidPipeAxis: axis,
                fluidPipeAxisMin: axisMin,
                fluidPipeAxisMax: axisMax);
        }

        private static int ResolveTransportTubeVisualAxis(ShapeBox box, byte connectionMask)
        {
            int axis = ResolveFluidPipeArmAxis(box);
            if (axis != 0)
                return axis;

            if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown) ||
                TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectUp))
            {
                return 2;
            }

            return ResolveFluidPipeCenterUvAxis(connectionMask);
        }

        private const float TransportTubeUvCrossMin = 0.3125f;

        private static Vector3 TransformTransportTubeImportedUvPoint(Vector3 localPoint, int transportTubeAxis)
        {
            switch (transportTubeAxis)
            {
                case 1:
                    return new Vector3(
                        localPoint.z,
                        localPoint.y,
                        localPoint.x);

                case 2:
                    return new Vector3(
                        localPoint.x,
                        localPoint.z - TransportTubeUvCrossMin,
                        localPoint.y);

                default:
                    return localPoint;
            }
        }

        private void AppendTransportTubeProceduralFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis)
        {
            ShapeBox canonicalBox = InverseTransformTransportTubeBox(box, visualMapping, visualAxis);
            if (!TryGetTransportTubeSourceCuboid(visualMapping, canonicalBox, out BlockModelCuboid sourceCuboid))
                return;

            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Right);
            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Left);
            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Top);
            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Bottom);
            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Front);
            AppendTransportTubeProceduralFaceRect(ref faceRects, box, canonicalBox, sourceCuboid, visualMapping, visualAxis, BlockFace.Back);
        }

        private static void AppendTransportTubeProceduralFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            ShapeBox canonicalBox,
            BlockModelCuboid sourceCuboid,
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis,
            BlockFace face)
        {
            BlockFace localFace = ResolveTransportTubeLocalFace(visualMapping, visualAxis, face);
            if (!sourceCuboid.HasFace(localFace))
                return;

            ResolveShapeBoxProjectedUvBounds(
                canonicalBox,
                localFace,
                out float sourceMinU,
                out float sourceMaxU,
                out float sourceMinV,
                out float sourceMaxV);

            AppendShapeFaceRect(
                ref faceRects,
                box,
                face,
                sourceCuboid.GetTileCoord(localFace, visualMapping),
                visualMapping.GetTint(localFace),
                true,
                sourceCuboid.TryGetUvRectData(localFace, visualMapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default,
                localFace,
                true,
                new Vector4(sourceMinU, sourceMaxU, sourceMinV, sourceMaxV));
        }

        private bool TryGetTransportTubeSourceCuboid(
            BlockTextureMapping visualMapping,
            ShapeBox canonicalBox,
            out BlockModelCuboid sourceCuboid)
        {
            int count = GetNativeMultiCuboidBoxCount(visualMapping);
            if (count <= 0)
            {
                sourceCuboid = default;
                return false;
            }

            int preferredIndex = 0;
            if (count > 1)
            {
                Vector3 size = canonicalBox.max - canonicalBox.min;
                if (size.x > size.z + 0.0001f)
                    preferredIndex = 1;
            }

            if (TryGetNativeMultiCuboid(visualMapping, preferredIndex, out sourceCuboid))
                return true;

            return TryGetNativeMultiCuboid(visualMapping, 0, out sourceCuboid);
        }

        private bool TryGetFluidPipeSourceCuboid(
            BlockTextureMapping visualMapping,
            ShapeBox canonicalBox,
            out BlockModelCuboid sourceCuboid)
        {
            if (TryGetFluidPipeSourceCuboidIndex(visualMapping, canonicalBox, out int sourceIndex))
                return TryGetNativeMultiCuboid(visualMapping, sourceIndex, out sourceCuboid);

            sourceCuboid = default;
            return false;
        }

        private bool TryGetFluidPipeSourceCuboidIndex(
            BlockTextureMapping visualMapping,
            ShapeBox canonicalBox,
            out int sourceIndex)
        {
            int count = GetNativeMultiCuboidBoxCount(visualMapping);
            if (count <= 0)
            {
                sourceIndex = -1;
                return false;
            }

            int bestIndex = -1;
            float bestOverlap = -1f;
            float bestDistance = float.PositiveInfinity;
            Vector3 canonicalCenter = (canonicalBox.min + canonicalBox.max) * 0.5f;

            for (int i = 0; i < count && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(visualMapping, i, out BlockModelCuboid candidate))
                    continue;

                ShapeBox candidateBox = candidate.ToShapeBox();
                float overlap = ComputeShapeBoxOverlapVolume(canonicalBox, candidateBox);
                Vector3 candidateCenter = (candidateBox.min + candidateBox.max) * 0.5f;
                float distance = (candidateCenter - canonicalCenter).sqrMagnitude;

                if (overlap > bestOverlap + 0.000001f ||
                    (math.abs(overlap - bestOverlap) <= 0.000001f && distance < bestDistance))
                {
                    bestIndex = i;
                    bestOverlap = overlap;
                    bestDistance = distance;
                }
            }

            sourceIndex = bestIndex >= 0 ? bestIndex : 0;
            return true;
        }

        private static float ComputeShapeBoxOverlapVolume(ShapeBox a, ShapeBox b)
        {
            float overlapX = math.max(0f, math.min(a.max.x, b.max.x) - math.max(a.min.x, b.min.x));
            float overlapY = math.max(0f, math.min(a.max.y, b.max.y) - math.max(a.min.y, b.min.y));
            float overlapZ = math.max(0f, math.min(a.max.z, b.max.z) - math.max(a.min.z, b.min.z));
            return overlapX * overlapY * overlapZ;
        }

        private static ShapeBox InverseTransformTransportTubeBox(
            ShapeBox box,
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis)
        {
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            EncapsulateInverseTransportTubeBoxPoint(box.min.x, box.min.y, box.min.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.max.x, box.min.y, box.min.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.min.x, box.max.y, box.min.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.max.x, box.max.y, box.min.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.min.x, box.min.y, box.max.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.max.x, box.min.y, box.max.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.min.x, box.max.y, box.max.z, visualMapping, visualAxis, ref min, ref max);
            EncapsulateInverseTransportTubeBoxPoint(box.max.x, box.max.y, box.max.z, visualMapping, visualAxis, ref min, ref max);

            return new ShapeBox(min, max);
        }

        private static void EncapsulateInverseTransportTubeBoxPoint(
            float x,
            float y,
            float z,
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis,
            ref Vector3 min,
            ref Vector3 max)
        {
            Vector3 point = BlockShapeUtility.InverseTransformPointForPlacement(
                new Vector3(x, y, z),
                visualMapping,
                visualAxis);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static BlockFace ResolveTransportTubeLocalFace(
            BlockTextureMapping visualMapping,
            BlockPlacementAxis visualAxis,
            BlockFace worldFace)
        {
            return BlockShapeUtility.TransformFaceForPlacement(
                worldFace,
                visualMapping,
                GetInverseTransportTubeHorizontalAxis(visualAxis));
        }

        private static BlockPlacementAxis GetInverseTransportTubeHorizontalAxis(BlockPlacementAxis visualAxis)
        {
            switch (BlockPlacementRotationUtility.SanitizeStoredAxis(visualAxis))
            {
                case BlockPlacementAxis.X:
                    return BlockPlacementAxis.XNegative;

                case BlockPlacementAxis.XNegative:
                    return BlockPlacementAxis.X;

                case BlockPlacementAxis.ZNegative:
                    return BlockPlacementAxis.ZNegative;

                default:
                    return visualAxis;
            }
        }

        private bool TryAddVerticalStraightTransportTubeShape(
            Vector3 origin,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            BlockTextureMapping visualMapping = GetLogicalBlockMapping(BlockType.TransportTube);
            int boxCount = GetNativeMultiCuboidBoxCount(visualMapping);
            if (boxCount <= 0)
                return false;

            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(visualMapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                AddVerticalStraightTransportTubeCuboid(
                    origin,
                    visualMapping,
                    cuboid,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris);
            }

            return appendedAnyCuboid;
        }

        private bool TryAddVerticalStraightFluidPipeShape(
            Vector3 origin,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            BlockTextureMapping visualMapping = GetLogicalBlockMapping(BlockType.FluidPipe);
            int boxCount = GetNativeMultiCuboidBoxCount(visualMapping);
            if (boxCount <= 0)
                return false;

            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(visualMapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                AddVerticalStraightTransportTubeCuboid(
                    origin,
                    visualMapping,
                    cuboid,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    light01,
                    tris);
            }

            return appendedAnyCuboid;
        }

        private void AddVerticalStraightTransportTubeCuboid(
            Vector3 origin,
            BlockTextureMapping visualMapping,
            BlockModelCuboid cuboid,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            ShapeBox sourceBox = cuboid.ToShapeBox();
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Right, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Left, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Top, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Bottom, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Front, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
            AddVerticalStraightTransportTubeFace(origin, visualMapping, cuboid, sourceBox, BlockFace.Back, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, light01, tris);
        }

        private void AddVerticalStraightTransportTubeFace(
            Vector3 origin,
            BlockTextureMapping visualMapping,
            BlockModelCuboid cuboid,
            ShapeBox sourceBox,
            BlockFace localFace,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(sourceBox, localFace, out Vector3 sourceP0, out Vector3 sourceP1, out Vector3 sourceP2, out Vector3 sourceP3, out Vector3 sourceNormal);

            Vector2 uv0 = ResolveShapeProjectedUv(localFace, sourceP0);
            Vector2 uv1 = ResolveShapeProjectedUv(localFace, sourceP1);
            Vector2 uv2 = ResolveShapeProjectedUv(localFace, sourceP2);
            Vector2 uv3 = ResolveShapeProjectedUv(localFace, sourceP3);
            ResolveShapeBoxProjectedUvBounds(
                sourceBox,
                localFace,
                out float sourceMinU,
                out float sourceMaxU,
                out float sourceMinV,
                out float sourceMaxV);
            NormalizeProjectedQuadUv(
                ref uv0,
                ref uv1,
                ref uv2,
                ref uv3,
                new Vector4(sourceMinU, sourceMaxU, sourceMinV, sourceMaxV));

            Vector3 p0 = TransformStraightTubePointFromZToY(sourceP0, sourceBox);
            Vector3 p1 = TransformStraightTubePointFromZToY(sourceP1, sourceBox);
            Vector3 p2 = TransformStraightTubePointFromZToY(sourceP2, sourceBox);
            Vector3 p3 = TransformStraightTubePointFromZToY(sourceP3, sourceBox);

            Vector3 desiredNormal = TransformStraightTubeDirectionFromZToY(sourceNormal).normalized;
            Vector3 geometricNormal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            bool invertWinding = Vector3.Dot(geometricNormal, desiredNormal) < 0f;
            BlockFace sampledFace = ResolveTransportTubeFaceFromNormal(desiredNormal);

            AddAmbientOccludedCustomQuad(
                origin,
                visualMapping,
                sampledFace,
                p0,
                p1,
                p2,
                p3,
                uv0,
                uv1,
                uv2,
                uv3,
                desiredNormal,
                desiredNormal,
                Vector3.right,
                Vector3.forward,
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
                BlockPlacementAxis.Y,
                RampShapeVariant.Straight,
                invertWinding,
                hasExplicitAppearance: true,
                explicitTile: cuboid.GetTileCoord(localFace, visualMapping),
                explicitTint: visualMapping.GetTint(localFace),
                explicitUvRectData: cuboid.TryGetUvRectData(localFace, visualMapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default);
        }

        private static Vector3 TransformStraightTubePointFromZToY(Vector3 point, ShapeBox sourceBox)
        {
            float centeredZMin = 0.5f - (sourceBox.max.y - sourceBox.min.y) * 0.5f;
            return new Vector3(
                point.x,
                point.z,
                centeredZMin + point.y - sourceBox.min.y);
        }

        private static Vector3 TransformStraightTubeDirectionFromZToY(Vector3 direction)
        {
            return new Vector3(direction.x, direction.z, direction.y);
        }

        private static BlockFace ResolveTransportTubeFaceFromNormal(Vector3 normal)
        {
            float absX = math.abs(normal.x);
            float absY = math.abs(normal.y);
            float absZ = math.abs(normal.z);

            if (absX >= absY && absX >= absZ)
                return normal.x >= 0f ? BlockFace.Right : BlockFace.Left;

            if (absY >= absZ)
                return normal.y >= 0f ? BlockFace.Top : BlockFace.Bottom;

            return normal.z >= 0f ? BlockFace.Front : BlockFace.Back;
        }
    }
}
