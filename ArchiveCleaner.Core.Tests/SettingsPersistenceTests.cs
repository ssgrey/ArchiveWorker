using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;

namespace ArchiveCleaner.Core.Tests;

public sealed class SettingsPersistenceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsAllProcessingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerSettings_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");
        try
        {
            var expected = new CleanupSettings
            {
                CleanRight = false,
                RightMarginMm = 23.5,
                DetectionSensitivity = 72,
                ProtectHandwriting = false,
                RepairMethod = "污点修复",
                OutputFormat = "PNG",
                JpegQuality = 91,
                ShowRulers = false,
                ShowCandidateGuides = false,
                ProtectBrushDiameterPixels = 144,
                RemoveBrushDiameterPixels = 96,
                CloneStampDiameterPixels = 72,
                EraserDiameterPixels = 36
            };
            var service = new SettingsPersistenceService();
            service.Save(expected, path);
            var actual = new CleanupSettings();

            Assert.True(service.TryLoad(actual, path));
            Assert.Equal(expected.CreateSnapshot(), actual.CreateSnapshot());
            Assert.Equal(144, actual.ProtectBrushDiameterPixels);
            Assert.Equal(96, actual.RemoveBrushDiameterPixels);
            Assert.Equal(72, actual.CloneStampDiameterPixels);
            Assert.Equal(36, actual.EraserDiameterPixels);
            Assert.False(actual.ShowRulers);
            Assert.False(actual.ShowCandidateGuides);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InvalidSavedSettings_FallBackWithoutChangingDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerSettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, "{ invalid json");
            var settings = new CleanupSettings();
            var defaults = settings.CreateSnapshot();

            Assert.False(new SettingsPersistenceService().TryLoad(settings, path));
            Assert.Equal(defaults, settings.CreateSnapshot());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
