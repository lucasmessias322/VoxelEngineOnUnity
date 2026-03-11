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

        return GetProceduralBlockFast(worldPos, chunkCoord);
    }



    private BlockType GetProceduralBlockFast(Vector3Int worldPos, Vector2Int chunkCoord)
    {
        int worldX = worldPos.x;
        int worldZ = worldPos.z;

        int surfaceHeight = GetSurfaceHeight(worldX, worldZ);

        // Cave check (cheap fallback)
        bool isCave = false;
        bool caveYAllowed = worldPos.y <= seaLevel * maxCaveDepthMultiplier;
        if (enableCave && caveYAllowed && wormTunnelSettings.enabled)
        {
            // Worm mode: Voronoi so guia a rota do worm (sem threshold volumetrico).
            isCave = IsInsideVoronoiWormTunnel(worldPos);
        }
        else if (enableCave && caveYAllowed && caveLayers != null && caveLayers.Length > 0)
        {
            int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (worldPos.y <= maxCaveY)
            {
                int border = treeSettings.canopyRadius + 2;

                int chunkMinX = chunkCoord.x * Chunk.SizeX;
                int chunkMinZ = chunkCoord.y * Chunk.SizeZ;

                int minWorldX = chunkMinX - border;
                int minWorldZ = chunkMinZ - border;
                int minWorldY = 0;

                int stride = math.max(1, caveStride);

                int cx0 = FloorDiv(worldX - minWorldX, stride);
                int cy0 = FloorDiv(worldPos.y - minWorldY, stride);
                int cz0 = FloorDiv(worldZ - minWorldZ, stride);

                int lowWX = minWorldX + cx0 * stride;
                int highWX = lowWX + stride;

                int lowWY = minWorldY + cy0 * stride;
                int highWY = lowWY + stride;

                int lowWZ = minWorldZ + cz0 * stride;
                int highWZ = lowWZ + stride;

                float c000 = ComputeCaveNoise(lowWX, lowWY, lowWZ);
                float c100 = ComputeCaveNoise(highWX, lowWY, lowWZ);
                float c010 = ComputeCaveNoise(lowWX, highWY, lowWZ);
                float c110 = ComputeCaveNoise(highWX, highWY, lowWZ);
                float c001 = ComputeCaveNoise(lowWX, lowWY, highWZ);
                float c101 = ComputeCaveNoise(highWX, lowWY, highWZ);
                float c011 = ComputeCaveNoise(lowWX, highWY, highWZ);
                float c111 = ComputeCaveNoise(highWX, highWY, highWZ);

                float fx = (float)(worldX - lowWX) / stride;
                float fy = (float)(worldPos.y - lowWY) / stride;
                float fz = (float)(worldZ - lowWZ) / stride;

                float x00 = Mathf.Lerp(c000, c100, fx);
                float x01 = Mathf.Lerp(c001, c101, fx);
                float x10 = Mathf.Lerp(c010, c110, fx);
                float x11 = Mathf.Lerp(c011, c111, fx);

                float z0 = Mathf.Lerp(x00, x01, fz);
                float z1 = Mathf.Lerp(x10, x11, fz);

                float interpolatedCave = Mathf.Lerp(z0, z1, fy);

                float surfaceBias = 0.001f * ((float)worldPos.y / math.max(1f, (float)surfaceHeight));
                if (worldPos.y < 5) surfaceBias -= 0.08f;

                float adjustedThreshold = caveThreshold - surfaceBias;
                if (interpolatedCave < adjustedThreshold)
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
        float totalCave = 0f;
        float sumCaveAmp = 0f;

        for (int i = 0; i < caveLayers.Length; i++)
        {
            var layer = caveLayers[i];
            if (!layer.enabled) continue;

            float nx = wx + layer.offset.x;
            float ny = (float)wy;
            float nz = wz + layer.offset.y;

            float finalSample = MyNoise.OctaveVoronoi3D(nx, ny, nz, layer);

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

    private bool IsInsideVoronoiWormTunnel(Vector3Int worldPos)
    {
        if (!TryGetPrimaryCaveLayer(out NoiseLayer guideLayer)) return false;

        int maxCaveY = math.min(Chunk.SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
        if (worldPos.y <= 10 || worldPos.y > maxCaveY) return false;

        int regionSize = math.max(8, wormTunnelSettings.regionSize);
        int wormsPerRegion = math.max(1, wormTunnelSettings.wormsPerRegion);
        int minSteps = math.max(4, wormTunnelSettings.minSteps);
        int maxSteps = math.max(minSteps, wormTunnelSettings.maxSteps);
        float stepLength = math.max(0.6f, wormTunnelSettings.stepLength);
        float baseRadius = math.max(0.75f, wormTunnelSettings.baseRadius);
        float radiusJitter = math.max(0f, wormTunnelSettings.radiusJitter);
        float maxTravelPerWorm = regionSize * 1.35f;
        int cappedMaxSteps = math.max(minSteps, math.min(maxSteps, (int)math.ceil(maxTravelPerWorm / stepLength)));
        int stepRange = cappedMaxSteps - minSteps + 1;

        int chunkMinX = Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX) * Chunk.SizeX;
        int chunkMinZ = Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ) * Chunk.SizeZ;
        int chunkMaxX = chunkMinX + Chunk.SizeX - 1;
        int chunkMaxZ = chunkMinZ + Chunk.SizeZ - 1;

        int regionRing = 1;
        int regionMinX = FloorDiv(chunkMinX, regionSize) - regionRing;
        int regionMaxX = FloorDiv(chunkMaxX, regionSize) + regionRing;
        int regionMinZ = FloorDiv(chunkMinZ, regionSize) - regionRing;
        int regionMaxZ = FloorDiv(chunkMaxZ, regionSize) + regionRing;

        float3 target = new float3(worldPos.x + 0.5f, worldPos.y + 0.5f, worldPos.z + 0.5f);
        float verticalDamping = math.clamp(wormTunnelSettings.verticalDamping, 0f, 1f);

        for (int rx = regionMinX; rx <= regionMaxX; rx++)
        {
            for (int rz = regionMinZ; rz <= regionMaxZ; rz++)
            {
                for (int wormIndex = 0; wormIndex < wormsPerRegion; wormIndex++)
                {
                    uint state = WormHash4((uint)wormTunnelSettings.seed, rx, rz, wormIndex);
                    float3 startPos = SelectWormVoronoiEdgeSeed(rx, rz, regionSize, maxCaveY, guideLayer, ref state);

                    float radiusRnd = WormHash01(WormNextHash(ref state)) * 2f - 1f;
                    float radius = math.max(0.75f, baseRadius + radiusRnd * radiusJitter);

                    int steps = minSteps + (int)math.floor(WormHash01(WormNextHash(ref state)) * stepRange);

                    float3 dir = WormDirectionFromHash(WormNextHash(ref state));
                    dir.y *= verticalDamping;
                    dir = WormSafeNormalize(dir, new float3(1f, 0f, 0f));

                    if (WormPathHitsPoint(target, startPos, dir, steps, radius, maxCaveY, guideLayer, state))
                        return true;

                    if (WormPathHitsPoint(target, startPos, -dir, steps, radius, maxCaveY, guideLayer, state ^ 0x9E3779B9u))
                        return true;
                }
            }
        }

        return false;
    }

    private bool WormPathHitsPoint(
        float3 target,
        float3 startPos,
        float3 startDir,
        int steps,
        float radius,
        int maxCaveY,
        NoiseLayer guideLayer,
        uint seedState)
    {
        float stepLength = math.max(0.6f, wormTunnelSettings.stepLength);
        float edgeAttraction = math.max(0f, wormTunnelSettings.edgeAttraction);
        float tangentStrength = math.max(0f, wormTunnelSettings.tangentStrength);
        float noiseStrength = math.max(0f, wormTunnelSettings.noiseStrength);
        float verticalDamping = math.clamp(wormTunnelSettings.verticalDamping, 0f, 1f);
        float smoothing = math.clamp(wormTunnelSettings.directionSmoothing, 0.05f, 1f);

        float3 pos = startPos;
        float3 dir = WormSafeNormalize(startDir, new float3(1f, 0f, 0f));
        float radiusSq = radius * radius;
        float minY = 11f;
        float maxY = math.max(minY + 1f, maxCaveY - 1f);

        for (int step = 0; step < steps; step++)
        {
            if (math.lengthsq(pos - target) <= radiusSq)
                return true;

            float edgeDistance = WormSampleVoronoiEdgeDistance(pos, guideLayer);
            float3 gradient = WormSampleVoronoiEdgeGradient(pos, guideLayer);
            float3 gradN = WormSafeNormalize(gradient, new float3(0f, 1f, 0f));

            float3 tangent = dir - gradN * math.dot(dir, gradN);
            tangent = WormSafeNormalize(tangent, WormOrthogonal(gradN));

            float3 noiseDir = WormSampleNoiseDirection(pos, seedState, step);
            float pull = math.saturate(edgeDistance * 3.5f);
            float3 targetDir = tangent * tangentStrength
                             + noiseDir * noiseStrength
                             + (-gradN) * edgeAttraction * pull;

            targetDir.y *= verticalDamping;
            targetDir = WormSafeNormalize(targetDir, dir);

            dir = WormSafeNormalize(math.lerp(dir, targetDir, smoothing), targetDir);
            pos += dir * stepLength;
            pos.y = math.clamp(pos.y, minY, maxY);
        }

        return false;
    }

    private bool TryGetPrimaryCaveLayer(out NoiseLayer layer)
    {
        if (caveLayers != null)
        {
            for (int i = 0; i < caveLayers.Length; i++)
            {
                if (!caveLayers[i].enabled) continue;
                layer = caveLayers[i];
                return true;
            }
        }

        layer = default;
        return false;
    }

    private static float3 SelectWormVoronoiEdgeSeed(int regionX, int regionZ, int regionSize, int maxCaveY, NoiseLayer guideLayer, ref uint state)
    {
        float minY = 11f;
        float maxY = math.max(minY + 1f, maxCaveY - 1f);
        float3 best = new float3(regionX * regionSize + regionSize * 0.5f, math.lerp(minY, maxY, 0.5f), regionZ * regionSize + regionSize * 0.5f);
        float bestEdge = float.MaxValue;

        for (int i = 0; i < 6; i++)
        {
            float fx = WormHash01(WormNextHash(ref state));
            float fy = WormHash01(WormNextHash(ref state));
            float fz = WormHash01(WormNextHash(ref state));

            float3 candidate = new float3(
                regionX * regionSize + fx * regionSize,
                math.lerp(minY, maxY, fy),
                regionZ * regionSize + fz * regionSize
            );

            float edge = WormSampleVoronoiEdgeDistance(candidate, guideLayer);
            if (edge < bestEdge)
            {
                bestEdge = edge;
                best = candidate;
            }
        }

        return best;
    }

    private static float3 WormSampleNoiseDirection(float3 worldPos, uint seedState, int step)
    {
        float seedOffset = (seedState & 0xFFFFu) * 0.00091f;
        float stepOffset = step * 0.173f;

        float nx = noise.snoise(new float3(worldPos.x * 0.041f + seedOffset, worldPos.y * 0.029f + stepOffset, worldPos.z * 0.041f - seedOffset));
        float ny = noise.snoise(new float3(worldPos.x * 0.037f - seedOffset, worldPos.y * 0.033f + 13.37f + stepOffset, worldPos.z * 0.037f + seedOffset));
        float nz = noise.snoise(new float3(worldPos.x * 0.043f + 29.1f + seedOffset, worldPos.y * 0.027f + stepOffset, worldPos.z * 0.043f));

        return WormSafeNormalize(new float3(nx, ny, nz), new float3(0.707f, 0f, 0.707f));
    }

    private static float WormSampleVoronoiEdgeDistance(float3 worldPos, NoiseLayer guideLayer)
    {
        float scale = math.max(1e-4f, guideLayer.scale);
        float verticalScale = guideLayer.verticalScale > 0f ? guideLayer.verticalScale : scale;

        float3 p = new float3(
            (worldPos.x + guideLayer.offset.x) / scale,
            worldPos.y / verticalScale,
            (worldPos.z + guideLayer.offset.y) / scale
        );

        float2 cell = noise.cellular(p);
        return math.max(0f, cell.y - cell.x);
    }

    private static float3 WormSampleVoronoiEdgeGradient(float3 worldPos, NoiseLayer guideLayer)
    {
        const float delta = 1.25f;
        float3 dx = new float3(delta, 0f, 0f);
        float3 dy = new float3(0f, delta, 0f);
        float3 dz = new float3(0f, 0f, delta);

        float gx = WormSampleVoronoiEdgeDistance(worldPos + dx, guideLayer) - WormSampleVoronoiEdgeDistance(worldPos - dx, guideLayer);
        float gy = WormSampleVoronoiEdgeDistance(worldPos + dy, guideLayer) - WormSampleVoronoiEdgeDistance(worldPos - dy, guideLayer);
        float gz = WormSampleVoronoiEdgeDistance(worldPos + dz, guideLayer) - WormSampleVoronoiEdgeDistance(worldPos - dz, guideLayer);

        return new float3(gx, gy, gz) * (0.5f / delta);
    }

    private static float3 WormDirectionFromHash(uint hash)
    {
        float x = WormHash01(math.hash(new uint2(hash, 0xA341316Cu))) * 2f - 1f;
        float y = WormHash01(math.hash(new uint2(hash, 0xC8013EA4u))) * 2f - 1f;
        float z = WormHash01(math.hash(new uint2(hash, 0xAD90777Du))) * 2f - 1f;
        return WormSafeNormalize(new float3(x, y, z), new float3(1f, 0f, 0f));
    }

    private static float3 WormOrthogonal(float3 v)
    {
        float3 axis = math.abs(v.y) < 0.95f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
        return WormSafeNormalize(math.cross(v, axis), new float3(1f, 0f, 0f));
    }

    private static float3 WormSafeNormalize(float3 v, float3 fallback)
    {
        float lenSq = math.lengthsq(v);
        if (lenSq <= 1e-8f) return fallback;
        return v * math.rsqrt(lenSq);
    }

    private static uint WormHash4(uint seed, int a, int b, int c)
    {
        return math.hash(new uint4(seed, (uint)a, (uint)b, (uint)c));
    }

    private static uint WormNextHash(ref uint state)
    {
        state = math.hash(new uint2(state, 0x9E3779B9u));
        return state;
    }

    private static float WormHash01(uint h)
    {
        return (h & 0x00FFFFFFu) * (1f / 16777215f);
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



    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
        return q;
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

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, type);

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

    private void ApplyBlockToLoadedChunkCache(Vector3Int worldPos, Vector2Int chunkCoord, BlockType type)
    {
        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk)) return;
        if (!chunk.hasVoxelData || !chunk.voxelData.IsCreated) return;

        int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        int ly = worldPos.y;

        if (lx < 0 || lx >= Chunk.SizeX || lz < 0 || lz >= Chunk.SizeZ || ly < 0 || ly >= Chunk.SizeY)
            return;

        int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
        chunk.voxelData[idx] = (byte)type;
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
