using ArchiveCleaner.Core.Audit;
using ArchiveCleaner.Core.Batch;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class GeometryAndSafetyTests
{
    [Fact]
    public void MillimetersToPixels_UsesDpiAndFallsBackForInvalidDpi()
    {
        Assert.Equal(236, ImageGeometry.MillimetersToPixels(20, 300, 1000));
        Assert.Equal(118, ImageGeometry.MillimetersToPixels(10, 0, 1000));
        Assert.Equal(20, ImageGeometry.MillimetersToPixels(100, 300, 20));
    }

    [Fact]
    public void CandidateRegions_ClipCoordinatesAndHonorEdgeSwitches()
    {
        var settings = new CleanupSettingsSnapshot
        {
            CleanLeft = true, LeftMarginMm = 2,
            CleanRight = false,
            CleanTop = true, TopMarginMm = 4,
            CleanBottom = false
        };
        var result = ImageGeometry.CreateCandidateRegions(100, 80, 254, 254, settings);
        Assert.Equal(new PixelRect(0, 0, 20, 80), result.Regions[EdgeSide.Left]);
        Assert.Equal(new PixelRect(0, 0, 100, 40), result.Regions[EdgeSide.Top]);
        Assert.False(result.Regions.ContainsKey(EdgeSide.Right));
        Assert.False(result.Regions.ContainsKey(EdgeSide.Bottom));
        Assert.Equal(4800, ImageGeometry.CreateCandidateMask(result).ActivePixelCount);
    }

    [Fact]
    public void CandidateRegions_ClipMarginsToHalfPhysicalDimension()
    {
        var settings = new CleanupSettingsSnapshot { CleanLeft = true, CleanRight = false, CleanTop = false, CleanBottom = false, LeftMarginMm = 6 };
        var result = ImageGeometry.CreateCandidateRegions(100, 80, 254, 254, settings);
        Assert.Equal(new PixelRect(0, 0, 50, 80), result.Regions[EdgeSide.Left]);
    }

    [Fact]
    public void MaskOperations_KeepRemovalInsideCandidateAndOutsideProtection()
    {
        var candidate = new MaskBuffer(4, 2, [255, 255, 0, 0, 255, 255, 0, 0]);
        var defects = new MaskBuffer(4, 2, [255, 255, 255, 0, 0, 255, 0, 0]);
        var protection = new MaskBuffer(4, 2, [0, 255, 0, 0, 0, 0, 0, 0]);
        var constrained = MaskMath.Intersect(defects, candidate);
        var autoRemove = MaskMath.Subtract(constrained, protection);
        var conflict = MaskMath.Intersect(constrained, protection);
        Assert.True(MaskMath.IsSubsetOf(autoRemove, candidate));
        Assert.Equal(2, autoRemove.ActivePixelCount);
        Assert.Equal(1, conflict.ActivePixelCount);
        Assert.Equal(0, MaskMath.Intersect(autoRemove, protection).ActivePixelCount);
    }

    [Theory]
    [InlineData(-1, 55, 6, 8, 58, 95)]
    [InlineData(20, 101, 6, 8, 58, 95)]
    [InlineData(20, 55, 31, 8, 58, 95)]
    [InlineData(20, 55, 6, 51, 58, 95)]
    [InlineData(20, 55, 6, 8, 101, 95)]
    [InlineData(20, 55, 6, 8, 58, 69)]
    public void InvalidSettings_AreRejectedAtCoreEntry(double margin, double sensitivity, int maskExpansion, int protectionPadding, double feathering, int quality)
    {
        var settings = new CleanupSettingsSnapshot
        {
            LeftMarginMm = margin, DetectionSensitivity = sensitivity, MaskExpansionPixels = maskExpansion,
            ProtectionPaddingPixels = protectionPadding, ColorFeathering = feathering, JpegQuality = quality
        };
        Assert.Throws<CleanupValidationException>(settings.Validate);
    }

    [Fact]
    public void SafeWriter_ReplacesPreviousOutputAndNeverOverwritesSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerTests_{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "page.png");
        var buffer = new ImageBuffer(8, 6, Enumerable.Repeat((byte)220, 8 * 6 * 3).ToArray(), 300, 300, ".png");
        ImageFileCodec.Save(buffer, sourcePath, ImageOutputFormat.Png, 95, true);
        var originalHash = Hashing.ComputeSha256(sourcePath);
        try
        {
            var writer = new SafeImageWriter();
            var first = writer.Write(buffer, sourcePath, outputDirectory, new CleanupSettingsSnapshot { OutputFormat = ImageOutputFormat.Png });
            var changed = new ImageBuffer(8, 6, Enumerable.Repeat((byte)180, 8 * 6 * 3).ToArray(), 300, 300, ".png");
            var second = writer.Write(changed, sourcePath, outputDirectory, new CleanupSettingsSnapshot { OutputFormat = ImageOutputFormat.Png });
            Assert.Equal(first.OutputPath, second.OutputPath);
            Assert.NotEqual(first.Sha256, second.Sha256);
            Assert.Single(Directory.GetFiles(outputDirectory, "page*.png"));
            Assert.Equal(originalHash, Hashing.ComputeSha256(sourcePath));
            Assert.Throws<CleanupValidationException>(() => writer.Write(buffer, sourcePath, sourceDirectory, new CleanupSettingsSnapshot()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SafeWriter_PreservesRelativeDirectoriesAndRejectsPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerRelative_{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "page.png");
        var buffer = new ImageBuffer(8, 6, Enumerable.Repeat((byte)220, 8 * 6 * 3).ToArray(), 300, 300, ".png");
        ImageFileCodec.Save(buffer, sourcePath, ImageOutputFormat.Png, 95, true);
        try
        {
            var writer = new SafeImageWriter();
            var result = writer.Write(buffer, sourcePath, outputDirectory,
                new CleanupSettingsSnapshot { OutputFormat = ImageOutputFormat.Png }, Path.Combine("year", "box", "page.png"));
            Assert.Equal(Path.Combine(outputDirectory, "year", "box", "page.png"), result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Throws<CleanupValidationException>(() => writer.Write(buffer, sourcePath, outputDirectory,
                new CleanupSettingsSnapshot { OutputFormat = ImageOutputFormat.Png }, Path.Combine("..", "escaped.png")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AuditReport_ReplacesPreviousReportFiles()
    {
        var output = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerAudit_{Guid.NewGuid():N}");
        var report = new BatchAuditReport
        {
            Application = "档案净页", Version = "test", OutputDirectory = output,
            StartedAt = DateTimeOffset.Now, FinishedAt = DateTimeOffset.Now,
            TotalCount = 0, SuccessCount = 0, FailedCount = 0, CancelledCount = 0, Pages = []
        };
        try
        {
            await AuditReportWriter.WriteAsync(report);
            await AuditReportWriter.WriteAsync(report);
            Assert.True(File.Exists(Path.Combine(output, "batch-report.json")));
            Assert.True(File.Exists(Path.Combine(output, "batch-report.csv")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public async Task ManualProtectionWinsOverManualRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerManual_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "page.png");
        var buffer = new ImageBuffer(12, 10, Enumerable.Repeat((byte)230, 12 * 10 * 3).ToArray(), 300, 300, ".png");
        ImageFileCodec.Save(buffer, source, ImageOutputFormat.Png, 95, true);
        try
        {
            var engine = new DocumentCleanupEngine();
            var analysis = await engine.AnalyzeAsync(new CleanupRequest(source, 300, 300, new CleanupSettingsSnapshot()), null, CancellationToken.None);
            var remove = new byte[12 * 10];
            var protect = new byte[12 * 10];
            remove[5 * 12 + 5] = 255;
            protect[5 * 12 + 5] = 255;
            remove[5 * 12 + 6] = 255;
            var result = await engine.RepairAsync(analysis, new ReviewDecisionSet(null,
                new MaskBuffer(12, 10, protect), new MaskBuffer(12, 10, remove)), CancellationToken.None);
            Assert.Equal(0, result.FinalRepairMask.Pixels[5 * 12 + 5]);
            Assert.Equal(255, result.FinalRepairMask.Pixels[5 * 12 + 6]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sha256AndCsvEscaping_AreDeterministic()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "archive-cleaner");
            Assert.Equal(Hashing.ComputeSha256(path), Hashing.ComputeSha256(path));
            Assert.Equal("\"中文,\"\"引号\"\"\n下一行\"", AuditReportWriter.EscapeCsv("中文,\"引号\"\n下一行"));
        }
        finally { File.Delete(path); }
    }
}
