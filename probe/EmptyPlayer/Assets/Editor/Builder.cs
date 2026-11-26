using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode entry points used to produce one player per scripting backend
// so the on-disk layouts can be compared.
public static class Builder
{
    public static void BuildCoreCLR() => Build(ScriptingImplementation.CoreCLR, "coreclr");

    public static void BuildIL2CPP() => Build(ScriptingImplementation.IL2CPP, "il2cpp");

    public static void BuildMono() => Build(ScriptingImplementation.Mono2x, "mono");

    static void Build(ScriptingImplementation backend, string name)
    {
        const string scenePath = "Assets/Main.unity";
        SaveScene(scenePath);

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);

        var outDir = Path.GetFullPath(Path.Combine("..", "builds", name));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = Path.Combine(outDir, "Probe.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });

        Debug.Log($"PROBE BUILD {name}: {report.summary.result}, errors={report.summary.totalErrors}, output={outDir}");
        if (report.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    // The scene is written from code on every build, so the serialized values Exercise prints are defined here.
    static void SaveScene(string scenePath)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var root = new GameObject("Probe");
        root.AddComponent<Probe>();

        var child = new GameObject("Child");
        child.transform.SetParent(root.transform, false);
        var marker = child.AddComponent<Marker>();
        marker.id = 42;
        marker.label = "from the scene";

        var exercise = root.AddComponent<Exercise>();
        exercise.number = 1234;
        exercise.ratio = 0.25f;
        exercise.text = "serialized text";
        exercise.point = new Vector3(1.5f, -2f, 3f);
        exercise.tint = new Color32(10, 20, 30, 255);
        exercise.pair = new Pair { a = 7, b = 1.5f };
        exercise.nested = new Nested { name = "nested", values = new[] { 1, 2, 3 }, pair = new Pair { a = -1, b = 0.5f } };
        exercise.list = new System.Collections.Generic.List<int> { 5, 8, 13 };
        exercise.marker = marker;
        exercise.SetPrivate(200, 6.25);

        EditorSceneManager.SaveScene(scene, scenePath);
    }
}
