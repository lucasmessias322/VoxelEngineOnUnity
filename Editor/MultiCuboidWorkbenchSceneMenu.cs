using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MultiCuboidWorkbenchSceneMenu
{
    private const string ScenePath = "Assets/Scenes/MultiCuboidWorkbench.unity";

    [MenuItem("Tools/Voxel/Open Multi Cuboid Workbench Scene")]
    public static void OpenOrCreateWorkbenchScene()
    {
        OpenOrCreateWorkbenchSceneInternal(null);
    }

    [MenuItem("Assets/Open in Multi Cuboid Workbench", true)]
    private static bool ValidateOpenSelectedBlockData()
    {
        return Selection.activeObject is BlockDataSO;
    }

    [MenuItem("Assets/Open in Multi Cuboid Workbench")]
    private static void OpenSelectedBlockData()
    {
        OpenOrCreateWorkbenchSceneInternal(Selection.activeObject as BlockDataSO);
    }

    private static void OpenOrCreateWorkbenchSceneInternal(BlockDataSO selectedBlockData)
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        EnsureScenesFolder();

        Scene scene;
        if (File.Exists(ScenePath))
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        else
            scene = CreateWorkbenchScene();

        MultiCuboidBlockWorkbench workbench = FindOrCreateWorkbench(scene);
        if (selectedBlockData != null)
        {
            Undo.RecordObject(workbench, "Assign BlockData to Multi Cuboid Workbench");
            workbench.blockData = selectedBlockData;
            workbench.LoadFromBlockData();
            EditorUtility.SetDirty(workbench);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        Selection.activeGameObject = workbench.gameObject;
        SceneView.FrameLastActiveSceneView();
        EditorSceneManager.SaveScene(scene, ScenePath);
    }

    private static Scene CreateWorkbenchScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateCamera();
        CreateLight();
        CreateWorkbenchRoot();

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.Refresh();
        return scene;
    }

    private static MultiCuboidBlockWorkbench FindOrCreateWorkbench(Scene scene)
    {
        MultiCuboidBlockWorkbench workbench = FindWorkbench();
        if (workbench != null)
            return workbench;

        GameObject root = CreateWorkbenchRoot();
        EditorSceneManager.MarkSceneDirty(scene);
        return root.GetComponent<MultiCuboidBlockWorkbench>();
    }

    private static GameObject CreateWorkbenchRoot()
    {
        GameObject root = new GameObject("Multi Cuboid Workbench");
        root.AddComponent<MultiCuboidBlockWorkbench>();
        root.transform.position = Vector3.zero;
        return root;
    }

    private static void CreateCamera()
    {
        GameObject cameraObject = new GameObject("Workbench Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.13f, 0.15f, 1f);
        camera.nearClipPlane = 0.02f;
        camera.farClipPlane = 100f;
        cameraObject.AddComponent<AudioListener>();
        cameraObject.transform.position = new Vector3(2.4f, 1.7f, 2.4f);
        LookAt(cameraObject.transform, Vector3.zero);
    }

    private static void CreateLight()
    {
        GameObject lightObject = new GameObject("Workbench Light");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        lightObject.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
    }

    private static void LookAt(Transform transform, Vector3 target)
    {
        Vector3 direction = target - transform.position;
        if (direction.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    private static void EnsureScenesFolder()
    {
        if (!Directory.Exists("Assets/Scenes"))
            Directory.CreateDirectory("Assets/Scenes");
    }

    private static MultiCuboidBlockWorkbench FindWorkbench()
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindAnyObjectByType<MultiCuboidBlockWorkbench>();
#else
        return Object.FindObjectOfType<MultiCuboidBlockWorkbench>();
#endif
    }
}
