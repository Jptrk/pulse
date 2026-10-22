using System.Runtime.InteropServices;

namespace Pulse.App.Sensors;

/// <summary>Battery and charger state from GetSystemPowerStatus — works on any Windows device, no driver.</summary>
internal static class PowerStatus
{
    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagNoBattery = 128;
    private const byte Unknown = 255;

    public readonly record struct Snapshot(bool HasBattery, double Percent, bool OnAc, bool Charging, double SecondsLeft);

    public static Snapshot Read()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s) || (s.BatteryFlag & BatteryFlagNoBattery) != 0 || s.BatteryFlag == Unknown)
            return new Snapshot(false, double.NaN, true, false, double.NaN);

        return new Snapshot(
            HasBattery: true,
            Percent: s.BatteryLifePercent == Unknown ? double.NaN : s.BatteryLifePercent,
            OnAc: s.ACLineStatus == 1,
            Charging: (s.BatteryFlag & BatteryFlagCharging) != 0,
            SecondsLeft: s.BatteryLifeTime < 0 ? double.NaN : s.BatteryLifeTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
