using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using ArchiveCleaner.Core.Audit;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Batch;

public sealed record BatchProgress(int Completed, int Total, string FileName, BatchItemStatus? Status, string Message)
{
    public double Percent => Total == 0 ? 0 : Completed * 100d / Total;
}

public sealed class BatchRequest
{
    public required IReadOnlyList<string> SourceFiles { get; init; }
    public required string OutputDirectory { get; init; }
    public required CleanupSettingsSnapshot Settings { get; init; }
    public IReadOnlyDictionary<string, CleanupSettingsSnapshot> SettingsByFile { get; init; } = new Dictionary<string, CleanupSettingsSnapshot>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> RelativeOutputPathsByFile { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, ReviewDecisionSet> DecisionsByFile { get; init; } = new Dictionary<string, ReviewDecisionSet>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<ManualPixelChange>> CloneStampsByFile { get; init; } = new Dictionary<string, IReadOnlyList<ManualPixelChange>>(StringComparer.OrdinalIgnoreCase);
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
}

public sealed class BatchProcessor
{
    private readonly Func<IDocumentCleanupEngine> _engineFactory;
    private readonly SafeImageWriter _writer = new();

    public BatchProcessor(Func<IDocumentCleanupEngine> engineFactory) => _engineFactory = engineFactory;

    public async Task<BatchAuditReport> ProcessAsync(BatchRequest request, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        request.Settings.Validate();
        if (request.SourceFiles.Count == 0) throw new CleanupValidationException("批处理中至少需要一张源图片。");
        ValidateOutputDirectory(request.SourceFiles, request.OutputDirectory);
        var started = DateTimeOffset.Now;
        var records = new ConcurrentBag<PageAuditRecord>();
        var hasCorrelatedAnalysis = request.Settings.DetectRepeatedDefects && request.SourceFiles.Count > 1;
        var progressTotal = hasCorrelatedAnalysis ? request.SourceFiles.Count * 2 : request.SourceFiles.Count;
        var preparedAnalyses = hasCorrelatedAnalysis
            ? await PrepareCorrelatedAnalysesWithProgressAsync(request, progress, progressTotal, cancellationToken)
            : null;
        var completed = 0;
        OperationCanceledException? cancellation = null;
        try
        {
            await Parallel.ForEachAsync(request.SourceFiles, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(request.MaxDegreeOfParallelism, 1, 4),
                CancellationToken = cancellationToken
            }, async (file, token) =>
            {
                CleanupAnalysisResult? preparedAnalysis = null;
                if (preparedAnalyses is not null) preparedAnalyses.TryGetValue(Path.GetFullPath(file), out preparedAnalysis);
                var record = await ProcessOneAsync(file, request, preparedAnalysis, token);
                records.Add(record);
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new BatchProgress(hasCorrelatedAnalysis ? request.SourceFiles.Count + count : count, progressTotal,
                    Path.GetFileName(file), record.Status,
                    record.Status == BatchItemStatus.Success ? "写出完成" : record.Error ?? record.Status.ToString()));
            });
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        var finished = DateTimeOffset.Now;
        var ordered = records.OrderBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new BatchAuditReport
        {
            Application = "档案净页", Version = "1.0.0-mvp", OutputDirectory = Path.GetFullPath(request.OutputDirectory),
            StartedAt = started, FinishedAt = finished, TotalCount = request.SourceFiles.Count,
            SuccessCount = ordered.Count(item => item.Status == BatchItemStatus.Success),
            FailedCount = ordered.Count(item => item.Status == BatchItemStatus.Failed),
            CancelledCount = request.SourceFiles.Count - ordered.Length + ordered.Count(item => item.Status == BatchItemStatus.Cancelled),
            Pages = ordered
        };
        if (cancellation is not null) throw cancellation;
        return report;
    }

    private async Task<PageAuditRecord> ProcessOneAsync(string sourcePath, BatchRequest batch, CleanupAnalysisResult? preparedAnalysis, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var sourceHash = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceHash = Hashing.ComputeSha256(sourcePath);
            using var metadata = Image.FromFile(sourcePath);
            var sourceWidth = metadata.Width;
            var sourceHeight = metadata.Height;
            var sourceDpiX = metadata.HorizontalResolution;
            var sourceDpiY = metadata.VerticalResolution;
            var fullSourcePath = Path.GetFullPath(sourcePath);
            var settings = batch.SettingsByFile.TryGetValue(fullSourcePath, out var perFileSettings)
                ? perFileSettings
                : batch.Settings;
            settings.Validate();
            var analysis = preparedAnalysis ?? await _engineFactory().AnalyzeAsync(new CleanupRequest(sourcePath, sourceDpiX, sourceDpiY, settings), null, cancellationToken);
            batch.DecisionsByFile.TryGetValue(Path.GetFullPath(sourcePath), out var decisions);
            var repair = await _engineFactory().RepairAsync(analysis, decisions ?? new ReviewDecisionSet(), cancellationToken);
            if (batch.CloneStampsByFile.TryGetValue(Path.GetFullPath(sourcePath), out var cloneStamps))
                foreach (var stamp in cloneStamps) ManualPixelEditor.ApplyChange(repair.RepairedImage.Pixels, stamp, useAfter: true);
            batch.RelativeOutputPathsByFile.TryGetValue(Path.GetFullPath(sourcePath), out var relativeOutputPath);
            var written = _writer.Write(repair.RepairedImage, sourcePath, batch.OutputDirectory, batch.Settings, relativeOutputPath);
            stopwatch.Stop();
            return new PageAuditRecord
            {
                SourcePath = Path.GetFullPath(sourcePath), OutputPath = written.OutputPath, SourceSha256 = sourceHash, OutputSha256 = written.Sha256,
                SourceWidth = sourceWidth, SourceHeight = sourceHeight, OutputWidth = written.Width, OutputHeight = written.Height,
                SourceDpiX = sourceDpiX, SourceDpiY = sourceDpiY, OutputDpiX = written.DpiX, OutputDpiY = written.DpiY,
                SourceFormat = Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant(), OutputFormat = written.Format, Settings = settings,
                AutoRemoveRegionCount = analysis.Regions.Count(region => region.Status == RegionStatus.AutoRemove), AutoRemovePixelArea = repair.FinalRepairMask.ActivePixelCount,
                ProtectedRegionCount = CountComponents(analysis.ProtectionMask), NeedsReviewRegionCount = analysis.Regions.Count(region => region.Status == RegionStatus.NeedsReview),
                ManualDecisions = decisions?.Decisions.Select(item => new ManualDecisionAudit(item.Key, item.Value)).ToArray() ?? [],
                ManualProtectionPixelArea = decisions?.ManualProtectionMask?.ActivePixelCount ?? 0,
                ManualRemovalPixelArea = decisions?.ManualRemovalMask?.ActivePixelCount ?? 0,
                StartedAt = started, FinishedAt = DateTimeOffset.Now, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds, Status = BatchItemStatus.Success,
                Warnings = analysis.Warnings.Concat(repair.Warnings).Distinct().ToArray()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new PageAuditRecord
            {
                SourcePath = Path.GetFullPath(sourcePath), SourceSha256 = sourceHash, SourceFormat = Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant(),
                Settings = batch.SettingsByFile.TryGetValue(Path.GetFullPath(sourcePath), out var failedSettings) ? failedSettings : batch.Settings,
                StartedAt = started, FinishedAt = DateTimeOffset.Now, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                Status = BatchItemStatus.Failed, Error = exception.Message
            };
        }
    }

    private async Task<IReadOnlyDictionary<string, CleanupAnalysisResult>> PrepareCorrelatedAnalysesWithProgressAsync(
        BatchRequest request, IProgress<BatchProgress>? progress, int progressTotal, CancellationToken cancellationToken)
    {
        var analyses = new ConcurrentDictionary<string, CleanupAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        await Parallel.ForEachAsync(request.SourceFiles, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(request.MaxDegreeOfParallelism, 1, 4),
            CancellationToken = cancellationToken
        }, async (file, token) =>
        {
            try
            {
                using var metadata = Image.FromFile(file);
                var settings = request.SettingsByFile.TryGetValue(Path.GetFullPath(file), out var perFileSettings)
                    ? perFileSettings
                    : request.Settings;
                settings.Validate();
                var analysis = await _engineFactory().AnalyzeAsync(
                    new CleanupRequest(file, metadata.HorizontalResolution, metadata.VerticalResolution, settings), null, token);
                analyses[Path.GetFullPath(file)] = analysis;
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new BatchProgress(count, progressTotal, Path.GetFileName(file), null, "第一遍检测完成"));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new BatchProgress(count, progressTotal, Path.GetFileName(file), BatchItemStatus.Failed, "第一遍检测失败"));
            }
        });

        var orderedPaths = request.SourceFiles.Select(Path.GetFullPath).Where(analyses.ContainsKey).ToArray();
        var enhanced = await Task.Run(
            () => RepeatedDefectAnalyzer.Enhance(orderedPaths.Select(path => analyses[path]).ToArray()),
            cancellationToken);
        return orderedPaths.Select((path, index) => (path, analysis: enhanced[index]))
            .ToDictionary(item => item.path, item => item.analysis, StringComparer.OrdinalIgnoreCase);
    }

    private static int CountComponents(MaskBuffer mask)
    {
        using var mat = Imaging.OpenCvBuffers.ToMat(mask);
        return Math.Max(0, OpenCvSharp.Cv2.ConnectedComponents(mat, new OpenCvSharp.Mat()) - 1);
    }

    private static void ValidateOutputDirectory(IEnumerable<string> sources, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new CleanupValidationException("请选择输出目录。");
        var output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(output)) throw new CleanupValidationException("请选择已经存在的输出文件夹。");
        foreach (var source in sources)
        {
            var sourceDirectory = (Path.GetDirectoryName(Path.GetFullPath(source)) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
            if (output.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                throw new CleanupValidationException("输出目录不能与任何源图片目录完全相同。");
        }
    }
}
