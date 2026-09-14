using ArchiveCleaner.Core.Contracts;

namespace ArchiveCleaner.Core.Audit;

public enum BatchItemStatus { Success, Skipped, Failed, Cancelled }

public sealed record ManualDecisionAudit(string RegionId, ReviewDecision Decision);

public sealed class PageAuditRecord
{
    public required string SourcePath { get; init; }
    public string? OutputPath { get; init; }
    public required string SourceSha256 { get; init; }
    public string? OutputSha256 { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public double SourceDpiX { get; init; }
    public double SourceDpiY { get; init; }
    public double OutputDpiX { get; init; }
    public double OutputDpiY { get; init; }
    public required string SourceFormat { get; init; }
    public string? OutputFormat { get; init; }
    public required CleanupSettingsSnapshot Settings { get; init; }
    public int AutoRemoveRegionCount { get; init; }
    public long AutoRemovePixelArea { get; init; }
    public int ProtectedRegionCount { get; init; }
    public int NeedsReviewRegionCount { get; init; }
    public IReadOnlyList<ManualDecisionAudit> ManualDecisions { get; init; } = [];
    public long ManualProtectionPixelArea { get; init; }
    public long ManualRemovalPixelArea { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public BatchItemStatus Status { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class BatchAuditReport
{
    public required string Application { get; init; }
    public required string Version { get; init; }
    public required string OutputDirectory { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int CancelledCount { get; init; }
    public required IReadOnlyList<PageAuditRecord> Pages { get; init; }
}
