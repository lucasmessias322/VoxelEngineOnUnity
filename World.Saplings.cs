using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    [Header("Saplings")]
    public bool enableSaplingGrowth = true;
    [Min(1f)] public float oakSaplingMinGrowSeconds = 90f;
    [Min(1f)] public float oakSaplingMaxGrowSeconds = 180f;
    [Min(1)] public int saplingRequiredClearanceBlocks = 10;
    [Range(0, 15)] public byte saplingRequiredLightLevel = 9;
    [Min(1)] public int saplingGrowthChecksPerFrame = 2;

    private readonly Queue<Vector3Int> queuedSaplingGrowth = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, float> queuedSaplingGrowTimes = new Dictionary<Vector3Int, float>(InitialInteractiveBlockLightRefreshCapacity);

    private void HandleSaplingBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (previousType == newType)
            return;

        if (SaplingBlockUtility.IsSapling(previousType))
            queuedSaplingGrowTimes.Remove(worldPos);

        if (newType == BlockType.oakTreeSapling)
            QueueOakSaplingGrowth(worldPos);
    }

    private void QueueOakSaplingGrowth(Vector3Int worldPos)
    {
        if (!enableSaplingGrowth)
            return;

        float minDelay = Mathf.Max(1f, oakSaplingMinGrowSeconds);
        float maxDelay = Mathf.Max(minDelay, oakSaplingMaxGrowSeconds);
        float delay = Mathf.Lerp(minDelay, maxDelay, Hash01(worldPos.x, worldPos.y, worldPos.z));
        queuedSaplingGrowTimes[worldPos] = Time.time + delay;
        queuedSaplingGrowth.Enqueue(worldPos);
    }

    private void ProcessQueuedSaplingGrowth()
    {
        if (!enableSaplingGrowth || queuedSaplingGrowth.Count == 0)
            return;

        int processed = 0;
        int attempts = queuedSaplingGrowth.Count;
        int perFrameLimit = Mathf.Max(1, saplingGrowthChecksPerFrame);
        float now = Time.time;

        while (processed < perFrameLimit && attempts-- > 0 && queuedSaplingGrowth.Count > 0)
        {
            Vector3Int pos = queuedSaplingGrowth.Dequeue();
            if (!queuedSaplingGrowTimes.TryGetValue(pos, out float growAt))
                continue;

            if (growAt > now)
            {
                queuedSaplingGrowth.Enqueue(pos);
                continue;
            }

            queuedSaplingGrowTimes.Remove(pos);
            if (GetBlockAt(pos) != BlockType.oakTreeSapling)
                continue;

            if (TryGrowOakSapling(pos))
            {
                processed++;
                continue;
            }

            QueueOakSaplingGrowth(pos);
            processed++;
        }
    }

    private bool TryGrowOakSapling(Vector3Int saplingPos)
    {
        Vector3Int groundPos = saplingPos + Vector3Int.down;
        if (!SaplingBlockUtility.CanPlantOn(GetBlockAt(groundPos)))
            return false;

        TreeSpawnRuleData treeRule = ResolveSaplingTreeRule(saplingPos);
        TreeSettings treeSettings = treeRule.settings;

        if (!HasSaplingGrowthClearance(saplingPos, treeRule.treeStyle, treeSettings))
            return false;

        if (!HasSaplingGrowthLight(saplingPos))
            return false;

        int treeHash = HashInt(saplingPos.x, saplingPos.y, saplingPos.z) ^
                       (treeSettings.seed * 92837111) ^
                       ((int)treeRule.treeStyle * 73428767);
        int trunkHeight = ResolveRuntimeTreeHeight(treeSettings, treeHash);
        int canopyHeight = Mathf.Max(1, treeSettings.canopyHeight);
        int canopyRadius = Mathf.Max(0, treeSettings.canopyRadius);
        int trunkClearance = Mathf.Max(0, treeSettings.trunkClearance);
        HashSet<Vector3Int> logBlocks = new HashSet<Vector3Int>();
        HashSet<Vector3Int> leafBlocks = new HashSet<Vector3Int>();

        if (!BuildRuntimeConfiguredTree(
                treeRule.treeStyle,
                saplingPos.x,
                saplingPos.z,
                groundPos.y,
                trunkHeight,
                canopyHeight,
                canopyRadius,
                trunkClearance,
                treeHash,
                logBlocks,
                leafBlocks,
                out BlockType trunkType))
        {
            return false;
        }

        if (!CanPlaceRuntimeTree(logBlocks, leafBlocks))
            return false;

        foreach (Vector3Int logPos in logBlocks)
            SetBlockAt(logPos, trunkType);

        foreach (Vector3Int leafPos in leafBlocks)
        {
            if (logBlocks.Contains(leafPos))
                continue;

            TryPlaceRuntimeLeaf(leafPos);
        }

        return true;
    }

    private TreeSpawnRuleData ResolveSaplingTreeRule(Vector3Int saplingPos)
    {
        BiomeType biome = GetBiomeAt(saplingPos.x, saplingPos.z);
        TreeSpawnRuleData[] rules = GetActiveTreeSpawnRules();
        float totalWeight = 0f;
        TreeSpawnRuleData firstBiomeTreeRule = default;
        bool hasMatch = false;

        if (rules != null)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                TreeSpawnRuleData rule = rules[i];
                if (rule.biome != biome)
                    continue;

                if (rule.treeStyle == TreeStyle.FancyOak)
                    return CreateFancyOakSaplingTreeRule(biome, rule.settings);

                if (rule.treeStyle == TreeStyle.Cactus)
                    continue;

                if (!hasMatch)
                {
                    firstBiomeTreeRule = rule;
                    hasMatch = true;
                }

                totalWeight += Mathf.Max(0.0001f, rule.settings.density);
            }
        }

        if (!hasMatch || totalWeight <= 0f)
            return CreateFallbackSaplingTreeRule(biome);

        float pick = Hash01(saplingPos.x, saplingPos.y, saplingPos.z) * totalWeight;
        float accumulated = 0f;
        for (int i = 0; i < rules.Length; i++)
        {
            TreeSpawnRuleData rule = rules[i];
            if (rule.biome != biome || rule.treeStyle == TreeStyle.Cactus)
                continue;

            accumulated += Mathf.Max(0.0001f, rule.settings.density);
            if (pick <= accumulated)
                return CreateFancyOakSaplingTreeRule(biome, rule.settings);
        }

        return CreateFancyOakSaplingTreeRule(biome, firstBiomeTreeRule.settings);
    }

    private TreeSpawnRuleData CreateFallbackSaplingTreeRule(BiomeType biome)
    {
        TreeSettings fallbackSettings = new TreeSettings
        {
            minHeight = 9,
            maxHeight = 11,
            canopyRadius = 4,
            canopyHeight = 4,
            trunkClearance = 0,
            minSpacing = 9,
            density = 1f,
            noiseScale = 1f,
            seed = seed
        };

        return CreateFancyOakSaplingTreeRule(biome, fallbackSettings);
    }

    private TreeSpawnRuleData CreateFancyOakSaplingTreeRule(BiomeType biome, TreeSettings settings)
    {
        if (settings.seed == 0)
            settings.seed = seed;

        return new TreeSpawnRuleData
        {
            biome = biome,
            treeStyle = TreeStyle.FancyOak,
            settings = settings
        };
    }

    private static int ResolveRuntimeTreeHeight(TreeSettings settings, int treeHash)
    {
        int minHeight = Mathf.Max(1, settings.minHeight);
        int maxHeight = Mathf.Max(minHeight, settings.maxHeight);
        int heightSpan = maxHeight - minHeight + 1;
        uint positiveHash = unchecked((uint)treeHash);
        return minHeight + (int)(positiveHash % (uint)Mathf.Max(1, heightSpan));
    }

    private bool BuildRuntimeConfiguredTree(
        TreeStyle treeStyle,
        int centerX,
        int centerZ,
        int surfaceY,
        int trunkHeight,
        int canopyHeight,
        int canopyRadius,
        int trunkClearance,
        int treeHash,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks,
        out BlockType trunkType)
    {
        trunkType = GetRuntimeTrunkBlockType(treeStyle);
        if (treeStyle == TreeStyle.FancyOak)
        {
            return BuildRuntimeFancyOakTree(
                centerX,
                centerZ,
                surfaceY,
                trunkHeight,
                canopyHeight,
                canopyRadius,
                treeHash,
                logBlocks,
                leafBlocks);
        }

        int trunkBlocksToPlace = Mathf.Max(1, trunkHeight);
        if (treeStyle == TreeStyle.TaigaSpruce)
            trunkBlocksToPlace = Mathf.Max(1, trunkBlocksToPlace - Mathf.Min(2, (treeHash >> 8) & 0x3));

        for (int dy = 1; dy <= trunkBlocksToPlace; dy++)
            logBlocks.Add(new Vector3Int(centerX, surfaceY + dy, centerZ));

        switch (treeStyle)
        {
            case TreeStyle.TaigaSpruce:
                AddRuntimeTaigaSpruceCanopy(centerX, centerZ, surfaceY, trunkHeight, canopyHeight, canopyRadius, trunkClearance, treeHash, leafBlocks);
                break;

            case TreeStyle.SavannaAcacia:
                AddRuntimeSavannaAcaciaShape(centerX, centerZ, surfaceY, trunkHeight, canopyHeight, canopyRadius, treeHash, logBlocks, leafBlocks);
                break;

            case TreeStyle.BirchBroadleaf:
                AddRuntimeBirchBroadleafCanopy(centerX, centerZ, surfaceY, trunkHeight, canopyHeight, canopyRadius, treeHash, leafBlocks);
                break;

            case TreeStyle.OakBroadleaf:
            default:
                AddRuntimeOakBroadleafCanopy(centerX, centerZ, surfaceY + trunkHeight - 1, canopyHeight, canopyRadius, treeHash, leafBlocks);
                break;
        }

        return true;
    }

    private static BlockType GetRuntimeTrunkBlockType(TreeStyle treeStyle)
    {
        switch (treeStyle)
        {
            case TreeStyle.BirchBroadleaf:
                return BlockType.birch_log;

            case TreeStyle.SavannaAcacia:
                return BlockType.acacia_log;

            default:
                return BlockType.Log;
        }
    }

    private static void AddRuntimeOakBroadleafCanopy(
        int centerX,
        int centerZ,
        int leafBottom,
        int canopyHeight,
        int canopyRadius,
        int treeHash,
        HashSet<Vector3Int> leafBlocks)
    {
        int safeCanopyHeight = Mathf.Max(1, canopyHeight);
        int safeCanopyRadius = Mathf.Max(1, canopyRadius);
        for (int dy = 0; dy < safeCanopyHeight; dy++)
        {
            int y = leafBottom + dy;
            if (dy == safeCanopyHeight - 1)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    for (int dz = -1; dz <= 0; dz++)
                        leafBlocks.Add(new Vector3Int(centerX + dx, y, centerZ + dz));
                }

                continue;
            }

            int radius = Mathf.Max(0, safeCanopyRadius - dy / 2);
            int cornerSkipMask = (treeHash ^ (dy * 1234567)) & 0xF;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int absDx = Mathf.Abs(dx);
                    int absDz = Mathf.Abs(dz);
                    bool isCorner = absDx == radius && absDz == radius && radius > 0;
                    if (isCorner)
                    {
                        int bit = (absDx + absDz + dy) & 3;
                        if (((cornerSkipMask >> bit) & 1) == 1)
                            continue;
                    }

                    leafBlocks.Add(new Vector3Int(centerX + dx, y, centerZ + dz));
                }
            }
        }
    }

    private static void AddRuntimeBirchBroadleafCanopy(
        int centerX,
        int centerZ,
        int surfaceY,
        int trunkHeight,
        int canopyHeight,
        int canopyRadius,
        int treeHash,
        HashSet<Vector3Int> leafBlocks)
    {
        int widestRadius = Mathf.Max(2, canopyRadius);
        int totalLayers = Mathf.Max(6, canopyHeight + 3);
        int canopyBaseY = surfaceY + trunkHeight;

        for (int layer = 0; layer < totalLayers; layer++)
        {
            int y = canopyBaseY + layer;
            int radius = GetRuntimeBirchLayerRadius(widestRadius, totalLayers, layer);
            int layerHash = treeHash ^ (layer * 73428767);
            AddRuntimeBirchLeafLayer(centerX, centerZ, y, radius, widestRadius, layer, totalLayers, layerHash, leafBlocks);
        }
    }

    private static int GetRuntimeBirchLayerRadius(int widestRadius, int totalLayers, int layer)
    {
        if (layer >= totalLayers - 1)
            return 0;

        float t = totalLayers <= 1 ? 0f : (float)layer / (totalLayers - 1);
        float radiusFactor;
        if (t < 0.18f)
            radiusFactor = Mathf.Lerp(0.45f, 0.7f, t / 0.18f);
        else if (t < 0.42f)
            radiusFactor = Mathf.Lerp(0.7f, 1f, (t - 0.18f) / 0.24f);
        else if (t < 0.72f)
            radiusFactor = Mathf.Lerp(1f, 0.82f, (t - 0.42f) / 0.30f);
        else if (t < 0.9f)
            radiusFactor = Mathf.Lerp(0.82f, 0.45f, (t - 0.72f) / 0.18f);
        else
            radiusFactor = Mathf.Lerp(0.45f, 0.12f, (t - 0.9f) / 0.1f);

        int radius = Mathf.RoundToInt(widestRadius * radiusFactor);
        if (radiusFactor > 0.18f)
            radius = Mathf.Max(1, radius);

        return Mathf.Clamp(radius, 0, widestRadius);
    }

    private static void AddRuntimeBirchLeafLayer(
        int centerX,
        int centerZ,
        int y,
        int radius,
        int widestRadius,
        int layerIndex,
        int totalLayers,
        int layerHash,
        HashSet<Vector3Int> leafBlocks)
    {
        if (radius <= 0)
        {
            leafBlocks.Add(new Vector3Int(centerX, y, centerZ));
            return;
        }

        float xScale = ((layerHash >> 2) & 1) == 0 ? 0.95f : 1.08f;
        float zScale = ((layerHash >> 3) & 1) == 0 ? 1.08f : 0.95f;
        float radiusPadding = radius + 0.35f;
        for (int dx = -radius; dx <= radius; dx++)
        {
            float normalizedX = (Mathf.Abs(dx) + 0.25f) / (radiusPadding * xScale);
            for (int dz = -radius; dz <= radius; dz++)
            {
                float normalizedZ = (Mathf.Abs(dz) + 0.25f) / (radiusPadding * zScale);
                float dist = normalizedX * normalizedX + normalizedZ * normalizedZ;
                if (dist > 1f)
                    continue;

                bool isEdge = dist > 0.58f;
                bool isCorner = Mathf.Abs(dx) == radius && Mathf.Abs(dz) == radius;
                int cellHash = layerHash ^ (dx * 912367421) ^ (dz * 73428767);
                float skipChance = 0f;
                if (isCorner)
                    skipChance += 0.5f;
                else if (isEdge)
                    skipChance += radius >= 2 ? 0.18f : 0.08f;
                if (layerIndex == 0)
                    skipChance += 0.16f;
                else if (layerIndex == totalLayers - 2)
                    skipChance += 0.22f;
                if (skipChance > 0f && Hash01(cellHash) < skipChance)
                    continue;

                Vector3Int leafPos = new Vector3Int(centerX + dx, y, centerZ + dz);
                leafBlocks.Add(leafPos);

                bool canDroop = isEdge && radius >= Mathf.Max(1, widestRadius - 1) && layerIndex > 0 && layerIndex < totalLayers - 2;
                if (canDroop && Hash01(cellHash ^ 0x51ED270B) > 0.84f)
                    leafBlocks.Add(leafPos + Vector3Int.down);
            }
        }
    }

    private static void AddRuntimeTaigaSpruceCanopy(
        int centerX,
        int centerZ,
        int surfaceY,
        int trunkHeight,
        int canopyHeight,
        int canopyRadius,
        int trunkClearance,
        int treeHash,
        HashSet<Vector3Int> leafBlocks)
    {
        int topY = surfaceY + Mathf.Max(4, trunkHeight);
        uint rngState = InitializeRuntimeTreeRandom(treeHash);
        bool wrapWholeTrunk = trunkClearance <= 0;
        int foliageBaseY;
        if (wrapWholeTrunk)
        {
            foliageBaseY = surfaceY + 1;
        }
        else
        {
            int desiredLayers = Mathf.Max(3, canopyHeight);
            int baseFromLayers = topY - desiredLayers + 1;
            int baseFromClearance = surfaceY + 1 + Mathf.Clamp(trunkClearance, 0, Mathf.Max(0, trunkHeight - 1));
            foliageBaseY = Mathf.Max(baseFromLayers, baseFromClearance) + NextRuntimeRandomInt(ref rngState, 2);
        }
        foliageBaseY = Mathf.Clamp(foliageBaseY, surfaceY + 1, topY);

        int maxRadius = Mathf.Clamp(Mathf.Max(2, canopyRadius, 2 + NextRuntimeRandomInt(ref rngState, 2)), 2, 3);
        int minimumLayerRadius = wrapWholeTrunk ? 1 : 0;
        int layerRadius = minimumLayerRadius + NextRuntimeRandomInt(ref rngState, 2);
        int growthThreshold = 1;
        int resetRadius = minimumLayerRadius;
        for (int y = topY; y >= foliageBaseY; y--)
        {
            AddRuntimeSquareLeafLayer(centerX, centerZ, y, layerRadius, skipCorners: layerRadius >= 2, leafBlocks);
            if (layerRadius >= growthThreshold)
            {
                layerRadius = resetRadius;
                resetRadius = 1;
                growthThreshold = Mathf.Min(maxRadius, growthThreshold + 1);
            }
            else
            {
                layerRadius++;
            }
        }

        leafBlocks.Add(new Vector3Int(centerX, topY + 1, centerZ));
    }

    private static void AddRuntimeSavannaAcaciaShape(
        int centerX,
        int centerZ,
        int surfaceY,
        int trunkHeight,
        int canopyHeight,
        int canopyRadius,
        int treeHash,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int trunkTopY = surfaceY + trunkHeight;
        int primaryDir = treeHash & 3;
        GetRuntimeCardinalDirection(primaryDir, out int dirX, out int dirZ);
        int branchStartY = surfaceY + Mathf.Max(3, trunkHeight - 2);
        int primaryBranchLength = Mathf.Clamp(1 + ((treeHash >> 3) & 0x1), 1, Mathf.Max(1, canopyRadius - 2));
        int mainX = centerX;
        int mainZ = centerZ;
        int mainY = trunkTopY;

        for (int step = 1; step <= primaryBranchLength; step++)
        {
            mainX += dirX;
            mainZ += dirZ;
            mainY = branchStartY + step;
            logBlocks.Add(new Vector3Int(mainX, mainY, mainZ));
        }

        int mainRadius = Mathf.Max(2, canopyRadius - 1);
        AddRuntimeSavannaCanopyPlatform(mainX, mainZ, mainY, mainRadius, treeHash, leafBlocks);
        AddRuntimeSavannaCanopyPlatform(mainX, mainZ, mainY + 1, Mathf.Max(1, mainRadius - 1), treeHash ^ 2038074743, leafBlocks);
        AddRuntimeSavannaLowerFringe(mainX, mainZ, mainY, mainRadius, leafBlocks);
        leafBlocks.Add(new Vector3Int(mainX, mainY + 2, mainZ));

        if (trunkHeight >= 6)
        {
            int secondaryDir = (primaryDir + (((treeHash >> 6) & 1) == 0 ? 1 : 3)) & 3;
            GetRuntimeCardinalDirection(secondaryDir, out int secondaryDirX, out int secondaryDirZ);
            int secondaryLength = Mathf.Min(1 + ((treeHash >> 8) & 1), Mathf.Max(1, canopyRadius - 2));
            int secondaryBaseY = surfaceY + Mathf.Max(3, trunkHeight - 3);
            int secondaryX = centerX;
            int secondaryZ = centerZ;
            int secondaryY = secondaryBaseY;
            for (int step = 1; step <= secondaryLength; step++)
            {
                secondaryX += secondaryDirX;
                secondaryZ += secondaryDirZ;
                secondaryY = secondaryBaseY + (step > 1 ? 1 : 0);
                logBlocks.Add(new Vector3Int(secondaryX, secondaryY, secondaryZ));
            }

            int secondaryRadius = Mathf.Max(1, mainRadius - 1);
            AddRuntimeSavannaCanopyPlatform(secondaryX, secondaryZ, secondaryY + 1, secondaryRadius, treeHash ^ 461845907, leafBlocks);
            leafBlocks.Add(new Vector3Int(secondaryX, secondaryY + 2, secondaryZ));
        }

        AddRuntimeLeafCross(centerX, centerZ, trunkTopY, Mathf.Max(1, canopyHeight - 2), leafBlocks);
    }

    private static void AddRuntimeSavannaCanopyPlatform(int centerX, int centerZ, int y, int radius, int layerHash, HashSet<Vector3Int> leafBlocks)
    {
        if (radius <= 0)
        {
            leafBlocks.Add(new Vector3Int(centerX, y, centerZ));
            return;
        }

        int manhattanLimit = radius + (radius > 2 ? 1 : 0);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int absDx = Mathf.Abs(dx);
                int absDz = Mathf.Abs(dz);
                if (absDx + absDz > manhattanLimit)
                    continue;
                if (radius >= 2 && absDx == radius && absDz == radius)
                    continue;
                if (radius >= 3 && absDx == radius && absDz >= radius - 1)
                {
                    int bit = (absDz + layerHash + dx * dx) & 3;
                    if (((layerHash >> bit) & 1) == 1)
                        continue;
                }

                leafBlocks.Add(new Vector3Int(centerX + dx, y, centerZ + dz));
            }
        }
    }

    private static void AddRuntimeSavannaLowerFringe(int centerX, int centerZ, int y, int radius, HashSet<Vector3Int> leafBlocks)
    {
        int fringeY = y - 1;
        if (radius <= 0)
            return;

        int fringeRadius = Mathf.Max(1, radius - 1);
        leafBlocks.Add(new Vector3Int(centerX + fringeRadius, fringeY, centerZ));
        leafBlocks.Add(new Vector3Int(centerX - fringeRadius, fringeY, centerZ));
        leafBlocks.Add(new Vector3Int(centerX, fringeY, centerZ + fringeRadius));
        leafBlocks.Add(new Vector3Int(centerX, fringeY, centerZ - fringeRadius));
        if (radius >= 3)
        {
            leafBlocks.Add(new Vector3Int(centerX + 1, fringeY, centerZ + fringeRadius));
            leafBlocks.Add(new Vector3Int(centerX - 1, fringeY, centerZ - fringeRadius));
        }
    }

    private static void AddRuntimeLeafCross(int centerX, int centerZ, int y, int radius, HashSet<Vector3Int> leafBlocks)
    {
        if (radius <= 0)
        {
            leafBlocks.Add(new Vector3Int(centerX, y, centerZ));
            return;
        }

        for (int step = 1; step <= radius; step++)
        {
            leafBlocks.Add(new Vector3Int(centerX - step, y, centerZ));
            leafBlocks.Add(new Vector3Int(centerX + step, y, centerZ));
            leafBlocks.Add(new Vector3Int(centerX, y, centerZ - step));
            leafBlocks.Add(new Vector3Int(centerX, y, centerZ + step));
        }
    }

    private static void AddRuntimeSquareLeafLayer(int centerX, int centerZ, int y, int radius, bool skipCorners, HashSet<Vector3Int> leafBlocks)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (skipCorners && Mathf.Abs(dx) == radius && Mathf.Abs(dz) == radius)
                    continue;

                leafBlocks.Add(new Vector3Int(centerX + dx, y, centerZ + dz));
            }
        }
    }

    private static void GetRuntimeCardinalDirection(int dir, out int dx, out int dz)
    {
        switch (dir & 3)
        {
            case 0: dx = 1; dz = 0; break;
            case 1: dx = -1; dz = 0; break;
            case 2: dx = 0; dz = 1; break;
            default: dx = 0; dz = -1; break;
        }
    }

    private static uint InitializeRuntimeTreeRandom(int treeHash)
    {
        uint state = (uint)treeHash ^ 0x9E3779B9u;
        return state != 0u ? state : 0xA341316Cu;
    }

    private static int NextRuntimeRandomInt(ref uint state, int maxExclusive)
    {
        if (maxExclusive <= 1)
            return 0;

        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)(state % (uint)maxExclusive);
    }

    private bool HasSaplingGrowthClearance(Vector3Int saplingPos, TreeStyle treeStyle, TreeSettings settings)
    {
        int configuredMargin = TreeGenerationMetrics.GetVerticalMargin(treeStyle, settings);
        int clearanceBlocks = Mathf.Max(1, saplingRequiredClearanceBlocks, configuredMargin);
        for (int offsetY = 1; offsetY <= clearanceBlocks; offsetY++)
        {
            if (GetBlockAt(saplingPos + Vector3Int.up * offsetY) != BlockType.Air)
                return false;
        }

        return true;
    }

    private bool HasSaplingGrowthLight(Vector3Int saplingPos)
    {
        byte requiredLight = (byte)Mathf.Clamp(saplingRequiredLightLevel, 0, 15);
        if (requiredLight == 0)
            return true;

        if (TryGetRenderedBlockLightAt(saplingPos, out ushort renderedLight))
        {
            if (LightUtils.GetSkyLight(renderedLight) >= requiredLight ||
                LightUtils.GetBlockLight(renderedLight) >= requiredLight)
            {
                return true;
            }
        }

        ushort globalBlockLight = GetGlobalBlockLightAt(saplingPos);
        if (LightUtils.GetBlockLight(globalBlockLight) >= requiredLight)
            return true;

        return IsSaplingOpenToSky(saplingPos);
    }

    private bool IsSaplingOpenToSky(Vector3Int saplingPos)
    {
        for (int y = saplingPos.y + 1; y < Chunk.SizeY; y++)
        {
            BlockType blockType = GetBlockAt(new Vector3Int(saplingPos.x, y, saplingPos.z));
            if (blockType == BlockType.Air)
                continue;

            if (GetBlockOpacity(blockType) > 0)
                return false;
        }

        return true;
    }

    private bool BuildRuntimeFancyOakTree(
        int centerX,
        int centerZ,
        int surfaceY,
        int heightLimitValue,
        int canopyHeight,
        int canopyRadius,
        int treeHash,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int heightLimit = Mathf.Max(6, heightLimitValue);
        int leafDistanceLimit = Mathf.Clamp(Mathf.Max(4, canopyHeight), 4, 5);
        int trunkHeight = Mathf.Max(1, Mathf.FloorToInt(heightLimit * 0.618f));
        if (trunkHeight >= heightLimit)
            trunkHeight = heightLimit - 1;

        int trunkTopY = surfaceY + trunkHeight;
        int topLeafY = surfaceY + heightLimit - leafDistanceLimit;
        float scaleWidth = Mathf.Max(1f, canopyRadius / 4f);
        float leafDensity = Mathf.Max(1f, canopyHeight / 4f);
        float nodeDensity = leafDensity * heightLimit / 13f;
        int nodesPerLayer = Mathf.Max(1, (int)(1.382f + nodeDensity * nodeDensity));
        nodesPerLayer = Mathf.Clamp(nodesPerLayer, 1, 4);

        AddRuntimeFancyOakLeafNode(centerX, topLeafY, centerZ, leafDistanceLimit, leafBlocks);

        int minLeafY = surfaceY + Mathf.CeilToInt(heightLimit * 0.3f);
        for (int layerY = topLeafY - 1; layerY >= minLeafY; layerY--)
        {
            float layerSize = GetRuntimeFancyOakLayerSize(heightLimit, layerY - surfaceY);
            if (layerSize < 0f)
                continue;

            for (int nodeIndex = 0; nodeIndex < nodesPerLayer; nodeIndex++)
            {
                int nodeHash = treeHash ^ (layerY * 73428767) ^ (nodeIndex * 912367421);
                float distance = scaleWidth * layerSize * (0.328f + Hash01(nodeHash));
                float angle = Hash01(nodeHash ^ 1757151723) * Mathf.PI * 2f;

                int nodeX = centerX + Mathf.FloorToInt(distance * Mathf.Sin(angle) + 0.5f);
                int nodeZ = centerZ + Mathf.FloorToInt(distance * Mathf.Cos(angle) + 0.5f);
                int nodeY = layerY;

                if (!IsRuntimeFancyOakLeafNodeClear(nodeX, nodeY, nodeZ, leafDistanceLimit, logBlocks, leafBlocks))
                    continue;

                int dx = nodeX - centerX;
                int dz = nodeZ - centerZ;
                float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);
                int branchBaseY = (float)nodeY - horizontalDistance * 0.381f > trunkTopY
                    ? trunkTopY
                    : (int)((float)nodeY - horizontalDistance * 0.381f);
                branchBaseY = Mathf.Clamp(branchBaseY, surfaceY + 1, trunkTopY);

                if (!IsRuntimeWoodLineClear(centerX, branchBaseY, centerZ, nodeX, nodeY, nodeZ, logBlocks, leafBlocks))
                    continue;

                AddRuntimeFancyOakLeafNode(nodeX, nodeY, nodeZ, leafDistanceLimit, leafBlocks);

                if ((float)(nodeY - surfaceY) >= heightLimit * 0.2f)
                {
                    AddRuntimeWoodLine(centerX, branchBaseY, centerZ, nodeX, nodeY, nodeZ, logBlocks);
                }
            }
        }

        AddRuntimeWoodLine(centerX, surfaceY + 1, centerZ, centerX, trunkTopY, centerZ, logBlocks);
        return true;
    }

    private void AddRuntimeFancyOakLeafNode(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        HashSet<Vector3Int> leafBlocks)
    {
        for (int yOffset = 0; yOffset < leafDistanceLimit; yOffset++)
        {
            float radius = GetRuntimeFancyOakLeafSize(yOffset, leafDistanceLimit);
            if (radius < 0f)
                continue;

            AddRuntimeFancyOakLeafDisc(centerX, centerY + yOffset, centerZ, radius, leafBlocks);
        }
    }

    private void AddRuntimeFancyOakLeafDisc(int centerX, int layerY, int centerZ, float radius, HashSet<Vector3Int> leafBlocks)
    {
        int discRadius = (int)(radius + 0.618f);
        float radiusSq = radius * radius;

        for (int dx = -discRadius; dx <= discRadius; dx++)
        {
            float absDx = Mathf.Abs(dx) + 0.5f;
            float absDxSq = absDx * absDx;

            for (int dz = -discRadius; dz <= discRadius; dz++)
            {
                float absDz = Mathf.Abs(dz) + 0.5f;
                float distSq = absDxSq + absDz * absDz;
                if (distSq > radiusSq)
                    continue;

                leafBlocks.Add(new Vector3Int(centerX + dx, layerY, centerZ + dz));
            }
        }
    }

    private static float GetRuntimeFancyOakLayerSize(int heightLimit, int layerOffset)
    {
        if ((float)layerOffset < heightLimit * 0.3f)
            return -1.618f;

        float halfHeight = heightLimit * 0.5f;
        float distanceFromCenter = halfHeight - layerOffset;

        if (distanceFromCenter == 0f)
            return halfHeight * 0.5f;
        if (Mathf.Abs(distanceFromCenter) >= halfHeight)
            return 0f;

        return Mathf.Sqrt(halfHeight * halfHeight - distanceFromCenter * distanceFromCenter) * 0.5f;
    }

    private static float GetRuntimeFancyOakLeafSize(int layerOffset, int leafDistanceLimit)
    {
        if (layerOffset < 0 || layerOffset >= leafDistanceLimit)
            return -1f;

        return (layerOffset != 0 && layerOffset != leafDistanceLimit - 1) ? 3f : 2f;
    }

    private bool IsRuntimeFancyOakLeafNodeClear(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int maxY = centerY + leafDistanceLimit;
        for (int y = centerY; y <= maxY; y++)
        {
            Vector3Int pos = new Vector3Int(centerX, y, centerZ);
            if (!CanRuntimeLeafReplaceAt(pos, logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private bool IsRuntimeWoodLineClear(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));

        if (steps == 0)
            return CanRuntimeWoodReplaceAt(new Vector3Int(startX, startY, startZ), logBlocks, leafBlocks);

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = Mathf.FloorToInt(startX + dx * t + 0.5f);
            int y = Mathf.FloorToInt(startY + dy * t + 0.5f);
            int z = Mathf.FloorToInt(startZ + dz * t + 0.5f);

            if (!CanRuntimeWoodReplaceAt(new Vector3Int(x, y, z), logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private static void AddRuntimeWoodLine(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        HashSet<Vector3Int> logBlocks)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));

        if (steps == 0)
        {
            logBlocks.Add(new Vector3Int(startX, startY, startZ));
            return;
        }

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = Mathf.FloorToInt(startX + dx * t + 0.5f);
            int y = Mathf.FloorToInt(startY + dy * t + 0.5f);
            int z = Mathf.FloorToInt(startZ + dz * t + 0.5f);

            logBlocks.Add(new Vector3Int(x, y, z));
        }
    }

    private bool CanPlaceRuntimeTree(HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        foreach (Vector3Int logPos in logBlocks)
        {
            if (!CanRuntimeWoodReplaceAt(logPos, logBlocks, leafBlocks))
                return false;
        }

        foreach (Vector3Int leafPos in leafBlocks)
        {
            if (!CanRuntimeLeafReplaceAt(leafPos, logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private bool CanRuntimeLeafReplaceAt(Vector3Int pos, HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        if (pos.y <= 2)
            return false;

        if (leafBlocks.Contains(pos) || logBlocks.Contains(pos))
            return true;

        BlockType existing = GetBlockAt(pos);
        return existing == BlockType.Air ||
               existing == BlockType.Leaves ||
               existing == BlockType.oakTreeSapling;
    }

    private bool CanRuntimeWoodReplaceAt(Vector3Int pos, HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        if (pos.y <= 2)
            return false;

        if (logBlocks.Contains(pos) || leafBlocks.Contains(pos))
            return true;

        BlockType existing = GetBlockAt(pos);
        return existing == BlockType.Air ||
               existing == BlockType.Leaves ||
               existing == BlockType.oakTreeSapling ||
               IsTreeLogBlock(existing);
    }

    private void TryPlaceRuntimeLeaf(Vector3Int pos)
    {
        BlockType existing = GetBlockAt(pos);
        if (existing == BlockType.Air || existing == BlockType.Leaves)
            SetBlockAt(pos, BlockType.Leaves);
    }

    private static float Hash01(int x, int y, int z)
    {
        return (HashInt(x, y, z) & 0x00FFFFFF) / 16777215f;
    }

    private static float Hash01(int value)
    {
        unchecked
        {
            uint x = (uint)value;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static int HashInt(int x, int y, int z)
    {
        unchecked
        {
            int hash = x * 73856093;
            hash ^= y * 83492791;
            hash ^= z * 19349663;
            hash ^= 0x51ED270B;
            hash ^= hash >> 16;
            hash *= unchecked((int)0x7FEB352D);
            hash ^= hash >> 15;
            hash *= unchecked((int)0x846CA68B);
            hash ^= hash >> 16;
            return hash;
        }
    }
}
