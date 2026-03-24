using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    [Serializable]
    private class ChunkBenchmarkScenario
    {
        public string name = "Baseline";
        public bool enabled = true;
        public bool trees = true;
        public bool caves = true;
        public bool ores = true;
        public bool lighting = true;
        public bool grassBillboards = true;
        public bool ambientOcclusion = true;
    }

    private struct ChunkBenchmarkNativeConfig
    {
        public NativeArray<NoiseLayer> noiseLayers;
        public NativeArray<WarpLayer> warpLayers;
        public NativeArray<BlockTextureMapping> blockMappings;
        public NativeArray<byte> effectiveOpacityByBlock;
        public NativeArray<OreSpawnSettings> oreSettings;
        public NativeArray<TreeSpawnRuleData> treeSpawnRules;

        public void Dispose()
        {
            SafeDisposeNativeArray(ref noiseLayers);
            SafeDisposeNativeArray(ref warpLayers);
            SafeDisposeNativeArray(ref blockMappings);
            SafeDisposeNativeArray(ref effectiveOpacityByBlock);
            SafeDisposeNativeArray(ref oreSettings);
            SafeDisposeNativeArray(ref treeSpawnRules);
        }
    }

    private struct ChunkBenchmarkScenarioRuntime
    {
        public bool enableTrees;
        public bool enableLighting;
        public bool enableGrassBillboards;
        public bool enableAmbientOcclusion;
        public CaveGenerationMode caveGenerationMode;
        public WormCaveSettings wormCaves;
        public SpaghettiCaveSettings spaghettiCaves;
    }

    private struct ChunkBenchmarkScheduledMesh
    {
        public JobHandle handle;
        public NativeList<MeshGenerator.PackedChunkVertex> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
        public NativeList<int> waterTriangles;
        public NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges;
        public NativeArray<ulong> subchunkVisibilityMasks;
    }

    private struct ChunkBenchmarkSampleResult
    {
        public string scenarioName;
        public Vector2Int coord;
        public double dataMs;
        public double meshMs;
        public double totalMs;
        public int subchunkCount;
        public int vertexCount;
        public int triangleCount;
    }

    private class ChunkBenchmarkScenarioResult
    {
        public string scenarioName;
        public string scenarioSummary;
        public int sampleCount;
        public double dataMs;
        public double meshMs;
        public double totalMs;
        public double minTotalMs = double.MaxValue;
        public double maxTotalMs = double.MinValue;
        public int subchunkCount;
        public int vertexCount;
        public int triangleCount;
        public double deltaTotalMs;
        public double deltaPercent;

        public double AverageDataMs => sampleCount > 0 ? dataMs / sampleCount : 0d;
        public double AverageMeshMs => sampleCount > 0 ? meshMs / sampleCount : 0d;
        public double AverageTotalMs => sampleCount > 0 ? totalMs / sampleCount : 0d;
        public double AverageSubchunks => sampleCount > 0 ? subchunkCount / (double)sampleCount : 0d;
        public double AverageVertices => sampleCount > 0 ? vertexCount / (double)sampleCount : 0d;
        public double AverageTriangles => sampleCount > 0 ? triangleCount / (double)sampleCount : 0d;

        public void AddSample(ChunkBenchmarkSampleResult sample)
        {
            sampleCount++;
            dataMs += sample.dataMs;
            meshMs += sample.meshMs;
            totalMs += sample.totalMs;
            subchunkCount += sample.subchunkCount;
            vertexCount += sample.vertexCount;
            triangleCount += sample.triangleCount;
            minTotalMs = Math.Min(minTotalMs, sample.totalMs);
            maxTotalMs = Math.Max(maxTotalMs, sample.totalMs);
        }
    }

    [Header("Chunk Benchmark")]
    [SerializeField] private bool showChunkBenchmarkOverlay = false;
    [SerializeField] private bool chunkBenchmarkEnableHotkeys = true;
    [SerializeField] private KeyCode chunkBenchmarkToggleOverlayKey = KeyCode.F8;
    [SerializeField] private KeyCode chunkBenchmarkRunKey = KeyCode.F9;
    [SerializeField, Min(0)] private int chunkBenchmarkSampleRadius = 2;
    [SerializeField, Min(1)] private int chunkBenchmarkMaxSamples = 9;
    [SerializeField, Min(0)] private int chunkBenchmarkWarmupSamples = 1;
    [SerializeField] private bool chunkBenchmarkIncludeCurrentBlockOverrides = true;
    [SerializeField] private bool chunkBenchmarkIncludeSuppressedBillboards = true;
    [SerializeField] private List<ChunkBenchmarkScenario> chunkBenchmarkScenarios = new List<ChunkBenchmarkScenario>();

    private readonly List<ChunkBenchmarkScenarioResult> chunkBenchmarkResults = new List<ChunkBenchmarkScenarioResult>();
    private readonly List<ChunkBenchmarkSampleResult> chunkBenchmarkSamples = new List<ChunkBenchmarkSampleResult>();
    private Coroutine chunkBenchmarkCoroutine;
    private bool chunkBenchmarkRunning;
    private bool chunkBenchmarkCancelRequested;
    private float chunkBenchmarkProgress01;
    private string chunkBenchmarkStatus = string.Empty;
    private string chunkBenchmarkLastCsvPath = string.Empty;
    private Vector2 chunkBenchmarkScroll;
    private Texture2D chunkBenchmarkWhiteTexture;
    private GUIStyle chunkBenchmarkPanelStyle;
    private GUIStyle chunkBenchmarkHeaderStyle;
    private GUIStyle chunkBenchmarkBodyStyle;
    private GUIStyle chunkBenchmarkMutedStyle;

    private void LateUpdate()
    {
        if (!Application.isPlaying || !chunkBenchmarkEnableHotkeys)
            return;

        if (Input.GetKeyDown(chunkBenchmarkToggleOverlayKey))
            showChunkBenchmarkOverlay = !showChunkBenchmarkOverlay;

        if (!Input.GetKeyDown(chunkBenchmarkRunKey))
            return;

        if (chunkBenchmarkRunning)
        {
            chunkBenchmarkCancelRequested = true;
        }
        else
        {
            StartChunkGenerationBenchmark();
        }
    }

    [ContextMenu("Benchmark/Run Chunk Generation Benchmark")]
    private void StartChunkGenerationBenchmarkFromContextMenu()
    {
        StartChunkGenerationBenchmark();
    }

    [ContextMenu("Benchmark/Clear Chunk Benchmark Results")]
    private void ClearChunkGenerationBenchmarkResultsFromContextMenu()
    {
        chunkBenchmarkResults.Clear();
        chunkBenchmarkSamples.Clear();
        chunkBenchmarkLastCsvPath = string.Empty;
        chunkBenchmarkStatus = "Resultados do benchmark limpos.";
    }

    private void StartChunkGenerationBenchmark()
    {
        EnsureChunkBenchmarkScenarios();

        if (!Application.isPlaying)
        {
            chunkBenchmarkStatus = "Entre em Play Mode para rodar o benchmark de chunks.";
            Debug.LogWarning(chunkBenchmarkStatus, this);
            return;
        }

        if (!CanRunChunkGenerationBenchmark(out string reason))
        {
            chunkBenchmarkStatus = reason;
            Debug.LogWarning(reason, this);
            showChunkBenchmarkOverlay = true;
            return;
        }

        if (chunkBenchmarkRunning)
            return;

        chunkBenchmarkResults.Clear();
        chunkBenchmarkSamples.Clear();
        chunkBenchmarkLastCsvPath = string.Empty;
        chunkBenchmarkCancelRequested = false;
        chunkBenchmarkProgress01 = 0f;
        chunkBenchmarkStatus = "Preparando benchmark de chunks...";
        showChunkBenchmarkOverlay = true;
        chunkBenchmarkCoroutine = StartCoroutine(RunChunkGenerationBenchmarkCoroutine());
    }

    private bool CanRunChunkGenerationBenchmark(out string reason)
    {
        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
        {
            reason = "O benchmark precisa de BlockData configurado no World.";
            return false;
        }

        if (Material == null || Material.Length == 0)
        {
            reason = "O benchmark precisa dos materiais do mundo configurados.";
            return false;
        }

        int enabledScenarioCount = 0;
        for (int i = 0; i < chunkBenchmarkScenarios.Count; i++)
        {
            if (chunkBenchmarkScenarios[i] != null && chunkBenchmarkScenarios[i].enabled)
                enabledScenarioCount++;
        }

        if (enabledScenarioCount == 0)
        {
            reason = "Ative pelo menos um cenario no Chunk Benchmark.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private IEnumerator RunChunkGenerationBenchmarkCoroutine()
    {
        chunkBenchmarkRunning = true;
        IEnumerator body = RunChunkGenerationBenchmarkBodyCoroutine();
        Exception failure = null;

        while (true)
        {
            object current = null;
            bool hasNext = false;

            try
            {
                hasNext = body.MoveNext();
                if (hasNext)
                    current = body.Current;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            if (!hasNext)
                break;

            yield return current;
        }

        if (body is IDisposable disposable)
            disposable.Dispose();

        if (failure != null)
        {
            chunkBenchmarkStatus = $"Erro no benchmark de chunks: {failure.Message}";
            Debug.LogException(failure, this);
        }

        chunkBenchmarkRunning = false;
        chunkBenchmarkCancelRequested = false;
        chunkBenchmarkCoroutine = null;
        chunkBenchmarkProgress01 = chunkBenchmarkResults.Count > 0 ? 1f : 0f;
    }

    private IEnumerator RunChunkGenerationBenchmarkBodyCoroutine()
    {
        yield return WaitForChunkBenchmarkWorldToGoIdle();

        List<ChunkBenchmarkScenario> enabledScenarios = GetEnabledChunkBenchmarkScenarios();
        List<Vector2Int> sampleCoords = BuildChunkBenchmarkSampleCoords();
        if (sampleCoords.Count == 0)
        {
            chunkBenchmarkStatus = "Nao foi possivel definir chunks de amostra para o benchmark.";
            yield break;
        }

        int totalSteps = Mathf.Max(1, enabledScenarios.Count * sampleCoords.Count);
        int completedSteps = 0;

        for (int scenarioIndex = 0; scenarioIndex < enabledScenarios.Count; scenarioIndex++)
        {
            if (chunkBenchmarkCancelRequested)
                break;

            ChunkBenchmarkScenario scenario = enabledScenarios[scenarioIndex];
            ChunkBenchmarkScenarioRuntime runtime = BuildChunkBenchmarkScenarioRuntime(scenario);
            ChunkBenchmarkNativeConfig nativeConfig = BuildChunkBenchmarkNativeConfig(scenario);
            ChunkBenchmarkScenarioResult scenarioResult = new ChunkBenchmarkScenarioResult
            {
                scenarioName = GetChunkBenchmarkScenarioName(scenario, scenarioIndex),
                scenarioSummary = DescribeChunkBenchmarkScenario(scenario)
            };

            try
            {
                int warmupCount = Mathf.Min(chunkBenchmarkWarmupSamples, sampleCoords.Count);
                for (int warmupIndex = 0; warmupIndex < warmupCount; warmupIndex++)
                {
                    if (chunkBenchmarkCancelRequested)
                        break;

                    chunkBenchmarkStatus = $"Aquecendo {scenarioResult.scenarioName} ({warmupIndex + 1}/{warmupCount})...";
                    RunSingleChunkGenerationBenchmarkSample(
                        scenarioResult.scenarioName,
                        sampleCoords[warmupIndex],
                        runtime,
                        nativeConfig);
                    yield return null;
                }

                for (int sampleIndex = 0; sampleIndex < sampleCoords.Count; sampleIndex++)
                {
                    if (chunkBenchmarkCancelRequested)
                        break;

                    Vector2Int coord = sampleCoords[sampleIndex];
                    chunkBenchmarkStatus = $"Medindo {scenarioResult.scenarioName} ({sampleIndex + 1}/{sampleCoords.Count})...";

                    ChunkBenchmarkSampleResult sample = RunSingleChunkGenerationBenchmarkSample(
                        scenarioResult.scenarioName,
                        coord,
                        runtime,
                        nativeConfig);

                    scenarioResult.AddSample(sample);
                    chunkBenchmarkSamples.Add(sample);

                    completedSteps++;
                    chunkBenchmarkProgress01 = completedSteps / (float)totalSteps;
                    yield return null;
                }
            }
            finally
            {
                nativeConfig.Dispose();
            }

            if (scenarioResult.sampleCount > 0)
                chunkBenchmarkResults.Add(scenarioResult);
        }

        if (chunkBenchmarkResults.Count > 0)
        {
            ApplyChunkBenchmarkDeltasFromBaseline();
            chunkBenchmarkLastCsvPath = ExportChunkBenchmarkCsv();
            if (chunkBenchmarkCancelRequested)
                chunkBenchmarkStatus = "Benchmark cancelado. Resultados parciais exportados.";
            else
                chunkBenchmarkStatus = $"Benchmark concluido. CSV salvo em: {chunkBenchmarkLastCsvPath}";

            Debug.Log(chunkBenchmarkStatus, this);
        }
        else if (chunkBenchmarkCancelRequested)
        {
            chunkBenchmarkStatus = "Benchmark cancelado antes de gerar resultados.";
        }
        else
        {
            chunkBenchmarkStatus = "Benchmark finalizado sem resultados validos.";
        }
    }

    private IEnumerator WaitForChunkBenchmarkWorldToGoIdle()
    {
        const int maxFramesToWait = 240;

        for (int frame = 0; frame < maxFramesToWait; frame++)
        {
            bool worldIsIdle =
                pendingChunks.Count == 0 &&
                pendingDataJobs.Count == 0 &&
                pendingMeshes.Count == 0 &&
                queuedChunkRebuilds.Count == 0 &&
                queuedColliderBuilds.Count == 0;

            if (worldIsIdle)
                yield break;

            if (chunkBenchmarkCancelRequested)
                yield break;

            chunkBenchmarkStatus = "Aguardando a fila de chunks estabilizar para medir com menos ruido...";
            yield return null;
        }
    }

    private List<ChunkBenchmarkScenario> GetEnabledChunkBenchmarkScenarios()
    {
        EnsureChunkBenchmarkScenarios();

        List<ChunkBenchmarkScenario> enabled = new List<ChunkBenchmarkScenario>(chunkBenchmarkScenarios.Count);
        for (int i = 0; i < chunkBenchmarkScenarios.Count; i++)
        {
            ChunkBenchmarkScenario scenario = chunkBenchmarkScenarios[i];
            if (scenario != null && scenario.enabled)
                enabled.Add(scenario);
        }

        return enabled;
    }

    private List<Vector2Int> BuildChunkBenchmarkSampleCoords()
    {
        Vector2Int center = GetCurrentPlayerChunkCoord();
        int radius = Mathf.Max(0, chunkBenchmarkSampleRadius);
        int maxSamples = Mathf.Max(1, chunkBenchmarkMaxSamples);
        List<Vector2Int> coords = new List<Vector2Int>();

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                Vector2Int coord = new Vector2Int(center.x + dx, center.y + dz);
                if (!IsCoordInsideCircularDistance(coord, center, radius))
                    continue;

                coords.Add(coord);
            }
        }

        coords.Sort((a, b) =>
        {
            int aDist = (a.x - center.x) * (a.x - center.x) + (a.y - center.y) * (a.y - center.y);
            int bDist = (b.x - center.x) * (b.x - center.x) + (b.y - center.y) * (b.y - center.y);
            if (aDist != bDist)
                return aDist.CompareTo(bDist);

            int xCompare = a.x.CompareTo(b.x);
            return xCompare != 0 ? xCompare : a.y.CompareTo(b.y);
        });

        if (coords.Count > maxSamples)
            coords.RemoveRange(maxSamples, coords.Count - maxSamples);

        if (coords.Count == 0)
            coords.Add(center);

        return coords;
    }

    private ChunkBenchmarkScenarioRuntime BuildChunkBenchmarkScenarioRuntime(ChunkBenchmarkScenario scenario)
    {
        ChunkBenchmarkScenarioRuntime runtime = new ChunkBenchmarkScenarioRuntime
        {
            enableTrees = scenario.trees,
            enableLighting = scenario.lighting,
            enableGrassBillboards = scenario.grassBillboards,
            enableAmbientOcclusion = scenario.ambientOcclusion,
            caveGenerationMode = caveGenerationMode,
            wormCaves = caveWormSettings,
            spaghettiCaves = caveSpaghettiSettings
        };

        if (!scenario.caves)
        {
            runtime.wormCaves.enabled = false;
            runtime.spaghettiCaves.enabled = false;
        }

        return runtime;
    }

    private ChunkBenchmarkNativeConfig BuildChunkBenchmarkNativeConfig(ChunkBenchmarkScenario scenario)
    {
        NoiseLayer[] runtimeNoiseLayers = noiseLayers ?? Array.Empty<NoiseLayer>();
        WarpLayer[] runtimeWarpLayers = warpLayers ?? Array.Empty<WarpLayer>();
        BlockTextureMapping[] runtimeBlockMappings = blockData != null && blockData.mappings != null
            ? blockData.mappings
            : Array.Empty<BlockTextureMapping>();
        TreeSpawnRuleData[] runtimeTreeSpawnRules = GetActiveTreeSpawnRules();
        OreSpawnSettings[] runtimeOreSettings = BuildChunkBenchmarkOreSettings(scenario.ores);

        ChunkBenchmarkNativeConfig config = new ChunkBenchmarkNativeConfig
        {
            noiseLayers = new NativeArray<NoiseLayer>(runtimeNoiseLayers, Allocator.Persistent),
            warpLayers = new NativeArray<WarpLayer>(runtimeWarpLayers, Allocator.Persistent),
            blockMappings = new NativeArray<BlockTextureMapping>(runtimeBlockMappings, Allocator.Persistent),
            effectiveOpacityByBlock = new NativeArray<byte>(runtimeBlockMappings.Length, Allocator.Persistent),
            oreSettings = new NativeArray<OreSpawnSettings>(runtimeOreSettings, Allocator.Persistent),
            treeSpawnRules = new NativeArray<TreeSpawnRuleData>(runtimeTreeSpawnRules, Allocator.Persistent)
        };

        for (int i = 0; i < runtimeBlockMappings.Length; i++)
            config.effectiveOpacityByBlock[i] = ChunkLighting.GetEffectiveOpacity(runtimeBlockMappings[i]);

        return config;
    }

    private OreSpawnSettings[] BuildChunkBenchmarkOreSettings(bool oresEnabled)
    {
        OreSpawnSettings[] source = oreSettings ?? Array.Empty<OreSpawnSettings>();
        OreSpawnSettings[] copy = new OreSpawnSettings[source.Length];
        Array.Copy(source, copy, source.Length);

        if (!oresEnabled)
        {
            for (int i = 0; i < copy.Length; i++)
                copy[i].enabled = false;
        }

        return copy;
    }

    private ChunkBenchmarkSampleResult RunSingleChunkGenerationBenchmarkSample(
        string scenarioName,
        Vector2Int coord,
        ChunkBenchmarkScenarioRuntime runtime,
        ChunkBenchmarkNativeConfig nativeConfig)
    {
        ChunkBenchmarkSampleResult result = new ChunkBenchmarkSampleResult
        {
            scenarioName = scenarioName,
            coord = coord
        };

        NativeArray<BlockEdit> nativeEdits = default;
        NativeArray<byte> chunkLightData = default;
        NativeArray<byte> chunkVoxelSnapshot = default;
        NativeArray<int> heightCache = default;
        NativeArray<byte> blockTypes = default;
        NativeArray<bool> solids = default;
        NativeArray<byte> light = default;
        NativeArray<byte> lightOpacityData = default;
        NativeArray<bool> subchunkNonEmpty = default;
        NativeArray<byte> knownVoxelData = default;
        NativeArray<int3> suppressedBillboards = default;
        List<ChunkBenchmarkScheduledMesh> scheduledMeshes = new List<ChunkBenchmarkScheduledMesh>(Chunk.SubchunksPerColumn);
        Stopwatch totalWatch = Stopwatch.StartNew();

        try
        {
            int dataBorderSize = Mathf.Max(GetMeshNeighborPadding(), detailedGenerationPadding);
            int lightBorderSize = runtime.enableLighting
                ? Mathf.Max(GetMeshNeighborPadding(), sunlightSmoothingPadding)
                : GetMeshNeighborPadding();
            int detailBorderSize = dataBorderSize;
            int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
            int treeMargin = GetMaxTreeMarginForGeneration();
            int maxTreeRadius = GetMaxTreeRadiusForGeneration();
            int chunkMinX = coord.x * Chunk.SizeX;
            int chunkMinZ = coord.y * Chunk.SizeZ;

            nativeEdits = BuildChunkBenchmarkEditArray(coord, overrideBorderSize);
            chunkVoxelSnapshot = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);

            if (runtime.enableLighting)
            {
                int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
                int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
                int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
                chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.Persistent);
                InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            }

            Stopwatch dataWatch = Stopwatch.StartNew();
            MeshGenerator.ScheduleDataJob(
                coord,
                nativeConfig.noiseLayers,
                nativeConfig.warpLayers,
                nativeConfig.blockMappings,
                nativeConfig.effectiveOpacityByBlock,
                baseHeight,
                offsetX,
                offsetZ,
                seaLevel,
                GetBiomeNoiseSettings(),
                seed,
                nativeEdits,
                treeMargin,
                dataBorderSize,
                lightBorderSize,
                detailBorderSize,
                maxTreeRadius,
                CliffTreshold,
                runtime.enableTrees,
                nativeConfig.oreSettings,
                nativeConfig.treeSpawnRules,
                runtime.caveGenerationMode,
                runtime.wormCaves,
                runtime.spaghettiCaves,
                runtime.enableLighting,
                chunkLightData,
                chunkVoxelSnapshot,
                out JobHandle dataHandle,
                out heightCache,
                out blockTypes,
                out solids,
                out light,
                out lightOpacityData,
                out subchunkNonEmpty);
            dataHandle.Complete();
            dataWatch.Stop();
            result.dataMs = dataWatch.Elapsed.TotalMilliseconds;

            knownVoxelData = CreateFullyKnownVoxelMask(blockTypes.Length);
            suppressedBillboards = BuildChunkBenchmarkSuppressedBillboardArray(coord, runtime.enableGrassBillboards);

            Stopwatch meshWatch = Stopwatch.StartNew();
            JobHandle combinedMeshHandle = default;
            bool hasMeshJobs = false;
            float effectiveAoStrength = runtime.enableAmbientOcclusion ? aoStrength : 0f;

            for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
            {
                if (!subchunkNonEmpty.IsCreated || !subchunkNonEmpty[sub])
                    continue;

                MeshGenerator.ScheduleMeshJob(
                    heightCache,
                    blockTypes,
                    solids,
                    light,
                    nativeConfig.blockMappings,
                    suppressedBillboards,
                    subchunkNonEmpty,
                    knownVoxelData,
                    atlasTilesX,
                    atlasTilesY,
                    true,
                    Mathf.Max(1, dataBorderSize),
                    coord.x,
                    coord.y,
                    1 << sub,
                    runtime.enableGrassBillboards,
                    grassBillboardChance,
                    grassBillboardBlockType,
                    grassBillboardHeight,
                    grassBillboardNoiseScale,
                    grassBillboardJitter,
                    effectiveAoStrength,
                    aoCurveExponent,
                    aoMinLight,
                    useFastBedrockStyleMeshing,
                    out JobHandle meshHandle,
                    out NativeList<MeshGenerator.PackedChunkVertex> vertices,
                    out NativeList<int> opaqueTriangles,
                    out NativeList<int> transparentTriangles,
                    out NativeList<int> billboardTriangles,
                    out NativeList<int> waterTriangles,
                    out NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
                    out NativeArray<ulong> subchunkVisibilityMasks);

                ChunkBenchmarkScheduledMesh scheduled = new ChunkBenchmarkScheduledMesh
                {
                    handle = meshHandle,
                    vertices = vertices,
                    opaqueTriangles = opaqueTriangles,
                    transparentTriangles = transparentTriangles,
                    billboardTriangles = billboardTriangles,
                    waterTriangles = waterTriangles,
                    subchunkRanges = subchunkRanges,
                    subchunkVisibilityMasks = subchunkVisibilityMasks
                };

                scheduledMeshes.Add(scheduled);
                combinedMeshHandle = hasMeshJobs
                    ? JobHandle.CombineDependencies(combinedMeshHandle, meshHandle)
                    : meshHandle;
                hasMeshJobs = true;
            }

            if (hasMeshJobs)
                combinedMeshHandle.Complete();

            meshWatch.Stop();
            result.meshMs = meshWatch.Elapsed.TotalMilliseconds;

            for (int i = 0; i < scheduledMeshes.Count; i++)
            {
                ChunkBenchmarkScheduledMesh scheduled = scheduledMeshes[i];
                result.subchunkCount++;
                result.vertexCount += scheduled.vertices.IsCreated ? scheduled.vertices.Length : 0;

                int triangleCount = 0;
                triangleCount += scheduled.opaqueTriangles.IsCreated ? scheduled.opaqueTriangles.Length : 0;
                triangleCount += scheduled.transparentTriangles.IsCreated ? scheduled.transparentTriangles.Length : 0;
                triangleCount += scheduled.billboardTriangles.IsCreated ? scheduled.billboardTriangles.Length : 0;
                triangleCount += scheduled.waterTriangles.IsCreated ? scheduled.waterTriangles.Length : 0;
                result.triangleCount += triangleCount / 3;
            }

            totalWatch.Stop();
            result.totalMs = totalWatch.Elapsed.TotalMilliseconds;
            return result;
        }
        finally
        {
            for (int i = 0; i < scheduledMeshes.Count; i++)
            {
                ChunkBenchmarkScheduledMesh scheduled = scheduledMeshes[i];
                DisposeChunkBenchmarkScheduledMesh(ref scheduled);
            }

            SafeDisposeNativeArray(ref nativeEdits);
            SafeDisposeNativeArray(ref chunkLightData);
            SafeDisposeNativeArray(ref chunkVoxelSnapshot);
            SafeDisposeNativeArray(ref heightCache);
            SafeDisposeNativeArray(ref blockTypes);
            SafeDisposeNativeArray(ref solids);
            SafeDisposeNativeArray(ref light);
            SafeDisposeNativeArray(ref lightOpacityData);
            SafeDisposeNativeArray(ref subchunkNonEmpty);
            SafeDisposeNativeArray(ref knownVoxelData);
            SafeDisposeNativeArray(ref suppressedBillboards);
        }
    }

    private NativeArray<BlockEdit> BuildChunkBenchmarkEditArray(Vector2Int coord, int borderSize)
    {
        if (!chunkBenchmarkIncludeCurrentBlockOverrides)
            return new NativeArray<BlockEdit>(0, Allocator.Persistent);

        List<BlockEdit> editsList = new List<BlockEdit>(32);
        AppendRelevantBlockEdits(coord, borderSize, editsList);
        NativeArray<BlockEdit> nativeEdits = new NativeArray<BlockEdit>(editsList.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < editsList.Count; i++)
            nativeEdits[i] = editsList[i];

        return nativeEdits;
    }

    private NativeArray<int3> BuildChunkBenchmarkSuppressedBillboardArray(Vector2Int coord, bool enableGrassBillboardsForScenario)
    {
        if (!enableGrassBillboardsForScenario || !chunkBenchmarkIncludeSuppressedBillboards)
            return new NativeArray<int3>(0, Allocator.Persistent);

        List<int3> suppressed = GetSuppressedGrassBillboardsForChunk(coord);
        NativeArray<int3> nativeSuppressed = new NativeArray<int3>(suppressed.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < suppressed.Count; i++)
            nativeSuppressed[i] = suppressed[i];

        return nativeSuppressed;
    }

    private void DisposeChunkBenchmarkScheduledMesh(ref ChunkBenchmarkScheduledMesh scheduled)
    {
        try
        {
            if (scheduled.vertices.IsCreated) scheduled.vertices.Dispose();
            if (scheduled.opaqueTriangles.IsCreated) scheduled.opaqueTriangles.Dispose();
            if (scheduled.transparentTriangles.IsCreated) scheduled.transparentTriangles.Dispose();
            if (scheduled.billboardTriangles.IsCreated) scheduled.billboardTriangles.Dispose();
            if (scheduled.waterTriangles.IsCreated) scheduled.waterTriangles.Dispose();
            if (scheduled.subchunkRanges.IsCreated) scheduled.subchunkRanges.Dispose();
            if (scheduled.subchunkVisibilityMasks.IsCreated) scheduled.subchunkVisibilityMasks.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        scheduled = default;
    }

    private void ApplyChunkBenchmarkDeltasFromBaseline()
    {
        if (chunkBenchmarkResults.Count == 0)
            return;

        double baseline = chunkBenchmarkResults[0].AverageTotalMs;
        if (baseline <= 0d)
            baseline = 1d;

        for (int i = 0; i < chunkBenchmarkResults.Count; i++)
        {
            ChunkBenchmarkScenarioResult result = chunkBenchmarkResults[i];
            result.deltaTotalMs = result.AverageTotalMs - baseline;
            result.deltaPercent = ((result.AverageTotalMs / baseline) - 1d) * 100d;
        }
    }

    private string ExportChunkBenchmarkCsv()
    {
        if (chunkBenchmarkResults.Count == 0)
            return string.Empty;

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
            projectRoot = Application.dataPath;

        string directory = Path.Combine(projectRoot, "Logs", "ChunkBenchmark");
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"chunk-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        StringBuilder builder = new StringBuilder(8192);
        builder.AppendLine("section,scenario,summary,chunk_x,chunk_z,data_ms,mesh_ms,total_ms,subchunks,vertices,triangles,delta_ms,delta_percent");

        for (int i = 0; i < chunkBenchmarkResults.Count; i++)
        {
            ChunkBenchmarkScenarioResult result = chunkBenchmarkResults[i];
            builder.Append("summary,");
            builder.Append(EscapeChunkBenchmarkCsv(result.scenarioName));
            builder.Append(',');
            builder.Append(EscapeChunkBenchmarkCsv(result.scenarioSummary));
            builder.Append(",,,");
            builder.Append(result.AverageDataMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.AverageMeshMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.AverageTotalMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.AverageSubchunks.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.AverageVertices.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.AverageTriangles.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.deltaTotalMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(result.deltaPercent.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.AppendLine();
        }

        for (int i = 0; i < chunkBenchmarkSamples.Count; i++)
        {
            ChunkBenchmarkSampleResult sample = chunkBenchmarkSamples[i];
            builder.Append("sample,");
            builder.Append(EscapeChunkBenchmarkCsv(sample.scenarioName));
            builder.Append(',');
            builder.Append(',');
            builder.Append(sample.coord.x.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.coord.y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.dataMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.meshMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.totalMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.subchunkCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.vertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.triangleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.AppendLine(",,");
        }

        File.WriteAllText(filePath, builder.ToString(), Encoding.UTF8);
        return filePath;
    }

    private static string EscapeChunkBenchmarkCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuotes)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private string GetChunkBenchmarkScenarioName(ChunkBenchmarkScenario scenario, int index)
    {
        if (scenario == null || string.IsNullOrWhiteSpace(scenario.name))
            return $"Cenario {index + 1}";

        return scenario.name.Trim();
    }

    private static string DescribeChunkBenchmarkScenario(ChunkBenchmarkScenario scenario)
    {
        if (scenario == null)
            return "Cenario invalido";

        return
            $"arvores {(scenario.trees ? "on" : "off")}, " +
            $"cavernas {(scenario.caves ? "on" : "off")}, " +
            $"minerio {(scenario.ores ? "on" : "off")}, " +
            $"luz {(scenario.lighting ? "on" : "off")}, " +
            $"grama {(scenario.grassBillboards ? "on" : "off")}, " +
            $"AO {(scenario.ambientOcclusion ? "on" : "off")}";
    }

    private void EnsureChunkBenchmarkScenarios()
    {
        if (chunkBenchmarkScenarios != null && chunkBenchmarkScenarios.Count > 0)
            return;

        chunkBenchmarkScenarios = new List<ChunkBenchmarkScenario>
        {
            new ChunkBenchmarkScenario { name = "Baseline", enabled = true, trees = true, caves = true, ores = true, lighting = true, grassBillboards = true, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem Arvores", enabled = true, trees = false, caves = true, ores = true, lighting = true, grassBillboards = true, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem Cavernas", enabled = true, trees = true, caves = false, ores = true, lighting = true, grassBillboards = true, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem Minerios", enabled = true, trees = true, caves = true, ores = false, lighting = true, grassBillboards = true, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem Iluminacao", enabled = true, trees = true, caves = true, ores = true, lighting = false, grassBillboards = true, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem Grama Billboard", enabled = true, trees = true, caves = true, ores = true, lighting = true, grassBillboards = false, ambientOcclusion = true },
            new ChunkBenchmarkScenario { name = "Sem AO", enabled = true, trees = true, caves = true, ores = true, lighting = true, grassBillboards = true, ambientOcclusion = false },
            new ChunkBenchmarkScenario { name = "Terreno Cru", enabled = true, trees = false, caves = false, ores = false, lighting = false, grassBillboards = false, ambientOcclusion = false }
        };
    }

    private void EnsureChunkBenchmarkGuiStyles()
    {
        if (chunkBenchmarkWhiteTexture == null)
        {
            chunkBenchmarkWhiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            chunkBenchmarkWhiteTexture.SetPixel(0, 0, Color.white);
            chunkBenchmarkWhiteTexture.Apply();
        }

        if (chunkBenchmarkPanelStyle == null)
        {
            chunkBenchmarkPanelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 12, 12)
            };
        }

        if (chunkBenchmarkHeaderStyle == null)
        {
            chunkBenchmarkHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };
        }

        if (chunkBenchmarkBodyStyle == null)
        {
            chunkBenchmarkBodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true
            };
        }

        if (chunkBenchmarkMutedStyle == null)
        {
            chunkBenchmarkMutedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = new Color(0.82f, 0.82f, 0.82f, 1f) }
            };
        }
    }

    private void OnGUI()
    {
        if (!showChunkBenchmarkOverlay && !chunkBenchmarkRunning)
            return;

        EnsureChunkBenchmarkScenarios();
        EnsureChunkBenchmarkGuiStyles();

        float width = Mathf.Min(700f, Screen.width - 24f);
        float height = Mathf.Min(680f, Screen.height - 24f);
        Rect panelRect = new Rect(12f, 12f, width, height);

        GUI.Box(panelRect, GUIContent.none, chunkBenchmarkPanelStyle);
        GUILayout.BeginArea(new Rect(panelRect.x + 10f, panelRect.y + 10f, panelRect.width - 20f, panelRect.height - 20f));

        GUILayout.Label("Chunk Benchmark", chunkBenchmarkHeaderStyle);
        GUILayout.Label(
            $"Amostras: {Mathf.Max(1, chunkBenchmarkMaxSamples)} chunks em raio {Mathf.Max(0, chunkBenchmarkSampleRadius)} | Hotkeys: {chunkBenchmarkToggleOverlayKey} overlay, {chunkBenchmarkRunKey} {(chunkBenchmarkRunning ? "cancela" : "roda")}",
            chunkBenchmarkMutedStyle);

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        GUI.enabled = !chunkBenchmarkRunning;
        if (GUILayout.Button("Rodar Benchmark", GUILayout.Height(28f)))
            StartChunkGenerationBenchmark();
        GUI.enabled = true;

        if (chunkBenchmarkRunning)
        {
            if (GUILayout.Button("Cancelar", GUILayout.Height(28f)))
                chunkBenchmarkCancelRequested = true;
        }
        else
        {
            if (GUILayout.Button("Limpar", GUILayout.Height(28f)))
            {
                chunkBenchmarkResults.Clear();
                chunkBenchmarkSamples.Clear();
                chunkBenchmarkLastCsvPath = string.Empty;
                chunkBenchmarkStatus = "Resultados do benchmark limpos.";
            }
        }

        if (GUILayout.Button("Fechar", GUILayout.Height(28f)))
            showChunkBenchmarkOverlay = false;
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        if (!string.IsNullOrEmpty(chunkBenchmarkStatus))
            GUILayout.Label(chunkBenchmarkStatus, chunkBenchmarkBodyStyle);

        if (chunkBenchmarkRunning)
        {
            Rect progressRect = GUILayoutUtility.GetRect(10f, 18f, GUILayout.ExpandWidth(true));
            DrawChunkBenchmarkProgressBar(progressRect, chunkBenchmarkProgress01, new Color(0.18f, 0.74f, 0.44f, 1f));
            GUILayout.Space(4f);
        }

        if (!string.IsNullOrEmpty(chunkBenchmarkLastCsvPath))
            GUILayout.Label($"CSV: {chunkBenchmarkLastCsvPath}", chunkBenchmarkMutedStyle);

        GUILayout.Space(8f);

        chunkBenchmarkScroll = GUILayout.BeginScrollView(chunkBenchmarkScroll, GUILayout.ExpandHeight(true));
        if (chunkBenchmarkResults.Count == 0)
        {
            GUILayout.Label("Nenhum resultado ainda. Rode o benchmark para comparar o peso de arvores, cavernas, iluminacao, grama e AO.", chunkBenchmarkBodyStyle);
        }
        else
        {
            double maxAverage = 0d;
            for (int i = 0; i < chunkBenchmarkResults.Count; i++)
                maxAverage = Math.Max(maxAverage, chunkBenchmarkResults[i].AverageTotalMs);

            for (int i = 0; i < chunkBenchmarkResults.Count; i++)
            {
                ChunkBenchmarkScenarioResult result = chunkBenchmarkResults[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label($"{result.scenarioName}  |  {result.AverageTotalMs:F2} ms/chunk", chunkBenchmarkHeaderStyle);
                GUILayout.Label(result.scenarioSummary, chunkBenchmarkMutedStyle);

                Rect barRect = GUILayoutUtility.GetRect(10f, 16f, GUILayout.ExpandWidth(true));
                float normalized = maxAverage > 0d ? Mathf.Clamp01((float)(result.AverageTotalMs / maxAverage)) : 0f;
                DrawChunkBenchmarkBar(barRect, normalized, GetChunkBenchmarkBarColor(result));

                GUILayout.Label(
                    $"data {result.AverageDataMs:F2} ms | mesh {result.AverageMeshMs:F2} ms | delta {FormatChunkBenchmarkDelta(result)}",
                    chunkBenchmarkBodyStyle);
                GUILayout.Label(
                    $"subchunks/chunk {result.AverageSubchunks:F2} | vertices/chunk {result.AverageVertices:F0} | triangulos/chunk {result.AverageTriangles:F0} | min {result.minTotalMs:F2} | max {result.maxTotalMs:F2}",
                    chunkBenchmarkMutedStyle);
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawChunkBenchmarkProgressBar(Rect rect, float progress01, Color fillColor)
    {
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(rect, chunkBenchmarkWhiteTexture);

        Rect fillRect = rect;
        fillRect.width = Mathf.Max(0f, rect.width * Mathf.Clamp01(progress01));

        GUI.color = fillColor;
        GUI.DrawTexture(fillRect, chunkBenchmarkWhiteTexture);
        GUI.color = Color.white;
    }

    private void DrawChunkBenchmarkBar(Rect rect, float normalizedWidth, Color fillColor)
    {
        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(rect, chunkBenchmarkWhiteTexture);

        Rect fillRect = rect;
        fillRect.width = Mathf.Max(0f, rect.width * Mathf.Clamp01(normalizedWidth));

        GUI.color = fillColor;
        GUI.DrawTexture(fillRect, chunkBenchmarkWhiteTexture);
        GUI.color = Color.white;
    }

    private static Color GetChunkBenchmarkBarColor(ChunkBenchmarkScenarioResult result)
    {
        if (Math.Abs(result.deltaTotalMs) < 0.0001d)
            return new Color(0.3f, 0.7f, 1f, 0.95f);

        if (result.deltaTotalMs < 0d)
            return new Color(0.24f, 0.82f, 0.44f, 0.95f);

        return new Color(0.95f, 0.48f, 0.25f, 0.95f);
    }

    private static string FormatChunkBenchmarkDelta(ChunkBenchmarkScenarioResult result)
    {
        if (Math.Abs(result.deltaTotalMs) < 0.0001d)
            return "baseline";

        string signal = result.deltaTotalMs > 0d ? "+" : string.Empty;
        return $"{signal}{result.deltaTotalMs:F2} ms ({signal}{result.deltaPercent:F1}%)";
    }
}
