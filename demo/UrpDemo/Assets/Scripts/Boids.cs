using UnityEngine;

/// <summary>
/// Flocking: every boid steers by its neighbours (stay close, match heading, keep apart), stays inside a sphere and
/// flees a predator. Neighbours come from a uniform grid rebuilt each step. All of it is plain arrays and Vector3
/// arithmetic in managed code, on one thread.
/// </summary>
public sealed class Boids : Section
{
    const float WorldRadius = 42f;
    const float Perception = 3f;
    const float KeepApart = 1.2f;
    const float MinSpeed = 6f;
    const float MaxSpeed = 12f;
    const float MaxAcceleration = 40f;
    const float Cohesion = 0.9f;
    const float Alignment = 1.6f;
    const float Separation = 9f;
    const float PredatorRadius = 12f;

    // The grid covers a cube around the origin; a cell is as wide as a boid can see.
    const int GridSize = 40;
    const float CellSize = Perception;
    const float GridOrigin = -GridSize * CellSize * 0.5f;

    int _count;
    Vector3[] _position;
    Vector3[] _velocity;
    Vector3[] _nextVelocity;
    int[] _cell;
    int[] _cellStart;
    int[] _cellFill;
    int[] _sorted;
    float _time;
    Vector3 _predator;
    Vector3 _predatorHeading;

    public override string Name => "boids";

    public override string Detail => _count + " boids";

    public override string Stresses => "struct math and array loops in managed code";

    public override void Begin(Demo demo, float scale)
    {
        _count = Mathf.Max(16, Mathf.RoundToInt(10000 * scale));
        _position = new Vector3[_count];
        _velocity = new Vector3[_count];
        _nextVelocity = new Vector3[_count];
        _cell = new int[_count];
        _sorted = new int[_count];
        _cellStart = new int[GridSize * GridSize * GridSize + 1];
        _cellFill = new int[GridSize * GridSize * GridSize];
        _time = 0f;

        var rng = new Rng(12345);
        for (int i = 0; i < _count; i++)
        {
            _position[i] = rng.InsideUnitSphere() * (WorldRadius * 0.9f);
            _velocity[i] = rng.OnUnitSphere() * rng.Range(MinSpeed, MaxSpeed);
        }
        MovePredator();
    }

    static int CellCoordinate(float value)
    {
        int c = (int)((value - GridOrigin) * (1f / CellSize));
        return c < 0 ? 0 : (c >= GridSize ? GridSize - 1 : c);
    }

    void BuildGrid()
    {
        System.Array.Clear(_cellStart, 0, _cellStart.Length);
        for (int i = 0; i < _count; i++)
        {
            Vector3 p = _position[i];
            int cell = (CellCoordinate(p.z) * GridSize + CellCoordinate(p.y)) * GridSize + CellCoordinate(p.x);
            _cell[i] = cell;
            _cellStart[cell + 1]++;
        }
        for (int c = 0; c < _cellFill.Length; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
            _cellFill[c] = _cellStart[c];
        }
        for (int i = 0; i < _count; i++) _sorted[_cellFill[_cell[i]]++] = i;
    }

    void MovePredator()
    {
        var next = new Vector3(Trig.Cos(_time * 0.37f) * 30f, Trig.Sin(_time * 0.53f) * 18f, Trig.Sin(_time * 0.29f) * 30f);
        Vector3 moved = next - _predator;
        if (moved.sqrMagnitude > 1e-8f) _predatorHeading = moved.normalized;
        _predator = next;
    }

    public override void Step(float dt)
    {
        _time += dt;
        MovePredator();
        BuildGrid();

        const float perceptionSq = Perception * Perception;
        const float keepApartSq = KeepApart * KeepApart;
        const float edge = WorldRadius * 0.8f;

        for (int i = 0; i < _count; i++)
        {
            Vector3 p = _position[i];
            Vector3 v = _velocity[i];
            Vector3 centre = Vector3.zero, heading = Vector3.zero, away = Vector3.zero;
            int neighbours = 0;

            int cx = CellCoordinate(p.x), cy = CellCoordinate(p.y), cz = CellCoordinate(p.z);
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if ((uint)z >= GridSize) continue;
                for (int y = cy - 1; y <= cy + 1; y++)
                {
                    if ((uint)y >= GridSize) continue;
                    for (int x = cx - 1; x <= cx + 1; x++)
                    {
                        if ((uint)x >= GridSize) continue;
                        int cell = (z * GridSize + y) * GridSize + x;
                        for (int k = _cellStart[cell], end = _cellStart[cell + 1]; k < end; k++)
                        {
                            int j = _sorted[k];
                            if (j == i) continue;
                            Vector3 other = _position[j];
                            Vector3 offset = other - p;
                            float distanceSq = offset.sqrMagnitude;
                            if (distanceSq > perceptionSq) continue;
                            neighbours++;
                            centre += other;
                            heading += _velocity[j];
                            if (distanceSq < keepApartSq && distanceSq > 1e-6f) away -= offset / distanceSq;
                        }
                    }
                }
            }

            Vector3 acceleration = Vector3.zero;
            if (neighbours > 0)
            {
                float inverse = 1f / neighbours;
                acceleration += (centre * inverse - p) * Cohesion;
                acceleration += (heading * inverse - v) * Alignment;
                acceleration += away * Separation;
            }

            float distance = p.magnitude;
            if (distance > edge) acceleration -= p * ((distance - edge) / distance * 3f);

            Vector3 fromPredator = p - _predator;
            float predatorSq = fromPredator.sqrMagnitude;
            if (predatorSq < PredatorRadius * PredatorRadius) acceleration += fromPredator * (400f / (predatorSq + 1f));

            float accelerationSq = acceleration.sqrMagnitude;
            if (accelerationSq > MaxAcceleration * MaxAcceleration) acceleration *= MaxAcceleration / Mathf.Sqrt(accelerationSq);

            v += acceleration * dt;
            float speed = v.magnitude;
            if (speed > MaxSpeed) v *= MaxSpeed / speed;
            else if (speed < MinSpeed && speed > 1e-4f) v *= MinSpeed / speed;
            _nextVelocity[i] = v;
        }

        // Every boid has steered by the same snapshot; now move them all.
        for (int i = 0; i < _count; i++)
        {
            Vector3 v = _nextVelocity[i];
            _velocity[i] = v;
            _position[i] += v * dt;
        }
    }

    public override void Render(Demo demo)
    {
        InstanceBatcher batcher = demo.Batcher;
        batcher.Begin(demo.Dart);
        for (int i = 0; i < _count; i++)
        {
            Vector3 v = _velocity[i];
            Vector3 forward = v * (1f / v.magnitude);
            // Colour by heading, so a flock that turns together changes colour together.
            var color = new Vector4(0.15f + 0.85f * forward.x * forward.x, 0.15f + 0.85f * forward.y * forward.y, 0.15f + 0.85f * forward.z * forward.z, 0.35f);
            batcher.Add(InstanceBatcher.Aim(_position[i], forward, 0.42f, 1.4f), color);
        }
        batcher.Add(InstanceBatcher.Aim(_predator, _predatorHeading, 2f, 5f), new Vector4(1f, 0.08f, 0.04f, 3f));
        batcher.Flush();
    }

    public override void PlaceCamera(Transform camera, float time) => Orbit(camera, Vector3.zero, 80f, 18f, time * 0.12f);

    public override ulong Checksum()
    {
        Hash64 hash = Hash64.Create();
        for (int i = 0; i < _count; i++) hash.Add(_position[i]);
        return hash.Value;
    }
}
