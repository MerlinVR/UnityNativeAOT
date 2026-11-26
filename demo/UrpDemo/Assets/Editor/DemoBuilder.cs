using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Creates the render pipeline assets, the materials and the scene from code, and builds one player per scripting
// backend into ..\builds\<backend>. Batch mode:
//
//   Unity.exe -batchmode -quit -projectPath demo\UrpDemo -executeMethod DemoBuilder.BuildCoreCLR
public static class DemoBuilder
{
    const string Settings = "Assets/Settings";
    const string ScenePath = "Assets/Demo.unity";
    const string Urp = "Packages/com.unity.render-pipelines.universal";

    public static void BuildMono() => Build(ScriptingImplementation.Mono2x, "mono");

    public static void BuildIL2CPP() => Build(ScriptingImplementation.IL2CPP, "il2cpp");

    public static void BuildCoreCLR() => Build(ScriptingImplementation.CoreCLR, "coreclr");

    static void Build(ScriptingImplementation backend, string name)
    {
        Setup();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);

        string outDir = Path.GetFullPath(Path.Combine("..", "builds", name));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = Path.Combine(outDir, "UrpDemo.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });

        Debug.Log($"DEMO BUILD {name}: {report.summary.result}, errors={report.summary.totalErrors}, seconds={report.summary.totalTime.TotalSeconds:F0}, output={outDir}");
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    [MenuItem("Demo/Set up project")]
    public static void Setup()
    {
        Directory.CreateDirectory(Settings);

        PlayerSettings.companyName = "UnityNativeAOT";
        PlayerSettings.productName = "UrpDemo";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        // A benchmark must not stall when its window loses focus.
        PlayerSettings.runInBackground = true;
        PlayerSettings.SplashScreen.show = false;
        // The same compiler settings a release build would use.
        PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);

        UniversalRenderPipelineAsset pipeline = LoadPipeline();
        GraphicsSettings.defaultRenderPipeline = pipeline;
        for (int level = 0; level < QualitySettings.names.Length; level++)
        {
            QualitySettings.SetQualityLevel(level, false);
            QualitySettings.renderPipeline = null;
            QualitySettings.vSyncCount = 0;
        }

        Material instanced = LoadMaterial("Instanced", "Assets/Shaders/InstancedColor.shader");
        instanced.enableInstancing = true;
        EditorUtility.SetDirty(instanced);
        Material screen = LoadMaterial("Screen", "Assets/Shaders/Screen.shader");
        VolumeProfile profile = LoadPostProcessing();
        AssetDatabase.SaveAssets();

        WriteScene(instanced, screen, profile);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
    }

    static UniversalRenderPipelineAsset LoadPipeline()
    {
        const string rendererPath = Settings + "/Renderer.asset", pipelinePath = Settings + "/Pipeline.asset";

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        if (renderer == null)
        {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(Urp + "/Runtime/Data/PostProcessData.asset");
            AssetDatabase.CreateAsset(renderer, rendererPath);
            ResourceReloader.ReloadAllNullIn(renderer, Urp);
        }

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, pipelinePath);
        }

        // HDR so that glowing instances exceed 1 and bloom; nothing in the demo casts shadows.
        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 1;
        pipeline.shadowDistance = 0f;
        EditorUtility.SetDirty(renderer);
        EditorUtility.SetDirty(pipeline);
        return pipeline;
    }

    static Material LoadMaterial(string name, string shaderPath)
    {
        string path = Settings + "/" + name + ".mat";
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader) material.shader = shader;
        return material;
    }

    static VolumeProfile LoadPostProcessing()
    {
        const string path = Settings + "/PostProcessing.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile != null) return profile;

        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);

        var bloom = profile.Add<Bloom>();
        bloom.threshold.Override(1f);
        bloom.intensity.Override(0.9f);
        bloom.scatter.Override(0.7f);
        AssetDatabase.AddObjectToAsset(bloom, profile);

        var tonemapping = profile.Add<Tonemapping>();
        tonemapping.mode.Override(TonemappingMode.Neutral);
        AssetDatabase.AddObjectToAsset(tonemapping, profile);

        var vignette = profile.Add<Vignette>();
        vignette.intensity.Override(0.28f);
        vignette.smoothness.Override(0.45f);
        AssetDatabase.AddObjectToAsset(vignette, profile);

        EditorUtility.SetDirty(profile);
        return profile;
    }

    // The scene is written again on every build, so it always matches this file.
    static void WriteScene(Material instanced, Material screen, VolumeProfile profile)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.01f, 0.012f, 0.022f);
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 600f;
        camera.allowHDR = true;
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = true;

        var lightObject = new GameObject("Sun");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.color = new Color(1f, 0.97f, 0.92f);
        light.shadows = LightShadows.None;
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

        var volumeObject = new GameObject("Post-processing");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;

        var demoObject = new GameObject("Demo");
        var demo = demoObject.AddComponent<Demo>();
        demo.instancedMaterial = instanced;
        demo.screenMaterial = screen;
        demo.viewCamera = camera;
        demoObject.AddComponent<Hud>();

        EditorSceneManager.SaveScene(scene, ScenePath);
    }
}
