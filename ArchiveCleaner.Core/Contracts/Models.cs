using System.Collections.ObjectModel;

namespace ArchiveCleaner.Core.Contracts;

public enum EdgeSide { Left, Right, Top, Bottom }
public enum DefectType { PageEdgeShadow, BindingHole, DarkStain, TearShadow, UnknownDarkRegion }
public enum RegionStatus { AutoRemove, Protected, NeedsReview, Ignored }
public enum ReviewDecision { Keep, Remove }
public enum RepairMode { PaperTexture, FastInpaint, SolidFill, CloneStamp, SpotHealing }
public enum ImageOutputFormat { SameAsSource, Jpeg, Png, Tiff, Bmp }

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed class ImageBuffer
{
    public ImageBuffer(int width, int height, byte[] pixels, double dpiX, double dpiY, string extension)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 3))
            throw new ArgumentException("图像像素缓冲区尺寸无效。", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        DpiX = dpiX;
        DpiY = dpiY;
        Extension = extension;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public double DpiX { get; }
    public double DpiY { get; }
    public string Extension { get; }
    public ImageBuffer Clone() => new(Width, Height, (byte[])Pixels.Clone(), DpiX, DpiY, Extension);
}

public sealed class MaskBuffer
{
    public MaskBuffer(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
            throw new ArgumentException("掩膜缓冲区尺寸无效。", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public long ActivePixelCount => Pixels.LongCount(value => value != 0);
}

public sealed record CleanupSettingsSnapshot
{
    public bool CleanLeft { get; init; } = true;
    public bool CleanRight { get; init; } = true;
    public bool CleanTop { get; init; } = true;
    public bool CleanBottom { get; init; } = true;
    public double LeftMarginMm { get; init; } = 20;
    public double RightMarginMm { get; init; } = 15;
    public double TopMarginMm { get; init; } = 12;
    public double BottomMarginMm { get; init; } = 15;
    public double DetectionSensitivity { get; init; } = 55;
    public bool UseAdvancedDetectionSettings { get; init; }
    public double RelativeContrastSensitivity { get; init; } = 55;
    public double AbsoluteDarknessSensitivity { get; init; } = 55;
    public double SmallDefectSensitivity { get; init; } = 50;
    public int TraceConnectionPixels { get; init; } = 2;
    public int EdgeShadowTracePixels { get; init; } = 8;
    public double AutoRemoveConfidencePercent { get; init; } = 75;
    public bool DetectRepeatedDefects { get; init; } = true;
    public bool DetectPageEdgeShadow { get; init; } = true;
    public int MaskExpansionPixels { get; init; } = 6;
    public bool ProtectPrintedText { get; init; } = true;
    public bool ProtectHandwriting { get; init; } = true;
    public bool ProtectStamps { get; init; } = true;
    public bool ProtectTablesAndPageNumbers { get; init; } = true;
    public double ContentProtectionStrength { get; init; } = 60;
    public int ProtectionPaddingPixels { get; init; } = 8;
    public bool BlockOnConflict { get; init; } = true;
    public RepairMode RepairMethod { get; init; } = RepairMode.PaperTexture;
    public double ColorFeathering { get; init; } = 58;
    public bool PreserveDimensionsAndDpi { get; init; } = true;
    public ImageOutputFormat OutputFormat { get; init; } = ImageOutputFormat.SameAsSource;
    public int JpegQuality { get; init; } = 95;

    public void Validate()
    {
        ValidateNonNegative(LeftMarginMm, "左侧候选范围");
        ValidateNonNegative(RightMarginMm, "右侧候选范围");
        ValidateNonNegative(TopMarginMm, "上侧候选范围");
        ValidateNonNegative(BottomMarginMm, "下侧候选范围");
        ValidateRange(DetectionSensitivity, 0, 100, "检测灵敏度");
        ValidateRange(RelativeContrastSensitivity, 0, 100, "浅痕检测灵敏度");
        ValidateRange(AbsoluteDarknessSensitivity, 0, 100, "深色区域灵敏度");
        ValidateRange(SmallDefectSensitivity, 0, 100, "小缺陷灵敏度");
        ValidateRange(TraceConnectionPixels, 0, 20, "断裂痕迹连接");
        ValidateRange(EdgeShadowTracePixels, 0, 40, "边影追踪距离");
        ValidateRange(AutoRemoveConfidencePercent, 50, 95, "自动清除严格度");
        ValidateRange(MaskExpansionPixels, 0, 30, "缺陷掩膜扩展");
        ValidateRange(ContentProtectionStrength, 0, 100, "内容保护强度");
        ValidateRange(ProtectionPaddingPixels, 0, 50, "保护区域外扩");
        ValidateRange(ColorFeathering, 0, 100, "颜色羽化");
        ValidateRange(JpegQuality, 70, 100, "JPEG 质量");
    }

    public void ValidateForImage(int width, int height, double dpiX, double dpiY)
    {
        Validate();
        if (width <= 0 || height <= 0)
            throw new CleanupValidationException("图像尺寸必须大于零。");
    }

    private static void ValidateNonNegative(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new CleanupValidationException($"{name}必须是非负数。");
    }

    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
            throw new CleanupValidationException($"{name}必须在 {minimum:0} 到 {maximum:0} 之间。");
    }
}

public sealed class CleanupValidationException(string message) : ArgumentException(message);

public sealed record CleanupRequest(
    string SourcePath,
    double DpiX,
    double DpiY,
    CleanupSettingsSnapshot Settings);

public sealed record ProcessingProgress(string Stage, double Percent, string Message);

public sealed class CandidateEdgeRegions
{
    public CandidateEdgeRegions(int imageWidth, int imageHeight, IReadOnlyDictionary<EdgeSide, PixelRect> regions)
    {
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        Regions = new ReadOnlyDictionary<EdgeSide, PixelRect>(new Dictionary<EdgeSide, PixelRect>(regions));
    }

    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public IReadOnlyDictionary<EdgeSide, PixelRect> Regions { get; }
    public bool TryGet(EdgeSide side, out PixelRect rectangle) => Regions.TryGetValue(side, out rectangle);
}

public sealed record DefectRegion(
    string Id,
    DefectType Type,
    RegionStatus Status,
    PixelRect Bounds,
    int PixelArea,
    EdgeSide SourceEdge,
    double Confidence,
    string Reason);

public sealed class CleanupAnalysisResult
{
    public required string SourcePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double DpiX { get; init; }
    public required double DpiY { get; init; }
    public required string Extension { get; init; }
    public required string Orientation { get; init; }
    public required CleanupSettingsSnapshot Settings { get; init; }
    public required CandidateEdgeRegions EdgeRegions { get; init; }
    public required MaskBuffer DefectMask { get; init; }
    public required MaskBuffer HighConfidenceDefectMask { get; init; }
    public required MaskBuffer ProtectionMask { get; init; }
    public required MaskBuffer ConflictMask { get; init; }
    public required MaskBuffer ReviewMask { get; init; }
    public required MaskBuffer AutoRemoveMask { get; init; }
    public required IReadOnlyList<DefectRegion> Regions { get; init; }
    public required ImageBuffer OverlayPreview { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class ReviewDecisionSet
{
    private readonly IReadOnlyDictionary<string, ReviewDecision> _decisions;
    public ReviewDecisionSet(
        IReadOnlyDictionary<string, ReviewDecision>? decisions = null,
        MaskBuffer? manualProtectionMask = null,
        MaskBuffer? manualRemovalMask = null,
        bool disableAutomaticRepair = false)
    {
        _decisions = decisions is null
            ? new Dictionary<string, ReviewDecision>()
            : new Dictionary<string, ReviewDecision>(decisions, StringComparer.Ordinal);
        ManualProtectionMask = manualProtectionMask;
        ManualRemovalMask = manualRemovalMask;
        DisableAutomaticRepair = disableAutomaticRepair;
    }

    public IReadOnlyDictionary<string, ReviewDecision> Decisions => _decisions;
    public MaskBuffer? ManualProtectionMask { get; }
    public MaskBuffer? ManualRemovalMask { get; }
    public bool DisableAutomaticRepair { get; }
    public bool TryGetDecision(string regionId, out ReviewDecision decision) => _decisions.TryGetValue(regionId, out decision);
}

public sealed class CleanupResult
{
    public required ImageBuffer RepairedImage { get; init; }
    public required MaskBuffer FinalRepairMask { get; init; }
    public required IReadOnlyList<string> AppliedRegionIds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
