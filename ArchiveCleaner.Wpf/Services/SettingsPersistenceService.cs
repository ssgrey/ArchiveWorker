using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ArchiveCleaner.Wpf.Models;

namespace ArchiveCleaner.Wpf.Services;

public sealed class SettingsPersistenceService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArchiveCleaner", "settings.json");

    public bool TryLoad(CleanupSettings target, string? path = null)
    {
        var settingsPath = path ?? DefaultPath;
        if (!File.Exists(settingsPath)) return false;
        try
        {
            var json = File.ReadAllText(settingsPath);
            var loaded = JsonSerializer.Deserialize<CleanupSettings>(json, SerializerOptions);
            if (loaded is null) return false;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty(nameof(CleanupSettings.ProtectBrushDiameterPixels), out _)
                && root.TryGetProperty(nameof(CleanupSettings.ManualBrushDiameterPixels), out var legacyBrush)
                && legacyBrush.TryGetDouble(out var legacyDiameter))
            {
                loaded.ProtectBrushDiameterPixels = legacyDiameter;
                loaded.RemoveBrushDiameterPixels = legacyDiameter;
                loaded.CloneStampDiameterPixels = legacyDiameter;
                loaded.EraserDiameterPixels = legacyDiameter;
            }
            loaded.CreateSnapshot();
            target.CopyFrom(loaded);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public void Save(CleanupSettings settings, string? path = null)
    {
        settings.CreateSnapshot();
        var settingsPath = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))
            ?? throw new InvalidOperationException("自动参数文件路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, settingsPath, overwrite: true);
    }
}
