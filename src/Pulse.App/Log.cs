using System.IO;

namespace Pulse.App;

/// <summary>Minimal append-only log at %LocalAppData%\Pulse\pulse.log; rotates to pulse.old.log past 2 MB.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");

    private static readonly string FilePath = Path.Combine(DataDirectory, "pulse.log");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                File.Delete(FilePath);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static int _writes;

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                // Pulse runs for days in the tray; rotate instead of growing forever.
                if (++_writes % 200 == 0 && File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore: disk full, file locked by an editor, etc.
        }
    }
}
