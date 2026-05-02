using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Singleton

    public static World Instance { get; private set; }
    public const int MinRenderDistance = 2;
    public const int MaxRenderDistance = 32;
    internal bool IsShuttingDown => isShuttingDown;

    private bool isShuttingDown;
    private TorchFireParticleController torchFireParticleController;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        pendingDataDistanceComparison = ComparePendingDataByDistance;
        pendingMeshDistanceComparison = ComparePendingMeshByDistance;

        if (caveSpaghettiSettings.LooksUninitialized || caveSpaghettiSettings.LooksLikeInitialSurfaceClosedDefault)
            caveSpaghettiSettings = SpaghettiCaveSettings.Default;

        VoxelShaderFallbackBuffers.EnsureBound();
        EnsureLoadingBootstrapExists();
        EnsureTorchFireParticleControllerExists();
    }

    #endregion

}
