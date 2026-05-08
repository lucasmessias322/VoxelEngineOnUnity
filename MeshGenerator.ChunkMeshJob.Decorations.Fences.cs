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
        private void AddFenceShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            bool singleRail,
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
            byte connectionMask = FenceShapeUtility.ResolveConnectionMask(
                voxelX,
                voxelY,
                voxelZ,
                blockTypes,
                blockMappings,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            FixedList512Bytes<ShapeBox> shapeBoxes = BuildFenceVisualBoxes(connectionMask, singleRail);
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


        private FixedList512Bytes<ShapeBox> BuildFenceVisualBoxes(byte connectionMask)
        {
            return BuildFenceVisualBoxes(connectionMask, false);
        }

        private FixedList512Bytes<ShapeBox> BuildFenceVisualBoxes(byte connectionMask, bool singleRail)
        {
            FixedList512Bytes<ShapeBox> boxes = default;
            boxes.Add(FenceShapeUtility.GetCenterPostVisualBox());

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
            {
                AddFenceRailVisualBoxes(ref boxes, FenceShapeUtility.ConnectWest, singleRail);
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
            {
                AddFenceRailVisualBoxes(ref boxes, FenceShapeUtility.ConnectEast, singleRail);
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
            {
                AddFenceRailVisualBoxes(ref boxes, FenceShapeUtility.ConnectSouth, singleRail);
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
            {
                AddFenceRailVisualBoxes(ref boxes, FenceShapeUtility.ConnectNorth, singleRail);
            }

            return boxes;
        }

        private void AddFenceRailVisualBoxes(ref FixedList512Bytes<ShapeBox> boxes, byte directionFlag, bool singleRail)
        {
            if (singleRail)
            {
                boxes.Add(FenceShapeUtility.GetSingleRailVisualBox(directionFlag));
                return;
            }

            boxes.Add(FenceShapeUtility.GetRailVisualBox(directionFlag, false));
            boxes.Add(FenceShapeUtility.GetRailVisualBox(directionFlag, true));
        }

        private void AddFenceRailIfConnected(
            Vector3 origin,
            BlockTextureMapping mapping,
            byte connectionMask,
            byte directionFlag,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!FenceShapeUtility.IsFenceConnectionActive(connectionMask, directionFlag))
                return;

            AddStaticLitShapeBox(origin, mapping, FenceShapeUtility.GetRailVisualBox(directionFlag, false), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeBox(origin, mapping, FenceShapeUtility.GetRailVisualBox(directionFlag, true), light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

    }
}
