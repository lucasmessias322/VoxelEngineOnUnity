using System;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainLayerProfile", menuName = "ScriptableObjects/Terrain Layer Profile", order = 3)]
public class TerrainLayerProfileSO : ScriptableObject
{
    [Header("Terrain Noise Layers")]
    public NoiseLayer[] noiseLayers = Array.Empty<NoiseLayer>();

    [Header("Domain Warp Layers")]
    public WarpLayer[] warpLayers = Array.Empty<WarpLayer>();

    public NoiseLayer[] CloneNoiseLayers()
    {
        if (noiseLayers == null || noiseLayers.Length == 0)
            return Array.Empty<NoiseLayer>();

        NoiseLayer[] copy = new NoiseLayer[noiseLayers.Length];
        Array.Copy(noiseLayers, copy, noiseLayers.Length);
        return copy;
    }

    public WarpLayer[] CloneWarpLayers()
    {
        if (warpLayers == null || warpLayers.Length == 0)
            return Array.Empty<WarpLayer>();

        WarpLayer[] copy = new WarpLayer[warpLayers.Length];
        Array.Copy(warpLayers, copy, warpLayers.Length);
        return copy;
    }
}
