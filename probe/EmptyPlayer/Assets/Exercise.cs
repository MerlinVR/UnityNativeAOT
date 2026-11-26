using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

[Serializable]
public struct Pair
{
    public int a;
    public float b;
}

[Serializable]
public class Nested
{
    public string name;
    public int[] values;
    public Pair pair;
}

public interface IShape
{
    float Area();
}

public abstract class Shape : IShape
{
    public abstract float Area();
    public virtual string Describe<T>(T tag) => GetType().Name + "<" + typeof(T).Name + ">(" + tag + ")";
}

public sealed class Square : Shape
{
    public float side;
    public override float Area() => side * side;
}

public sealed class Circle : Shape
{
    public float r;
    public override float Area() => 3f * r * r;
    public override string Describe<T>(T tag) => "circle:" + base.Describe(tag);
}

// Exercises the parts of the scripting runtime a real game leans on, one "PROBE x.<name> <result>" line per check, so
// the output of two scripting backends can be compared line by line. Every check is isolated: a failure is printed
// and the rest still run. Quits the player when the sequence ends.
public class Exercise : MonoBehaviour
{
    // Set by Builder when the scene is saved. The engine writes them into the object when it loads the scene.
    public int number;
    public float ratio;
    public string text;
    public Vector3 point;
    public Color32 tint;
    public Pair pair;
    public Nested nested;
    public List<int> list;
    public Marker marker;
    [SerializeField] byte flag;
    [SerializeField] double big;

    static Texture2D s_heldByStatic;

    readonly List<string> _order = new List<string>();
    Texture2D _heldByField;
    GameObject _clone;
    int _frame;
    int _pings;
    bool _later;

    public void SetPrivate(byte flagValue, double bigValue)
    {
        flag = flagValue;
        big = bigValue;
    }

    static void Line(string name, object value) => Debug.Log("PROBE x." + name + " " + value);

    static void Run(string name, Func<object> check)
    {
        try { Line(name, check()); }
        catch (Exception e) { Line(name, "FAILED " + e.GetType().Name + ": " + e.Message); }
    }

    void Awake() => _order.Add("Awake");

    void OnEnable() => _order.Add("OnEnable");

    void Start()
    {
        _order.Add("Start");

        Run("serialized", () => FormattableString.Invariant(
            $"number={number} ratio={ratio} text={text} point={point} tint={tint} pair={pair.a}/{pair.b} flag={flag} big={big}"));
        Run("nested", () => nested == null ? "null" : FormattableString.Invariant(
            $"name={nested.name} values=[{string.Join(",", nested.values ?? new int[0])}] pair={nested.pair.a}/{nested.pair.b}"));
        Run("list", () => list == null ? "null" : string.Join(",", list));
        Run("reference", () => marker == null ? "null" : FormattableString.Invariant(
            $"{marker.name} id={marker.id} label={marker.label} parent={marker.transform.parent.name}"));

        Run("components", () =>
        {
            bool self = ReferenceEquals(GetComponent<Exercise>(), this);
            bool markerOnSelf = TryGetComponent<Marker>(out _);
            return $"transform={GetComponent<Transform>() != null} self={self} behaviours={GetComponents<MonoBehaviour>().Length}"
                + $" markerOnSelf={markerOnSelf} markersInChildren={GetComponentsInChildren<Marker>(true).Length}"
                + $" byType={GetComponent(typeof(Probe)) != null} byName={GetComponent("Probe") != null}";
        });

        Run("transform", () =>
        {
            transform.position = new Vector3(1, 2, 3);
            transform.Rotate(0, 90, 0);
            return FormattableString.Invariant($"position={transform.position} forward={transform.forward} children={transform.childCount}");
        });

        Run("create", () =>
        {
            var made = new GameObject("made");
            var added = made.AddComponent<Marker>();
            added.id = 7;
            added.label = "made at run time";
            made.transform.SetParent(transform, false);
            return $"name={made.name} id={made.GetComponent<Marker>().id} markersInChildren={GetComponentsInChildren<Marker>(true).Length} active={made.activeInHierarchy}";
        });

        Run("instantiate", () =>
        {
            Marker clone = Instantiate(marker);
            _clone = clone.gameObject;
            return $"name={clone.name} id={clone.id} label={clone.label} same={ReferenceEquals(clone, marker)}";
        });

        Run("managed", () =>
        {
            Shape[] shapes = { new Square { side = 2 }, new Circle { r = 1 } };
            float total = shapes.Sum(s => s.Area());
            Dictionary<string, float> byName = shapes.ToDictionary(s => s.GetType().Name, s => s.Area());
            string generic = shapes[0].Describe(5) + " " + shapes[1].Describe("tag") + " " + shapes[1].Describe(new Pair { a = 1 });
            IShape largest = shapes.OrderByDescending(s => s.Area()).First();
            return FormattableString.Invariant(
                $"total={total} keys={string.Join("+", byName.Keys.OrderBy(k => k))} generic={generic} largest={largest.GetType().Name}");
        });

        Run("reflection", () =>
        {
            FieldInfo field = typeof(Exercise).GetField("flag", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo method = typeof(Exercise).GetMethod("OnPing", BindingFlags.Instance | BindingFlags.NonPublic);
            int before = _pings;
            method.Invoke(this, new object[] { 100 });
            return $"field={field.GetValue(this)} invoked={_pings - before} attributes={field.GetCustomAttributes(typeof(SerializeField), false).Length}";
        });

        // Generic instantiations over a value type that appear nowhere else in the program. An ahead-of-time compiler
        // can prepare the first, where both types are constants, but has no code for the second.
        Run("makegeneric.constant", () => Activator.CreateInstance(typeof(List<>).MakeGenericType(typeof(Pair))).GetType().Name);
        Run("makegeneric.computed", () =>
        {
            Type argument = ((object)pair).GetType();
            return Activator.CreateInstance(typeof(Queue<>).MakeGenericType(argument)).GetType().Name;
        });

        Run("thread", () =>
        {
            int sum = 0;
            var worker = new Thread(() =>
            {
                sum = Enumerable.Range(1, 100).Sum();
                Debug.Log("PROBE x.thread.log logged from a second thread");
            });
            worker.Start();
            worker.Join();
            return "sum=" + sum;
        });

        Run("exception", () =>
        {
            try
            {
                ThrowDeep(3);
                return "not thrown";
            }
            catch (InvalidOperationException e)
            {
                return "caught " + e.Message + " traced=" + (e.StackTrace != null && e.StackTrace.Contains("ThrowDeep"));
            }
        });

        Run("sendmessage", () =>
        {
            int before = _pings;
            SendMessage("OnPing", 5);
            SendMessage("OnPing", 6, SendMessageOptions.RequireReceiver);
            return "pings=" + (_pings - before);
        });

        Run("json", () =>
        {
            string json = JsonUtility.ToJson(nested);
            Nested back = JsonUtility.FromJson<Nested>(json);
            return json + " roundtrip=" + (back.name == nested.name && back.values.SequenceEqual(nested.values) && back.pair.b == nested.pair.b);
        });

        Invoke(nameof(Later), 0.01f);
        StartCoroutine(Sequence());
    }

    static void ThrowDeep(int depth)
    {
        if (depth == 0) throw new InvalidOperationException("thrown three frames down");
        ThrowDeep(depth - 1);
    }

    void OnPing(int amount) => _pings += amount;

    void Later() => _later = true;

    void Update()
    {
        _frame++;
        // An exception escaping into the engine: it should be logged and the player should carry on.
        if (_frame == 2) throw new InvalidOperationException("PROBE x.update.exception thrown on purpose in frame 2");
        // By the clock, not by frame count: a batch-mode player runs thousands of frames a second.
        if (Time.realtimeSinceStartup > 30f)
        {
            Line("timeout", "the coroutine never finished");
            Application.Quit(1);
        }
    }

    IEnumerator Sequence()
    {
        int started = _frame;
        yield return null;
        yield return null;
        Line("coroutine.frames", _frame - started);
        yield return new WaitForSeconds(0.05f);
        Line("coroutine.waited", Time.time > 0f);
        yield return StartCoroutine(DestroyClone());
        Line("destroyed", _clone == null);
        Line("order", string.Join(">", _order) + " later=" + _later + " survivedFrame2=" + (_frame > 2));

        // Unloading unused assets asks the scripting runtime which engine objects scripts can still reach. These three
        // are reachable only from managed code: through a field, through a static, and not at all.
        _heldByField = new Texture2D(4, 4) { name = "held by a field" };
        s_heldByStatic = new Texture2D(4, 4) { name = "held by a static" };
        new Texture2D(4, 4).name = "unreferenced";
        AsyncOperation unload = Resources.UnloadUnusedAssets();
        yield return unload;
        Line("unload", "done=" + unload.isDone + " fieldAlive=" + (_heldByField != null) + " staticAlive=" + (s_heldByStatic != null)
            + " unreferencedAlive=" + Resources.FindObjectsOfTypeAll<Texture2D>().Any(t => t.name == "unreferenced"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Line("gc", "collected=" + (GC.CollectionCount(0) > 0) + " markerAlive=" + (marker != null) + " id=" + marker.id);
        Line("done", "ok");
        Application.Quit();
    }

    IEnumerator DestroyClone()
    {
        Destroy(_clone);
        yield return null;
    }
}
