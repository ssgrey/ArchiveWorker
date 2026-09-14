using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class AdvancedDetectionTests
{
    [Fact]
    public void AdvancedSettings_OutOfRangeValuesAreRejected()
    {
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { RelativeContrastSensitivity = 101 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { AbsoluteDarknessSensitivity = -1 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { SmallDefectSensitivity = 101 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { TraceConnectionPixels = 21 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { EdgeShadowTracePixels = 41 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { AutoRemoveConfidencePercent = 49 }.Validate());
        Assert.Throws<CleanupValidationException>(() => new CleanupSettingsSnapshot { ContentProtectionStrength = 101 }.Validate());
    }

    [Fact]
    public async Task SmallDefectSensitivity_ControlsNoiseFiltering()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = CreateTestImage(root, "small.png", 160, 120, [(10, 58, 3, 3)]);
            var low = await Analyze(path, AdvancedSettings(smallDefectSensitivity: 0));
            var high = await Analyze(path, AdvancedSettings(smallDefectSensitivity: 100));

            Assert.True(high.DefectMask.ActivePixelCount > low.DefectMask.ActivePixelCount);
            Assert.True(high.Regions.Count > low.Regions.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AutoRemoveThreshold_ChangesReviewStatusWithoutChangingCandidates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = CreateTestImage(root, "threshold.png", 160, 120, [(12, 50, 14, 14)]);
            var relaxed = await Analyze(path, AdvancedSettings(autoRemoveConfidence: 70));
            var strict = await Analyze(path, AdvancedSettings(autoRemoveConfidence: 95));

            Assert.Equal(relaxed.DefectMask.Pixels, strict.DefectMask.Pixels);
            Assert.True(relaxed.AutoRemoveMask.ActivePixelCount > strict.AutoRemoveMask.ActivePixelCount);
            Assert.True(strict.ReviewMask.ActivePixelCount > 0);
            Assert.Equal(0, MaskMath.Intersect(strict.AutoRemoveMask, strict.ReviewMask).ActivePixelCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RepeatedDefects_PromoteMatchingReviewRegionsButNeverProtectionConflicts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstPath = CreateTestImage(root, "repeat-1.png", 160, 120, [(12, 50, 14, 14)]);
            var secondPath = CreateTestImage(root, "repeat-2.png", 160, 120, [(12, 50, 14, 14)]);
            var settings = AdvancedSettings(autoRemoveConfidence: 95) with { DetectRepeatedDefects = true };
            var first = await Analyze(firstPath, settings);
            var second = await Analyze(secondPath, settings);
            Assert.All(first.Regions.Concat(second.Regions), region => Assert.Equal(RegionStatus.NeedsReview, region.Status));

            var enhanced = RepeatedDefectAnalyzer.Enhance([first, second]);

            Assert.All(enhanced, analysis => Assert.Contains(analysis.Regions, region => region.Status == RegionStatus.AutoRemove));
            Assert.All(enhanced, analysis => Assert.Equal(0, MaskMath.Intersect(analysis.AutoRemoveMask, analysis.ProtectionMask).ActivePixelCount));

            var protectedEnhanced = RepeatedDefectAnalyzer.Enhance([AddProtectionConflict(first), AddProtectionConflict(second)]);
            Assert.All(protectedEnhanced, analysis => Assert.All(analysis.Regions, region => Assert.Equal(RegionStatus.NeedsReview, region.Status)));
            Assert.All(protectedEnhanced, analysis => Assert.Equal(0, analysis.AutoRemoveMask.ActivePixelCount));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CleanupSettingsSnapshot AdvancedSettings(double smallDefectSensitivity = 50, double autoRemoveConfidence = 75) => new()
    {
        CleanLeft = true, CleanRight = false, CleanTop = false, CleanBottom = false, LeftMarginMm = 8,
        UseAdvancedDetectionSettings = true, RelativeContrastSensitivity = 75, AbsoluteDarknessSensitivity = 75,
        SmallDefectSensitivity = smallDefectSensitivity, TraceConnectionPixels = 0, EdgeShadowTracePixels = 0,
        AutoRemoveConfidencePercent = autoRemoveConfidence, DetectPageEdgeShadow = false, DetectRepeatedDefects = false,
        MaskExpansionPixels = 0, ProtectPrintedText = false, ProtectHandwriting = false, ProtectStamps = false,
        ProtectTablesAndPageNumbers = false, ProtectionPaddingPixels = 0
    };

    private static async Task<CleanupAnalysisResult> Analyze(string path, CleanupSettingsSnapshot settings) =>
        await new DocumentCleanupEngine().AnalyzeAsync(new CleanupRequest(path, 300, 300, settings), null, CancellationToken.None);

    private static CleanupAnalysisResult AddProtectionConflict(CleanupAnalysisResult source)
    {
        var protection = new byte[source.Width * source.Height];
        foreach (var region in source.Regions)
        for (var y = region.Bounds.Y; y < region.Bounds.Bottom; y++)
        for (var x = region.Bounds.X; x < region.Bounds.Right; x++)
        {
            var index = y * source.Width + x;
            if (source.DefectMask.Pixels[index] != 0) protection[index] = 255;
        }
        var protectionMask = new MaskBuffer(source.Width, source.Height, protection);
        return new CleanupAnalysisResult
        {
            SourcePath = source.SourcePath, Width = source.Width, Height = source.Height,
            DpiX = source.DpiX, DpiY = source.DpiY, Extension = source.Extension, Orientation = source.Orientation,
            Settings = source.Settings, EdgeRegions = source.EdgeRegions, DefectMask = source.DefectMask,
            HighConfidenceDefectMask = source.HighConfidenceDefectMask, ProtectionMask = protectionMask,
            ConflictMask = MaskMath.Intersect(source.DefectMask, protectionMask), ReviewMask = source.ReviewMask,
            AutoRemoveMask = source.AutoRemoveMask, Regions = source.Regions, OverlayPreview = source.OverlayPreview,
            Elapsed = source.Elapsed, Warnings = source.Warnings
        };
    }

    private static string CreateTestImage(string directory, string fileName, int width, int height,
        IReadOnlyList<(int X, int Y, int Width, int Height)> defects)
    {
        var pixels = Enumerable.Repeat((byte)235, width * height * 3).ToArray();
        foreach (var defect in defects)
        for (var y = defect.Y; y < defect.Y + defect.Height; y++)
        for (var x = defect.X; x < defect.X + defect.Width; x++)
        {
            var index = (y * width + x) * 3;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 20;
        }

        var path = Path.Combine(directory, fileName);
        ImageFileCodec.Save(new ImageBuffer(width, height, pixels, 300, 300, ".png"), path, ImageOutputFormat.Png, 95, true);
        return path;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerAdvanced_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
