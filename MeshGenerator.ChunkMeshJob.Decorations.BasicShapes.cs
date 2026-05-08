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
        private void AddCrossShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            ResolveAtlasRect(mapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
            Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
            Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
            Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
            AddDoubleSidedShapeQuad(a0, a1, a2, a3, atlasUv, atlasSize, light01, tint, tris);

            Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
            Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
            Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
            Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
            AddDoubleSidedShapeQuad(b0, b1, b2, b3, atlasUv, atlasSize, light01, tint, tris);
        }

        private void AddPlaneShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockPlacementAxis placementAxis,
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
            ResolvePlaneSupportFlags(
                voxelX,
                voxelY,
                voxelZ,
                placementAxis,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out bool hasNegativeSupport,
                out bool hasPositiveSupport);

            BlockShapeUtility.ResolvePlaneQuad(
                mapping,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2,
                out Vector3 p3,
                out BlockFace sampledFace,
                out _);

            ResolveAtlasRect(mapping, sampledFace, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            float tint = mapping.GetTint(sampledFace) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            if (blockType == BlockType.wire)
            {
                bool wireHasTopOnCurrentCell = WirePlacementUtility.HasTop(rawPlacementData);
                bool wireHasWallOnCurrentCell = WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis wireSurfaceAxis, out int wireAttachmentSide);
                if (!wireHasWallOnCurrentCell)
                {
                    BlockPlacementAxis resolvedSurfaceAxis = ResolveWireSurfaceAxis(placementAxis, hasNegativeSupport, hasPositiveSupport);
                    if (resolvedSurfaceAxis == BlockPlacementAxis.Y)
                    {
                        wireHasTopOnCurrentCell = true;
                    }
                    else
                    {
                        wireHasWallOnCurrentCell = true;
                        wireSurfaceAxis = resolvedSurfaceAxis;
                        wireAttachmentSide = ResolveWireAttachmentSide(resolvedSurfaceAxis, hasNegativeSupport, hasPositiveSupport);
                    }
                }

                ResolveAtlasRect(mapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out Vector2 lineAtlasUv, out Vector2 lineAtlasSize);
                ResolveAtlasRect(mapping, BlockFace.Back, invAtlasTilesX, invAtlasTilesY, out Vector2 shortLineAtlasUv, out Vector2 shortLineAtlasSize);
                ResolveAtlasRect(mapping, BlockFace.Top, invAtlasTilesX, invAtlasTilesY, out Vector2 dotAtlasUv, out Vector2 dotAtlasSize);

                if (wireHasTopOnCurrentCell)
                {
                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        BlockPlacementAxis.Y,
                        false,
                        false,
                        out Vector3 topP0,
                        out Vector3 topP1,
                        out Vector3 topP2,
                        out Vector3 topP3,
                        out _,
                        out _);

                    OffsetWireTopQuad(ref topP0, ref topP1, ref topP2, ref topP3);

                    RenderWireTopSurface(
                        origin,
                        topP0,
                        topP1,
                        topP2,
                        topP3,
                        wireHasWallOnCurrentCell ? wireSurfaceAxis : BlockPlacementAxis.Y,
                        wireHasWallOnCurrentCell ? wireAttachmentSide : 0,
                        lineAtlasUv,
                        lineAtlasSize,
                        shortLineAtlasUv,
                        shortLineAtlasSize,
                        dotAtlasUv,
                        dotAtlasSize,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                if (wireHasWallOnCurrentCell)
                {
                    bool wallHasNegativeSupport = wireAttachmentSide < 0;
                    bool wallHasPositiveSupport = wireAttachmentSide > 0;

                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        wireSurfaceAxis,
                        wallHasNegativeSupport,
                        wallHasPositiveSupport,
                        out Vector3 wallP0,
                        out Vector3 wallP1,
                        out Vector3 wallP2,
                        out Vector3 wallP3,
                        out _,
                        out _);

                    OffsetWireWallQuad(ref wallP0, ref wallP1, ref wallP2, ref wallP3, wireSurfaceAxis, wireAttachmentSide);

                    RenderWireWallSurface(
                        origin,
                        wallP0,
                        wallP1,
                        wallP2,
                        wallP3,
                        wireSurfaceAxis,
                        wireAttachmentSide,
                        wireHasTopOnCurrentCell,
                        lineAtlasUv,
                        lineAtlasSize,
                        shortLineAtlasUv,
                        shortLineAtlasSize,
                        dotAtlasUv,
                        dotAtlasSize,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                return;
            }

            AddDoubleSidedShapeQuad(
                origin + p0,
                origin + p1,
                origin + p2,
                origin + p3,
                atlasUv,
                atlasSize,
                light01,
                tint,
                tris);
        }

        private void AddSlabShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
            AddStaticLitShapeBox(
                origin,
                mapping,
                SlabShapeUtility.GetVisualBox(placementAxis),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

    }
}
