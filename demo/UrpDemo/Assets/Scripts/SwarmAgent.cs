using UnityEngine;

/// <summary>One member of the <see cref="Swarm"/>. Its position is a function of the swarm's clock alone, so the order
/// in which the engine updates the agents does not matter.</summary>
public sealed class SwarmAgent : MonoBehaviour
{
    Swarm _swarm;
    Transform _self;
    float _phase, _radius, _speed, _height, _wave;

    public void Configure(Swarm swarm, float phase, float radius, float speed, float height, float wave)
    {
        _swarm = swarm;
        _self = transform;
        _phase = phase;
        _radius = radius;
        _speed = speed;
        _height = height;
        _wave = wave;
        _self.localPosition = PositionAt(swarm.Clock);
    }

    Vector3 PositionAt(float time)
    {
        float angle = _phase + time * _speed;
        Trig.SinCos(angle, out float sin, out float cos);
        return new Vector3(cos * _radius, _height + Trig.Sin(angle * 3f + time) * _wave, sin * _radius);
    }

    void Update()
    {
        float time = _swarm.Clock;
        Vector3 previous = _self.localPosition;
        Vector3 next = PositionAt(time);
        _self.localPosition = next;

        Vector3 heading = next - previous;
        if (heading.sqrMagnitude > 1e-8f) _self.localRotation = Quaternion.LookRotation(heading, Vector3.up);

        float pulse = 0.3f + 0.12f * Trig.Sin(time * 2f + _phase * 5f);
        _self.localScale = new Vector3(pulse, pulse, pulse * 2.2f);
    }
}
