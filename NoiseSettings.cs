using UnityEngine;

[CreateAssetMenu(fileName = "NoiseSettings", menuName = "ScriptableObjects/NoiseSettings", order = 1)]
public class NoiseSettings : ScriptableObject
{
    public int baseGroundLevel = 32;
    public int bedrockLayers = 1;
    public int dirtLayers = 3;

    public int seed = 0;

    [Header("Continental Settings")]
    public float continentalScale = 0.008f;
    public float continentalStrength = 40f;
    public int continentalOctaves = 4;
    public float continentalPersistence = 0.5f;
    public float continentalLacunarity = 2f;

    [Header("Domain Warp")]
    public float domainWarpStrength = 20f;
    public float warpNoiseScale = 0.05f;
}