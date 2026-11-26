using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Fireworks written the way gameplay code often is: every particle is its own object behind a virtual Update, dead
/// ones are dropped for the collector, and each step runs a few LINQ queries and builds a string. Thousands of short-
/// lived objects per step make this a test of the allocator and the garbage collector as much as of the code.
/// </summary>
public sealed class Fireworks : Section
{
    static readonly Color[] Palette =
    {
        new Color(1f, 0.25f, 0.2f), new Color(1f, 0.6f, 0.1f), new Color(1f, 0.95f, 0.3f), new Color(0.3f, 1f, 0.35f),
        new Color(0.2f, 0.9f, 1f), new Color(0.3f, 0.45f, 1f), new Color(0.8f, 0.3f, 1f), new Color(1f, 0.4f, 0.75f),
    };

    static readonly Vector3 Gravity = new Vector3(0f, -9.8f, 0f);

    abstract class Particle
    {
        public Vector3 position, velocity;
        public float age, life, size;
        public int color, id;
        public bool alive = true;

        public float Fade => 1f - age / life;

        public abstract float Glow { get; }

        /// <summary>Returns false once the particle has burnt out.</summary>
        public virtual bool Update(float dt, Fireworks world)
        {
            velocity += Gravity * dt;
            position += velocity * dt;
            age += dt;
            return age < life;
        }
    }

    /// <summary>The rocket: climbs until its fuse runs out, then bursts.</summary>
    sealed class Shell : Particle
    {
        public override float Glow => 2f;

        public override bool Update(float dt, Fireworks world)
        {
            if (base.Update(dt, world)) return true;
            world.Burst(this);
            return false;
        }
    }

    /// <summary>A piece of a burst. Slows in the air and sheds a trail.</summary>
    sealed class Spark : Particle
    {
        public float intensity;
        int _steps;

        public override float Glow => Fade * intensity;

        public override bool Update(float dt, Fireworks world)
        {
            velocity *= 0.985f;
            if (++_steps % 3 == 0) world.Spawn(new Trail { position = position, life = 0.35f, size = size * 0.55f, color = color });
            return base.Update(dt, world);
        }
    }

    /// <summary>Glowing dust left behind by a spark. Does not move.</summary>
    sealed class Trail : Particle
    {
        public override float Glow => Fade * 0.5f;

        public override bool Update(float dt, Fireworks world)
        {
            age += dt;
            return age < life;
        }
    }

    readonly List<Particle> _particles = new List<Particle>();
    List<Particle> _born;
    Rng _rng;
    int _frame, _nextId, _launchInterval, _sparksPerBurst;
    Vector3 _focus;

    /// <summary>Counts per colour, rebuilt every step.</summary>
    public string Status { get; private set; } = "";

    public override string Name => "fireworks";

    public override string Detail => _particles.Count + " particle objects";

    public override string Stresses => "allocation, garbage collection, virtual calls, LINQ";

    public override void Begin(Demo demo, float scale)
    {
        _particles.Clear();
        _rng = new Rng(777);
        _frame = 0;
        _nextId = 0;
        _launchInterval = 6;
        _sparksPerBurst = Mathf.Max(8, Mathf.RoundToInt(320 * scale));
        _focus = new Vector3(0f, 24f, 0f);
    }

    void Spawn(Particle particle)
    {
        particle.id = _nextId++;
        _born.Add(particle);
    }

    void Launch()
    {
        Vector3 ground = _rng.InsideUnitSphere() * 18f;
        ground.y = 0f;
        Spawn(new Shell
        {
            position = ground,
            velocity = new Vector3(_rng.Range(-2.5f, 2.5f), _rng.Range(24f, 31f), _rng.Range(-2.5f, 2.5f)),
            life = _rng.Range(1.2f, 1.8f),
            size = 0.8f,
            color = _rng.Range(0, Palette.Length),
        });
    }

    void Burst(Shell shell)
    {
        float power = _rng.Range(7f, 13f);
        for (int i = 0; i < _sparksPerBurst; i++)
        {
            Spawn(new Spark
            {
                position = shell.position,
                velocity = shell.velocity * 0.3f + _rng.OnUnitSphere() * (power * _rng.Range(0.6f, 1f)),
                life = _rng.Range(1.6f, 2.6f),
                size = _rng.Range(0.3f, 0.5f),
                // Most sparks take the shell's colour, a few differ.
                color = _rng.Float() < 0.85f ? shell.color : _rng.Range(0, Palette.Length),
                intensity = _rng.Range(0.7f, 1.5f),
            });
        }
    }

    public override void Step(float dt)
    {
        _born = new List<Particle>();
        if (_frame++ % _launchInterval == 0) Launch();

        foreach (Particle particle in _particles) particle.alive = particle.Update(dt, this);
        _particles.RemoveAll(particle => !particle.alive);
        _particles.AddRange(_born);

        // The camera follows the brightest sparks.
        List<Particle> brightest = _particles
            .Where(particle => particle is Spark)
            .OrderByDescending(particle => particle.Glow)
            .ThenBy(particle => particle.id)
            .Take(6)
            .ToList();
        if (brightest.Count > 0)
        {
            Vector3 centre = brightest.Aggregate(Vector3.zero, (sum, particle) => sum + particle.position) / brightest.Count;
            _focus = Vector3.Lerp(_focus, centre, 0.02f);
        }

        Dictionary<int, int> perColor = _particles.GroupBy(particle => particle.color).ToDictionary(group => group.Key, group => group.Count());
        Status = string.Join(" ", perColor.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value));
    }

    public override void Render(Demo demo)
    {
        InstanceBatcher batcher = demo.Batcher;
        batcher.Begin(demo.Diamond);
        foreach (Particle particle in _particles)
        {
            Color color = Palette[particle.color];
            float fade = particle.Fade;
            batcher.Add(InstanceBatcher.Place(particle.position, particle.size * (0.4f + 0.6f * fade)),
                new Vector4(color.r * fade, color.g * fade, color.b * fade, particle.Glow));
        }
        batcher.Flush();
    }

    public override void PlaceCamera(Transform camera, float time)
    {
        float angle = time * 0.08f;
        camera.position = new Vector3(Mathf.Sin(angle) * 48f, 12f, -Mathf.Cos(angle) * 48f);
        camera.LookAt(new Vector3(_focus.x * 0.3f, 12f + _focus.y * 0.45f, _focus.z * 0.3f));
    }

    public override ulong Checksum()
    {
        Hash64 hash = Hash64.Create();
        hash.Add(_particles.Count);
        foreach (Particle particle in _particles) hash.Add(particle.position);
        return hash.Value;
    }

    public override void End() => _particles.Clear();
}
