using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(World))]
public sealed class TorchFireParticleController : MonoBehaviour
{
    [Header("Scan")]
    [SerializeField, Min(0.05f)] private float scanInterval = 0.2f;
    [SerializeField, Min(1)] private int horizontalScanRadius = 12;
    [SerializeField, Min(1)] private int verticalScanRadius = 8;
    [SerializeField, Min(1)] private int maxActiveTorchEffects = 48;
    [SerializeField] private bool useCameraFallback = true;

    [Header("Style")]
    [SerializeField, Min(0f)] private float smokeHeightOffset = 0.05f;
    [SerializeField, Min(0.1f)] private float effectScale = 1f;

    [Header("Custom Effect")]
    [Tooltip("Prefab opcional com um ou mais ParticleSystems. Se definido, ele e instanciado nas tochas em vez do efeito gerado por codigo.")]
    [SerializeField] private GameObject overrideTorchEffectPrefab;

    private readonly Dictionary<Vector3Int, TorchEffectInstance> activeEffects = new Dictionary<Vector3Int, TorchEffectInstance>();
    private readonly HashSet<Vector3Int> scannedPositions = new HashSet<Vector3Int>();
    private readonly List<Vector3Int> pendingRemovals = new List<Vector3Int>();
    private readonly List<TorchScanCandidate> scanCandidates = new List<TorchScanCandidate>(64);

    private World world;
    private Transform observer;
    private float nextScanTime;
    private Vector3Int lastObserverCell = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

    private sealed class TorchEffectInstance
    {
        public GameObject root;
        public ParticleSystem generatedSmoke;
        public ParticleSystem[] systems;
        public bool usesGeneratedLayout;
        public BlockType blockType;
    }

    private struct TorchScanCandidate
    {
        public Vector3Int position;
        public BlockType blockType;
        public int distanceSqr;
    }

    private void Awake()
    {
        world = GetComponent<World>();
    }

    private void OnDisable()
    {
        ClearAllEffects();
    }

    private void Update()
    {
        if (!Application.isPlaying || world == null)
            return;

        ResolveObserver();
        if (observer == null)
        {
            ClearAllEffects();
            return;
        }

        Vector3Int observerCell = Vector3Int.FloorToInt(observer.position);
        bool observerMoved = observerCell != lastObserverCell;
        if (!observerMoved && Time.time < nextScanTime)
            return;

        RefreshVisibleTorches(observerCell);
        lastObserverCell = observerCell;
        nextScanTime = Time.time + Mathf.Max(0.05f, scanInterval);
    }

    public void NotifyBlockChanged(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!Application.isPlaying || world == null)
            return;

        ResolveObserver();
        if (observer == null)
            return;

        Vector3Int observerCell = Vector3Int.FloorToInt(observer.position);
        bool wasTracked = activeEffects.ContainsKey(worldPos);
        if (!wasTracked && !IsWithinScanVolume(worldPos, observerCell))
            return;

        if (!TryGetTorchType(worldPos, out BlockType currentType))
        {
            RemoveEffect(worldPos);
            return;
        }

        EnsureEffect(worldPos, currentType);
    }

    private void ResolveObserver()
    {
        if (world != null && world.player != null)
        {
            observer = world.player;
            return;
        }

        if (useCameraFallback && Camera.main != null)
        {
            observer = Camera.main.transform;
            return;
        }

        observer = null;
    }

    private void RefreshVisibleTorches(Vector3Int observerCell)
    {
        scanCandidates.Clear();
        scannedPositions.Clear();

        int horizontalRadiusSqr = horizontalScanRadius * horizontalScanRadius;
        for (int y = observerCell.y - verticalScanRadius; y <= observerCell.y + verticalScanRadius; y++)
        {
            if (y < 0 || y >= Chunk.SizeY)
                continue;

            for (int dz = -horizontalScanRadius; dz <= horizontalScanRadius; dz++)
            {
                for (int dx = -horizontalScanRadius; dx <= horizontalScanRadius; dx++)
                {
                    int horizontalDistanceSqr = dx * dx + dz * dz;
                    if (horizontalDistanceSqr > horizontalRadiusSqr)
                        continue;

                    Vector3Int position = new Vector3Int(observerCell.x + dx, y, observerCell.z + dz);
                    if (!TryGetTorchType(position, out BlockType torchType))
                        continue;

                    int dy = y - observerCell.y;
                    scanCandidates.Add(new TorchScanCandidate
                    {
                        position = position,
                        blockType = torchType,
                        distanceSqr = horizontalDistanceSqr + dy * dy
                    });
                }
            }
        }

        if (scanCandidates.Count > 1)
            scanCandidates.Sort((a, b) => a.distanceSqr.CompareTo(b.distanceSqr));

        int limit = maxActiveTorchEffects > 0
            ? Mathf.Min(maxActiveTorchEffects, scanCandidates.Count)
            : scanCandidates.Count;

        for (int i = 0; i < limit; i++)
        {
            TorchScanCandidate candidate = scanCandidates[i];
            scannedPositions.Add(candidate.position);
            EnsureEffect(candidate.position, candidate.blockType);
        }

        pendingRemovals.Clear();
        foreach (KeyValuePair<Vector3Int, TorchEffectInstance> entry in activeEffects)
        {
            if (!scannedPositions.Contains(entry.Key))
                pendingRemovals.Add(entry.Key);
        }

        for (int i = 0; i < pendingRemovals.Count; i++)
            RemoveEffect(pendingRemovals[i]);
    }

    private bool TryGetTorchType(Vector3Int worldPos, out BlockType blockType)
    {
        blockType = BlockType.Air;
        return world != null &&
               world.TryGetLoadedBlockAt(worldPos, out blockType) &&
               TorchPlacementUtility.IsTorchFireSource(blockType);
    }

    private bool IsWithinScanVolume(Vector3Int worldPos, Vector3Int observerCell)
    {
        int dy = Mathf.Abs(worldPos.y - observerCell.y);
        if (dy > verticalScanRadius)
            return false;

        int dx = worldPos.x - observerCell.x;
        int dz = worldPos.z - observerCell.z;
        return dx * dx + dz * dz <= horizontalScanRadius * horizontalScanRadius;
    }

    private void EnsureEffect(Vector3Int blockPos, BlockType blockType)
    {
        if (!activeEffects.TryGetValue(blockPos, out TorchEffectInstance instance) || instance == null || instance.root == null)
        {
            instance = CreateEffect(blockPos, blockType);
            activeEffects[blockPos] = instance;
            return;
        }

        instance.blockType = blockType;
        UpdateEffectTransform(instance, blockPos, blockType);
        EnsureEffectPlaying(instance);
    }

    private TorchEffectInstance CreateEffect(Vector3Int blockPos, BlockType blockType)
    {
        GameObject effectPrefab = ResolveTorchEffectPrefab();
        if (effectPrefab != null)
        {
            TorchEffectInstance prefabInstance = CreatePrefabEffect(effectPrefab, blockPos, blockType);
            if (prefabInstance != null)
                return prefabInstance;
        }

        return CreateGeneratedEffect(blockPos, blockType);
    }

    private GameObject ResolveTorchEffectPrefab()
    {
        if (overrideTorchEffectPrefab != null)
            return overrideTorchEffectPrefab;

        return world != null ? world.torchFireEffectPrefab : null;
    }

    private TorchEffectInstance CreatePrefabEffect(GameObject effectPrefab, Vector3Int blockPos, BlockType blockType)
    {
        TorchEffectInstance instance = new TorchEffectInstance();
        instance.root = Instantiate(effectPrefab, transform);
        instance.root.name = $"TorchFire_{blockPos.x}_{blockPos.y}_{blockPos.z}";
        instance.root.SetActive(true);
        instance.root.transform.localScale *= Mathf.Max(0.1f, effectScale);
        instance.systems = instance.root.GetComponentsInChildren<ParticleSystem>(true);
        if (instance.systems == null || instance.systems.Length == 0)
        {
            Destroy(instance.root);
            return null;
        }

        instance.usesGeneratedLayout = false;
        instance.blockType = blockType;
        UpdateEffectTransform(instance, blockPos, blockType);
        EnsureEffectPlaying(instance);
        return instance;
    }

    private TorchEffectInstance CreateGeneratedEffect(Vector3Int blockPos, BlockType blockType)
    {
        TorchEffectInstance instance = new TorchEffectInstance();
        instance.root = new GameObject($"TorchFire_{blockPos.x}_{blockPos.y}_{blockPos.z}");
        instance.root.transform.SetParent(transform, false);
        instance.root.transform.localScale = Vector3.one * Mathf.Max(0.1f, effectScale);

        ParticleSystem flame = CreateParticleSystemObject("Flame", instance.root.transform, Vector3.zero);
        ConfigureFlameSystem(flame);

        instance.generatedSmoke = CreateParticleSystemObject("Smoke", instance.root.transform, Vector3.up * smokeHeightOffset);
        ConfigureSmokeSystem(instance.generatedSmoke);

        instance.systems = new[] { flame, instance.generatedSmoke };
        instance.usesGeneratedLayout = true;
        instance.blockType = blockType;
        UpdateEffectTransform(instance, blockPos, blockType);
        EnsureEffectPlaying(instance);
        return instance;
    }

    private static ParticleSystem CreateParticleSystemObject(string name, Transform parent, Vector3 localPosition)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        return go.AddComponent<ParticleSystem>();
    }

    private static void ConfigureFlameSystem(ParticleSystem system)
    {
        var main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.duration = 1f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.maxParticles = 24;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.38f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.04f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.045f, 0.085f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = 0f;

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 18f;

        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.012f;

        var velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.y = new ParticleSystem.MinMaxCurve(0.08f, 0.18f);
        velocity.x = new ParticleSystem.MinMaxCurve(-0.015f, 0.015f);
        velocity.z = new ParticleSystem.MinMaxCurve(-0.015f, 0.015f);

        var noise = system.noise;
        noise.enabled = true;
        noise.strength = new ParticleSystem.MinMaxCurve(0.025f, 0.07f);
        noise.frequency = 0.7f;
        noise.scrollSpeed = 0.3f;
        noise.damping = true;

        var sizeOverLifetime = system.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, BuildFlameSizeCurve());

        var colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildFlameGradient());

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.OldestInFront;
        renderer.alignment = ParticleSystemRenderSpace.View;
    }

    private static void ConfigureSmokeSystem(ParticleSystem system)
    {
        var main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.duration = 1.5f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.maxParticles = 16;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.35f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.04f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.11f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);

        var emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 3f;

        var shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.01f;

        var velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.y = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
        velocity.x = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);
        velocity.z = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);

        var noise = system.noise;
        noise.enabled = true;
        noise.strength = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        noise.frequency = 0.45f;
        noise.scrollSpeed = 0.18f;
        noise.damping = true;

        var sizeOverLifetime = system.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, BuildSmokeSizeCurve());

        var colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildSmokeGradient());

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.OldestInFront;
        renderer.alignment = ParticleSystemRenderSpace.View;
    }

    private static AnimationCurve BuildFlameSizeCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.2f, 0.95f),
            new Keyframe(0.7f, 0.65f),
            new Keyframe(1f, 0.05f));
    }

    private static AnimationCurve BuildSmokeSizeCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.45f, 0.8f),
            new Keyframe(1f, 1.2f));
    }

    private static Gradient BuildFlameGradient()
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.98f, 0.88f), 0f),
                new GradientColorKey(new Color(1f, 0.8f, 0.28f), 0.28f),
                new GradientColorKey(new Color(1f, 0.42f, 0.08f), 0.72f),
                new GradientColorKey(new Color(0.45f, 0.1f, 0.03f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.95f, 0.12f),
                new GradientAlphaKey(0.8f, 0.7f),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }

    private static Gradient BuildSmokeGradient()
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.28f, 0.28f, 0.28f), 0f),
                new GradientColorKey(new Color(0.18f, 0.18f, 0.18f), 0.45f),
                new GradientColorKey(new Color(0.1f, 0.1f, 0.1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.18f, 0.18f),
                new GradientAlphaKey(0.09f, 0.55f),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }

    private void UpdateEffectTransform(TorchEffectInstance instance, Vector3Int blockPos, BlockType blockType)
    {
        if (instance == null || instance.root == null)
            return;

        instance.root.transform.position = TorchPlacementUtility.GetFireAnchorWorldPosition(blockPos, blockType);
        if (instance.usesGeneratedLayout && instance.generatedSmoke != null)
            instance.generatedSmoke.transform.localPosition = Vector3.up * smokeHeightOffset;
    }

    private static void EnsureEffectPlaying(TorchEffectInstance instance)
    {
        if (instance?.systems == null)
            return;

        for (int i = 0; i < instance.systems.Length; i++)
        {
            ParticleSystem system = instance.systems[i];
            if (system != null && !system.isPlaying)
                system.Play(true);
        }
    }

    private void RemoveEffect(Vector3Int worldPos)
    {
        if (!activeEffects.TryGetValue(worldPos, out TorchEffectInstance instance))
            return;

        if (instance != null && instance.root != null)
            Destroy(instance.root);

        activeEffects.Remove(worldPos);
    }

    private void ClearAllEffects()
    {
        foreach (KeyValuePair<Vector3Int, TorchEffectInstance> entry in activeEffects)
        {
            if (entry.Value != null && entry.Value.root != null)
                Destroy(entry.Value.root);
        }

        activeEffects.Clear();
        scannedPositions.Clear();
        pendingRemovals.Clear();
        scanCandidates.Clear();
    }
}
