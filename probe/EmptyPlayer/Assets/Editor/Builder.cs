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
        if (!File.Exists(scenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Probe").AddComponent<Probe>();
            EditorSceneManager.SaveScene(scene, scenePath);
        }

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
}
