using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public struct BlockEdit
{
    public int x;
    public int y;
    public int z;
    public int type;
    public byte placementAxis;
}
