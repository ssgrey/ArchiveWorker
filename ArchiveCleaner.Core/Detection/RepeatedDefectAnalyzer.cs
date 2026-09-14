using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Detection;

public static class RepeatedDefectAnalyzer
{
    public static IReadOnlyList<CleanupAnalysisResult> Enhance(IReadOnlyList<CleanupAnalysisResult> analyses)
    {
        if (analyses.Count < 2 || analyses.Any(item => !item.Settings.DetectRepeatedDefects)) return analyses;

        var mixedOrientation = analyses.Select(item => item.Orientation).Distinct(StringComparer.Ordinal).Count() > 1;
        var widthRatio = analyses.Max(item => item.Width) / (double)analyses.Min(item => item.Width);
        var heightRatio = analyses.Max(item => item.Height) / (double)analyses.Min(item => item.Height);
        var aspectRatios = analyses.Select(item => item.Width / (double)item.Height).ToArray();
        var mixedGeometry = mixedOrientation || widthRatio > 1.35 || heightRatio > 1.35 || aspectRatios.Max() / aspectRatios.Min() > 1.12;
        var requiredOccurrences = Math.Max(2, (int)Math.Ceiling(analyses.Count * 0.4));
        var occurrences = BuildOccurrenceCounts(analyses);
        var results = new CleanupAnalysisResult[analyses.Count];

        for (var pageIndex = 0; pageIndex < analyses.Count; pageIndex++)
        {
            var analysis = analyses[pageIndex];
            var parameters = DetectionParameters.Create(analysis.Settings, analysis.DpiX, analysis.DpiY);
            var promoted = new HashSet<string>(StringComparer.Ordinal);
            var updatedRegions = analysis.Regions.Select(region =>
            {
                if (!occurrences.TryGetValue((pageIndex, region.Id), out var count) || count < requiredOccurrences ||
                    region.Status != RegionStatus.NeedsReview || HasProtectionConflict(analysis, region)) return region;

                var boost = Math.Max(0.04, Math.Min(0.18, 0.08 + (count - requiredOccurrences) * 0.03) - (mixedGeometry ? 0.04 : 0));
                var confidence = Math.Min(0.99, region.Confidence + boost);
                if (confidence < parameters.AutoRemoveConfidence) return region with
                {
                    Confidence = confidence,
                    Reason = $"{region.Reason}；同批 {count} 页相近位置重复出现，已提高置信度"
                };

                promoted.Add(region.Id);
                return region with
                {
                    Status = RegionStatus.AutoRemove,
                    Confidence = confidence,
                    Reason = $"{region.Reason}；同批 {count} 页相近位置重复出现，达到自动清除门槛"
                };
            }).ToArray();

            results[pageIndex] = Rebuild(analysis, updatedRegions, promoted, mixedGeometry);
        }

        return results;
    }

    private static Dictionary<(int PageIndex, string RegionId), int> BuildOccurrenceCounts(IReadOnlyList<CleanupAnalysisResult> analyses)
    {
        var counts = new Dictionary<(int PageIndex, string RegionId), int>();
        for (var pageIndex = 0; pageIndex < analyses.Count; pageIndex++)
        foreach (var region in analyses[pageIndex].Regions)
        {
            var matchingPages = 1;
            for (var otherPage = 0; otherPage < analyses.Count; otherPage++)
            {
                if (otherPage == pageIndex || analyses[otherPage].Orientation != analyses[pageIndex].Orientation) continue;
                if (analyses[otherPage].Regions.Any(other => IsSimilar(region, analyses[pageIndex], other, analyses[otherPage]))) matchingPages++;
            }
            counts[(pageIndex, region.Id)] = matchingPages;
        }
        return counts;
    }

    private static bool IsSimilar(DefectRegion left, CleanupAnalysisResult leftPage, DefectRegion right, CleanupAnalysisResult rightPage)
    {
        if (left.Type != right.Type || left.SourceEdge != right.SourceEdge) return false;
        var leftCenterX = (left.Bounds.X + left.Bounds.Width / 2d) / leftPage.Width;
        var leftCenterY = (left.Bounds.Y + left.Bounds.Height / 2d) / leftPage.Height;
        var rightCenterX = (right.Bounds.X + right.Bounds.Width / 2d) / rightPage.Width;
        var rightCenterY = (right.Bounds.Y + right.Bounds.Height / 2d) / rightPage.Height;
        var leftWidth = left.Bounds.Width / (double)leftPage.Width;
        var leftHeight = left.Bounds.Height / (double)leftPage.Height;
        var rightWidth = right.Bounds.Width / (double)rightPage.Width;
        var rightHeight = right.Bounds.Height / (double)rightPage.Height;
        return Math.Abs(leftCenterX - rightCenterX) <= 0.018 &&
               Math.Abs(leftCenterY - rightCenterY) <= 0.018 &&
               Math.Abs(leftWidth - rightWidth) <= Math.Max(0.006, Math.Max(leftWidth, rightWidth) * 0.5) &&
               Math.Abs(leftHeight - rightHeight) <= Math.Max(0.006, Math.Max(leftHeight, rightHeight) * 0.5);
    }

    private static bool HasProtectionConflict(CleanupAnalysisResult analysis, DefectRegion region)
    {
        var rectangle = region.Bounds;
        for (var y = rectangle.Y; y < rectangle.Bottom; y++)
        for (var x = rectangle.X; x < rectangle.Right; x++)
        {
            var index = y * analysis.Width + x;
            if (analysis.DefectMask.Pixels[index] != 0 && analysis.ProtectionMask.Pixels[index] != 0) return true;
        }
        return false;
    }

    private static CleanupAnalysisResult Rebuild(CleanupAnalysisResult analysis, IReadOnlyList<DefectRegion> regions,
        IReadOnlySet<string> promoted, bool mixedGeometry)
    {
        var highConfidence = (byte[])analysis.HighConfidenceDefectMask.Pixels.Clone();
        var autoRemove = (byte[])analysis.AutoRemoveMask.Pixels.Clone();
        var review = (byte[])analysis.ReviewMask.Pixels.Clone();
        var expansion = DetectionParameters.Create(analysis.Settings, analysis.DpiX, analysis.DpiY).MaskExpansion;

        foreach (var region in regions.Where(item => promoted.Contains(item.Id)))
        {
            var left = Math.Max(0, region.Bounds.X - expansion);
            var top = Math.Max(0, region.Bounds.Y - expansion);
            var right = Math.Min(analysis.Width, region.Bounds.Right + expansion);
            var bottom = Math.Min(analysis.Height, region.Bounds.Bottom + expansion);
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var index = y * analysis.Width + x;
                if (analysis.DefectMask.Pixels[index] == 0 || analysis.ProtectionMask.Pixels[index] != 0) continue;
                highConfidence[index] = 255;
                autoRemove[index] = 255;
                review[index] = 0;
            }
        }

        var source = ImageFileCodec.Load(analysis.SourcePath, analysis.DpiX, analysis.DpiY);
        var overlayPixels = (byte[])source.Pixels.Clone();
        for (var index = 0; index < overlayPixels.Length / 3; index++)
        {
            if (analysis.ProtectionMask.Pixels[index] != 0) Blend(overlayPixels, index, 45, 160, 65, 0.34);
            if (autoRemove[index] != 0) Blend(overlayPixels, index, 25, 45, 220, 0.48);
            if (review[index] != 0) Blend(overlayPixels, index, 20, 205, 240, 0.58);
        }

        var warnings = analysis.Warnings.ToList();
        if (promoted.Count > 0) warnings.Add($"批次重复痕迹增强了 {promoted.Count} 个区域的置信度；内容保护仍优先生效。");
        if (mixedGeometry) warnings.Add("批次页面方向、尺寸或纵横比差异明显，已降低重复痕迹信号权重，并只在相同方向页面之间比较。");

        return new CleanupAnalysisResult
        {
            SourcePath = analysis.SourcePath, Width = analysis.Width, Height = analysis.Height,
            DpiX = analysis.DpiX, DpiY = analysis.DpiY, Extension = analysis.Extension, Orientation = analysis.Orientation,
            Settings = analysis.Settings, EdgeRegions = analysis.EdgeRegions, DefectMask = analysis.DefectMask,
            HighConfidenceDefectMask = new MaskBuffer(analysis.Width, analysis.Height, highConfidence),
            ProtectionMask = analysis.ProtectionMask, ConflictMask = analysis.ConflictMask,
            ReviewMask = new MaskBuffer(analysis.Width, analysis.Height, review),
            AutoRemoveMask = new MaskBuffer(analysis.Width, analysis.Height, autoRemove), Regions = regions,
            OverlayPreview = new ImageBuffer(analysis.Width, analysis.Height, overlayPixels, analysis.DpiX, analysis.DpiY, analysis.Extension),
            Elapsed = analysis.Elapsed, Warnings = warnings.Distinct().ToArray()
        };
    }

    private static void Blend(byte[] pixels, int maskIndex, byte blue, byte green, byte red, double alpha)
    {
        var index = maskIndex * 3;
        pixels[index] = (byte)(pixels[index] * (1 - alpha) + blue * alpha);
        pixels[index + 1] = (byte)(pixels[index + 1] * (1 - alpha) + green * alpha);
        pixels[index + 2] = (byte)(pixels[index + 2] * (1 - alpha) + red * alpha);
    }
}
