using UnityEngine;

public static class SubchunkOcclusion
{
    public const int FaceCount = 6;
    public const int Down = 0;
    public const int Up = 1;
    public const int North = 2;
    public const int South = 3;
    public const int West = 4;
    public const int East = 5;

    public const ulong AllVisibleMask = (1UL << (FaceCount * FaceCount)) - 1UL;
    public const int MinimumAdvancedCullingSectionDistance = 3; // floor(60 / 16), matching Minecraft.
    public const float CeiledSectionDiagonal = 28f; // ceil(sqrt(3) * 16)

    public static bool FacesCanSeeEachOther(ulong visibilityMask, int faceA, int faceB)
    {
        int bitIndex = faceA + faceB * FaceCount;
        return (visibilityMask & (1UL << bitIndex)) != 0UL;
    }

    public static ulong SetVisible(ulong visibilityMask, int faceA, int faceB, bool value)
    {
        ulong bitA = 1UL << (faceA + faceB * FaceCount);
        ulong bitB = 1UL << (faceB + faceA * FaceCount);

        if (value)
            return visibilityMask | bitA | bitB;

        return visibilityMask & ~bitA & ~bitB;
    }

    public static ulong AddFaceSet(ulong visibilityMask, byte faceMask)
    {
        for (int faceA = 0; faceA < FaceCount; faceA++)
        {
            if ((faceMask & (1 << faceA)) == 0)
                continue;

            for (int faceB = 0; faceB < FaceCount; faceB++)
            {
                if ((faceMask & (1 << faceB)) == 0)
                    continue;

                visibilityMask = SetVisible(visibilityMask, faceA, faceB, true);
            }
        }

        return visibilityMask;
    }

    public static int GetOppositeFace(int face)
    {
        switch (face)
        {
            case Down: return Up;
            case Up: return Down;
            case North: return South;
            case South: return North;
            case West: return East;
            case East: return West;
            default: return -1;
        }
    }

    public static bool TryStep(Vector2Int chunkCoord, int subchunkIndex, int face, out Vector2Int nextChunkCoord, out int nextSubchunkIndex)
    {
        nextChunkCoord = chunkCoord;
        nextSubchunkIndex = subchunkIndex;

        switch (face)
        {
            case Down:
                nextSubchunkIndex--;
                return true;
            case Up:
                nextSubchunkIndex++;
                return true;
            case North:
                nextChunkCoord += Vector2Int.down;
                return true;
            case South:
                nextChunkCoord += Vector2Int.up;
                return true;
            case West:
                nextChunkCoord += Vector2Int.left;
                return true;
            case East:
                nextChunkCoord += Vector2Int.right;
                return true;
            default:
                return false;
        }
    }
}
