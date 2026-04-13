using UnityEngine;

[DisallowMultipleComponent]
public sealed class MultiCuboidWorkbenchCuboid : MonoBehaviour
{
    [HideInInspector] public MultiCuboidBlockWorkbench owner;
    [HideInInspector] public int index;
}
