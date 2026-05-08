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
        private void AddStairShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            StairShapeVariant variant = ResolveStairShapeVariant(rawPlacementData, voxelX, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            FixedList512Bytes<ShapeBox> shapeBoxes = BuildStairVisualBoxes(rawPlacementData, variant);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
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

        private void AddRampShape(
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
            bool rampTopHalf = RampShapeUtility.IsTopHalf(placementAxis);
            RampShapeVariant rampVariant = mapping.blockType == BlockType.conveyorBelt_45deg
                ? RampShapeVariant.Straight
                : ResolveRampShapeVariant(
                    placementAxis,
                    voxelX,
                    voxelY,
                    voxelZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            BlockFace flatFace = rampTopHalf ? BlockFace.Top : BlockFace.Bottom;
            Vector3 flatNormal = rampTopHalf ? Vector3.up : Vector3.down;
            RampShapeUtility.ResolveBottomQuad(placementAxis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2, out Vector3 bottom3);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                flatFace,
                bottom0,
                bottom1,
                bottom2,
                bottom3,
                ResolveShapeProjectedUv(flatFace, bottom0),
                ResolveShapeProjectedUv(flatFace, bottom1),
                ResolveShapeProjectedUv(flatFace, bottom2),
                ResolveShapeProjectedUv(flatFace, bottom3),
                flatNormal,
                flatNormal,
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
                BlockRenderShape.Ramp,
                placementAxis,
                rampVariant,
                false);

            BlockFace slopeFace = rampTopHalf ? BlockFace.Bottom : BlockFace.Top;
            RampShapeUtility.ResolveTopTriangles(placementAxis, rampVariant, out Vector3 top0a, out Vector3 top0b, out Vector3 top0c, out Vector3 top1a, out Vector3 top1b, out Vector3 top1c);
            Vector3 topNormal0 = Vector3.Normalize(Vector3.Cross(top0b - top0a, top0c - top0a));
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                slopeFace,
                top0a,
                top0b,
                top0c,
                ResolveShapeProjectedUv(slopeFace, top0a),
                ResolveShapeProjectedUv(slopeFace, top0b),
                ResolveShapeProjectedUv(slopeFace, top0c),
                topNormal0,
                topNormal0,
                (top0b - top0a).normalized,
                (top0c - top0a).normalized,
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
                BlockRenderShape.Ramp,
                placementAxis,
                rampVariant,
                true);

            Vector3 topNormal1 = Vector3.Normalize(Vector3.Cross(top1b - top1a, top1c - top1a));
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                slopeFace,
                top1a,
                top1b,
                top1c,
                ResolveShapeProjectedUv(slopeFace, top1a),
                ResolveShapeProjectedUv(slopeFace, top1b),
                ResolveShapeProjectedUv(slopeFace, top1c),
                topNormal1,
                topNormal1,
                (top1b - top1a).normalized,
                (top1c - top1a).normalized,
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
                BlockRenderShape.Ramp,
                placementAxis,
                rampVariant,
                true);

            AppendRampEdgeSurface(origin, mapping, placementAxis, rampVariant, RampEdge.Left, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, placementAxis, rampVariant, RampEdge.Right, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, placementAxis, rampVariant, RampEdge.Front, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, placementAxis, rampVariant, RampEdge.Back, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AppendRampEdgeSurface(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis rampState,
            RampShapeVariant rampVariant,
            RampEdge edge,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!RampShapeUtility.ResolveEdgeSurface(rampState, rampVariant, edge, out int vertexCount, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out BlockFace sampledFace))
                return;

            Vector3 normal = ResolveShapeFaceNormal(sampledFace);
            if (vertexCount == 4)
            {
                AddAmbientOccludedCustomQuad(
                    origin,
                    mapping,
                    sampledFace,
                    p0,
                    p1,
                    p2,
                    p3,
                    ResolveShapeProjectedUv(sampledFace, p0),
                    ResolveShapeProjectedUv(sampledFace, p1),
                    ResolveShapeProjectedUv(sampledFace, p2),
                    ResolveShapeProjectedUv(sampledFace, p3),
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
                    BlockRenderShape.Ramp,
                    rampState,
                    rampVariant,
                    false);
                return;
            }

            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                sampledFace,
                p0,
                p1,
                p2,
                ResolveShapeProjectedUv(sampledFace, p0),
                ResolveShapeProjectedUv(sampledFace, p1),
                ResolveShapeProjectedUv(sampledFace, p2),
                normal,
                normal,
                (p1 - p0).normalized,
                (p2 - p0).normalized,
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
                BlockRenderShape.Ramp,
                rampState,
                rampVariant);
        }

        private RampShapeVariant ResolveRampShapeVariant(
            BlockPlacementAxis placementState,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!RampShapeUtility.TryDecode(placementState, out StairFacing currentFacing, out bool currentTopHalf))
                return RampShapeVariant.Straight;

            Vector3Int frontOffset = StairPlacementUtility.ToOffset(currentFacing);
            Vector3Int backOffset = StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

            bool hasFrontNeighbor = TryGetNeighborRampState(
                voxelX + frontOffset.x,
                voxelY + frontOffset.y,
                voxelZ + frontOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing frontFacing,
                out bool frontTopHalf);

            bool hasBackNeighbor = TryGetNeighborRampState(
                voxelX + backOffset.x,
                voxelY + backOffset.y,
                voxelZ + backOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing backFacing,
                out bool backTopHalf);

            return RampShapeUtility.ResolveShapeVariant(
                currentFacing,
                currentTopHalf,
                hasFrontNeighbor,
                frontFacing,
                frontTopHalf,
                hasBackNeighbor,
                backFacing,
                backTopHalf);
        }

        private bool TryGetNeighborRampState(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out StairFacing facing,
            out bool topHalf)
        {
            facing = StairFacing.North;
            topHalf = false;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            BlockType neighborType = (BlockType)blockTypes[idx];
            int mapIndex = (int)neighborType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            if (BlockShapeUtility.GetEffectiveRenderShape(blockMappings[mapIndex]) != BlockRenderShape.Ramp)
                return false;

            return RampShapeUtility.TryDecode(GetBlockPlacementAxisValue(idx), out facing, out topHalf);
        }

        private void AddVerticalRampShape(
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
            BlockPlacementAxis axis = VerticalRampShapeUtility.SanitizeAxis(placementAxis);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            VerticalRampShapeUtility.ResolveBottomTriangle(axis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2);
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Bottom,
                bottom0,
                bottom1,
                bottom2,
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom0),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom1),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom2),
                Vector3.down,
                Vector3.down,
                (bottom1 - bottom0).normalized,
                (bottom2 - bottom0).normalized,
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
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight);

            VerticalRampShapeUtility.ResolveTopTriangle(axis, out Vector3 top0, out Vector3 top1, out Vector3 top2);
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Top,
                top0,
                top1,
                top2,
                ResolveShapeProjectedUv(BlockFace.Top, top0),
                ResolveShapeProjectedUv(BlockFace.Top, top1),
                ResolveShapeProjectedUv(BlockFace.Top, top2),
                Vector3.up,
                Vector3.up,
                (top1 - top0).normalized,
                (top2 - top0).normalized,
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
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight);

            VerticalRampShapeUtility.ResolveSideQuad(axis, out Vector3 side0, out Vector3 side1, out Vector3 side2, out Vector3 side3, out BlockFace sideFace);
            Vector3 sideNormal = ResolveShapeFaceNormal(sideFace);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                sideFace,
                side0,
                side1,
                side2,
                side3,
                ResolveShapeProjectedUv(sideFace, side0),
                ResolveShapeProjectedUv(sideFace, side1),
                ResolveShapeProjectedUv(sideFace, side2),
                ResolveShapeProjectedUv(sideFace, side3),
                sideNormal,
                sideNormal,
                (side1 - side0).normalized,
                (side3 - side0).normalized,
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
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false);

            VerticalRampShapeUtility.ResolveFrontQuad(axis, out Vector3 front0, out Vector3 front1, out Vector3 front2, out Vector3 front3, out BlockFace frontFace);
            Vector3 frontNormal = ResolveShapeFaceNormal(frontFace);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                frontFace,
                front0,
                front1,
                front2,
                front3,
                ResolveShapeProjectedUv(frontFace, front0),
                ResolveShapeProjectedUv(frontFace, front1),
                ResolveShapeProjectedUv(frontFace, front2),
                ResolveShapeProjectedUv(frontFace, front3),
                frontNormal,
                frontNormal,
                (front1 - front0).normalized,
                (front3 - front0).normalized,
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
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false);

            VerticalRampShapeUtility.ResolveSlopeQuad(axis, out Vector3 slope0, out Vector3 slope1, out Vector3 slope2, out Vector3 slope3, out BlockFace slopeFace, out Vector3 slopeNormal);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                slopeFace,
                slope0,
                slope1,
                slope2,
                slope3,
                ResolveShapeProjectedUv(slopeFace, slope0),
                ResolveShapeProjectedUv(slopeFace, slope1),
                ResolveShapeProjectedUv(slopeFace, slope2),
                ResolveShapeProjectedUv(slopeFace, slope3),
                slopeNormal,
                slopeNormal,
                (slope1 - slope0).normalized,
                (slope3 - slope0).normalized,
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
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false,
                true);
        }

        private StairShapeVariant ResolveStairShapeVariant(
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!StairPlacementUtility.TryDecode(rawPlacementData, out StairFacing currentFacing, out bool topHalf))
                return StairShapeVariant.Straight;

            Vector3Int frontOffset = StairPlacementUtility.ToOffset(currentFacing);
            Vector3Int backOffset = StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

            bool hasFrontNeighbor = TryGetNeighborStairState(
                voxelX + frontOffset.x,
                voxelY + frontOffset.y,
                voxelZ + frontOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing frontFacing,
                out bool frontTopHalf);

            bool hasBackNeighbor = TryGetNeighborStairState(
                voxelX + backOffset.x,
                voxelY + backOffset.y,
                voxelZ + backOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing backFacing,
                out bool backTopHalf);

            return StairShapeUtility.ResolveShapeVariant(
                currentFacing,
                topHalf,
                hasFrontNeighbor,
                frontFacing,
                frontTopHalf,
                hasBackNeighbor,
                backFacing,
                backTopHalf);
        }

        private bool TryGetNeighborStairState(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out StairFacing facing,
            out bool topHalf)
        {
            facing = StairFacing.North;
            topHalf = false;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            BlockType neighborType = (BlockType)blockTypes[idx];
            int mapIndex = (int)neighborType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            if (BlockShapeUtility.GetEffectiveRenderShape(blockMappings[mapIndex]) != BlockRenderShape.Stairs)
                return false;

            return StairPlacementUtility.TryDecode(GetBlockPlacementAxisValue(idx), out facing, out topHalf);
        }


        private FixedList512Bytes<ShapeBox> BuildStairVisualBoxes(byte rawPlacementData, StairShapeVariant variant)
        {
            StairShapeUtility.ResolveBoxes(
                rawPlacementData,
                variant,
                out int boxCount,
                out ShapeBox box0,
                out ShapeBox box1,
                out ShapeBox box2,
                out ShapeBox box3,
                out ShapeBox box4);

            FixedList512Bytes<ShapeBox> boxes = default;
            boxes.Add(box0);
            if (boxCount > 1) boxes.Add(box1);
            if (boxCount > 2) boxes.Add(box2);
            if (boxCount > 3) boxes.Add(box3);
            if (boxCount > 4) boxes.Add(box4);
            return boxes;
        }

    }
}
