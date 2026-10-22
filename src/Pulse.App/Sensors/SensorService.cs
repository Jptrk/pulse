using System.Diagnostics;
using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.App.Telemetry;

namespace Pulse.App.Sensors;

/// <summary>
/// Polls CPU, GPU and memory sensors on a dedicated thread (the LibreHardwareMonitor <see cref="Computer"/> is not thread-safe,
/// so only this thread touches it). Storage and motherboard are left off: SMART polling can cause hitches and isn't needed here.
/// CPU temperature/power need the PawnIO driver; without it those read NaN and everything else still works.
/// </summary>
internal sealed class SensorService : IDisposable
{
    private readonly TelemetryHub _hub;
    private readonly Func<int> _intervalMs;
    private readonly ManualResetEventSlim _stop = new();
    private readonly NvmlPcie _nvml = new();
    private Thread? _thread;
    private Computer? _computer;
    private PerformanceCounter? _cpuPerf, _cpuFreq;

    public SensorService(TelemetryHub hub, Func<int> intervalMs)
    {
        _hub = hub;
        _intervalMs = intervalMs;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Sensors" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _computer = Open();
            Log.Info($"Sensors open: {string.Join(", ", _computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}"))}; PawnIO installed: {LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled}");
        }
        catch (Exception ex)
        {
            Log.Error("Opening hardware sensors failed", ex);
        }

        bool nvml = !_stop.IsSet && _nvml.TryInit();
        Log.Info("NVML " + (nvml ? "ready" : "unavailable"));
        try
        {
            Poll(nvml);
        }
        finally
        {
            // Closed here, on the only thread that ever touches LHM and NVML — even if Dispose stopped waiting for us.
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("Closing sensors: " + ex.Message);
            }
            _nvml.Dispose();
            _cpuPerf?.Dispose();
            _cpuFreq?.Dispose();
        }
    }

    private void Poll(bool nvml)
    {
        int tick = 0;
        while (!_stop.IsSet)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (_computer is not null)
                {
                    _hub.Hardware = Read(_computer);
                    if (tick == 0)
                        Log.Info($"First sensor read: GPU {_hub.Hardware.GpuLoad:0}% {_hub.Hardware.GpuTempC:0}°C, CPU {_hub.Hardware.CpuLoad:0}% {_hub.Hardware.CpuTempC:0}°C, RAM {_hub.Hardware.RamUsedGb:0.0} GB");
                }
                if (nvml && tick % 2 == 0)
                    _hub.Pcie = _nvml.Read();
            }
            catch (Exception ex)
            {
                Log.Error("Sensor poll failed", ex);
            }
            tick++;
            int wait = Math.Clamp(_intervalMs(), 250, 5000) - (int)sw.ElapsedMilliseconds;
            _stop.Wait(Math.Max(50, wait));
        }
    }

    private static Computer Open()
    {
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
        computer.Open();
        return computer;
    }

    private HardwareReading Read(Computer computer)
    {
        IHardware? cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        IHardware? gpu = PickGpu(computer.Hardware);
        IHardware? memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

        cpu?.Update();
        gpu?.Update();
        memory?.Update();

        float[] threads = cpu is null ? [] : ThreadLoads(cpu);
        double cpuClock = cpu is null ? double.NaN : MaxOf(cpu, SensorType.Clock, s => s.Name.StartsWith("Core #", StringComparison.Ordinal));
        if (double.IsNaN(cpuClock))
            cpuClock = FallbackCpuClock();

        double vramUsed = Get(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        double vramTotal = Get(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        double ramUsed = Get(memory, SensorType.Data, "Memory Used");
        double ramAvailable = Get(memory, SensorType.Data, "Memory Available");

        return new HardwareReading
        {
            CpuName = cpu?.Name ?? "",
            CpuLoad = Get(cpu, SensorType.Load, "CPU Total"),
            CpuMaxThreadLoad = threads.Length > 0 ? threads.Max() : Get(cpu, SensorType.Load, "CPU Core Max"),
            ThreadLoads = threads,
            CpuClockMhz = cpuClock,
            CpuTempC = Get(cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "CPU Package", "Package", "Core Average", "Core Max"),
            CpuPowerW = Get(cpu, SensorType.Power, "Package", "CPU Package"),

            GpuName = gpu?.Name ?? "",
            GpuLoad = Get(gpu, SensorType.Load, "GPU Core", "D3D 3D"),
            GpuTempC = Get(gpu, SensorType.Temperature, "GPU Core"),
            GpuHotspotC = Get(gpu, SensorType.Temperature, "GPU Hot Spot"),
            GpuMemJunctionC = Get(gpu, SensorType.Temperature, "GPU Memory Junction"),
            GpuClockMhz = Get(gpu, SensorType.Clock, "GPU Core"),
            GpuMemClockMhz = Get(gpu, SensorType.Clock, "GPU Memory"),
            GpuPowerW = Get(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power"),
            GpuFanRpm = First(gpu, SensorType.Fan),
            GpuFanPercent = First(gpu, SensorType.Control),
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,

            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed + ramAvailable,
        };
    }

    /// <summary>Prefer the discrete GPU: NVIDIA first, then whichever AMD/Intel GPU reports the most VRAM.</summary>
    private static IHardware? PickGpu(IList<IHardware> hardware)
    {
        var gpus = hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        return gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
               ?? gpus.OrderByDescending(h => Get(h, SensorType.SmallData, "GPU Memory Total") is var v && double.IsNaN(v) ? 0 : v).FirstOrDefault();
    }

    /// <summary>Per-logical-processor loads ("CPU Core #3 Thread #2"), in core/thread order.</summary>
    private static float[] ThreadLoads(IHardware cpu)
    {
        var loads = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal) && s.Value.HasValue)
            .OrderBy(s => s.Index)
            .Select(s => s.Value!.Value)
            .ToArray();
        return loads;
    }

    private static double Get(IHardware? hardware, SensorType type, params string[] names)
    {
        if (hardware is null)
            return double.NaN;
        // Without the PawnIO driver LHM reports CPU temperature/power/clock as 0 rather than missing.
        bool zeroMeansMissing = type is SensorType.Temperature or SensorType.Power or SensorType.Clock;
        foreach (string name in names)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType == type && sensor.Value is float v && sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return zeroMeansMissing && v <= 0 ? double.NaN : v;
            }
        }
        return double.NaN;
    }

    private static double First(IHardware? hardware, SensorType type) =>
        hardware?.Sensors.FirstOrDefault(s => s.SensorType == type && s.Value.HasValue)?.Value ?? double.NaN;

    private static double MaxOf(IHardware hardware, SensorType type, Func<ISensor, bool> filter)
    {
        double max = double.NaN;
        foreach (ISensor s in hardware.Sensors)
        {
            if (s.SensorType == type && s.Value is float v && v > 0 && filter(s))
                max = double.IsNaN(max) ? v : Math.Max(max, v);
        }
        return max;
    }

    /// <summary>Effective CPU clock from Windows counters, used when no driver gives per-core clocks.</summary>
    private double FallbackCpuClock()
    {
        try
        {
            _cpuPerf ??= new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cpuFreq ??= new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
            double mhz = _cpuFreq.NextValue() * _cpuPerf.NextValue() / 100.0;
            return mhz > 0 ? mhz : double.NaN;
        }
        catch (Exception ex)
        {
            Log.Warn("CPU clock counters unavailable: " + ex.Message);
            _cpuPerf = _cpuFreq = null;
            return double.NaN;
        }
    }

    /// <summary>Writes every sensor LibreHardwareMonitor exposes on this machine, for tuning the name mappings.</summary>
    public static void Dump(TextWriter output)
    {
        var computer = Open();
        try
        {
            output.WriteLine($"PawnIO installed: {LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled} {LibreHardwareMonitor.PawnIo.PawnIo.Version}");
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (IHardware h in computer.Hardware)
                    h.Update();
                Thread.Sleep(500);
            }
            foreach (IHardware h in computer.Hardware)
            {
                output.WriteLine($"[{h.HardwareType}] {h.Name}");
                foreach (ISensor s in h.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                    output.WriteLine($"  {s.SensorType,-12} #{s.Index,-3} {s.Name,-36} {s.Value?.ToString("0.##") ?? "-"}");
                foreach (IHardware sub in h.SubHardware)
                {
                    sub.Update();
                    output.WriteLine($"  [{sub.HardwareType}] {sub.Name}");
                    foreach (ISensor s in sub.Sensors)
                        output.WriteLine($"    {s.SensorType,-12} #{s.Index,-3} {s.Name,-36} {s.Value?.ToString("0.##") ?? "-"}");
                }
            }
        }
        finally
        {
            computer.Close();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread is not null && !_thread.Join(5000))
            Log.Warn("Sensor thread still busy at shutdown; it will close sensors when it finishes.");
    }
}
