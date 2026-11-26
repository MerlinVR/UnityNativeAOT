using UnityEngine;

/// <summary>
/// One workload of the demo. Each advances by a fixed time step, so every scripting backend does the same work per
/// frame whatever its frame rate, and the state after N frames can be compared between backends.
/// </summary>
public abstract class Section
{
    /// <summary>Short identifier used on the command line and in the results.</summary>
    public abstract string Name { get; }

    /// <summary>What the section holds at the current scale, for the HUD and the results.</summary>
    public abstract string Detail { get; }

    /// <summary>What part of the scripting runtime the section leans on.</summary>
    public abstract string Stresses { get; }

    public abstract void Begin(Demo demo, float scale);

    /// <summary>Advances the simulation. This is the part whose time is reported as "step".</summary>
    public abstract void Step(float dt);

    /// <summary>Hands the frame's draw calls to the engine. Reported as "submit".</summary>
    public abstract void Render(Demo demo);

    public abstract void PlaceCamera(Transform camera, float time);

    /// <summary>A hash of the simulation state, for comparing backends after the same number of steps.</summary>
    public abstract ulong Checksum();

    public virtual void End() { }

    protected static void Orbit(Transform camera, Vector3 target, float distance, float height, float angle)
    {
        camera.position = target + new Vector3(Mathf.Sin(angle) * distance, height, -Mathf.Cos(angle) * distance);
        camera.LookAt(target);
    }
}
