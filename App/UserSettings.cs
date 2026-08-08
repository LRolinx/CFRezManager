using System.IO;
using System.Text.Json;

namespace CFRezManager;

public sealed class UserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Language { get; set; } = "zh";
    public string Theme { get; set; } = "Light";
    public string LastDirectory { get; set; } = string.Empty;
    public string LastRezDirectory { get; set; } = string.Empty;
    public string LastPackDirectory { get; set; } = string.Empty;
    public string LastOutputDirectory { get; set; } = string.Empty;
    public string LastSaveDirectory { get; set; } = string.Empty;
    public double ViewSize { get; set; } = 72;
    public double AudioPreviewVolume { get; set; } = 0.8;
    public string AudioPreviewRepeatMode { get; set; } = "Directory";
    public Dictionary<string, string> ImageExportModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SkipImageExportOptionsDialog { get; set; }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CFRezManager",
        "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UserSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings are a convenience only; the app should keep working if they cannot be written.
        }
    }
}
