using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DefaultExecutionOrder(-100)]
public class VoxelProceduralSkyController : MonoBehaviour
{
    public const float MinecraftTicksPerDay = 24000f;

    private const string SkyboxShaderName = "Voxel/URP/Procedural Voxel Skybox";

    private static readonly int UseMainLightDirectionId = Shader.PropertyToID("_UseMainLightDirection");
    private static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
    private static readonly int MoonDirectionId = Shader.PropertyToID("_MoonDirection");

    public enum DayPhase
    {
        Dawn,
        Day,
        Dusk,
        Night
    }

    [Header("References")]
    public Light directionalLight;
    public Material skyboxMaterial;
    public FogControl fogControl;
    public World world;
    public bool assignSkyboxOnEnable = true;
    public bool findDirectionalLightAutomatically = true;
    public bool findFogControlAutomatically = true;
    public bool findWorldAutomatically = true;
    public bool useUrpMainLightDirection = true;

    [Header("Minecraft Time Cycle")]
    public bool animateDirectionalLight = true;
    public bool driveDirectionalLightRotation = true;
    [Min(1f)] public float dayLengthSeconds = 1200f;
    [Min(0f)] public float timeScale = 1f;
    public bool pauseCycle;
    [Range(0f, 1f)] public float timeOfDay = 0.25f;
    [Range(0f, MinecraftTicksPerDay)] public float minecraftTime = 6000f;
    public float lightYaw = -30f;

    [Header("Phase Boundaries")]
    [Range(0f, MinecraftTicksPerDay)] public float dawnStartTick = 23000f;
    [Range(0f, MinecraftTicksPerDay)] public float dayStartTick = 1000f;
    [Range(0f, MinecraftTicksPerDay)] public float duskStartTick = 12000f;
    [Range(0f, MinecraftTicksPerDay)] public float nightStartTick = 13500f;
    public string dawnDisplayName = "Amanhecer";
    public string dayDisplayName = "Dia";
    public string duskDisplayName = "Tarde";
    public string nightDisplayName = "Noite";
    public bool logPhaseChanges;

    [Header("Optional Scene Lighting")]
    public bool driveLightColorAndIntensity = true;
    public bool driveAmbientLight = true;
    public float dayLightIntensity = 1.2f;
    public float twilightLightIntensity = 0.35f;
    public float nightLightIntensity = 0.04f;
    public Color dayLightColor = new Color(1f, 0.96f, 0.82f, 1f);
    public Color twilightLightColor = new Color(1f, 0.52f, 0.28f, 1f);
    public Color nightLightColor = new Color(0.36f, 0.48f, 0.75f, 1f);
    public Color dayAmbientColor = new Color(0.58f, 0.68f, 0.82f, 1f);
    public Color twilightAmbientColor = new Color(0.38f, 0.27f, 0.28f, 1f);
    public Color nightAmbientColor = new Color(0.045f, 0.055f, 0.095f, 1f);

    [Header("Voxel Material Night Darkening")]
    public bool driveVoxelMaterialLighting = true;
    public bool includeWorldMaterials = true;
    public Material[] extraVoxelMaterials;
    [Range(0f, 1f)] public float dayVoxelSkyLightMultiplier = 1f;
    [Range(0f, 1f)] public float twilightVoxelSkyLightMultiplier = 0.38f;
    [Range(0f, 1f)] public float nightVoxelSkyLightMultiplier = 0.08f;
    [Range(0f, 1f)] public float dayVoxelMinLight = 0.075f;
    [Range(0f, 1f)] public float twilightVoxelMinLight = 0.025f;
    [Range(0f, 1f)] public float nightVoxelMinLight = 0.008f;
    [Range(0f, 2f)] public float dayVoxelAmbientStrength = 0.28f;
    [Range(0f, 2f)] public float twilightVoxelAmbientStrength = 0.09f;
    [Range(0f, 2f)] public float nightVoxelAmbientStrength = 0.025f;
    [Range(0f, 2f)] public float dayVoxelDirectionalStrength = 0.58f;
    [Range(0f, 2f)] public float twilightVoxelDirectionalStrength = 0.18f;
    [Range(0f, 2f)] public float nightVoxelDirectionalStrength = 0.045f;
    [Range(0f, 1f)] public float dayVoxelWrapLighting = 0.18f;
    [Range(0f, 1f)] public float twilightVoxelWrapLighting = 0.06f;
    [Range(0f, 1f)] public float nightVoxelWrapLighting = 0.015f;

    [Header("Optional Fog")]
    public bool driveFog = true;
    public Color dayFogColor = new Color(0.53f, 0.68f, 0.9f, 1f);
    public Color twilightFogColor = new Color(0.68f, 0.36f, 0.28f, 1f);
    public Color nightFogColor = new Color(0.045f, 0.055f, 0.11f, 1f);
    [Min(0f)] public float dayFogDensity = 0.01f;
    [Min(0f)] public float twilightFogDensity = 0.014f;
    [Min(0f)] public float nightFogDensity = 0.02f;
    [Min(0f)] public float dayFogStart = 80f;
    [Min(0f)] public float twilightFogStart = 65f;
    [Min(0f)] public float nightFogStart = 45f;
    [Min(0f)] public float dayFogEnd = 500f;
    [Min(0f)] public float twilightFogEnd = 380f;
    [Min(0f)] public float nightFogEnd = 240f;

    public event Action<DayPhase> PhaseChanged;

    public float NormalizedTime => timeOfDay;
    public int CurrentTick => Mathf.FloorToInt(minecraftTime) % (int)MinecraftTicksPerDay;
    public DayPhase CurrentPhase => currentPhase;
    public string CurrentPhaseName => GetPhaseDisplayName(currentPhase);

    private Material runtimeSkyboxMaterial;
    private DayPhase currentPhase = DayPhase.Day;
    private DayPhase previousPhase = (DayPhase)(-1);
    private float previousTimeOfDay = -1f;
    private float previousMinecraftTime = -1f;
    private readonly HashSet<Material> appliedVoxelMaterials = new HashSet<Material>();

    private static readonly int MinLightId = Shader.PropertyToID("_MinLight");
    private static readonly int VoxelSkyLightMultiplierId = Shader.PropertyToID("_VoxelSkyLightMultiplier");
    private static readonly int AmbientStrengthId = Shader.PropertyToID("_AmbientStrength");
    private static readonly int DirectionalStrengthId = Shader.PropertyToID("_DirectionalStrength");
    private static readonly int WrapLightingId = Shader.PropertyToID("_WrapLighting");

    private void Reset()
    {
        ResolveReferences();
        SyncTimeFieldsFromInspector();
        ApplyTimeState();
    }

    private void OnEnable()
    {
        ResolveReferences();
        SyncTimeFieldsFromInspector();
        ApplyTimeState();
        ApplySky();
    }

    private void OnValidate()
    {
        dayLengthSeconds = Mathf.Max(1f, dayLengthSeconds);
        timeScale = Mathf.Max(0f, timeScale);
        dawnStartTick = WrapMinecraftTicks(dawnStartTick);
        dayStartTick = WrapMinecraftTicks(dayStartTick);
        duskStartTick = WrapMinecraftTicks(duskStartTick);
        nightStartTick = WrapMinecraftTicks(nightStartTick);
        dayFogEnd = Mathf.Max(dayFogStart, dayFogEnd);
        twilightFogEnd = Mathf.Max(twilightFogStart, twilightFogEnd);
        nightFogEnd = Mathf.Max(nightFogStart, nightFogEnd);

        SyncTimeFieldsFromInspector();

        if (isActiveAndEnabled)
        {
            ResolveReferences();
            ApplyTimeState();
            ApplySky();
        }
    }

    private void Update()
    {
        ResolveReferences();
        SyncTimeFieldsFromInspector();

        if (Application.isPlaying && animateDirectionalLight && !pauseCycle)
        {
            float ticksPerSecond = MinecraftTicksPerDay / Mathf.Max(1f, dayLengthSeconds);
            SetMinecraftTimeInternal(minecraftTime + ticksPerSecond * timeScale * Time.deltaTime);
        }

        ApplyTimeState();
        ApplySky();
    }

    private void OnDestroy()
    {
        if (runtimeSkyboxMaterial == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(runtimeSkyboxMaterial);
        }
        else
        {
            DestroyImmediate(runtimeSkyboxMaterial);
        }
    }

    public void SetMinecraftTime(float tick)
    {
        SetMinecraftTimeInternal(tick);
        ApplyTimeState();
        ApplySky();
    }

    public void SetTimeOfDay01(float normalizedTime)
    {
        SetMinecraftTime(NormalizedTimeToTick(normalizedTime));
    }

    public void AddMinecraftTicks(float ticks)
    {
        SetMinecraftTime(minecraftTime + ticks);
    }

    public void SetPhase(DayPhase phase)
    {
        switch (phase)
        {
            case DayPhase.Dawn:
                SetMinecraftTime(dawnStartTick);
                break;
            case DayPhase.Day:
                SetMinecraftTime(dayStartTick);
                break;
            case DayPhase.Dusk:
                SetMinecraftTime(duskStartTick);
                break;
            case DayPhase.Night:
                SetMinecraftTime(nightStartTick);
                break;
        }
    }

    public string GetClockText()
    {
        float worldHour = Mathf.Repeat(minecraftTime / 1000f + 6f, 24f);
        int hour = Mathf.FloorToInt(worldHour);
        int minute = Mathf.FloorToInt((worldHour - hour) * 60f);
        return $"{hour:00}:{minute:00}";
    }

    private void ResolveReferences()
    {
        ResolveDirectionalLight();
        ResolveFogControl();
        ResolveWorld();
    }

    private void ResolveDirectionalLight()
    {
        if (directionalLight != null || !findDirectionalLightAutomatically)
        {
            return;
        }

        if (RenderSettings.sun != null)
        {
            directionalLight = RenderSettings.sun;
            return;
        }

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude);
#else
        Light[] lights = FindObjectsOfType<Light>();
#endif
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i] != null && lights[i].type == LightType.Directional)
            {
                directionalLight = lights[i];
                return;
            }
        }
    }

    private void ResolveFogControl()
    {
        if (fogControl != null || !findFogControlAutomatically)
        {
            return;
        }

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        FogControl[] fogControls = FindObjectsByType<FogControl>(FindObjectsInactive.Exclude);
#else
        FogControl[] fogControls = FindObjectsOfType<FogControl>();
#endif
        if (fogControls.Length > 0)
        {
            fogControl = fogControls[0];
        }
    }

    private void ResolveWorld()
    {
        if (world != null || !findWorldAutomatically)
        {
            return;
        }

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        World[] worlds = FindObjectsByType<World>(FindObjectsInactive.Exclude);
#else
        World[] worlds = FindObjectsOfType<World>();
#endif
        if (worlds.Length > 0)
        {
            world = worlds[0];
        }
    }

    private void SyncTimeFieldsFromInspector()
    {
        timeOfDay = Mathf.Repeat(timeOfDay, 1f);
        minecraftTime = WrapMinecraftTicks(minecraftTime);

        bool normalizedChanged = !Mathf.Approximately(timeOfDay, previousTimeOfDay);
        bool tickChanged = !Mathf.Approximately(minecraftTime, previousMinecraftTime);

        if (tickChanged && !normalizedChanged)
        {
            timeOfDay = TickToNormalizedTime(minecraftTime);
        }
        else
        {
            minecraftTime = NormalizedTimeToTick(timeOfDay);
        }

        StorePreviousTimeFields();
    }

    private void SetMinecraftTimeInternal(float tick)
    {
        minecraftTime = WrapMinecraftTicks(tick);
        timeOfDay = TickToNormalizedTime(minecraftTime);
        StorePreviousTimeFields();
    }

    private void StorePreviousTimeFields()
    {
        previousTimeOfDay = timeOfDay;
        previousMinecraftTime = minecraftTime;
    }

    private void ApplyTimeState()
    {
        if (driveDirectionalLightRotation && directionalLight != null)
        {
            directionalLight.transform.rotation = Quaternion.Euler(timeOfDay * 360f, lightYaw, 0f);
        }

        DayPhase nextPhase = EvaluatePhase(minecraftTime);
        if (nextPhase == currentPhase && previousPhase == nextPhase)
        {
            return;
        }

        currentPhase = nextPhase;
        if (previousPhase != currentPhase)
        {
            previousPhase = currentPhase;
            PhaseChanged?.Invoke(currentPhase);

            if (Application.isPlaying && logPhaseChanges)
            {
                Debug.Log($"Time phase changed to {GetPhaseDisplayName(currentPhase)} at {GetClockText()} ({CurrentTick} ticks).", this);
            }
        }
    }

    private void ApplySky()
    {
        Material material = GetSkyboxMaterial();
        if (material == null)
        {
            return;
        }

        if (assignSkyboxOnEnable && RenderSettings.skybox != material)
        {
            RenderSettings.skybox = material;
        }

        if (directionalLight != null)
        {
            RenderSettings.sun = directionalLight;
        }

        Vector3 sunDirection = directionalLight != null ? -directionalLight.transform.forward : Vector3.up;
        if (sunDirection.sqrMagnitude < 0.0001f)
        {
            sunDirection = Vector3.up;
        }

        sunDirection.Normalize();
        Vector3 moonDirection = -sunDirection;

        material.SetFloat(UseMainLightDirectionId, useUrpMainLightDirection ? 1f : 0f);
        material.SetVector(SunDirectionId, new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, 0f));
        material.SetVector(MoonDirectionId, new Vector4(moonDirection.x, moonDirection.y, moonDirection.z, 0f));

        ApplySceneLighting(sunDirection.y);
    }

    private Material GetSkyboxMaterial()
    {
        if (skyboxMaterial != null)
        {
            return skyboxMaterial;
        }

        if (RenderSettings.skybox != null &&
            RenderSettings.skybox.shader != null &&
            RenderSettings.skybox.shader.name == SkyboxShaderName)
        {
            return RenderSettings.skybox;
        }

        if (runtimeSkyboxMaterial != null)
        {
            return runtimeSkyboxMaterial;
        }

        Shader shader = Shader.Find(SkyboxShaderName);
        if (shader == null)
        {
            return null;
        }

        runtimeSkyboxMaterial = new Material(shader)
        {
            name = "Runtime Voxel Procedural Skybox",
            hideFlags = HideFlags.DontSave
        };

        return runtimeSkyboxMaterial;
    }

    private void ApplySceneLighting(float sunHeight)
    {
        CalculatePhaseWeights(sunHeight, out float dayWeight, out float twilightWeight, out float nightWeight);

        if (driveLightColorAndIntensity && directionalLight != null)
        {
            directionalLight.intensity =
                dayLightIntensity * dayWeight +
                twilightLightIntensity * twilightWeight +
                nightLightIntensity * nightWeight;

            directionalLight.color = BlendByPhase(dayLightColor, twilightLightColor, nightLightColor, dayWeight, twilightWeight, nightWeight);
        }

        if (driveAmbientLight)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = BlendByPhase(dayAmbientColor, twilightAmbientColor, nightAmbientColor, dayWeight, twilightWeight, nightWeight);
        }

        if (driveFog)
        {
            ApplyFog(dayWeight, twilightWeight, nightWeight);
        }

        if (driveVoxelMaterialLighting)
        {
            ApplyVoxelMaterialLighting(dayWeight, twilightWeight, nightWeight);
        }
    }

    private void ApplyVoxelMaterialLighting(float dayWeight, float twilightWeight, float nightWeight)
    {
        float minLight = BlendByPhase(dayVoxelMinLight, twilightVoxelMinLight, nightVoxelMinLight, dayWeight, twilightWeight, nightWeight);
        float skyLightMultiplier = BlendByPhase(dayVoxelSkyLightMultiplier, twilightVoxelSkyLightMultiplier, nightVoxelSkyLightMultiplier, dayWeight, twilightWeight, nightWeight);
        float ambientStrength = BlendByPhase(dayVoxelAmbientStrength, twilightVoxelAmbientStrength, nightVoxelAmbientStrength, dayWeight, twilightWeight, nightWeight);
        float directionalStrength = BlendByPhase(dayVoxelDirectionalStrength, twilightVoxelDirectionalStrength, nightVoxelDirectionalStrength, dayWeight, twilightWeight, nightWeight);
        float wrapLighting = BlendByPhase(dayVoxelWrapLighting, twilightVoxelWrapLighting, nightVoxelWrapLighting, dayWeight, twilightWeight, nightWeight);

        if (world != null)
        {
            world.SetVoxelSkyLightMultiplier(skyLightMultiplier);
        }

        appliedVoxelMaterials.Clear();

        if (includeWorldMaterials && world != null && world.Material != null)
        {
            ApplyVoxelLightingToMaterials(world.Material, minLight, skyLightMultiplier, ambientStrength, directionalStrength, wrapLighting);
        }

        ApplyVoxelLightingToMaterials(extraVoxelMaterials, minLight, skyLightMultiplier, ambientStrength, directionalStrength, wrapLighting);
    }

    private void ApplyVoxelLightingToMaterials(Material[] materials, float minLight, float skyLightMultiplier, float ambientStrength, float directionalStrength, float wrapLighting)
    {
        if (materials == null)
        {
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null || !appliedVoxelMaterials.Add(material))
            {
                continue;
            }

            SetFloatIfPresent(material, MinLightId, minLight);
            SetFloatIfPresent(material, VoxelSkyLightMultiplierId, skyLightMultiplier);
            SetFloatIfPresent(material, AmbientStrengthId, ambientStrength);
            SetFloatIfPresent(material, DirectionalStrengthId, directionalStrength);
            SetFloatIfPresent(material, WrapLightingId, wrapLighting);
        }
    }

    private static void SetFloatIfPresent(Material material, int propertyId, float value)
    {
        if (material.HasProperty(propertyId))
        {
            material.SetFloat(propertyId, value);
        }
    }

    private void ApplyFog(float dayWeight, float twilightWeight, float nightWeight)
    {
        Color fogColor = BlendByPhase(dayFogColor, twilightFogColor, nightFogColor, dayWeight, twilightWeight, nightWeight);
        float fogDensity =
            dayFogDensity * dayWeight +
            twilightFogDensity * twilightWeight +
            nightFogDensity * nightWeight;
        float fogStart =
            dayFogStart * dayWeight +
            twilightFogStart * twilightWeight +
            nightFogStart * nightWeight;
        float fogEnd =
            dayFogEnd * dayWeight +
            twilightFogEnd * twilightWeight +
            nightFogEnd * nightWeight;

        if (fogControl != null)
        {
            fogControl.FogColorSurface = fogColor;
            fogControl.FogStart = Mathf.RoundToInt(fogStart);
            fogControl.FogEnd = Mathf.RoundToInt(Mathf.Max(fogStart + 1f, fogEnd));
            fogControl.ApplyNow();
            fogControl.GetActiveFogSettings(out Color activeFogColor, out float activeFogStart, out float activeFogEnd);

            RenderSettings.fogColor = activeFogColor;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogStartDistance = activeFogStart;
            RenderSettings.fogEndDistance = Mathf.Max(activeFogStart + 1f, activeFogEnd);
            return;
        }

        RenderSettings.fogColor = fogColor;
        RenderSettings.fogDensity = fogDensity;
        RenderSettings.fogStartDistance = fogStart;
        RenderSettings.fogEndDistance = Mathf.Max(fogStart + 1f, fogEnd);
    }

    private DayPhase EvaluatePhase(float tick)
    {
        tick = WrapMinecraftTicks(tick);

        if (IsTickInWrappedRange(tick, dawnStartTick, dayStartTick))
        {
            return DayPhase.Dawn;
        }

        if (IsTickInWrappedRange(tick, dayStartTick, duskStartTick))
        {
            return DayPhase.Day;
        }

        if (IsTickInWrappedRange(tick, duskStartTick, nightStartTick))
        {
            return DayPhase.Dusk;
        }

        return DayPhase.Night;
    }

    private string GetPhaseDisplayName(DayPhase phase)
    {
        switch (phase)
        {
            case DayPhase.Dawn:
                return string.IsNullOrWhiteSpace(dawnDisplayName) ? "Dawn" : dawnDisplayName;
            case DayPhase.Day:
                return string.IsNullOrWhiteSpace(dayDisplayName) ? "Day" : dayDisplayName;
            case DayPhase.Dusk:
                return string.IsNullOrWhiteSpace(duskDisplayName) ? "Dusk" : duskDisplayName;
            case DayPhase.Night:
                return string.IsNullOrWhiteSpace(nightDisplayName) ? "Night" : nightDisplayName;
            default:
                return phase.ToString();
        }
    }

    private static void CalculatePhaseWeights(float sunHeight, out float dayWeight, out float twilightWeight, out float nightWeight)
    {
        dayWeight = SmoothRange(0.02f, 0.22f, sunHeight);
        nightWeight = 1f - SmoothRange(-0.22f, -0.05f, sunHeight);
        twilightWeight = Mathf.Clamp01(1f - dayWeight - nightWeight);

        float total = Mathf.Max(0.0001f, dayWeight + twilightWeight + nightWeight);
        dayWeight /= total;
        twilightWeight /= total;
        nightWeight /= total;
    }

    private static bool IsTickInWrappedRange(float tick, float start, float end)
    {
        start = WrapMinecraftTicks(start);
        end = WrapMinecraftTicks(end);

        if (Mathf.Approximately(start, end))
        {
            return false;
        }

        if (start < end)
        {
            return tick >= start && tick < end;
        }

        return tick >= start || tick < end;
    }

    private static float TickToNormalizedTime(float tick)
    {
        return WrapMinecraftTicks(tick) / MinecraftTicksPerDay;
    }

    private static float NormalizedTimeToTick(float normalizedTime)
    {
        return WrapMinecraftTicks(Mathf.Repeat(normalizedTime, 1f) * MinecraftTicksPerDay);
    }

    private static float WrapMinecraftTicks(float tick)
    {
        return Mathf.Repeat(tick, MinecraftTicksPerDay);
    }

    private static float SmoothRange(float min, float max, float value)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(min, max, value));
    }

    private static Color BlendByPhase(Color day, Color twilight, Color night, float dayWeight, float twilightWeight, float nightWeight)
    {
        return day * dayWeight + twilight * twilightWeight + night * nightWeight;
    }

    private static float BlendByPhase(float day, float twilight, float night, float dayWeight, float twilightWeight, float nightWeight)
    {
        return day * dayWeight + twilight * twilightWeight + night * nightWeight;
    }
}
