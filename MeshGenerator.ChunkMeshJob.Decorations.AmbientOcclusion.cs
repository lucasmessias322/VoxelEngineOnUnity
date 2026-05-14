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
        private void AddAmbientOccludedShapeBoxes(
            Vector3 origin,
            BlockTextureMapping mapping,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            BlockRenderShape currentShape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            FixedList4096Bytes<ShapeFaceRect> faceRects = default;
            for (int i = 0; i < shapeBoxes.Length; i++)
                AppendShapeFaceRects(ref faceRects, shapeBoxes[i]);

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
                    currentShape,
                    BlockPlacementAxis.Y,
                    RampShapeVariant.Straight);
            }
        }


        private void AddAmbientOccludedShapeRect(
            Vector3 origin,
            BlockTextureMapping mapping,
            ShapeFaceRect rect,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            ResolveShapeRectAppearance(
                rect,
                mapping,
                currentShape,
                currentPlacementAxis,
                out BlockFace textureFace,
                out BlockFace uvFace,
                out Vector2Int tile,
                out bool tint,
                out Vector4 explicitUvRectData);

            switch (rect.face)
            {
                case BlockFace.Right:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.plane, rect.minA, rect.minB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.minB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.minA, rect.maxB),
                        Vector3.right,
                        BlockFace.Right,
                        Vector3Int.right,
                        Vector3Int.up,
                        Vector3Int.forward,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }

                case BlockFace.Left:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.plane, rect.minA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.minB),
                        origin + new Vector3(rect.plane, rect.minA, rect.minB),
                        Vector3.left,
                        BlockFace.Left,
                        Vector3Int.left,
                        Vector3Int.up,
                        Vector3Int.back,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }

                case BlockFace.Top:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.minB),
                        origin + new Vector3(rect.minA, rect.plane, rect.minB),
                        Vector3.up,
                        BlockFace.Top,
                        Vector3Int.up,
                        Vector3Int.right,
                        Vector3Int.back,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }

                case BlockFace.Bottom:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.plane, rect.minB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.minB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.minA, rect.plane, rect.maxB),
                        Vector3.down,
                        BlockFace.Bottom,
                        Vector3Int.down,
                        Vector3Int.right,
                        Vector3Int.forward,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }

                case BlockFace.Front:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.maxA, rect.minB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.minA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.minA, rect.minB, rect.plane),
                        Vector3.forward,
                        BlockFace.Front,
                        Vector3Int.forward,
                        Vector3Int.up,
                        Vector3Int.left,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }

                default:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.minB, rect.plane),
                        origin + new Vector3(rect.minA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.minB, rect.plane),
                        Vector3.back,
                        BlockFace.Back,
                        Vector3Int.back,
                        Vector3Int.up,
                        Vector3Int.right,
                        mapping,
                        textureFace,
                        uvFace,
                        new Vector4(rect.sourceMinU, rect.sourceMaxU, rect.sourceMinV, rect.sourceMaxV),
                        rect.usesExplicitAppearance,
                        tile,
                        explicitUvRectData,
                        tint,
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
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant,
                        rect.usesFluidPipeImportedUv,
                        rect.usesTransportTubeImportedUv,
                        rect.fluidPipeAxis,
                        rect.fluidPipeAxisMin,
                        rect.fluidPipeAxisMax);
                    return;
                }
            }
        }

        private void AddAmbientOccludedCustomQuad(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockFace sampledFace,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector2 uv3,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool invertWinding,
            bool suppressNeighborRampAO = false,
            bool hasExplicitAppearance = false,
            Vector2Int explicitTile = default(Vector2Int),
            bool explicitTint = false,
            Vector4 explicitUvRectData = default)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            Vector2Int tile;
            bool tint;
            Vector2 atlasUv;
            Vector2 atlasSize;
            if (hasExplicitAppearance)
            {
                tile = explicitTile;
                tint = explicitTint;
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
            }
            else
            {
                BlockFace textureFace = ResolveShapeTextureFace(mapping, sampledFace, currentShape, currentPlacementAxis);
                tile = mapping.GetTileCoord(textureFace);
                tint = mapping.GetTint(textureFace);
                ResolveAtlasRect(mapping, textureFace, invAtlasTilesX, invAtlasTilesY, out atlasUv, out atlasSize);
            }
            FixedList512Bytes<ShapeBox> emptyShapeBoxes = default;
            int vIndex = GetCurrentSliceVertexIndex();

            ResolveCustomFaceVertexAOFrame(sampledFace, p0, normal, out Vector3 aoNormal0, out Vector3 stepU0, out Vector3 stepV0);
            ResolveCustomFaceVertexAOFrame(sampledFace, p1, normal, out Vector3 aoNormal1, out Vector3 stepU1, out Vector3 stepV1);
            ResolveCustomFaceVertexAOFrame(sampledFace, p2, normal, out Vector3 aoNormal2, out Vector3 stepU2, out Vector3 stepV2);
            ResolveCustomFaceVertexAOFrame(sampledFace, p3, normal, out Vector3 aoNormal3, out Vector3 stepU3, out Vector3 stepV3);

            if (currentShape == BlockRenderShape.MultiCuboid)
            {
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);
                RotateConveyorProjectedUvForPlacement(mapping, sampledFace, currentPlacementAxis, ref uv0, ref uv1, ref uv2, ref uv3);
            }

            AddAmbientOccludedShapeVertex(origin + p0, uv0, normal, aoNormal0, stepU0, stepV0, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p1, uv1, normal, aoNormal1, stepU1, stepV1, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p2, uv2, normal, aoNormal2, stepU2, stepV2, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p3, uv3, normal, aoNormal3, stepU3, stepV3, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);

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

        private void AddAmbientOccludedCustomTriangle(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockFace sampledFace,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO = false)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            Vector2Int tile = mapping.GetTileCoord(sampledFace);
            bool tint = mapping.GetTint(sampledFace);
            ResolveAtlasRect(mapping, sampledFace, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            FixedList512Bytes<ShapeBox> emptyShapeBoxes = default;
            int vIndex = GetCurrentSliceVertexIndex();

            ResolveCustomFaceVertexAOFrame(sampledFace, p0, normal, out Vector3 aoNormal0, out Vector3 stepU0, out Vector3 stepV0);
            ResolveCustomFaceVertexAOFrame(sampledFace, p1, normal, out Vector3 aoNormal1, out Vector3 stepU1, out Vector3 stepV1);
            ResolveCustomFaceVertexAOFrame(sampledFace, p2, normal, out Vector3 aoNormal2, out Vector3 stepU2, out Vector3 stepV2);

            if (mapping.blockType == BlockType.conveyorBelt_45deg &&
                (sampledFace == BlockFace.Top || sampledFace == BlockFace.Bottom))
            {
                NormalizeProjectedTriangleUv(ref uv0, ref uv1, ref uv2);
                Vector2 uv3 = uv2;
                RotateConveyorProjectedUvForPlacement(mapping, sampledFace, currentPlacementAxis, ref uv0, ref uv1, ref uv2, ref uv3);
            }

            AddAmbientOccludedShapeVertex(origin + p0, uv0, normal, aoNormal0, stepU0, stepV0, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p1, uv1, normal, aoNormal1, stepU1, stepV1, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p2, uv2, normal, aoNormal2, stepU2, stepV2, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
        }

        private static void ResolveCustomFaceVertexAOFrame(
            BlockFace sampledFace,
            Vector3 localPos,
            Vector3 geometricNormal,
            out Vector3 aoNormal,
            out Vector3 stepU,
            out Vector3 stepV)
        {
            switch (sampledFace)
            {
                case BlockFace.Top:
                    aoNormal = Vector3.up;
                    stepU = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Bottom:
                    aoNormal = Vector3.down;
                    stepU = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Right:
                    aoNormal = Vector3.right;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Left:
                    aoNormal = Vector3.left;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Front:
                    aoNormal = Vector3.forward;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    return;

                case BlockFace.Back:
                    aoNormal = Vector3.back;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    return;

                default:
                    Vector3 fallbackNormal = geometricNormal.sqrMagnitude > 0.0001f ? geometricNormal.normalized : Vector3.up;
                    aoNormal = fallbackNormal;
                    stepU = Vector3.right;
                    stepV = Vector3.forward;
                    return;
            }
        }

        private void AddAmbientOccludedShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            BlockFace sampledFace,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            BlockTextureMapping mapping,
            BlockFace textureFace,
            BlockFace uvFace,
            Vector4 sourceProjectedUvBounds,
            bool usesExplicitAppearance,
            Vector2Int tile,
            Vector4 explicitUvRectData,
            bool tint,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            bool disableAOForCurrentBlock,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool usesFluidPipeImportedUv = false,
            bool usesTransportTubeImportedUv = false,
            int fluidPipeAxis = 0,
            float fluidPipeAxisMin = 0f,
            float fluidPipeAxisMax = 1f)
        {
            int vIndex = GetCurrentSliceVertexIndex();
            Vector2 atlasUv;
            Vector2 atlasSize;
            if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
            {
                atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
                atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
            }
            else
            {
                if (usesExplicitAppearance)
                {
                    atlasUv = new Vector2(tile.x * invAtlasTilesX, tile.y * invAtlasTilesY);
                    atlasSize = new Vector2(invAtlasTilesX, invAtlasTilesY);
                }
                else
                {
                    ResolveAtlasRect(mapping, textureFace, invAtlasTilesX, invAtlasTilesY, out atlasUv, out atlasSize);
                }
            }
            Vector3 blockOrigin = new Vector3(voxelX - border, voxelY, voxelZ - border);

            Vector2 uv0;
            Vector2 uv1;
            Vector2 uv2;
            Vector2 uv3;
            bool rotateConveyorUv = (currentShape == BlockRenderShape.MultiCuboid ||
                                     mapping.blockType == BlockType.conveyorBelt_45deg) &&
                                    IsConveyorBlock(mapping.blockType) &&
                                    (sampledFace == BlockFace.Top || sampledFace == BlockFace.Bottom);
            if (rotateConveyorUv)
            {
                uv0 = ResolveShapeProjectedUv(sampledFace, p0 - blockOrigin);
                uv1 = ResolveShapeProjectedUv(sampledFace, p1 - blockOrigin);
                uv2 = ResolveShapeProjectedUv(sampledFace, p2 - blockOrigin);
                uv3 = ResolveShapeProjectedUv(sampledFace, p3 - blockOrigin);
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3, sourceProjectedUvBounds);
                RotateConveyorProjectedUvForPlacement(mapping, sampledFace, currentPlacementAxis, ref uv0, ref uv1, ref uv2, ref uv3);
            }
            else if (currentShape == BlockRenderShape.MultiCuboid && usesExplicitAppearance)
            {
                Vector3 local0 = p0 - blockOrigin;
                Vector3 local1 = p1 - blockOrigin;
                Vector3 local2 = p2 - blockOrigin;
                Vector3 local3 = p3 - blockOrigin;
                if (usesFluidPipeImportedUv)
                {
                    local0 = TransformFluidPipeImportedUvPoint(local0, fluidPipeAxis, fluidPipeAxisMin, fluidPipeAxisMax);
                    local1 = TransformFluidPipeImportedUvPoint(local1, fluidPipeAxis, fluidPipeAxisMin, fluidPipeAxisMax);
                    local2 = TransformFluidPipeImportedUvPoint(local2, fluidPipeAxis, fluidPipeAxisMin, fluidPipeAxisMax);
                    local3 = TransformFluidPipeImportedUvPoint(local3, fluidPipeAxis, fluidPipeAxisMin, fluidPipeAxisMax);
                }
                else if (usesTransportTubeImportedUv)
                {
                    local0 = TransformTransportTubeImportedUvPoint(local0, fluidPipeAxis);
                    local1 = TransformTransportTubeImportedUvPoint(local1, fluidPipeAxis);
                    local2 = TransformTransportTubeImportedUvPoint(local2, fluidPipeAxis);
                    local3 = TransformTransportTubeImportedUvPoint(local3, fluidPipeAxis);
                }
                else
                {
                    local0 = BlockShapeUtility.InverseTransformPointForPlacement(local0, mapping, currentPlacementAxis);
                    local1 = BlockShapeUtility.InverseTransformPointForPlacement(local1, mapping, currentPlacementAxis);
                    local2 = BlockShapeUtility.InverseTransformPointForPlacement(local2, mapping, currentPlacementAxis);
                    local3 = BlockShapeUtility.InverseTransformPointForPlacement(local3, mapping, currentPlacementAxis);
                }

                uv0 = ResolveShapeProjectedUv(uvFace, local0);
                uv1 = ResolveShapeProjectedUv(uvFace, local1);
                uv2 = ResolveShapeProjectedUv(uvFace, local2);
                uv3 = ResolveShapeProjectedUv(uvFace, local3);
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3, sourceProjectedUvBounds);
            }
            else if (currentShape == BlockRenderShape.MultiCuboid && uvFace != sampledFace)
            {
                ResolveCanonicalModelFaceQuadUv(uvFace, out uv0, out uv1, out uv2, out uv3);
            }
            else
            {
                uv0 = ResolveShapeProjectedUv(sampledFace, p0 - blockOrigin);
                uv1 = ResolveShapeProjectedUv(sampledFace, p1 - blockOrigin);
                uv2 = ResolveShapeProjectedUv(sampledFace, p2 - blockOrigin);
                uv3 = ResolveShapeProjectedUv(sampledFace, p3 - blockOrigin);
                if (currentShape == BlockRenderShape.MultiCuboid)
                    NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3, sourceProjectedUvBounds);
            }
            if (currentShape == BlockRenderShape.MultiCuboid && !rotateConveyorUv)
                RotateConveyorProjectedUvForPlacement(mapping, sampledFace, currentPlacementAxis, ref uv0, ref uv1, ref uv2, ref uv3);

            AddAmbientOccludedShapeVertex(p0, uv0, normal, aoNormal, -aoStepU, -aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p1, uv1, normal, aoNormal, aoStepU, -aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p2, uv2, normal, aoNormal, aoStepU, aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p3, uv3, normal, aoNormal, -aoStepU, aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddAmbientOccludedShapeVertex(
            Vector3 position,
            Vector2 uv,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 stepU,
            Vector3 stepV,
            bool tint,
            float light01,
            bool disableAO,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
            Vector2 atlasSize,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO = false)
        {
            byte aoValue = 3;
            if (!disableAO)
            {
                const float normalOffset = 0.1f;
                const float tangentOffset = 0.45f;
                Vector3 sampleSpaceOffset = new Vector3(border, 0f, border);
                Vector3 sampleOrigin = position + sampleSpaceOffset + aoNormal * normalOffset;
                bool side1 = IsAmbientOccluderAtPoint(sampleOrigin + stepU * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                bool side2 = IsAmbientOccluderAtPoint(sampleOrigin + stepV * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                bool corner = IsAmbientOccluderAtPoint(sampleOrigin + (stepU + stepV) * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                aoValue = ResolveShapeVertexAO(side1, side2, corner);
            }

            float floatTint = tint ? 1f : 0f;
            VoxelLightChannels lightChannels = ResolveActiveSpecialMeshLightChannels(light01);
            AddPackedVertex(
                position,
                normal,
                uv,
                atlasUv,
                new Vector4(lightChannels.sky, floatTint, ResolveAmbientOcclusionFactor(aoValue), 0f),
                EncodeAtlasSizeWithBlockLight(atlasSize, lightChannels),
                lightChannels.blockLightColor);
        }

        private bool IsAmbientOccluderAtPoint(
            Vector3 samplePos,
            int currentVoxelX,
            int currentVoxelY,
            int currentVoxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            in FixedList512Bytes<ShapeBox> currentShapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO)
        {
            int cellX = (int)math.floor(samplePos.x);
            int cellY = (int)math.floor(samplePos.y);
            int cellZ = (int)math.floor(samplePos.z);
            Vector3 localPos = samplePos - new Vector3(cellX, cellY, cellZ);

            if (cellX == currentVoxelX &&
                cellY == currentVoxelY &&
                cellZ == currentVoxelZ)
            {
                if (currentShape == BlockRenderShape.Ramp)
                {
                    if (suppressNeighborRampAO)
                        return false;

                    return RampShapeUtility.ContainsLocalPoint(localPos, currentPlacementAxis, currentRampVariant);
                }

                if (currentShape == BlockRenderShape.VerticalRamp)
                {
                    if (suppressNeighborRampAO)
                        return false;

                    return VerticalRampShapeUtility.ContainsLocalPoint(localPos, currentPlacementAxis);
                }

                return IsPointInsideShapeBoxes(localPos, currentShapeBoxes);
            }

            return IsAmbientOcclusionVolumeAtLocalPoint(cellX, cellY, cellZ, localPos, voxelSizeX, voxelSizeZ, voxelPlaneSize, suppressNeighborRampAO);
        }

        private bool IsAmbientOcclusionVolumeAtLocalPoint(
            int voxelX,
            int voxelY,
            int voxelZ,
            Vector3 localPos,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            bool suppressNeighborRampAO)
        {
            if (!TryGetResolvedVoxelIndex(voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return false;

            if (!solids[idx])
                return false;

            BlockType blockType = (BlockType)blockTypes[idx];
            int mapIndex = (int)blockType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            if (!CastsShapeAmbientOcclusion(blockType, mapping))
                return false;

            BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            switch (shape)
            {
                case BlockRenderShape.Cube:
                    return true;

                case BlockRenderShape.Cuboid:
                    ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
                    return IsPointInsideBox(localPos, new ShapeBox(min, max));

                case BlockRenderShape.MultiCuboid:
                {
                    BlockPlacementAxis multiAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    if (blockType == BlockType.conveyorBelt_45deg)
                    {
                        BlockPlacementAxis rampAxis = ResolveSlopedConveyorRampPlacementAxis(
                            blockTypes,
                            multiAxis,
                            voxelX,
                            voxelY,
                            voxelZ,
                            voxelSizeX,
                            voxelSizeZ,
                            voxelPlaneSize);
                        return RampShapeUtility.ContainsAmbientOcclusionPoint(localPos, rampAxis, RampShapeVariant.Straight);
                    }

                    FixedList512Bytes<ShapeBox> multiBoxes = BuildNativeMultiCuboidShapeBoxes(mapping, multiAxis);
                    return IsPointInsideShapeBoxes(localPos, multiBoxes);
                }

                case BlockRenderShape.Stairs:
                {
                    byte rawPlacementData = GetBlockPlacementAxisValue(idx);
                    StairShapeVariant variant = ResolveStairShapeVariant(rawPlacementData, voxelX, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    FixedList512Bytes<ShapeBox> stairBoxes = BuildStairVisualBoxes(rawPlacementData, variant);
                    return IsPointInsideShapeBoxes(localPos, stairBoxes);
                }

                case BlockRenderShape.Ramp:
                {
                    if (suppressNeighborRampAO)
                        return false;

                    BlockPlacementAxis rampAxis = (BlockPlacementAxis)GetBlockPlacementAxisValue(idx);
                    bool slopedConveyor = blockType == BlockType.conveyorBelt_45deg;
                    if (slopedConveyor)
                    {
                        rampAxis = ResolveSlopedConveyorRampPlacementAxis(
                            blockTypes,
                            BlockPlacementRotationUtility.SanitizeStoredAxis(rampAxis),
                            voxelX,
                            voxelY,
                            voxelZ,
                            voxelSizeX,
                            voxelSizeZ,
                            voxelPlaneSize);
                    }

                    RampShapeVariant rampVariant = slopedConveyor
                        ? RampShapeVariant.Straight
                        : ResolveRampShapeVariant(
                            rampAxis,
                            voxelX,
                            voxelY,
                            voxelZ,
                            blockTypes,
                            voxelSizeX,
                            voxelSizeZ,
                            voxelPlaneSize);
                    return RampShapeUtility.ContainsAmbientOcclusionPoint(localPos, rampAxis, rampVariant);
                }

                case BlockRenderShape.VerticalRamp:
                {
                    if (suppressNeighborRampAO)
                        return false;

                    BlockPlacementAxis verticalRampAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    return VerticalRampShapeUtility.ContainsAmbientOcclusionPoint(localPos, verticalRampAxis);
                }

                case BlockRenderShape.Fence:
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
                    FixedList512Bytes<ShapeBox> fenceBoxes = BuildFenceVisualBoxes(connectionMask);
                    return IsPointInsideShapeBoxes(localPos, fenceBoxes);
                }

                case BlockRenderShape.Fence2:
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
                    FixedList512Bytes<ShapeBox> fenceBoxes = BuildFenceVisualBoxes(connectionMask, true);
                    return IsPointInsideShapeBoxes(localPos, fenceBoxes);
                }

                case BlockRenderShape.Slab:
                {
                    BlockPlacementAxis slabAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    return IsPointInsideBox(localPos, SlabShapeUtility.GetVisualBox(slabAxis));
                }

                default:
                    ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                    return IsPointInsideBox(localPos, new ShapeBox(fallbackMin, fallbackMax));
            }
        }

        private static bool CastsShapeAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
        {
            BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            if (shape == BlockRenderShape.Cube)
                return CastsAmbientOcclusion(blockType, mapping);

            if (mapping.isEmpty ||
                mapping.isLiquid ||
                mapping.lightOpacity == 0 ||
                mapping.isTransparent ||
                !mapping.isSolid ||
                TorchPlacementUtility.IsTorchLike(blockType))
            {
                return false;
            }

            return shape != BlockRenderShape.Cross && shape != BlockRenderShape.Plane;
        }

        private static bool IsPointInsideShapeBoxes(Vector3 localPos, in FixedList512Bytes<ShapeBox> boxes)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                if (IsPointInsideBox(localPos, boxes[i]))
                    return true;
            }

            return false;
        }

        private static bool IsPointInsideBox(Vector3 localPos, ShapeBox box)
        {
            const float epsilon = 0.0001f;
            return localPos.x > box.min.x + epsilon && localPos.x < box.max.x - epsilon &&
                   localPos.y > box.min.y + epsilon && localPos.y < box.max.y - epsilon &&
                   localPos.z > box.min.z + epsilon && localPos.z < box.max.z - epsilon;
        }

        private static byte ResolveShapeVertexAO(bool side1, bool side2, bool corner)
        {
            if (side1 && side2)
                return 0;

            return (byte)(3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0));
        }


        private float ResolveAmbientOcclusionFactor(byte aoValue)
        {
            float aoCurve = aoCurveExponent > 0f ? aoCurveExponent : DefaultAOCurveExponent;
            float aoBase = aoValue / 3f;
            float aoCurved = math.pow(aoBase, aoCurve);
            float aoDarkened = 1f - (1f - aoCurved) * math.max(0f, aoStrength);
            return math.max(math.saturate(aoMinLight), math.saturate(aoDarkened));
        }

    }
}
