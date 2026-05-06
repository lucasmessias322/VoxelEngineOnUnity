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
        private void TryAddHighQualityLeafFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            NativeArray<ushort> light,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);
            float chanceRoll = (variationHash & 0x0000FFFFu) / 65535f;
            float spawnChance = math.saturate(leafFoliageSpawnChance);
            if (chanceRoll > spawnChance)
                return;

            float heightMin = math.max(0.2f, math.min(leafFoliageHeightMin, leafFoliageHeightMax));
            float heightMax = math.max(heightMin, math.max(leafFoliageHeightMin, leafFoliageHeightMax));
            float halfWidthMin = math.max(0.5f, math.min(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float halfWidthMax = math.max(halfWidthMin, math.max(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float baseYOffsetMin = math.min(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float baseYOffsetMax = math.max(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float centerJitter = math.max(0f, leafFoliageCenterJitter);

            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 11, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 17, centerJitter);
            float baseYOffset = math.lerp(baseYOffsetMin, baseYOffsetMax, ((variationHash >> 16) & 0xFFu) / 255f);
            float height = math.lerp(heightMin, heightMax, ((variationHash >> 24) & 0xFFu) / 255f);
            float halfWidth = math.lerp(halfWidthMin, halfWidthMax, ((variationHash >> 8) & 0xFFu) / 255f);

            VoxelLightChannels billboardLight = GetSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Mantem o centro do billboard no centro do voxel para as quads cruzarem pelas diagonais do bloco.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardCross(center, height, halfWidth, atlasUv, atlasSize, billboardLight.sky, billboardLight.block, billboardLight.blockLightColor, tint);
        }

        private void AddUltraLeafBillboardFoliage(
            int x,
            int y,
            int z,
            NativeArray<ushort> light,
            int voxelSizeX,
            int voxelSizeZ,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);

            float centerJitter = math.max(0f, leafUltraCenterJitter);
            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 5, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 13, centerJitter);
            float height = math.max(0.4f, leafUltraBillboardHeight);
            float halfWidth = math.max(0.5f, leafUltraBillboardHalfWidth);
            float baseYOffset = math.clamp(leafUltraBaseYOffset, -0.4f, 0.4f);
            float baseRotationDeg = math.clamp(leafUltraRotationOffsetDegrees, 0f, 45f);
            float randomRotationRangeDeg = math.max(0f, leafUltraRotationRandomDegrees);
            float baseTiltDeg = math.clamp(leafUltraFaceTiltDegrees, 0f, 60f);
            float randomTiltRangeDeg = math.max(0f, leafUltraFaceTiltRandomDegrees);
            float random01 = ((variationHash >> 20) & 0x3FFu) / 1023f;
            float rotationDeg = baseRotationDeg + (random01 * 2f - 1f) * randomRotationRangeDeg;

            VoxelLightChannels billboardLight = GetSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Pivot no centro do voxel para manter volume aparente de qualquer angulo.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + 0.5f + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardFourFaces(center, height, halfWidth, atlasUv, atlasSize, billboardLight.sky, billboardLight.block, billboardLight.blockLightColor, tint, rotationDeg, baseTiltDeg, randomTiltRangeDeg, variationHash);
        }

        private void AddBillboardFourFaces(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint,
            float rotationOffsetDegrees,
            float tiltBaseDegrees,
            float tiltRandomDegrees,
            uint variationHash)
        {
            float baseRad = math.radians(rotationOffsetDegrees);
            for (int i = 0; i < 4; i++)
            {
                float angle = baseRad + i * (math.PI * 0.25f);
                Vector2 dir = new Vector2(math.cos(angle), math.sin(angle));

                float tiltNoise = ((variationHash >> (i * 8)) & 0xFFu) / 255f;
                float tiltDegrees = tiltBaseDegrees + (tiltNoise * 2f - 1f) * tiltRandomDegrees;
                if ((i & 1) != 0)
                    tiltDegrees = -tiltDegrees;

                AddBillboardPlane(center, height, halfWidth, dir, tiltDegrees, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
            }
        }

        private void AddBillboardPlane(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 horizontalDirectionXZ,
            float tiltDegrees,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint)
        {
            Vector3 right = new Vector3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y) * halfWidth;
            Vector3 up = Vector3.up;

            if (math.abs(tiltDegrees) > 0.001f)
            {
                float3 axis = math.normalize(new float3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y));
                quaternion tilt = quaternion.AxisAngle(axis, math.radians(tiltDegrees));
                float3 tiltedUp = math.mul(tilt, new float3(0f, 1f, 0f));
                up = new Vector3(tiltedUp.x, tiltedUp.y, tiltedUp.z);
            }

            Vector3 halfUp = up * (height * 0.5f);
            Vector3 p0 = center - right - halfUp;
            Vector3 p1 = center + right - halfUp;
            Vector3 p2 = center + right + halfUp;
            Vector3 p3 = center - right + halfUp;
            AddDoubleSidedQuad(p0, p1, p2, p3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
        }

        private bool TryResolveVegetationBillboardRule(
            BlockType groundBlockType,
            int worldX,
            int worldY,
            int worldZ,
            float noiseScale,
            out BlockType billboardBlockType,
            out uint variationHash)
        {
            billboardBlockType = BlockType.Air;
            variationHash = 0u;

            if (IsSuppressedGrassBillboard(worldX, worldY, worldZ))
                return false;

            return VegetationBillboardUtility.TryResolveBillboardRule(
                biomeNoiseSettings,
                vegetationBillboardRules,
                worldX,
                worldY,
                worldZ,
                groundBlockType,
                grassBillboardChance,
                noiseScale,
                grassBillboardBlockType,
                out billboardBlockType,
                out variationHash);
        }

        private void AddBillboardCross(Vector3 center, float height, float halfWidth, Vector2 atlasUv, Vector2 atlasSize, float skyLight01, float blockLight01, uint blockLightColor, float tint)
        {
            Vector3 a0 = center + new Vector3(-halfWidth, 0f, -halfWidth);
            Vector3 a1 = center + new Vector3(halfWidth, 0f, halfWidth);
            Vector3 a2 = a1 + new Vector3(0f, height, 0f);
            Vector3 a3 = a0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(a0, a1, a2, a3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);

            Vector3 b0 = center + new Vector3(-halfWidth, 0f, halfWidth);
            Vector3 b1 = center + new Vector3(halfWidth, 0f, -halfWidth);
            Vector3 b2 = b1 + new Vector3(0f, height, 0f);
            Vector3 b3 = b0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(b0, b1, b2, b3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
        }

        private bool IsSuppressedGrassBillboard(int worldX, int worldY, int worldZ)
        {
            for (int i = 0; i < suppressedGrassBillboards.Length; i++)
            {
                int3 p = suppressedGrassBillboards[i];
                if (p.x == worldX && p.y == worldY && p.z == worldZ)
                    return true;
            }
            return false;
        }

        private static Vector3 ComputeQuadPlaneNormal(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            float3 edgeA = new float3(p1.x - p0.x, p1.y - p0.y, p1.z - p0.z);
            float3 edgeB = new float3(p2.x - p0.x, p2.y - p0.y, p2.z - p0.z);
            float3 n = math.cross(edgeA, edgeB);
            float lenSq = math.lengthsq(n);
            if (lenSq <= 1e-8f)
                return new Vector3(0f, 1f, 0f);

            float invLen = math.rsqrt(lenSq);
            n *= invLen;
            return new Vector3(n.x, n.y, n.z);
        }

        private void AddDoubleSidedQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint)
        {
            int vIndex = GetCurrentSliceVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);

            Vector4 e = new Vector4(math.saturate(skyLight01), tint, 1f, 0f);
            Vector4 atlasAndBlockLight = new Vector4(atlasSize.x, atlasSize.y, math.saturate(blockLight01), 0f);
            AddPackedVertex(p0, planeNormal, new Vector2(0f, 0f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p1, planeNormal, new Vector2(1f, 0f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p2, planeNormal, new Vector2(1f, 1f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p3, planeNormal, new Vector2(0f, 1f), atlasUv, e, atlasAndBlockLight, blockLightColor);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 3);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 3);
            billboardTriangles.Add(vIndex + 2);
        }

    }
}
