using UnityEngine;

/// <summary>
/// Thousands of GameObjects, each with its own MonoBehaviour whose Update moves its Transform. The work per object is
/// small; what is measured is the engine calling into script once per object and each script calling back into the
/// engine several times.
/// </summary>
public sealed class Swarm : Section
{
    const int RingSize = 100;

    int _count;
    GameObject _root;
    SwarmAgent[] _agents;

    /// <summary>Simulation time. The agents read it in their Update.</summary>
    public float Clock { get; private set; }

    public override string Name => "swarm";

    public override string Detail => _count + " GameObjects with an Update each";

    public override string Stresses => "engine-to-script and script-to-engine calls";

    public override void Begin(Demo demo, float scale)
    {
        _count = Mathf.Max(8, Mathf.RoundToInt(8000 * scale));
        _root = new GameObject("Swarm");
        _agents = new SwarmAgent[_count];
        Clock = 0f;

        var block = new MaterialPropertyBlock();
        int colorId = Shader.PropertyToID("_InstanceColor");
        int rings = (_count + RingSize - 1) / RingSize;

        for (int i = 0; i < _count; i++)
        {
            int ring = i / RingSize;
            int slot = i % RingSize;
            float ringT = rings > 1 ? ring / (float)(rings - 1) : 0f;

            var go = new GameObject("Agent");
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = demo.Cube;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = demo.instancedMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Color color = Color.HSVToRGB(ringT * 0.85f, 0.75f, 1f);
            block.SetVector(colorId, new Vector4(color.r, color.g, color.b, slot % 8 == 0 ? 1.5f : 0.15f));
            renderer.SetPropertyBlock(block);

            SwarmAgent agent = go.AddComponent<SwarmAgent>();
            float radius = 6f + ring * 0.42f;
            agent.Configure(this,
                phase: slot * (Mathf.PI * 2f / RingSize) + ring * 0.37f,
                radius: radius,
                speed: 2.2f / Mathf.Sqrt(radius),
                height: (ringT - 0.5f) * 4f,
                wave: 0.4f + ringT * 1.6f);
            _agents[i] = agent;
        }
    }

    // The agents move themselves; the engine calls their Update after this.
    public override void Step(float dt) => Clock += dt;

    public override void Render(Demo demo) { }

    public override void PlaceCamera(Transform camera, float time) => Orbit(camera, Vector3.zero, 62f, 30f, time * 0.1f);

    public override ulong Checksum()
    {
        Hash64 hash = Hash64.Create();
        for (int i = 0; i < _agents.Length; i++) hash.Add(_agents[i].transform.localPosition);
        return hash.Value;
    }

    public override void End()
    {
        Object.Destroy(_root);
        _agents = null;
    }
}
