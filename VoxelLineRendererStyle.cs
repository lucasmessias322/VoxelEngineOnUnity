using UnityEngine;
using UnityEngine.Rendering;

[System.Serializable]
public sealed class VoxelLineRendererStyle
{
    public Material material;
    public Texture texture;
    public Color color = Color.white;
    [Min(0.001f)] public float width = 0.035f;
    public LineTextureMode textureMode = LineTextureMode.Stretch;
    public LineAlignment alignment = LineAlignment.View;
    [Min(0)] public int capVertices = 4;
    [Min(0)] public int cornerVertices = 4;
    public ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off;
    public bool receiveShadows;

    [Header("Emission")]
    public bool enableEmission;
    public Color emissionColor = Color.white;
    [Min(0f)] public float emissionIntensity = 1f;

    [System.NonSerialized] private Material runtimeMaterial;

    public void Configure(LineRenderer line, string runtimeMaterialName, bool loop)
    {
        if (line == null)
            return;

        Material resolvedMaterial = ResolveMaterial(runtimeMaterialName);
        if (resolvedMaterial != null)
            line.sharedMaterial = resolvedMaterial;

        line.useWorldSpace = true;
        line.loop = loop;
        line.startWidth = Mathf.Max(0.001f, width);
        line.endWidth = Mathf.Max(0.001f, width);
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = Mathf.Max(0, capVertices);
        line.numCornerVertices = Mathf.Max(0, cornerVertices);
        line.textureMode = textureMode;
        line.alignment = alignment;
        line.shadowCastingMode = shadowCastingMode;
        line.receiveShadows = receiveShadows;
    }

    public void DestroyRuntimeMaterial()
    {
        if (runtimeMaterial == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(runtimeMaterial);
        else
            Object.DestroyImmediate(runtimeMaterial);

        runtimeMaterial = null;
    }

    private Material ResolveMaterial(string runtimeMaterialName)
    {
        if (runtimeMaterial != null)
        {
            ApplyMaterialProperties(runtimeMaterial);
            return runtimeMaterial;
        }

        if (material != null)
        {
            runtimeMaterial = new Material(material)
            {
                name = string.IsNullOrWhiteSpace(runtimeMaterialName)
                    ? $"{material.name} Runtime"
                    : runtimeMaterialName,
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        else
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                return null;

            runtimeMaterial = new Material(shader)
            {
                name = string.IsNullOrWhiteSpace(runtimeMaterialName)
                    ? "Runtime Voxel Line Material"
                    : runtimeMaterialName,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        ApplyMaterialProperties(runtimeMaterial);
        return runtimeMaterial;
    }

    private void ApplyMaterialProperties(Material target)
    {
        if (target == null)
            return;

        Texture resolvedTexture = texture != null ? texture : Texture2D.whiteTexture;
        if (target.HasProperty("_Color"))
            target.SetColor("_Color", color);
        if (target.HasProperty("_BaseColor"))
            target.SetColor("_BaseColor", color);
        if (target.HasProperty("_MainTex"))
            target.SetTexture("_MainTex", resolvedTexture);
        if (target.HasProperty("_BaseMap"))
            target.SetTexture("_BaseMap", resolvedTexture);
        if (target.HasProperty("_Atlas"))
            target.SetTexture("_Atlas", resolvedTexture);

        target.mainTexture = resolvedTexture;

        if (!enableEmission)
            return;

        Color resolvedEmission = emissionColor * Mathf.Max(0f, emissionIntensity);
        if (target.HasProperty("_EmissionColor"))
        {
            target.SetColor("_EmissionColor", resolvedEmission);
            target.EnableKeyword("_EMISSION");
        }
    }
}
