using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public partial class World
{
    private readonly Vector3Int[] sixDirections = new Vector3Int[]
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    #region Light Helpers

    public byte GetBlockOpacity(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return BlockShapeUtility.GetEffectiveLightOpacity(blockData.mappings[t]);
        }
        return 15;
    }

    public byte GetBlockEmission(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length) return blockData.mappings[t].lightEmission;
        }
        return 0;
    }

    public ushort GetBlockEmissionPacked(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length)
            {
                BlockTextureMapping mapping = blockData.mappings[t];
                return LightUtils.PackEmission(mapping.lightEmission, mapping.lightColor);
            }
        }
        return 0;
    }

    public Color GetBlockEmissionColor(BlockType type)
    {
        if (blockData != null && blockData.mappings != null)
        {
            int t = (int)type;
            if (t >= 0 && t < blockData.mappings.Length)
            {
                Color color = blockData.mappings[t].lightColor;
                float maxComponent = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
                return maxComponent > 0.001f ? color : Color.white;
            }
        }
        return Color.white;
    }

    private ushort GetRuntimeBlockEmissionPacked(Vector3Int worldPos, BlockType type)
    {
        ushort emission = GetBlockEmissionPacked(type);
        if (poweredElectricalEmissionByPosition.TryGetValue(worldPos, out ushort poweredEmission))
            emission = LightUtils.MaxBlockLight(emission, poweredEmission);

        return emission;
    }

    private static ushort GetOverlappingBlockLight(ushort currentLight, ushort candidateLight)
    {
        return LightUtils.MinBlockLight(currentLight, candidateLight);
    }

    private int GetDirtySubchunkMaskForLightSample(int worldY)
    {
        int mask = GetDirtySubchunkMaskForWorldY(worldY);
        mask |= GetSubchunkBitForWorldY(worldY - 1);
        mask |= GetSubchunkBitForWorldY(worldY + 1);
        return SanitizeDirtySubchunkMask(mask);
    }

    private void AddDirtyLightSampleChunks(Dictionary<Vector2Int, int> dirtyMasks, Vector3Int worldPos)
    {
        int dirtySubchunkMask = GetDirtySubchunkMaskForLightSample(worldPos.y);
        if (dirtySubchunkMask == 0)
            return;

        Vector2Int coord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        AddDirtySubchunkMask(dirtyMasks, coord, dirtySubchunkMask);

        int localX = worldPos.x - coord.x * Chunk.SizeX;
        int localZ = worldPos.z - coord.y * Chunk.SizeZ;
        bool touchesMinX = localX == 0;
        bool touchesMaxX = localX == Chunk.SizeX - 1;
        bool touchesMinZ = localZ == 0;
        bool touchesMaxZ = localZ == Chunk.SizeZ - 1;

        if (touchesMinX) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.left, dirtySubchunkMask);
        if (touchesMaxX) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.right, dirtySubchunkMask);
        if (touchesMinZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.down, dirtySubchunkMask);
        if (touchesMaxZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.up, dirtySubchunkMask);
        if (touchesMinX && touchesMinZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.left + Vector2Int.down, dirtySubchunkMask);
        if (touchesMinX && touchesMaxZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.left + Vector2Int.up, dirtySubchunkMask);
        if (touchesMaxX && touchesMinZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.right + Vector2Int.down, dirtySubchunkMask);
        if (touchesMaxX && touchesMaxZ) AddDirtySubchunkMask(dirtyMasks, coord + Vector2Int.right + Vector2Int.up, dirtySubchunkMask);
    }

    private void AddPotentialStaleBlockLightRemovalRegion(
        Vector3Int center,
        ushort removedEmission,
        Dictionary<Vector3Int, ushort> affectedContributions)
    {
        int radius = Mathf.Clamp(LightUtils.GetBlockLight(removedEmission), 1, 15);
        int minY = Mathf.Max(0, center.y - radius);
        int maxY = Mathf.Min(Chunk.SizeY - 1, center.y + radius);

        for (int z = center.z - radius; z <= center.z + radius; z++)
        {
            for (int x = center.x - radius; x <= center.x + radius; x++)
            {
                if (!IsWorldColumnLoaded(x, z) && !globalLightColumns.ContainsKey(new Vector2Int(x, z)))
                    continue;

                for (int y = minY; y <= maxY; y++)
                {
                    Vector3Int pos = new Vector3Int(x, y, z);
                    int manhattanDistance =
                        Mathf.Abs(pos.x - center.x) +
                        Mathf.Abs(pos.y - center.y) +
                        Mathf.Abs(pos.z - center.z);
                    if (manhattanDistance > radius)
                        continue;

                    ushort currentLight = GetColumnLight(x, z, y);
                    if (!LightUtils.HasBlockLight(currentLight))
                        continue;

                    if (affectedContributions.TryGetValue(pos, out ushort existing))
                        affectedContributions[pos] = LightUtils.MaxBlockLight(existing, currentLight);
                    else
                        affectedContributions[pos] = currentLight;
                }
            }
        }
    }

    #endregion

    #region Column Light Helpers (MUITO mais rápido)

    private ushort GetColumnLight(int worldX, int worldZ, int y)
    {
        if (y < 0 || y >= Chunk.SizeY) return 0;

        var key = new Vector2Int(worldX, worldZ);
        return globalLightColumns.TryGetValue(key, out ushort[] column) ? column[y] : (ushort)0;
    }

    public ushort GetGlobalBlockLightAt(Vector3Int worldPos)
    {
        return GetColumnLight(worldPos.x, worldPos.z, worldPos.y);
    }

    public bool TryGetRenderedBlockLightAt(Vector3Int worldPos, out ushort packedLight)
    {
        packedLight = 0;
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return false;

        Vector2Int chunkCoord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk) || chunk == null)
            return false;

        int localX = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int localZ = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        return chunk.TryGetLightSnapshot(localX, worldPos.y, localZ, out packedLight);
    }

    private void SetColumnLight(int worldX, int worldZ, int y, ushort value)
    {
        if (y < 0 || y >= Chunk.SizeY) return;

        var key = new Vector2Int(worldX, worldZ);

        if (!globalLightColumns.TryGetValue(key, out ushort[] column))
        {
            if (value == 0) return;
            column = new ushort[Chunk.SizeY];
            globalLightColumns[key] = column;
        }

        column[y] = value;
    }

    private void SyncChunkBlockLightColumns(Vector2Int coord, NativeArray<ushort> packedLight, int borderSize)
    {
        if (!packedLight.IsCreated)
            return;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        for (int lz = 0; lz < Chunk.SizeZ; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int sampleZ = lz + borderSize;

            for (int lx = 0; lx < Chunk.SizeX; lx++)
            {
                int worldX = chunkMinX + lx;
                int sampleX = lx + borderSize;
                Vector2Int key = new Vector2Int(worldX, worldZ);
                ushort[] column = null;
                bool hasAnyLight = false;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    int idx = sampleX + y * voxelSizeX + sampleZ * voxelPlaneSize;
                    ushort blockLight = LightUtils.GetBlockLightPacked(packedLight[idx]);
                    if (LightUtils.HasBlockLight(blockLight))
                    {
                        hasAnyLight = true;
                        if (column == null)
                        {
                            if (!globalLightColumns.TryGetValue(key, out column))
                            {
                                column = new ushort[Chunk.SizeY];
                                globalLightColumns[key] = column;
                            }
                        }

                        column[y] = blockLight;
                    }
                    else if (column != null)
                    {
                        column[y] = 0;
                    }
                }

                if (!hasAnyLight)
                    globalLightColumns.Remove(key);
            }
        }
    }

    private bool ChunkHasBoundaryBlockLight(NativeArray<ushort> packedLight, int borderSize)
    {
        if (!packedLight.IsCreated)
            return false;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int minX = borderSize;
        int maxX = borderSize + Chunk.SizeX - 1;
        int minZ = borderSize;
        int maxZ = borderSize + Chunk.SizeZ - 1;

        for (int z = minZ; z <= maxZ; z++)
        {
            bool isBoundaryZ = z == minZ || z == maxZ;
            for (int y = 0; y < Chunk.SizeY; y++)
            {
                int baseIndex = y * voxelSizeX + z * voxelPlaneSize;
                for (int x = minX; x <= maxX; x++)
                {
                    if (!isBoundaryZ && x != minX && x != maxX)
                        continue;

                    if (LightUtils.GetBlockLight(packedLight[baseIndex + x]) > 0)
                        return true;
                }
            }
        }

        return false;
    }

    private void RequestNeighborChunkLightingRefresh(Vector2Int coord)
    {
        int chunkRadius = Mathf.Max(1, Mathf.CeilToInt(GetLightSmoothingBorderSize() / (float)Chunk.SizeX));
        int fullMask = GetFullSubchunkMask();

        for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
            {
                if (dx == 0 && dz == 0)
                    continue;

                Vector2Int neighborCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                if (!activeChunks.ContainsKey(neighborCoord))
                    continue;

                RequestLightingOnlyChunkRebuild(neighborCoord, fullMask);
            }
        }
    }

    private void PrimeNearbyOverrideLightSources(Vector2Int coord, int borderSize)
    {
        if (!enableVoxelLighting || blockOverrides.Count == 0)
            return;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;

            ushort emission = GetBlockEmissionPacked(ResolveWaterStateForDebug(overrideType));
            if (!LightUtils.HasBlockLight(emission))
                continue;

            PropagateLightGlobal(worldPos, emission, false);
        }
    }

    #endregion

    #region Global Light Propagation


    public void PropagateLightGlobal(Vector3Int startWorldPos, byte lightEmission, bool requestChunkRebuilds = true)
    {
        PropagateLightGlobal(startWorldPos, LightUtils.PackBlockLight(lightEmission), requestChunkRebuilds);
    }

    public void PropagateLightGlobal(Vector3Int startWorldPos, ushort lightEmission, bool requestChunkRebuilds = true)
    {
        if (startWorldPos.y < 0 || startWorldPos.y >= Chunk.SizeY) return;
        if (!LightUtils.HasBlockLight(lightEmission)) return;

        Queue<Vector3Int> lightQueue = propagateLightQueueBuffer;
        lightQueue.Clear();
        lightQueue.Enqueue(startWorldPos);

        // Define a luz inicial usando o novo sistema de colunas
        SetColumnLight(
            startWorldPos.x,
            startWorldPos.z,
            startWorldPos.y,
            LightUtils.MaxBlockLight(GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y), lightEmission));

        Dictionary<Vector2Int, int> dirtiedChunks = propagateDirtyChunksBuffer;
        dirtiedChunks.Clear();

        while (lightQueue.Count > 0)
        {
            Vector3Int node = lightQueue.Dequeue();

            // Pega a luz atual da coluna
            ushort currentLight = GetColumnLight(node.x, node.z, node.y);

            // Marca o chunk como sujo
            AddDirtyLightSampleChunks(dirtiedChunks, node);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;

                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                    continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ)
                );

                if (!IsChunkLoaded(neighborChunk))
                    continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);

                if (opacity >= 15)
                    continue;

                // Pega a luz do vizinho usando coluna
                ushort neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                ushort candidateLight = LightUtils.AttenuateBlockLight(currentLight, 1 + opacity);

                if (LightUtils.IsBlockLightGreater(candidateLight, neighborLight))
                {
                    // Define a nova luz usando coluna
                    SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, LightUtils.MaxBlockLight(neighborLight, candidateLight));
                    lightQueue.Enqueue(neighborPos);
                }
            }
        }

        // Reconstrói os chunks que receberam luz
        if (!requestChunkRebuilds)
            return;

        foreach (var kv in dirtiedChunks)
            RequestLightingOnlyChunkRebuild(kv.Key, kv.Value);
    }
    public void RemoveLightGlobal(Vector3Int startWorldPos)
    {
        RemoveLightGlobal(startWorldPos, GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y));
    }

    public void RemoveLightGlobal(Vector3Int startWorldPos, ushort removedEmission)
    {
        if (startWorldPos.y < 0 || startWorldPos.y >= Chunk.SizeY)
            return;
        if (!LightUtils.HasBlockLight(removedEmission))
            return;

        Queue<(Vector3Int pos, ushort lightLevel)> darkQueue = removeLightDarkQueueBuffer;
        darkQueue.Clear();
        Queue<Vector3Int> refillQueue = removeLightRefillQueueBuffer;
        refillQueue.Clear();
        Dictionary<Vector3Int, ushort> affectedContributions = removeLightAffectedContributionsBuffer;
        affectedContributions.Clear();
        Dictionary<Vector2Int, int> dirtiedChunks = removeLightDirtyChunksBuffer;
        dirtiedChunks.Clear();
        HashSet<Vector3Int> enqueued = refillLightEnqueuedBuffer;
        enqueued.Clear();
        HashSet<Vector2Int> columnsToCleanup = cleanupLightColumnKeysBuffer;
        columnsToCleanup.Clear();

        darkQueue.Enqueue((startWorldPos, removedEmission));

        while (darkQueue.Count > 0)
        {
            var node = darkQueue.Dequeue();
            if (!LightUtils.HasBlockLight(node.lightLevel))
                continue;

            ushort currentNodeLight = GetColumnLight(node.pos.x, node.pos.z, node.pos.y);
            ushort overlappingLight = GetOverlappingBlockLight(currentNodeLight, node.lightLevel);
            if (!LightUtils.HasBlockLight(overlappingLight))
                continue;

            if (affectedContributions.TryGetValue(node.pos, out ushort previousContribution))
            {
                ushort mergedContribution = LightUtils.MaxBlockLight(previousContribution, overlappingLight);
                if (mergedContribution == previousContribution)
                    continue;

                affectedContributions[node.pos] = mergedContribution;
                overlappingLight = mergedContribution;
            }
            else
            {
                affectedContributions[node.pos] = overlappingLight;
            }

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node.pos + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                    continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt(neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt(neighborPos.z / Chunk.SizeZ));
                if (!IsChunkLoaded(neighborChunk))
                    continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);
                if (opacity >= 15)
                    continue;

                ushort propagatedRemoval = LightUtils.AttenuateBlockLight(overlappingLight, 1 + opacity);
                if (!LightUtils.HasBlockLight(propagatedRemoval))
                    continue;

                ushort neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                ushort neighborOverlap = GetOverlappingBlockLight(neighborLight, propagatedRemoval);
                if (!LightUtils.HasBlockLight(neighborOverlap))
                    continue;

                if (affectedContributions.TryGetValue(neighborPos, out ushort previousNeighborContribution) &&
                    !LightUtils.IsBlockLightGreater(neighborOverlap, previousNeighborContribution))
                    continue;

                darkQueue.Enqueue((neighborPos, neighborOverlap));
            }
        }

        AddPotentialStaleBlockLightRemovalRegion(startWorldPos, removedEmission, affectedContributions);

        if (affectedContributions.Count == 0)
        {
            enqueued.Clear();
            columnsToCleanup.Clear();
            return;
        }

        foreach (var kv in affectedContributions)
        {
            Vector3Int pos = kv.Key;
            SetColumnLight(pos.x, pos.z, pos.y, 0);
            columnsToCleanup.Add(new Vector2Int(pos.x, pos.z));

            AddDirtyLightSampleChunks(dirtiedChunks, pos);
        }

        foreach (var kv in affectedContributions)
        {
            Vector3Int pos = kv.Key;
            BlockType blockAtPos = GetBlockAt(pos);
            byte targetOpacity = GetBlockOpacity(blockAtPos);
            ushort seededLight = GetRuntimeBlockEmissionPacked(pos, blockAtPos);

            if (targetOpacity < 15)
            {
                foreach (Vector3Int dir in sixDirections)
                {
                    Vector3Int neighborPos = pos + dir;
                    if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                        continue;

                    if (affectedContributions.ContainsKey(neighborPos))
                        continue;

                    Vector2Int neighborChunk = new Vector2Int(
                        Mathf.FloorToInt((float)neighborPos.x / Chunk.SizeX),
                        Mathf.FloorToInt((float)neighborPos.z / Chunk.SizeZ));
                    if (!IsChunkLoaded(neighborChunk))
                        continue;

                    ushort neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                    if (!LightUtils.HasBlockLight(neighborLight))
                        continue;

                    ushort candidateLight = LightUtils.AttenuateBlockLight(neighborLight, 1 + targetOpacity);
                    seededLight = LightUtils.MaxBlockLight(seededLight, candidateLight);
                }
            }

            if (!LightUtils.HasBlockLight(seededLight))
                continue;

            SetColumnLight(pos.x, pos.z, pos.y, seededLight);
            if (enqueued.Add(pos))
                refillQueue.Enqueue(pos);
        }

        while (refillQueue.Count > 0)
        {
            Vector3Int node = refillQueue.Dequeue();
            ushort currentLight = GetColumnLight(node.x, node.z, node.y);
            if (!LightUtils.HasBlockLight(currentLight))
                continue;

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                    continue;
                if (!affectedContributions.ContainsKey(neighborPos))
                    continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt((float)neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt((float)neighborPos.z / Chunk.SizeZ));
                if (!IsChunkLoaded(neighborChunk))
                    continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);
                if (opacity >= 15)
                    continue;

                ushort neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                ushort candidateLight = LightUtils.AttenuateBlockLight(currentLight, 1 + opacity);
                if (!LightUtils.IsBlockLightGreater(candidateLight, neighborLight))
                    continue;

                SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, LightUtils.MaxBlockLight(neighborLight, candidateLight));
                if (enqueued.Add(neighborPos))
                    refillQueue.Enqueue(neighborPos);
            }
        }

        foreach (var kv in dirtiedChunks)
            RequestLightingOnlyChunkRebuild(kv.Key, kv.Value);

        CleanupEmptyLightColumns(columnsToCleanup);
        affectedContributions.Clear();
        enqueued.Clear();
        columnsToCleanup.Clear();
    }

    public void RefillLightGlobal(Vector3Int startWorldPos)
    {
        if (startWorldPos.y < 0 || startWorldPos.y >= Chunk.SizeY) return;

        Dictionary<Vector2Int, int> dirtiedChunks = refillLightDirtyChunksBuffer;
        dirtiedChunks.Clear();
        Queue<Vector3Int> refillQueue = refillLightQueueBuffer;
        refillQueue.Clear();
        HashSet<Vector3Int> enqueued = refillLightEnqueuedBuffer;
        enqueued.Clear();

        // Try to relight the changed voxel from the brightest lit neighbor.
        BlockType startBlock = GetBlockAt(startWorldPos);
        byte startOpacity = GetBlockOpacity(startBlock);
        if (startOpacity < 15)
        {
            ushort bestAtStart = GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int n = startWorldPos + dir;
                if (n.y < 0 || n.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt((float)n.x / Chunk.SizeX),
                    Mathf.FloorToInt((float)n.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                ushort nLight = GetColumnLight(n.x, n.z, n.y);
                if (!LightUtils.HasBlockLight(nLight)) continue;

                ushort candidate = LightUtils.AttenuateBlockLight(nLight, 1 + startOpacity);
                if (LightUtils.IsBlockLightGreater(candidate, bestAtStart))
                    bestAtStart = LightUtils.MaxBlockLight(bestAtStart, candidate);
            }

            if (LightUtils.IsBlockLightGreater(bestAtStart, GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y)))
            {
                SetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y, bestAtStart);
            }
        }

        // Seed queue with changed position and lit neighbors.
        if (LightUtils.HasBlockLight(GetColumnLight(startWorldPos.x, startWorldPos.z, startWorldPos.y)))
        {
            refillQueue.Enqueue(startWorldPos);
            enqueued.Add(startWorldPos);
        }

        foreach (Vector3Int dir in sixDirections)
        {
            Vector3Int n = startWorldPos + dir;
            if (n.y < 0 || n.y >= Chunk.SizeY) continue;

            Vector2Int neighborChunk = new Vector2Int(
                Mathf.FloorToInt((float)n.x / Chunk.SizeX),
                Mathf.FloorToInt((float)n.z / Chunk.SizeZ)
            );
            if (!IsChunkLoaded(neighborChunk)) continue;

            if (LightUtils.HasBlockLight(GetColumnLight(n.x, n.z, n.y)) && enqueued.Add(n))
            {
                refillQueue.Enqueue(n);
            }
        }

        while (refillQueue.Count > 0)
        {
            Vector3Int node = refillQueue.Dequeue();
            ushort currentLight = GetColumnLight(node.x, node.z, node.y);
            if (!LightUtils.HasBlockLight(currentLight)) continue;

            AddDirtyLightSampleChunks(dirtiedChunks, node);

            foreach (Vector3Int dir in sixDirections)
            {
                Vector3Int neighborPos = node + dir;
                if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY) continue;

                Vector2Int neighborChunk = new Vector2Int(
                    Mathf.FloorToInt((float)neighborPos.x / Chunk.SizeX),
                    Mathf.FloorToInt((float)neighborPos.z / Chunk.SizeZ)
                );
                if (!IsChunkLoaded(neighborChunk)) continue;

                BlockType neighborBlock = GetBlockAt(neighborPos);
                byte opacity = GetBlockOpacity(neighborBlock);
                if (opacity >= 15) continue;

                ushort neighborLight = GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y);
                ushort candidateLight = LightUtils.AttenuateBlockLight(currentLight, 1 + opacity);

                if (LightUtils.IsBlockLightGreater(candidateLight, neighborLight))
                {
                    SetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y, LightUtils.MaxBlockLight(neighborLight, candidateLight));
                    if (enqueued.Add(neighborPos))
                    {
                        refillQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        foreach (var kv in dirtiedChunks)
        {
            RequestLightingOnlyChunkRebuild(kv.Key, kv.Value);
        }
    }

    private void CleanupEmptyLightColumns()
    {
        List<Vector2Int> toRemove = cleanupLightColumnsRemoveBuffer;
        toRemove.Clear();
        foreach (var kv in globalLightColumns)
        {
            bool allZero = true;
            for (int i = 0; i < kv.Value.Length; i++)
            {
                if (kv.Value[i] != 0) { allZero = false; break; }
            }
            if (allZero) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) globalLightColumns.Remove(k);
    }

    private void CleanupEmptyLightColumns(IEnumerable<Vector2Int> candidateColumns)
    {
        if (candidateColumns == null)
        {
            CleanupEmptyLightColumns();
            return;
        }

        List<Vector2Int> toRemove = cleanupLightColumnsRemoveBuffer;
        toRemove.Clear();
        foreach (Vector2Int key in candidateColumns)
        {
            if (!globalLightColumns.TryGetValue(key, out ushort[] column))
                continue;

            bool allZero = true;
            for (int i = 0; i < column.Length; i++)
            {
                if (column[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
                toRemove.Add(key);
        }

        for (int i = 0; i < toRemove.Count; i++)
            globalLightColumns.Remove(toRemove[i]);
    }
    #endregion
}
