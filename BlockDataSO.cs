using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Right = 2, Left = 3, Front = 4, Back = 5, Side = 6 }
public enum BlockRenderShape : byte { Cube = 0, Cross = 1, Cuboid = 2, Plane = 3, Stairs = 4, Fence = 5, Ramp = 6, VerticalRamp = 7, MultiCuboid = 8, Fence2 = 9, Slab = 10 }
public enum BlockPlacementAxis : byte
{
    Y = 0,
    X = 1,
    Z = 2,
    XNegative = 3,
    ZNegative = 4,
    YNegative = 5
}
public enum BlockPlacementRotationAxes : byte { Vertical = 0, Horizontal = 1, Both = 2 }

[System.Flags]
public enum BlockCuboidFaceMask : byte
{
    None = 0,
    Right = 1 << 0,
    Left = 1 << 1,
    Top = 1 << 2,
    Bottom = 1 << 3,
    Front = 1 << 4,
    Back = 1 << 5,
    All = Right | Left | Top | Bottom | Front | Back
}

[System.Serializable]
public struct BlockModelCuboid
{
    [Tooltip("Canto minimo local do cuboide no espaco do bloco. Pode sair de 0..1 para modelos maiores que um voxel.")]
    public Vector3 min;

    [Tooltip("Canto maximo local do cuboide no espaco do bloco. Pode sair de 0..1 para modelos maiores que um voxel.")]
    public Vector3 max;

    [Tooltip("Rotacao local em graus ao redor do centro do cuboide.")]
    public Vector3 eulerRotation;

    [Tooltip("Faces desenhadas para este cuboide. None e tratado como All para facilitar a criacao no Inspector.")]
    public BlockCuboidFaceMask faces;

    [Tooltip("Faces que usam textura propria neste cuboide. Faces nao marcadas continuam usando o mapping do bloco.")]
    public BlockCuboidFaceMask textureOverrideFaces;

    [HideInInspector] public Vector2Int textureTop;
    [HideInInspector] public Vector2Int textureBottom;
    [HideInInspector] public Vector2Int textureRight;
    [HideInInspector] public Vector2Int textureLeft;
    [HideInInspector] public Vector2Int textureFront;
    [HideInInspector] public Vector2Int textureBack;

    [SerializeField, HideInInspector] private Vector4 textureTopUvRect;
    [SerializeField, HideInInspector] private Vector4 textureBottomUvRect;
    [SerializeField, HideInInspector] private Vector4 textureRightUvRect;
    [SerializeField, HideInInspector] private Vector4 textureLeftUvRect;
    [SerializeField, HideInInspector] private Vector4 textureFrontUvRect;
    [SerializeField, HideInInspector] private Vector4 textureBackUvRect;
    [SerializeField, HideInInspector] private bool runtimeUvRectOverridesInitialized;

    public BlockModelCuboid(Vector3 min, Vector3 max)
    {
        this.min = min;
        this.max = max;
        eulerRotation = Vector3.zero;
        faces = BlockCuboidFaceMask.All;
        textureOverrideFaces = BlockCuboidFaceMask.None;
        textureTop = Vector2Int.zero;
        textureBottom = Vector2Int.zero;
        textureRight = Vector2Int.zero;
        textureLeft = Vector2Int.zero;
        textureFront = Vector2Int.zero;
        textureBack = Vector2Int.zero;
        textureTopUvRect = Vector4.zero;
        textureBottomUvRect = Vector4.zero;
        textureRightUvRect = Vector4.zero;
        textureLeftUvRect = Vector4.zero;
        textureFrontUvRect = Vector4.zero;
        textureBackUvRect = Vector4.zero;
        runtimeUvRectOverridesInitialized = false;
    }

    public ShapeBox ToShapeBox()
    {
        return new ShapeBox(min, max);
    }

    public BlockCuboidFaceMask EffectiveFaces
    {
        get { return faces == BlockCuboidFaceMask.None ? BlockCuboidFaceMask.All : faces; }
    }

    public BlockCuboidFaceMask EffectiveTextureOverrideFaces
    {
        get { return textureOverrideFaces & BlockCuboidFaceMask.All; }
    }

    public bool HasFace(BlockFace face)
    {
        BlockCuboidFaceMask mask = EffectiveFaces;
        switch (face)
        {
            case BlockFace.Right: return (mask & BlockCuboidFaceMask.Right) != 0;
            case BlockFace.Left: return (mask & BlockCuboidFaceMask.Left) != 0;
            case BlockFace.Top: return (mask & BlockCuboidFaceMask.Top) != 0;
            case BlockFace.Bottom: return (mask & BlockCuboidFaceMask.Bottom) != 0;
            case BlockFace.Front: return (mask & BlockCuboidFaceMask.Front) != 0;
            case BlockFace.Back: return (mask & BlockCuboidFaceMask.Back) != 0;
            default: return false;
        }
    }

    public bool HasTextureOverride(BlockFace face)
    {
        return (EffectiveTextureOverrideFaces & GetMaskForFace(face)) != 0;
    }

    public Vector2Int GetTileCoord(BlockFace face, BlockTextureMapping fallbackMapping)
    {
        return HasTextureOverride(face)
            ? GetOverrideTileCoord(face)
            : fallbackMapping.GetTileCoord(face);
    }

    public void SetOverrideUvRectData(BlockFace face, Vector4 uvRectData)
    {
        switch (face)
        {
            case BlockFace.Top:
                textureTopUvRect = uvRectData;
                break;
            case BlockFace.Bottom:
                textureBottomUvRect = uvRectData;
                break;
            case BlockFace.Right:
                textureRightUvRect = uvRectData;
                break;
            case BlockFace.Left:
                textureLeftUvRect = uvRectData;
                break;
            case BlockFace.Front:
                textureFrontUvRect = uvRectData;
                break;
            case BlockFace.Back:
                textureBackUvRect = uvRectData;
                break;
            default:
                return;
        }

        runtimeUvRectOverridesInitialized = true;
    }

    public bool TryGetUvRectData(BlockFace face, BlockTextureMapping fallbackMapping, out Vector4 uvRectData)
    {
        if (HasTextureOverride(face) && TryGetOverrideUvRectData(face, out uvRectData))
            return true;

        return fallbackMapping.TryGetUvRectData(face, out uvRectData);
    }

    public void CopyUvRectOverrideDataFrom(BlockModelCuboid source)
    {
        textureTopUvRect = source.textureTopUvRect;
        textureBottomUvRect = source.textureBottomUvRect;
        textureRightUvRect = source.textureRightUvRect;
        textureLeftUvRect = source.textureLeftUvRect;
        textureFrontUvRect = source.textureFrontUvRect;
        textureBackUvRect = source.textureBackUvRect;
        runtimeUvRectOverridesInitialized = source.runtimeUvRectOverridesInitialized;
    }

    private Vector2Int GetOverrideTileCoord(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top: return textureTop;
            case BlockFace.Bottom: return textureBottom;
            case BlockFace.Right: return textureRight;
            case BlockFace.Left: return textureLeft;
            case BlockFace.Front: return textureFront;
            case BlockFace.Back: return textureBack;
            default: return Vector2Int.zero;
        }
    }

    private bool TryGetOverrideUvRectData(BlockFace face, out Vector4 uvRectData)
    {
        uvRectData = default;
        if (!runtimeUvRectOverridesInitialized)
            return false;

        switch (face)
        {
            case BlockFace.Top:
                uvRectData = textureTopUvRect;
                break;
            case BlockFace.Bottom:
                uvRectData = textureBottomUvRect;
                break;
            case BlockFace.Right:
                uvRectData = textureRightUvRect;
                break;
            case BlockFace.Left:
                uvRectData = textureLeftUvRect;
                break;
            case BlockFace.Front:
                uvRectData = textureFrontUvRect;
                break;
            case BlockFace.Back:
                uvRectData = textureBackUvRect;
                break;
            default:
                return false;
        }

        return BlockAtlasUvUtility.IsValidUvRectData(uvRectData);
    }

    private static BlockCuboidFaceMask GetMaskForFace(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Right: return BlockCuboidFaceMask.Right;
            case BlockFace.Left: return BlockCuboidFaceMask.Left;
            case BlockFace.Top: return BlockCuboidFaceMask.Top;
            case BlockFace.Bottom: return BlockCuboidFaceMask.Bottom;
            case BlockFace.Front: return BlockCuboidFaceMask.Front;
            case BlockFace.Back: return BlockCuboidFaceMask.Back;
            default: return BlockCuboidFaceMask.None;
        }
    }
}

[System.Serializable]
public sealed class BlockMultiCuboidDefinition
{
    public BlockType blockType;
    public List<BlockModelCuboid> cuboids = new List<BlockModelCuboid>();
}

[System.Serializable]
public sealed class BlockFaceTextureEntryIdSet
{
    public string top;
    public string bottom;
    public string right;
    public string left;
    public string front;
    public string back;

    public bool TryGet(BlockFace face, out string entryId)
    {
        entryId = Sanitize(Get(face));
        return !string.IsNullOrEmpty(entryId);
    }

    public string Get(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top: return top;
            case BlockFace.Bottom: return bottom;
            case BlockFace.Right: return right;
            case BlockFace.Left: return left;
            case BlockFace.Front: return front;
            case BlockFace.Back: return back;
            default: return string.Empty;
        }
    }

    public bool Set(BlockFace face, string entryId)
    {
        string sanitized = Sanitize(entryId);
        string current = Sanitize(Get(face));
        if (string.Equals(current, sanitized, System.StringComparison.Ordinal))
            return false;

        switch (face)
        {
            case BlockFace.Top:
                top = sanitized;
                return true;
            case BlockFace.Bottom:
                bottom = sanitized;
                return true;
            case BlockFace.Right:
                right = sanitized;
                return true;
            case BlockFace.Left:
                left = sanitized;
                return true;
            case BlockFace.Front:
                front = sanitized;
                return true;
            case BlockFace.Back:
                back = sanitized;
                return true;
            default:
                return false;
        }
    }

    public bool HasAny()
    {
        return
            !string.IsNullOrEmpty(Sanitize(top)) ||
            !string.IsNullOrEmpty(Sanitize(bottom)) ||
            !string.IsNullOrEmpty(Sanitize(right)) ||
            !string.IsNullOrEmpty(Sanitize(left)) ||
            !string.IsNullOrEmpty(Sanitize(front)) ||
            !string.IsNullOrEmpty(Sanitize(back));
    }

    public BlockFaceTextureEntryIdSet Clone()
    {
        return new BlockFaceTextureEntryIdSet
        {
            top = Sanitize(top),
            bottom = Sanitize(bottom),
            right = Sanitize(right),
            left = Sanitize(left),
            front = Sanitize(front),
            back = Sanitize(back)
        };
    }

    private static string Sanitize(string entryId)
    {
        return string.IsNullOrWhiteSpace(entryId) ? string.Empty : entryId.Trim();
    }
}

[System.Serializable]
public sealed class BlockTextureEntryIdMapping
{
    public BlockType blockType;
    public BlockFaceTextureEntryIdSet entryIds = new BlockFaceTextureEntryIdSet();
}

[System.Serializable]
public sealed class BlockCuboidTextureEntryIdMapping
{
    public BlockType blockType;
    public int cuboidIndex;
    public BlockFaceTextureEntryIdSet entryIds = new BlockFaceTextureEntryIdSet();
}

public enum BlockVisualStateCondition : byte
{
    None = 0,
    ElectricalPowered = 1,
    BatteryCharge25 = 2,
    BatteryCharge50 = 3,
    BatteryCharge75 = 4,
    BatteryCharge100 = 5
}

[System.Serializable]
public sealed class BlockStateTextureDefinition
{
    public BlockType blockType;
    public BlockVisualStateCondition condition = BlockVisualStateCondition.ElectricalPowered;

    [Tooltip("Opcional: aplica este entryId em todas as faces deste estado visual. Faces preenchidas abaixo sobrescrevem este valor.")]
    public string allFacesEntryId;

    public BlockFaceTextureEntryIdSet entryIds = new BlockFaceTextureEntryIdSet();
}

public struct BlockVisualStateTextureMapping
{
    private byte faceMask;
    private Vector4 topUvRect;
    private Vector4 bottomUvRect;
    private Vector4 rightUvRect;
    private Vector4 leftUvRect;
    private Vector4 frontUvRect;
    private Vector4 backUvRect;

    public bool HasAnyFace
    {
        get { return faceMask != 0; }
    }

    public void SetUvRectData(BlockFace face, Vector4 uvRectData)
    {
        if (!BlockAtlasUvUtility.IsValidUvRectData(uvRectData))
            return;

        faceMask |= GetMaskForFace(face);
        switch (face)
        {
            case BlockFace.Top:
                topUvRect = uvRectData;
                break;
            case BlockFace.Bottom:
                bottomUvRect = uvRectData;
                break;
            case BlockFace.Right:
                rightUvRect = uvRectData;
                break;
            case BlockFace.Left:
                leftUvRect = uvRectData;
                break;
            case BlockFace.Front:
                frontUvRect = uvRectData;
                break;
            case BlockFace.Back:
                backUvRect = uvRectData;
                break;
        }
    }

    public bool TryGetUvRectData(BlockFace face, out Vector4 uvRectData)
    {
        uvRectData = default;
        if ((faceMask & GetMaskForFace(face)) == 0)
            return false;

        switch (face)
        {
            case BlockFace.Top:
                uvRectData = topUvRect;
                break;
            case BlockFace.Bottom:
                uvRectData = bottomUvRect;
                break;
            case BlockFace.Right:
                uvRectData = rightUvRect;
                break;
            case BlockFace.Left:
                uvRectData = leftUvRect;
                break;
            case BlockFace.Front:
                uvRectData = frontUvRect;
                break;
            case BlockFace.Back:
                uvRectData = backUvRect;
                break;
            default:
                return false;
        }

        return BlockAtlasUvUtility.IsValidUvRectData(uvRectData);
    }

    private static byte GetMaskForFace(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top: return 1 << 0;
            case BlockFace.Bottom: return 1 << 1;
            case BlockFace.Right: return 1 << 2;
            case BlockFace.Left: return 1 << 3;
            case BlockFace.Front: return 1 << 4;
            case BlockFace.Back: return 1 << 5;
            default: return 0;
        }
    }
}

public static class BlockVisualStateUtility
{
    public const int StateCount = (int)BlockVisualStateCondition.BatteryCharge100 + 1;

    public static bool IsValidState(BlockVisualStateCondition state)
    {
        return state > BlockVisualStateCondition.None &&
               (int)state < StateCount;
    }

    public static int GetTextureMappingIndex(BlockType blockType, BlockVisualStateCondition state, int blockTypeCount)
    {
        return GetTextureMappingIndex((int)blockType, (byte)state, blockTypeCount);
    }

    public static int GetTextureMappingIndex(int blockTypeIndex, byte state, int blockTypeCount)
    {
        if (blockTypeIndex < 0 ||
            blockTypeIndex >= blockTypeCount ||
            state == 0 ||
            state >= StateCount)
        {
            return -1;
        }

        return blockTypeIndex * StateCount + state;
    }

    public static BlockVisualStateCondition GetBatteryChargeState(float charge01)
    {
        float charge = Mathf.Clamp01(charge01);
        if (charge <= 0.01f)
            return BlockVisualStateCondition.None;

        if (charge <= 0.25f)
            return BlockVisualStateCondition.BatteryCharge25;

        if (charge <= 0.5f)
            return BlockVisualStateCondition.BatteryCharge50;

        if (charge <= 0.75f)
            return BlockVisualStateCondition.BatteryCharge75;

        return BlockVisualStateCondition.BatteryCharge100;
    }
}

[System.Serializable]
public sealed class DynamicBlockPrefabDefinition
{
    public BlockType blockType;
    public GameObject prefab;

    [Tooltip("Offset local aplicado a partir da origem do voxel no chunk.")]
    public Vector3 localOffset = Vector3.zero;

    [Tooltip("Rotacao local adicional do prefab.")]
    public Vector3 localEulerAngles = Vector3.zero;

    [Tooltip("Escala local aplicada ao prefab instanciado.")]
    public Vector3 localScale = Vector3.one;

    [Tooltip("Quando ativo, usa o eixo salvo na colocacao do bloco para girar o prefab no eixo Y.")]
    public bool rotateWithPlacementAxis = true;

    [Tooltip("Eixos permitidos para salvar a rotacao de colocacao do prefab dinamico.")]
    public BlockPlacementRotationAxes placementRotationAxes = BlockPlacementRotationAxes.Horizontal;

    [Tooltip("Quando ativo, copia o layer do chunk para todos os filhos do prefab.")]
    public bool inheritChunkLayer = true;

    [Header("Comportamento Voxel")]
    [Tooltip("Define se o bloco dinamico cria collider e conta como suporte solido. Nao oculta faces vizinhas; blocos dinamicos sao sempre transparentes para renderizacao.")]
    public bool isSolid = true;

    [Tooltip("Define se o bloco dinamico se comporta como liquido.")]
    public bool isLiquid;

    [Tooltip("Marca o bloco dinamico como fonte de luz.")]
    public bool isLightSource;

    [Tooltip("Quando ativo, o bloco dinamico quebra se nao estiver ligado a um suporte solido estavel.")]
    public bool breaksWithoutSupport;

    [Tooltip("0 = nao reduz luz, 15 = bloqueia luz.")]
    [Range(0, 15)] public byte lightOpacity = 15;

    [Tooltip("Intensidade da luz emitida pelo bloco dinamico, de 0 a 15.")]
    [Range(0, 15)] public byte lightEmission;

    [Tooltip("Cor RGB da luz emitida. Preto mantem compatibilidade e emite branco quando Light Emission > 0.")]
    public Color lightColor = Color.white;

    [Header("Ocupacao Voxel")]
    [Tooltip("Quantidade de blocos ocupados no plano horizontal X/Z a partir do voxel de origem.")]
    [Min(1)] public int occupiedHorizontalBlocks = 1;

    [Tooltip("Quantidade de blocos ocupados para cima a partir do voxel de origem.")]
    [Min(1)] public int occupiedVerticalBlocks = 1;
}

[System.Serializable]
public sealed class BlockDropDefinition
{
    public BlockType blockType;
    [Tooltip("Item que substitui o drop padrao do bloco. Deixe vazio para o bloco dropar ele mesmo.")]
    public Item dropItem;
    [Tooltip("Quantidade dropada. 0 em assets antigos e tratado como 1 em runtime.")]
    [Min(1)] public int amount = 1;
}

public static class WirePlacementUtility
{
    public const byte SideWest = 16;
    public const byte SideEast = 17;
    public const byte SideSouth = 18;
    public const byte SideNorth = 19;
    public const byte TopWest = 20;
    public const byte TopEast = 21;
    public const byte TopSouth = 22;
    public const byte TopNorth = 23;

    public static bool IsEncodedState(byte rawValue)
    {
        return rawValue >= SideWest && rawValue <= TopNorth;
    }

    public static byte ResolvePlacementCode(Vector3Int hitNormal)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return hitNormal.x > 0 ? SideWest : SideEast;

        if (Mathf.Abs(hitNormal.z) > 0)
            return hitNormal.z > 0 ? SideSouth : SideNorth;

        return (byte)BlockPlacementAxis.Y;
    }

    public static bool HasTop(byte rawValue)
    {
        return rawValue == (byte)BlockPlacementAxis.Y ||
               rawValue == (byte)BlockPlacementAxis.XNegative ||
               rawValue == (byte)BlockPlacementAxis.ZNegative ||
               rawValue == TopWest ||
               rawValue == TopEast ||
               rawValue == TopSouth ||
               rawValue == TopNorth;
    }

    public static bool TryGetWall(byte rawValue, out BlockPlacementAxis axis, out int attachmentSide)
    {
        switch (rawValue)
        {
            case SideWest:
            case TopWest:
                axis = BlockPlacementAxis.X;
                attachmentSide = -1;
                return true;

            case SideEast:
            case TopEast:
                axis = BlockPlacementAxis.X;
                attachmentSide = 1;
                return true;

            case SideSouth:
            case TopSouth:
                axis = BlockPlacementAxis.Z;
                attachmentSide = -1;
                return true;

            case SideNorth:
            case TopNorth:
                axis = BlockPlacementAxis.Z;
                attachmentSide = 1;
                return true;

            default:
                axis = BlockPlacementAxis.Y;
                attachmentSide = 0;
                return false;
        }
    }

    public static bool TryMerge(byte existingRawValue, byte requestedRawValue, out byte mergedRawValue)
    {
        mergedRawValue = existingRawValue;

        bool existingHasTop = HasTop(existingRawValue);
        bool requestedHasTop = HasTop(requestedRawValue);
        bool existingHasWall = TryGetWall(existingRawValue, out BlockPlacementAxis existingWallAxis, out int existingAttachmentSide);
        bool requestedHasWall = TryGetWall(requestedRawValue, out BlockPlacementAxis requestedWallAxis, out int requestedAttachmentSide);

        if (!requestedHasTop && !requestedHasWall)
            return false;

        if (requestedHasTop && !requestedHasWall)
        {
            if (existingHasTop)
                return true;

            if (!existingHasWall)
            {
                mergedRawValue = (byte)BlockPlacementAxis.Y;
                return true;
            }

            mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, true);
            return true;
        }

        if (!requestedHasTop && requestedHasWall)
        {
            if (!existingHasWall)
            {
                mergedRawValue = Compose(requestedWallAxis, requestedAttachmentSide, existingHasTop);
                return true;
            }

            if (existingWallAxis != requestedWallAxis || existingAttachmentSide != requestedAttachmentSide)
                return false;

            if (existingHasTop)
                mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, true);
            else
                mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, false);

            return true;
        }

        if (!existingHasWall && !existingHasTop)
        {
            mergedRawValue = requestedRawValue;
            return true;
        }

        if (existingHasWall &&
            requestedHasWall &&
            (existingWallAxis != requestedWallAxis || existingAttachmentSide != requestedAttachmentSide))
        {
            return false;
        }

        mergedRawValue = Compose(
            existingHasWall ? existingWallAxis : requestedWallAxis,
            existingHasWall ? existingAttachmentSide : requestedAttachmentSide,
            existingHasTop || requestedHasTop);
        return true;
    }

    public static byte Compose(BlockPlacementAxis wallAxis, int attachmentSide, bool includeTop)
    {
        if (wallAxis == BlockPlacementAxis.X)
        {
            if (attachmentSide < 0)
                return includeTop ? TopWest : SideWest;

            return includeTop ? TopEast : SideEast;
        }

        if (attachmentSide < 0)
            return includeTop ? TopSouth : SideSouth;

        return includeTop ? TopNorth : SideNorth;
    }
}

public static class BlockFaceUtility
{
    public static BlockFace FromCubeFaceIndex(int faceIndex)
    {
        switch (faceIndex)
        {
            case 0: return BlockFace.Right;
            case 1: return BlockFace.Left;
            case 2: return BlockFace.Top;
            case 3: return BlockFace.Bottom;
            case 4: return BlockFace.Front;
            case 5: return BlockFace.Back;
            default: return BlockFace.Side;
        }
    }

    public static BlockFace FromAxisNormal(int axis, int normalSign)
    {
        switch (axis)
        {
            case 0:
                return normalSign > 0 ? BlockFace.Right : BlockFace.Left;

            case 1:
                return normalSign > 0 ? BlockFace.Top : BlockFace.Bottom;

            case 2:
                return normalSign > 0 ? BlockFace.Front : BlockFace.Back;

            default:
                return BlockFace.Side;
        }
    }
}

[CreateAssetMenu(fileName = "BlockDataSO", menuName = "ScriptableObjects/BlockDataSO", order = 1)]
public class BlockDataSO : ScriptableObject
{
    [Header("Texturas")]
    public Vector2 atlasSize = new Vector2(4, 4); // numero de tiles X,Y no atlas
    [Tooltip("Quando ligado, tile (0,0) representa a linha de cima do atlas.")]
    public bool atlasCoordinatesStartTopLeft = true;
    public List<BlockTextureMapping> blockTextures = new List<BlockTextureMapping>();
    [SerializeField, HideInInspector] private List<BlockTextureEntryIdMapping> blockTextureEntryIds = new List<BlockTextureEntryIdMapping>();

    [Header("Texturas por Estado")]
    [Tooltip("Overrides visuais por estado sem trocar o BlockType real. Use ElectricalPowered para blocos que mudam textura ao receber energia.")]
    public List<BlockStateTextureDefinition> stateTextureOverrides = new List<BlockStateTextureDefinition>();

    [Header("Multi Cubos")]
    [Tooltip("Modelos por blocos compostos por varios cuboides no espaco local do bloco. Coordenadas podem sair de 0..1 para suportar blocos maiores.")]
    public List<BlockMultiCuboidDefinition> multiCuboidShapes = new List<BlockMultiCuboidDefinition>();
    [SerializeField, HideInInspector] private List<BlockCuboidTextureEntryIdMapping> multiCuboidTextureEntryIds = new List<BlockCuboidTextureEntryIdMapping>();

    [Header("Blocos Dinamicos")]
    [Tooltip("Blocos que continuam no voxel data, mas sao renderizados por prefabs reais fora da malha estatica do chunk.")]
    public List<DynamicBlockPrefabDefinition> dynamicBlockPrefabs = new List<DynamicBlockPrefabDefinition>();

    [Header("Drops")]
    [Tooltip("Overrides de drop por bloco. Sem entrada aqui, o bloco dropa o item-bloco dele normalmente.")]
    public List<BlockDropDefinition> blockDrops = new List<BlockDropDefinition>();

    [System.NonSerialized]
    public BlockTextureMapping[] mappings;

    [System.NonSerialized]
    public BlockModelCuboid[] runtimeMultiCuboidBoxes = System.Array.Empty<BlockModelCuboid>();

    [System.NonSerialized]
    public Vector3Int runtimeMultiCuboidOverflowSearchRadius = Vector3Int.zero;

    [System.NonSerialized]
    private Dictionary<BlockType, DynamicBlockPrefabDefinition> dynamicBlockPrefabLookup;

    [System.NonSerialized]
    private Dictionary<BlockType, BlockDropDefinition> blockDropLookup;

    [System.NonSerialized]
    private bool[] runtimeDynamicVisualBlockCache = System.Array.Empty<bool>();

    [System.NonSerialized]
    public Vector3Int runtimeDynamicBlockOverflowSearchRadius = Vector3Int.zero;

    public static bool[] IsSolidCache;
    public static bool[] IsEmptyCache;
    private static readonly BlockFace[] SupportedTextureFaces =
    {
        BlockFace.Top,
        BlockFace.Bottom,
        BlockFace.Right,
        BlockFace.Left,
        BlockFace.Front,
        BlockFace.Back
    };

    private void OnEnable()
    {
        SyncDirectionalSideMappings();
        BuildBlockDropLookup();
    }

    private void OnValidate()
    {
        SyncDirectionalSideMappings();
        BuildBlockDropLookup();
    }

    public bool TryGetCustomDrop(BlockType blockType, out Item dropItem, out int amount)
    {
        dropItem = null;
        amount = 1;

        if (blockDropLookup == null)
            BuildBlockDropLookup();

        if (blockDropLookup == null ||
            !blockDropLookup.TryGetValue(blockType, out BlockDropDefinition definition) ||
            definition == null ||
            definition.dropItem == null)
        {
            return false;
        }

        dropItem = definition.dropItem;
        amount = Mathf.Max(1, definition.amount);
        return true;
    }

    private void BuildBlockDropLookup()
    {
        if (blockDropLookup == null)
            blockDropLookup = new Dictionary<BlockType, BlockDropDefinition>();
        else
            blockDropLookup.Clear();

        if (blockDrops == null)
            return;

        for (int i = 0; i < blockDrops.Count; i++)
        {
            BlockDropDefinition definition = blockDrops[i];
            if (definition == null ||
                definition.blockType == BlockType.Air ||
                definition.dropItem == null)
            {
                continue;
            }

            blockDropLookup[definition.blockType] = definition;
        }
    }

    public bool TryGetTextureEntryId(BlockType blockType, BlockFace face, out string entryId)
    {
        entryId = string.Empty;
        BlockTextureEntryIdMapping record = FindBlockTextureEntryIdMapping(blockType);
        return record != null &&
               record.entryIds != null &&
               record.entryIds.TryGet(face, out entryId);
    }

    public bool TryGetResolvedTextureEntryId(TextureAtlasGenerator generator, BlockType blockType, BlockFace face, out string entryId)
    {
        entryId = string.Empty;

        if (TryGetTextureEntryId(blockType, face, out string explicitEntryId) &&
            (generator == null || generator.TryGetUv(explicitEntryId, out _)))
        {
            entryId = explicitEntryId;
            return true;
        }

        if (BlockTextureEntryIdResolver.TryGetCanonicalEntryId(blockType, face, out string canonicalEntryId) &&
            (generator == null || generator.TryGetUv(canonicalEntryId, out _)))
        {
            entryId = canonicalEntryId;
            return true;
        }

        return false;
    }

    public bool SetTextureEntryId(BlockType blockType, BlockFace face, string entryId)
    {
        string sanitized = SanitizeTextureEntryId(entryId);
        BlockTextureEntryIdMapping record = FindOrCreateBlockTextureEntryIdMapping(blockType, createIfMissing: !string.IsNullOrEmpty(sanitized));
        if (record == null || record.entryIds == null)
            return false;

        bool changed = record.entryIds.Set(face, sanitized);
        CleanupTextureEntryIdMappings();
        return changed;
    }

    public bool TryGetCuboidTextureEntryId(BlockType blockType, int cuboidIndex, BlockFace face, out string entryId)
    {
        entryId = string.Empty;
        BlockCuboidTextureEntryIdMapping record = FindBlockCuboidTextureEntryIdMapping(blockType, cuboidIndex);
        return record != null &&
               record.entryIds != null &&
               record.entryIds.TryGet(face, out entryId);
    }

    public bool SetCuboidTextureEntryId(BlockType blockType, int cuboidIndex, BlockFace face, string entryId)
    {
        string sanitized = SanitizeTextureEntryId(entryId);
        BlockCuboidTextureEntryIdMapping record = FindOrCreateBlockCuboidTextureEntryIdMapping(
            blockType,
            cuboidIndex,
            createIfMissing: !string.IsNullOrEmpty(sanitized));
        if (record == null || record.entryIds == null)
            return false;

        bool changed = record.entryIds.Set(face, sanitized);
        CleanupCuboidTextureEntryIdMappings();
        return changed;
    }

    public bool ClearCuboidTextureEntryIds(BlockType blockType)
    {
        if (multiCuboidTextureEntryIds == null || multiCuboidTextureEntryIds.Count == 0)
            return false;

        bool changed = false;

        for (int i = multiCuboidTextureEntryIds.Count - 1; i >= 0; i--)
        {
            BlockCuboidTextureEntryIdMapping record = multiCuboidTextureEntryIds[i];
            if (record == null || record.blockType == blockType)
            {
                multiCuboidTextureEntryIds.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    [ContextMenu("Normalize Built-In Texture Entry IDs")]
    public void NormalizeBuiltInTextureEntryIdsContextMenu()
    {
        NormalizeBuiltInTextureEntryIds(null);
    }

    [ContextMenu("Clear Legacy Texture Tile Coords")]
    public void ClearLegacyTextureTileCoordsContextMenu()
    {
        ClearLegacyTextureTileCoords();
    }

    public bool NormalizeBuiltInTextureEntryIds(TextureAtlasGenerator generator)
    {
        if (blockTextures == null || blockTextures.Count == 0)
            return false;

        bool changed = false;
        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockType blockType = blockTextures[i].blockType;
            for (int f = 0; f < SupportedTextureFaces.Length; f++)
            {
                BlockFace face = SupportedTextureFaces[f];
                if (TryGetTextureEntryId(blockType, face, out string explicitEntryId) &&
                    (generator == null || generator.TryGetUv(explicitEntryId, out _)))
                {
                    continue;
                }

                if (!BlockTextureEntryIdResolver.TryGetCanonicalEntryId(blockType, face, out string entryId))
                    continue;

                if (generator != null && !generator.TryGetUv(entryId, out _))
                    continue;

                changed |= SetTextureEntryId(blockType, face, entryId);
            }
        }

#if UNITY_EDITOR
        if (changed && !Application.isPlaying)
            UnityEditor.EditorUtility.SetDirty(this);
#endif

        return changed;
    }

    public bool ClearLegacyTextureTileCoords()
    {
        bool changed = false;

        if (blockTextures != null)
        {
            for (int i = 0; i < blockTextures.Count; i++)
            {
                BlockTextureMapping mapping = blockTextures[i];
                if (!HasResolvedTextureEntryIdsForAllFaces(mapping.blockType))
                    continue;

                if (!mapping.TryClearLegacyTextureTileCoords())
                    continue;

                blockTextures[i] = mapping;
                changed = true;
            }
        }

        if (multiCuboidShapes != null)
        {
            for (int i = 0; i < multiCuboidShapes.Count; i++)
            {
                BlockMultiCuboidDefinition definition = multiCuboidShapes[i];
                if (definition == null || definition.cuboids == null)
                    continue;

                for (int c = 0; c < definition.cuboids.Count; c++)
                {
                    BlockModelCuboid cuboid = definition.cuboids[c];
                    if (!CanClearLegacyCuboidTextureTileCoords(definition.blockType, c, cuboid))
                        continue;

                    if (!TryClearLegacyTextureTileCoords(ref cuboid))
                        continue;

                    definition.cuboids[c] = cuboid;
                    changed = true;
                }
            }
        }

#if UNITY_EDITOR
        if (changed && !Application.isPlaying)
            UnityEditor.EditorUtility.SetDirty(this);
#endif

        return changed;
    }

    /// <summary>
    /// Inicializa o array de mapeamentos.
    /// </summary>
    public void InitializeDictionary()
    {
        SyncDirectionalSideMappings();

        int maxBlockTypeValue = GetMaxBlockTypeValue();
        int mappingCount = maxBlockTypeValue + 1;
        mappings = new BlockTextureMapping[mappingCount];

        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockTextures[i];
            int index = (int)mapping.blockType;
            if (index >= 0 && index < mappingCount)
                mappings[index] = mapping;
        }

        PopulateTorchFallbackMappings();
        PopulateWaterFallbackMappings();
        PopulateConveyorFallbackMappings();
        PopulateAutoMinerFallbackMapping();
        BuildMultiCuboidRuntimeData();
        BuildDynamicBlockRuntimeData();

    }

    private void BuildMultiCuboidRuntimeData()
    {
        if (mappings == null)
        {
            runtimeMultiCuboidBoxes = System.Array.Empty<BlockModelCuboid>();
            runtimeMultiCuboidOverflowSearchRadius = Vector3Int.zero;
            return;
        }

        for (int i = 0; i < mappings.Length; i++)
        {
            BlockTextureMapping mapping = mappings[i];
            mapping.multiCuboidStartIndex = 0;
            mapping.multiCuboidCount = 0;
            mappings[i] = mapping;
        }

        if (multiCuboidShapes == null || multiCuboidShapes.Count == 0)
        {
            runtimeMultiCuboidBoxes = System.Array.Empty<BlockModelCuboid>();
            runtimeMultiCuboidOverflowSearchRadius = Vector3Int.zero;
            return;
        }

        List<BlockModelCuboid> cuboids = new List<BlockModelCuboid>(multiCuboidShapes.Count * 3);
        for (int i = 0; i < multiCuboidShapes.Count; i++)
        {
            BlockMultiCuboidDefinition definition = multiCuboidShapes[i];
            if (definition == null || definition.cuboids == null)
                continue;

            int mappingIndex = (int)definition.blockType;
            if (mappingIndex < 0 || mappingIndex >= mappings.Length)
                continue;

            int startIndex = cuboids.Count;
            for (int c = 0; c < definition.cuboids.Count; c++)
            {
                if (!TrySanitizeModelCuboid(definition.cuboids[c], out BlockModelCuboid sanitized))
                    continue;

                cuboids.Add(sanitized);
            }

            int count = cuboids.Count - startIndex;
            if (count <= 0)
                continue;

            BlockTextureMapping mapping = mappings[mappingIndex];
            mapping.blockType = definition.blockType;
            mapping.renderShape = BlockRenderShape.MultiCuboid;
            mapping.multiCuboidStartIndex = startIndex;
            mapping.multiCuboidCount = count;
            mappings[mappingIndex] = mapping;
        }

        CopyConveyorMultiCuboidRuntimeDataToSlopedConveyor();

        runtimeMultiCuboidBoxes = cuboids.Count > 0
            ? cuboids.ToArray()
            : System.Array.Empty<BlockModelCuboid>();
        runtimeMultiCuboidOverflowSearchRadius = ComputeMultiCuboidOverflowSearchRadius();
    }

    private void CopyConveyorMultiCuboidRuntimeDataToSlopedConveyor()
    {
        int sourceIndex = (int)BlockType.ConveyorBelt;
        int targetIndex = (int)BlockType.conveyorBelt_45deg;
        if (mappings == null ||
            sourceIndex < 0 || sourceIndex >= mappings.Length ||
            targetIndex < 0 || targetIndex >= mappings.Length)
        {
            return;
        }

        BlockTextureMapping source = mappings[sourceIndex];
        if (source.blockType != BlockType.ConveyorBelt || source.multiCuboidCount <= 0)
            return;

        BlockTextureMapping target = mappings[targetIndex];
        if (target.blockType != BlockType.conveyorBelt_45deg)
            return;

        target.renderShape = BlockRenderShape.MultiCuboid;
        target.multiCuboidStartIndex = source.multiCuboidStartIndex;
        target.multiCuboidCount = source.multiCuboidCount;
        mappings[targetIndex] = target;
    }

    private Vector3Int ComputeMultiCuboidOverflowSearchRadius()
    {
        if (mappings == null || runtimeMultiCuboidBoxes == null || runtimeMultiCuboidBoxes.Length == 0)
            return Vector3Int.zero;

        int radiusX = 0;
        int radiusY = 0;
        int radiusZ = 0;

        for (int i = 0; i < mappings.Length; i++)
        {
            BlockTextureMapping mapping = mappings[i];
            if (BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.MultiCuboid)
                continue;

            AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.Y, ref radiusX, ref radiusY, ref radiusZ);
            if (!mapping.usePlacementAxisRotation)
                continue;

            AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.X, ref radiusX, ref radiusY, ref radiusZ);
            AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.XNegative, ref radiusX, ref radiusY, ref radiusZ);
            AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.Z, ref radiusX, ref radiusY, ref radiusZ);
            AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.ZNegative, ref radiusX, ref radiusY, ref radiusZ);
            if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
                AccumulateMultiCuboidOverflowSearchRadius(mapping, BlockPlacementAxis.YNegative, ref radiusX, ref radiusY, ref radiusZ);
        }

        return new Vector3Int(radiusX, radiusY, radiusZ);
    }

    private void AccumulateMultiCuboidOverflowSearchRadius(
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        ref int radiusX,
        ref int radiusY,
        ref int radiusZ)
    {
        if (!BlockShapeUtility.TryGetMultiCuboidBounds(
                Vector3Int.zero,
                mapping,
                runtimeMultiCuboidBoxes,
                placementAxis,
                mapping.blockType,
                out Bounds bounds))
        {
            return;
        }

        float negativeOverflowX = Mathf.Max(0f, -bounds.min.x);
        float negativeOverflowY = Mathf.Max(0f, -bounds.min.y);
        float negativeOverflowZ = Mathf.Max(0f, -bounds.min.z);
        float positiveOverflowX = Mathf.Max(0f, bounds.max.x - 1f);
        float positiveOverflowY = Mathf.Max(0f, bounds.max.y - 1f);
        float positiveOverflowZ = Mathf.Max(0f, bounds.max.z - 1f);

        radiusX = Mathf.Max(radiusX, Mathf.CeilToInt(Mathf.Max(negativeOverflowX, positiveOverflowX)));
        radiusY = Mathf.Max(radiusY, Mathf.CeilToInt(Mathf.Max(negativeOverflowY, positiveOverflowY)));
        radiusZ = Mathf.Max(radiusZ, Mathf.CeilToInt(Mathf.Max(negativeOverflowZ, positiveOverflowZ)));
    }

    private static bool TrySanitizeModelCuboid(BlockModelCuboid source, out BlockModelCuboid sanitized)
    {
        Vector3 min = Vector3.Min(source.min, source.max);
        Vector3 max = Vector3.Max(source.min, source.max);

        bool valid =
            max.x > min.x + 0.0001f &&
            max.y > min.y + 0.0001f &&
            max.z > min.z + 0.0001f;

        if (!valid)
        {
            sanitized = default;
            return false;
        }

        sanitized = new BlockModelCuboid
        {
            min = min,
            max = max,
            eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(source.eulerRotation),
            faces = source.EffectiveFaces,
            textureOverrideFaces = source.EffectiveTextureOverrideFaces,
            textureTop = source.textureTop,
            textureBottom = source.textureBottom,
            textureRight = source.textureRight,
            textureLeft = source.textureLeft,
            textureFront = source.textureFront,
            textureBack = source.textureBack
        };
        sanitized.CopyUvRectOverrideDataFrom(source);
        return true;
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(
            Mathf.Clamp01(value.x),
            Mathf.Clamp01(value.y),
            Mathf.Clamp01(value.z));
    }

    private void BuildDynamicBlockRuntimeData()
    {
        dynamicBlockPrefabLookup = new Dictionary<BlockType, DynamicBlockPrefabDefinition>();
        int mappingCount = mappings != null ? mappings.Length : 0;
        runtimeDynamicVisualBlockCache = mappingCount > 0
            ? new bool[mappingCount]
            : System.Array.Empty<bool>();
        runtimeDynamicBlockOverflowSearchRadius = Vector3Int.zero;

        if (mappings == null || mappings.Length == 0)
            return;

        if (dynamicBlockPrefabs != null)
        {
            for (int i = 0; i < dynamicBlockPrefabs.Count; i++)
            {
                DynamicBlockPrefabDefinition definition = dynamicBlockPrefabs[i];
                if (definition == null || definition.prefab == null)
                    continue;

                dynamicBlockPrefabLookup[definition.blockType] = definition;

                int mappingIndex = (int)definition.blockType;
                if (mappingIndex < 0 || mappingIndex >= mappings.Length)
                    continue;

                BlockTextureMapping mapping = mappings[mappingIndex];
                mapping.blockType = definition.blockType;
                mapping.renderAsDynamicPrefab = true;
                mapping.dynamicOccupiedHorizontalBlocks = Mathf.Max(1, definition.occupiedHorizontalBlocks);
                mapping.dynamicOccupiedVerticalBlocks = Mathf.Max(1, definition.occupiedVerticalBlocks);
                if (definition.rotateWithPlacementAxis)
                {
                    mapping.usePlacementAxisRotation = true;
                    mapping.placementRotationAxes = definition.placementRotationAxes;
                }

                mapping.isSolid = definition.isSolid;
                // Blocos dinamicos usam prefab visual; solidez aqui e fisica/suporte, nao culling de faces vizinhas.
                mapping.isTransparent = true;
                mapping.isLiquid = definition.isLiquid;
                mapping.isLightSource = definition.isLightSource || definition.lightEmission > 0;
                mapping.breaksWithoutSupport = definition.breaksWithoutSupport;
                mapping.lightOpacity = definition.lightOpacity;
                mapping.lightEmission = definition.lightEmission;
                mapping.lightColor = definition.lightColor;

                runtimeDynamicBlockOverflowSearchRadius = new Vector3Int(
                    Mathf.Max(runtimeDynamicBlockOverflowSearchRadius.x, mapping.dynamicOccupiedHorizontalBlocks - 1),
                    Mathf.Max(runtimeDynamicBlockOverflowSearchRadius.y, mapping.dynamicOccupiedVerticalBlocks - 1),
                    Mathf.Max(runtimeDynamicBlockOverflowSearchRadius.z, mapping.dynamicOccupiedHorizontalBlocks - 1));

                mappings[mappingIndex] = mapping;
            }
        }

        for (int i = 0; i < mappings.Length; i++)
            runtimeDynamicVisualBlockCache[i] = mappings[i].renderAsDynamicPrefab;
    }

    public bool IsDynamicVisualBlock(BlockType blockType)
    {
        if (mappings == null || mappings.Length == 0 || runtimeDynamicVisualBlockCache == null)
            InitializeDictionary();

        int index = (int)blockType;
        return runtimeDynamicVisualBlockCache != null &&
               index >= 0 &&
               index < runtimeDynamicVisualBlockCache.Length &&
               runtimeDynamicVisualBlockCache[index];
    }

    public bool TryGetDynamicBlockPrefabDefinition(BlockType blockType, out DynamicBlockPrefabDefinition definition)
    {
        definition = null;

        if (mappings == null || mappings.Length == 0 || dynamicBlockPrefabLookup == null)
            InitializeDictionary();

        return dynamicBlockPrefabLookup != null &&
               dynamicBlockPrefabLookup.TryGetValue(blockType, out definition) &&
               definition != null &&
               definition.prefab != null;
    }

    public bool TryGetDynamicBlockOccupancy(BlockType blockType, out int horizontalBlocks, out int verticalBlocks)
    {
        horizontalBlocks = 1;
        verticalBlocks = 1;

        if (!TryGetDynamicBlockPrefabDefinition(blockType, out DynamicBlockPrefabDefinition definition))
            return false;

        horizontalBlocks = Mathf.Max(1, definition.occupiedHorizontalBlocks);
        verticalBlocks = Mathf.Max(1, definition.occupiedVerticalBlocks);
        return true;
    }

    private BlockTextureEntryIdMapping FindBlockTextureEntryIdMapping(BlockType blockType)
    {
        if (blockTextureEntryIds == null)
            return null;

        for (int i = 0; i < blockTextureEntryIds.Count; i++)
        {
            BlockTextureEntryIdMapping record = blockTextureEntryIds[i];
            if (record != null && record.blockType == blockType)
                return record;
        }

        return null;
    }

    private BlockTextureEntryIdMapping FindOrCreateBlockTextureEntryIdMapping(BlockType blockType, bool createIfMissing)
    {
        BlockTextureEntryIdMapping record = FindBlockTextureEntryIdMapping(blockType);
        if (record != null || !createIfMissing)
            return record;

        if (blockTextureEntryIds == null)
            blockTextureEntryIds = new List<BlockTextureEntryIdMapping>();

        record = new BlockTextureEntryIdMapping { blockType = blockType };
        blockTextureEntryIds.Add(record);
        return record;
    }

    private BlockCuboidTextureEntryIdMapping FindBlockCuboidTextureEntryIdMapping(BlockType blockType, int cuboidIndex)
    {
        if (multiCuboidTextureEntryIds == null)
            return null;

        for (int i = 0; i < multiCuboidTextureEntryIds.Count; i++)
        {
            BlockCuboidTextureEntryIdMapping record = multiCuboidTextureEntryIds[i];
            if (record != null && record.blockType == blockType && record.cuboidIndex == cuboidIndex)
                return record;
        }

        return null;
    }

    private BlockCuboidTextureEntryIdMapping FindOrCreateBlockCuboidTextureEntryIdMapping(BlockType blockType, int cuboidIndex, bool createIfMissing)
    {
        BlockCuboidTextureEntryIdMapping record = FindBlockCuboidTextureEntryIdMapping(blockType, cuboidIndex);
        if (record != null || !createIfMissing)
            return record;

        if (multiCuboidTextureEntryIds == null)
            multiCuboidTextureEntryIds = new List<BlockCuboidTextureEntryIdMapping>();

        record = new BlockCuboidTextureEntryIdMapping
        {
            blockType = blockType,
            cuboidIndex = cuboidIndex
        };
        multiCuboidTextureEntryIds.Add(record);
        return record;
    }

    private void CleanupTextureEntryIdMappings()
    {
        if (blockTextureEntryIds == null)
            return;

        for (int i = blockTextureEntryIds.Count - 1; i >= 0; i--)
        {
            BlockTextureEntryIdMapping record = blockTextureEntryIds[i];
            if (record == null || record.entryIds == null || !record.entryIds.HasAny())
                blockTextureEntryIds.RemoveAt(i);
        }
    }

    private void CleanupCuboidTextureEntryIdMappings()
    {
        if (multiCuboidTextureEntryIds == null)
            return;

        for (int i = multiCuboidTextureEntryIds.Count - 1; i >= 0; i--)
        {
            BlockCuboidTextureEntryIdMapping record = multiCuboidTextureEntryIds[i];
            if (record == null || record.entryIds == null || !record.entryIds.HasAny())
                multiCuboidTextureEntryIds.RemoveAt(i);
        }
    }

    private static string SanitizeTextureEntryId(string entryId)
    {
        return string.IsNullOrWhiteSpace(entryId) ? string.Empty : entryId.Trim();
    }

    private static int GetMaxBlockTypeValue()
    {
        int maxValue = 0;
        System.Array values = System.Enum.GetValues(typeof(BlockType));
        for (int i = 0; i < values.Length; i++)
        {
            int value = (int)values.GetValue(i);
            if (value > maxValue)
                maxValue = value;
        }

        return maxValue;
    }

    /// <summary>
    /// Retorna o mapping para o tipo de bloco; se nao existir, retorna null.
    /// </summary>
    public BlockTextureMapping? GetMapping(BlockType type)
    {
        if (mappings == null || mappings.Length == 0)
            InitializeDictionary();

        int index = (int)type;
        if (index >= 0 && index < mappings.Length)
            return mappings[index];

        return null;
    }

    private void PopulateTorchFallbackMappings()
    {
        if (!TryGetTorchTemplateMapping(out BlockTextureMapping template))
            return;

        EnsureFallbackMapping(BlockType.torch, template);
        EnsureFallbackMapping(BlockType.WallTorchEast, template);
        EnsureFallbackMapping(BlockType.WallTorchWest, template);
        EnsureFallbackMapping(BlockType.WallTorchSouth, template);
        EnsureFallbackMapping(BlockType.WallTorchNorth, template);
    }

    private void NormalizeTorchRuntimeMapping(
        BlockType blockType,
        Vector4 topUvRectData,
        Vector4 bottomUvRectData,
        Vector4 sideUvRectData)
    {
        int index = (int)blockType;
        if (index < 0 || index >= mappings.Length)
            return;

        BlockTextureMapping mapping = mappings[index];
        mapping.blockType = blockType;
        mapping.renderShape = BlockRenderShape.Cuboid;
        mapping.isFlat = false;
        mapping.shapeMin = new Vector3(7f / 16f, 0f, 7f / 16f);
        mapping.shapeMax = new Vector3(9f / 16f, 10f / 16f, 9f / 16f);
        mapping.multiCuboidStartIndex = 0;
        mapping.multiCuboidCount = 0;
        mapping.SetUvRectData(BlockFace.Top, topUvRectData);
        mapping.SetUvRectData(BlockFace.Bottom, bottomUvRectData);
        mapping.SetUvRectData(BlockFace.Right, sideUvRectData);
        mapping.SetUvRectData(BlockFace.Left, sideUvRectData);
        mapping.SetUvRectData(BlockFace.Front, sideUvRectData);
        mapping.SetUvRectData(BlockFace.Back, sideUvRectData);
        mappings[index] = mapping;
    }

    private static Rect GetTorchSubRect(
        Rect baseUvRect,
        float minXNormalized,
        float minYNormalized,
        float widthNormalized,
        float heightNormalized)
    {
        float xMin = baseUvRect.xMin + baseUvRect.width * minXNormalized;
        float yMin = baseUvRect.yMin + baseUvRect.height * minYNormalized;
        float xMax = xMin + baseUvRect.width * widthNormalized;
        float yMax = yMin + baseUvRect.height * heightNormalized;
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private void PopulateWaterFallbackMappings()
    {
        if (!TryGetExplicitMapping(BlockType.Water, out BlockTextureMapping template))
            return;

        EnsureFallbackMapping(BlockType.WaterFlow1, template);
        EnsureFallbackMapping(BlockType.WaterFlow2, template);
        EnsureFallbackMapping(BlockType.WaterFlow3, template);
        EnsureFallbackMapping(BlockType.WaterFlow4, template);
        EnsureFallbackMapping(BlockType.WaterFlow5, template);
        EnsureFallbackMapping(BlockType.WaterFlow6, template);
        EnsureFallbackMapping(BlockType.WaterFlow7, template);
        EnsureFallbackMapping(BlockType.WaterFall0, template);
        EnsureFallbackMapping(BlockType.WaterFall1, template);
        EnsureFallbackMapping(BlockType.WaterFall2, template);
        EnsureFallbackMapping(BlockType.WaterFall3, template);
        EnsureFallbackMapping(BlockType.WaterFall4, template);
        EnsureFallbackMapping(BlockType.WaterFall5, template);
        EnsureFallbackMapping(BlockType.WaterFall6, template);
        EnsureFallbackMapping(BlockType.WaterFall7, template);
    }

    private void PopulateConveyorFallbackMappings()
    {
        if (!TryGetExplicitMapping(BlockType.ConveyorBelt, out BlockTextureMapping template))
            return;

        EnsureFallbackMapping(BlockType.conveyorBelt_45deg, template);
        NormalizeSlopedConveyorRuntimeMapping();
    }

    private void PopulateAutoMinerFallbackMapping()
    {
        if (TryGetExplicitMapping(BlockType.AutoMiner, out _))
            return;

        if (!TryGetExplicitMapping(BlockType.StoneFurnance, out BlockTextureMapping template) &&
            !TryGetExplicitMapping(BlockType.Treecutter, out template))
        {
            return;
        }

        int index = (int)BlockType.AutoMiner;
        if (index < 0 || index >= mappings.Length)
            return;

        template.blockType = BlockType.AutoMiner;
        template.renderShape = BlockRenderShape.Cube;
        template.renderAsDynamicPrefab = false;
        template.isFlat = false;
        template.shapeMin = Vector3.zero;
        template.shapeMax = Vector3.zero;
        template.multiCuboidStartIndex = 0;
        template.multiCuboidCount = 0;
        template.isEmpty = false;
        template.isSolid = true;
        template.isTransparent = false;
        template.isLiquid = false;
        template.breaksWithoutSupport = false;
        template.usePlacementAxisRotation = true;
        template.placementRotationAxes = BlockPlacementRotationAxes.Horizontal;
        template.lightOpacity = 15;
        template.isElectricalEndpoint = true;
        template.poweredElectricalEnergyPerSecond = 0f;
        mappings[index] = template;
    }

    private void NormalizeSlopedConveyorRuntimeMapping()
    {
        int index = (int)BlockType.conveyorBelt_45deg;
        if (index < 0 || index >= mappings.Length)
            return;

        BlockTextureMapping mapping = mappings[index];
        mapping.blockType = BlockType.conveyorBelt_45deg;
        mapping.renderShape = BlockRenderShape.MultiCuboid;
        mapping.renderAsDynamicPrefab = false;
        mapping.isFlat = false;
        mapping.shapeMin = Vector3.zero;
        mapping.shapeMax = Vector3.one;
        mapping.multiCuboidStartIndex = 0;
        mapping.multiCuboidCount = 0;
        mapping.usePlacementAxisRotation = true;
        mapping.placementRotationAxes = BlockPlacementRotationAxes.Horizontal;
        mappings[index] = mapping;
    }

    private bool TryGetTorchTemplateMapping(out BlockTextureMapping template)
    {
        if (TryGetExplicitMapping(BlockType.torch, out template))
            return true;

        return TryGetExplicitMapping(BlockType.glowstone, out template);
    }

    private bool TryGetExplicitMapping(BlockType type, out BlockTextureMapping mapping)
    {
        mapping = default;
        if (mappings == null || mappings.Length == 0)
            return false;

        int index = (int)type;
        if (index < 0 || index >= mappings.Length)
            return false;

        BlockTextureMapping candidate = mappings[index];
        if (candidate.blockType != type)
            return false;

        mapping = candidate;
        return true;
    }

    private void EnsureFallbackMapping(BlockType type, BlockTextureMapping template)
    {
        int index = (int)type;
        if (index < 0 || index >= mappings.Length)
            return;

        if (mappings[index].blockType == type)
            return;

        template.blockType = type;
        mappings[index] = template;
    }

    /// <summary>
    /// Retorna as coordenadas do tile para a face desejada.
    /// </summary>
    public Vector2Int GetTileCoord(BlockType type, BlockFace face)
    {
        BlockTextureMapping? mapping = GetMapping(type);
        if (mapping == null)
            return new Vector2Int(0, 0);

        return mapping.Value.GetTileCoord(face);
    }

    /// <summary>
    /// Retorna se o bloco foi marcado como liquido no mapeamento.
    /// Mantem compatibilidade para agua mesmo sem mapeamento.
    /// </summary>
    public bool IsLiquid(BlockType type)
    {
        if (FluidBlockUtility.IsWater(type))
            return true;

        BlockTextureMapping? mapping = GetMapping(type);
        return mapping != null && mapping.Value.isLiquid;
    }

    private void SyncDirectionalSideMappings()
    {
        if (blockTextures == null)
            return;

        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockTextures[i];
            if (!mapping.EnsureDirectionalSideData())
                continue;

            blockTextures[i] = mapping;
        }
    }

    private bool HasResolvedTextureEntryIdsForAllFaces(BlockType blockType)
    {
        for (int i = 0; i < SupportedTextureFaces.Length; i++)
        {
            if (!TryGetResolvedTextureEntryId(null, blockType, SupportedTextureFaces[i], out _))
                return false;
        }

        return true;
    }

    private bool CanClearLegacyCuboidTextureTileCoords(BlockType blockType, int cuboidIndex, BlockModelCuboid cuboid)
    {
        if (cuboid.EffectiveTextureOverrideFaces == BlockCuboidFaceMask.None)
            return HasAnyLegacyTextureTileCoords(cuboid);

        for (int i = 0; i < SupportedTextureFaces.Length; i++)
        {
            BlockFace face = SupportedTextureFaces[i];
            if (!cuboid.HasTextureOverride(face))
                continue;

            if (!TryGetCuboidTextureEntryId(blockType, cuboidIndex, face, out _))
                return false;
        }

        return true;
    }

    private static bool HasAnyLegacyTextureTileCoords(BlockModelCuboid cuboid)
    {
        return cuboid.textureTop != Vector2Int.zero ||
               cuboid.textureBottom != Vector2Int.zero ||
               cuboid.textureRight != Vector2Int.zero ||
               cuboid.textureLeft != Vector2Int.zero ||
               cuboid.textureFront != Vector2Int.zero ||
               cuboid.textureBack != Vector2Int.zero;
    }

    private static bool TryClearLegacyTextureTileCoords(ref BlockModelCuboid cuboid)
    {
        if (!HasAnyLegacyTextureTileCoords(cuboid))
            return false;

        cuboid.textureTop = Vector2Int.zero;
        cuboid.textureBottom = Vector2Int.zero;
        cuboid.textureRight = Vector2Int.zero;
        cuboid.textureLeft = Vector2Int.zero;
        cuboid.textureFront = Vector2Int.zero;
        cuboid.textureBack = Vector2Int.zero;
        return true;
    }
}

[System.Serializable]
public struct BlockTextureMapping
{
    public BlockType blockType;
    [HideInInspector] public Vector2Int top;    // legado: coordenada no atlas para a face de cima (tileX, tileY)
    [HideInInspector] public Vector2Int bottom; // legado: coordenada no atlas para a face de baixo
    [HideInInspector] public Vector2Int right;  // legado: face +X
    [HideInInspector] public Vector2Int left;   // legado: face -X
    [HideInInspector] public Vector2Int front;  // legado: face +Z
    [HideInInspector] public Vector2Int back;   // legado: face -Z

    [HideInInspector] public Vector2Int side; // legado: usado para migrar assets antigos
    [SerializeField, HideInInspector] private bool directionalSideDataInitialized;
    [SerializeField, HideInInspector] private Vector4 topUvRect;
    [SerializeField, HideInInspector] private Vector4 bottomUvRect;
    [SerializeField, HideInInspector] private Vector4 rightUvRect;
    [SerializeField, HideInInspector] private Vector4 leftUvRect;
    [SerializeField, HideInInspector] private Vector4 frontUvRect;
    [SerializeField, HideInInspector] private Vector4 backUvRect;
    [SerializeField, HideInInspector] private bool runtimeUvRectDataInitialized;

    [Header("Rendering")]
    [Tooltip("Cube = voxel normal, Cross = duas quads cruzadas para plantas, Cuboid = caixa menor dentro do voxel (bom para tochas/postes), Plane = quad dupla face (redstone/quadros/vinhas).")]
    public BlockRenderShape renderShape;
    [Tooltip("Quando ativo, este bloco fica no voxel data, mas o chunk mesher nao gera geometria estatica para ele. Use a lista Blocos Dinamicos do BlockDataSO para apontar o prefab.")]
    public bool renderAsDynamicPrefab;
    [Tooltip("Quando ativo, o bloco usa malha plana (quad dupla face), similar a redstone/quadro/vinhas.")]
    public bool isFlat;
    [Tooltip("Canto minimo local do formato dentro do voxel (0..1). Usado em Cross, Cuboid e Plane.")]
    public Vector3 shapeMin;
    [Tooltip("Canto maximo local do formato dentro do voxel (0..1). Usado em Cross, Cuboid e Plane.")]
    public Vector3 shapeMax;
    [HideInInspector] public int multiCuboidStartIndex;
    [HideInInspector] public int multiCuboidCount;

    [Header("Behavior (use to control face culling / water handling)")]
    public bool isEmpty;       // ex: true para agua/ar
    public bool isSolid;       // defina como true no Inspector para blocos solidos
    public bool isTransparent; // ex: true para vidro, folhas
    public bool isLiquid;      // true para agua e outros blocos liquidos
    public bool isLightSource; // ex: blocos que emitem luz, como tochas
    [Tooltip("Quando ativo, este bloco quebra se ele e o grupo conectado dele nao estiverem encostando em nenhum bloco solido estavel.")]
    public bool breaksWithoutSupport;
    public int materialIndex;  // default: 0

    [Header("Breaking")]
    [Tooltip("Dureza estilo Minecraft. 0 usa o valor padrao do BlockType; -1 torna o bloco inquebravel no Survival.")]
    [Min(-1f)] public float minecraftHardness;
    [Tooltip("Ajuste legado opcional aplicado depois da formula Minecraft. 0 ignora.")]
    [Min(0f)] public float breakTimeMultiplier;
    public ToolType preferredTool;

    [Header("Placement Rotation")]
    [Tooltip("Quando ativo, o bloco gira o eixo de exibicao ao ser colocado (estilo tronco do Minecraft).")]
    public bool usePlacementAxisRotation;
    [Tooltip("Vertical = eixo Y, Horizontal = eixo X/Z, Both = permite os dois.")]
    public BlockPlacementRotationAxes placementRotationAxes;

    public byte lightOpacity;  // 0..15 (0 = nao reduz, 15 = bloqueia)
    public byte lightEmission; // 0..15 (Glowstone = 15, Torch = 14)
    [Tooltip("Cor RGB da luz emitida. Preto mantem compatibilidade e emite branco quando Light Emission > 0.")]
    public Color lightColor;

    [Header("Electricity")]
    [Tooltip("Quando ativo, o bloco pode participar da rede eletrica como endpoint conectavel.")]
    public bool isElectricalEndpoint;
    [Tooltip("Consumo continuo por segundo enquanto o bloco permanecer energizado. 0 = nao consome energia continuamente.")]
    [Min(0f)] public float poweredElectricalEnergyPerSecond;
    [Tooltip("Luz emitida enquanto o bloco estiver energizado pela rede eletrica.")]
    [Range(0, 15)] public byte poweredLightEmission;
    [Tooltip("Cor da luz emitida enquanto o bloco estiver energizado. Preto mantem compatibilidade e emite branco quando Powered Light Emission > 0.")]
    public Color poweredLightColor;

    [HideInInspector] public int dynamicOccupiedHorizontalBlocks;
    [HideInInspector] public int dynamicOccupiedVerticalBlocks;

    [Header("Biome Tinting")]
    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintTop;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintBottom;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintRight;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintLeft;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintFront;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintBack;

    [HideInInspector] public bool tintSide; // legado: usado para migrar assets antigos

    public bool EnsureDirectionalSideData()
    {
        if (directionalSideDataInitialized)
            return false;

        right = side;
        left = side;
        front = side;
        back = side;
        tintRight = tintSide;
        tintLeft = tintSide;
        tintFront = tintSide;
        tintBack = tintSide;
        directionalSideDataInitialized = true;
        return true;
    }

    public Vector2Int GetTileCoord(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top:
                return top;
            case BlockFace.Bottom:
                return bottom;
            case BlockFace.Right:
                return right;
            case BlockFace.Left:
                return left;
            case BlockFace.Front:
                return front;
            case BlockFace.Back:
                return back;
            default:
                return side;
        }
    }

    public bool GetTint(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top:
                return tintTop;
            case BlockFace.Bottom:
                return tintBottom;
            case BlockFace.Right:
                return tintRight;
            case BlockFace.Left:
                return tintLeft;
            case BlockFace.Front:
                return tintFront;
            case BlockFace.Back:
                return tintBack;
            default:
                return tintSide;
        }
    }

    public void SetUvRectData(BlockFace face, Vector4 uvRectData)
    {
        switch (face)
        {
            case BlockFace.Top:
                topUvRect = uvRectData;
                break;
            case BlockFace.Bottom:
                bottomUvRect = uvRectData;
                break;
            case BlockFace.Right:
                rightUvRect = uvRectData;
                break;
            case BlockFace.Left:
                leftUvRect = uvRectData;
                break;
            case BlockFace.Front:
                frontUvRect = uvRectData;
                break;
            case BlockFace.Back:
                backUvRect = uvRectData;
                break;
            default:
                return;
        }

        runtimeUvRectDataInitialized = true;
    }

    public bool TryGetUvRectData(BlockFace face, out Vector4 uvRectData)
    {
        uvRectData = default;
        if (!runtimeUvRectDataInitialized)
            return false;

        switch (face)
        {
            case BlockFace.Top:
                uvRectData = topUvRect;
                break;
            case BlockFace.Bottom:
                uvRectData = bottomUvRect;
                break;
            case BlockFace.Right:
                uvRectData = rightUvRect;
                break;
            case BlockFace.Left:
                uvRectData = leftUvRect;
                break;
            case BlockFace.Front:
                uvRectData = frontUvRect;
                break;
            case BlockFace.Back:
                uvRectData = backUvRect;
                break;
            default:
                return false;
        }

        return BlockAtlasUvUtility.IsValidUvRectData(uvRectData);
    }

    public void CopyUvRectDataFrom(BlockTextureMapping source)
    {
        topUvRect = source.topUvRect;
        bottomUvRect = source.bottomUvRect;
        rightUvRect = source.rightUvRect;
        leftUvRect = source.leftUvRect;
        frontUvRect = source.frontUvRect;
        backUvRect = source.backUvRect;
        runtimeUvRectDataInitialized = source.runtimeUvRectDataInitialized;
    }

    public bool TryClearLegacyTextureTileCoords()
    {
        if (top == Vector2Int.zero &&
            bottom == Vector2Int.zero &&
            right == Vector2Int.zero &&
            left == Vector2Int.zero &&
            front == Vector2Int.zero &&
            back == Vector2Int.zero &&
            side == Vector2Int.zero)
        {
            return false;
        }

        top = Vector2Int.zero;
        bottom = Vector2Int.zero;
        right = Vector2Int.zero;
        left = Vector2Int.zero;
        front = Vector2Int.zero;
        back = Vector2Int.zero;
        side = Vector2Int.zero;
        return true;
    }
}

public static class BlockAtlasUvUtility
{
    public static bool IsValidUvRectData(Vector4 uvRectData)
    {
        return uvRectData.z > 0f && uvRectData.w > 0f;
    }

    public static Vector4 RectToUvRectData(Rect uvRect)
    {
        return new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height);
    }

    public static Rect UvRectDataToRect(Vector4 uvRectData)
    {
        return new Rect(uvRectData.x, uvRectData.y, uvRectData.z, uvRectData.w);
    }

    public static Vector4 BuildLegacyUvRectData(Vector2Int tile, Vector2Int atlasTiles, bool atlasOriginTopLeft)
    {
        int safeTilesX = Mathf.Max(1, atlasTiles.x);
        int safeTilesY = Mathf.Max(1, atlasTiles.y);
        int tileX = Mathf.Clamp(tile.x, 0, safeTilesX - 1);
        int tileY = Mathf.Clamp(tile.y, 0, safeTilesY - 1);

        float tileWidth = 1f / safeTilesX;
        float tileHeight = 1f / safeTilesY;
        float originX = tileX * tileWidth;
        float originY = atlasOriginTopLeft
            ? 1f - (tileY + 1) * tileHeight
            : tileY * tileHeight;

        return new Vector4(originX, originY, tileWidth, tileHeight);
    }

    public static Vector4 ResolveUvRectData(
        BlockTextureMapping mapping,
        BlockFace face,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft)
    {
        if (mapping.TryGetUvRectData(face, out Vector4 uvRectData))
            return uvRectData;

        return default;
    }

    public static Vector4 ResolveUvRectData(
        BlockModelCuboid cuboid,
        BlockFace face,
        BlockTextureMapping fallbackMapping,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft)
    {
        if (cuboid.TryGetUvRectData(face, fallbackMapping, out Vector4 uvRectData))
            return uvRectData;

        return default;
    }

    public static Rect ResolveUvRect(
        BlockTextureMapping mapping,
        BlockFace face,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft)
    {
        return UvRectDataToRect(ResolveUvRectData(mapping, face, atlasTiles, atlasOriginTopLeft));
    }
}

public static class BlockPlacementRotationUtility
{
    public static BlockPlacementAxis SanitizeAxis(BlockPlacementAxis axis)
    {
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.X,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.Z,
            BlockPlacementAxis.YNegative => BlockPlacementAxis.Y,
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockPlacementAxis SanitizeAxis(byte axis)
    {
        return SanitizeAxis((BlockPlacementAxis)axis);
    }

    public static byte SanitizeAxisByte(byte axis)
    {
        return (byte)SanitizeAxis(axis);
    }

    public static BlockPlacementAxis SanitizeStoredAxis(BlockPlacementAxis axis)
    {
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.XNegative,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.ZNegative,
            BlockPlacementAxis.YNegative => BlockPlacementAxis.YNegative,
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockPlacementAxis SanitizeStoredAxis(byte axis)
    {
        return SanitizeStoredAxis((BlockPlacementAxis)axis);
    }

    public static byte SanitizeStoredAxisByte(byte axis)
    {
        return (byte)SanitizeStoredAxis(axis);
    }

    public static BlockPlacementAxis ResolvePlacementAxis(
        BlockTextureMapping mapping,
        Vector3Int hitNormal,
        Vector3 lookForward)
    {
        return ResolvePlacementAxis(mapping, hitNormal, lookForward, Vector3.zero);
    }

    public static BlockPlacementAxis ResolvePlacementAxis(
        BlockTextureMapping mapping,
        Vector3Int hitNormal,
        Vector3 lookForward,
        Vector3 hitPoint)
    {
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        if (shape == BlockRenderShape.Stairs)
            return StairPlacementUtility.ResolvePlacementCode(hitNormal, lookForward, hitPoint);

        if (shape == BlockRenderShape.Slab)
            return SlabShapeUtility.ResolvePlacementCode(hitNormal, hitPoint);

        if (shape == BlockRenderShape.Ramp)
        {
            if (mapping.usePlacementAxisRotation &&
                mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
            {
                return RampShapeUtility.ResolvePlacementCode(hitNormal, lookForward, hitPoint);
            }

            return RampShapeUtility.ResolvePlacementAxis(lookForward);
        }

        if (shape == BlockRenderShape.VerticalRamp)
            return VerticalRampShapeUtility.ResolvePlacementAxis(lookForward);

        if (!mapping.usePlacementAxisRotation)
            return BlockPlacementAxis.Y;

        if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
            (shape == BlockRenderShape.Cube || shape == BlockRenderShape.MultiCuboid))
        {
            // Furnace/chest-style blocks should face the player, not look away.
            return ResolveHorizontalAxisFacingPlayer(lookForward);
        }

        if (BlockShapeUtility.IsFlatShape(mapping) &&
            mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
        {
            return ResolveFlatPlanePlacementAxis(hitNormal, lookForward);
        }

        return ResolvePlacementAxis(mapping.placementRotationAxes, hitNormal, lookForward);
    }

    public static BlockPlacementAxis ResolvePlacementAxis(
        BlockPlacementRotationAxes allowedAxes,
        Vector3Int hitNormal,
        Vector3 lookForward)
    {
        return allowedAxes switch
        {
            BlockPlacementRotationAxes.Vertical => BlockPlacementAxis.Y,
            BlockPlacementRotationAxes.Horizontal => ResolveHorizontalAxisFromLookForward(lookForward),
            BlockPlacementRotationAxes.Both => ResolveAxisFromHitNormalOrFallback(hitNormal, lookForward),
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockFace ResolveFaceForPlacement(BlockTextureMapping mapping, BlockFace worldFace, BlockPlacementAxis axis)
    {
        if (!mapping.usePlacementAxisRotation)
            return worldFace;

        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
            (shape == BlockRenderShape.Cube || shape == BlockRenderShape.MultiCuboid))
        {
            return RemapHorizontalFacingFace(worldFace, axis);
        }

        return RemapFace(worldFace, axis);
    }

    public static BlockFace RemapFace(BlockFace worldFace, BlockPlacementAxis axis)
    {
        axis = SanitizeStoredAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.YNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Left,
                BlockFace.Left => BlockFace.Right,
                BlockFace.Top => BlockFace.Bottom,
                BlockFace.Bottom => BlockFace.Top,
                BlockFace.Front => BlockFace.Front,
                BlockFace.Back => BlockFace.Back,
                _ => worldFace
            },

            BlockPlacementAxis.XNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Top,
                BlockFace.Left => BlockFace.Bottom,
                BlockFace.Top => BlockFace.Left,
                BlockFace.Bottom => BlockFace.Right,
                BlockFace.Front => BlockFace.Front,
                BlockFace.Back => BlockFace.Back,
                _ => worldFace
            },

            BlockPlacementAxis.X => worldFace switch
            {
                BlockFace.Right => BlockFace.Bottom,
                BlockFace.Left => BlockFace.Top,
                BlockFace.Top => BlockFace.Right,
                BlockFace.Bottom => BlockFace.Left,
                BlockFace.Front => BlockFace.Front,
                BlockFace.Back => BlockFace.Back,
                _ => worldFace
            },

            BlockPlacementAxis.ZNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Right,
                BlockFace.Left => BlockFace.Left,
                BlockFace.Top => BlockFace.Back,
                BlockFace.Bottom => BlockFace.Front,
                BlockFace.Front => BlockFace.Top,
                BlockFace.Back => BlockFace.Bottom,
                _ => worldFace
            },

            BlockPlacementAxis.Z => worldFace switch
            {
                BlockFace.Right => BlockFace.Right,
                BlockFace.Left => BlockFace.Left,
                BlockFace.Top => BlockFace.Front,
                BlockFace.Bottom => BlockFace.Back,
                BlockFace.Front => BlockFace.Bottom,
                BlockFace.Back => BlockFace.Top,
                _ => worldFace
            },

            _ => worldFace
        };
    }

    private static BlockFace RemapHorizontalFacingFace(BlockFace worldFace, BlockPlacementAxis axis)
    {
        axis = SanitizeStoredAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => worldFace switch
            {
                BlockFace.Right => BlockFace.Front,
                BlockFace.Left => BlockFace.Back,
                BlockFace.Front => BlockFace.Left,
                BlockFace.Back => BlockFace.Right,
                _ => worldFace
            },

            BlockPlacementAxis.XNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Back,
                BlockFace.Left => BlockFace.Front,
                BlockFace.Front => BlockFace.Right,
                BlockFace.Back => BlockFace.Left,
                _ => worldFace
            },

            BlockPlacementAxis.ZNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Left,
                BlockFace.Left => BlockFace.Right,
                BlockFace.Front => BlockFace.Back,
                BlockFace.Back => BlockFace.Front,
                _ => worldFace
            },

            _ => worldFace
        };
    }

    private static BlockPlacementAxis ResolveAxisFromHitNormalOrFallback(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (hitNormal.x > 0)
            return BlockPlacementAxis.XNegative;

        if (hitNormal.x < 0)
            return BlockPlacementAxis.X;

        if (hitNormal.z > 0)
            return BlockPlacementAxis.ZNegative;

        if (hitNormal.z < 0)
            return BlockPlacementAxis.Z;

        if (hitNormal.y > 0)
            return BlockPlacementAxis.Y;

        if (hitNormal.y < 0)
            return BlockPlacementAxis.YNegative;

        return ResolveHorizontalAxisFromLookForward(lookForward);
    }

    private static BlockPlacementAxis ResolveHorizontalAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return BlockPlacementAxis.X;

        if (Mathf.Abs(hitNormal.z) > 0)
            return BlockPlacementAxis.Z;

        return ResolveHorizontalAxisFromLookForward(lookForward);
    }

    private static BlockPlacementAxis ResolveHorizontalAxisFromLookForward(Vector3 lookForward)
    {
        float absX = Mathf.Abs(lookForward.x);
        float absZ = Mathf.Abs(lookForward.z);
        if (absX >= absZ)
            return lookForward.x >= 0f ? BlockPlacementAxis.X : BlockPlacementAxis.XNegative;

        return lookForward.z >= 0f ? BlockPlacementAxis.Z : BlockPlacementAxis.ZNegative;
    }

    private static BlockPlacementAxis ResolveHorizontalAxisFacingPlayer(Vector3 lookForward)
    {
        return ResolveHorizontalAxisFromLookForward(lookForward) switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.XNegative,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.X,
            BlockPlacementAxis.Z => BlockPlacementAxis.ZNegative,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.Z,
            _ => BlockPlacementAxis.Y
        };
    }

    private static BlockPlacementAxis ResolveFlatPlanePlacementAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return BlockPlacementAxis.X;

        if (Mathf.Abs(hitNormal.z) > 0)
            return BlockPlacementAxis.Z;

        BlockPlacementAxis horizontalAxis = ResolveHorizontalAxisFromLookForward(lookForward);
        return horizontalAxis == BlockPlacementAxis.Z || horizontalAxis == BlockPlacementAxis.ZNegative
            ? BlockPlacementAxis.ZNegative
            : BlockPlacementAxis.XNegative;
    }
}

public static class BlockShapeUtility
{
    private static readonly Vector3 DefaultCrossMin = new Vector3(0.15f, 0f, 0.15f);
    private static readonly Vector3 DefaultCrossMax = new Vector3(0.85f, 1f, 0.85f);
    private static readonly Vector3 DefaultCuboidMin = new Vector3(0.375f, 0f, 0.375f);
    private static readonly Vector3 DefaultCuboidMax = new Vector3(0.625f, 0.75f, 0.625f);
    private static readonly Vector3 DefaultPlaneMin = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 DefaultPlaneMax = new Vector3(1f, 0.0625f, 1f);
    private static readonly Vector3 DefaultSlabMin = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 DefaultSlabMax = new Vector3(1f, 0.5f, 1f);
    private const float PlaneBoundsHalfThickness = 0.01f;
    private const float PlaneAttachmentInset01 = 0.001f;
    private const float BoundsEpsilon = 0.0001f;

    public static BlockRenderShape GetEffectiveRenderShape(BlockTextureMapping mapping)
    {
        return mapping.isFlat ? BlockRenderShape.Plane : mapping.renderShape;
    }

    public static bool IsFlatShape(BlockTextureMapping mapping)
    {
        return GetEffectiveRenderShape(mapping) == BlockRenderShape.Plane;
    }

    public static bool UsesCustomMesh(BlockTextureMapping mapping)
    {
        if (mapping.renderAsDynamicPrefab)
            return false;

        return GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube;
    }

    public static int GetDynamicOccupiedHorizontalBlocks(BlockTextureMapping mapping)
    {
        return mapping.renderAsDynamicPrefab ? Mathf.Max(1, mapping.dynamicOccupiedHorizontalBlocks) : 1;
    }

    public static int GetDynamicOccupiedVerticalBlocks(BlockTextureMapping mapping)
    {
        return mapping.renderAsDynamicPrefab ? Mathf.Max(1, mapping.dynamicOccupiedVerticalBlocks) : 1;
    }

    public static bool HasExpandedDynamicOccupancy(BlockTextureMapping mapping)
    {
        return mapping.renderAsDynamicPrefab &&
               (GetDynamicOccupiedHorizontalBlocks(mapping) > 1 ||
                GetDynamicOccupiedVerticalBlocks(mapping) > 1);
    }

    public static ShapeBox GetDynamicOccupancyBox(BlockTextureMapping mapping)
    {
        int horizontalBlocks = GetDynamicOccupiedHorizontalBlocks(mapping);
        int verticalBlocks = GetDynamicOccupiedVerticalBlocks(mapping);
        return new ShapeBox(Vector3.zero, new Vector3(horizontalBlocks, verticalBlocks, horizontalBlocks));
    }

    public static Bounds GetDynamicOccupancyBounds(Vector3Int blockPos, BlockTextureMapping mapping)
    {
        return GetDynamicOccupancyBox(mapping).ToWorldBounds(blockPos);
    }

    public static byte GetEffectiveLightOpacity(BlockTextureMapping mapping)
    {
        if (GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube && !mapping.isSolid && !mapping.isLiquid)
            return 0;

        return mapping.lightOpacity;
    }

    public static void ResolveShapeBounds(BlockTextureMapping mapping, out Vector3 min, out Vector3 max)
    {
        Vector3 clampedMin = Clamp01(mapping.shapeMin);
        Vector3 clampedMax = Clamp01(mapping.shapeMax);

        bool valid =
            clampedMax.x > clampedMin.x + BoundsEpsilon &&
            clampedMax.y > clampedMin.y + BoundsEpsilon &&
            clampedMax.z > clampedMin.z + BoundsEpsilon;

        if (valid)
        {
            min = clampedMin;
            max = clampedMax;
            return;
        }

        switch (GetEffectiveRenderShape(mapping))
        {
            case BlockRenderShape.Cross:
                min = DefaultCrossMin;
                max = DefaultCrossMax;
                return;

            case BlockRenderShape.Cuboid:
                min = DefaultCuboidMin;
                max = DefaultCuboidMax;
                return;

            case BlockRenderShape.Plane:
                min = DefaultPlaneMin;
                max = DefaultPlaneMax;
                return;

            case BlockRenderShape.Slab:
                min = DefaultSlabMin;
                max = DefaultSlabMax;
                return;

            default:
                min = Vector3.zero;
                max = Vector3.one;
                return;
        }
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockTextureMapping mapping)
    {
        return GetWorldBounds(blockPos, BlockType.Air, mapping, BlockPlacementAxis.Y);
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockType blockType, BlockTextureMapping mapping)
    {
        return GetWorldBounds(blockPos, blockType, mapping, BlockPlacementAxis.Y);
    }

    public static Bounds GetWorldBounds(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        return GetWorldBounds(blockPos, blockType, mapping, placementAxis, false, false);
    }

    public static Bounds GetWorldBounds(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        bool hasNegativeSupport,
        bool hasPositiveSupport)
    {
        if (mapping.renderAsDynamicPrefab)
            return GetDynamicOccupancyBounds(blockPos, mapping);

        if (TorchPlacementUtility.IsWallTorch(blockType))
            return TorchPlacementUtility.GetWorldBounds(blockPos, blockType, mapping);

        if (IsFlatShape(mapping))
        {
            ResolvePlaneQuad(
                mapping,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2,
                out Vector3 p3,
                out _,
                out _);

            Vector3 worldP0 = blockPos + p0;
            Vector3 worldP1 = blockPos + p1;
            Vector3 worldP2 = blockPos + p2;
            Vector3 worldP3 = blockPos + p3;

            Vector3 planeWorldMin = Vector3.Min(Vector3.Min(worldP0, worldP1), Vector3.Min(worldP2, worldP3));
            Vector3 planeWorldMax = Vector3.Max(Vector3.Max(worldP0, worldP1), Vector3.Max(worldP2, worldP3));

            BlockPlacementAxis axis = ResolvePlaneAxis(mapping, placementAxis);
            switch (axis)
            {
                case BlockPlacementAxis.X:
                    planeWorldMin.x -= PlaneBoundsHalfThickness;
                    planeWorldMax.x += PlaneBoundsHalfThickness;
                    break;

                case BlockPlacementAxis.Z:
                    planeWorldMin.z -= PlaneBoundsHalfThickness;
                    planeWorldMax.z += PlaneBoundsHalfThickness;
                    break;

                default:
                    planeWorldMin.y -= PlaneBoundsHalfThickness;
                    planeWorldMax.y += PlaneBoundsHalfThickness;
                    break;
            }

            Vector3 planeSize = planeWorldMax - planeWorldMin;
            return new Bounds(planeWorldMin + planeSize * 0.5f, planeSize);
        }

        BlockRenderShape shape = GetEffectiveRenderShape(mapping);
        ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        if (shape == BlockRenderShape.Cuboid || shape == BlockRenderShape.MultiCuboid)
        {
            Vector3 supportOffset = BlockSupportSurfaceUtility.GetSurfaceAlignedWorldOffset(
                World.Instance,
                blockPos,
                blockType,
                mapping,
                placementAxis);
            min += supportOffset;
            max += supportOffset;
        }

        Vector3 boundsMin = blockPos + min;
        Vector3 boundsMax = blockPos + max;
        Vector3 size = boundsMax - boundsMin;
        Vector3 center = boundsMin + size * 0.5f;
        return new Bounds(center, size);
    }

    public static void ResolvePlaneQuad(
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace,
        out Vector3 normal)
    {
        ResolvePlaneQuad(
            mapping,
            placementAxis,
            false,
            false,
            out p0,
            out p1,
            out p2,
            out p3,
            out sampledFace,
            out normal);
    }

    public static void ResolvePlaneQuad(
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        bool hasNegativeSupport,
        bool hasPositiveSupport,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace,
        out Vector3 normal)
    {
        ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        BlockPlacementAxis axis = ResolvePlaneAxis(mapping, placementAxis);

        switch (axis)
        {
            case BlockPlacementAxis.X:
                {
                    float x = ResolveSidePlaneCoordinate(hasNegativeSupport, hasPositiveSupport);
                    p0 = new Vector3(x, min.x, min.z);
                    p1 = new Vector3(x, max.x, min.z);
                    p2 = new Vector3(x, max.x, max.z);
                    p3 = new Vector3(x, min.x, max.z);
                    sampledFace = BlockFace.Right;
                    normal = Vector3.right;
                    return;
                }

            case BlockPlacementAxis.Z:
                {
                    float z = ResolveSidePlaneCoordinate(hasNegativeSupport, hasPositiveSupport);
                    p0 = new Vector3(min.x, min.z, z);
                    p1 = new Vector3(max.x, min.z, z);
                    p2 = new Vector3(max.x, max.z, z);
                    p3 = new Vector3(min.x, max.z, z);
                    sampledFace = BlockFace.Front;
                    normal = Vector3.forward;
                    return;
                }

            default:
                {
                    float y = (min.y + max.y) * 0.5f;
                    p0 = new Vector3(min.x, y, min.z);
                    p1 = new Vector3(min.x, y, max.z);
                    p2 = new Vector3(max.x, y, max.z);
                    p3 = new Vector3(max.x, y, min.z);
                    sampledFace = BlockFace.Top;
                    normal = Vector3.up;
                    return;
                }
        }
    }

    private static BlockPlacementAxis ResolvePlaneAxis(BlockTextureMapping mapping, BlockPlacementAxis placementAxis)
    {
        if (!mapping.usePlacementAxisRotation)
            return BlockPlacementAxis.Y;

        if (GetEffectiveRenderShape(mapping) == BlockRenderShape.Plane &&
            mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
        {
            BlockPlacementAxis storedAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            return storedAxis switch
            {
                BlockPlacementAxis.X => BlockPlacementAxis.X,
                BlockPlacementAxis.Z => BlockPlacementAxis.Z,
                _ => BlockPlacementAxis.Y
            };
        }

        return BlockPlacementRotationUtility.SanitizeAxis(placementAxis);
    }

    private static float ResolveSidePlaneCoordinate(bool hasNegativeSupport, bool hasPositiveSupport)
    {
        if (hasNegativeSupport == hasPositiveSupport)
            return 0.5f;

        return hasNegativeSupport ? PlaneAttachmentInset01 : 1f - PlaneAttachmentInset01;
    }

    public static int GetMultiCuboidBoxCount(BlockTextureMapping mapping, BlockModelCuboid[] cuboids)
    {
        if (cuboids == null || cuboids.Length == 0 || mapping.multiCuboidCount <= 0)
            return 0;

        if (mapping.multiCuboidStartIndex < 0 || mapping.multiCuboidStartIndex >= cuboids.Length)
            return 0;

        return Mathf.Min(mapping.multiCuboidCount, cuboids.Length - mapping.multiCuboidStartIndex);
    }

    public static bool TryGetMultiCuboidBox(
        BlockTextureMapping mapping,
        BlockModelCuboid[] cuboids,
        int localIndex,
        BlockPlacementAxis placementAxis,
        BlockType blockType,
        out ShapeBox box)
    {
        box = default;
        if (!TryGetMultiCuboidModelCuboid(mapping, cuboids, localIndex, out BlockModelCuboid cuboid))
            return false;

        if (TorchPlacementUtility.IsWallTorch(blockType))
        {
            box = GetWallTorchMultiCuboidBounds(cuboid, blockType);
            return true;
        }

        ShapeBox sanitized = cuboid.ToShapeBox();
        box = HasCuboidRotation(cuboid)
            ? GetTransformedCuboidBounds(sanitized, cuboid.eulerRotation, mapping, placementAxis)
            : TransformShapeBoxForPlacement(sanitized, mapping, placementAxis);
        return true;
    }

    public static bool TryGetMultiCuboidModelCuboid(
        BlockTextureMapping mapping,
        BlockModelCuboid[] cuboids,
        int localIndex,
        out BlockModelCuboid cuboid)
    {
        cuboid = default;
        int count = GetMultiCuboidBoxCount(mapping, cuboids);
        if (localIndex < 0 || localIndex >= count)
            return false;

        BlockModelCuboid source = cuboids[mapping.multiCuboidStartIndex + localIndex];
        if (!TrySanitizeShapeBox(source.min, source.max, out ShapeBox sanitized))
            return false;

        cuboid = new BlockModelCuboid
        {
            min = sanitized.min,
            max = sanitized.max,
            eulerRotation = NormalizeCuboidEulerRotation(source.eulerRotation),
            faces = source.EffectiveFaces,
            textureOverrideFaces = source.EffectiveTextureOverrideFaces,
            textureTop = source.textureTop,
            textureBottom = source.textureBottom,
            textureRight = source.textureRight,
            textureLeft = source.textureLeft,
            textureFront = source.textureFront,
            textureBack = source.textureBack
        };
        cuboid.CopyUvRectOverrideDataFrom(source);
        return true;
    }

    public static bool TryGetMultiCuboidBounds(
        Vector3Int blockPos,
        BlockTextureMapping mapping,
        BlockModelCuboid[] cuboids,
        BlockPlacementAxis placementAxis,
        BlockType blockType,
        out Bounds bounds)
    {
        bounds = default;
        int count = GetMultiCuboidBoxCount(mapping, cuboids);
        if (count <= 0)
            return false;

        bool hasBounds = false;
        Vector3 supportOffset = BlockSupportSurfaceUtility.GetSurfaceAlignedWorldOffset(
            World.Instance,
            blockPos,
            blockType,
            mapping,
            placementAxis);

        for (int i = 0; i < count; i++)
        {
            if (!TryGetMultiCuboidBox(mapping, cuboids, i, placementAxis, blockType, out ShapeBox box))
                continue;

            Bounds boxBounds = box.ToWorldBounds(blockPos);
            boxBounds.center += supportOffset;

            if (!hasBounds)
            {
                bounds = boxBounds;
                hasBounds = true;
                continue;
            }

            bounds.Encapsulate(boxBounds);
        }

        return hasBounds;
    }

    private static ShapeBox GetWallTorchMultiCuboidBounds(BlockModelCuboid cuboid, BlockType blockType)
    {
        ShapeBox box = cuboid.ToShapeBox();
        Vector3 center = (box.min + box.max) * 0.5f;
        Quaternion rotation = Quaternion.Euler(cuboid.eulerRotation);
        bool hasCuboidRotation = HasCuboidRotation(cuboid);
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        EncapsulateWallTorchMultiCuboidPoint(box.min.x, box.min.y, box.min.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.max.x, box.min.y, box.min.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.min.x, box.max.y, box.min.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.max.x, box.max.y, box.min.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.min.x, box.min.y, box.max.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.max.x, box.min.y, box.max.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.min.x, box.max.y, box.max.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);
        EncapsulateWallTorchMultiCuboidPoint(box.max.x, box.max.y, box.max.z, center, rotation, hasCuboidRotation, blockType, ref min, ref max);

        return new ShapeBox(min, max);
    }

    private static void EncapsulateWallTorchMultiCuboidPoint(
        float x,
        float y,
        float z,
        Vector3 center,
        Quaternion rotation,
        bool hasCuboidRotation,
        BlockType blockType,
        ref Vector3 min,
        ref Vector3 max)
    {
        Vector3 point = new Vector3(x, y, z);
        if (hasCuboidRotation)
            point = center + rotation * (point - center);

        point = TorchPlacementUtility.TransformVoxelPoint(blockType, point);
        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }

    public static ShapeBox TransformShapeBoxForPlacement(
        ShapeBox box,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (!ShouldRotateShapeForPlacement(mapping))
            return box;

        return UsesFullPlacementAxisRotation(mapping)
            ? RotateFullPlacementShapeBox(box, placementAxis)
            : RotateHorizontalShapeBox(box, placementAxis);
    }

    public static Vector3 TransformPointForPlacement(
        Vector3 point,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (!ShouldRotateShapeForPlacement(mapping))
            return point;

        return UsesFullPlacementAxisRotation(mapping)
            ? RotateFullPlacementPoint(point, placementAxis)
            : RotateHorizontalPoint(point, placementAxis);
    }

    public static Vector3 InverseTransformPointForPlacement(
        Vector3 point,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (!ShouldRotateShapeForPlacement(mapping))
            return point;

        return UsesFullPlacementAxisRotation(mapping)
            ? RotateFullPlacementPoint(point, GetInverseFullPlacementAxis(placementAxis))
            : RotateHorizontalPoint(point, GetInverseHorizontalPlacementAxis(placementAxis));
    }

    public static Vector3 TransformDirectionForPlacement(
        Vector3 direction,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (!ShouldRotateShapeForPlacement(mapping))
            return direction;

        return UsesFullPlacementAxisRotation(mapping)
            ? RotateFullPlacementDirection(direction, placementAxis)
            : RotateHorizontalDirection(direction, placementAxis);
    }

    public static BlockFace TransformFaceForPlacement(
        BlockFace face,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (!ShouldRotateShapeForPlacement(mapping))
            return face;

        if (UsesFullPlacementAxisRotation(mapping))
            return ResolveFaceFromDirection(RotateFullPlacementDirection(ResolveFaceNormal(face), placementAxis));

        return RotateHorizontalFace(face, placementAxis);
    }

    private static bool ShouldRotateShapeForPlacement(BlockTextureMapping mapping)
    {
        if (!mapping.usePlacementAxisRotation)
            return false;

        return mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal ||
               mapping.placementRotationAxes == BlockPlacementRotationAxes.Both;
    }

    private static bool UsesFullPlacementAxisRotation(BlockTextureMapping mapping)
    {
        return mapping.placementRotationAxes == BlockPlacementRotationAxes.Both;
    }

    public static bool HasCuboidRotation(BlockModelCuboid cuboid)
    {
        Vector3 rotation = NormalizeCuboidEulerRotation(cuboid.eulerRotation);
        return Mathf.Abs(rotation.x) > BoundsEpsilon ||
               Mathf.Abs(rotation.y) > BoundsEpsilon ||
               Mathf.Abs(rotation.z) > BoundsEpsilon;
    }

    public static Vector3 NormalizeCuboidEulerRotation(Vector3 eulerRotation)
    {
        return new Vector3(
            NormalizeEulerAngle(eulerRotation.x),
            NormalizeEulerAngle(eulerRotation.y),
            NormalizeEulerAngle(eulerRotation.z));
    }

    private static float NormalizeEulerAngle(float angle)
    {
        if (float.IsNaN(angle) || float.IsInfinity(angle))
            return 0f;

        angle %= 360f;
        if (angle < 0f)
            angle += 360f;

        return Mathf.Abs(angle - 360f) <= BoundsEpsilon ? 0f : angle;
    }

    private static bool TrySanitizeShapeBox(Vector3 sourceMin, Vector3 sourceMax, out ShapeBox box)
    {
        Vector3 min = Vector3.Min(sourceMin, sourceMax);
        Vector3 max = Vector3.Max(sourceMin, sourceMax);
        bool valid =
            max.x > min.x + BoundsEpsilon &&
            max.y > min.y + BoundsEpsilon &&
            max.z > min.z + BoundsEpsilon;

        if (!valid)
        {
            box = default;
            return false;
        }

        box = new ShapeBox(min, max);
        return true;
    }

    private static ShapeBox RotateHorizontalShapeBox(ShapeBox box, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        if (axis == BlockPlacementAxis.Y || axis == BlockPlacementAxis.Z)
            return box;

        Vector3 a = RotateHorizontalPoint(new Vector3(box.min.x, box.min.y, box.min.z), axis);
        Vector3 b = RotateHorizontalPoint(new Vector3(box.max.x, box.min.y, box.min.z), axis);
        Vector3 c = RotateHorizontalPoint(new Vector3(box.min.x, box.min.y, box.max.z), axis);
        Vector3 d = RotateHorizontalPoint(new Vector3(box.max.x, box.min.y, box.max.z), axis);

        float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
        float maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));

        return new ShapeBox(
            new Vector3(minX, box.min.y, minZ),
            new Vector3(maxX, box.max.y, maxZ));
    }

    private static ShapeBox RotateFullPlacementShapeBox(ShapeBox box, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        if (axis == BlockPlacementAxis.Y)
            return box;

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        EncapsulateFullPlacementShapePoint(box.min.x, box.min.y, box.min.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.max.x, box.min.y, box.min.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.min.x, box.max.y, box.min.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.max.x, box.max.y, box.min.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.min.x, box.min.y, box.max.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.max.x, box.min.y, box.max.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.min.x, box.max.y, box.max.z, axis, ref min, ref max);
        EncapsulateFullPlacementShapePoint(box.max.x, box.max.y, box.max.z, axis, ref min, ref max);

        return new ShapeBox(min, max);
    }

    private static void EncapsulateFullPlacementShapePoint(
        float x,
        float y,
        float z,
        BlockPlacementAxis axis,
        ref Vector3 min,
        ref Vector3 max)
    {
        Vector3 point = RotateFullPlacementPoint(new Vector3(x, y, z), axis);
        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }

    private static ShapeBox GetTransformedCuboidBounds(
        ShapeBox box,
        Vector3 eulerRotation,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        Vector3 center = (box.min + box.max) * 0.5f;
        Quaternion rotation = Quaternion.Euler(NormalizeCuboidEulerRotation(eulerRotation));
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        EncapsulateTransformedCuboidPoint(box.min.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.max.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.min.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.max.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.min.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.max.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.min.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
        EncapsulateTransformedCuboidPoint(box.max.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);

        return new ShapeBox(min, max);
    }

    private static void EncapsulateTransformedCuboidPoint(
        float x,
        float y,
        float z,
        Vector3 center,
        Quaternion rotation,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        ref Vector3 min,
        ref Vector3 max)
    {
        Vector3 point = new Vector3(x, y, z);
        point = center + rotation * (point - center);
        point = TransformPointForPlacement(point, mapping, placementAxis);
        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }

    private static Vector3 RotateHorizontalPoint(Vector3 point, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                return new Vector3(point.z, point.y, 1f - point.x);

            case BlockPlacementAxis.ZNegative:
                return new Vector3(1f - point.x, point.y, 1f - point.z);

            case BlockPlacementAxis.XNegative:
                return new Vector3(1f - point.z, point.y, point.x);

            default:
                return point;
        }
    }

    private static BlockPlacementAxis GetInverseHorizontalPlacementAxis(BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                return BlockPlacementAxis.XNegative;

            case BlockPlacementAxis.XNegative:
                return BlockPlacementAxis.X;

            case BlockPlacementAxis.ZNegative:
                return BlockPlacementAxis.ZNegative;

            default:
                return axis;
        }
    }

    private static BlockPlacementAxis GetInverseFullPlacementAxis(BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                return BlockPlacementAxis.XNegative;

            case BlockPlacementAxis.XNegative:
                return BlockPlacementAxis.X;

            case BlockPlacementAxis.Z:
                return BlockPlacementAxis.ZNegative;

            case BlockPlacementAxis.ZNegative:
                return BlockPlacementAxis.Z;

            default:
                return axis;
        }
    }

    private static Vector3 RotateHorizontalDirection(Vector3 direction, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                return new Vector3(direction.z, direction.y, -direction.x);

            case BlockPlacementAxis.ZNegative:
                return new Vector3(-direction.x, direction.y, -direction.z);

            case BlockPlacementAxis.XNegative:
                return new Vector3(-direction.z, direction.y, direction.x);

            default:
                return direction;
        }
    }

    private static Vector3 RotateFullPlacementPoint(Vector3 point, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.YNegative:
                return new Vector3(1f - point.x, 1f - point.y, point.z);

            case BlockPlacementAxis.XNegative:
                return new Vector3(point.y, 1f - point.x, point.z);

            case BlockPlacementAxis.X:
                return new Vector3(1f - point.y, point.x, point.z);

            case BlockPlacementAxis.ZNegative:
                return new Vector3(point.x, 1f - point.z, point.y);

            case BlockPlacementAxis.Z:
                return new Vector3(point.x, point.z, 1f - point.y);

            default:
                return point;
        }
    }

    private static Vector3 RotateFullPlacementDirection(Vector3 direction, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.YNegative:
                return new Vector3(-direction.x, -direction.y, direction.z);

            case BlockPlacementAxis.XNegative:
                return new Vector3(direction.y, -direction.x, direction.z);

            case BlockPlacementAxis.X:
                return new Vector3(-direction.y, direction.x, direction.z);

            case BlockPlacementAxis.ZNegative:
                return new Vector3(direction.x, -direction.z, direction.y);

            case BlockPlacementAxis.Z:
                return new Vector3(direction.x, direction.z, -direction.y);

            default:
                return direction;
        }
    }

    private static Vector3 ResolveFaceNormal(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Right: return Vector3.right;
            case BlockFace.Left: return Vector3.left;
            case BlockFace.Top: return Vector3.up;
            case BlockFace.Bottom: return Vector3.down;
            case BlockFace.Front: return Vector3.forward;
            case BlockFace.Back: return Vector3.back;
            default: return Vector3.zero;
        }
    }

    private static BlockFace ResolveFaceFromDirection(Vector3 direction)
    {
        float absX = Mathf.Abs(direction.x);
        float absY = Mathf.Abs(direction.y);
        float absZ = Mathf.Abs(direction.z);

        if (absX >= absY && absX >= absZ)
            return direction.x >= 0f ? BlockFace.Right : BlockFace.Left;

        if (absY >= absZ)
            return direction.y >= 0f ? BlockFace.Top : BlockFace.Bottom;

        return direction.z >= 0f ? BlockFace.Front : BlockFace.Back;
    }

    private static BlockFace RotateHorizontalFace(BlockFace face, BlockPlacementAxis placementAxis)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                switch (face)
                {
                    case BlockFace.Right: return BlockFace.Back;
                    case BlockFace.Left: return BlockFace.Front;
                    case BlockFace.Front: return BlockFace.Right;
                    case BlockFace.Back: return BlockFace.Left;
                    default: return face;
                }

            case BlockPlacementAxis.ZNegative:
                switch (face)
                {
                    case BlockFace.Right: return BlockFace.Left;
                    case BlockFace.Left: return BlockFace.Right;
                    case BlockFace.Front: return BlockFace.Back;
                    case BlockFace.Back: return BlockFace.Front;
                    default: return face;
                }

            case BlockPlacementAxis.XNegative:
                switch (face)
                {
                    case BlockFace.Right: return BlockFace.Front;
                    case BlockFace.Left: return BlockFace.Back;
                    case BlockFace.Front: return BlockFace.Left;
                    case BlockFace.Back: return BlockFace.Right;
                    default: return face;
                }

            default:
                return face;
        }
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(
            Mathf.Clamp01(value.x),
            Mathf.Clamp01(value.y),
            Mathf.Clamp01(value.z));
    }
}
