using System.Diagnostics;

namespace Pulse.App.Sensors;

/// <summary>
/// Windows' built-in "Energy Meter" performance counters, which expose the CPU's RAPL power readings through the
/// inbox processor power driver — no third-party driver needed. Instances look like RAPL_Package0_PKG (whole chip),
/// RAPL_Package0_PP0 / ..._CoreN_CORE (CPU cores) and RAPL_Package0_PP1 (integrated GPU), depending on the CPU.
/// Values are milliwatts. Must be used from a single thread (the sensor thread).
/// </summary>
internal sealed class EnergyMeter : IDisposable
{
    private const string Category = "Energy Meter";

    private PerformanceCounter? _package;
    private PerformanceCounter? _gpu;

    public static string[] Instances()
    {
        try
        {
            return PerformanceCounterCategory.Exists(Category) ? new PerformanceCounterCategory(Category).GetInstanceNames() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public bool TryOpen()
    {
        string[] instances = Instances();
        string? package = instances.FirstOrDefault(i => i.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase));
        string? gpu = instances.FirstOrDefault(i => i.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase) || i.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (package is not null)
                _package = Open(package);
            if (gpu is not null)
                _gpu = Open(gpu);
        }
        catch (Exception ex)
        {
            Log.Warn("Energy Meter counters unavailable: " + ex.Message);
            Dispose();
        }
        Log.Info($"Energy Meter: package={package ?? "none"}, gpu={gpu ?? "none"}");
        return _package is not null;
    }

    /// <summary>Whole-chip power in watts (CPU + integrated GPU on SoCs), or NaN.</summary>
    public double PackageWatts => Read(_package);

    /// <summary>Integrated-GPU power in watts where the CPU reports it separately, or NaN.</summary>
    public double GpuWatts => Read(_gpu);

    private static PerformanceCounter Open(string instance)
    {
        var counter = new PerformanceCounter(Category, "Power", instance, readOnly: true);
        counter.NextValue(); // prime: the first sample of a rate counter is always 0
        return counter;
    }

    private static double Read(PerformanceCounter? counter)
    {
        if (counter is null)
            return double.NaN;
        try
        {
            float mw = counter.NextValue();
            return mw > 0 ? mw / 1000.0 : double.NaN;
        }
        catch (Exception)
        {
            return double.NaN;
        }
    }

    public void Dispose()
    {
        _package?.Dispose();
        _gpu?.Dispose();
        _package = _gpu = null;
    }
}
