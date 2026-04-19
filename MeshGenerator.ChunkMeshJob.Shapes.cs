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
        private void ResolvePlaneSupportFlags(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis placementAxis,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out bool hasNegativeSupport,
            out bool hasPositiveSupport)
        {
            hasNegativeSupport = false;
            hasPositiveSupport = false;

            switch (BlockPlacementRotationUtility.SanitizeAxis(placementAxis))
            {
                case BlockPlacementAxis.X:
                    hasNegativeSupport = IsPlaneSupportBlock(voxelX - 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    hasPositiveSupport = IsPlaneSupportBlock(voxelX + 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    break;

                case BlockPlacementAxis.Z:
                    hasNegativeSupport = IsPlaneSupportBlock(voxelX, voxelY, voxelZ - 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    hasPositiveSupport = IsPlaneSupportBlock(voxelX, voxelY, voxelZ + 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    break;
            }
        }

        private bool IsPlaneSupportBlock(
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
            BlockType type = (BlockType)blockTypes[idx];
            if (type == BlockType.Air || FluidBlockUtility.IsWater(type))
                return false;

            int mapIndex = (int)type;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            return mapping.isSolid && !mapping.isEmpty && !mapping.isLiquid;
        }

        private void AddCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            int voxelX,
            int voxelY,
            int voxelZ,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);
            byte emission = mapping.lightEmission;

            if (IsWallTorch(blockType))
            {
                float resolvedLight01 = math.max(light01, emission / 15f);
                AddWallTorchShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, resolvedLight01, tris);
                return;
            }

            AddShapeFace(
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, min.y, max.z),
                Vector3.right,
                BlockFace.Right,
                mapping,
                mapping.GetTint(BlockFace.Right),
                new Vector3Int(voxelX + 1, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(min.x, min.y, min.z),
                Vector3.left,
                BlockFace.Left,
                mapping,
                mapping.GetTint(BlockFace.Left),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                Vector3.up,
                BlockFace.Top,
                mapping,
                mapping.GetTint(BlockFace.Top),
                new Vector3Int(voxelX, voxelY + 1, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.down,
                BlockFace.Bottom,
                mapping,
                mapping.GetTint(BlockFace.Bottom),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.forward,
                BlockFace.Front,
                mapping,
                mapping.GetTint(BlockFace.Front),
                new Vector3Int(voxelX, voxelY, voxelZ + 1),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                Vector3.back,
                BlockFace.Back,
                mapping,
                mapping.GetTint(BlockFace.Back),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddWallTorchShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            Vector3 modelMin = new Vector3(min.x - 0.5f, min.y, min.z - 0.5f);
            Vector3 modelMax = new Vector3(max.x - 0.5f, max.y, max.z - 0.5f);

            Vector3 p000 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMin.z));
            Vector3 p001 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMax.z));
            Vector3 p010 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMin.z));
            Vector3 p011 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMax.z));
            Vector3 p100 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMin.z));
            Vector3 p101 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMax.z));
            Vector3 p110 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMin.z));
            Vector3 p111 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMax.z));

            AddStaticLitShapeFace(p100, p110, p111, p101, mapping, BlockFace.Right, mapping.GetTint(BlockFace.Right), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
            AddStaticLitShapeFace(p001, p011, p010, p000, mapping, BlockFace.Left, mapping.GetTint(BlockFace.Left), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
            AddStaticLitShapeFace(p011, p111, p110, p010, mapping, BlockFace.Top, mapping.GetTint(BlockFace.Top), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
            AddStaticLitShapeFace(p000, p100, p101, p001, mapping, BlockFace.Bottom, mapping.GetTint(BlockFace.Bottom), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
            AddStaticLitShapeFace(p101, p111, p011, p001, mapping, BlockFace.Front, mapping.GetTint(BlockFace.Front), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
            AddStaticLitShapeFace(p000, p010, p110, p100, mapping, BlockFace.Back, mapping.GetTint(BlockFace.Back), light01, invAtlasTilesX, invAtlasTilesY, tris, light01);
        }

        private void AddStaticLitShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            BlockTextureMapping mapping,
            BlockFace face,
            bool tint,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            float blockLight01 = 0f)
        {
            int vIndex = GetCurrentSliceVertexIndex();
            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            ResolveAtlasRect(mapping, face, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            VoxelLightChannels lightChannels = ResolveActiveSpecialMeshLightChannels(light01);
            if (mapping.lightEmission > 0)
            {
                ushort emissionPacked = LightUtils.PackEmission(
                    mapping.lightEmission,
                    mapping.lightColor.r,
                    mapping.lightColor.g,
                    mapping.lightColor.b);
                lightChannels = VoxelLightChannels.Max(lightChannels, new VoxelLightChannels(emissionPacked));
            }

            Vector4 extra = new Vector4(lightChannels.sky, tint ? 1f : 0f, 1f, 0f);
            Vector4 atlasAndBlockLight = EncodeAtlasSizeWithBlockLight(atlasSize, lightChannels);
            AddPackedVertex(p0, normal, new Vector2(0f, 0f), atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p1, normal, new Vector2(1f, 0f), atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p2, normal, new Vector2(1f, 1f), atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p3, normal, new Vector2(0f, 1f), atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private static bool IsWallTorch(BlockType blockType)
        {
            return blockType == BlockType.WallTorchEast ||
                   blockType == BlockType.WallTorchWest ||
                   blockType == BlockType.WallTorchSouth ||
                   blockType == BlockType.WallTorchNorth;
        }

        private static Vector3 TransformTorchModelPoint(BlockType blockType, Vector3 modelPoint)
        {
            if (!IsWallTorch(blockType))
                return modelPoint + new Vector3(0.5f, 0f, 0.5f);

            const float angleRadians = 0.3926991f;
            const float anchorHeight = TorchPlacementUtility.WallAnchorHeight;
            const float anchorOffset = TorchPlacementUtility.WallAnchorOffset;

            float sin = math.sin(angleRadians);
            float cos = math.cos(angleRadians);

            Vector3 rotated = modelPoint;
            switch (blockType)
            {
                case BlockType.WallTorchEast:
                    rotated = new Vector3(
                        modelPoint.x * cos + modelPoint.y * sin,
                        -modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f - anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchWest:
                    rotated = new Vector3(
                        modelPoint.x * cos - modelPoint.y * sin,
                        modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f + anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchSouth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos - modelPoint.z * sin,
                        modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f - anchorOffset);

                case BlockType.WallTorchNorth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos + modelPoint.z * sin,
                        -modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f + anchorOffset);

                default:
                    return modelPoint + new Vector3(0.5f, 0f, 0.5f);
            }
        }

        private void AddShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            BlockFace face,
            BlockTextureMapping mapping,
            bool tint,
            Vector3Int lightPlanePos,
            Vector3Int lightStepU,
            Vector3Int lightStepV,
            byte emission,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int vertexGlobalStart = vertices.Length;
            int vIndex = GetCurrentSliceVertexIndex();
            ushort emissionPacked = LightUtils.PackEmission(
                emission,
                mapping.lightColor.r,
                mapping.lightColor.g,
                mapping.lightColor.b);
            ResolveAtlasRect(mapping, face, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            AddPackedVertex(p0, normal, new Vector2(0f, 0f), atlasUv, default, atlasSize);
            AddPackedVertex(p1, normal, new Vector2(1f, 0f), atlasUv, default, atlasSize);
            AddPackedVertex(p2, normal, new Vector2(1f, 1f), atlasUv, default, atlasSize);
            AddPackedVertex(p3, normal, new Vector2(0f, 1f), atlasUv, default, atlasSize);

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3Int stepU = (corner == 1 || corner == 2) ? lightStepU : -lightStepU;
                Vector3Int stepV = (corner == 2 || corner == 3) ? lightStepV : -lightStepV;
                ushort vertexLight = GetVertexLight(lightPlanePos, stepU, stepV, light, SizeX + 2 * border, SizeZ + 2 * border, (SizeX + 2 * border) * SizeY);
                if (emission > 0)
                    vertexLight = WithBlockLightAtLeast(vertexLight, emissionPacked);

                PackedChunkVertex vertex = vertices[vertexGlobalStart + corner];
                vertex.uv2 = new Vector4(GetSkyLight01(vertexLight), tint ? 1f : 0f, 1f, 0f);
                vertex.uv3 = EncodeAtlasSizeWithBlockLight(atlasSize, vertexLight);
                vertex.blockLightColor = LightUtils.EncodeBlockLightColor32(vertexLight);
                vertices[vertexGlobalStart + corner] = vertex;
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private static void ResolveShapeQuadUv(
            int uvQuarterTurns,
            out Vector2 uv0,
            out Vector2 uv1,
            out Vector2 uv2,
            out Vector2 uv3)
        {
            switch (uvQuarterTurns & 3)
            {
                case 1:
                    uv0 = new Vector2(0f, 1f);
                    uv1 = new Vector2(0f, 0f);
                    uv2 = new Vector2(1f, 0f);
                    uv3 = new Vector2(1f, 1f);
                    return;

                case 2:
                    uv0 = new Vector2(1f, 1f);
                    uv1 = new Vector2(0f, 1f);
                    uv2 = new Vector2(0f, 0f);
                    uv3 = new Vector2(1f, 0f);
                    return;

                case 3:
                    uv0 = new Vector2(1f, 0f);
                    uv1 = new Vector2(1f, 1f);
                    uv2 = new Vector2(0f, 1f);
                    uv3 = new Vector2(0f, 0f);
                    return;

                default:
                    uv0 = new Vector2(0f, 0f);
                    uv1 = new Vector2(1f, 0f);
                    uv2 = new Vector2(1f, 1f);
                    uv3 = new Vector2(0f, 1f);
                    return;
            }
        }

        private void AddDoubleSidedShapeQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float light01,
            float tint,
            NativeList<int> tris,
            int uvQuarterTurns = 0)
        {
            int vIndex = GetCurrentSliceVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);
            ResolveShapeQuadUv(uvQuarterTurns, out Vector2 uv0, out Vector2 uv1, out Vector2 uv2, out Vector2 uv3);

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

        private struct VoxelLightChannels
        {
            public float sky;
            public float block;
            public ushort packedLight;
            public uint blockLightColor;

            public VoxelLightChannels(ushort packedLight)
            {
                this.packedLight = packedLight;
                sky = GetSkyLight01(packedLight);
                block = GetBlockLight01(packedLight);
                blockLightColor = LightUtils.EncodeBlockLightColor32(packedLight);
            }

            public static VoxelLightChannels Max(VoxelLightChannels a, VoxelLightChannels b)
            {
                return new VoxelLightChannels(LightUtils.MaxPackedLight(a.packedLight, b.packedLight));
            }
        }

        private VoxelLightChannels ResolveActiveSpecialMeshLightChannels(float fallbackLight01)
        {
            VoxelLightChannels channels = activeSpecialMeshLightChannels;
            if (channels.packedLight != 0 || channels.sky > 0f || channels.block > 0f || fallbackLight01 <= 0f)
                return channels;

            byte fallbackSkyLight = (byte)math.clamp((int)math.round(math.saturate(fallbackLight01) * 15f), 0, 15);
            return new VoxelLightChannels(LightUtils.PackLight(fallbackSkyLight, 0));
        }

        private static float GetResolvedLight01(VoxelLightChannels channels)
        {
            return math.max(channels.sky, channels.block);
        }

        private static Vector4 EncodeAtlasSizeWithBlockLight(Vector2 atlasSize, VoxelLightChannels channels)
        {
            return new Vector4(atlasSize.x, atlasSize.y, math.saturate(channels.block), 0f);
        }

        private VoxelLightChannels GetSpecialMeshLightChannels01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<ushort> light)
        {
            VoxelLightChannels channels = SampleSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
            channels = VoxelLightChannels.Max(channels, SampleSpecialMeshLightChannels01(x, y + 1, z, voxelSizeX, voxelSizeZ, light));
            channels = VoxelLightChannels.Max(channels, SampleSpecialMeshLightChannels01(x + 1, y, z, voxelSizeX, voxelSizeZ, light));
            channels = VoxelLightChannels.Max(channels, SampleSpecialMeshLightChannels01(x - 1, y, z, voxelSizeX, voxelSizeZ, light));
            channels = VoxelLightChannels.Max(channels, SampleSpecialMeshLightChannels01(x, y, z + 1, voxelSizeX, voxelSizeZ, light));
            channels = VoxelLightChannels.Max(channels, SampleSpecialMeshLightChannels01(x, y, z - 1, voxelSizeX, voxelSizeZ, light));
            return channels;
        }

        private VoxelLightChannels SampleSpecialMeshLightChannels01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<ushort> light)
        {
            if ((uint)x >= (uint)voxelSizeX ||
                (uint)y >= (uint)SizeY ||
                (uint)z >= (uint)voxelSizeZ)
            {
                return default;
            }

            int voxelPlaneSize = voxelSizeX * SizeY;
            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)light.Length)
                return default;

            ushort packed = light[idx];
            return new VoxelLightChannels(packed);
        }

        private float GetSpecialMeshLight01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<ushort> light)
        {
            float light01 = SampleSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y + 1, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x + 1, y, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x - 1, y, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y, z + 1, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y, z - 1, voxelSizeX, voxelSizeZ, light));
            return light01;
        }

        private float SampleSpecialMeshLight01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<ushort> light)
        {
            if ((uint)x >= (uint)voxelSizeX ||
                (uint)y >= (uint)SizeY ||
                (uint)z >= (uint)voxelSizeZ)
            {
                return 0f;
            }

            int voxelPlaneSize = voxelSizeX * SizeY;
            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)light.Length)
                return 0f;

            return GetResolvedLight01(light[idx]);
        }

        private float GetResolvedLight01(ushort packed)
        {
            byte lightValue = ResolvePackedLightValue(packed);
            return lightValue / 15f;
        }

        private void ResolveShapeBounds(BlockTextureMapping mapping, out Vector3 min, out Vector3 max)
        {
            float3 clampedMin = math.clamp(
                new float3(mapping.shapeMin.x, mapping.shapeMin.y, mapping.shapeMin.z),
                0f,
                1f);
            float3 clampedMax = math.clamp(
                new float3(mapping.shapeMax.x, mapping.shapeMax.y, mapping.shapeMax.z),
                0f,
                1f);

            bool valid =
                clampedMax.x > clampedMin.x + 0.0001f &&
                clampedMax.y > clampedMin.y + 0.0001f &&
                clampedMax.z > clampedMin.z + 0.0001f;

            if (valid)
            {
                min = new Vector3(clampedMin.x, clampedMin.y, clampedMin.z);
                max = new Vector3(clampedMax.x, clampedMax.y, clampedMax.z);
                return;
            }

            switch (BlockShapeUtility.GetEffectiveRenderShape(mapping))
            {
                case BlockRenderShape.Cross:
                    min = new Vector3(0.15f, 0f, 0.15f);
                    max = new Vector3(0.85f, 1f, 0.85f);
                    return;

                case BlockRenderShape.Cuboid:
                    min = new Vector3(0.375f, 0f, 0.375f);
                    max = new Vector3(0.625f, 0.75f, 0.625f);
                    return;

                case BlockRenderShape.Plane:
                    min = new Vector3(0f, 0f, 0f);
                    max = new Vector3(1f, 0.0625f, 1f);
                    return;

                default:
                    min = Vector3.zero;
                    max = Vector3.one;
                    return;
            }
        }

        private bool IsFaceVisibleForCurrentBlock(BlockType current, BlockType neighbor)
        {
            if (FluidBlockUtility.IsWater(current) && FluidBlockUtility.IsWater(neighbor))
                return false;

            if (current == neighbor && blockMappings[(int)current].isTransparent)
                return false;

            if (blockMappings[(int)neighbor].isEmpty)
                return true;

            bool neighborOpaque = BlockShapeUtility.GetEffectiveRenderShape(blockMappings[(int)neighbor]) == BlockRenderShape.Cube &&
                                  blockMappings[(int)neighbor].isSolid &&
                                  !blockMappings[(int)neighbor].isTransparent;
            return !neighborOpaque;
        }
    }
}
