using UnityEngine;

/// <summary>The overlay: which backend, which section, and how long a frame takes. Off during benchmarks unless -hud
/// is given, because OnGUI is script work of its own.</summary>
public sealed class Hud : MonoBehaviour
{
    Demo _demo;
    GUIStyle _title, _text;
    double _smoothFrame, _smoothStep, _smoothScript;

    void Awake() => _demo = GetComponent<Demo>();

    void Update()
    {
        _smoothFrame = Smooth(_smoothFrame, _demo.FrameMs);
        _smoothStep = Smooth(_smoothStep, _demo.StepMs);
        _smoothScript = Smooth(_smoothScript, _demo.ScriptMs);
    }

    static double Smooth(double current, double sample) => current <= 0 ? sample : current + (sample - current) * 0.05;

    void OnGUI()
    {
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _title.normal.textColor = Color.white;
            _text.normal.textColor = new Color(0.85f, 0.9f, 1f);
        }

        Section section = _demo.Current;
        if (section == null) return;

        GUI.Box(new Rect(10, 10, 520, 112), GUIContent.none);
        GUI.Label(new Rect(22, 14, 500, 26), _demo.Backend + "  |  " + _demo.SectionNumber + "/" + _demo.SectionCount + "  " + section.Name, _title);
        GUI.Label(new Rect(22, 42, 500, 22), section.Detail, _text);
        GUI.Label(new Rect(22, 62, 500, 22), "stresses " + section.Stresses, _text);

        double fps = _smoothFrame > 0 ? 1000.0 / _smoothFrame : 0;
        GUI.Label(new Rect(22, 84, 500, 22),
            fps.ToString("F0") + " fps   frame " + _smoothFrame.ToString("F2") + " ms   step " + _smoothStep.ToString("F2") + " ms   scripts " + _smoothScript.ToString("F2") + " ms",
            _text);

        if (!_demo.Benchmark)
            GUI.Label(new Rect(22, Screen.height - 34, 900, 22), "1-6 pick a section   arrows step   space " + (_demo.Holding ? "resumes the tour" : "holds this one") + "   U " + (Application.targetFrameRate == 60 ? "lifts" : "restores") + " the frame limit   Esc quits", _text);
    }
}
