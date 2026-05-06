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

            FixedList512Bytes<ShapeBox> shapeBoxes = BuildFenceVisualBoxes(connectionMask);
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
            FixedList512Bytes<ShapeBox> boxes = default;
            boxes.Add(FenceShapeUtility.GetCenterPostVisualBox());

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, true));
            }

            return boxes;
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
