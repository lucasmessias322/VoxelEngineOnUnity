using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class TreePlacement
{
    /// <summary>
    /// Aplica as instâncias de árvores (troncos e copas) nos arrays de blocos e solids.
    /// Usa Burst para máxima performance.
    /// </summary>
    [BurstCompile]
    public static void ApplyTreeInstancesToVoxels(
     NativeArray<BlockType> blockTypes,
     NativeArray<bool> solids,
     NativeArray<BlockTextureMapping> blockMappings,
     NativeArray<MeshGenerator.TreeInstance> treeInstances,
     Vector2Int coord,
     int border,
     int chunkSizeX,     // geralmente 16
     int chunkSizeZ,     // geralmente 16
     int chunkSizeY,     // geralmente 256
     int voxelSizeX,
     int voxelSizeZ,
     int voxelPlaneSize
 )
    {
        if (treeInstances.Length == 0)
            return;

        int baseWorldX = coord.x * chunkSizeX;
        int baseWorldZ = coord.y * chunkSizeZ;

        for (int i = 0; i < treeInstances.Length; i++)
        {
            var t = treeInstances[i];

            int localX = t.worldX - baseWorldX;
            int localZ = t.worldZ - baseWorldZ;

            // Permite overhang (copa saindo do chunk)
            if (localX < -t.canopyRadius || localX >= chunkSizeX + t.canopyRadius ||
                localZ < -t.canopyRadius || localZ >= chunkSizeZ + t.canopyRadius)
                continue;

            int ix = localX + border;
            int iz = localZ + border;

            // Encontra a superfície (do topo para baixo)
            int surfaceY = -1;
            for (int y = chunkSizeY - 1; y >= 0; y--)
            {
                int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                BlockType bt = blockTypes[idx];
                if (bt != BlockType.Air && bt != BlockType.Water)
                {
                    surfaceY = y;
                    break;
                }
            }

            if (surfaceY < 0 || surfaceY >= chunkSizeY)
                continue;

            // Pre-calc da copa / tronco para as checagens
            int leafBottom = surfaceY + t.trunkHeight - 1;
            int canopyH = math.max(1, t.canopyHeight);
            int canopyR = math.max(0, t.canopyRadius);

            // ---------------------------
            // PRE-CHECK: evitar árvores sobre/na água e perto de entradas de caverna
            // ---------------------------
            bool skipTree = false;

            // 1) Se houver água no volume do tronco + copa -> pular árvore
            int maxCheckY = math.min(chunkSizeY - 1, surfaceY + t.trunkHeight + canopyH);
            for (int yy = surfaceY + 1; yy <= maxCheckY && !skipTree; yy++)
            {
                for (int dx = -canopyR; dx <= canopyR && !skipTree; dx++)
                {
                    for (int dz = -canopyR; dz <= canopyR; dz++)
                    {
                        int cx = ix + dx;
                        int cz = iz + dz;
                        if (cx < 0 || cx >= voxelSizeX || cz < 0 || cz >= voxelSizeZ) continue;
                        int cidx = cx + yy * voxelSizeX + cz * voxelPlaneSize;
                        if (blockTypes[cidx] == BlockType.Water)
                        {
                            skipTree = true;
                            break;
                        }
                    }
                }
            }

            if (skipTree) continue;

            // 2) Detectar possíveis entradas de caverna: se qualquer bloco adjacente (3x3) no nível da superfície
            // ou até 2 blocos abaixo é Air, então é provável uma entrada — pular árvore.
            for (int dx = -1; dx <= 1 && !skipTree; dx++)
            {
                for (int dz = -1; dz <= 1 && !skipTree; dz++)
                {
                    for (int dy = 0; dy >= -2; dy--) // checar surfaceY, surfaceY-1, surfaceY-2
                    {
                        int cy = surfaceY + dy;
                        if (cy < 0) break;
                        int cx = ix + dx;
                        int cz = iz + dz;
                        if (cx < 0 || cx >= voxelSizeX || cz < 0 || cz >= voxelSizeZ) continue;
                        int cidx = cx + cy * voxelSizeX + cz * voxelPlaneSize;
                        if (blockTypes[cidx] == BlockType.Air)
                        {
                            skipTree = true;
                            break;
                        }
                    }
                }
            }

            if (skipTree) continue;

            // --------------------------------------------------
            // Tronco (1×1)
            // --------------------------------------------------
            for (int dy = 1; dy <= t.trunkHeight; dy++)
            {
                int ty = surfaceY + dy;
                if (ty >= chunkSizeY) break;

                int tidx = ix + ty * voxelSizeX + iz * voxelPlaneSize;

                BlockType existing = blockTypes[tidx];
                // NÃO substituir água — só substitui se for ar ou folhas (evita destruir outros blocos/água)
                if (existing == BlockType.Air || existing == BlockType.Leaves)
                {
                    blockTypes[tidx] = BlockType.Log;
                    solids[tidx] = blockMappings[(int)BlockType.Log].isSolid;
                }
            }

            // --------------------------------------------------
            // Copa (estilo Minecraft Oak com variação)
            // --------------------------------------------------
            // Hash simples para variação determinística por árvore e camada
            int treeHash = (t.worldX * 73856093) ^ (t.worldZ * 19349663) ^ (t.trunkHeight * 83492791);

            for (int dy = 0; dy < canopyH; dy++)
            {
                int ly = leafBottom + dy;
                if (ly < 0 || ly >= chunkSizeY) continue;

                // Camada superior: mantém 2×2 central (estilo oak)
                if (dy == canopyH - 1)
                {
                    for (int dx = -1; dx <= 0; dx++)
                        for (int dz = -1; dz <= 0; dz++)
                        {
                            int lx = ix + dx;
                            int lz = iz + dz;
                            if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ) continue;

                            int lidx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
                            BlockType ex = blockTypes[lidx];
                            if (ex == BlockType.Log) continue;
                            // NÃO substituir água
                            if (ex == BlockType.Air || ex == BlockType.Leaves)
                            {
                                blockTypes[lidx] = BlockType.Leaves;
                                solids[lidx] = blockMappings[(int)BlockType.Leaves].isSolid;
                            }
                        }
                    continue;
                }

                // Camadas intermediárias e inferiores
                int shrink = dy / 2;
                int radius = math.max(0, canopyR - shrink);

                // Variação nos cantos para forma mais natural
                int layerHash = treeHash ^ (dy * 1234567);
                int cornerSkipMask = layerHash & 0xF;

                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int absDx = math.abs(dx);
                        int absDz = math.abs(dz);

                        // Pula alguns cantos para forma mais orgânica
                        bool isCorner = (absDx == radius && absDz == radius) && radius > 0;
                        if (isCorner)
                        {
                            int bit = (absDx + absDz + dy) & 3;
                            if (((cornerSkipMask >> bit) & 1) == 1)
                                continue;
                        }

                        int lx = ix + dx;
                        int lz = iz + dz;
                        if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ) continue;

                        int lidx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
                        BlockType existing = blockTypes[lidx];

                        if (existing == BlockType.Log) continue;
                        // NÃO substituir água
                        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
                            continue;

                        blockTypes[lidx] = BlockType.Leaves;
                        solids[lidx] = blockMappings[(int)BlockType.Leaves].isSolid;
                    }
                }
            }

            // Garante que o tronco principal permaneça (defesa extra)
            for (int dy = 1; dy <= t.trunkHeight; dy++)
            {
                int ty = surfaceY + dy;
                if (ty >= chunkSizeY) break;
                int tidx = ix + ty * voxelSizeX + iz * voxelPlaneSize;
                if (blockTypes[tidx] != BlockType.Log)
                {
                    // Só sobrescreve se for Air ou Leaves (nunca água)
                    if (blockTypes[tidx] == BlockType.Air || blockTypes[tidx] == BlockType.Leaves)
                    {
                        blockTypes[tidx] = BlockType.Log;
                        solids[tidx] = blockMappings[(int)BlockType.Log].isSolid;
                    }
                }
            }
        }
    }

}