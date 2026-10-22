using System.Diagnostics;

namespace Pulse.App;

/// <summary>One monotonic clock for every "now" in the app, so timestamps from different threads compare correctly.</summary>
internal static class AppClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static double NowMs => Watch.Elapsed.TotalMilliseconds;
}
