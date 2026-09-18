using System;
using System.IO;
using System.Text.Json;
using ArchiveCleaner.Wpf.Models;

namespace ArchiveCleaner.Wpf.Services;

public class SettingsManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArchiveCleaner", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppSettings();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Silently fail - settings will use defaults
        }
    }
}
