using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// A Julia set computed on the CPU every frame and uploaded as a texture. The inner loop is double arithmetic with no
/// calls and no allocation, the kind of code where only the compiler's output matters. It runs either on the main
/// thread or one row at a time through Parallel.For.
/// </summary>
public sealed class JuliaSet : Section
{
    const int MaxIterations = 96;

    // One step of the constant's walk around the origin: cos and sin of 0.004 radians, written out so that no
    // runtime's math library takes part in the result.
    const double StepCos = 0.99999200001066666;
    const double StepSin = 0.0039999893333418667;

    readonly bool _parallel;
    int _width, _height;
    Texture2D _texture;
    Material _material;
    RenderParams _renderParams;
    Color32[] _pixels;
    byte[] _iterations;
    Color32[] _palette;
    double _cx, _cy;
    int _frame;
    System.Action<int> _row;

    public JuliaSet(bool parallel) => _parallel = parallel;

    public override string Name => _parallel ? "julia-mt" : "julia";

    public override string Detail => _width + "x" + _height + " pixels, " + (_parallel ? "Parallel.For over rows" : "one thread");

    public override string Stresses => _parallel ? "the thread pool and delegates around a numeric loop" : "a tight numeric loop";

    public override void Begin(Demo demo, float scale)
    {
        float side = Mathf.Sqrt(Mathf.Max(scale, 0.01f));
        _width = Mathf.Max(64, Mathf.RoundToInt(768 * side) & ~3);
        _height = Mathf.Max(36, Mathf.RoundToInt(432 * side) & ~3);
        _pixels = new Color32[_width * _height];
        _iterations = new byte[_width * _height];
        _cx = 0.7885;
        _cy = 0.0;
        _frame = 0;
        _row = Row;

        _palette = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 256f * Mathf.PI * 2f;
            _palette[i] = new Color32(
                (byte)(127.5f + 127.5f * Mathf.Sin(t)),
                (byte)(127.5f + 127.5f * Mathf.Sin(t + 2.1f)),
                (byte)(127.5f + 127.5f * Mathf.Sin(t + 4.2f)), 255);
        }

        _texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false) { name = "Julia", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        _material = demo.screenMaterial;
        _material.mainTexture = _texture;
        _renderParams = new RenderParams(_material) { worldBounds = new Bounds(Vector3.zero, Vector3.one * 100f) };
    }

    public override void Step(float dt)
    {
        // Rotate the constant: additions and multiplications only, so the picture is the same everywhere.
        double cx = _cx * StepCos - _cy * StepSin;
        _cy = _cx * StepSin + _cy * StepCos;
        _cx = cx;
        _frame++;

        if (_parallel) Parallel.For(0, _height, _row);
        else for (int y = 0; y < _height; y++) Row(y);
    }

    void Row(int y)
    {
        int width = _width;
        double scale = 3.2 / width;
        double cx = _cx, cy = _cy;
        double startY = (y - _height * 0.5) * scale;
        int shift = _frame;
        int offset = y * width;
        Color32[] pixels = _pixels;
        byte[] iterations = _iterations;
        Color32[] palette = _palette;
        var inside = new Color32(4, 4, 10, 255);

        for (int x = 0; x < width; x++)
        {
            double zx = (x - width * 0.5) * scale;
            double zy = startY;
            double xx = zx * zx, yy = zy * zy;
            int n = 0;
            while (n < MaxIterations && xx + yy <= 4.0)
            {
                zy = 2.0 * zx * zy + cy;
                zx = xx - yy + cx;
                xx = zx * zx;
                yy = zy * zy;
                n++;
            }
            iterations[offset + x] = (byte)n;
            pixels[offset + x] = n == MaxIterations ? inside : palette[(n * 6 + shift) & 255];
        }
    }

    public override void Render(Demo demo)
    {
        _texture.SetPixelData(_pixels, 0);
        _texture.Apply(false);
        Graphics.RenderMesh(_renderParams, demo.ScreenQuad, 0, Matrix4x4.identity);
    }

    public override void PlaceCamera(Transform camera, float time)
    {
        // The distance at which the 16 by 9 screen mesh exactly fills a 60 degree field of view.
        camera.position = new Vector3(0f, 0f, -Demo.ScreenHeight * 0.5f / Mathf.Tan(30f * Mathf.Deg2Rad));
        camera.rotation = Quaternion.identity;
    }

    public override ulong Checksum()
    {
        Hash64 hash = Hash64.Create();
        for (int i = 0; i < _iterations.Length; i++) hash.Add(_iterations[i]);
        return hash.Value;
    }

    public override void End()
    {
        if (_material != null) _material.mainTexture = null;
        Object.Destroy(_texture);
    }
}
