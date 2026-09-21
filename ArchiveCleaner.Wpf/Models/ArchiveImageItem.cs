using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArchiveCleaner.Wpf.ViewModels;
using ArchiveCleaner.Core.Contracts;
using System.Collections.ObjectModel;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Wpf.Models;

public enum ImageStatusKind
{
    Waiting,
    Analyzing,
    Completed,
    Review
    ,Failed
}

public enum ManualEditMode
{
    None,
    ProtectBrush,
    RemoveBrush,
    CloneStamp,
    Eraser
}

public sealed class ReviewRegionItem : ObservableObject
{
    private string _decisionText = "未确认";
    public required DefectRegion Region { get; init; }
    public string Id => Region.Id;
    public string DisplayText => $"{Region.Type} · {Region.SourceEdge} · {Region.Confidence:P0}";
    public string DecisionText { get => _decisionText; set => SetProperty(ref _decisionText, value); }
}

public sealed class ArchiveImageItem : ObservableObject
{
    private const int MaximumHistoryEntries = 100;
    private const int MaximumHistoryPixels = 5_000_000;
    private string _statusText = "等待处理";
    private ImageStatusKind _statusKind = ImageStatusKind.Waiting;
    private BitmapSource? _analysisPreview;
    private BitmapSource? _repairedPreview;
    private BitmapSource? _maskPreview;
    private bool _manualOnly;
    private bool _cleanLeft = true;
    private bool _cleanRight = true;
    private bool _cleanTop = true;
    private bool _cleanBottom = true;
    private double _leftMarginMm = 20;
    private double _rightMarginMm = 15;
    private double _topMarginMm = 12;
    private double _bottomMarginMm = 15;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string RelativeOutputPath { get; init; }
    public string? PdfSourcePath { get; init; }
    public int PdfPageNumber { get; init; }
    public double PdfWidthPoints { get; init; }
    public double PdfHeightPoints { get; init; }
    public bool IsPdfPage => !string.IsNullOrWhiteSpace(PdfSourcePath) && PdfPageNumber > 0;
    public int RotationQuarterTurns { get; private set; }
    public int DisplayWidth => RotationQuarterTurns % 2 == 0 ? PixelWidth : PixelHeight;
    public int DisplayHeight => RotationQuarterTurns % 2 == 0 ? PixelHeight : PixelWidth;
    public double DisplayDpiX => RotationQuarterTurns % 2 == 0 ? DpiX : DpiY;
    public double DisplayDpiY => RotationQuarterTurns % 2 == 0 ? DpiY : DpiX;

    public bool TryRotateClockwise()
    {
        if (IsPdfPage) return false;
        RotationQuarterTurns = (RotationQuarterTurns + 1) % 4;
        OnPropertyChanged(nameof(RotationQuarterTurns));
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
        OnPropertyChanged(nameof(DisplayDpiX));
        OnPropertyChanged(nameof(DisplayDpiY));
        OnPropertyChanged(nameof(DimensionsText));
        return true;
    }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public double DpiX { get; init; }
    public double DpiY { get; init; }
    public bool CleanLeft { get => _cleanLeft; set => SetProperty(ref _cleanLeft, value); }
    public bool CleanRight { get => _cleanRight; set => SetProperty(ref _cleanRight, value); }
    public bool CleanTop { get => _cleanTop; set => SetProperty(ref _cleanTop, value); }
    public bool CleanBottom { get => _cleanBottom; set => SetProperty(ref _cleanBottom, value); }
    public double LeftMarginMm { get => _leftMarginMm; set => SetProperty(ref _leftMarginMm, value); }
    public double RightMarginMm { get => _rightMarginMm; set => SetProperty(ref _rightMarginMm, value); }
    public double TopMarginMm { get => _topMarginMm; set => SetProperty(ref _topMarginMm, value); }
    public double BottomMarginMm { get => _bottomMarginMm; set => SetProperty(ref _bottomMarginMm, value); }
    public CleanupAnalysisResult? Analysis { get; set; }
    public bool ManualOnly
    {
        get => _manualOnly;
        set => SetProperty(ref _manualOnly, value);
    }
    public Dictionary<string, ReviewDecision> Decisions { get; } = new(StringComparer.Ordinal);
    public ObservableCollection<ReviewRegionItem> ReviewRegions { get; } = [];
    public byte[]? ManualProtectionPixels { get; set; }
    public byte[]? ManualRemovalPixels { get; set; }
    private List<IManualEditChange> UndoHistory { get; } = [];
    private List<IManualEditChange> RedoHistory { get; } = [];
    private List<ManualPixelChange> CloneStampHistory { get; } = [];
    public bool CanUndoManualEdit => UndoHistory.Count > 0;
    public bool CanRedoManualEdit => RedoHistory.Count > 0;

    public MaskBuffer? CreateManualProtectionMask() => ManualProtectionPixels is null || Analysis is null
        ? null
        : new MaskBuffer(Analysis.Width, Analysis.Height, (byte[])ManualProtectionPixels.Clone());

    public MaskBuffer? CreateManualRemovalMask() => ManualRemovalPixels is null || Analysis is null
        ? null
        : new MaskBuffer(Analysis.Width, Analysis.Height, (byte[])ManualRemovalPixels.Clone());

    public IReadOnlyList<ManualPixelChange> ActiveCloneStamps => CloneStampHistory;

    public void RecordManualEdit(IManualEditChange change)
    {
        if (change.PixelCount == 0) return;
        UndoHistory.Add(change);
        RedoHistory.Clear();
        while (UndoHistory.Count > MaximumHistoryEntries || UndoHistory.Sum(item => item.PixelCount) > MaximumHistoryPixels)
        {
            // Expiring undo entries must not remove edits that are still visible or exported.
            UndoHistory.RemoveAt(0);
        }
    }

    public void RecordCloneStamp(ManualPixelChange change)
    {
        if (change.PixelCount == 0) return;
        CloneStampHistory.Add(change);
        RecordManualEdit(change);
    }

    public void RemoveCloneStamp(ManualPixelChange change) => CloneStampHistory.Remove(change);
    public void ReplaceCloneStamps(IReadOnlyList<ManualPixelChange> stamps)
    {
        var snapshot = stamps.ToArray();
        CloneStampHistory.Clear();
        CloneStampHistory.AddRange(snapshot);
    }
    public void RestoreCloneStamp(ManualPixelChange change)
    {
        if (!CloneStampHistory.Contains(change)) CloneStampHistory.Add(change);
    }

    public void ClearManualEditHistory()
    {
        UndoHistory.Clear();
        RedoHistory.Clear();
        CloneStampHistory.Clear();
    }

    public void ClearManualCorrections()
    {
        Decisions.Clear();
        ManualProtectionPixels = null;
        ManualRemovalPixels = null;
        ClearManualEditHistory();
    }

    public IManualEditChange? TakeUndoManualEdit()
    {
        if (UndoHistory.Count == 0) return null;
        var change = UndoHistory[^1];
        UndoHistory.RemoveAt(UndoHistory.Count - 1);
        RedoHistory.Add(change);
        return change;
    }

    public IManualEditChange? TakeRedoManualEdit()
    {
        if (RedoHistory.Count == 0) return null;
        var change = RedoHistory[^1];
        RedoHistory.RemoveAt(RedoHistory.Count - 1);
        UndoHistory.Add(change);
        return change;
    }

    public BitmapSource? AnalysisPreview { get => _analysisPreview; set => SetProperty(ref _analysisPreview, value); }
    public BitmapSource? RepairedPreview { get => _repairedPreview; set => SetProperty(ref _repairedPreview, value); }
    public BitmapSource? MaskPreview { get => _maskPreview; set => SetProperty(ref _maskPreview, value); }

    public string DimensionsText => IsPdfPage ? $"第 {PdfPageNumber} 页 · {PixelWidth} × {PixelHeight} · {DpiX:0} DPI" : $"{DisplayWidth} × {DisplayHeight} · {DisplayDpiX:0} DPI";

    public void CopyBoundaryFrom(CleanupSettings source)
    {
        CleanLeft = source.CleanLeft;
        CleanRight = source.CleanRight;
        CleanTop = source.CleanTop;
        CleanBottom = source.CleanBottom;
        LeftMarginMm = source.LeftMarginMm;
        RightMarginMm = source.RightMarginMm;
        TopMarginMm = source.TopMarginMm;
        BottomMarginMm = source.BottomMarginMm;
    }

    public void CopyBoundaryFrom(ArchiveImageItem source)
    {
        CleanLeft = source.CleanLeft;
        CleanRight = source.CleanRight;
        CleanTop = source.CleanTop;
        CleanBottom = source.CleanBottom;
        LeftMarginMm = source.LeftMarginMm;
        RightMarginMm = source.RightMarginMm;
        TopMarginMm = source.TopMarginMm;
        BottomMarginMm = source.BottomMarginMm;
    }

    public void CopyBoundaryTo(CleanupSettings target)
    {
        target.CleanLeft = CleanLeft;
        target.CleanRight = CleanRight;
        target.CleanTop = CleanTop;
        target.CleanBottom = CleanBottom;
        target.LeftMarginMm = LeftMarginMm;
        target.RightMarginMm = RightMarginMm;
        target.TopMarginMm = TopMarginMm;
        target.BottomMarginMm = BottomMarginMm;
    }

    public CleanupSettingsSnapshot ApplyBoundary(CleanupSettingsSnapshot settings) => settings with
    {
        CleanLeft = CleanLeft,
        CleanRight = CleanRight,
        CleanTop = CleanTop,
        CleanBottom = CleanBottom,
        LeftMarginMm = LeftMarginMm,
        RightMarginMm = RightMarginMm,
        TopMarginMm = TopMarginMm,
        BottomMarginMm = BottomMarginMm
    };

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ImageStatusKind StatusKind
    {
        get => _statusKind;
        set
        {
            if (SetProperty(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public Brush StatusBrush => StatusKind switch
    {
        ImageStatusKind.Completed => Brushes.SeaGreen,
        ImageStatusKind.Review => Brushes.DarkGoldenrod,
        ImageStatusKind.Analyzing => Brushes.Teal,
        ImageStatusKind.Failed => Brushes.Firebrick,
        _ => Brushes.Gray
    };
}
