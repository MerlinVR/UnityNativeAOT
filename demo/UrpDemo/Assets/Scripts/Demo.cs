using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Runs the sections one after another.
///
/// Without arguments it is a slideshow at sixty frames a second: each section plays for a while, 1 to 6 pick one,
/// space holds the current one, U lifts the frame limit, Escape quits. With -benchmark it plays each section for a
/// fixed number of frames, measures them, writes the numbers to the log (and to a JSON file with -results) and quits.
///
///   -benchmark             measure and quit
///   -results FILE          write the measurements as JSON
///   -frames N              measured frames per section (600)
///   -warmup N              frames before measuring starts (120)
///   -scale X               multiplies every section's size (1)
///   -sections a,b,c        only these sections: boids, tree, julia, julia-mt, swarm, fireworks
///   -screenshots DIR       save one PNG per section
///   -label TEXT            name for this run in the results (the scripting backend by default)
///   -hud                   show the overlay in benchmark mode too
///   -seconds X             how long the slideshow stays on a section (12)
///   -once                  quit when the slideshow has shown every section
///   -launched FILETIME     when the process was started (UTC file time), to report the time to the first frame
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class Demo : MonoBehaviour
{
    public const float FixedStep = 1f / 60f;
    public const float ScreenWidth = 16f, ScreenHeight = 9f;

    // Assigned by DemoBuilder when it writes the scene.
    public Material instancedMaterial;
    public Material screenMaterial;
    public Camera viewCamera;

    public Mesh Cube { get; private set; }
    public Mesh Dart { get; private set; }
    public Mesh Diamond { get; private set; }
    public Mesh ScreenQuad { get; private set; }
    public InstanceBatcher Batcher { get; private set; }

    // Read by the HUD.
    public string Backend { get; private set; }
    public bool Benchmark => _options.benchmark;
    public bool Holding { get; private set; }
    public Section Current { get; private set; }
    public int SectionNumber => _index + 1;
    public int SectionCount => _sections.Length;
    public int Frame { get; private set; }
    public double FrameMs { get; private set; }
    public double StepMs { get; private set; }
    public double ScriptMs { get; private set; }

    sealed class Options
    {
        public bool benchmark, hud, once;
        public string results, screenshots, label;
        public int frames = 600, warmup = 120;
        public float scale = 1f, seconds = 12f;
        public string[] sections;
        public long launched;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-benchmark": o.benchmark = true; break;
                    case "-hud": o.hud = true; break;
                    case "-once": o.once = true; break;
                    case "-results": o.results = next; i++; break;
                    case "-screenshots": o.screenshots = next; i++; break;
                    case "-label": o.label = next; i++; break;
                    case "-frames": o.frames = Math.Max(1, int.Parse(next, CultureInfo.InvariantCulture)); i++; break;
                    case "-warmup": o.warmup = Math.Max(1, int.Parse(next, CultureInfo.InvariantCulture)); i++; break;
                    case "-scale": o.scale = float.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "-seconds": o.seconds = float.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "-sections": o.sections = next.Split(','); i++; break;
                    case "-launched": o.launched = long.Parse(next, CultureInfo.InvariantCulture); i++; break;
                }
            }
            return o;
        }
    }

    Options _options;
    Section[] _sections;
    int _index = -1;
    float _sectionTime;
    bool _failed;

    // Timestamps of the current frame.
    long _frameStart, _stepStart;
    bool _measuredFrame, _finishPending;

    // Measurements of the current section.
    readonly Samples _frameSamples = new Samples(), _stepSamples = new Samples(), _submitSamples = new Samples(), _scriptSamples = new Samples();
    double _firstStepMs;
    int _gen0AtStart, _fullAtStart;
    long _allocatedAtStart;
    string _error;

    RunResult _run;
    double _startupSeconds = -1;

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static string DetectBackend()
    {
#if ENABLE_IL2CPP
        return "IL2CPP";
#else
        if (Type.GetType("Mono.Runtime") != null) return "Mono";
        return RuntimeFeature.IsDynamicCodeSupported ? "CoreCLR" : "NativeAOT";
#endif
    }

    void Awake()
    {
        _options = Options.Parse(Environment.GetCommandLineArgs());
        Backend = DetectBackend();

        // The log is read by scripts; keep each message to one line.
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        // Every frame advances the simulation by the same step: as many frames as possible for a benchmark, sixty a
        // second for the slideshow so that it plays in real time.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = _options.benchmark ? -1 : 60;
        Application.runInBackground = true;

        Cube = Meshes.Cube();
        Dart = Meshes.Dart();
        Diamond = Meshes.Diamond();
        ScreenQuad = Meshes.Screen(ScreenWidth, ScreenHeight);
        Batcher = new InstanceBatcher(instancedMaterial);

        var all = new Section[] { new Boids(), new FractalTree(), new JuliaSet(false), new JuliaSet(true), new Swarm(), new Fireworks() };
        if (_options.sections == null) _sections = all;
        else
        {
            var chosen = new List<Section>();
            foreach (string name in _options.sections)
            {
                Section match = Array.Find(all, section => section.Name == name.Trim());
                if (match != null) chosen.Add(match);
                else Debug.LogWarning("DEMO unknown section '" + name + "'");
            }
            _sections = chosen.Count > 0 ? chosen.ToArray() : all;
        }

        if (_options.screenshots != null) Directory.CreateDirectory(_options.screenshots);

        var hud = GetComponent<Hud>();
        if (hud != null) hud.enabled = !_options.benchmark || _options.hud;

        _run = new RunResult
        {
            backend = Backend,
            label = _options.label ?? Backend,
            runtime = RuntimeInformation.FrameworkDescription,
            unity = Application.unityVersion,
            graphics = SystemInfo.graphicsDeviceType.ToString(),
            processors = SystemInfo.processorCount,
            frames = _options.frames,
            warmup = _options.warmup,
            scale = _options.scale,
        };

        Debug.Log(FormattableString.Invariant(
            $"DEMO start backend={Backend} runtime={_run.runtime} unity={_run.unity} graphics={_run.graphics} benchmark={_options.benchmark} frames={_options.frames} warmup={_options.warmup} scale={_options.scale}"));
    }

    void Update()
    {
        long now = Stopwatch.GetTimestamp();
        if (_frameStart != 0)
        {
            FrameMs = Ms(now - _frameStart);
            if (_measuredFrame) _frameSamples.Add(FrameMs);
        }
        _frameStart = now;
        _measuredFrame = false;

        if (_startupSeconds < 0)
        {
            // The engine's clock starts some way into start-up; the launcher's timestamp covers all of it.
            _startupSeconds = _options.launched != 0 ? (DateTime.UtcNow.ToFileTimeUtc() - _options.launched) / 1e7 : Time.realtimeSinceStartupAsDouble;
            _run.width = Screen.width;
            _run.height = Screen.height;
            Debug.Log(FormattableString.Invariant($"DEMO first-frame after={_startupSeconds:F3}s size={_run.width}x{_run.height}"));
        }

        if (_finishPending)
        {
            _finishPending = false;
            FinishSection();
        }
        if (Current == null && !Begin(_index + 1)) return;
        if (!_options.benchmark) ReadKeys();
        if (Current == null) return;

        if (_options.benchmark && Frame == _options.warmup)
        {
            _gen0AtStart = GC.CollectionCount(0);
            _fullAtStart = GC.CollectionCount(GC.MaxGeneration);
            _allocatedAtStart = Allocated();
        }

        Batcher.ResetCounters();
        _stepStart = Stopwatch.GetTimestamp();
        long afterStep, afterRender;
        try
        {
            Current.Step(FixedStep);
            afterStep = Stopwatch.GetTimestamp();
            Current.Render(this);
            afterRender = Stopwatch.GetTimestamp();
            Current.PlaceCamera(viewCamera.transform, _sectionTime);
        }
        catch (Exception e)
        {
            Fail(e);
            return;
        }

        StepMs = Ms(afterStep - _stepStart);
        if (Frame == 0) _firstStepMs = StepMs;
        if (_options.benchmark && Frame >= _options.warmup)
        {
            _measuredFrame = true;
            _stepSamples.Add(StepMs);
            _submitSamples.Add(Ms(afterRender - afterStep));
        }

        if (_options.screenshots != null && Frame == ScreenshotFrame)
            ScreenCapture.CaptureScreenshot(Path.Combine(_options.screenshots, Current.Name + ".png"));
    }

    int ScreenshotFrame => _options.benchmark ? Math.Max(0, _options.warmup - 10) : 240;

    // Runs after every other script's Update, so the span from the start of the step to here is the frame's script time.
    void LateUpdate()
    {
        if (Current == null) return;
        ScriptMs = Ms(Stopwatch.GetTimestamp() - _stepStart);
        if (_measuredFrame) _scriptSamples.Add(ScriptMs);

        Frame++;
        _sectionTime += FixedStep;

        if (_options.benchmark)
        {
            // The section ends at the start of the next frame, so that this frame's full length is recorded first.
            if (Frame == _options.warmup + _options.frames) _finishPending = true;
        }
        else if (!Holding && _sectionTime >= _options.seconds)
        {
            if (_options.once && _index == _sections.Length - 1)
            {
                Debug.Log("DEMO done ok=" + !_failed + " slideshow");
                enabled = false;
                Application.Quit(_failed ? 2 : 0);
            }
            else Switch((_index + 1) % _sections.Length);
        }
    }

    void ReadKeys()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        if (Input.GetKeyDown(KeyCode.Space)) Holding = !Holding;
        if (Input.GetKeyDown(KeyCode.U)) Application.targetFrameRate = Application.targetFrameRate == 60 ? -1 : 60;
        if (Input.GetKeyDown(KeyCode.RightArrow)) Switch((_index + 1) % _sections.Length);
        if (Input.GetKeyDown(KeyCode.LeftArrow)) Switch((_index + _sections.Length - 1) % _sections.Length);
        for (int i = 0; i < _sections.Length && i < 9; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                Holding = true;
                Switch(i);
            }
        }
    }

    void Switch(int index)
    {
        if (Current != null) Current.End();
        Current = null;
        Begin(index);
    }

    /// <summary>Starts section <paramref name="index"/>. Returns false when there is none left and the run is over.</summary>
    bool Begin(int index)
    {
        if (index >= _sections.Length)
        {
            if (_options.benchmark) Finish();
            return false;
        }

        _index = index;
        Frame = 0;
        _sectionTime = 0f;
        _error = null;
        _frameSamples.Clear();
        _stepSamples.Clear();
        _submitSamples.Clear();
        _scriptSamples.Clear();

        Section section = _sections[index];
        try
        {
            section.Begin(this, _options.scale);
            Current = section;
        }
        catch (Exception e)
        {
            Current = section;
            Fail(e);
            return Current != null;
        }
        Debug.Log("DEMO begin " + section.Name + ": " + section.Detail);
        return true;
    }

    // A section that throws is reported and skipped, so one failure does not hide the results of the others.
    void Fail(Exception e)
    {
        _failed = true;
        _error = e.GetType().Name + ": " + e.Message;
        Debug.LogError("DEMO error " + Current.Name + " frame=" + Frame + " " + e);
        if (_options.benchmark) FinishSection();
        else Switch((_index + 1) % _sections.Length);
    }

    static long Allocated()
    {
        try { return GC.GetAllocatedBytesForCurrentThread(); }
        catch (Exception) { return 0; }
    }

    void FinishSection()
    {
        Section section = Current;
        var result = new SectionResult { name = section.Name, ok = _error == null, error = _error ?? "", frames = _frameSamples.Count };

        try
        {
            result.detail = section.Detail;
            result.stresses = section.Stresses;
            if (_error == null) result.checksum = section.Checksum().ToString("x16");
        }
        catch (Exception e)
        {
            result.ok = false;
            result.error = e.GetType().Name + ": " + e.Message;
        }

        Summary frame = _frameSamples.Summarise(), step = _stepSamples.Summarise(), submit = _submitSamples.Summarise(), script = _scriptSamples.Summarise();
        result.frameAvg = frame.average;
        result.frameMedian = frame.median;
        result.frameP95 = frame.p95;
        result.frameP99 = frame.p99;
        result.frameMax = frame.max;
        result.stepAvg = step.average;
        result.stepMedian = step.median;
        result.stepP99 = step.p99;
        result.stepMax = step.max;
        result.stepFirst = _firstStepMs;
        result.submitAvg = submit.average;
        result.scriptAvg = script.average;
        result.scriptMedian = script.median;
        result.gen0 = GC.CollectionCount(0) - _gen0AtStart;
        result.fullGc = GC.CollectionCount(GC.MaxGeneration) - _fullAtStart;
        result.allocatedMb = (Allocated() - _allocatedAtStart) / (1024.0 * 1024.0);
        result.heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        _run.sections.Add(result);

        Debug.Log(FormattableString.Invariant(
            $"DEMO result {result.name} ok={result.ok} frames={result.frames} frame_ms avg={result.frameAvg:F3} median={result.frameMedian:F3} p99={result.frameP99:F3} max={result.frameMax:F3} | step_ms avg={result.stepAvg:F3} median={result.stepMedian:F3} first={result.stepFirst:F3} | submit_ms avg={result.submitAvg:F3} | script_ms avg={result.scriptAvg:F3} | gen0={result.gen0} full={result.fullGc} allocated_mb={result.allocatedMb:F1} heap_mb={result.heapMb:F1} | checksum={result.checksum} | {result.detail}"));

        try { section.End(); }
        catch (Exception e) { Debug.LogError("DEMO error ending " + section.Name + " " + e); }
        Current = null;
    }

    void Finish()
    {
        _run.startupSeconds = _startupSeconds;
        _run.totalSeconds = Time.realtimeSinceStartupAsDouble;
        if (!string.IsNullOrEmpty(_options.results))
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(_options.results));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_options.results, JsonUtility.ToJson(_run, true));
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogError("DEMO error writing results " + e);
            }
        }
        Debug.Log(FormattableString.Invariant($"DEMO done ok={!_failed} sections={_run.sections.Count} total={_run.totalSeconds:F2}s"));
        enabled = false;
        Application.Quit(_failed ? 2 : 0);
    }
}
