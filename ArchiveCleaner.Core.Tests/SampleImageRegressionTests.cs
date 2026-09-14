using ArchiveCleaner.Core.Batch;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class SampleImageRegressionTests
{
    [Fact]
    public async Task FiveRealSamples_AnalyzeRepairAndPreserveSourceFiles()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var files = Directory.GetFiles(sampleDirectory, "*.jpg").OrderBy(path => path).ToArray();
        Assert.Equal(5, files.Length);
        var sourceHashes = files.ToDictionary(path => path, Hashing.ComputeSha256);
        var settings = new CleanupSettingsSnapshot();
        var requestedOutput = Environment.GetEnvironmentVariable("ARCHIVE_CLEANER_SAMPLE_OUTPUT");
        var overlayDirectory = string.IsNullOrWhiteSpace(requestedOutput) ? null : Path.Combine(Path.GetFullPath(requestedOutput), "overlays");
        if (overlayDirectory is not null) Directory.CreateDirectory(overlayDirectory);
        var candidateCount = 0L;
        foreach (var file in files)
        {
            var engine = new DocumentCleanupEngine();
            var analysis = await engine.AnalyzeAsync(new CleanupRequest(file, 300, 300, settings), null, CancellationToken.None);
            var result = await engine.RepairAsync(analysis, new ReviewDecisionSet(), CancellationToken.None);
            if (overlayDirectory is not null)
            {
                var overlayPath = Path.Combine(overlayDirectory, Path.GetFileNameWithoutExtension(file) + "_overlay.png");
                ImageFileCodec.Save(analysis.OverlayPreview, overlayPath, ImageOutputFormat.Png, 95, true);
            }
            Assert.Equal(analysis.Width, result.RepairedImage.Width);
            Assert.Equal(analysis.Height, result.RepairedImage.Height);
            Assert.Equal(analysis.Width * analysis.Height * 3, result.RepairedImage.Pixels.Length);
            var candidateMask = ImageGeometry.CreateCandidateMask(analysis.EdgeRegions);
            Assert.True(MaskMath.IsSubsetOf(analysis.AutoRemoveMask, candidateMask));
            Assert.Equal(0, MaskMath.Intersect(analysis.AutoRemoveMask, analysis.ProtectionMask).ActivePixelCount);
            Assert.Equal(0, MaskMath.Intersect(analysis.AutoRemoveMask, analysis.ConflictMask).ActivePixelCount);
            var source = ImageFileCodec.Load(file, 300, 300);
            for (var pixel = 0; pixel < result.FinalRepairMask.Pixels.Length; pixel++)
            {
                if (result.FinalRepairMask.Pixels[pixel] != 0) continue;
                var offset = pixel * 3;
                Assert.Equal(source.Pixels[offset], result.RepairedImage.Pixels[offset]);
                Assert.Equal(source.Pixels[offset + 1], result.RepairedImage.Pixels[offset + 1]);
                Assert.Equal(source.Pixels[offset + 2], result.RepairedImage.Pixels[offset + 2]);
            }
            candidateCount += analysis.DefectMask.ActivePixelCount;
            Assert.Equal(sourceHashes[file], Hashing.ComputeSha256(file));
        }
        Assert.True(candidateCount > 0, "真实样图应至少检测到一个边缘缺陷像素。");
    }

    [Fact]
    public async Task BatchOutput_IsDecodableAndContainsImagesOnly()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var files = Directory.GetFiles(sampleDirectory, "*.jpg").OrderBy(path => path).ToArray();
        var hashes = files.ToDictionary(path => path, Hashing.ComputeSha256);
        var requestedOutput = Environment.GetEnvironmentVariable("ARCHIVE_CLEANER_SAMPLE_OUTPUT");
        var output = string.IsNullOrWhiteSpace(requestedOutput)
            ? Path.Combine(Path.GetTempPath(), $"ArchiveCleanerBatch_{Guid.NewGuid():N}")
            : Path.GetFullPath(requestedOutput);
        var preserveOutput = !string.IsNullOrWhiteSpace(requestedOutput);
        Directory.CreateDirectory(output);
        try
        {
            var processor = new BatchProcessor(() => new DocumentCleanupEngine());
            var report = await processor.ProcessAsync(new BatchRequest
            {
                SourceFiles = files, OutputDirectory = output, Settings = new CleanupSettingsSnapshot()
            }, null, CancellationToken.None);
            Assert.Equal(5, report.SuccessCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Empty(Directory.GetFiles(output, "batch-report.*", SearchOption.TopDirectoryOnly));
            foreach (var page in report.Pages)
            {
                Assert.NotNull(page.OutputPath);
                var decoded = ImageFileCodec.Load(page.OutputPath!, page.OutputDpiX, page.OutputDpiY);
                Assert.Equal(page.SourceWidth, decoded.Width);
                Assert.Equal(page.SourceHeight, decoded.Height);
                Assert.Equal(hashes[page.SourcePath], Hashing.ComputeSha256(page.SourcePath));
            }

            var secondReport = await processor.ProcessAsync(new BatchRequest
            {
                SourceFiles = files, OutputDirectory = output, Settings = new CleanupSettingsSnapshot()
            }, null, CancellationToken.None);
            Assert.Equal(5, secondReport.SuccessCount);
            Assert.Equal(5, Directory.GetFiles(output, "*.jpg").Length);
        }
        finally { if (!preserveOutput && Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [Theory]
    [InlineData(RepairMode.CloneStamp)]
    [InlineData(RepairMode.SpotHealing)]
    public async Task PatchBasedRepairModes_RepairRealSampleWithoutChangingDimensions(RepairMode mode)
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var file = Directory.GetFiles(sampleDirectory, "*.jpg").OrderBy(path => path).First();
        var sourceHash = Hashing.ComputeSha256(file);
        var settings = new CleanupSettingsSnapshot { RepairMethod = mode };
        var engine = new DocumentCleanupEngine();
        var analysis = await engine.AnalyzeAsync(new CleanupRequest(file, 300, 300, settings), null, CancellationToken.None);
        var result = await engine.RepairAsync(analysis, new ReviewDecisionSet(), CancellationToken.None);

        Assert.Equal(analysis.Width, result.RepairedImage.Width);
        Assert.Equal(analysis.Height, result.RepairedImage.Height);
        Assert.Equal(analysis.Width * analysis.Height * 3, result.RepairedImage.Pixels.Length);
        Assert.Equal(0, MaskMath.Intersect(result.FinalRepairMask, analysis.ProtectionMask).ActivePixelCount);
        Assert.Equal(sourceHash, Hashing.ComputeSha256(file));
    }
}
