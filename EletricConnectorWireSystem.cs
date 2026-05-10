using System.Collections.Generic;
using UnityEngine;

public readonly struct EletricWireConnectionSnapshot
{
    public readonly Vector3Int Start;
    public readonly Vector3Int End;

    public EletricWireConnectionSnapshot(Vector3Int start, Vector3Int end)
    {
        Start = start;
        End = end;
    }
}

public class EletricConnectorWireSystem : MonoBehaviour
{
    private sealed class WireConnection
    {
        public Vector3Int start;
        public Vector3Int end;
        public GameObject root;
        public LineRenderer line;
    }

    private static EletricConnectorWireSystem instance;

    [Header("Wire Visual")]
    [SerializeField] private Material lineMaterial;
    [SerializeField] private Color wireColor = new Color(0.04f, 0.035f, 0.03f, 1f);
    [SerializeField, Min(0.005f)] private float wireWidth = 0.045f;
    [SerializeField] private VoxelLineRendererStyle wireLineStyle = new VoxelLineRendererStyle
    {
        color = new Color(0.04f, 0.035f, 0.03f, 1f),
        width = 0.045f,
        capVertices = 6,
        cornerVertices = 6
    };
    [SerializeField, Min(2)] private int wireSegments = 18;
    [SerializeField, Min(0f)] private float sagPerBlock = 0.055f;
    [SerializeField, Min(0f)] private float maxSag = 1.15f;
    [SerializeField] private Vector3 connectorAnchorOffset = new Vector3(0.5f, 0.53f, 0.5f);
    [SerializeField] private Vector3 solarPanelAnchorOffset = new Vector3(0.5f, 0.28f, 0.5f);
    [SerializeField] private Vector3 windMillAnchorOffset = new Vector3(0.5f, 0.65f, 0.5f);

    [Header("Connection Rules")]
    [SerializeField, Min(1f)] private float maxConnectionDistanceBlocks = 16f;

    [Header("Selection Visual")]
    [SerializeField] private Color pendingColor = new Color(1f, 0.72f, 0.18f, 1f);
    [SerializeField, Min(0.01f)] private float pendingMarkerRadius = 0.28f;
    [SerializeField, Min(0.005f)] private float pendingMarkerWidth = 0.025f;
    [SerializeField] private VoxelLineRendererStyle pendingMarkerLineStyle = new VoxelLineRendererStyle
    {
        color = new Color(1f, 0.72f, 0.18f, 1f),
        width = 0.025f,
        capVertices = 6,
        cornerVertices = 6
    };
    [SerializeField, Min(8)] private int pendingMarkerSegments = 28;
    [SerializeField, Min(0f)] private float pendingMarkerPulseAmount = 0.035f;
    [SerializeField, Min(0f)] private float pendingMarkerPulseSpeed = 4f;

    [Header("Cleanup")]
    [SerializeField, Min(0.1f)] private float validationIntervalSeconds = 0.5f;

    private readonly List<WireConnection> connections = new List<WireConnection>();
    private World subscribedWorld;
    private Transform connectionsRoot;
    private LineRenderer pendingMarker;
    private bool hasPendingConnector;
    private Vector3Int pendingConnector;
    private float nextValidationTime;

    public static EletricConnectorWireSystem EnsureInstance()
    {
        if (instance != null)
            return instance;

        EletricConnectorWireSystem existing = FindAnyObjectByType<EletricConnectorWireSystem>();
        if (existing != null)
        {
            instance = existing;
            return instance;
        }

        GameObject systemObject = new GameObject(nameof(EletricConnectorWireSystem));
        instance = systemObject.AddComponent<EletricConnectorWireSystem>();
        return instance;
    }

    public static void CancelPendingSelectionIfAny()
    {
        if (instance != null)
            instance.ClearPendingConnector();
    }

    public static void CopyActiveConnections(List<EletricWireConnectionSnapshot> output)
    {
        if (output == null || instance == null)
            return;

        instance.CopyConnections(output);
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        SyncWorldSubscription();
    }

    private void OnEnable()
    {
        SyncWorldSubscription();
    }

    private void OnDisable()
    {
        UnsubscribeFromWorld();
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        ClearConnections();
        DestroyPendingMarker();
        wireLineStyle?.DestroyRuntimeMaterial();
        pendingMarkerLineStyle?.DestroyRuntimeMaterial();
    }

    private void Update()
    {
        SyncWorldSubscription();

        if (hasPendingConnector && !IsConnectorStillValid(pendingConnector))
            ClearPendingConnector();

        UpdatePendingMarker();

        if (Time.time >= nextValidationTime)
        {
            nextValidationTime = Time.time + Mathf.Max(0.1f, validationIntervalSeconds);
            PruneInvalidConnections();
            RefreshConnectionLines();
        }
    }

    public bool TryHandleConnectorInteraction(
        BlockSelector selector,
        HotbarMirror hotbar,
        bool consumeWireOnConnection)
    {
        if (!TryGetTargetConnector(selector, out Vector3Int connectorPos))
        {
            ClearPendingConnector();
            return false;
        }

        if (!hasPendingConnector || !IsConnectorStillValid(pendingConnector))
        {
            SetPendingConnector(connectorPos);
            return true;
        }

        if (pendingConnector == connectorPos)
        {
            ClearPendingConnector();
            return true;
        }

        if (!IsWithinMaxConnectionDistance(pendingConnector, connectorPos, out float distanceBlocks))
        {
            Debug.Log(
                $"[EletricConnectorWireSystem] Conexao recusada: distancia {distanceBlocks:0.##} blocos, maximo {maxConnectionDistanceBlocks:0.##}.");
            return true;
        }

        Vector3Int start = pendingConnector;
        Vector3Int end = connectorPos;
        ClearPendingConnector();

        if (TryRemoveConnection(start, end))
            return true;

        if (consumeWireOnConnection && hotbar != null && !hotbar.TryConsumeSelected(1))
            return true;

        CreateConnection(start, end);
        return true;
    }

    private bool TryGetTargetConnector(BlockSelector selector, out Vector3Int connectorPos)
    {
        connectorPos = default;
        World world = World.Instance;
        if (selector == null || world == null || !selector.TryGetSelectedBlock(out connectorPos, out _))
            return false;

        return IsWireEndpointBlock(world.GetBlockAt(connectorPos));
    }

    private void SetPendingConnector(Vector3Int connectorPos)
    {
        pendingConnector = connectorPos;
        hasPendingConnector = true;
        EnsurePendingMarker();
        UpdatePendingMarker();
    }

    private void ClearPendingConnector()
    {
        hasPendingConnector = false;
        pendingConnector = default;

        if (pendingMarker != null)
            pendingMarker.gameObject.SetActive(false);
    }

    private void CreateConnection(Vector3Int start, Vector3Int end)
    {
        SortEndpoints(ref start, ref end);

        GameObject wireObject = new GameObject(
            $"EletricWire_{start.x}_{start.y}_{start.z}_to_{end.x}_{end.y}_{end.z}");
        wireObject.transform.SetParent(EnsureConnectionsRoot(), false);

        LineRenderer line = wireObject.AddComponent<LineRenderer>();
        ConfigureWireLineRenderer(line);

        WireConnection connection = new WireConnection
        {
            start = start,
            end = end,
            root = wireObject,
            line = line
        };

        connections.Add(connection);
        UpdateConnectionLine(connection);
        NotifyWorldElectricConnectionsChanged();
    }

    private Transform EnsureConnectionsRoot()
    {
        if (connectionsRoot != null)
            return connectionsRoot;

        Transform existing = transform.Find("Connections");
        if (existing != null)
        {
            connectionsRoot = existing;
            return connectionsRoot;
        }

        GameObject rootObject = new GameObject("Connections");
        rootObject.transform.SetParent(transform, false);
        rootObject.transform.localPosition = Vector3.zero;
        rootObject.transform.localRotation = Quaternion.identity;
        rootObject.transform.localScale = Vector3.one;
        connectionsRoot = rootObject.transform;
        return connectionsRoot;
    }

    private void ConfigureWireLineRenderer(LineRenderer line)
    {
        if (line == null)
            return;

        VoxelLineRendererStyle style = GetWireLineStyle();
        style.Configure(line, "Runtime Eletric Wire Line Material", loop: false);
    }

    private void ConfigurePendingMarkerLineRenderer(LineRenderer line)
    {
        if (line == null)
            return;

        VoxelLineRendererStyle style = GetPendingMarkerLineStyle();
        style.Configure(line, "Runtime Eletric Pending Marker Line Material", loop: true);
    }

    private VoxelLineRendererStyle GetWireLineStyle()
    {
        if (wireLineStyle == null)
        {
            wireLineStyle = new VoxelLineRendererStyle
            {
                material = lineMaterial,
                color = wireColor,
                width = Mathf.Max(0.005f, wireWidth),
                capVertices = 6,
                cornerVertices = 6
            };
        }
        else if (wireLineStyle.material == null && lineMaterial != null)
        {
            wireLineStyle.material = lineMaterial;
        }

        return wireLineStyle;
    }

    private VoxelLineRendererStyle GetPendingMarkerLineStyle()
    {
        if (pendingMarkerLineStyle == null)
        {
            pendingMarkerLineStyle = new VoxelLineRendererStyle
            {
                color = pendingColor,
                width = Mathf.Max(0.005f, pendingMarkerWidth),
                capVertices = 6,
                cornerVertices = 6
            };
        }

        return pendingMarkerLineStyle;
    }

    private void UpdateConnectionLine(WireConnection connection)
    {
        if (connection == null || connection.line == null)
            return;

        ConfigureWireLineRenderer(connection.line);

        int segmentCount = Mathf.Max(2, wireSegments);
        connection.line.positionCount = segmentCount;

        Vector3 start = GetConnectorAnchorWorldPosition(connection.start);
        Vector3 end = GetConnectorAnchorWorldPosition(connection.end);
        float distance = Vector3.Distance(start, end);
        float sag = Mathf.Min(maxSag, distance * sagPerBlock);
        for (int i = 0; i < segmentCount; i++)
        {
            float t = segmentCount <= 1 ? 0f : i / (float)(segmentCount - 1);
            Vector3 point = Vector3.Lerp(start, end, t);
            point.y -= Mathf.Sin(t * Mathf.PI) * sag;
            connection.line.SetPosition(i, point);
        }
    }

    private void EnsurePendingMarker()
    {
        if (pendingMarker != null)
            return;

        GameObject markerObject = new GameObject("PendingEletricConnectorWire");
        markerObject.transform.SetParent(transform, false);
        pendingMarker = markerObject.AddComponent<LineRenderer>();
        ConfigurePendingMarkerLineRenderer(pendingMarker);
        pendingMarker.gameObject.SetActive(false);
    }

    private void UpdatePendingMarker()
    {
        if (!hasPendingConnector)
        {
            if (pendingMarker != null)
                pendingMarker.gameObject.SetActive(false);
            return;
        }

        EnsurePendingMarker();
        if (pendingMarker == null)
            return;

        pendingMarker.gameObject.SetActive(true);
        ConfigurePendingMarkerLineRenderer(pendingMarker);

        int segmentCount = Mathf.Max(8, pendingMarkerSegments);
        pendingMarker.positionCount = segmentCount;

        Vector3 center = GetConnectorAnchorWorldPosition(pendingConnector);
        float pulse = pendingMarkerPulseAmount <= 0f
            ? 0f
            : (Mathf.Sin(Time.time * pendingMarkerPulseSpeed) * 0.5f + 0.5f) * pendingMarkerPulseAmount;
        float radius = pendingMarkerRadius + pulse;

        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i / (float)segmentCount * Mathf.PI * 2f;
            Vector3 point = center + new Vector3(Mathf.Cos(angle) * radius, 0.025f, Mathf.Sin(angle) * radius);
            pendingMarker.SetPosition(i, point);
        }
    }

    private Vector3 GetConnectorAnchorWorldPosition(Vector3Int blockPos)
    {
        World world = World.Instance;
        BlockType blockType = world != null ? world.GetBlockAt(blockPos) : BlockType.Air;
        return (Vector3)blockPos + GetAnchorOffset(blockType) + GetSurfaceAlignedAnchorOffset(world, blockPos, blockType);
    }

    private static Vector3 GetSurfaceAlignedAnchorOffset(World world, Vector3Int blockPos, BlockType blockType)
    {
        if (world == null || world.blockData == null)
            return Vector3.zero;

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return Vector3.zero;

        BlockTextureMapping mapping = mappingResult.Value;
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        if (shape != BlockRenderShape.Cuboid && shape != BlockRenderShape.MultiCuboid)
            return Vector3.zero;

        BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(blockPos, blockType);
        return BlockSupportSurfaceUtility.GetSurfaceAlignedWorldOffset(world, blockPos, blockType, placementAxis);
    }

    private Vector3 GetAnchorOffset(BlockType blockType)
    {
        if (blockType == BlockType.SolarPanel)
            return solarPanelAnchorOffset;

        if (blockType == BlockType.windmill)
            return windMillAnchorOffset;

        return connectorAnchorOffset;
    }

    private static bool IsWireEndpointBlock(BlockType blockType)
    {
        World world = World.Instance;
        if (world != null)
            return world.IsElectricalWireEndpointBlockType(blockType);

        return IsLegacyWireEndpointBlock(blockType);
    }

    private static bool IsLegacyWireEndpointBlock(BlockType blockType)
    {
        return blockType == BlockType.EletricConnector ||
               LeverUtility.IsLeverBlock(blockType) ||
               blockType == BlockType.RoboticArm ||
               blockType == BlockType.SolarPanel ||
               BatteryBlockUtility.IsBatteryBlock(blockType) ||
               blockType == BlockType.windmill ||
               blockType == BlockType.ledWhiteBlock ||
               blockType == BlockType.AutoMiner ||
               blockType == BlockType.StoneCrusher;
    }

    private bool TryRemoveConnection(Vector3Int start, Vector3Int end)
    {
        SortEndpoints(ref start, ref end);

        for (int i = 0; i < connections.Count; i++)
        {
            WireConnection connection = connections[i];
            if (connection == null || connection.start != start || connection.end != end)
                continue;

            RemoveConnectionAt(i);
            return true;
        }

        return false;
    }

    private bool IsWithinMaxConnectionDistance(Vector3Int start, Vector3Int end, out float distanceBlocks)
    {
        distanceBlocks = Vector3.Distance(start, end);
        return distanceBlocks <= maxConnectionDistanceBlocks + 0.001f;
    }

    private bool IsConnectorStillValid(Vector3Int connectorPos)
    {
        World world = World.Instance;
        return world != null && IsWireEndpointBlock(world.GetBlockAt(connectorPos));
    }

    private void PruneInvalidConnections()
    {
        for (int i = connections.Count - 1; i >= 0; i--)
        {
            WireConnection connection = connections[i];
            if (connection == null ||
                !IsConnectorStillValid(connection.start) ||
                !IsConnectorStillValid(connection.end))
            {
                RemoveConnectionAt(i);
            }
        }
    }

    private void RefreshConnectionLines()
    {
        for (int i = 0; i < connections.Count; i++)
            UpdateConnectionLine(connections[i]);
    }

    private void RemoveConnectionAt(int index)
    {
        if (index < 0 || index >= connections.Count)
            return;

        WireConnection connection = connections[index];
        connections.RemoveAt(index);

        if (connection != null && connection.root != null)
            DestroyRuntimeObject(connection.root);

        NotifyWorldElectricConnectionsChanged();
    }

    private void ClearConnections()
    {
        for (int i = connections.Count - 1; i >= 0; i--)
            RemoveConnectionAt(i);
    }

    private void DestroyPendingMarker()
    {
        if (pendingMarker == null)
            return;

        DestroyRuntimeObject(pendingMarker.gameObject);
        pendingMarker = null;
    }

    private void SyncWorldSubscription()
    {
        World currentWorld = World.Instance;
        if (subscribedWorld == currentWorld)
            return;

        UnsubscribeFromWorld();
        subscribedWorld = currentWorld;

        if (subscribedWorld != null)
            subscribedWorld.BlockChanged += HandleWorldBlockChanged;
    }

    private void UnsubscribeFromWorld()
    {
        if (subscribedWorld == null)
            return;

        subscribedWorld.BlockChanged -= HandleWorldBlockChanged;
        subscribedWorld = null;
    }

    private void HandleWorldBlockChanged(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!IsWireEndpointBlock(previousType) && !IsWireEndpointBlock(newType))
            return;

        if (hasPendingConnector && pendingConnector == worldPos && !IsWireEndpointBlock(newType))
            ClearPendingConnector();

        PruneInvalidConnections();
    }

    private void CopyConnections(List<EletricWireConnectionSnapshot> output)
    {
        PruneInvalidConnections();

        for (int i = 0; i < connections.Count; i++)
        {
            WireConnection connection = connections[i];
            if (connection == null)
                continue;

            output.Add(new EletricWireConnectionSnapshot(connection.start, connection.end));
        }
    }

    private static void NotifyWorldElectricConnectionsChanged()
    {
        if (World.Instance != null)
            World.Instance.NotifyElectricConnectorConnectionsChanged();
    }

    private static void SortEndpoints(ref Vector3Int start, ref Vector3Int end)
    {
        if (ComparePositions(start, end) <= 0)
            return;

        Vector3Int temp = start;
        start = end;
        end = temp;
    }

    private static int ComparePositions(Vector3Int left, Vector3Int right)
    {
        int x = left.x.CompareTo(right.x);
        if (x != 0)
            return x;

        int y = left.y.CompareTo(right.y);
        if (y != 0)
            return y;

        return left.z.CompareTo(right.z);
    }

    private static void DestroyRuntimeObject(Object target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }
}
