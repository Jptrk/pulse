using System.Runtime.InteropServices;

namespace Pulse.App.Sensors;

/// <summary>
/// Reads the NVIDIA GPU's PCIe link state through NVML (nvml.dll, shipped with the driver — the library nvidia-smi uses).
/// User-mode only, no driver of ours involved.
/// </summary>
internal sealed class NvmlPcie : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private nint _device;
    private bool _initialized;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int Init();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int Shutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int GetHandleByIndex(uint index, out nint device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkGeneration")]
    private static extern int GetCurrGen(nint device, out uint gen);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkWidth")]
    private static extern int GetCurrWidth(nint device, out uint width);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxPcieLinkGeneration")]
    private static extern int GetMaxGen(nint device, out uint gen);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxPcieLinkWidth")]
    private static extern int GetMaxWidth(nint device, out uint width);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPcieReplayCounter")]
    private static extern int GetReplayCounter(nint device, out uint value);

    public bool TryInit()
    {
        try
        {
            if (Init() != NVML_SUCCESS)
                return false;
            _initialized = true;
            return GetHandleByIndex(0, out _device) == NVML_SUCCESS;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Info("NVML not available: " + ex.Message);
            return false;
        }
    }

    public PcieReading? Read()
    {
        if (_device == 0)
            return null;
        try
        {
            int gen = GetCurrGen(_device, out uint g) == NVML_SUCCESS ? (int)g : 0;
            int width = GetCurrWidth(_device, out uint w) == NVML_SUCCESS ? (int)w : 0;
            int maxGen = GetMaxGen(_device, out uint mg) == NVML_SUCCESS ? (int)mg : 0;
            int maxWidth = GetMaxWidth(_device, out uint mw) == NVML_SUCCESS ? (int)mw : 0;
            long replays = GetReplayCounter(_device, out uint r) == NVML_SUCCESS ? r : -1;
            return gen == 0 ? null : new PcieReading(gen, width, maxGen, maxWidth, replays);
        }
        catch (Exception ex)
        {
            Log.Warn("NVML read failed: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            Shutdown();
            _initialized = false;
        }
    }
}
