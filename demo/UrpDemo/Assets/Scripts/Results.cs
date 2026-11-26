using System;
using System.Collections.Generic;

/// <summary>What a benchmark run writes with -results. Times are milliseconds unless the name says otherwise.</summary>
[Serializable]
public sealed class RunResult
{
    public string label;
    public string backend;
    public string runtime;
    public string unity;
    public string graphics;
    public int processors;
    public int width, height;
    public int frames, warmup;
    public float scale;
    /// <summary>Launch to the first Update when the launcher passed -launched; otherwise by the engine's clock, which starts later.</summary>
    public double startupSeconds;
    public double totalSeconds;
    public List<SectionResult> sections = new List<SectionResult>();
}

[Serializable]
public sealed class SectionResult
{
    public string name;
    public string detail;
    public string stresses;
    public bool ok;
    public string error;
    public int frames;

    /// <summary>Whole frames: scripts, engine and rendering.</summary>
    public double frameAvg, frameMedian, frameP95, frameP99, frameMax;

    /// <summary>The section's simulation step alone.</summary>
    public double stepAvg, stepMedian, stepP99, stepMax;

    /// <summary>The very first step, which on a JIT includes compiling the code.</summary>
    public double stepFirst;

    /// <summary>Handing draw calls to the engine.</summary>
    public double submitAvg;

    /// <summary>From the start of the step to LateUpdate: every script's Update for the frame.</summary>
    public double scriptAvg, scriptMedian;

    /// <summary>Collections during the measured frames, and what the main thread allocated in them.</summary>
    public int gen0, fullGc;
    public double allocatedMb, heapMb;

    /// <summary>Hash of the simulation state after the last frame.</summary>
    public string checksum;
}

public struct Summary
{
    public double average, median, p95, p99, min, max;
}

public sealed class Samples
{
    double[] _values = new double[1024];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Add(double value)
    {
        if (Count == _values.Length) Array.Resize(ref _values, Count * 2);
        _values[Count++] = value;
    }

    public Summary Summarise()
    {
        if (Count == 0) return default;
        var sorted = new double[Count];
        Array.Copy(_values, sorted, Count);
        Array.Sort(sorted);
        double sum = 0;
        for (int i = 0; i < sorted.Length; i++) sum += sorted[i];
        return new Summary
        {
            average = sum / Count,
            median = At(sorted, 0.5),
            p95 = At(sorted, 0.95),
            p99 = At(sorted, 0.99),
            min = sorted[0],
            max = sorted[Count - 1],
        };
    }

    static double At(double[] sorted, double quantile)
    {
        int index = (int)Math.Ceiling(quantile * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))];
    }
}
