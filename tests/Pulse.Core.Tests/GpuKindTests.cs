using Pulse.Core.Analysis;

namespace Pulse.Core.Tests;

public class GpuKindTests
{
    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]            // Ryzen desktop iGPU, ROG Ally / Claw A8 (Z1/Z2)
    [InlineData("AMD Radeon 780M Graphics", true)]           // Legion Go
    [InlineData("AMD Radeon(TM) 890M Graphics", true)]
    [InlineData("AMD Custom GPU 0405", true)]                // Steam Deck
    [InlineData("AMD Radeon RX 7800 XT", false)]
    [InlineData("AMD Radeon RX 9070 XT", false)]
    [InlineData("AMD Radeon Pro W7800", false)]
    public void Amd_by_name(string name, bool integrated)
    {
        Assert.Equal(integrated, GpuKind.IsIntegrated(GpuVendor.Amd, name));
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", true, true)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false, false)]
    public void Intel_trusts_sensor_library(string name, bool reported, bool integrated)
    {
        Assert.Equal(integrated, GpuKind.IsIntegrated(GpuVendor.Intel, name, reported));
    }

    [Fact]
    public void Nvidia_is_never_integrated()
    {
        Assert.False(GpuKind.IsIntegrated(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5070 Ti", reportedIntegrated: true));
    }
}
