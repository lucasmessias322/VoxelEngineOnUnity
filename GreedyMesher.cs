using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Greedy meshing implementation (CPU). Produz quads mescladas para cada face exposta.
/// A função é agnóstica ao sistema de texturas: recebe um delegate getTileIndex(blockType, normal)
/// que deve retornar o índice da layer no Texture2DArray (0..N-1).
/// </summary>
public static class GreedyMesher
{
    public struct MergedQuad
    {
        public Vector3 v0, v1, v2, v3; // world-space verts (or chunk-space; adapt conforme o seu pipeline)
        public Vector3 normal;
        public int tileIndex; // layer index para Texture2DArray
        public int materialIndex; // opcional se você tem submeshes por material
        public int widthBlocks, heightBlocks; // tamanho em blocos da quad (útil se quiser repetir a textura)
    }

    // Delegate para obter se um bloco é "oculto" (vazio/água) e para obter tileIndex.
    public delegate bool IsTransparentDelegate(int x, int y, int z);
    public delegate int GetTileIndexDelegate(int x, int y, int z, Vector3 normal, int blockType);
    public delegate int GetMaterialIndexDelegate(int blockType);

    // blocks: função que devolve blockType int (ou -1 = empty). width/height/depth = dimensões do chunk.
    public static List<MergedQuad> Generate(
        Func<int,int,int,int> getBlockTypeAt, // getBlockTypeAt(x,y,z) -> int (use -1 for air)
        int width, int height, int depth,
        float blockSize,
        GetTileIndexDelegate getTileIndex,
        GetMaterialIndexDelegate getMaterialIndex)
    {
        var quads = new List<MergedQuad>();

        // We will run greedy in 3 axes (X, Y, Z). For each axis we create a 2D mask and merge rectangles.
        // Adapted from classic greedy meshing algorithms.
        // Helper to fetch block type with bounds check:
        int GetBT(int x, int y, int z)
        {
            if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth) return -1;
            return getBlockTypeAt(x,y,z);
        }

        // For each axis: 0=X,1=Y,2=Z
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            int[] dims = new int[3] { width, height, depth };
            int dimU = dims[u];
            int dimV = dims[v];
            int dimW = dims[axis];

            // mask: for each slice along axis 'w' build a 2D array dimU x dimV
            for (int w = 0; w < dimW; w++)
            {
                // mask of tuples (blockType, tileIndex, mat) or null (-1)
                var maskBT = new int[dimU * dimV];
                var maskTile = new int[dimU * dimV];
                var maskMat = new int[dimU * dimV];

                for (int j = 0; j < dimV; j++)
                {
                    for (int i = 0; i < dimU; i++)
                    {
                        // compute x,y,z from (i,j,w) depending on axis
                        int x = 0, y = 0, z = 0;
                        int[] pos = new int[3];
                        pos[axis] = w;
                        pos[u] = i;
                        pos[v] = j;
                        x = pos[0]; y = pos[1]; z = pos[2];

                        int current = GetBT(x,y,z);
                        int neighborX = x, neighborY = y, neighborZ = z;
                        // neighbor in +axis direction
                        pos[axis] = w + 1;
                        neighborX = pos[0]; neighborY = pos[1]; neighborZ = pos[2];
                        int neighbor = GetBT(neighborX,neighborY,neighborZ);

                        // A face is exposed if current != neighbor (one is empty or different block)
                        // For greedy we only create quads for faces that are boundary between current != neighbor.
                        // We'll encode as: if current > -1 and neighbor == -1 => face pointing +axis (positive normal)
                        // or if current == -1 and neighbor > -1 => face pointing -axis (negative normal) with tile from neighbor.
                        if (current != neighbor)
                        {
                            int faceBlockType;
                            Vector3 normal;
                            if (current > -1)
                            {
                                // face belongs to current block, normal +axis
                                faceBlockType = current;
                                normal = AxisToNormal(axis, true);
                            }
                            else
                            {
                                // face belongs to neighbor block, normal -axis (we flip)
                                faceBlockType = neighbor;
                                normal = AxisToNormal(axis, false);
                            }

                            // get tile index using provided delegate; note we pass the block coordinates belonging to the face owner
                            int ownerX = current > -1 ? x : neighborX;
                            int ownerY = current > -1 ? y : neighborY;
                            int ownerZ = current > -1 ? z : neighborZ;

                            int tileIndex = getTileIndex(ownerX, ownerY, ownerZ, normal, faceBlockType);
                            int matIndex = getMaterialIndex(faceBlockType);

                            int idx = i + j * dimU;
                            maskBT[idx] = faceBlockType + 1; // store >0 to differentiate from empty
                            maskTile[idx] = tileIndex;
                            maskMat[idx] = matIndex;
                        }
                        else
                        {
                            int idx = i + j * dimU;
                            maskBT[idx] = 0; // empty
                            maskTile[idx] = -1;
                            maskMat[idx] = -1;
                        }
                    }
                }

                // Now greedy merge rectangles in mask arrays (classic)
                bool[] consumed = new bool[dimU * dimV];
                for (int j = 0; j < dimV; j++)
                {
                    for (int i = 0; i < dimU; i++)
                    {
                        int idx = i + j * dimU;
                        if (consumed[idx] || maskBT[idx] == 0) continue;

                        // value to match
                        int tile = maskTile[idx];
                        int mat = maskMat[idx];

                        // compute width
                        int widthRect = 1;
                        while (i + widthRect < dimU && !consumed[(i + widthRect) + j * dimU] && maskBT[(i + widthRect) + j * dimU] != 0
                               && maskTile[(i + widthRect) + j * dimU] == tile && maskMat[(i + widthRect) + j * dimU] == mat)
                            widthRect++;

                        // compute height
                        int heightRect = 1;
                        bool done = false;
                        while (!done && j + heightRect < dimV)
                        {
                            for (int k = 0; k < widthRect; k++)
                            {
                                int idx2 = (i + k) + (j + heightRect) * dimU;
                                if (consumed[idx2] || maskBT[idx2] == 0 || maskTile[idx2] != tile || maskMat[idx2] != mat)
                                {
                                    done = true; break;
                                }
                            }
                            if (!done) heightRect++;
                        }

                        // mark consumed
                        for (int jj = 0; jj < heightRect; jj++)
                            for (int ii = 0; ii < widthRect; ii++)
                                consumed[(i + ii) + (j + jj) * dimU] = true;

                        // Build quad geometry in chunk space.
                        // We need to compute the 4 vertices (v0..v3) depending on axis and whether face is positive or negative.
                        // Determine the owner block coordinates of the lower-left corner of the quad in (x,y,z)
                        // For current implementation: the face owner is the one we used to get tile.
                        // We'll compute base position (in block units) for the rectangle.
                        int[] basePos = new int[3];
                        basePos[axis] = w;
                        basePos[u] = i;
                        basePos[v] = j;

                        // if the face belonged to negative side (current == -1 case), we need to shift basePos along axis by 1
                        // detect sign by checking neighbor: check the stored normal direction by comparing tile owner to current neighbor logic
                        // Simpler: we rebuild normal using the getTileIndex call info: mask was filled with face owner info where current>neighbor => +axis else -axis.
                        // The normal will be deduced by checking the surrounding block: if GetBT(basePos + 0) > -1 and GetBT(basePos + +1) == -1 -> positive.
                        // We'll recompute a consistent normal:
                        int ownerX = basePos[0];
                        int ownerY = basePos[1];
                        int ownerZ = basePos[2];
                        // Recompute whether owner block is at basePos or basePos + axis
                        bool ownerOnNegativeSide = false;
                        // get types
                        int t0 = GetBT(ownerX, ownerY, ownerZ);
                        int t1;
                        int[] posNeighbor = new int[3] { ownerX, ownerY, ownerZ };
                        posNeighbor[axis] = basePos[axis] + 1;
                        t1 = GetBT(posNeighbor[0], posNeighbor[1], posNeighbor[2]);
                        Vector3 normal = (t0 > -1 && t1 == -1) ? AxisToNormal(axis, true) : AxisToNormal(axis, false);

                        // compute world positions (corner positions)
                        // We'll create quad in chunk-local coordinates (multiply by blockSize later)
                        // rectangle spans i..i+widthRect and j..j+heightRect on u/v axes and is located at w (on axis).
                        Vector3 du = IndexToAxisVector(u);
                        Vector3 dv = IndexToAxisVector(v);
                        Vector3 wa = IndexToAxisVector(axis);

                        // base corner in block coords
                        Vector3 baseCorner = new Vector3(basePos[0], basePos[1], basePos[2]);

                        // If face is the negative side, we must move baseCorner along axis (so it sits at owner block face)
                        // Determine owner block presence to decide
                        // We check if block at basePos is empty: if so owner is at basePos + axis (negative face)
                        if (GetBT((int)baseCorner.x, (int)baseCorner.y, (int)baseCorner.z) == -1)
                        {
                            baseCorner += wa; // shift to neighbor
                        }

                        // Construct the four corners (ordering consistent with winding)
                        // corners:
                        // c0 = baseCorner
                        // c1 = baseCorner + du * widthRect
                        // c2 = baseCorner + du * widthRect + dv * heightRect
                        // c3 = baseCorner + dv * heightRect
                        Vector3 c0 = baseCorner;
                        Vector3 c1 = baseCorner + du * widthRect;
                        Vector3 c2 = baseCorner + du * widthRect + dv * heightRect;
                        Vector3 c3 = baseCorner + dv * heightRect;

                        // Convert to world units (blockSize)
                        c0 *= blockSize; c1 *= blockSize; c2 *= blockSize; c3 *= blockSize;

                        var mq = new MergedQuad()
                        {
                            v0 = c0,
                            v1 = c1,
                            v2 = c2,
                            v3 = c3,
                            normal = normal,
                            tileIndex = tile,
                            materialIndex = mat,
                            widthBlocks = widthRect,
                            heightBlocks = heightRect
                        };
                        quads.Add(mq);
                    }
                } // end for i,j
            } // end for w
        } // end for axis

        return quads;
    }

    static Vector3 AxisToNormal(int axis, bool positive)
    {
        switch (axis)
        {
            case 0: return positive ? Vector3.right : Vector3.left;
            case 1: return positive ? Vector3.up : Vector3.down;
            default: return positive ? Vector3.forward : Vector3.back;
        }
    }
    static Vector3 IndexToAxisVector(int idx)
    {
        switch (idx)
        {
            case 0: return Vector3.right;
            case 1: return Vector3.up;
            default: return Vector3.forward;
        }
    }
}
