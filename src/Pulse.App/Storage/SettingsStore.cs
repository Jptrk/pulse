using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Core.Settings;

namespace Pulse.App.Storage;

/// <summary>Settings as JSON at %AppData%\Pulse\settings.json. A corrupt file is kept aside and defaults are used.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulse", "settings.json");

    public PulseSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PulseSettings>(File.ReadAllText(FilePath), Options) ?? new PulseSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable, using defaults", ex);
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch
            {
                // Best effort.
            }
        }
        return new PulseSettings();
    }

    public void Save(PulseSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
