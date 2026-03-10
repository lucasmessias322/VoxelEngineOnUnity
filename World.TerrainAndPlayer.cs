using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    #region Helpers: Padding, GetBlock, Procedural Fallback


    public NativeArray<byte> GetPaddedVoxelData(int chunkX, int chunkZ)
    {
        int sizeX = Chunk.SizeX;
        int sizeY = Chunk.SizeY;
        int sizeZ = Chunk.SizeZ;
        int border = treeSettings.canopyRadius + 1;
        int padX = sizeX + border;
        int padZ = sizeZ + border;

        NativeArray<byte> paddedData = new NativeArray<byte>(padX * sizeY * padZ, Allocator.TempJob);

        for (int z = -border; z < sizeZ + border; z++)
        {
            for (int x = -border; x < sizeX + border; x++)
            {
                int currentCX = chunkX;
                int currentCZ = chunkZ;
                int readX = x;
                int readZ = z;

                if (x < 0) { currentCX--; readX = sizeX - 1; }
                else if (x >= sizeX) { currentCX++; readX = 0; }

                if (z < 0) { currentCZ--; readZ = sizeZ - 1; }
                else if (z >= sizeZ) { currentCZ++; readZ = 0; }

                if (activeChunks.TryGetValue(new Vector2Int(currentCX, currentCZ), out Chunk c))
                {
                    if (c.hasVoxelData)
                    {
                        for (int y = 0; y < sizeY; y++)
                        {
                            int srcIdx = readX + readZ * sizeX + y * sizeX * sizeZ;
                            int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                            paddedData[dstIdx] = c.voxelData[srcIdx];
                        }
                        continue;
                    }
                }

                for (int y = 0; y < sizeY; y++)
                {
                    int dstIdx = (x + 1) + (z + 1) * padX + y * padX * padZ;
                    paddedData[dstIdx] = 0;
                }
            }
        }

        return paddedData;
    }



    public BlockType GetBlockAt(Vector3Int worldPos)
    {
        if (blockOverrides.TryGetValue(worldPos, out BlockType overridden))
            return overridden;

        if (worldPos.y < 0) return BlockType.Air;
        if (worldPos.y >= Chunk.SizeY) return BlockType.Air;
        if (worldPos.y <= 2) return BlockType.Bedrock;

        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.hasVoxelData)
        {
            int lx = worldX - chunkCoord.x * Chunk.SizeX;
            int lz = worldZ - chunkCoord.y * Chunk.SizeZ;
            int ly = worldPos.y;

            if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && ly >= 0 && ly < Chunk.SizeY)
            {
                int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
                return (BlockType)chunk.voxelData[idx];
            }
        }

        return GetProceduralBlockFast(worldPos);
    }



    private BlockType GetProceduralBlockFast(Vector3Int worldPos)
    {
        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        int surfaceHeight = GetSurfaceHeight(worldX, worldZ);

        // Cave check (cheap fallback)
        bool isCave = false;
        bool canEvaluateWormCaves = caveWormSettings.enabled && worldPos.y < surfaceHeight;
        bool canEvaluateClassicCaves = !caveWormSettings.enabled && worldPos.y <= seaLevel * maxCaveDepthMultiplier;
        if (caveLayers != null && caveLayers.Length > 0 && enableCave && (canEvaluateWormCaves || canEvaluateClassicCaves))
        {
            int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (caveWormSettings.enabled)
            {
                isCave = IsWormCarvedAt(worldPos, surfaceHeight, maxCaveY);
            }
            else if (worldPos.y <= maxCaveY)
            {
                float caveSample = ComputeCaveNoise(worldX, worldPos.y, worldZ);
                if (caveSample < caveThreshold)
                    isCave = true;
            }
        }

        if (isCave) return BlockType.Air;

        if (worldPos.y > surfaceHeight)
            return (worldPos.y <= seaLevel) ? BlockType.Water : BlockType.Air;

        bool isBeachArea = (surfaceHeight <= seaLevel + 2);
        bool isCliff = IsCliff(worldX, worldZ, CliffTreshold);
        bool isHighMountain = surfaceHeight >= baseHeight + 70;

        if (worldPos.y == surfaceHeight)
        {
            if (isHighMountain || isCliff) return BlockType.Stone;
            return isBeachArea ? BlockType.Sand : BlockType.Grass;
        }
        else if (worldPos.y > surfaceHeight - 4)
        {
            return (isCliff || isHighMountain) ? BlockType.Stone : (isBeachArea ? BlockType.Sand : BlockType.Dirt);
        }
        else if (worldPos.y <= 2)
            return BlockType.Bedrock;
        else if (worldPos.y > surfaceHeight - 50)
            return BlockType.Stone;
        else
            return BlockType.Deepslate;
    }

    #endregion

    #region Noise & Height Helpers


    private float ComputeCaveNoise(int wx, int wy, int wz)
    {
        return ComputeCaveNoise((float)wx, wy, (float)wz);
    }

    private float ComputeCaveNoise(float wx, float wy, float wz)
    {
        float totalCave = 0f;
        float sumCaveAmp = 0f;

        for (int i = 0; i < caveLayers.Length; i++)
        {
            var layer = caveLayers[i];
            if (!layer.enabled) continue;

            float nx = wx + layer.offset.x;
            float ny = wy;
            float nz = wz + layer.offset.y;

            float finalSample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);

            if (layer.redistributionModifier != 1f || layer.exponent != 1f)
            {
                finalSample = MyNoise.Redistribution(finalSample, layer.redistributionModifier, layer.exponent);
            }

            totalCave += finalSample * layer.amplitude;
            sumCaveAmp += math.max(1e-5f, layer.amplitude);
        }

        float baseCaveResult = (sumCaveAmp > 0f) ? totalCave / sumCaveAmp : 0f;
        return baseCaveResult;
    }

    private bool IsWormCarvedAt(Vector3Int worldPos, int surfaceHeight, int maxCaveY)
    {
        int minCaveY = math.clamp(caveWormSettings.minY, 11, math.max(11, maxCaveY - 2));
        if (worldPos.y <= 10 || worldPos.y >= surfaceHeight - 1) return false;

        int cellSize = math.max(8, caveWormSettings.cellSize);
        int carveStride = math.max(1, caveWormSettings.carveStride);
        float spawnChance = math.clamp(caveWormSettings.spawnChance, 0f, 1f);
        int minSteps = math.max(8, caveWormSettings.minSteps);
        int maxSteps = math.max(minSteps, caveWormSettings.maxSteps);
        float stepLength = math.max(0.5f, caveWormSettings.stepLength);
        float minRadius = math.max(0.75f, caveWormSettings.minRadius);
        float maxRadius = math.max(minRadius, caveWormSettings.maxRadius);
        float verticalRadiusMultiplier = math.clamp(caveWormSettings.verticalRadiusMultiplier, 0.25f, 1.5f);
        float directionNoiseScale = math.max(0.001f, caveWormSettings.directionNoiseScale);
        float boundaryPullStrength = math.max(0.05f, caveWormSettings.boundaryPullStrength);
        float boundaryBand = math.max(0.005f, caveWormSettings.boundaryBand);
        float verticalJitter = math.clamp(caveWormSettings.verticalJitter, 0f, 1f);
        int boundarySearchIters = math.clamp(caveWormSettings.boundarySearchIters, 1, 8);

        float maxReach = maxSteps * stepLength + maxRadius + 4f;
        int seedPadding = (int)math.ceil(maxReach);

        int cellX0 = FloorDiv(worldPos.x - seedPadding, cellSize);
        int cellX1 = FloorDiv(worldPos.x + seedPadding, cellSize);
        int cellZ0 = FloorDiv(worldPos.z - seedPadding, cellSize);
        int cellZ1 = FloorDiv(worldPos.z + seedPadding, cellSize);
        int yRange = math.max(1, maxCaveY - minCaveY);

        for (int cellX = cellX0; cellX <= cellX1; cellX++)
        {
            for (int cellZ = cellZ0; cellZ <= cellZ1; cellZ++)
            {
                uint spawnHash = math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 9127));
                if (Hash01(spawnHash) > spawnChance) continue;

                float startOffsetX = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 1223)));
                float startOffsetZ = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 2141)));
                float startOffsetY = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 3319)));

                float startX = cellX * cellSize + startOffsetX * cellSize;
                float startZ = cellZ * cellSize + startOffsetZ * cellSize;
                float startY = minCaveY + startOffsetY * yRange;
                int startSurfaceY = GetSurfaceHeight((int)math.floor(startX), (int)math.floor(startZ));
                int cellMaxCaveY = math.min(Chunk.SizeY - 1, math.max(maxCaveY, startSurfaceY - 1));
                if (cellMaxCaveY <= minCaveY + 1) continue;

                int steps = minSteps + (int)math.floor(Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 4019))) * (maxSteps - minSteps + 1));
                float yaw = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 5171))) * math.PI * 2f;
                float pitch = HashSigned(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 6131))) * verticalJitter;

                float3 pos = new float3(startX, startY, startZ);
                ProjectToBoundary(ref pos, boundaryBand, boundaryPullStrength, boundarySearchIters, minCaveY, cellMaxCaveY);
                if (math.abs(ComputeCaveSignedNoise(pos.x, pos.y, pos.z)) > boundaryBand * 5f) continue;

                float3 dir = math.normalizesafe(new float3(math.cos(yaw), pitch, math.sin(yaw)), new float3(1f, 0f, 0f));
                int stepsSinceLastCheck = 0;

                for (int step = 0; step < steps; step++)
                {
                    float t = step / math.max(1f, steps - 1f);
                    float tunnelShape = math.sin(t * math.PI);
                    float radiusNoise = HashSigned(math.hash(new int4(cellX ^ step, cellZ + step * 17, caveWormSettings.seed, 7331)));
                    float radiusBlend = math.saturate(0.35f + 0.65f * tunnelShape + radiusNoise * 0.12f);
                    float radius = math.lerp(minRadius, maxRadius, radiusBlend);
                    stepsSinceLastCheck++;

                    bool shouldCheck = step == 0 || stepsSinceLastCheck >= carveStride || step == steps - 1;
                    if (shouldCheck)
                    {
                        float bridgeRadius = stepLength * math.max(0, stepsSinceLastCheck - 1) * 0.35f;
                        float checkRadius = radius + bridgeRadius;

                        if (IsPointInsideEllipsoid(worldPos, pos, checkRadius, verticalRadiusMultiplier))
                        {
                            float radiusY = math.max(0.6f, checkRadius * verticalRadiusMultiplier);
                            int topLimit = GetWormTopCarveLimit(worldPos.x, worldPos.z, surfaceHeight, pos.y, radiusY);
                            if (worldPos.y <= math.min(cellMaxCaveY, topLimit))
                                return true;
                        }

                        stepsSinceLastCheck = 0;
                    }

                    float signed = ComputeCaveSignedNoise(pos.x, pos.y, pos.z);
                    float3 grad = EstimateCaveGradient(pos.x, pos.y, pos.z);
                    float3 gradDir = math.normalizesafe(grad, new float3(0f, 0f, 0f));
                    if (math.lengthsq(gradDir) > 1e-5f)
                    {
                        pos -= gradDir * signed * boundaryPullStrength * 2f;
                    }

                    float3 flow = SampleDirectionFlow(pos, directionNoiseScale, caveWormSettings.seed);
                    flow.y *= verticalJitter;
                    dir = math.normalizesafe(dir * 0.82f + flow * 0.18f, dir);
                    pos += dir * stepLength;

                    if (pos.y < minCaveY + 0.5f)
                    {
                        pos.y = minCaveY + 0.5f;
                        dir.y = math.abs(dir.y);
                    }
                    else if (pos.y > cellMaxCaveY - 0.5f)
                    {
                        pos.y = cellMaxCaveY - 0.5f;
                        dir.y = -math.abs(dir.y);
                    }
                }
            }
        }

        return false;
    }

    private bool IsPointInsideEllipsoid(Vector3Int point, float3 center, float radiusXZ, float verticalRadiusMultiplier)
    {
        float radiusY = math.max(0.6f, radiusXZ * verticalRadiusMultiplier);
        float dx = (point.x + 0.5f - center.x) / math.max(0.001f, radiusXZ);
        float dy = (point.y + 0.5f - center.y) / math.max(0.001f, radiusY);
        float dz = (point.z + 0.5f - center.z) / math.max(0.001f, radiusXZ);
        return dx * dx + dy * dy + dz * dz <= 1f;
    }

    private void ProjectToBoundary(ref float3 pos, float boundaryBand, float boundaryPullStrength, int searchIters, int minCaveY, int maxCaveY)
    {
        for (int i = 0; i < searchIters; i++)
        {
            float signed = ComputeCaveSignedNoise(pos.x, pos.y, pos.z);
            if (math.abs(signed) <= boundaryBand) break;

            float3 grad = EstimateCaveGradient(pos.x, pos.y, pos.z);
            float3 gradDir = math.normalizesafe(grad, new float3(0f, 0f, 0f));
            if (math.lengthsq(gradDir) < 1e-5f) break;

            pos -= gradDir * signed * boundaryPullStrength * 2.6f;
            pos.y = math.clamp(pos.y, minCaveY + 0.5f, maxCaveY - 0.5f);
        }
    }

    private float3 SampleDirectionFlow(float3 pos, float scale, int seed)
    {
        float sx = seed * 0.00117f;
        float sy = seed * -0.00091f;
        float sz = seed * 0.00063f;
        float3 p = pos * scale;

        float nx = noise.snoise(new float3(p.x + sx + 11.1f, p.y + sy, p.z + sz));
        float ny = noise.snoise(new float3(p.x + sx - 7.3f, p.y + sy + 21.7f, p.z + sz + 5.9f));
        float nz = noise.snoise(new float3(p.x + sx + 19.4f, p.y + sy - 13.5f, p.z + sz - 17.2f));

        return math.normalizesafe(new float3(nx, ny, nz), new float3(1f, 0f, 0f));
    }

    private float3 EstimateCaveGradient(float worldX, float worldY, float worldZ)
    {
        const float eps = 1.25f;

        float dx = ComputeCaveSignedNoise(worldX + eps, worldY, worldZ) - ComputeCaveSignedNoise(worldX - eps, worldY, worldZ);
        float dy = ComputeCaveSignedNoise(worldX, worldY + eps, worldZ) - ComputeCaveSignedNoise(worldX, worldY - eps, worldZ);
        float dz = ComputeCaveSignedNoise(worldX, worldY, worldZ + eps) - ComputeCaveSignedNoise(worldX, worldY, worldZ - eps);

        return new float3(dx, dy, dz);
    }

    private float ComputeCaveSignedNoise(float worldX, float worldY, float worldZ)
    {
        return ComputeCaveNoise(worldX, worldY, worldZ) - caveThreshold;
    }

    private int GetWormTopCarveLimit(int worldX, int worldZ, int surfaceY, float centerY, float radiusY)
    {
        int closedLimit = surfaceY - 2;
        bool nearSurface = (surfaceY - centerY) <= (radiusY + 1.2f);
        if (!nearSurface) return closedLimit;

        float mask = noise.snoise(new float2(
            (worldX + caveWormSettings.seed * 0.37f) * 0.08f,
            (worldZ - caveWormSettings.seed * 0.21f) * 0.08f
        )) * 0.5f + 0.5f;

        return mask > 0.68f ? surfaceY : closedLimit;
    }

    private static float Hash01(uint hash)
    {
        return (hash & 0x00FFFFFFu) / 16777216f;
    }

    private static float HashSigned(uint hash)
    {
        return Hash01(hash) * 2f - 1f;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
        return q;
    }


    private int GetSurfaceHeight(int worldX, int worldZ)
    {
        float warpX = 0f;
        float warpZ = 0f;
        float sumWarpAmp = 0f;
        if (warpLayers != null)
        {
            for (int i = 0; i < warpLayers.Length; i++)
            {
                var layer = warpLayers[i];
                if (!layer.enabled) continue;

                float baseNx = worldX + layer.offset.x;
                float baseNz = worldZ + layer.offset.y;

                float sampleX = MyNoise.OctavePerlin(baseNx + 100f, baseNz, layer);
                float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);

                warpX += (sampleX * 2f - 1f) * layer.amplitude;
                warpZ += (sampleZ * 2f - 1f) * layer.amplitude;
                sumWarpAmp += layer.amplitude;
            }
        }
        if (sumWarpAmp > 0f)
        {
            warpX /= sumWarpAmp;
            warpZ /= sumWarpAmp;
        }
        warpX = (warpX - 0.5f) * 2f;
        warpZ = (warpZ - 0.5f) * 2f;

        float totalNoise = 0f;
        float sumAmp = 0f;
        bool hasActiveLayers = false;
        if (noiseLayers != null)
        {
            for (int i = 0; i < noiseLayers.Length; i++)
            {
                var layer = noiseLayers[i];
                if (!layer.enabled) continue;

                hasActiveLayers = true;

                float nx = (worldX + warpX) + layer.offset.x;
                float nz = (worldZ + warpZ) + layer.offset.y;

                float sample = MyNoise.OctavePerlin(nx, nz, layer);

                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                {
                    sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                }

                totalNoise += sample * layer.amplitude;
                sumAmp += math.max(1e-5f, layer.amplitude);
            }
        }

        if (!hasActiveLayers || sumAmp <= 0f)
        {
            float nx = (worldX + warpX) * 0.05f + offsetX;
            float nz = (worldZ + warpZ) * 0.05f + offsetZ;
            totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
            sumAmp = 1f;
        }

        return GetHeightFromNoise(totalNoise, sumAmp);
    }


    private int GetHeightFromNoise(float noiseValue, float sumAmp)
    {
        float centered = noiseValue - sumAmp * 0.5f;
        return math.clamp(baseHeight + (int)math.floor(centered), 1, Chunk.SizeY - 1);
    }




    private bool IsCliff(int worldX, int worldZ, int threshold = 2)
    {
        int h = GetSurfaceHeight(worldX, worldZ);

        int hN = GetSurfaceHeight(worldX, worldZ + 1);
        int hS = GetSurfaceHeight(worldX, worldZ - 1);
        int hE = GetSurfaceHeight(worldX + 1, worldZ);
        int hW = GetSurfaceHeight(worldX - 1, worldZ);

        int maxDiff = 0;
        maxDiff = math.max(maxDiff, math.abs(h - hN));
        maxDiff = math.max(maxDiff, math.abs(h - hS));
        maxDiff = math.max(maxDiff, math.abs(h - hE));
        maxDiff = math.max(maxDiff, math.abs(h - hW));

        return maxDiff >= threshold;
    }


    #endregion

    #region Lighting System (Global BFS)

    private bool IsChunkLoaded(Vector2Int coord)
    {
        return activeChunks.TryGetValue(coord, out Chunk chunk) && chunk.hasVoxelData;
    }


    #endregion

    #region Player Actions

    public bool IsGrassBillboardSuppressed(Vector3Int billboardPos)
    {
        return suppressedGrassBillboards.Contains(billboardPos);
    }

    public void SuppressGrassBillboardAt(Vector3Int billboardPos)
    {
        if (billboardPos.y <= 0) return;
        if (!suppressedGrassBillboards.Add(billboardPos)) return;

        Vector2Int coord = new Vector2Int(
            Mathf.FloorToInt((float)billboardPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)billboardPos.z / Chunk.SizeZ)
        );

        RequestChunkRebuild(coord);
    }

    private bool TrySetBlockInLoadedChunkCache(Vector3Int worldPos, BlockType type)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY) return false;

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk) ||
            chunk == null ||
            !chunk.hasVoxelData ||
            !chunk.voxelData.IsCreated)
        {
            return false;
        }

        int localX = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int localZ = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        if (localX < 0 || localX >= Chunk.SizeX || localZ < 0 || localZ >= Chunk.SizeZ)
            return false;

        int idx = localX + localZ * Chunk.SizeX + worldPos.y * Chunk.SizeX * Chunk.SizeZ;
        chunk.voxelData[idx] = (byte)type;
        return true;
    }


    public void SetBlockAt(Vector3Int worldPos, BlockType type)
    {
        if (worldPos.y <= 2)
        {
            Debug.Log("Tentativa de modificar Bedrock/abaixo ignorada: " + worldPos);
            return;
        }

        BlockType current = GetBlockAt(worldPos);
        if (current == BlockType.Bedrock)
        {
            Debug.Log("Tentativa de modificar Bedrock ignorada: " + worldPos);
            return;
        }

        if (current == type) return;

        // If this position gets occupied, it cannot host a billboard anymore.
        if (type != BlockType.Air)
            suppressedGrassBillboards.Remove(worldPos);

        // If ground changes from grass to anything else, clear suppression above it.
        if (type != BlockType.Grass)
            suppressedGrassBillboards.Remove(new Vector3Int(worldPos.x, worldPos.y + 1, worldPos.z));

        // Keep explicit Air overrides so broken procedural terrain stays removed.
        // Removing the key would make GetBlockAt() fall back to procedural data again.
        blockOverrides[worldPos] = type;
        TrySetBlockInLoadedChunkCache(worldPos, type);

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        HashSet<Vector2Int> chunksToRebuild = new HashSet<Vector2Int>();
        chunksToRebuild.Add(chunkCoord);

        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);

        if (localX == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.left);
        if (localX == Chunk.SizeX - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.right);
        if (localZ == 0) chunksToRebuild.Add(chunkCoord + Vector2Int.down);
        if (localZ == Chunk.SizeZ - 1) chunksToRebuild.Add(chunkCoord + Vector2Int.up);

        // Fora da altura simulada por chunk, mantemos apenas override:
        // evita custo de light propagation/rebuild de terrain data que nao cobre esse Y.
        if (worldPos.y >= Chunk.SizeY)
        {
            IndexHighOverride(worldPos, chunkCoord, type);

            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);

            if (worldPos.y == Chunk.SizeY || worldPos.y == Chunk.SizeY + 1)
            {
                foreach (Vector2Int coord in chunksToRebuild)
                    RequestChunkRebuild(coord);
            }
            return;
        }

        byte newEmission = GetBlockEmission(type);
        byte oldEmission = GetBlockEmission(current);
        byte newOpacity = GetBlockOpacity(type);
        byte oldOpacity = GetBlockOpacity(current);

        if (newEmission > 0)
        {
            PropagateLightGlobal(worldPos, newEmission);
        }
        else if (oldEmission > 0)
        {
            RemoveLightGlobal(worldPos);
        }
        else
        {
            bool becameOpaque = oldOpacity < 15 && newOpacity >= 15;
            bool becameTransparent = oldOpacity >= 15 && newOpacity < 15;

            if (becameOpaque)
            {
                RemoveLightGlobal(worldPos);
            }
            else if (becameTransparent)
            {
                RefillLightGlobal(worldPos);
            }
        }

        foreach (Vector2Int coord in chunksToRebuild)
        {
            RequestChunkRebuild(coord);
        }

        // Mudanca no topo do chunk pode expor/ocultar a face inferior de construcoes altas.
        if (worldPos.y >= Chunk.SizeY - 1)
        {
            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);
        }
    }

    #endregion

    #region Frustum / Vertical Culling


    private void UpdateVerticalSubchunkVisibility()
    {
        if (player == null) return;

        Vector2Int playerChunkCoord = new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ)
        );

        int playerSubchunkY = Mathf.FloorToInt(player.position.y / Chunk.SubchunkHeight);

        foreach (var kvp in activeChunks)
        {
            Chunk chunk = kvp.Value;
            if (chunk.state != Chunk.ChunkState.Active || chunk.subchunks == null) continue;

            Vector2Int chunkCoord = kvp.Key;

            bool isWithinFullVisibilityRadius =
                Mathf.Abs(chunkCoord.x - playerChunkCoord.x) <= horizontalFullVisibilityRadius &&
                Mathf.Abs(chunkCoord.y - playerChunkCoord.y) <= horizontalFullVisibilityRadius;

            for (int subIdx = 0; subIdx < Chunk.SubchunksPerColumn; subIdx++)
            {
                if (isWithinFullVisibilityRadius)
                {
                    chunk.subchunks[subIdx].SetVisible(true);
                }
                else
                {
                    int verticalDelta = subIdx - playerSubchunkY;
                    bool shouldBeVisible = verticalDelta >= 0
                        ? verticalDelta <= verticalSubchunkRenderDistanceAbove
                        : (-verticalDelta) <= verticalSubchunkRenderDistanceBelow;
                    chunk.subchunks[subIdx].SetVisible(shouldBeVisible);
                }
            }
        }
    }


    #endregion


}
