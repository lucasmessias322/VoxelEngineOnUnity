using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public static class VoxelShaderFallbackBuffers
{
    private static readonly int PulledOpaqueFacesBufferPropertyId = Shader.PropertyToID("_PulledOpaqueFaces");
    private static readonly int CompactOpaqueFacesBufferPropertyId = Shader.PropertyToID("_CompactOpaqueFaces");
    private static readonly int OpaqueGpuSectionsBufferPropertyId = Shader.PropertyToID("_OpaqueGpuSections");
    private static readonly int OpaqueBlockMappingsBufferPropertyId = Shader.PropertyToID("_OpaqueBlockMappings");
    private static readonly int UnityIndirectDrawArgsBufferPropertyId = Shader.PropertyToID("unity_IndirectDrawArgs");

    private const int PulledOpaqueFaceStrideBytes = 112;   // 7 * float4
    private const int CompactOpaqueFaceStrideBytes = 16;   // 4 * uint
    private const int OpaqueGpuSectionStrideBytes = 32;    // 2 * float4
    private const int OpaqueBlockMappingStrideBytes = 32;  // 2 * float4
    private const int UnityIndirectDrawArgsWordCount = 4;  // IndirectDrawArgs = 4 uint (16 bytes)

    private static ComputeBuffer fallbackPulledOpaqueFacesBuffer;
    private static ComputeBuffer fallbackCompactOpaqueFacesBuffer;
    private static ComputeBuffer fallbackOpaqueGpuSectionsBuffer;
    private static ComputeBuffer fallbackOpaqueBlockMappingsBuffer;
    private static ComputeBuffer fallbackUnityIndirectDrawArgsBuffer;
    private static bool runtimeReleaseHookRegistered;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InitializeRuntime()
    {
        EnsureBound();
        RegisterRuntimeReleaseHook();
    }

#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    private static void InitializeEditor()
    {
        EnsureBound();
        AssemblyReloadEvents.beforeAssemblyReload -= Release;
        AssemblyReloadEvents.beforeAssemblyReload += Release;
        EditorApplication.quitting -= Release;
        EditorApplication.quitting += Release;
    }
#endif

    public static void EnsureBound()
    {
        if (fallbackPulledOpaqueFacesBuffer == null)
        {
            fallbackPulledOpaqueFacesBuffer = new ComputeBuffer(1, PulledOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackPulledOpaqueFacesBuffer.SetData(new float[28]);
        }

        if (fallbackCompactOpaqueFacesBuffer == null)
        {
            fallbackCompactOpaqueFacesBuffer = new ComputeBuffer(1, CompactOpaqueFaceStrideBytes, ComputeBufferType.Structured);
            fallbackCompactOpaqueFacesBuffer.SetData(new uint[4]);
        }

        if (fallbackOpaqueGpuSectionsBuffer == null)
        {
            fallbackOpaqueGpuSectionsBuffer = new ComputeBuffer(1, OpaqueGpuSectionStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueGpuSectionsBuffer.SetData(new float[8]);
        }

        if (fallbackOpaqueBlockMappingsBuffer == null)
        {
            fallbackOpaqueBlockMappingsBuffer = new ComputeBuffer(1, OpaqueBlockMappingStrideBytes, ComputeBufferType.Structured);
            fallbackOpaqueBlockMappingsBuffer.SetData(new float[8]);
        }

        if (fallbackUnityIndirectDrawArgsBuffer == null)
        {
            fallbackUnityIndirectDrawArgsBuffer = new ComputeBuffer(UnityIndirectDrawArgsWordCount, sizeof(uint), ComputeBufferType.Raw);
            fallbackUnityIndirectDrawArgsBuffer.SetData(new uint[UnityIndirectDrawArgsWordCount]);
        }

        Shader.SetGlobalBuffer(PulledOpaqueFacesBufferPropertyId, fallbackPulledOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(CompactOpaqueFacesBufferPropertyId, fallbackCompactOpaqueFacesBuffer);
        Shader.SetGlobalBuffer(OpaqueGpuSectionsBufferPropertyId, fallbackOpaqueGpuSectionsBuffer);
        Shader.SetGlobalBuffer(OpaqueBlockMappingsBufferPropertyId, fallbackOpaqueBlockMappingsBuffer);
        Shader.SetGlobalBuffer(UnityIndirectDrawArgsBufferPropertyId, fallbackUnityIndirectDrawArgsBuffer);
    }

    public static void Release()
    {
        Application.quitting -= Release;
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= Release;
        EditorApplication.quitting -= Release;
#endif

        ReleaseComputeBuffer(ref fallbackPulledOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackCompactOpaqueFacesBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueGpuSectionsBuffer);
        ReleaseComputeBuffer(ref fallbackOpaqueBlockMappingsBuffer);
        ReleaseComputeBuffer(ref fallbackUnityIndirectDrawArgsBuffer);
        runtimeReleaseHookRegistered = false;
    }

    private static void RegisterRuntimeReleaseHook()
    {
        if (runtimeReleaseHookRegistered)
            return;

        Application.quitting += Release;
        runtimeReleaseHookRegistered = true;
    }

    private static void ReleaseComputeBuffer(ref ComputeBuffer buffer)
    {
        if (buffer == null)
            return;

        buffer.Release();
        buffer = null;
    }
}
