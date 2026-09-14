namespace ArchiveCleaner.Core.Contracts;

public interface IDocumentCleanupEngine
{
    Task<CleanupAnalysisResult> AnalyzeAsync(
        CleanupRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken);

    Task<CleanupResult> RepairAsync(
        CleanupAnalysisResult analysis,
        ReviewDecisionSet decisions,
        CancellationToken cancellationToken);
}

public interface IContentProtectionDetector
{
    ContentProtectionResult Detect(
        ImageBuffer source,
        CandidateEdgeRegions edgeRegions,
        CleanupSettingsSnapshot settings,
        CancellationToken cancellationToken);
}

public sealed record ContentProtectionResult(MaskBuffer Mask, IReadOnlyList<string> Warnings);
