using System.Text.RegularExpressions;

namespace Pulse.Core.Analysis;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
}

/// <summary>
/// Tells integrated GPUs (handhelds, laptops, APUs) from discrete cards. Memory size can't decide it: AMD handhelds
/// often reserve 4–8 GB as "dedicated" VRAM. Names can for AMD; Intel's integrated names (e.g. "Arc 140V", "Arc B390")
/// look like discrete models, so Intel relies on the sensor library telling the two apart.
/// </summary>
public static partial class GpuKind
{
    [GeneratedRegex(@"\bRX\b|Radeon\s*Pro|Radeon\s*VII|Instinct|FirePro", RegexOptions.IgnoreCase)]
    private static partial Regex AmdDiscreteName();

    public static bool IsIntegrated(GpuVendor vendor, string name, bool reportedIntegrated = false) => vendor switch
    {
        GpuVendor.Nvidia => false,
        GpuVendor.Intel => reportedIntegrated,
        GpuVendor.Amd => !AmdDiscreteName().IsMatch(name),
        _ => false,
    };
}
