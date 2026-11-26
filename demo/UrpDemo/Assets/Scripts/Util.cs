using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Xorshift generator: the same sequence on every backend, unlike anything seeded by the engine.</summary>
public struct Rng
{
    uint _state;

    public Rng(uint seed) => _state = seed != 0 ? seed : 0x9E3779B9u;

    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float Float() => (NextUInt() >> 8) * (1f / 16777216f);

    public float Range(float min, float max) => min + (max - min) * Float();

    public int Range(int min, int max) => min + (int)(NextUInt() % (uint)(max - min));

    public Vector3 InsideUnitSphere()
    {
        for (;;)
        {
            var v = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
            if (v.sqrMagnitude <= 1f) return v;
        }
    }

    public Vector3 OnUnitSphere()
    {
        for (;;)
        {
            Vector3 v = InsideUnitSphere();
            float length = v.magnitude;
            if (length > 0.05f) return v / length;
        }
    }
}

/// <summary>
/// Sine and cosine from additions and multiplications alone. The C runtime's versions differ in the last bit from one
/// scripting backend to the next, which is enough to change a checksum; these give every backend the same bits.
/// </summary>
public static class Trig
{
    const float Pi = 3.14159274f, TwoPi = 6.28318548f, HalfPi = 1.57079637f;

    public static void SinCos(float angle, out float sin, out float cos)
    {
        // Bring the angle into [-pi, pi], then fold it into [-pi/2, pi/2] where the series converges fast.
        float turns = angle * (1f / TwoPi);
        turns -= (float)Math.Floor(turns + 0.5);
        float x = turns * TwoPi;
        float sign = 1f;
        if (x > HalfPi)
        {
            x = Pi - x;
            sign = -1f;
        }
        else if (x < -HalfPi)
        {
            x = -Pi - x;
            sign = -1f;
        }

        float x2 = x * x;
        sin = x * (1f + x2 * (-1f / 6f + x2 * (1f / 120f + x2 * (-1f / 5040f + x2 * (1f / 362880f + x2 * (-1f / 39916800f))))));
        cos = sign * (1f + x2 * (-0.5f + x2 * (1f / 24f + x2 * (-1f / 720f + x2 * (1f / 40320f + x2 * (-1f / 3628800f + x2 * (1f / 479001600f)))))));
    }

    public static float Sin(float angle)
    {
        SinCos(angle, out float sin, out _);
        return sin;
    }

    public static float Cos(float angle)
    {
        SinCos(angle, out _, out float cos);
        return cos;
    }
}

/// <summary>FNV-1a over 32-bit words. Floats are hashed by their bits, so any difference at all shows.</summary>
public struct Hash64
{
    ulong _hash;

    public static Hash64 Create() => new Hash64 { _hash = 14695981039346656037UL };

    public void Add(int value) => _hash = (_hash ^ (uint)value) * 1099511628211UL;

    public void Add(float value) => Add(BitConverter.SingleToInt32Bits(value));

    public void Add(Vector3 value)
    {
        Add(value.x);
        Add(value.y);
        Add(value.z);
    }

    public ulong Value => _hash;
}

/// <summary>The few meshes the demo draws, built in code so the project carries no model files.</summary>
public static class Meshes
{
    public static Mesh Cube() => Convex("Cube",
        new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        },
        new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 2, 6, 1, 6, 5 });

    /// <summary>A four-sided dart pointing along +Z.</summary>
    public static Mesh Dart() => Convex("Dart",
        new[]
        {
            new Vector3(-0.5f, -0.3f, -0.5f), new Vector3(0.5f, -0.3f, -0.5f), new Vector3(0.5f, 0.3f, -0.5f), new Vector3(-0.5f, 0.3f, -0.5f),
            new Vector3(0f, 0f, 0.5f),
        },
        new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4, 0, 1, 2, 0, 2, 3 });

    public static Mesh Diamond() => Convex("Diamond",
        new[]
        {
            new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 0f), new Vector3(0f, 0.5f, 0f),
            new Vector3(0f, -0.5f, 0f), new Vector3(0f, 0f, 0.5f), new Vector3(0f, 0f, -0.5f),
        },
        new[] { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 2, 4, 1, 4, 3, 1, 3, 5, 1, 5, 2 });

    /// <summary>A 16:9 rectangle in the XY plane with texture coordinates.</summary>
    public static Mesh Screen(float width, float height)
    {
        float x = width * 0.5f, y = height * 0.5f;
        var mesh = new Mesh { name = "Screen" };
        mesh.SetVertices(new[] { new Vector3(-x, -y, 0f), new Vector3(-x, y, 0f), new Vector3(x, y, 0f), new Vector3(x, -y, 0f) });
        mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// A flat-shaded mesh from the faces of a convex shape that contains the origin. Each face is turned to point
    /// away from the origin, so the faces can be listed in any winding.
    /// </summary>
    static Mesh Convex(string name, Vector3[] points, int[] faces)
    {
        var vertices = new Vector3[faces.Length];
        var normals = new Vector3[faces.Length];
        var triangles = new int[faces.Length];
        for (int i = 0; i < faces.Length; i += 3)
        {
            Vector3 a = points[faces[i]], b = points[faces[i + 1]], c = points[faces[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, a + b + c) < 0f)
            {
                Vector3 swap = b;
                b = c;
                c = swap;
                normal = -normal;
            }
            vertices[i] = a;
            vertices[i + 1] = b;
            vertices[i + 2] = c;
            normals[i] = normals[i + 1] = normals[i + 2] = normal;
            triangles[i] = i;
            triangles[i + 1] = i + 1;
            triangles[i + 2] = i + 2;
        }

        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

/// <summary>
/// Collects instances of one mesh and draws them with GPU instancing, a full batch at a time. The colour of each
/// instance goes along in a property block; its alpha is how strongly the instance glows.
/// </summary>
public sealed class InstanceBatcher
{
    public const int BatchSize = 1023;

    static readonly int ColorId = Shader.PropertyToID("_InstanceColor");

    readonly Matrix4x4[] _matrices = new Matrix4x4[BatchSize];
    readonly Vector4[] _colors = new Vector4[BatchSize];
    readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    RenderParams _params;
    Mesh _mesh;
    int _count;

    /// <summary>Instances handed to the engine since the last call to <see cref="ResetCounters"/>.</summary>
    public int Instances { get; private set; }

    public InstanceBatcher(Material material)
    {
        _params = new RenderParams(material)
        {
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 2000f),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            matProps = _block,
        };
    }

    public void ResetCounters() => Instances = 0;

    public void Begin(Mesh mesh)
    {
        Flush();
        _mesh = mesh;
    }

    public void Add(in Matrix4x4 matrix, Vector4 color)
    {
        _matrices[_count] = matrix;
        _colors[_count] = color;
        if (++_count == BatchSize) Flush();
    }

    public void Flush()
    {
        if (_count == 0) return;
        _block.SetVectorArray(ColorId, _colors);
        Graphics.RenderMeshInstanced(_params, _mesh, 0, _matrices, _count);
        Instances += _count;
        _count = 0;
    }

    /// <summary>A matrix that moves to <paramref name="position"/> and scales uniformly, without calling the engine.</summary>
    public static Matrix4x4 Place(Vector3 position, float scale)
    {
        Matrix4x4 m = default;
        m.m00 = scale;
        m.m11 = scale;
        m.m22 = scale;
        m.m03 = position.x;
        m.m13 = position.y;
        m.m23 = position.z;
        m.m33 = 1f;
        return m;
    }

    /// <summary>A matrix whose Z axis points along <paramref name="forward"/> (unit length), without calling the engine.</summary>
    public static Matrix4x4 Aim(Vector3 position, Vector3 forward, float width, float length)
    {
        // right = up x forward with up = +Y, then up = forward x right.
        var right = new Vector3(forward.z, 0f, -forward.x);
        float rightLength = Mathf.Sqrt(right.x * right.x + right.z * right.z);
        right = rightLength < 1e-4f ? Vector3.right : right * (1f / rightLength);
        Vector3 up = Vector3.Cross(forward, right);

        Matrix4x4 m = default;
        m.m00 = right.x * width;
        m.m10 = right.y * width;
        m.m20 = right.z * width;
        m.m01 = up.x * width;
        m.m11 = up.y * width;
        m.m21 = up.z * width;
        m.m02 = forward.x * length;
        m.m12 = forward.y * length;
        m.m22 = forward.z * length;
        m.m03 = position.x;
        m.m13 = position.y;
        m.m23 = position.z;
        m.m33 = 1f;
        return m;
    }
}
