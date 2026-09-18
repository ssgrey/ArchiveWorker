using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Core.Protection;
using OpenCvSharp;

namespace ArchiveCleaner.Core.Detection;

public sealed class DocumentCleanupEngine : IDocumentCleanupEngine
{
    private readonly IContentProtectionDetector _protectionDetector;

    public DocumentCleanupEngine(IContentProtectionDetector? protectionDetector = null) =>
        _protectionDetector = protectionDetector ?? new OpenCvContentProtectionDetector();

    public Task<CleanupAnalysisResult> AnalyzeAsync(CleanupRequest request, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Analyze(request, progress, cancellationToken), cancellationToken);

    public Task<CleanupResult> RepairAsync(CleanupAnalysisResult analysis, ReviewDecisionSet decisions, CancellationToken cancellationToken) =>
        Task.Run(() => Repair(analysis, decisions, cancellationToken), cancellationToken);

    private CleanupAnalysisResult Analyze(CleanupRequest request, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        request.Settings.Validate();
        if (string.IsNullOrWhiteSpace(request.SourcePath)) throw new CleanupValidationException("源文件路径不能为空。");
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        progress?.Report(new ProcessingProgress("读取", 5, "正在读取源图片"));
        var source = ImageFileCodec.Load(request.SourcePath, request.DpiX, request.DpiY);
        cancellationToken.ThrowIfCancellationRequested();

        var dpiX = ImageGeometry.IsValidDpi(source.DpiX) ? source.DpiX : ImageGeometry.DefaultDpi;
        var dpiY = ImageGeometry.IsValidDpi(source.DpiY) ? source.DpiY : ImageGeometry.DefaultDpi;
        if (!ImageGeometry.IsValidDpi(source.DpiX) || !ImageGeometry.IsValidDpi(source.DpiY))
            warnings.Add("源图片 DPI 无效，候选范围换算临时使用 300 DPI。");
        request.Settings.ValidateForImage(source.Width, source.Height, dpiX, dpiY);
        var regions = ImageGeometry.CreateCandidateRegions(source.Width, source.Height, dpiX, dpiY, request.Settings);
        var parameters = DetectionParameters.Create(request.Settings, dpiX, dpiY);
        var candidateBuffer = ImageGeometry.CreateCandidateMask(regions);
        progress?.Report(new ProcessingProgress("内容保护", 22, "正在识别边缘文字、笔画、印章和线框"));
        var protection = _protectionDetector.Detect(source, regions, request.Settings, cancellationToken);
        warnings.AddRange(protection.Warnings);

        progress?.Report(new ProcessingProgress("缺陷检测", 45, "正在检测孔洞、撕裂、污斑和扫描边影"));
        using var color = OpenCvBuffers.ToMat(source);
        using var gray = new Mat();
        using var background = new Mat();
        using var difference = new Mat();
        using var relativeDark = new Mat();
        using var absoluteDark = new Mat();
        using var rawCandidate = new Mat();
        using var candidate = OpenCvBuffers.ToMat(candidateBuffer);
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, background, new Size(0, 0), parameters.BackgroundSigma);
        Cv2.Subtract(background, gray, difference);
        Cv2.Threshold(difference, relativeDark, parameters.DifferenceThreshold, 255, ThresholdTypes.Binary);
        Cv2.Threshold(gray, absoluteDark, parameters.DarknessThreshold, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(relativeDark, absoluteDark, rawCandidate);
        Cv2.BitwiseAnd(rawCandidate, candidate, rawCandidate);
        if (parameters.NoiseFilterRadius > 0)
        {
            var diameter = parameters.NoiseFilterRadius * 2 + 1;
            using var openKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(diameter, diameter));
            Cv2.MorphologyEx(rawCandidate, rawCandidate, MorphTypes.Open, openKernel);
        }
        if (parameters.ConnectionRadius > 0)
        {
            var diameter = parameters.ConnectionRadius * 2 + 1;
            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(diameter, diameter));
            Cv2.MorphologyEx(rawCandidate, rawCandidate, MorphTypes.Close, closeKernel);
        }

        if (request.Settings.DetectPageEdgeShadow && parameters.EdgeTraceDistance > 0)
            AddOuterEdgeShadows(gray, candidate, rawCandidate, parameters.DarknessThreshold + 18,
                parameters.EdgeSeedDepth, parameters.EdgeTraceDistance);

        cancellationToken.ThrowIfCancellationRequested();
        using var defectMask = Mat.Zeros(source.Height, source.Width, MatType.CV_8UC1).ToMat();
        using var highConfidenceMask = Mat.Zeros(source.Height, source.Width, MatType.CV_8UC1).ToMat();
        using var reviewMask = Mat.Zeros(source.Height, source.Width, MatType.CV_8UC1).ToMat();
        using var protectionMask = OpenCvBuffers.ToMat(protection.Mask);
        var detectedRegions = new List<DefectRegion>();
        var regionMasks = new List<(DefectRegion Region, Mat Mask)>();
        Cv2.FindContours(rawCandidate, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var area = Cv2.ContourArea(contour);
            var box = Cv2.BoundingRect(contour);
            if (area < parameters.MinimumArea || box.Width < parameters.MinimumDimension || box.Height < parameters.MinimumDimension || area > source.Width * source.Height * 0.12) continue;
            var edge = FindSourceEdge(box, regions);
            var (type, confidence, reason) = Classify(contour, box, area, edge, source.Width, source.Height, request.Settings, parameters);
            if (confidence < 0.46) continue;

            var regionMask = Mat.Zeros(source.Height, source.Width, MatType.CV_8UC1).ToMat();
            Cv2.DrawContours(regionMask, [contour], -1, Scalar.White, -1);
            if (parameters.MaskExpansion > 0)
            {
                var diameter = parameters.MaskExpansion * 2 + 1;
                using var expansion = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(diameter, diameter));
                Cv2.Dilate(regionMask, regionMask, expansion);
            }
            Cv2.BitwiseAnd(regionMask, candidate, regionMask);
            Cv2.BitwiseOr(defectMask, regionMask, defectMask);

            using var overlap = new Mat();
            Cv2.BitwiseAnd(regionMask, protectionMask, overlap);
            var hasConflict = Cv2.CountNonZero(overlap) > 0;
            var status = hasConflict ? RegionStatus.NeedsReview : confidence >= parameters.AutoRemoveConfidence ? RegionStatus.AutoRemove : RegionStatus.NeedsReview;
            var id = CreateStableRegionId(source.Width, source.Height, box, type, edge);
            var region = new DefectRegion(id, type, status, new PixelRect(box.X, box.Y, box.Width, box.Height), (int)Math.Round(area), edge, confidence,
                hasConflict ? $"{reason}；与内容保护区域重叠，已阻止自动修复" : confidence < parameters.AutoRemoveConfidence ? $"{reason}；置信度不足，需人工确认" : reason);
            detectedRegions.Add(region);
            regionMasks.Add((region, regionMask));
            if (status == RegionStatus.AutoRemove) Cv2.BitwiseOr(highConfidenceMask, regionMask, highConfidenceMask);
            else if (status == RegionStatus.NeedsReview) Cv2.BitwiseOr(reviewMask, regionMask, reviewMask);
        }

        using var conflictMask = new Mat();
        Cv2.BitwiseAnd(defectMask, protectionMask, conflictMask);
        using var autoRemoveMask = new Mat();
        Cv2.BitwiseAnd(highConfidenceMask, candidate, autoRemoveMask);
        Cv2.BitwiseAnd(autoRemoveMask, ~protectionMask, autoRemoveMask);
        if (request.Settings.BlockOnConflict) Cv2.BitwiseAnd(autoRemoveMask, ~conflictMask, autoRemoveMask);

        foreach (var (_, mask) in regionMasks) mask.Dispose();
        progress?.Report(new ProcessingProgress("预览", 82, "正在合成真实分析覆盖层"));
        var defectBuffer = OpenCvBuffers.ToMaskBuffer(defectMask);
        var highBuffer = OpenCvBuffers.ToMaskBuffer(highConfidenceMask);
        var conflictBuffer = OpenCvBuffers.ToMaskBuffer(conflictMask);
        var reviewBuffer = OpenCvBuffers.ToMaskBuffer(reviewMask);
        var autoBuffer = OpenCvBuffers.ToMaskBuffer(autoRemoveMask);
        var overlay = CreateOverlay(source, autoBuffer, protection.Mask, reviewBuffer);
        stopwatch.Stop();
        progress?.Report(new ProcessingProgress("完成", 100, "分析完成"));
        return new CleanupAnalysisResult
        {
            SourcePath = Path.GetFullPath(request.SourcePath), Width = source.Width, Height = source.Height,
            DpiX = dpiX, DpiY = dpiY, Extension = source.Extension,
            Orientation = source.Height >= source.Width ? "Portrait" : "Landscape", Settings = request.Settings,
            EdgeRegions = regions, DefectMask = defectBuffer, HighConfidenceDefectMask = highBuffer,
            ProtectionMask = protection.Mask, ConflictMask = conflictBuffer, ReviewMask = reviewBuffer, AutoRemoveMask = autoBuffer,
            Regions = detectedRegions.OrderBy(region => region.Bounds.Y).ThenBy(region => region.Bounds.X).ToArray(),
            OverlayPreview = overlay, Elapsed = stopwatch.Elapsed, Warnings = warnings.Distinct().ToArray()
        };
    }

    public static MaskBuffer CreateRepairMask(CleanupAnalysisResult analysis, ReviewDecisionSet decisions,
        CancellationToken cancellationToken = default) => BuildRepairPlan(analysis, decisions, cancellationToken).Mask;

    private static CleanupResult Repair(CleanupAnalysisResult analysis, ReviewDecisionSet decisions, CancellationToken cancellationToken)
    {
        analysis.Settings.Validate();
        var source = ImageFileCodec.Load(analysis.SourcePath, analysis.DpiX, analysis.DpiY);
        if (source.Width != analysis.Width || source.Height != analysis.Height) throw new InvalidDataException("源图片尺寸在分析后发生变化，请重新分析。");
        analysis.Settings.ValidateForImage(source.Width, source.Height, source.DpiX, source.DpiY);
        var plan = BuildRepairPlan(analysis, decisions, cancellationToken);
        var sourceProtection = decisions.DisableAutomaticRepair
            ? decisions.ManualProtectionMask
            : MergeMasks(analysis.ProtectionMask, decisions.ManualProtectionMask);
        var repaired = RepairPixels(source, plan.Mask, analysis.Settings, cancellationToken, sourceProtection);
        var warnings = analysis.Regions.Any(region => region.Status == RegionStatus.NeedsReview && !decisions.TryGetDecision(region.Id, out _))
            ? new[] { "仍有未确认的冲突区域；这些区域未执行修复。" }
            : Array.Empty<string>();
        return new CleanupResult { RepairedImage = repaired, FinalRepairMask = plan.Mask, AppliedRegionIds = plan.AppliedIds, Warnings = warnings };
    }

    private static (MaskBuffer Mask, IReadOnlyList<string> AppliedIds) BuildRepairPlan(
        CleanupAnalysisResult analysis, ReviewDecisionSet decisions, CancellationToken cancellationToken)
    {
        analysis.Settings.Validate();
        var finalPixels = decisions.DisableAutomaticRepair
            ? new byte[analysis.Width * analysis.Height]
            : (byte[])analysis.AutoRemoveMask.Pixels.Clone();
        var appliedIds = decisions.DisableAutomaticRepair
            ? new List<string>()
            : analysis.Regions.Where(region => region.Status == RegionStatus.AutoRemove).Select(region => region.Id).ToList();

        // Collect all approved NeedsReview regions
        var approvedRegions = new List<DefectRegion>();
        foreach (var region in analysis.Regions.Where(region => !decisions.DisableAutomaticRepair && region.Status == RegionStatus.NeedsReview))
        {
            if (decisions.TryGetDecision(region.Id, out var decision) && decision == ReviewDecision.Remove)
            {
                approvedRegions.Add(region);
                appliedIds.Add(region.Id);
            }
        }

        // If all NeedsReview regions are approved, add the entire ReviewMask
        var allNeedsReviewRegions = analysis.Regions.Where(r => r.Status == RegionStatus.NeedsReview).ToList();
        if (approvedRegions.Count == allNeedsReviewRegions.Count && approvedRegions.Count > 0)
        {
            // All NeedsReview regions approved - add entire ReviewMask
            for (var index = 0; index < finalPixels.Length; index++)
            {
                if (analysis.ReviewMask.Pixels[index] != 0)
                {
                    finalPixels[index] = 255;
                }
            }
        }
        else
        {
            // Partial approval - only add pixels within approved region bounds
            foreach (var region in approvedRegions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rectangle = region.Bounds;
                var padding = DetectionParameters.Create(analysis.Settings, analysis.DpiX, analysis.DpiY).MaskExpansion;
                var left = Math.Max(0, rectangle.X - padding);
                var top = Math.Max(0, rectangle.Y - padding);
                var right = Math.Min(analysis.Width, rectangle.Right + padding);
                var bottom = Math.Min(analysis.Height, rectangle.Bottom + padding);

                for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                {
                    var index = y * analysis.Width + x;
                    if (analysis.DefectMask.Pixels[index] != 0 || analysis.ReviewMask.Pixels[index] != 0)
                    {
                        finalPixels[index] = 255;
                    }
                }
            }
        }

        ApplyManualMask(finalPixels, decisions.ManualRemovalMask, analysis.Width, analysis.Height, add: true);
        // Explicit protection always wins when manual masks overlap.
        ApplyManualMask(finalPixels, decisions.ManualProtectionMask, analysis.Width, analysis.Height, add: false);
        return (new MaskBuffer(analysis.Width, analysis.Height, finalPixels), appliedIds);
    }

    private static void ApplyManualMask(byte[] finalPixels, MaskBuffer? manualMask, int width, int height, bool add)
    {
        if (manualMask is null) return;
        if (manualMask.Width != width || manualMask.Height != height)
        {
            // Silently skip mismatched manual masks - they're from a previous analysis with different dimensions
            return;
        }

        for (var index = 0; index < finalPixels.Length; index++)
        {
            if (manualMask.Pixels[index] != 0) finalPixels[index] = add ? (byte)255 : (byte)0;
        }
    }

    private static ImageBuffer RepairPixels(ImageBuffer source, MaskBuffer mask, CleanupSettingsSnapshot settings, CancellationToken cancellationToken, MaskBuffer? protectedMask = null)
    {
        if (mask.ActivePixelCount == 0) return source.Clone();
        if (settings.RepairMethod is RepairMode.CloneStamp or RepairMode.SpotHealing)
            return RepairByPatchTransfer(source, mask, settings, cancellationToken, protectedMask, settings.RepairMethod == RepairMode.SpotHealing);

        using var sourceMat = OpenCvBuffers.ToMat(source);
        using var maskMat = OpenCvBuffers.ToMat(mask);
        using var reconstructed = new Mat();
        switch (settings.RepairMethod)
        {
            case RepairMode.FastInpaint:
                Cv2.Inpaint(sourceMat, maskMat, reconstructed, 4, InpaintMethod.Telea);
                break;
            case RepairMode.SolidFill:
                Cv2.GaussianBlur(sourceMat, reconstructed, new Size(0, 0), 35);
                break;
            default:
                Cv2.Inpaint(sourceMat, maskMat, reconstructed, 6, InpaintMethod.Telea);
                ReconstructBoundaryPixels(reconstructed, mask);
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = OpenCvBuffers.ToImageBuffer(reconstructed, source.DpiX, source.DpiY, source.Extension);
        var output = (byte[])source.Pixels.Clone();
        var softness = 1 + settings.ColorFeathering / 18d;
        using var distance = new Mat();
        Cv2.DistanceTransform(maskMat, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        for (var i = 0; i < mask.Pixels.Length; i++)
        {
            if (mask.Pixels[i] == 0) continue;
            var y = i / mask.Width;
            var x = i - y * mask.Width;
            var alpha = Math.Clamp(0.62 + distance.At<float>(y, x) / softness * 0.38, 0.62, 1.0);
            var pixel = i * 3;
            for (var channel = 0; channel < 3; channel++)
                output[pixel + channel] = (byte)Math.Clamp(Math.Round(source.Pixels[pixel + channel] * (1 - alpha) + candidate.Pixels[pixel + channel] * alpha), 0, 255);
        }
        return new ImageBuffer(source.Width, source.Height, output, source.DpiX, source.DpiY, source.Extension);
    }

    private static MaskBuffer MergeMasks(MaskBuffer first, MaskBuffer? second)
    {
        if (second is null) return first;
        if (first.Width != second.Width || first.Height != second.Height)
            throw new CleanupValidationException("人工保护掩膜尺寸与当前图片不一致。");
        var pixels = (byte[])first.Pixels.Clone();
        for (var index = 0; index < pixels.Length; index++)
            if (second.Pixels[index] != 0) pixels[index] = 255;
        return new MaskBuffer(first.Width, first.Height, pixels);
    }

    // Patch transfer gives edge defects a real nearby paper texture instead of a blurred or purely interpolated fill.
    private static ImageBuffer RepairByPatchTransfer(ImageBuffer source, MaskBuffer mask, CleanupSettingsSnapshot settings,
        CancellationToken cancellationToken, MaskBuffer? protectedMask, bool healTone)
    {
        using var sourceMat = OpenCvBuffers.ToMat(source);
        using var maskMat = OpenCvBuffers.ToMat(mask);
        using var fallbackMat = new Mat();
        Cv2.Inpaint(sourceMat, maskMat, fallbackMat, 5, InpaintMethod.Telea);
        var fallback = OpenCvBuffers.ToImageBuffer(fallbackMat, source.DpiX, source.DpiY, source.Extension);
        var output = (byte[])fallback.Pixels.Clone();
        var forbidden = new byte[mask.Pixels.Length];
        for (var index = 0; index < forbidden.Length; index++)
            forbidden[index] = (byte)(mask.Pixels[index] != 0 || protectedMask?.Pixels[index] != 0 ? 255 : 0);

        foreach (var component in FindMaskComponents(mask))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = FindPatchTransfer(source, mask, forbidden, component, healTone, cancellationToken);
            if (patch is null) continue;
            foreach (var targetIndex in component.Pixels)
            {
                var y = targetIndex / source.Width;
                var x = targetIndex - y * source.Width;
                var sourceX = x + patch.OffsetX;
                var sourceY = y + patch.OffsetY;
                if ((uint)sourceX >= (uint)source.Width || (uint)sourceY >= (uint)source.Height) continue;
                var sourceIndex = sourceY * source.Width + sourceX;
                if (forbidden[sourceIndex] != 0) continue;
                var destination = targetIndex * 3;
                var origin = sourceIndex * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = source.Pixels[origin + channel] + (healTone ? patch.ToneDelta[channel] : 0);
                    output[destination + channel] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        return new ImageBuffer(source.Width, source.Height, output, source.DpiX, source.DpiY, source.Extension);
    }

    private static PatchTransfer? FindPatchTransfer(ImageBuffer source, MaskBuffer targetMask, byte[] forbidden,
        MaskComponent component, bool healTone, CancellationToken cancellationToken)
    {
        var width = component.MaxX - component.MinX + 1;
        var height = component.MaxY - component.MinY + 1;
        var pad = Math.Clamp(Math.Max(width, height) / 2, 10, 36);
        var sampleLeft = Math.Max(0, component.MinX - pad);
        var sampleTop = Math.Max(0, component.MinY - pad);
        var sampleRight = Math.Min(source.Width - 1, component.MaxX + pad);
        var sampleBottom = Math.Min(source.Height - 1, component.MaxY + pad);
        var centerX = (component.MinX + component.MaxX) / 2;
        var centerY = (component.MinY + component.MaxY) / 2;
        var candidates = new List<(int OffsetX, int OffsetY)>();

        void AddCandidate(int sourceCenterX, int sourceCenterY)
        {
            var offsetX = sourceCenterX - centerX;
            var offsetY = sourceCenterY - centerY;
            if (offsetX == 0 && offsetY == 0) return;
            if (Math.Abs(offsetX) < 4 && Math.Abs(offsetY) < 4) return;
            if (!candidates.Contains((offsetX, offsetY))) candidates.Add((offsetX, offsetY));
        }

        // Prefer an inward parallel patch for page edges, then nearby patches for local stains.
        if (component.MinX <= 3) AddCandidate(centerX + Math.Max(14, width + pad), centerY);
        if (component.MaxX >= source.Width - 4) AddCandidate(centerX - Math.Max(14, width + pad), centerY);
        if (component.MinY <= 3) AddCandidate(centerX, centerY + Math.Max(14, height + pad));
        if (component.MaxY >= source.Height - 4) AddCandidate(centerX, centerY - Math.Max(14, height + pad));
        foreach (var dy in new[] { -2, -1, 0, 1, 2 })
        foreach (var dx in new[] { -2, -1, 0, 1, 2 })
            AddCandidate(centerX + dx * (pad + Math.Max(width, 8)), centerY + dy * (pad + Math.Max(height, 8)));

        var step = Math.Max(24, Math.Min(80, Math.Max(width, height) + pad));
        for (var y = step / 2; y < source.Height && candidates.Count < 80; y += step)
        for (var x = step / 2; x < source.Width && candidates.Count < 80; x += step)
            AddCandidate(x, y);

        var bestScore = double.MaxValue;
        PatchTransfer? best = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = ScorePatch(source, targetMask, forbidden, component, candidate.OffsetX, candidate.OffsetY,
                sampleLeft, sampleTop, sampleRight, sampleBottom, out var toneDelta);
            if (score < bestScore)
            {
                bestScore = score;
                best = new PatchTransfer(candidate.OffsetX, candidate.OffsetY, toneDelta);
            }
        }

        return bestScore <= 78 ? best : null;
    }

    private static double ScorePatch(ImageBuffer source, MaskBuffer targetMask, byte[] forbidden, MaskComponent component,
        int offsetX, int offsetY, int left, int top, int right, int bottom, out int[] toneDelta)
    {
        var total = new long[3];
        var sourceTotal = new long[3];
        var differences = 0d;
        var samples = 0;
        var validSourcePixels = 0;
        var sourceLeft = left + offsetX;
        var sourceTop = top + offsetY;
        var sourceRight = right + offsetX;
        var sourceBottom = bottom + offsetY;
        if (sourceLeft < 0 || sourceTop < 0 || sourceRight >= source.Width || sourceBottom >= source.Height)
        {
            toneDelta = [0, 0, 0];
            return double.MaxValue;
        }

        var sampleStep = Math.Max(2, Math.Max(right - left, bottom - top) / 48);
        for (var y = top; y <= bottom; y += sampleStep)
        for (var x = left; x <= right; x += sampleStep)
        {
            var targetIndex = y * source.Width + x;
            if (targetMask.Pixels[targetIndex] != 0) continue;
            var sourceIndex = (y + offsetY) * source.Width + x + offsetX;
            if (forbidden[sourceIndex] != 0) continue;
            var targetPixel = targetIndex * 3;
            var sourcePixel = sourceIndex * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                total[channel] += source.Pixels[targetPixel + channel];
                sourceTotal[channel] += source.Pixels[sourcePixel + channel];
                differences += Math.Abs(source.Pixels[targetPixel + channel] - source.Pixels[sourcePixel + channel]);
            }
            samples++;
        }

        for (var y = sourceTop; y <= sourceBottom; y += sampleStep)
        for (var x = sourceLeft; x <= sourceRight; x += sampleStep)
            if (forbidden[y * source.Width + x] == 0) validSourcePixels++;
        if (samples < 8 || validSourcePixels < 8) { toneDelta = [0, 0, 0]; return double.MaxValue; }
        toneDelta = new int[3];
        for (var channel = 0; channel < 3; channel++)
            toneDelta[channel] = (int)Math.Round((double)(total[channel] - sourceTotal[channel]) / samples);
        return differences / (samples * 3d);
    }

    private static IReadOnlyList<MaskComponent> FindMaskComponents(MaskBuffer mask)
    {
        var visited = new bool[mask.Pixels.Length];
        var components = new List<MaskComponent>();
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Pixels.Length; start++)
        {
            if (mask.Pixels[start] == 0 || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            var pixels = new List<int>();
            var minX = mask.Width; var minY = mask.Height; var maxX = 0; var maxY = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                pixels.Add(index);
                var y = index / mask.Width;
                var x = index - y * mask.Width;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                foreach (var neighbor in Neighbors(index, x, y, mask.Width, mask.Height))
                {
                    if (visited[neighbor] || mask.Pixels[neighbor] == 0) continue;
                    visited[neighbor] = true;
                    queue.Enqueue(neighbor);
                }
            }
            components.Add(new MaskComponent(pixels, minX, minY, maxX, maxY));
        }
        return components;
    }

    private static IEnumerable<int> Neighbors(int index, int x, int y, int width, int height)
    {
        if (x > 0) yield return index - 1;
        if (x + 1 < width) yield return index + 1;
        if (y > 0) yield return index - width;
        if (y + 1 < height) yield return index + width;
    }

    private sealed record MaskComponent(IReadOnlyList<int> Pixels, int MinX, int MinY, int MaxX, int MaxY);
    private sealed record PatchTransfer(int OffsetX, int OffsetY, int[] ToneDelta);

    private static void ReconstructBoundaryPixels(Mat reconstructed, MaskBuffer mask)
    {
        var pixels = OpenCvBuffers.ToImageBuffer(reconstructed, 300, 300, ".bmp").Pixels;
        var width = mask.Width;
        var height = mask.Height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (mask.Pixels[index] == 0) continue;
            var nearest = FindInteriorSample(mask, x, y);
            if (nearest < 0) continue;
            var target = index * 3;
            var source = nearest * 3;
            pixels[target] = pixels[source]; pixels[target + 1] = pixels[source + 1]; pixels[target + 2] = pixels[source + 2];
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, reconstructed.Data, pixels.Length);
    }

    private static int FindInteriorSample(MaskBuffer mask, int x, int y)
    {
        var horizontalDirection = x < mask.Width / 2 ? 1 : -1;
        for (var step = 1; step <= Math.Min(400, mask.Width - 1); step++)
        {
            var sampleX = x + step * horizontalDirection;
            if (sampleX < 0 || sampleX >= mask.Width) break;
            var index = y * mask.Width + sampleX;
            if (mask.Pixels[index] == 0) return index;
        }
        var verticalDirection = y < mask.Height / 2 ? 1 : -1;
        for (var step = 1; step <= Math.Min(400, mask.Height - 1); step++)
        {
            var sampleY = y + step * verticalDirection;
            if (sampleY < 0 || sampleY >= mask.Height) break;
            var index = sampleY * mask.Width + x;
            if (mask.Pixels[index] == 0) return index;
        }
        return -1;
    }

    private static void AddOuterEdgeShadows(Mat gray, Mat candidate, Mat target, double threshold, int seedDepth, int traceDistance)
    {
        using var dark = new Mat();
        using var edgeConnected = Mat.Zeros(gray.Rows, gray.Cols, MatType.CV_8UC1).ToMat();
        Cv2.Threshold(gray, dark, Math.Min(210, threshold), 255, ThresholdTypes.BinaryInv);
        var strips = new[]
        {
            new Rect(0, 0, Math.Min(seedDepth, gray.Cols), gray.Rows),
            new Rect(Math.Max(0, gray.Cols - seedDepth), 0, Math.Min(seedDepth, gray.Cols), gray.Rows),
            new Rect(0, 0, gray.Cols, Math.Min(seedDepth, gray.Rows)),
            new Rect(0, Math.Max(0, gray.Rows - seedDepth), gray.Cols, Math.Min(seedDepth, gray.Rows))
        };
        foreach (var strip in strips)
        {
            using var source = new Mat(dark, strip);
            using var destination = new Mat(edgeConnected, strip);
            source.CopyTo(destination);
        }
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var expanded = new Mat();
        for (var iteration = 0; iteration < traceDistance; iteration++)
        {
            Cv2.Dilate(edgeConnected, expanded, kernel);
            Cv2.BitwiseAnd(expanded, dark, edgeConnected);
        }
        Cv2.BitwiseAnd(edgeConnected, candidate, edgeConnected);
        Cv2.BitwiseOr(target, edgeConnected, target);
    }

    private static (DefectType Type, double Confidence, string Reason) Classify(Point[] contour, Rect box, double area, EdgeSide edge, int width, int height, CleanupSettingsSnapshot settings, DetectionParameters parameters)
    {
        var perimeter = Math.Max(1, Cv2.ArcLength(contour, true));
        var circularity = Math.Clamp(4 * Math.PI * area / (perimeter * perimeter), 0, 1);
        var touchesOuter = box.X <= 3 || box.Y <= 3 || box.Right >= width - 3 || box.Bottom >= height - 3;
        var compactness = area / Math.Max(1, box.Width * box.Height);
        if (touchesOuter && settings.DetectPageEdgeShadow && (box.Height > parameters.EdgeClassificationLength || box.Width > parameters.EdgeClassificationLength))
            return (DefectType.PageEdgeShadow, 0.96, "与页面最外边缘相连的连续暗影");
        if (box.Width >= parameters.HoleMinimumSize && box.Width <= parameters.HoleMaximumSize &&
            box.Height >= parameters.HoleMinimumSize && box.Height <= parameters.HoleMaximumSize && circularity > 0.12 && compactness > 0.12)
            return (DefectType.BindingHole, Math.Clamp(0.78 + circularity * 0.18, 0, 0.96), "形状和位置符合装订孔或孔洞撕裂特征");
        if (Math.Max(box.Width, box.Height) > Math.Min(box.Width, box.Height) * 3.2)
            return (DefectType.TearShadow, 0.79, "细长暗区符合撕裂或折痕阴影特征");
        if (area > parameters.StainMinimumArea && compactness > 0.18)
            return (DefectType.DarkStain, 0.76, "局部亮度显著低于邻近纸张背景");
        return (DefectType.UnknownDarkRegion, 0.58, "边缘存在无法可靠分类的深色区域");
    }

    private static EdgeSide FindSourceEdge(Rect box, CandidateEdgeRegions regions)
    {
        var centerX = box.X + box.Width / 2d;
        var centerY = box.Y + box.Height / 2d;
        return regions.Regions
            .Where(pair => centerX >= pair.Value.X && centerX < pair.Value.Right && centerY >= pair.Value.Y && centerY < pair.Value.Bottom)
            .OrderBy(pair => pair.Key switch
            {
                EdgeSide.Left => centerX,
                EdgeSide.Right => regions.ImageWidth - centerX,
                EdgeSide.Top => centerY,
                _ => regions.ImageHeight - centerY
            })
            .Select(pair => pair.Key)
            .DefaultIfEmpty(EdgeSide.Left)
            .First();
    }

    private static string CreateStableRegionId(int width, int height, Rect box, DefectType type, EdgeSide edge)
    {
        var normalized = $"{type}|{edge}|{Math.Round(box.X * 10000d / width)}|{Math.Round(box.Y * 10000d / height)}|{Math.Round(box.Width * 10000d / width)}|{Math.Round(box.Height * 10000d / height)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private static ImageBuffer CreateOverlay(ImageBuffer source, MaskBuffer autoRemove, MaskBuffer protectedMask, MaskBuffer conflict)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var i = 0; i < protectedMask.Pixels.Length; i++)
        {
            if (protectedMask.Pixels[i] != 0) Blend(pixels, i, 45, 160, 65, 0.34);
            if (autoRemove.Pixels[i] != 0) Blend(pixels, i, 25, 45, 220, 0.48);
            if (conflict.Pixels[i] != 0) Blend(pixels, i, 20, 205, 240, 0.58);
        }
        return new ImageBuffer(source.Width, source.Height, pixels, source.DpiX, source.DpiY, source.Extension);
    }

    private static void Blend(byte[] pixels, int maskIndex, byte blue, byte green, byte red, double alpha)
    {
        var index = maskIndex * 3;
        pixels[index] = (byte)(pixels[index] * (1 - alpha) + blue * alpha);
        pixels[index + 1] = (byte)(pixels[index + 1] * (1 - alpha) + green * alpha);
        pixels[index + 2] = (byte)(pixels[index + 2] * (1 - alpha) + red * alpha);
    }
}
