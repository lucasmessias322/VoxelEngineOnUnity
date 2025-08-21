using UnityEngine;

[System.Serializable]
public class NoisePreset
{
    public string name = "Preset";
    public int octaves = 4;
    public float persistence = 0.5f;
    public float lacunarity = 2f;
    public float scale = 0.02f;
    public float heightOffset = 0f;
    public float heightMultiplier = 10f;
    public bool ridged = false;
}
