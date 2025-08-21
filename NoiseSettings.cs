using UnityEngine;

[CreateAssetMenu(menuName = "World/NoiseSettings", fileName = "NoiseSettings")]
public class NoiseSettings : ScriptableObject
{
    [Header("Domain Warp")]
    public float domainWarpStrength = 20f;
    public float warpNoiseScale = 0.001f;

    [Header("Continental (macro)")]
    public int continentalOctaves = 3;
    public float continentalPersistence = 0.5f;
    public float continentalLacunarity = 2f;
    public float continentalScale = 0.0008f; // muito grande => longas massas terrestres
    public float baseGroundLevel = 64f;       // equivalente ao 'sea level' do Minecraft
    public float continentalStrength = 48f;   // amplitude da continentalidade

    [Header("Mountains")]
    public int mountainOctaves = 4;
    public float mountainPersistence = 0.5f;
    public float mountainLacunarity = 2f;
    public float mountainScale = 0.002f;      // escala média
    public float mountainStrength = 64f;      // quão altas as montanhas podem ficar
    public float mountainMaskBias = 0.45f;    // threshold para aparecer montanha
    public float mountainMaskExponent = 2f;   // contrai/expande a máscara

    [Header("Erosion / valleys")]
    public int erosionOctaves = 3;
    public float erosionScale = 0.01f;
    public float erosionStrength = 8f;

    [Header("Detail")]
    public int detailOctaves = 4;
    public float detailScale = 0.06f;
    public float detailStrength = 6f;

    [Header("Cliff detection")]
    public float cliffAngleThreshold = 3f; // usado para decidir quando trocar para pedra/cliff

    [Header("Misc")]
    public float seaLevel = 16f; // já tem no VoxelWorld, mas mantive aqui como fallback
}
