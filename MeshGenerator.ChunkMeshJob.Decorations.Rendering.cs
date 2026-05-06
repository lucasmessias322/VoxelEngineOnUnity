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
        private static Vector2 ResolveShapeProjectedUv(BlockFace sampledFace, Vector3 localPos)
        {
            return sampledFace switch
            {
                BlockFace.Top => new Vector2(localPos.x, -localPos.z),
                BlockFace.Bottom => new Vector2(localPos.x, localPos.z),
                BlockFace.Right => new Vector2(-localPos.z, localPos.y),
                BlockFace.Left => new Vector2(localPos.z, localPos.y),
                BlockFace.Front => new Vector2(localPos.x, localPos.y),
                BlockFace.Back => new Vector2(-localPos.x, localPos.y),
                _ => new Vector2(localPos.x, localPos.y)
            };
        }

        private static void ResolveCanonicalModelFaceQuadUv(
            BlockFace localFace,
            out Vector2 uv0,
            out Vector2 uv1,
            out Vector2 uv2,
            out Vector2 uv3)
        {
            switch (localFace)
            {
                case BlockFace.Top:
                    uv0 = new Vector2(0f, 0f);
                    uv1 = new Vector2(1f, 0f);
                    uv2 = new Vector2(1f, 1f);
                    uv3 = new Vector2(0f, 1f);
                    return;

                case BlockFace.Bottom:
                    uv0 = new Vector2(0f, 0f);
                    uv1 = new Vector2(1f, 0f);
                    uv2 = new Vector2(1f, 1f);
                    uv3 = new Vector2(0f, 1f);
                    return;

                case BlockFace.Right:
                case BlockFace.Left:
                case BlockFace.Front:
                case BlockFace.Back:
                    uv0 = new Vector2(1f, 0f);
                    uv1 = new Vector2(1f, 1f);
                    uv2 = new Vector2(0f, 1f);
                    uv3 = new Vector2(0f, 0f);
                    return;
            }

            uv0 = new Vector2(0f, 0f);
            uv1 = new Vector2(0f, 1f);
            uv2 = new Vector2(1f, 1f);
            uv3 = new Vector2(1f, 0f);
        }

        private static void NormalizeProjectedQuadUv(ref Vector2 uv0, ref Vector2 uv1, ref Vector2 uv2, ref Vector2 uv3)
        {
            float minU = math.min(math.min(uv0.x, uv1.x), math.min(uv2.x, uv3.x));
            float maxU = math.max(math.max(uv0.x, uv1.x), math.max(uv2.x, uv3.x));
            float minV = math.min(math.min(uv0.y, uv1.y), math.min(uv2.y, uv3.y));
            float maxV = math.max(math.max(uv0.y, uv1.y), math.max(uv2.y, uv3.y));

            float invSpanU = 1f / math.max(maxU - minU, 1e-6f);
            float invSpanV = 1f / math.max(maxV - minV, 1e-6f);

            uv0 = new Vector2((uv0.x - minU) * invSpanU, (uv0.y - minV) * invSpanV);
            uv1 = new Vector2((uv1.x - minU) * invSpanU, (uv1.y - minV) * invSpanV);
            uv2 = new Vector2((uv2.x - minU) * invSpanU, (uv2.y - minV) * invSpanV);
            uv3 = new Vector2((uv3.x - minU) * invSpanU, (uv3.y - minV) * invSpanV);
        }

        private static void NormalizeProjectedQuadUv(
            ref Vector2 uv0,
            ref Vector2 uv1,
            ref Vector2 uv2,
            ref Vector2 uv3,
            Vector4 sourceProjectedUvBounds)
        {
            float minU = sourceProjectedUvBounds.x;
            float maxU = sourceProjectedUvBounds.y;
            float minV = sourceProjectedUvBounds.z;
            float maxV = sourceProjectedUvBounds.w;

            if (math.abs(maxU - minU) <= 1e-6f || math.abs(maxV - minV) <= 1e-6f)
            {
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);
                return;
            }

            float invSpanU = 1f / (maxU - minU);
            float invSpanV = 1f / (maxV - minV);

            uv0 = new Vector2((uv0.x - minU) * invSpanU, (uv0.y - minV) * invSpanV);
            uv1 = new Vector2((uv1.x - minU) * invSpanU, (uv1.y - minV) * invSpanV);
            uv2 = new Vector2((uv2.x - minU) * invSpanU, (uv2.y - minV) * invSpanV);
            uv3 = new Vector2((uv3.x - minU) * invSpanU, (uv3.y - minV) * invSpanV);
        }

        private static void RotateConveyorProjectedUvForPlacement(
            BlockTextureMapping mapping,
            BlockFace sampledFace,
            BlockPlacementAxis placementAxis,
            ref Vector2 uv0,
            ref Vector2 uv1,
            ref Vector2 uv2,
            ref Vector2 uv3)
        {
            if (mapping.blockType != BlockType.ConveyorBelt ||
                (sampledFace != BlockFace.Top && sampledFace != BlockFace.Bottom))
            {
                return;
            }

            BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            if (axis == BlockPlacementAxis.Y)
                return;

            uv0 = RotateConveyorProjectedUv(uv0, axis);
            uv1 = RotateConveyorProjectedUv(uv1, axis);
            uv2 = RotateConveyorProjectedUv(uv2, axis);
            uv3 = RotateConveyorProjectedUv(uv3, axis);
        }

        private static Vector2 RotateConveyorProjectedUv(Vector2 uv, BlockPlacementAxis placementAxis)
        {
            switch (placementAxis)
            {
                case BlockPlacementAxis.X:
                    return new Vector2(uv.y, 1f - uv.x);

                case BlockPlacementAxis.XNegative:
                    return new Vector2(1f - uv.y, uv.x);

                case BlockPlacementAxis.ZNegative:
                    return uv;

                default:
                    return new Vector2(1f - uv.x, 1f - uv.y);
            }
        }

        private static Vector3 ResolveShapeFaceNormal(BlockFace face)
        {
            return face switch
            {
                BlockFace.Right => Vector3.right,
                BlockFace.Left => Vector3.left,
                BlockFace.Top => Vector3.up,
                BlockFace.Bottom => Vector3.down,
                BlockFace.Front => Vector3.forward,
                _ => Vector3.back
            };
        }


        private void AddStaticLitCustomQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector2 uv3,
            Vector3 normal,
            Vector2Int tile,
            bool tint,
            Vector4 explicitUvRectData,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            bool invertWinding = false)
        {
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            Vector2 atlasUv;
            Vector2 atlasSize;
            if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
            {
                atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
                atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
            }
            else
            {
                atlasUv = new Vector2(tile.x * invAtlasTilesX, tile.y * invAtlasTilesY);
                atlasSize = new Vector2(invAtlasTilesX, invAtlasTilesY);
            }

            int vIndex = GetCurrentSliceVertexIndex();
            VoxelLightChannels lightChannels = ResolveActiveSpecialMeshLightChannels(light01);
            Vector4 extra = new Vector4(lightChannels.sky, tint ? 1f : 0f, 1f, 0f);
            Vector4 atlasAndBlockLight = EncodeAtlasSizeWithBlockLight(atlasSize, lightChannels);
            AddPackedVertex(p0, normal, uv0, atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p1, normal, uv1, atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p2, normal, uv2, atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);
            AddPackedVertex(p3, normal, uv3, atlasUv, extra, atlasAndBlockLight, lightChannels.blockLightColor);

            if (invertWinding)
            {
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 2);
                tris.Add(vIndex + 1);
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 3);
                tris.Add(vIndex + 2);
                return;
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddStaticLitShapeBox(
            Vector3 origin,
            BlockTextureMapping mapping,
            ShapeBox box,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            AddStaticLitShapeFace(
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Right,
                mapping.GetTint(BlockFace.Right),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                mapping,
                BlockFace.Left,
                mapping.GetTint(BlockFace.Left),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                mapping,
                BlockFace.Top,
                mapping.GetTint(BlockFace.Top),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Bottom,
                mapping.GetTint(BlockFace.Bottom),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Front,
                mapping.GetTint(BlockFace.Front),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                mapping,
                BlockFace.Back,
                mapping.GetTint(BlockFace.Back),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }
    }
}
