using UnityEngine;

[CreateAssetMenu(fileName = "MobType", menuName = "Voxel Mobs/Mob Type")]
public sealed class MobType : ScriptableObject
{
    [SerializeField] private string id;
    [SerializeField] private Color debugColor = Color.white;

    public string Id => string.IsNullOrWhiteSpace(id) ? name : id;
    public Color DebugColor => debugColor;

    private void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(id))
            id = name;
    }
}
