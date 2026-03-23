using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public partial class World : MonoBehaviour
{
    [Header("Simple Profiler")]
    [Tooltip("Ativa um profiler leve em runtime para medir geracao de chunks e identificar lag spikes.")]
    public bool enableSimpleProfiler = true;
    [Tooltip("Mostra um HUD simples com os dados do profiler. Pode ser alternado com a tecla configurada.")]
    public bool showSimpleProfilerOverlay = true;
    [SerializeField] private KeyCode simpleProfilerToggleKey = KeyCode.F8;
    [SerializeField, Min(0.05f)] private float simpleProfilerOverlayRefreshInterval = 0.2f;
    [SerializeField, Min(10)] private int simpleProfilerFrameHistorySize = 180;
    [SerializeField, Min(4)] private int simpleProfilerChunkHistorySize = 24;
    [SerializeField, Min(4)] private int simpleProfilerLagSpikeHistorySize = 12;
    [SerializeField, Min(12f)] private float simpleProfilerLagSpikeThresholdMs = 28f;
    [SerializeField, Range(1.05f, 3f)] private float simpleProfilerLagSpikeAverageMultiplier = 1.45f;
    [SerializeField, Min(20f)] private float simpleProfilerSevereLagSpikeThresholdMs = 55f;
    [SerializeField, Min(0.25f)] private float simpleProfilerSevereSpikeLogCooldown = 1.5f;
    [SerializeField] private bool logSevereLagSpikes = false;

    private const float SimpleProfilerChunkProfileTimeoutSeconds = 30f;
    private const float SimpleProfilerOverlayWidth = 540f;

    private enum SimpleProfilerStage : byte
    {
        BlockColliderToggle = 0,
        VisualFeatureToggle = 1,
        WaterUpdates = 2,
        ChunkRebuildQueue = 3,
        HighBuildRebuildQueue = 4,
        LeafDecay = 5,
        UpdateChunks = 6,
        SimulationRefresh = 7,
        ColliderBuilds = 8,
        DataJobProcessing = 9,
        MeshApply = 10,
        SectionOcclusion = 11,
        Count = 12
    }

    private enum ChunkProfileKind : byte
    {
        NewChunk = 0,
        Rebuild = 1,
        FastRebuild = 2
    }

    private enum ProfilerFeedbackLevel : byte
    {
        Good = 0,
        Warning = 1,
        Critical = 2
    }

    private sealed class ActiveChunkProfile
    {
        public Vector2Int coord;
        public int generation;
        public ChunkProfileKind kind;
        public int dirtySubchunkMask;
        public int dirtySubchunkCount;
        public double scheduledAt;
        public double dataReadyAt;
        public double meshScheduledAt;
        public int appliedSubchunkCount;
        public bool hasDataReady;
        public bool hasMeshScheduled;
    }

    private struct ChunkProfileSnapshot
    {
        public Vector2Int coord;
        public ChunkProfileKind kind;
        public int dirtySubchunkCount;
        public int appliedSubchunkCount;
        public float dataReadyMs;
        public float meshReadyMs;
        public float totalVisualMs;
        public float completedAtRealtime;
    }

    private struct LagSpikeSnapshot
    {
        public float frameMs;
        public float averageFrameMs;
        public SimpleProfilerStage primaryStage;
        public float primaryStageMs;
        public SimpleProfilerStage secondaryStage;
        public float secondaryStageMs;
        public string cause;
        public int chunksRequested;
        public int rebuildsProcessed;
        public int dataJobsCompleted;
        public int subchunksApplied;
        public int colliderBuildsProcessed;
        public float capturedAtRealtime;
    }

    private struct ChunkHistoryStats
    {
        public int sampleCount;
        public float averageTotalMs;
        public float averageDataMs;
        public float averageMeshMs;
        public float slowestTotalMs;
    }

    private static readonly string[] SimpleProfilerStageLabels =
    {
        "toggle colliders",
        "toggle visual",
        "agua",
        "fila rebuild",
        "high build",
        "leaf decay",
        "update chunks",
        "simulation distance",
        "colliders",
        "data jobs",
        "mesh",
        "occlusion"
    };

    private readonly Dictionary<int, ActiveChunkProfile> simpleProfilerActiveChunks = new Dictionary<int, ActiveChunkProfile>();
    private readonly Dictionary<Vector2Int, int> simpleProfilerGenerationByCoord = new Dictionary<Vector2Int, int>();
    private readonly List<ChunkProfileSnapshot> simpleProfilerRecentChunks = new List<ChunkProfileSnapshot>();
    private readonly List<LagSpikeSnapshot> simpleProfilerRecentLagSpikes = new List<LagSpikeSnapshot>();
    private readonly List<int> simpleProfilerStaleGenerations = new List<int>();
    private readonly float[] simpleProfilerStageTimesMs = new float[(int)SimpleProfilerStage.Count];
    private readonly StringBuilder simpleProfilerOverlayBuilder = new StringBuilder(1024);

    private float[] simpleProfilerFrameHistory = Array.Empty<float>();
    private float[] simpleProfilerFrameSortScratch = Array.Empty<float>();
    private int simpleProfilerFrameHistoryWriteIndex;
    private int simpleProfilerFrameHistoryCount;
    private float simpleProfilerFrameHistorySumMs;

    private double simpleProfilerFrameStartTime;
    private float simpleProfilerCurrentFrameMs;
    private float simpleProfilerAverageFrameMs;
    private float simpleProfilerOverlayNextRefreshTime;
    private float simpleProfilerLastSevereSpikeLogTime = -999f;
    private string simpleProfilerOverlayCachedText = string.Empty;

    private int simpleProfilerFrameChunksRequested;
    private int simpleProfilerFrameChunkRebuildsProcessed;
    private int simpleProfilerFrameDataJobsCompleted;
    private int simpleProfilerFrameSubchunksApplied;
    private int simpleProfilerFrameChunksCompleted;
    private int simpleProfilerFrameColliderBuildsProcessed;
    private int simpleProfilerFrameFastRebuildsScheduled;
    private int simpleProfilerFrameFullRebuildsScheduled;

    private bool simpleProfilerHasLastChunk;
    private ChunkProfileSnapshot simpleProfilerLastChunk;
    private bool simpleProfilerHasLastLagSpike;
    private LagSpikeSnapshot simpleProfilerLastLagSpike;

    private GUIStyle simpleProfilerBoxStyle;
    private GUIStyle simpleProfilerLabelStyle;

    private bool IsSimpleProfilerActive => enableSimpleProfiler && Application.isPlaying;

    private void ProfilerValidateSettings()
    {
        simpleProfilerOverlayRefreshInterval = Mathf.Max(0.05f, simpleProfilerOverlayRefreshInterval);
        simpleProfilerFrameHistorySize = Mathf.Max(10, simpleProfilerFrameHistorySize);
        simpleProfilerChunkHistorySize = Mathf.Max(4, simpleProfilerChunkHistorySize);
        simpleProfilerLagSpikeHistorySize = Mathf.Max(4, simpleProfilerLagSpikeHistorySize);
        simpleProfilerLagSpikeThresholdMs = Mathf.Max(12f, simpleProfilerLagSpikeThresholdMs);
        simpleProfilerLagSpikeAverageMultiplier = Mathf.Clamp(simpleProfilerLagSpikeAverageMultiplier, 1.05f, 3f);
        simpleProfilerSevereLagSpikeThresholdMs = Mathf.Max(simpleProfilerLagSpikeThresholdMs, simpleProfilerSevereLagSpikeThresholdMs);
        simpleProfilerSevereSpikeLogCooldown = Mathf.Max(0.25f, simpleProfilerSevereSpikeLogCooldown);

        if (simpleProfilerFrameHistory.Length != simpleProfilerFrameHistorySize)
        {
            simpleProfilerFrameHistory = new float[simpleProfilerFrameHistorySize];
            simpleProfilerFrameSortScratch = new float[simpleProfilerFrameHistorySize];
            simpleProfilerFrameHistoryWriteIndex = 0;
            simpleProfilerFrameHistoryCount = 0;
            simpleProfilerFrameHistorySumMs = 0f;
        }
        else if (simpleProfilerFrameSortScratch.Length != simpleProfilerFrameHistorySize)
        {
            simpleProfilerFrameSortScratch = new float[simpleProfilerFrameHistorySize];
        }
    }

    private void ProfilerInitialize()
    {
        ProfilerValidateSettings();
        simpleProfilerOverlayNextRefreshTime = 0f;
        simpleProfilerOverlayCachedText = string.Empty;
    }

    private void ProfilerShutdown()
    {
        simpleProfilerActiveChunks.Clear();
        simpleProfilerGenerationByCoord.Clear();
        simpleProfilerStaleGenerations.Clear();
    }

    private void ProfilerHandleRuntimeInput()
    {
        if (!Application.isPlaying || simpleProfilerToggleKey == KeyCode.None)
            return;

        if (Input.GetKeyDown(simpleProfilerToggleKey))
        {
            showSimpleProfilerOverlay = !showSimpleProfilerOverlay;
            simpleProfilerOverlayNextRefreshTime = 0f;
        }
    }

    private void ProfilerBeginFrame()
    {
        if (!IsSimpleProfilerActive)
            return;

        ProfilerValidateSettings();
        simpleProfilerFrameStartTime = Time.realtimeSinceStartupAsDouble;
        Array.Clear(simpleProfilerStageTimesMs, 0, simpleProfilerStageTimesMs.Length);
        simpleProfilerFrameChunksRequested = 0;
        simpleProfilerFrameChunkRebuildsProcessed = 0;
        simpleProfilerFrameDataJobsCompleted = 0;
        simpleProfilerFrameSubchunksApplied = 0;
        simpleProfilerFrameChunksCompleted = 0;
        simpleProfilerFrameColliderBuildsProcessed = 0;
        simpleProfilerFrameFastRebuildsScheduled = 0;
        simpleProfilerFrameFullRebuildsScheduled = 0;
    }

    private double ProfilerBeginStage(SimpleProfilerStage stage)
    {
        return IsSimpleProfilerActive ? Time.realtimeSinceStartupAsDouble : 0d;
    }

    private void ProfilerEndStage(SimpleProfilerStage stage, double stageStart)
    {
        if (!IsSimpleProfilerActive)
            return;

        float elapsedMs = (float)((Time.realtimeSinceStartupAsDouble - stageStart) * 1000.0d);
        simpleProfilerStageTimesMs[(int)stage] += Mathf.Max(0f, elapsedMs);
    }

    private void ProfilerEndFrame()
    {
        if (!IsSimpleProfilerActive)
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        float frameMs = (float)((now - simpleProfilerFrameStartTime) * 1000.0d);
        simpleProfilerCurrentFrameMs = Mathf.Max(0f, frameMs);

        ProfilerRecordFrameSample(simpleProfilerCurrentFrameMs);
        simpleProfilerAverageFrameMs = simpleProfilerFrameHistoryCount > 0
            ? simpleProfilerFrameHistorySumMs / simpleProfilerFrameHistoryCount
            : simpleProfilerCurrentFrameMs;

        ProfilerPruneStaleChunkProfiles(now);
        ProfilerMaybeRecordLagSpike(simpleProfilerCurrentFrameMs, simpleProfilerAverageFrameMs);
        ProfilerMaybeRefreshOverlayCache((float)now);
    }

    private void ProfilerRecordFrameSample(float frameMs)
    {
        if (simpleProfilerFrameHistory.Length == 0)
            return;

        if (simpleProfilerFrameHistoryCount < simpleProfilerFrameHistory.Length)
        {
            simpleProfilerFrameHistory[simpleProfilerFrameHistoryWriteIndex] = frameMs;
            simpleProfilerFrameHistorySumMs += frameMs;
            simpleProfilerFrameHistoryCount++;
        }
        else
        {
            simpleProfilerFrameHistorySumMs -= simpleProfilerFrameHistory[simpleProfilerFrameHistoryWriteIndex];
            simpleProfilerFrameHistory[simpleProfilerFrameHistoryWriteIndex] = frameMs;
            simpleProfilerFrameHistorySumMs += frameMs;
        }

        simpleProfilerFrameHistoryWriteIndex++;
        if (simpleProfilerFrameHistoryWriteIndex >= simpleProfilerFrameHistory.Length)
            simpleProfilerFrameHistoryWriteIndex = 0;
    }

    private void ProfilerPruneStaleChunkProfiles(double now)
    {
        if (simpleProfilerActiveChunks.Count == 0)
            return;

        simpleProfilerStaleGenerations.Clear();
        foreach (KeyValuePair<int, ActiveChunkProfile> kv in simpleProfilerActiveChunks)
        {
            if (now - kv.Value.scheduledAt >= SimpleProfilerChunkProfileTimeoutSeconds)
                simpleProfilerStaleGenerations.Add(kv.Key);
        }

        for (int i = 0; i < simpleProfilerStaleGenerations.Count; i++)
        {
            int generation = simpleProfilerStaleGenerations[i];
            if (!simpleProfilerActiveChunks.TryGetValue(generation, out ActiveChunkProfile profile))
                continue;

            simpleProfilerActiveChunks.Remove(generation);
            if (simpleProfilerGenerationByCoord.TryGetValue(profile.coord, out int mappedGeneration) &&
                mappedGeneration == generation)
            {
                simpleProfilerGenerationByCoord.Remove(profile.coord);
            }
        }
    }

    private void ProfilerTrackChunkScheduled(Vector2Int coord, int generation, ChunkProfileKind kind, int dirtySubchunkMask)
    {
        if (!IsSimpleProfilerActive)
            return;

        if (simpleProfilerGenerationByCoord.TryGetValue(coord, out int previousGeneration))
            simpleProfilerActiveChunks.Remove(previousGeneration);

        simpleProfilerGenerationByCoord[coord] = generation;
        simpleProfilerActiveChunks[generation] = new ActiveChunkProfile
        {
            coord = coord,
            generation = generation,
            kind = kind,
            dirtySubchunkMask = dirtySubchunkMask,
            dirtySubchunkCount = CountSetBits(dirtySubchunkMask),
            scheduledAt = Time.realtimeSinceStartupAsDouble,
            dataReadyAt = 0d,
            meshScheduledAt = 0d,
            appliedSubchunkCount = 0,
            hasDataReady = false,
            hasMeshScheduled = false
        };

        if (kind == ChunkProfileKind.NewChunk)
            simpleProfilerFrameChunksRequested++;
        else if (kind == ChunkProfileKind.FastRebuild)
            simpleProfilerFrameFastRebuildsScheduled++;
        else
            simpleProfilerFrameFullRebuildsScheduled++;
    }

    private void ProfilerTrackDataJobCompleted(int generation)
    {
        if (!IsSimpleProfilerActive)
            return;

        if (!simpleProfilerActiveChunks.TryGetValue(generation, out ActiveChunkProfile profile))
            return;

        if (profile.hasDataReady)
            return;

        profile.hasDataReady = true;
        profile.dataReadyAt = Time.realtimeSinceStartupAsDouble;
    }

    private void ProfilerTrackMeshJobScheduled(int generation, int dirtySubchunkMask)
    {
        if (!IsSimpleProfilerActive)
            return;

        if (!simpleProfilerActiveChunks.TryGetValue(generation, out ActiveChunkProfile profile))
            return;

        profile.hasMeshScheduled = true;
        profile.meshScheduledAt = Time.realtimeSinceStartupAsDouble;
        if (dirtySubchunkMask != 0)
        {
            profile.dirtySubchunkMask = dirtySubchunkMask;
            profile.dirtySubchunkCount = CountSetBits(dirtySubchunkMask);
        }
    }

    private void ProfilerTrackSubchunkMeshApplied(int generation)
    {
        if (!IsSimpleProfilerActive)
            return;

        simpleProfilerFrameSubchunksApplied++;
        if (simpleProfilerActiveChunks.TryGetValue(generation, out ActiveChunkProfile profile))
            profile.appliedSubchunkCount++;
    }

    private void ProfilerTrackChunkVisualReady(Vector2Int coord, int generation)
    {
        if (!IsSimpleProfilerActive)
            return;

        if (!simpleProfilerActiveChunks.TryGetValue(generation, out ActiveChunkProfile profile))
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        double dataReadyAt = profile.hasDataReady ? profile.dataReadyAt : profile.scheduledAt;
        float dataReadyMs = (float)((dataReadyAt - profile.scheduledAt) * 1000.0d);
        float meshReadyMs = (float)((now - dataReadyAt) * 1000.0d);
        float totalVisualMs = (float)((now - profile.scheduledAt) * 1000.0d);

        ChunkProfileSnapshot snapshot = new ChunkProfileSnapshot
        {
            coord = coord,
            kind = profile.kind,
            dirtySubchunkCount = profile.dirtySubchunkCount,
            appliedSubchunkCount = profile.appliedSubchunkCount,
            dataReadyMs = Mathf.Max(0f, dataReadyMs),
            meshReadyMs = Mathf.Max(0f, meshReadyMs),
            totalVisualMs = Mathf.Max(0f, totalVisualMs),
            completedAtRealtime = (float)now
        };

        simpleProfilerHasLastChunk = true;
        simpleProfilerLastChunk = snapshot;
        simpleProfilerFrameChunksCompleted++;
        simpleProfilerRecentChunks.Add(snapshot);
        while (simpleProfilerRecentChunks.Count > simpleProfilerChunkHistorySize)
            simpleProfilerRecentChunks.RemoveAt(0);

        simpleProfilerActiveChunks.Remove(generation);
        if (simpleProfilerGenerationByCoord.TryGetValue(coord, out int mappedGeneration) &&
            mappedGeneration == generation)
        {
            simpleProfilerGenerationByCoord.Remove(coord);
        }
    }

    private void ProfilerIncrementDataJobCompletionCount()
    {
        if (IsSimpleProfilerActive)
            simpleProfilerFrameDataJobsCompleted++;
    }

    private void ProfilerIncrementChunkRebuildProcessedCount()
    {
        if (IsSimpleProfilerActive)
            simpleProfilerFrameChunkRebuildsProcessed++;
    }

    private void ProfilerIncrementColliderBuildCount()
    {
        if (IsSimpleProfilerActive)
            simpleProfilerFrameColliderBuildsProcessed++;
    }

    private void ProfilerMaybeRecordLagSpike(float frameMs, float averageFrameMs)
    {
        float adaptiveThreshold = Mathf.Max(simpleProfilerLagSpikeThresholdMs, averageFrameMs * simpleProfilerLagSpikeAverageMultiplier);
        bool isSpike = frameMs >= adaptiveThreshold || frameMs >= simpleProfilerSevereLagSpikeThresholdMs;
        if (!isSpike)
            return;

        GetTopProfilerStages(out SimpleProfilerStage primaryStage, out float primaryStageMs, out SimpleProfilerStage secondaryStage, out float secondaryStageMs);
        string cause = BuildLagSpikeCause(primaryStage);

        LagSpikeSnapshot spike = new LagSpikeSnapshot
        {
            frameMs = frameMs,
            averageFrameMs = averageFrameMs,
            primaryStage = primaryStage,
            primaryStageMs = primaryStageMs,
            secondaryStage = secondaryStage,
            secondaryStageMs = secondaryStageMs,
            cause = cause,
            chunksRequested = simpleProfilerFrameChunksRequested,
            rebuildsProcessed = simpleProfilerFrameChunkRebuildsProcessed,
            dataJobsCompleted = simpleProfilerFrameDataJobsCompleted,
            subchunksApplied = simpleProfilerFrameSubchunksApplied,
            colliderBuildsProcessed = simpleProfilerFrameColliderBuildsProcessed,
            capturedAtRealtime = Time.realtimeSinceStartup
        };

        simpleProfilerHasLastLagSpike = true;
        simpleProfilerLastLagSpike = spike;
        simpleProfilerRecentLagSpikes.Add(spike);
        while (simpleProfilerRecentLagSpikes.Count > simpleProfilerLagSpikeHistorySize)
            simpleProfilerRecentLagSpikes.RemoveAt(0);

        if (logSevereLagSpikes &&
            frameMs >= simpleProfilerSevereLagSpikeThresholdMs &&
            Time.realtimeSinceStartup - simpleProfilerLastSevereSpikeLogTime >= simpleProfilerSevereSpikeLogCooldown)
        {
            simpleProfilerLastSevereSpikeLogTime = Time.realtimeSinceStartup;
            Debug.LogWarning($"[WorldProfiler] Lag spike {frameMs:0.0}ms. Causa provavel: {cause}. Top: {GetStageLabel(primaryStage)} {primaryStageMs:0.0}ms.");
        }
    }

    private void GetTopProfilerStages(out SimpleProfilerStage primaryStage, out float primaryStageMs, out SimpleProfilerStage secondaryStage, out float secondaryStageMs)
    {
        primaryStage = SimpleProfilerStage.BlockColliderToggle;
        primaryStageMs = 0f;
        secondaryStage = SimpleProfilerStage.BlockColliderToggle;
        secondaryStageMs = 0f;

        for (int i = 0; i < (int)SimpleProfilerStage.Count; i++)
        {
            float value = simpleProfilerStageTimesMs[i];
            if (value > primaryStageMs)
            {
                secondaryStage = primaryStage;
                secondaryStageMs = primaryStageMs;
                primaryStage = (SimpleProfilerStage)i;
                primaryStageMs = value;
            }
            else if (value > secondaryStageMs)
            {
                secondaryStage = (SimpleProfilerStage)i;
                secondaryStageMs = value;
            }
        }
    }

    private string BuildLagSpikeCause(SimpleProfilerStage stage)
    {
        switch (stage)
        {
            case SimpleProfilerStage.DataJobProcessing:
                return simpleProfilerFrameDataJobsCompleted > 0
                    ? $"conclusao de {simpleProfilerFrameDataJobsCompleted} job(s) de geracao de chunk"
                    : "processamento de jobs de geracao";

            case SimpleProfilerStage.MeshApply:
                return simpleProfilerFrameSubchunksApplied > 0
                    ? $"aplicacao de mesh em {simpleProfilerFrameSubchunksApplied} subchunk(s)"
                    : "aplicacao de mesh de chunk";

            case SimpleProfilerStage.ColliderBuilds:
                return simpleProfilerFrameColliderBuildsProcessed > 0
                    ? $"reconstrucao de {simpleProfilerFrameColliderBuildsProcessed} collider(s)"
                    : "reconstrucao de colliders";

            case SimpleProfilerStage.UpdateChunks:
                return simpleProfilerFrameChunksRequested > 0
                    ? $"agendamento de {simpleProfilerFrameChunksRequested} novo(s) chunk(s)"
                    : "atualizacao da grade de chunks";

            case SimpleProfilerStage.ChunkRebuildQueue:
                return simpleProfilerFrameChunkRebuildsProcessed > 0
                    ? $"rebuild de {simpleProfilerFrameChunkRebuildsProcessed} chunk(s)"
                    : "fila de rebuild de chunks";

            case SimpleProfilerStage.HighBuildRebuildQueue:
                return "rebuild de high build";

            case SimpleProfilerStage.WaterUpdates:
                return "atualizacao de agua";

            case SimpleProfilerStage.LeafDecay:
                return "simulacao de leaf decay";

            case SimpleProfilerStage.SectionOcclusion:
                return "rebuild de section occlusion";

            case SimpleProfilerStage.VisualFeatureToggle:
                return "rebuild disparado por toggle visual";

            case SimpleProfilerStage.BlockColliderToggle:
                return "alternancia do sistema de colliders";

            case SimpleProfilerStage.SimulationRefresh:
                return "refresh da simulation distance";

            default:
                return $"etapa {GetStageLabel(stage)}";
        }
    }

    private ProfilerFeedbackLevel EvaluateFrameFeedbackLevel(float frameMs, float averageFrameMs, float p95Ms)
    {
        float warningThreshold = Mathf.Max(simpleProfilerLagSpikeThresholdMs, averageFrameMs * 1.15f);
        float criticalThreshold = Mathf.Max(simpleProfilerSevereLagSpikeThresholdMs, warningThreshold * 1.45f);

        if (frameMs >= criticalThreshold || p95Ms >= simpleProfilerSevereLagSpikeThresholdMs * 0.9f)
            return ProfilerFeedbackLevel.Critical;

        if (frameMs >= warningThreshold || p95Ms >= simpleProfilerLagSpikeThresholdMs || averageFrameMs >= simpleProfilerLagSpikeThresholdMs * 0.85f)
            return ProfilerFeedbackLevel.Warning;

        return ProfilerFeedbackLevel.Good;
    }

    private ProfilerFeedbackLevel EvaluatePendingPressureLevel()
    {
        int totalPending = pendingChunks.Count + pendingDataJobs.Count + pendingMeshes.Count + queuedColliderBuilds.Count;
        if (totalPending >= 24)
            return ProfilerFeedbackLevel.Critical;
        if (totalPending >= 10)
            return ProfilerFeedbackLevel.Warning;
        return ProfilerFeedbackLevel.Good;
    }

    private static ProfilerFeedbackLevel EvaluateChunkFeedbackLevel(float totalVisualMs)
    {
        if (totalVisualMs >= 90f)
            return ProfilerFeedbackLevel.Critical;
        if (totalVisualMs >= 45f)
            return ProfilerFeedbackLevel.Warning;
        return ProfilerFeedbackLevel.Good;
    }

    private static string GetFeedbackTag(ProfilerFeedbackLevel level)
    {
        switch (level)
        {
            case ProfilerFeedbackLevel.Good:
                return "<color=#91D36A>[OK]</color>";
            case ProfilerFeedbackLevel.Warning:
                return "<color=#F2C14E>[WARN]</color>";
            default:
                return "<color=#FF7B72>[CRIT]</color>";
        }
    }

    private static string GetFeedbackSummary(ProfilerFeedbackLevel level)
    {
        switch (level)
        {
            case ProfilerFeedbackLevel.Good:
                return "estavel";
            case ProfilerFeedbackLevel.Warning:
                return "pedindo atencao";
            default:
                return "critico";
        }
    }

    private static Color GetFeedbackBackgroundColor(ProfilerFeedbackLevel level)
    {
        switch (level)
        {
            case ProfilerFeedbackLevel.Good:
                return new Color(0.10f, 0.18f, 0.12f, 0.96f);
            case ProfilerFeedbackLevel.Warning:
                return new Color(0.25f, 0.18f, 0.08f, 0.96f);
            default:
                return new Color(0.25f, 0.10f, 0.10f, 0.96f);
        }
    }

    private static string GetStageMeaning(SimpleProfilerStage stage)
    {
        switch (stage)
        {
            case SimpleProfilerStage.DataJobProcessing:
                return "gerando dados do chunk: terreno, blocos e luz.";
            case SimpleProfilerStage.MeshApply:
                return "aplicando malha no lado principal da Unity.";
            case SimpleProfilerStage.ColliderBuilds:
                return "reconstruindo colisoes dos subchunks.";
            case SimpleProfilerStage.UpdateChunks:
                return "abrindo pedidos de chunks ao redor do player.";
            case SimpleProfilerStage.ChunkRebuildQueue:
                return "reprocessando chunks alterados por blocos, luz ou rebuilds.";
            case SimpleProfilerStage.HighBuildRebuildQueue:
                return "atualizando meshes acima da altura base do chunk.";
            case SimpleProfilerStage.SectionOcclusion:
                return "recalculando visibilidade e oclusion das secoes.";
            case SimpleProfilerStage.WaterUpdates:
                return "processando simulacao de agua.";
            case SimpleProfilerStage.LeafDecay:
                return "processando leaf decay.";
            case SimpleProfilerStage.SimulationRefresh:
                return "ligando e desligando sistemas pela simulation distance.";
            case SimpleProfilerStage.BlockColliderToggle:
                return "alternando o sistema de colliders.";
            case SimpleProfilerStage.VisualFeatureToggle:
                return "rebuild causado por troca de recursos visuais.";
            default:
                return "etapa nao classificada.";
        }
    }

    private static string GetStageRecommendation(SimpleProfilerStage stage)
    {
        switch (stage)
        {
            case SimpleProfilerStage.DataJobProcessing:
                return "Baixe maxDataCompletionsPerFrame, maxChunksPerFrame ou renderDistance.";
            case SimpleProfilerStage.MeshApply:
                return "Baixe maxMeshAppliesPerFrame ou reduza renderDistance.";
            case SimpleProfilerStage.ColliderBuilds:
                return "Baixe maxColliderBuildsPerFrame ou alivie colliders longe do player.";
            case SimpleProfilerStage.UpdateChunks:
                return "Baixe maxChunksPerFrame e confira se o renderDistance nao esta alto.";
            case SimpleProfilerStage.ChunkRebuildQueue:
                return "Baixe maxChunkRebuildsPerFrame ou agrupe mudancas de bloco.";
            case SimpleProfilerStage.HighBuildRebuildQueue:
                return "Evite rebuilds em lote nas estruturas altas.";
            case SimpleProfilerStage.SectionOcclusion:
                return "Reduza invalidacoes de visibilidade ou simplifique a oclusion.";
            case SimpleProfilerStage.WaterUpdates:
                return "Reduza o trabalho de agua por frame.";
            case SimpleProfilerStage.LeafDecay:
                return "Baixe leafDecayChecksPerFrame se isso nao for prioritario.";
            case SimpleProfilerStage.SimulationRefresh:
                return "Baixe simulationDistance se houver muita area simulada.";
            case SimpleProfilerStage.BlockColliderToggle:
            case SimpleProfilerStage.VisualFeatureToggle:
                return "Evite trocar esse recurso no meio do gameplay pesado.";
            default:
                return "Observe a etapa dominante e ajuste o limite por frame correspondente.";
        }
    }

    private string BuildPendingPressureText()
    {
        int totalPending = pendingChunks.Count + pendingDataJobs.Count + pendingMeshes.Count + queuedColliderBuilds.Count;
        ProfilerFeedbackLevel level = EvaluatePendingPressureLevel();

        if (level == ProfilerFeedbackLevel.Good)
            return $"{GetFeedbackTag(level)} fila leve ({totalPending} pendencias)";
        if (level == ProfilerFeedbackLevel.Warning)
            return $"{GetFeedbackTag(level)} fila media ({totalPending} pendencias)";

        return $"{GetFeedbackTag(level)} fila alta ({totalPending} pendencias)";
    }

    private void ProfilerMaybeRefreshOverlayCache(float now)
    {
        if (!showSimpleProfilerOverlay)
            return;

        if (now < simpleProfilerOverlayNextRefreshTime && !string.IsNullOrEmpty(simpleProfilerOverlayCachedText))
            return;

        simpleProfilerOverlayNextRefreshTime = now + simpleProfilerOverlayRefreshInterval;
        simpleProfilerOverlayCachedText = BuildProfilerOverlayText();
    }

    private string BuildProfilerOverlayText()
    {
        simpleProfilerOverlayBuilder.Clear();

        float fps = simpleProfilerCurrentFrameMs > 0.0001f ? 1000f / simpleProfilerCurrentFrameMs : 0f;
        float p95 = GetFramePercentileMs(0.95f);
        ChunkHistoryStats stats = BuildChunkHistoryStats();
        GetTopProfilerStages(out SimpleProfilerStage primaryStage, out float primaryStageMs, out SimpleProfilerStage secondaryStage, out float secondaryStageMs);

        ProfilerFeedbackLevel frameLevel = EvaluateFrameFeedbackLevel(simpleProfilerCurrentFrameMs, simpleProfilerAverageFrameMs, p95);
        float primaryStageShare = simpleProfilerCurrentFrameMs > 0.0001f
            ? (primaryStageMs / simpleProfilerCurrentFrameMs) * 100f
            : 0f;

        simpleProfilerOverlayBuilder.Append("<b>Voxel profiler</b> [");
        simpleProfilerOverlayBuilder.Append(simpleProfilerToggleKey);
        simpleProfilerOverlayBuilder.AppendLine("]");

        simpleProfilerOverlayBuilder.AppendFormat("<b>Status geral</b>: {0} {1} | {2:0.0} FPS | frame {3:0.0}ms | media {4:0.0}ms | p95 {5:0.0}ms\n",
            GetFeedbackTag(frameLevel),
            GetFeedbackSummary(frameLevel),
            fps,
            simpleProfilerCurrentFrameMs,
            simpleProfilerAverageFrameMs,
            p95);

        simpleProfilerOverlayBuilder.AppendFormat("<b>Pressao</b>: {0} | ativos {1} | chunks concluidos no frame {2}\n",
            BuildPendingPressureText(),
            activeChunks.Count,
            simpleProfilerFrameChunksCompleted);

        simpleProfilerOverlayBuilder.AppendFormat("<b>Gargalo agora</b>: {0} ({1:0.0}ms, {2:0}% do frame)\n",
            GetStageLabel(primaryStage),
            primaryStageMs,
            primaryStageShare);

        simpleProfilerOverlayBuilder.AppendFormat("<b>Leitura</b>: {0}\n",
            GetStageMeaning(primaryStage));

        simpleProfilerOverlayBuilder.AppendFormat("<b>Ajuste rapido</b>: {0}\n",
            GetStageRecommendation(primaryStage));

        simpleProfilerOverlayBuilder.AppendFormat("<b>Frame atual</b>: novo {0} | rebuild {1} | fast {2} | data ok {3} | subchunks {4} | coll ok {5}\n",
            simpleProfilerFrameChunksRequested,
            simpleProfilerFrameFullRebuildsScheduled,
            simpleProfilerFrameFastRebuildsScheduled,
            simpleProfilerFrameDataJobsCompleted,
            simpleProfilerFrameSubchunksApplied,
            simpleProfilerFrameColliderBuildsProcessed);

        simpleProfilerOverlayBuilder.AppendFormat("<b>Filas</b>: pend req {0} | data {1} | mesh {2} | coll {3}\n",
            pendingChunks.Count,
            pendingDataJobs.Count,
            pendingMeshes.Count,
            queuedColliderBuilds.Count);

        if (stats.sampleCount > 0)
        {
            simpleProfilerOverlayBuilder.AppendFormat("<b>Chunks recentes</b>: media {0:0.0}ms | dados {1:0.0}ms | mesh {2:0.0}ms | pior {3:0.0}ms\n",
                stats.averageTotalMs,
                stats.averageDataMs,
                stats.averageMeshMs,
                stats.slowestTotalMs);
        }
        else
        {
            simpleProfilerOverlayBuilder.AppendLine("<b>Chunks recentes</b>: aguardando os primeiros chunks finalizarem.");
        }

        if (stats.sampleCount > 0 && simpleProfilerHasLastChunk)
        {
            ProfilerFeedbackLevel chunkLevel = EvaluateChunkFeedbackLevel(simpleProfilerLastChunk.totalVisualMs);

            simpleProfilerOverlayBuilder.AppendFormat("<b>Ultimo chunk</b>: {0} ({1},{2}) {3:0.0}ms | {4} | dados {5:0.0}ms | mesh {6:0.0}ms | subchunks {7}/{8}\n",
                GetFeedbackTag(chunkLevel),
                simpleProfilerLastChunk.coord.x,
                simpleProfilerLastChunk.coord.y,
                simpleProfilerLastChunk.totalVisualMs,
                GetChunkKindLabel(simpleProfilerLastChunk.kind),
                simpleProfilerLastChunk.dataReadyMs,
                simpleProfilerLastChunk.meshReadyMs,
                simpleProfilerLastChunk.appliedSubchunkCount,
                simpleProfilerLastChunk.dirtySubchunkCount);
        }
        else
        {
            simpleProfilerOverlayBuilder.AppendLine("<b>Ultimo chunk</b>: ainda sem amostra.");
        }

        if (simpleProfilerHasLastLagSpike)
        {
            ProfilerFeedbackLevel spikeLevel = EvaluateFrameFeedbackLevel(
                simpleProfilerLastLagSpike.frameMs,
                simpleProfilerLastLagSpike.averageFrameMs,
                simpleProfilerLastLagSpike.frameMs);

            simpleProfilerOverlayBuilder.AppendFormat("<b>Ultimo spike</b>: {0} {1:0.0}ms | {2}\n",
                GetFeedbackTag(spikeLevel),
                simpleProfilerLastLagSpike.frameMs,
                simpleProfilerLastLagSpike.cause);

            simpleProfilerOverlayBuilder.AppendFormat("<b>Feedback do spike</b>: {0}\n",
                GetStageRecommendation(simpleProfilerLastLagSpike.primaryStage));

            simpleProfilerOverlayBuilder.AppendFormat("<b>Spike top</b>: {0} {1:0.0}ms | {2} {3:0.0}ms",
                GetStageLabel(simpleProfilerLastLagSpike.primaryStage),
                simpleProfilerLastLagSpike.primaryStageMs,
                GetStageLabel(simpleProfilerLastLagSpike.secondaryStage),
                simpleProfilerLastLagSpike.secondaryStageMs);
        }
        else
        {
            simpleProfilerOverlayBuilder.Append("<b>Ultimo spike</b>: nenhum acima de ");
            simpleProfilerOverlayBuilder.Append(simpleProfilerLagSpikeThresholdMs.ToString("0.0"));
            simpleProfilerOverlayBuilder.Append("ms");
        }

        return simpleProfilerOverlayBuilder.ToString();
    }

    private ChunkHistoryStats BuildChunkHistoryStats()
    {
        ChunkHistoryStats stats = default;
        if (simpleProfilerRecentChunks.Count == 0)
            return stats;

        float totalTotalMs = 0f;
        float totalDataMs = 0f;
        float totalMeshMs = 0f;
        float slowestMs = 0f;

        for (int i = 0; i < simpleProfilerRecentChunks.Count; i++)
        {
            ChunkProfileSnapshot snapshot = simpleProfilerRecentChunks[i];
            totalTotalMs += snapshot.totalVisualMs;
            totalDataMs += snapshot.dataReadyMs;
            totalMeshMs += snapshot.meshReadyMs;
            if (snapshot.totalVisualMs > slowestMs)
                slowestMs = snapshot.totalVisualMs;
        }

        stats.sampleCount = simpleProfilerRecentChunks.Count;
        stats.averageTotalMs = totalTotalMs / stats.sampleCount;
        stats.averageDataMs = totalDataMs / stats.sampleCount;
        stats.averageMeshMs = totalMeshMs / stats.sampleCount;
        stats.slowestTotalMs = slowestMs;
        return stats;
    }

    private float GetFramePercentileMs(float percentile)
    {
        if (simpleProfilerFrameHistoryCount == 0)
            return 0f;

        percentile = Mathf.Clamp01(percentile);
        Array.Copy(simpleProfilerFrameHistory, simpleProfilerFrameSortScratch, simpleProfilerFrameHistoryCount);
        Array.Sort(simpleProfilerFrameSortScratch, 0, simpleProfilerFrameHistoryCount);

        int index = Mathf.Clamp(Mathf.CeilToInt(simpleProfilerFrameHistoryCount * percentile) - 1, 0, simpleProfilerFrameHistoryCount - 1);
        return simpleProfilerFrameSortScratch[index];
    }

    private static int CountSetBits(int value)
    {
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    private static string GetChunkKindLabel(ChunkProfileKind kind)
    {
        switch (kind)
        {
            case ChunkProfileKind.NewChunk:
                return "novo";
            case ChunkProfileKind.FastRebuild:
                return "fast rebuild";
            default:
                return "rebuild";
        }
    }

    private static string GetStageLabel(SimpleProfilerStage stage)
    {
        int index = (int)stage;
        if (index < 0 || index >= SimpleProfilerStageLabels.Length)
            return "unknown";

        return SimpleProfilerStageLabels[index];
    }

    private void EnsureProfilerGuiStyles()
    {
        if (simpleProfilerBoxStyle != null && simpleProfilerLabelStyle != null)
            return;

        simpleProfilerBoxStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(10, 10, 10, 10),
            fontSize = 12
        };

        simpleProfilerLabelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 12,
            wordWrap = true,
            richText = true
        };

        simpleProfilerLabelStyle.normal.textColor = Color.white;
    }

    private void OnGUI()
    {
        if (!Application.isPlaying || !enableSimpleProfiler || !showSimpleProfilerOverlay)
            return;

        if (string.IsNullOrEmpty(simpleProfilerOverlayCachedText))
            simpleProfilerOverlayCachedText = BuildProfilerOverlayText();

        EnsureProfilerGuiStyles();

        ProfilerFeedbackLevel frameLevel = EvaluateFrameFeedbackLevel(
            simpleProfilerCurrentFrameMs,
            simpleProfilerAverageFrameMs,
            GetFramePercentileMs(0.95f));

        Rect textRect = new Rect(20f, 20f, SimpleProfilerOverlayWidth - 20f, 0f);
        float height = simpleProfilerLabelStyle.CalcHeight(new GUIContent(simpleProfilerOverlayCachedText), textRect.width);
        Rect boxRect = new Rect(12f, 12f, SimpleProfilerOverlayWidth, height + 20f);
        textRect.height = height;

        GUI.depth = -1000;
        Color previousColor = GUI.color;
        GUI.color = GetFeedbackBackgroundColor(frameLevel);
        GUI.Box(boxRect, GUIContent.none, simpleProfilerBoxStyle);
        GUI.color = previousColor;
        GUI.Label(textRect, simpleProfilerOverlayCachedText, simpleProfilerLabelStyle);
    }
}
