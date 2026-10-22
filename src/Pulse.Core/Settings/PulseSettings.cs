using Pulse.Core.Analysis;

namespace Pulse.Core.Settings;

public enum OverlayMode
{
    Off,
    Strip,
    Panel,
    Detailed,
}

public enum OverlayCorner
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomRight,
}

public sealed class OverlaySettings
{
    public OverlayMode Mode { get; set; } = OverlayMode.Panel;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;
    public double Scale { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.7;
    public bool OnlyWhenGameDetected { get; set; } = true;
    public int StatsWindowSeconds { get; set; } = 30;
    public int GraphFrames { get; set; } = 240;
    public bool ShowPerCoreBars { get; set; } = true;

    /// <summary>Show battery level on the overlay when the device has one (handhelds, laptops).</summary>
    public bool ShowBattery { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string CycleMode { get; set; } = "Alt+Shift+O";
    public string ToggleVisible { get; set; } = "Alt+Shift+H";
}

public sealed class SensorSettings
{
    public int PollIntervalMs { get; set; } = 1000;
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public double CpuTempC { get; set; } = 90;
    public double GpuTempC { get; set; } = 83;
    public double VramPercent { get; set; } = 92;
    public double RamPercent { get; set; } = 92;
    public bool PcieChecks { get; set; } = true;
    public int ExpectedPcieGen { get; set; } = 4;
    public int ExpectedPcieWidth { get; set; } = 16;
    public bool WheaChecks { get; set; } = true;
    public double BatteryLowPercent { get; set; } = 15;
}

public sealed class GameSettings
{
    /// <summary>Exe names always treated as games, even windowed.</summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>Exe names never treated as games, on top of the built-in list.</summary>
    public List<string> Deny { get; set; } = [];

    public double MinPresentRate { get; set; } = 20;
}

public sealed class PulseSettings
{
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public SensorSettings Sensors { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public GameSettings Games { get; set; } = new();
    public bool StartMinimized { get; set; } = true;
    public bool Autostart { get; set; }

    public GameDetectorOptions ToDetectorOptions()
    {
        var deny = new HashSet<string>(GameDetectorOptions.DefaultDeny, StringComparer.OrdinalIgnoreCase);
        deny.UnionWith(Games.Deny);
        var allow = new HashSet<string>(Games.Allow, StringComparer.OrdinalIgnoreCase);
        deny.ExceptWith(allow);
        return new GameDetectorOptions { Allow = allow, Deny = deny, MinPresentRate = Games.MinPresentRate };
    }

    /// <summary>Metric keys produced by the app each second and consumed by these rules.</summary>
    public static class Metrics
    {
        public const string CpuTemp = "cpu.temp";
        public const string GpuTemp = "gpu.temp";
        public const string VramPercent = "vram.pct";
        public const string RamPercent = "ram.pct";
        public const string GpuUnderLoad = "gpu.loaded";
        public const string PcieGen = "pcie.gen";
        public const string PcieWidth = "pcie.width";
        public const string PcieReplaysRecent = "pcie.replays.recent";
        public const string WheaRecent = "whea.recent";
        public const string BatteryPercent = "battery.pct";
        public const string OnBattery = "battery.discharging";
    }

    public IReadOnlyList<AlertRule> BuildAlertRules()
    {
        if (!Alerts.Enabled)
            return [];

        var a = Alerts;
        var rules = new List<AlertRule>
        {
            new("cpu-hot", Metrics.CpuTemp, AlertCompare.AtLeast, a.CpuTempC, "CPU {0:0}°C — running hot", SustainSeconds: 5, ClearMargin: 3),
            new("gpu-hot", Metrics.GpuTemp, AlertCompare.AtLeast, a.GpuTempC, "GPU {0:0}°C — running hot", SustainSeconds: 5, ClearMargin: 3),
            new("vram-full", Metrics.VramPercent, AlertCompare.AtLeast, a.VramPercent, "VRAM {0:0}% — nearly full", SustainSeconds: 10, ClearMargin: 3),
            new("ram-full", Metrics.RamPercent, AlertCompare.AtLeast, a.RamPercent, "RAM {0:0}% — nearly full", SustainSeconds: 10, ClearMargin: 3),
        };
        if (a.PcieChecks)
        {
            rules.Add(new("pcie-gen", Metrics.PcieGen, AlertCompare.NotEqual, a.ExpectedPcieGen,
                "PCIe Gen{0:0} — expected Gen" + a.ExpectedPcieGen, SustainSeconds: 5, GateMetric: Metrics.GpuUnderLoad));
            rules.Add(new("pcie-width", Metrics.PcieWidth, AlertCompare.Below, a.ExpectedPcieWidth,
                "PCIe x{0:0} — expected x" + a.ExpectedPcieWidth, SustainSeconds: 5, GateMetric: Metrics.GpuUnderLoad));
            rules.Add(new("pcie-replays", Metrics.PcieReplaysRecent, AlertCompare.AtLeast, 1,
                "PCIe link retries: {0:0} in last min (riser?)", SustainSeconds: 0, CooldownSeconds: 0));
        }
        if (a.BatteryLowPercent > 0)
        {
            rules.Add(new("battery-low", Metrics.BatteryPercent, AlertCompare.Below, a.BatteryLowPercent,
                "Battery {0:0}% — plug in soon", SustainSeconds: 0, CooldownSeconds: 0, GateMetric: Metrics.OnBattery));
        }
        if (a.WheaChecks)
        {
            rules.Add(new("whea", Metrics.WheaRecent, AlertCompare.AtLeast, 1,
                "WHEA hardware errors: {0:0} in last min", SustainSeconds: 0, CooldownSeconds: 0));
        }
        return rules;
    }
}
