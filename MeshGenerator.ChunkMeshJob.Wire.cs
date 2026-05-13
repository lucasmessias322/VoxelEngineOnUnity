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
        private const float WireConnectionOverlap = 0.01f;
        private const float WireUvEdgeInset = 0.001f;

        private static void OffsetWireTopQuad(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, ref Vector3 p3)
        {
            p0.y = WireSurfaceBlockOffset;
            p1.y = WireSurfaceBlockOffset;
            p2.y = WireSurfaceBlockOffset;
            p3.y = WireSurfaceBlockOffset;
        }

        private static void OffsetWireWallQuad(
            ref Vector3 p0,
            ref Vector3 p1,
            ref Vector3 p2,
            ref Vector3 p3,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide)
        {
            if (attachmentSide == 0)
                return;

            if (surfaceAxis == BlockPlacementAxis.X)
            {
                float x = attachmentSide < 0 ? WireSurfaceBlockOffset : 1f - WireSurfaceBlockOffset;
                p0.x = x;
                p1.x = x;
                p2.x = x;
                p3.x = x;
                return;
            }

            if (surfaceAxis == BlockPlacementAxis.Z)
            {
                float z = attachmentSide < 0 ? WireSurfaceBlockOffset : 1f - WireSurfaceBlockOffset;
                p0.z = z;
                p1.z = z;
                p2.z = z;
                p3.z = z;
            }
        }

        private void RenderWireTopSurface(
            Vector3 origin,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            BlockPlacementAxis wallSurfaceAxis,
            int wallAttachmentSide,
            Vector2 lineAtlasUv,
            Vector2 lineAtlasSize,
            Vector2 shortLineAtlasUv,
            Vector2 shortLineAtlasSize,
            Vector2 dotAtlasUv,
            Vector2 dotAtlasSize,
            float light01,
            float tint,
            NativeList<int> tris,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            const float armHalfWidth = 0.12f;
            const float centerHalfSize = 0.18f;
            const float layerStep = 0.0006f;

            WireTopConnectionMode eastConnection = ResolveWireTopConnectionMode(voxelX, voxelY, voxelZ, 1, 0, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            WireTopConnectionMode westConnection = ResolveWireTopConnectionMode(voxelX, voxelY, voxelZ, -1, 0, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            WireTopConnectionMode northConnection = ResolveWireTopConnectionMode(voxelX, voxelY, voxelZ, 0, 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            WireTopConnectionMode southConnection = ResolveWireTopConnectionMode(voxelX, voxelY, voxelZ, 0, -1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            bool connectEast = eastConnection != WireTopConnectionMode.None;
            bool connectWest = westConnection != WireTopConnectionMode.None;
            bool connectNorth = northConnection != WireTopConnectionMode.None;
            bool connectSouth = southConnection != WireTopConnectionMode.None;

            if (wallSurfaceAxis == BlockPlacementAxis.X)
            {
                if (wallAttachmentSide > 0)
                    connectEast = true;
                else if (wallAttachmentSide < 0)
                    connectWest = true;
            }
            else if (wallSurfaceAxis == BlockPlacementAxis.Z)
            {
                if (wallAttachmentSide > 0)
                    connectNorth = true;
                else if (wallAttachmentSide < 0)
                    connectSouth = true;
            }

            float topY = p0.y;
            if (eastConnection == WireTopConnectionMode.Up)
                AddWireTopVerticalBridge(origin, topY, 1, 0, true, lineAtlasUv, lineAtlasSize, light01, tint, tris);

            if (westConnection == WireTopConnectionMode.Up)
                AddWireTopVerticalBridge(origin, topY, -1, 0, true, lineAtlasUv, lineAtlasSize, light01, tint, tris);

            if (northConnection == WireTopConnectionMode.Up)
                AddWireTopVerticalBridge(origin, topY, 0, 1, true, lineAtlasUv, lineAtlasSize, light01, tint, tris);

            if (southConnection == WireTopConnectionMode.Up)
                AddWireTopVerticalBridge(origin, topY, 0, -1, true, lineAtlasUv, lineAtlasSize, light01, tint, tris);

            if (!connectEast && !connectWest && !connectNorth && !connectSouth)
            {
                AddWireSurfaceQuad(
                    origin,
                    p0,
                    p1,
                    p2,
                    p3,
                    0.5f - centerHalfSize,
                    0.5f + centerHalfSize,
                    0.5f - centerHalfSize,
                    0.5f + centerHalfSize,
                    dotAtlasUv,
                    dotAtlasSize,
                    light01,
                    tint,
                    tris,
                    0);
                return;
            }

            bool straightX = connectEast && connectWest && !connectNorth && !connectSouth;
            bool straightZ = connectNorth && connectSouth && !connectEast && !connectWest;

            if (straightX)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, -WireConnectionOverlap, 1f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, lineAtlasSize, light01, tint, tris, 0, projectTextureFromSurface: true);
                return;
            }

            if (straightZ)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, -WireConnectionOverlap, 1f + WireConnectionOverlap, lineAtlasUv, lineAtlasSize, light01, tint, tris, 1, projectTextureFromSurface: true);
                return;
            }

            if (connectEast)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - WireConnectionOverlap, 1f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 0, normalOffset: layerStep, projectTextureFromSurface: true);

            if (connectWest)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, -WireConnectionOverlap, 0.5f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 0, normalOffset: layerStep * 2f, projectTextureFromSurface: true);

            if (connectNorth)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0.5f - WireConnectionOverlap, 1f + WireConnectionOverlap, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 1, normalOffset: layerStep * 3f, projectTextureFromSurface: true);

            if (connectSouth)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, -WireConnectionOverlap, 0.5f + WireConnectionOverlap, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 1, normalOffset: layerStep * 4f, projectTextureFromSurface: true);
        }

        private void RenderWireWallSurface(
            Vector3 origin,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            BlockPlacementAxis wireSurfaceAxis,
            int wireAttachmentSide,
            bool hasTopOnCurrentCell,
            Vector2 lineAtlasUv,
            Vector2 lineAtlasSize,
            Vector2 shortLineAtlasUv,
            Vector2 shortLineAtlasSize,
            Vector2 dotAtlasUv,
            Vector2 dotAtlasSize,
            float light01,
            float tint,
            NativeList<int> tris,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            const float armHalfWidth = 0.12f;
            const float centerHalfSize = 0.18f;
            const float layerStep = 0.0006f;

            ResolveWireConnectionOffsets(
                wireSurfaceAxis,
                out Vector3Int negSOffset,
                out Vector3Int posSOffset,
                out Vector3Int negTOffset,
                out Vector3Int posTOffset);

            bool connectNegS = IsWireConnectedAlongSurface(
                voxelX + negSOffset.x,
                voxelY + negSOffset.y,
                voxelZ + negSOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectPosS = IsWireConnectedAlongSurface(
                voxelX + posSOffset.x,
                voxelY + posSOffset.y,
                voxelZ + posSOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectNegT = IsWireConnectedAlongSurface(
                voxelX + negTOffset.x,
                voxelY + negTOffset.y,
                voxelZ + negTOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectPosT = IsWireConnectedAlongSurface(
                voxelX + posTOffset.x,
                voxelY + posTOffset.y,
                voxelZ + posTOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            if (HasTopWireConnectionForWall(
                    voxelX,
                    voxelY,
                    voxelZ,
                    wireSurfaceAxis,
                    wireAttachmentSide,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                if (wireSurfaceAxis == BlockPlacementAxis.X)
                    connectPosT = true;
                else if (wireSurfaceAxis == BlockPlacementAxis.Z)
                    connectPosS = true;
            }

            if (HasGroundWireConnectionForWall(
                    voxelX,
                    voxelY,
                    voxelZ,
                    wireSurfaceAxis,
                    wireAttachmentSide,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                if (wireSurfaceAxis == BlockPlacementAxis.X)
                    connectNegT = true;
                else if (wireSurfaceAxis == BlockPlacementAxis.Z)
                    connectNegS = true;

                if (!hasTopOnCurrentCell)
                    AddWireWallGroundBridge(origin, wireSurfaceAxis, lineAtlasUv, lineAtlasSize, light01, tint, tris);
            }

            if (hasTopOnCurrentCell)
            {
                if (wireSurfaceAxis == BlockPlacementAxis.X)
                    connectPosT = true;
                else if (wireSurfaceAxis == BlockPlacementAxis.Z)
                    connectPosS = true;
            }

            if (!connectNegS && !connectPosS && !connectNegT && !connectPosT)
            {
                AddWireSurfaceQuad(
                    origin,
                    p0,
                    p1,
                    p2,
                    p3,
                    0.5f - centerHalfSize,
                    0.5f + centerHalfSize,
                    0.5f - centerHalfSize,
                    0.5f + centerHalfSize,
                    dotAtlasUv,
                    dotAtlasSize,
                    light01,
                    tint,
                    tris,
                    0);
                return;
            }

            bool straightS = connectNegS && connectPosS && !connectNegT && !connectPosT;
            bool straightT = connectNegT && connectPosT && !connectNegS && !connectPosS;

            if (straightS)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, -WireConnectionOverlap, 1f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, lineAtlasSize, light01, tint, tris, 0, projectTextureFromSurface: true);
                return;
            }

            if (straightT)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, -WireConnectionOverlap, 1f + WireConnectionOverlap, lineAtlasUv, lineAtlasSize, light01, tint, tris, 1, projectTextureFromSurface: true);
                return;
            }

            if (connectPosS)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - WireConnectionOverlap, 1f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 0, normalOffset: layerStep, projectTextureFromSurface: true);

            if (connectNegS)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, -WireConnectionOverlap, 0.5f + WireConnectionOverlap, 0.5f - armHalfWidth, 0.5f + armHalfWidth, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 0, normalOffset: layerStep * 2f, projectTextureFromSurface: true);

            if (connectPosT)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0.5f - WireConnectionOverlap, 1f + WireConnectionOverlap, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 1, normalOffset: layerStep * 3f, projectTextureFromSurface: true);

            if (connectNegT)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, -WireConnectionOverlap, 0.5f + WireConnectionOverlap, shortLineAtlasUv, shortLineAtlasSize, light01, tint, tris, 1, normalOffset: layerStep * 4f, projectTextureFromSurface: true);
        }

        private enum WireTopConnectionMode : byte
        {
            None = 0,
            Flat = 1,
            Up = 2,
            Down = 3
        }

        private WireTopConnectionMode ResolveWireTopConnectionMode(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int nx = voxelX + dirX;
            int nz = voxelZ + dirZ;

            if (IsTopSurfaceWireAt(nx, voxelY, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return WireTopConnectionMode.Flat;

            if (HasSameLevelWallWireConnectionFromTop(
                    voxelX,
                    voxelY,
                    voxelZ,
                    dirX,
                    dirZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return WireTopConnectionMode.Flat;
            }

            if (IsWireEndpointBlockAt(nx, voxelY, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return WireTopConnectionMode.Flat;

            bool adjacentIsSolid = IsSolidSupportBlock(nx, voxelY, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            if (adjacentIsSolid &&
                (IsTopSurfaceWireAt(nx, voxelY + 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize) ||
                 IsWireEndpointBlockAt(nx, voxelY + 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)))
            {
                return WireTopConnectionMode.Up;
            }

            if (!adjacentIsSolid &&
                (IsTopSurfaceWireAt(nx, voxelY - 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize) ||
                 IsWireEndpointBlockAt(nx, voxelY - 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)))
            {
                return WireTopConnectionMode.Down;
            }

            if (HasWallWireConnectionFromTop(
                    voxelX,
                    voxelY,
                    voxelZ,
                    dirX,
                    dirZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return WireTopConnectionMode.Flat;
            }

            return WireTopConnectionMode.None;
        }

        private bool HasSameLevelWallWireConnectionFromTop(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int wallX = voxelX + dirX;
            int wallZ = voxelZ + dirZ;

            if (!TryGetWireSurfaceAt(
                    wallX,
                    voxelY,
                    wallZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (dirX != 0)
                return wallSurfaceAxis == BlockPlacementAxis.X && wallAttachmentSide == dirX;

            return wallSurfaceAxis == BlockPlacementAxis.Z && wallAttachmentSide == dirZ;
        }

        private bool HasWallWireConnectionFromTop(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            return MatchesWallWireForTopConnection(
                       voxelX,
                       voxelY - 1,
                       voxelZ,
                       dirX,
                       dirZ,
                       1,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize)
                || MatchesWallWireForTopConnection(
                       voxelX + dirX,
                       voxelY - 1,
                       voxelZ + dirZ,
                       dirX,
                       dirZ,
                       -1,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize);
        }

        private bool MatchesWallWireForTopConnection(
            int wallX,
            int wallY,
            int wallZ,
            int dirX,
            int dirZ,
            int attachmentDirectionMultiplier,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!TryGetWireSurfaceAt(
                    wallX,
                    wallY,
                    wallZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (dirX != 0)
                return wallSurfaceAxis == BlockPlacementAxis.X && wallAttachmentSide == dirX * attachmentDirectionMultiplier;

            return wallSurfaceAxis == BlockPlacementAxis.Z && wallAttachmentSide == dirZ * attachmentDirectionMultiplier;
        }

        private bool IsTopSurfaceWireAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            return TryGetWireStateAt(
                       x,
                       y,
                       z,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize,
                       out bool hasTop,
                       out _,
                       out _)
                   && hasTop;
        }

        private bool IsSolidSupportBlock(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)blockTypes.Length)
                return false;

            BlockType type = (BlockType)blockTypes[idx];
            if (type == BlockType.Air || FluidBlockUtility.IsWater(type))
                return false;

            int mapIndex = (int)type;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping supportMapping = blockMappings[mapIndex];
            return supportMapping.isSolid && !supportMapping.isEmpty && !supportMapping.isLiquid;
        }

        private bool IsWireEndpointBlockAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (TryGetBlockTypeAt(x, y, z, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, out BlockType blockType) &&
                IsWireEndpointBlock(blockType))
            {
                return true;
            }

            return IsDynamicWireEndpointFootprintAt(x, y, z, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        private bool IsDynamicWireEndpointFootprintAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            const int maxHorizontalFootprintSearch = 4;
            const int maxVerticalFootprintSearch = 3;

            for (int yOffset = 0; yOffset <= maxVerticalFootprintSearch; yOffset++)
            {
                for (int zOffset = -maxHorizontalFootprintSearch; zOffset <= maxHorizontalFootprintSearch; zOffset++)
                {
                    for (int xOffset = -maxHorizontalFootprintSearch; xOffset <= maxHorizontalFootprintSearch; xOffset++)
                    {
                        int originX = x - xOffset;
                        int originY = y - yOffset;
                        int originZ = z - zOffset;
                        if (!TryGetBlockTypeAt(
                                originX,
                                originY,
                                originZ,
                                blockTypes,
                                voxelSizeX,
                                voxelSizeZ,
                                voxelPlaneSize,
                                out BlockType originType) ||
                            !IsWireEndpointBlock(originType))
                        {
                            continue;
                        }

                        int mappingIndex = (int)originType;
                        if ((uint)mappingIndex >= (uint)blockMappings.Length)
                            continue;

                        BlockTextureMapping mapping = blockMappings[mappingIndex];
                        if (!mapping.renderAsDynamicPrefab)
                            continue;

                        int originIndex = originX + originY * voxelSizeX + originZ * voxelPlaneSize;
                        BlockPlacementAxis placementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(
                            (BlockPlacementAxis)GetBlockPlacementAxisValue(originIndex));
                        Vector3Int localOffset = new Vector3Int(xOffset, yOffset, zOffset);
                        if (BlockShapeUtility.IsLocalOffsetInsideDynamicOccupancy(localOffset, mapping, placementAxis))
                            return true;
                    }
                }
            }

            return false;
        }

        private bool TryGetBlockTypeAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out BlockType blockType)
        {
            blockType = BlockType.Air;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)blockTypes.Length)
                return false;

            blockType = (BlockType)blockTypes[idx];
            return true;
        }

        private bool IsWireEndpointBlock(BlockType blockType)
        {
            return IsConfiguredElectricalEndpoint(blockType) ||
                   LeverUtility.IsLeverBlock(blockType) ||
                   blockType == BlockType.RoboticArm ||
                   blockType == BlockType.EletricConnector ||
                   blockType == BlockType.SolarPanel ||
                   BatteryBlockUtility.IsBatteryBlock(blockType) ||
                   blockType == BlockType.windmill ||
                   blockType == BlockType.ledWhiteBlock ||
                   blockType == BlockType.Treecutter ||
                   blockType == BlockType.AutoMiner ||
                   blockType == BlockType.StoneCrusher ||
                     blockType == BlockType.MagneticSeparator;
        }

        private bool IsConfiguredElectricalEndpoint(BlockType blockType)
        {
            int mappingIndex = (int)blockType;
            return mappingIndex >= 0 &&
                   mappingIndex < blockMappings.Length &&
                   blockMappings[mappingIndex].isElectricalEndpoint;
        }

        private bool HasTopWireConnectionForWall(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (surfaceAxis == BlockPlacementAxis.Y || attachmentSide == 0)
                return false;

            if (IsTopSurfaceWireAt(
                    voxelX,
                    voxelY + 1,
                    voxelZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize) ||
                IsWireEndpointBlockAt(
                    voxelX,
                    voxelY + 1,
                    voxelZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return true;
            }

            int supportTopX = voxelX;
            int supportTopZ = voxelZ;
            if (surfaceAxis == BlockPlacementAxis.X)
                supportTopX += attachmentSide;
            else
                supportTopZ += attachmentSide;

            return IsTopSurfaceWireAt(
                       supportTopX,
                       voxelY + 1,
                       supportTopZ,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize)
                   || IsWireEndpointBlockAt(
                       supportTopX,
                       voxelY + 1,
                       supportTopZ,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize);
        }

        private bool HasGroundWireConnectionForWall(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (surfaceAxis == BlockPlacementAxis.Y || attachmentSide == 0)
                return false;

            int outwardX = voxelX;
            int outwardZ = voxelZ;
            if (surfaceAxis == BlockPlacementAxis.X)
                outwardX -= attachmentSide;
            else
                outwardZ -= attachmentSide;

            return IsTopSurfaceWireAt(
                outwardX,
                voxelY,
                outwardZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
        }

        private void AddWireWallGroundBridge(
            Vector3 origin,
            BlockPlacementAxis surfaceAxis,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float light01,
            float tint,
            NativeList<int> tris)
        {
            const float armHalfWidth = 0.12f;
            const float floorInset = WireSurfaceBlockOffset;

            if (surfaceAxis == BlockPlacementAxis.X)
            {
                AddDoubleSidedShapeQuad(
                    origin + new Vector3(0f, floorInset, 0.5f - armHalfWidth),
                    origin + new Vector3(0f, floorInset, 0.5f + armHalfWidth),
                    origin + new Vector3(1f, floorInset, 0.5f + armHalfWidth),
                    origin + new Vector3(1f, floorInset, 0.5f - armHalfWidth),
                    atlasUv,
                    atlasSize,
                    light01,
                    tint,
                    tris,
                    0);
                return;
            }

            AddDoubleSidedShapeQuad(
                origin + new Vector3(0.5f - armHalfWidth, floorInset, 0f),
                origin + new Vector3(0.5f - armHalfWidth, floorInset, 1f),
                origin + new Vector3(0.5f + armHalfWidth, floorInset, 1f),
                origin + new Vector3(0.5f + armHalfWidth, floorInset, 0f),
                atlasUv,
                atlasSize,
                light01,
                tint,
                tris,
                1);
        }

        private void AddWireTopVerticalBridge(
            Vector3 origin,
            float baseY,
            int dirX,
            int dirZ,
            bool ascend,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float light01,
            float tint,
            NativeList<int> tris)
        {
            const float armHalfWidth = 0.12f;
            const float faceInset = WireSurfaceBlockOffset;

            float y0 = ascend ? baseY : baseY - 1f;
            float y1 = ascend ? baseY + 1f : baseY;

            Vector3 p0;
            Vector3 p1;
            Vector3 p2;
            Vector3 p3;

            if (dirX > 0)
            {
                float x = 1f - faceInset;
                float z0 = 0.5f - armHalfWidth;
                float z1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x, y0, z0);
                p1 = new Vector3(x, y1, z0);
                p2 = new Vector3(x, y1, z1);
                p3 = new Vector3(x, y0, z1);
            }
            else if (dirX < 0)
            {
                float x = faceInset;
                float z0 = 0.5f - armHalfWidth;
                float z1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x, y0, z0);
                p1 = new Vector3(x, y1, z0);
                p2 = new Vector3(x, y1, z1);
                p3 = new Vector3(x, y0, z1);
            }
            else if (dirZ > 0)
            {
                float z = 1f - faceInset;
                float x0 = 0.5f - armHalfWidth;
                float x1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x0, y0, z);
                p1 = new Vector3(x0, y1, z);
                p2 = new Vector3(x1, y1, z);
                p3 = new Vector3(x1, y0, z);
            }
            else
            {
                float z = faceInset;
                float x0 = 0.5f - armHalfWidth;
                float x1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x0, y0, z);
                p1 = new Vector3(x0, y1, z);
                p2 = new Vector3(x1, y1, z);
                p3 = new Vector3(x1, y0, z);
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
                tris,
                1);
        }

        private static BlockPlacementAxis ResolveWireSurfaceAxis(
            BlockPlacementAxis placementAxis,
            bool hasNegativeSupport,
            bool hasPositiveSupport)
        {
            placementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            return placementAxis switch
            {
                BlockPlacementAxis.X => hasNegativeSupport || hasPositiveSupport ? BlockPlacementAxis.X : BlockPlacementAxis.Y,
                BlockPlacementAxis.Z => hasNegativeSupport || hasPositiveSupport ? BlockPlacementAxis.Z : BlockPlacementAxis.Y,
                _ => BlockPlacementAxis.Y
            };
        }

        private static int ResolveWireAttachmentSide(
            BlockPlacementAxis surfaceAxis,
            bool hasNegativeSupport,
            bool hasPositiveSupport)
        {
            if (surfaceAxis == BlockPlacementAxis.Y)
                return 0;

            if (hasNegativeSupport == hasPositiveSupport)
                return 0;

            return hasNegativeSupport ? -1 : 1;
        }

        private static void ResolveWireConnectionOffsets(
            BlockPlacementAxis surfaceAxis,
            out Vector3Int negSOffset,
            out Vector3Int posSOffset,
            out Vector3Int negTOffset,
            out Vector3Int posTOffset)
        {
            switch (surfaceAxis)
            {
                case BlockPlacementAxis.X:
                    negSOffset = new Vector3Int(0, 0, -1);
                    posSOffset = new Vector3Int(0, 0, 1);
                    negTOffset = new Vector3Int(0, -1, 0);
                    posTOffset = new Vector3Int(0, 1, 0);
                    return;

                case BlockPlacementAxis.Z:
                    negSOffset = new Vector3Int(0, -1, 0);
                    posSOffset = new Vector3Int(0, 1, 0);
                    negTOffset = new Vector3Int(-1, 0, 0);
                    posTOffset = new Vector3Int(1, 0, 0);
                    return;

                default:
                    negSOffset = new Vector3Int(-1, 0, 0);
                    posSOffset = new Vector3Int(1, 0, 0);
                    negTOffset = new Vector3Int(0, 0, -1);
                    posTOffset = new Vector3Int(0, 0, 1);
                    return;
            }
        }

        private bool IsWireConnectedAlongSurface(
            int neighborX,
            int neighborY,
            int neighborZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (IsWireEndpointBlockAt(neighborX, neighborY, neighborZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return true;

            if (IsWallSurfaceEndpointBehindNeighbor(
                    neighborX,
                    neighborY,
                    neighborZ,
                    surfaceAxis,
                    attachmentSide,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return true;
            }

            if (!TryGetWireSurfaceAt(
                    neighborX,
                    neighborY,
                    neighborZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis neighborSurfaceAxis,
                    out int neighborAttachmentSide))
            {
                return false;
            }

            return AreWireSurfacesCompatible(
                surfaceAxis,
                attachmentSide,
                neighborSurfaceAxis,
                neighborAttachmentSide);
        }

        private bool TryGetWireStateAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out bool hasTop,
            out BlockPlacementAxis wallSurfaceAxis,
            out int wallAttachmentSide)
        {
            hasTop = false;
            wallSurfaceAxis = BlockPlacementAxis.Y;
            wallAttachmentSide = 0;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)blockTypes.Length)
                return false;

            if ((BlockType)blockTypes[idx] != BlockType.wire)
                return false;

            byte rawPlacementData = GetBlockPlacementAxisValue(idx);
            hasTop = WirePlacementUtility.HasTop(rawPlacementData);
            if (WirePlacementUtility.TryGetWall(rawPlacementData, out wallSurfaceAxis, out wallAttachmentSide))
                return true;

            BlockPlacementAxis neighborPlacementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis((BlockPlacementAxis)rawPlacementData);
            ResolvePlaneSupportFlags(
                x,
                y,
                z,
                neighborPlacementAxis,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out bool hasNegativeSupport,
                out bool hasPositiveSupport);

            BlockPlacementAxis resolvedSurfaceAxis = ResolveWireSurfaceAxis(neighborPlacementAxis, hasNegativeSupport, hasPositiveSupport);
            if (resolvedSurfaceAxis == BlockPlacementAxis.Y)
            {
                hasTop = true;
                return true;
            }

            wallSurfaceAxis = resolvedSurfaceAxis;
            wallAttachmentSide = ResolveWireAttachmentSide(resolvedSurfaceAxis, hasNegativeSupport, hasPositiveSupport);
            return true;
        }

        private bool TryGetWireSurfaceAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out BlockPlacementAxis surfaceAxis,
            out int attachmentSide)
        {
            surfaceAxis = BlockPlacementAxis.Y;
            attachmentSide = 0;

            if (!TryGetWireStateAt(
                    x,
                    y,
                    z,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out bool hasTop,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (wallSurfaceAxis != BlockPlacementAxis.Y)
            {
                surfaceAxis = wallSurfaceAxis;
                attachmentSide = wallAttachmentSide;
                return true;
            }

            if (!hasTop)
                return false;

            surfaceAxis = BlockPlacementAxis.Y;
            return true;
        }

        private static bool AreWireSurfacesCompatible(
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            BlockPlacementAxis neighborSurfaceAxis,
            int neighborAttachmentSide)
        {
            if (surfaceAxis != neighborSurfaceAxis)
                return false;

            if (surfaceAxis == BlockPlacementAxis.Y)
                return true;

            if (attachmentSide == 0 || neighborAttachmentSide == 0)
                return true;

            return attachmentSide == neighborAttachmentSide;
        }

        private bool IsWallSurfaceEndpointBehindNeighbor(
            int neighborX,
            int neighborY,
            int neighborZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (surfaceAxis == BlockPlacementAxis.Y || attachmentSide == 0)
                return false;

            if (surfaceAxis == BlockPlacementAxis.X)
                neighborX += attachmentSide;
            else if (surfaceAxis == BlockPlacementAxis.Z)
                neighborZ += attachmentSide;
            else
                return false;

            return IsWireEndpointBlockAt(
                neighborX,
                neighborY,
                neighborZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
        }

        private void AddWireSurfaceQuad(
            Vector3 origin,
            Vector3 planeP0,
            Vector3 planeP1,
            Vector3 planeP2,
            Vector3 planeP3,
            float minS,
            float maxS,
            float minT,
            float maxT,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float light01,
            float tint,
            NativeList<int> tris,
            int uvQuarterTurns,
            float normalOffset = 0f,
            bool projectTextureFromSurface = false)
        {
            Vector3 axisS = planeP3 - planeP0;
            Vector3 axisT = planeP1 - planeP0;
            Vector3 q0 = planeP0 + axisS * minS + axisT * minT;
            Vector3 q1 = planeP0 + axisS * minS + axisT * maxT;
            Vector3 q2 = planeP0 + axisS * maxS + axisT * maxT;
            Vector3 q3 = planeP0 + axisS * maxS + axisT * minT;

            if (normalOffset > 0f)
            {
                Vector3 normal = ComputeQuadPlaneNormal(q0, q1, q2) * normalOffset;
                q0 += normal;
                q1 += normal;
                q2 += normal;
                q3 += normal;
            }

            if (projectTextureFromSurface)
            {
                ResolveWireSurfaceUv(
                    minS,
                    maxS,
                    minT,
                    maxT,
                    uvQuarterTurns,
                    out Vector2 uv0,
                    out Vector2 uv1,
                    out Vector2 uv2,
                    out Vector2 uv3);

                AddDoubleSidedWireQuad(
                    origin + q0,
                    origin + q1,
                    origin + q2,
                    origin + q3,
                    atlasUv,
                    atlasSize,
                    light01,
                    tint,
                    tris,
                    uv0,
                    uv1,
                    uv2,
                    uv3);
                return;
            }

            AddDoubleSidedShapeQuad(
                origin + q0,
                origin + q1,
                origin + q2,
                origin + q3,
                atlasUv,
                atlasSize,
                light01,
                tint,
                tris,
                uvQuarterTurns);
        }

        private static void ResolveWireSurfaceUv(
            float minS,
            float maxS,
            float minT,
            float maxT,
            int uvQuarterTurns,
            out Vector2 uv0,
            out Vector2 uv1,
            out Vector2 uv2,
            out Vector2 uv3)
        {
            uv0 = ClampWireSurfaceUv(RotateWireSurfaceUv(new Vector2(minT, minS), uvQuarterTurns));
            uv1 = ClampWireSurfaceUv(RotateWireSurfaceUv(new Vector2(maxT, minS), uvQuarterTurns));
            uv2 = ClampWireSurfaceUv(RotateWireSurfaceUv(new Vector2(maxT, maxS), uvQuarterTurns));
            uv3 = ClampWireSurfaceUv(RotateWireSurfaceUv(new Vector2(minT, maxS), uvQuarterTurns));
        }

        private static Vector2 RotateWireSurfaceUv(Vector2 uv, int uvQuarterTurns)
        {
            switch (uvQuarterTurns & 3)
            {
                case 1:
                    return new Vector2(uv.y, 1f - uv.x);
                case 2:
                    return new Vector2(1f - uv.x, 1f - uv.y);
                case 3:
                    return new Vector2(1f - uv.y, uv.x);
                default:
                    return uv;
            }
        }

        private static Vector2 ClampWireSurfaceUv(Vector2 uv)
        {
            return new Vector2(
                math.clamp(uv.x, WireUvEdgeInset, 1f - WireUvEdgeInset),
                math.clamp(uv.y, WireUvEdgeInset, 1f - WireUvEdgeInset));
        }

        private void AddDoubleSidedWireQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float light01,
            float tint,
            NativeList<int> tris,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector2 uv3)
        {
            int vIndex = GetCurrentSliceVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);

            VoxelLightChannels lightChannels = ResolveActiveSpecialMeshLightChannels(light01);
            Vector4 e = new Vector4(lightChannels.sky, tint, 1f, 0f);
            Vector4 atlasAndBlockLight = EncodeAtlasSizeWithBlockLight(atlasSize, lightChannels);
            AddPackedVertex(p0, planeNormal, uv0, atlasUv, e, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p1, planeNormal, uv1, atlasUv, e, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p2, planeNormal, uv2, atlasUv, e, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p3, planeNormal, uv3, atlasUv, e, atlasAndBlockLight, lightChannels.blockLightColor);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 3);
            tris.Add(vIndex + 2);
        }
    }
}
