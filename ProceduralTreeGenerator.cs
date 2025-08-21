// ProceduralTreeGenerator.cs
// Gera árvores mais variadas (oak / pine) usando os BlockType configurados em VoxelWorld.
// Uso: ProceduralTreeGenerator.SpawnOakAtBlock(new Vector3Int(x,y,z), seed);
// ou   ProceduralTreeGenerator.SpawnPineAtWorld(new Vector3(worldX, worldY, worldZ), seed);

using System;
using UnityEngine;

public static class ProceduralTreeGenerator
{
    public enum TreeType { Oak, Pine }

    // API pública: instancia a árvore usando coordenadas de bloco (inteiros)
    // blockPos = posição do bloco de chão (onde o tronco começa em y)
    public static void SpawnOakAtBlock(Vector3Int blockPos, int seed = 0)
    {
        if (VoxelWorld.Instance == null) return;
        float bs = VoxelWorld.Instance.blockSize;
        Vector3 worldPos = new Vector3(blockPos.x * bs, blockPos.y * bs, blockPos.z * bs);
        SpawnOakAtWorld(worldPos, seed);
    }

    public static void SpawnPineAtBlock(Vector3Int blockPos, int seed = 0)
    {
        if (VoxelWorld.Instance == null) return;
        float bs = VoxelWorld.Instance.blockSize;
        Vector3 worldPos = new Vector3(blockPos.x * bs, blockPos.y * bs, blockPos.z * bs);
        SpawnPineAtWorld(worldPos, seed);
    }

    // API pública: instancia a árvore usando posição world (unidades do Unity). Use VoxelWorld.blockSize como escala.
    public static void SpawnOakAtWorld(Vector3 worldBasePos, int seed = 0)
    {
        var rng = new System.Random(seed ^ (int)worldBasePos.x * 73428767 ^ (int)worldBasePos.z * 19349663);
        int height = 4 + rng.Next(3); // 4..6
        int trunkThickness = (rng.NextDouble() < 0.12) ? 2 : 1; // vez ou outra tronco grosso

        var world = VoxelWorld.Instance;
        var wood = world != null ? world.woodBlock : BlockType.Placeholder;
        var leaves = world != null ? world.leavesBlock : BlockType.Placeholder;

        // tronco (pode ser 1 ou 2 de espessura)
        int bx = Mathf.FloorToInt(worldBasePos.x / world.blockSize);
        int by = Mathf.FloorToInt(worldBasePos.y / world.blockSize);
        int bz = Mathf.FloorToInt(worldBasePos.z / world.blockSize);

        for (int y = 1; y <= height; y++)
        {
            for (int ox = 0; ox < trunkThickness; ox++)
            {
                for (int oz = 0; oz < trunkThickness; oz++)
                {
                    SetBlockSafe(new Vector3Int(bx + ox, by + y, bz + oz), wood);
                }
            }

            // pequenas variações horizontais (lean)
            if (y > 1 && rng.NextDouble() < 0.15)
            {
                int dx = rng.Next(-1, 2);
                int dz = rng.Next(-1, 2);
                bx += dx; bz += dz;
            }
        }

        // folhas: clusters irregulares ao redor do topo
        int crownBase = by + height;
        int crownHeight = 3 + rng.Next(2);
        for (int y = 0; y < crownHeight; y++)
        {
            int layerY = crownBase + y;
            float radius = 2.0f + (float)(rng.NextDouble() * (1.5 - y * 0.3));
            FillLeafSphereWithNoise(bx, layerY, bz, radius, rng, leaves);
        }

        // ramos ocasionais
        int branches = 1 + rng.Next(3);
        for (int i = 0; i < branches; i++)
        {
            MakeOakBranch(bx, crownBase - 1 - rng.Next(2), bz, rng, wood, leaves);
        }
    }

    public static void SpawnPineAtWorld(Vector3 worldBasePos, int seed = 0)
    {
        var rng = new System.Random(seed ^ (int)worldBasePos.x * 9176213 ^ (int)worldBasePos.z * 19284763);
        int height = 6 + rng.Next(6); // 6..11 (pinheiros são mais altos)

        var world = VoxelWorld.Instance;
        var wood = world != null ? world.woodBlock : BlockType.Placeholder;
        var leaves = world != null ? world.leavesBlock : BlockType.Placeholder;

        int bx = Mathf.FloorToInt(worldBasePos.x / world.blockSize);
        int by = Mathf.FloorToInt(worldBasePos.y / world.blockSize);
        int bz = Mathf.FloorToInt(worldBasePos.z / world.blockSize);

        // tronco estreito e alto
        for (int y = 1; y <= height; y++)
        {
            SetBlockSafe(new Vector3Int(bx, by + y, bz), wood);
        }

        // camadas cônicas de folhas
        for (int y = 0; y < height; y++)
        {
            int layerY = by + height - y;
            float radius = 1.5f + (y * 0.35f); // base maior, topo menor
            radius = Mathf.Clamp(radius, 1f, height * 0.6f);
            int iradius = Mathf.CeilToInt(radius);
            for (int ox = -iradius; ox <= iradius; ox++)
            {
                for (int oz = -iradius; oz <= iradius; oz++)
                {
                    float dist = Mathf.Sqrt(ox * ox + oz * oz);
                    // camada mais externa mais rala
                    if (dist <= radius - (rng.NextDouble() * 0.6))
                    {
                        SetIfAir(new Vector3Int(bx + ox, layerY, bz + oz), leaves);
                    }
                }
            }
        }
    }

    // cria ramo para oak (simples): vai a partir do tronco em direção e termina em pequena copa
    private static void MakeOakBranch(int startX, int startY, int startZ, System.Random rng, BlockType wood, BlockType leaves)
    {
        int length = 2 + rng.Next(3);
        int dirX = rng.Next(-1, 2);
        int dirZ = rng.Next(-1, 2);
        if (dirX == 0 && dirZ == 0) dirX = 1;

        int x = startX, y = startY, z = startZ;
        for (int i = 0; i < length; i++)
        {
            x += dirX;
            z += dirZ;
            y += (rng.NextDouble() < 0.25) ? 1 : 0; // sobe às vezes
            SetBlockSafe(new Vector3Int(x, y, z), wood);
        }

        // pequena copa no fim
        FillLeafSphereWithNoise(x, y, z, 1.6f, rng, leaves);
    }

    // preenche uma esfera de folhas com ruído para parecer menos regular
    private static void FillLeafSphereWithNoise(int cx, int cy, int cz, float radius, System.Random rng, BlockType leaves)
    {
        int r = Mathf.CeilToInt(radius);
        for (int ox = -r; ox <= r; ox++)
        {
            for (int oy = -r; oy <= r; oy++)
            {
                for (int oz = -r; oz <= r; oz++)
                {
                    float dist = Mathf.Sqrt(ox * ox + oy * oy + oz * oz);
                    if (dist <= radius + 0.001f)
                    {
                        // aplicar um pouco de ruído para buracos/irrregularidade
                        double n = (rng.NextDouble() - 0.5) * 0.6;
                        if (dist + n <= radius)
                        {
                            SetIfAir(new Vector3Int(cx + ox, cy + oy, cz + oz), leaves);
                        }
                    }
                }
            }
        }
    }

    // seta bloco somente se posição atual for ar ou água (evita sobrepor o terreno)
    private static void SetIfAir(Vector3Int pos, BlockType block)
    {
        var world = VoxelWorld.Instance;
        if (world == null) return;
        Vector3 wp = new Vector3(pos.x * world.blockSize, pos.y * world.blockSize, pos.z * world.blockSize);
        var cur = world.GetBlockAtWorld(wp);
        if (cur == BlockType.Air || cur == BlockType.Water)
        {
            world.SetBlockAtWorld(wp, block);
        }
    }

    // seta bloco sem checagens extras (use com cuidado)
    private static void SetBlockSafe(Vector3Int pos, BlockType block)
    {
        var world = VoxelWorld.Instance;
        if (world == null) return;
        Vector3 wp = new Vector3(pos.x * world.blockSize, pos.y * world.blockSize, pos.z * world.blockSize);
        world.SetBlockAtWorld(wp, block);
    }
}
