using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Block Overrides & Placement

    private void EnsureTerrainOverrideIndexBuilt()
    {
        if (terrainOverrideIndexInitialized)
            return;

        terrainOverridePositionsByChunk.Clear();
        foreach (var kv in blockOverrides)
        {
            Vector3Int worldPos = kv.Key;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;

            Vector2Int coord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
            if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
            {
                positions = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
                terrainOverridePositionsByChunk[coord] = positions;
            }

            positions.Add(worldPos);
        }

        terrainOverrideIndexInitialized = true;
    }

    private void IndexTerrainOverride(Vector3Int worldPos, Vector2Int coord)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return;

        if (!terrainOverridePositionsByChunk.TryGetValue(coord, out HashSet<Vector3Int> positions))
        {
            positions = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
            terrainOverridePositionsByChunk[coord] = positions;
        }

        positions.Add(worldPos);
    }

    private void CollectRelevantTerrainOverridePositions(Vector2Int coord, int borderSize, List<Vector3Int> output)
    {
        output.Clear();
        if (blockOverrides.Count == 0)
            return;

        EnsureTerrainOverrideIndexBuilt();

        int minX = coord.x * Chunk.SizeX - borderSize;
        int minZ = coord.y * Chunk.SizeZ - borderSize;
        int maxX = coord.x * Chunk.SizeX + Chunk.SizeX - 1 + borderSize;
        int maxZ = coord.y * Chunk.SizeZ + Chunk.SizeZ - 1 + borderSize;
        int chunkRadiusX = Mathf.CeilToInt(borderSize / (float)Chunk.SizeX);
        int chunkRadiusZ = Mathf.CeilToInt(borderSize / (float)Chunk.SizeZ);

        for (int dz = -chunkRadiusZ; dz <= chunkRadiusZ; dz++)
        {
            for (int dx = -chunkRadiusX; dx <= chunkRadiusX; dx++)
            {
                Vector2Int candidateCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                if (!terrainOverridePositionsByChunk.TryGetValue(candidateCoord, out HashSet<Vector3Int> positions))
                    continue;

                foreach (Vector3Int worldPos in positions)
                {
                    if (worldPos.x < minX || worldPos.x > maxX || worldPos.z < minZ || worldPos.z > maxZ)
                        continue;

                    output.Add(worldPos);
                }
            }
        }
    }

    public BlockPlacementAxis ResolvePlacementAxisForPlacement(
        BlockType blockType,
        Vector3Int hitNormal,
        Vector3 lookForward,
        Vector3 hitPoint)
    {
        if (blockType == BlockType.ConveyorBelt ||
            blockType == BlockType.conveyorBelt_splitter)
        {
            return ConveyorBeltUtility.ResolvePlacementAxis(lookForward);
        }
        if (blockType == BlockType.conveyorBelt_45deg)
            return RampShapeUtility.ResolvePlacementAxis(lookForward);

        if (blockType == BlockType.SolarPanel)
            return ConveyorBeltUtility.ResolvePlacementAxis(lookForward);

        if (blockType == BlockType.wire)
            return (BlockPlacementAxis)WirePlacementUtility.ResolvePlacementCode(hitNormal);

        if (TransportTubeUtility.IsTransportTubeNetworkBlock(blockType) ||
            FluidPipeUtility.IsFluidPipeBlock(blockType))
        {
            if (hitNormal.x != 0)
                return BlockPlacementAxis.X;

            if (hitNormal.z != 0)
                return BlockPlacementAxis.Z;

            if (hitNormal.y != 0)
                return BlockPlacementAxis.YNegative;
        }

        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return BlockPlacementAxis.Y;

        return BlockPlacementRotationUtility.ResolvePlacementAxis(mapping, hitNormal, lookForward, hitPoint);
    }

    public BlockPlacementAxis GetPlacementAxisAt(Vector3Int worldPos, BlockType blockType)
    {
        return GetStoredPlacementAxis(worldPos, blockType);
    }

    private byte GetStoredPlacementAxisRawValue(Vector3Int worldPos, BlockType blockType)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return (byte)BlockPlacementAxis.Y;

        if (!blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis axis))
            return (byte)BlockPlacementAxis.Y;

        if (blockType == BlockType.wire)
            return ResolveStoredWirePlacementRaw(worldPos, (byte)axis);

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs &&
            StairPlacementUtility.IsEncodedState((byte)axis))
        {
            return (byte)axis;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Ramp &&
            RampShapeUtility.IsEncodedState((byte)axis))
        {
            return (byte)axis;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Slab)
        {
            return (byte)(SlabShapeUtility.IsTopHalf(axis) ? BlockPlacementAxis.YNegative : BlockPlacementAxis.Y);
        }

        return BlockPlacementRotationUtility.SanitizeStoredAxisByte((byte)axis);
    }

    private BlockPlacementAxis GetStoredPlacementAxis(Vector3Int worldPos, BlockType blockType)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
            return BlockPlacementAxis.Y;

        if (!blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis axis))
            return BlockPlacementAxis.Y;

        if (blockType == BlockType.wire)
            return ResolveStoredWirePlacementAxis(worldPos, (byte)axis);

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs &&
            StairPlacementUtility.IsEncodedState((byte)axis))
        {
            return axis;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Ramp &&
            RampShapeUtility.IsEncodedState((byte)axis))
        {
            return axis;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Slab)
        {
            return SlabShapeUtility.IsTopHalf(axis) ? BlockPlacementAxis.YNegative : BlockPlacementAxis.Y;
        }

        BlockPlacementAxis sanitized = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);

        return sanitized;
    }

    private void UpdateStoredPlacementAxis(Vector3Int worldPos, BlockType blockType, BlockPlacementAxis axis)
    {
        if (!TryGetPlacementRotationMapping(blockType, out BlockTextureMapping mapping))
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        if (blockType == BlockType.wire)
        {
            UpdateStoredWirePlacementAxis(worldPos, (byte)axis);
            return;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Stairs)
        {
            byte rawState = (byte)axis;
            if (!StairPlacementUtility.IsEncodedState(rawState))
            {
                blockPlacementAxes.Remove(worldPos);
                return;
            }

            blockPlacementAxes[worldPos] = (BlockPlacementAxis)rawState;
            return;
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Ramp)
        {
            byte rawState = (byte)axis;
            if (RampShapeUtility.IsEncodedState(rawState))
            {
                blockPlacementAxes[worldPos] = (BlockPlacementAxis)rawState;
                return;
            }
        }

        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Slab)
        {
            if (SlabShapeUtility.IsTopHalf(axis))
                blockPlacementAxes[worldPos] = BlockPlacementAxis.YNegative;
            else
                blockPlacementAxes.Remove(worldPos);

            return;
        }

        BlockPlacementAxis sanitized = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);
        if (sanitized == BlockPlacementAxis.Y)
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        blockPlacementAxes[worldPos] = sanitized;
    }

    private bool TryGetPlacementRotationMapping(BlockType blockType, out BlockTextureMapping mapping)
    {
        mapping = default;
        if (blockType == BlockType.Air || blockData == null)
            return false;

        BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
        if (mappingResult == null)
            return false;

        mapping = mappingResult.Value;
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        return blockType == BlockType.ConveyorBelt ||
               blockType == BlockType.conveyorBelt_splitter ||
               blockType == BlockType.conveyorBelt_45deg ||
               blockType == BlockType.SolarPanel ||
               mapping.usePlacementAxisRotation ||
               shape == BlockRenderShape.Stairs ||
               shape == BlockRenderShape.Ramp ||
               shape == BlockRenderShape.VerticalRamp ||
               shape == BlockRenderShape.Slab;
    }

    private static BlockPlacementAxis ResolveWirePlacementAxis(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return BlockPlacementAxis.XNegative;

        if (Mathf.Abs(horizontal.z) > Mathf.Abs(horizontal.x))
            return BlockPlacementAxis.ZNegative;

        return BlockPlacementAxis.XNegative;
    }

    private static BlockPlacementAxis ResolveWirePlacementAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        return (BlockPlacementAxis)WirePlacementUtility.ResolvePlacementCode(hitNormal);
    }

    private BlockPlacementAxis ResolveStoredWirePlacementAxis(Vector3Int worldPos, byte rawValue)
    {
        rawValue = ResolveStoredWirePlacementRaw(worldPos, rawValue);
        if (WirePlacementUtility.TryGetWall(rawValue, out BlockPlacementAxis encodedWallAxis, out int encodedAttachmentSide))
        {
            if (HasWireSupportOnSide(worldPos, encodedWallAxis, encodedAttachmentSide))
                return encodedWallAxis;

            return WirePlacementUtility.HasTop(rawValue) ? BlockPlacementAxis.Y : encodedWallAxis;
        }

        BlockPlacementAxis axis = NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
        if (axis == BlockPlacementAxis.X || axis == BlockPlacementAxis.Z)
        {
            if (HasWireSupportOnAxis(worldPos, axis))
                return axis;

            return BlockPlacementAxis.Y;
        }

        return BlockPlacementAxis.Y;
    }

    private byte ResolveStoredWirePlacementRaw(Vector3Int worldPos, byte rawValue)
    {
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return rawValue;

        BlockPlacementAxis axis = NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
        return axis switch
        {
            BlockPlacementAxis.X when HasWireSupportOnSide(worldPos, BlockPlacementAxis.X, -1) => WirePlacementUtility.SideWest,
            BlockPlacementAxis.X when HasWireSupportOnSide(worldPos, BlockPlacementAxis.X, 1) => WirePlacementUtility.SideEast,
            BlockPlacementAxis.Z when HasWireSupportOnSide(worldPos, BlockPlacementAxis.Z, -1) => WirePlacementUtility.SideSouth,
            BlockPlacementAxis.Z when HasWireSupportOnSide(worldPos, BlockPlacementAxis.Z, 1) => WirePlacementUtility.SideNorth,
            _ => (byte)BlockPlacementAxis.Y
        };
    }

    private bool HasWireSupportOnAxis(Vector3Int worldPos, BlockPlacementAxis axis)
    {
        axis = BlockPlacementRotationUtility.SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => IsWireSupportBlock(worldPos + Vector3Int.left) || IsWireSupportBlock(worldPos + Vector3Int.right),
            BlockPlacementAxis.Z => IsWireSupportBlock(worldPos + new Vector3Int(0, 0, -1)) || IsWireSupportBlock(worldPos + new Vector3Int(0, 0, 1)),
            _ => false
        };
    }

    private bool HasWireSupportOnSide(Vector3Int worldPos, BlockPlacementAxis axis, int attachmentSide)
    {
        return axis switch
        {
            BlockPlacementAxis.X => IsWireSupportBlock(worldPos + (attachmentSide < 0 ? Vector3Int.left : Vector3Int.right)),
            BlockPlacementAxis.Z => IsWireSupportBlock(worldPos + (attachmentSide < 0 ? new Vector3Int(0, 0, -1) : new Vector3Int(0, 0, 1))),
            _ => false
        };
    }

    private bool IsWireSupportBlock(Vector3Int worldPos)
    {
        BlockType supportType = GetBlockAt(worldPos);
        if ((supportType == BlockType.Air || FluidBlockUtility.IsWater(supportType)) &&
            TryResolveDynamicBlockFootprintAt(worldPos, out _, out BlockType footprintType))
        {
            supportType = footprintType;
        }

        if (supportType == BlockType.Air || FluidBlockUtility.IsWater(supportType) || blockData == null)
            return false;

        BlockTextureMapping? mapping = blockData.GetMapping(supportType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty && !value.isLiquid;
    }

    private bool TryResolveDynamicBlockFootprintAt(Vector3Int worldPos, out Vector3Int origin, out BlockType blockType)
    {
        origin = default;
        blockType = BlockType.Air;
        if (blockData == null)
            return false;

        int maxHorizontalRadius = Mathf.Max(
            4,
            Mathf.Max(
                blockData.runtimeDynamicBlockOverflowSearchRadius.x,
                blockData.runtimeDynamicBlockOverflowSearchRadius.z));
        int maxVerticalRadius = Mathf.Max(3, blockData.runtimeDynamicBlockOverflowSearchRadius.y);
        for (int yOffset = 0; yOffset <= maxVerticalRadius; yOffset++)
        {
            for (int zOffset = -maxHorizontalRadius; zOffset <= maxHorizontalRadius; zOffset++)
            {
                for (int xOffset = -maxHorizontalRadius; xOffset <= maxHorizontalRadius; xOffset++)
                {
                    Vector3Int candidateOrigin = worldPos - new Vector3Int(xOffset, yOffset, zOffset);
                    BlockType candidateType = GetBlockAt(candidateOrigin);
                    if (candidateType == BlockType.Air)
                        continue;

                    BlockTextureMapping? mappingResult = blockData.GetMapping(candidateType);
                    if (mappingResult == null)
                        continue;

                    BlockTextureMapping mapping = mappingResult.Value;
                    if (!mapping.renderAsDynamicPrefab)
                        continue;

                    BlockPlacementAxis placementAxis = GetPlacementAxisAt(candidateOrigin, candidateType);
                    Vector3Int localOffset = new Vector3Int(xOffset, yOffset, zOffset);
                    if (!BlockShapeUtility.IsLocalOffsetInsideDynamicOccupancy(localOffset, mapping, placementAxis))
                        continue;

                    origin = candidateOrigin;
                    blockType = candidateType;
                    return true;
                }
            }
        }

        return false;
    }

    private static BlockPlacementAxis NormalizeWirePlacementAxis(BlockPlacementAxis axis)
    {
        byte rawValue = (byte)axis;
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return (BlockPlacementAxis)rawValue;

        axis = BlockPlacementRotationUtility.SanitizeStoredAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.XNegative,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.ZNegative,
            _ => BlockPlacementAxis.Y
        };
    }

    private void UpdateStoredWirePlacementAxis(Vector3Int worldPos, byte rawValue)
    {
        rawValue = NormalizeStoredWirePlacementRaw(rawValue);
        if (rawValue == (byte)BlockPlacementAxis.Y)
        {
            blockPlacementAxes.Remove(worldPos);
            return;
        }

        blockPlacementAxes[worldPos] = (BlockPlacementAxis)rawValue;
    }

    private static byte NormalizeStoredWirePlacementRaw(byte rawValue)
    {
        if (WirePlacementUtility.IsEncodedState(rawValue))
            return rawValue;

        return (byte)NormalizeWirePlacementAxis((BlockPlacementAxis)rawValue);
    }

    private static NativeArray<byte> CreateDefaultPlacementAxisArray(int length)
    {
        return MeshGenerator.RentByteBuffer(length, true);
    }

    private static void ApplyPlacementAxesFromBlockEdits(
        NativeArray<BlockEdit> edits,
        NativeArray<byte> blockPlacementAxes,
        int chunkMinX,
        int chunkMinZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (!edits.IsCreated || edits.Length == 0 || !blockPlacementAxes.IsCreated)
            return;

        for (int i = 0; i < edits.Length; i++)
        {
            BlockEdit edit = edits[i];
            if (edit.y < 0 || edit.y >= Chunk.SizeY)
                continue;

            int ix = edit.x - chunkMinX + borderSize;
            int iz = edit.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
            if ((uint)idx >= (uint)blockPlacementAxes.Length)
                continue;

            blockPlacementAxes[idx] = edit.placementAxis;
        }
    }

    private void AppendRelevantBlockEdits(Vector2Int coord, int borderSize, List<BlockEdit> editsList)
    {
        if (editsList == null)
            return;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            editsList.Add(new BlockEdit
            {
                x = worldPos.x,
                y = worldPos.y,
                z = worldPos.z,
                type = (int)overrideType,
                placementAxis = GetStoredPlacementAxisRawValue(worldPos, overrideType)
            });
        }
    }

    #endregion

}
