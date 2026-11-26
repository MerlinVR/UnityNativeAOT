using UnityEngine;

/// <summary>
/// A fractal of cubes: every part carries five children at half its size, and every part spins. Each step walks the
/// levels from the root out and recomputes each part's world rotation, position and matrix from its parent. That is
/// quaternion and vector operators in managed code plus two calls into the engine per part (Quaternion.Euler and
/// Matrix4x4.TRS).
/// </summary>
public sealed class FractalTree : Section
{
    struct Part
    {
        public Vector3 direction;
        public Quaternion rotation;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public float spinAngle;
    }

    static readonly Vector3[] Directions = { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

    static readonly Quaternion[] Rotations =
    {
        Quaternion.identity, Quaternion.Euler(0f, 0f, -90f), Quaternion.Euler(0f, 0f, 90f),
        Quaternion.Euler(90f, 0f, 0f), Quaternion.Euler(-90f, 0f, 0f),
    };

    int _depth;
    int _total;
    Part[][] _parts;
    Matrix4x4[][] _matrices;
    Vector4[] _levelColor;

    public override string Name => "tree";

    public override string Detail => _total + " parts in " + _depth + " levels";

    public override string Stresses => "quaternion and matrix math, two engine calls per part";

    public override void Begin(Demo demo, float scale)
    {
        // Each level multiplies the work by five, so the scale moves the depth.
        _depth = Mathf.Clamp(8 + Mathf.RoundToInt(Mathf.Log(Mathf.Max(scale, 0.01f), 5f)), 2, 9);
        _parts = new Part[_depth][];
        _matrices = new Matrix4x4[_depth][];
        _levelColor = new Vector4[_depth];
        _total = 0;

        for (int level = 0, length = 1; level < _depth; level++, length *= 5)
        {
            _parts[level] = new Part[length];
            _matrices[level] = new Matrix4x4[length];
            _total += length;

            float t = _depth > 1 ? level / (float)(_depth - 1) : 0f;
            // Orange at the trunk through green to blue at the tips, which glow a little.
            Color color = Color.HSVToRGB(0.07f + 0.5f * t, 0.85f, 1f);
            _levelColor[level] = new Vector4(color.r, color.g, color.b, 0.04f + 0.4f * t * t);
        }

        _parts[0][0] = new Part { direction = Vector3.up, rotation = Quaternion.identity };
        for (int level = 1; level < _depth; level++)
        {
            Part[] parts = _parts[level];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = new Part { direction = Directions[i % 5], rotation = Rotations[i % 5] };
        }
    }

    public override void Step(float dt)
    {
        float spinDelta = 22.5f * dt;

        Part root = _parts[0][0];
        root.spinAngle += spinDelta;
        root.worldRotation = root.rotation * Quaternion.Euler(0f, root.spinAngle, 0f);
        root.worldPosition = Vector3.zero;
        _parts[0][0] = root;
        _matrices[0][0] = Matrix4x4.TRS(root.worldPosition, root.worldRotation, Vector3.one);

        float scale = 1f;
        for (int level = 1; level < _depth; level++)
        {
            scale *= 0.5f;
            Part[] parents = _parts[level - 1];
            Part[] parts = _parts[level];
            Matrix4x4[] matrices = _matrices[level];
            Vector3 size = scale * Vector3.one;
            // Deeper levels spin faster, alternating direction.
            float delta = spinDelta * (1f + 0.35f * level) * ((level & 1) == 0 ? 1f : -1f);

            for (int i = 0; i < parts.Length; i++)
            {
                Part parent = parents[i / 5];
                Part part = parts[i];
                part.spinAngle += delta;
                part.worldRotation = parent.worldRotation * (part.rotation * Quaternion.Euler(0f, part.spinAngle, 0f));
                part.worldPosition = parent.worldPosition + parent.worldRotation * (1.5f * scale * part.direction);
                parts[i] = part;
                matrices[i] = Matrix4x4.TRS(part.worldPosition, part.worldRotation, size);
            }
        }
    }

    public override void Render(Demo demo)
    {
        InstanceBatcher batcher = demo.Batcher;
        batcher.Begin(demo.Cube);
        for (int level = 0; level < _depth; level++)
        {
            Matrix4x4[] matrices = _matrices[level];
            Vector4 color = _levelColor[level];
            for (int i = 0; i < matrices.Length; i++) batcher.Add(matrices[i], color);
        }
        batcher.Flush();
    }

    public override void PlaceCamera(Transform camera, float time) => Orbit(camera, new Vector3(0f, 0.15f, 0f), 3.7f, 1.5f, time * 0.2f);

    public override ulong Checksum()
    {
        Hash64 hash = Hash64.Create();
        Part[] leaves = _parts[_depth - 1];
        for (int i = 0; i < leaves.Length; i++) hash.Add(leaves[i].worldPosition);
        return hash.Value;
    }
}
