using Pulse.App.Sensors;
using Pulse.Core.Analysis;
using Pulse.Core.Frames;

namespace Pulse.App.Overlay;

/// <summary>Everything one overlay text refresh needs, captured at one instant.</summary>
internal sealed record OverlayModel(
    FrameWindowStats? Frames,
    HardwareReading? Hardware,
    PcieReading? Pcie,
    long PcieReplaysRecent,
    string? GameName,
    TimeSpan GameTime,
    IReadOnlyList<ActiveAlert> Alerts,
    double CpuHotC,
    double GpuHotC,
    bool ShowBattery = true);
