using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;
using OpenCvSharp;

namespace ArchiveCleaner.Core.Protection;

public sealed class OpenCvContentProtectionDetector : IContentProtectionDetector
{
    public ContentProtectionResult Detect(ImageBuffer source, CandidateEdgeRegions edgeRegions, CleanupSettingsSnapshot settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var color = OpenCvBuffers.ToMat(source);
        using var gray = new Mat();
        using var candidate = OpenCvBuffers.ToMat(ImageGeometry.CreateCandidateMask(edgeRegions));
        using var protection = Mat.Zeros(source.Height, source.Width, MatType.CV_8UC1).ToMat();
        var parameters = DetectionParameters.Create(settings, source.DpiX, source.DpiY);
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);

        using var dark = new Mat();
        using var absoluteDark = new Mat();
        Cv2.AdaptiveThreshold(gray, dark, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv,
            parameters.ProtectionAdaptiveBlockSize, parameters.ProtectionAdaptiveConstant);
        Cv2.Threshold(gray, absoluteDark, parameters.ProtectionDarknessThreshold, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(dark, absoluteDark, dark);
        Cv2.BitwiseAnd(dark, candidate, dark);

        if (settings.ProtectPrintedText) ProtectTextGroups(dark, protection, parameters.TextGroupingWidth);
        if (settings.ProtectHandwriting) ProtectHandwritingGroups(dark, protection);
        if (settings.ProtectStamps) ProtectColoredContent(color, candidate, protection, parameters.StampMinimumSaturation);
        if (settings.ProtectTablesAndPageNumbers)
        {
            ProtectLongLines(dark, protection, parameters.LongLineLength);
            ProtectPageNumberGroups(dark, protection, parameters.PageNumberGroupingWidth);
        }

        Cv2.BitwiseAnd(protection, candidate, protection);
        if (parameters.ProtectionPadding > 0)
        {
            var diameter = parameters.ProtectionPadding * 2 + 1;
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(diameter, diameter));
            Cv2.Dilate(protection, protection, kernel);
            Cv2.BitwiseAnd(protection, candidate, protection);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ContentProtectionResult(OpenCvBuffers.ToMaskBuffer(protection),
            ["传统规则无法完全区分黑色手写笔画与黑色污斑；重叠区域已按保守策略进入复核。"]);
    }

    private static void ProtectTextGroups(Mat dark, Mat protection, int horizontalKernelWidth)
    {
        Cv2.FindContours(dark, out var sourceContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var sourceBoxes = sourceContours.Select(Cv2.BoundingRect).ToArray();
        using var grouped = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(horizontalKernelWidth, 3));
        Cv2.MorphologyEx(dark, grouped, MorphTypes.Close, kernel);
        Cv2.FindContours(grouped, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            if (box.Width < 18 || box.Width > 700 || box.Height < 4 || box.Height > 120 || box.Width < box.Height * 1.15) continue;
            var componentCount = sourceBoxes.Count(component =>
                component.X + component.Width / 2 >= box.X && component.X + component.Width / 2 < box.Right &&
                component.Y + component.Height / 2 >= box.Y && component.Y + component.Height / 2 < box.Bottom);
            if (componentCount < 2) continue;
            using var sourceRoi = new Mat(dark, box);
            using var targetRoi = new Mat(protection, box);
            Cv2.BitwiseOr(targetRoi, sourceRoi, targetRoi);
        }
    }

    private static void ProtectHandwritingGroups(Mat dark, Mat protection)
    {
        Cv2.FindContours(dark, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var boxes = contours.Select(Cv2.BoundingRect).Where(box => box.Width is >= 2 and <= 90 && box.Height is >= 3 and <= 90).ToArray();
        foreach (var box in boxes)
        {
            var neighbors = boxes.Where(other => other != box && Math.Abs(other.Y - box.Y) < 28 && Math.Abs(other.X - box.X) < 120).ToArray();
            var horizontalSpan = neighbors.Length == 0 ? 0 : neighbors.Append(box).Max(other => other.X + other.Width / 2) - neighbors.Append(box).Min(other => other.X + other.Width / 2);
            if (neighbors.Length < 2 || horizontalSpan < Math.Max(18, box.Width) || box.Width * box.Height > 2600) continue;
            using var sourceRoi = new Mat(dark, box);
            using var targetRoi = new Mat(protection, box);
            Cv2.BitwiseOr(targetRoi, sourceRoi, targetRoi);
        }
    }

    private static void ProtectColoredContent(Mat color, Mat candidate, Mat protection, double minimumSaturation)
    {
        using var hsv = new Mat();
        using var redLow = new Mat();
        using var redHigh = new Mat();
        using var blue = new Mat();
        using var saturated = new Mat();
        Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, minimumSaturation, 35), new Scalar(13, 255, 255), redLow);
        Cv2.InRange(hsv, new Scalar(165, minimumSaturation, 35), new Scalar(179, 255, 255), redHigh);
        Cv2.InRange(hsv, new Scalar(88, Math.Max(40, minimumSaturation - 5), 30), new Scalar(138, 255, 255), blue);
        Cv2.BitwiseOr(redLow, redHigh, saturated);
        Cv2.BitwiseOr(saturated, blue, saturated);
        Cv2.BitwiseAnd(saturated, candidate, saturated);
        Cv2.BitwiseOr(protection, saturated, protection);
    }

    private static void ProtectLongLines(Mat dark, Mat protection, int lineLength)
    {
        using var horizontal = new Mat();
        using var vertical = new Mat();
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(lineLength, 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, lineLength));
        Cv2.MorphologyEx(dark, horizontal, MorphTypes.Open, horizontalKernel);
        Cv2.MorphologyEx(dark, vertical, MorphTypes.Open, verticalKernel);
        var border = Math.Min(12, Math.Min(dark.Rows, dark.Cols) / 4);
        if (border > 0)
        {
            using var horizontalTop = new Mat(horizontal, new Rect(0, 0, horizontal.Cols, border));
            using var horizontalBottom = new Mat(horizontal, new Rect(0, horizontal.Rows - border, horizontal.Cols, border));
            using var verticalLeft = new Mat(vertical, new Rect(0, 0, border, vertical.Rows));
            using var verticalRight = new Mat(vertical, new Rect(vertical.Cols - border, 0, border, vertical.Rows));
            horizontalTop.SetTo(Scalar.Black);
            horizontalBottom.SetTo(Scalar.Black);
            verticalLeft.SetTo(Scalar.Black);
            verticalRight.SetTo(Scalar.Black);
        }
        Cv2.BitwiseOr(protection, horizontal, protection);
        Cv2.BitwiseOr(protection, vertical, protection);
    }

    private static void ProtectPageNumberGroups(Mat dark, Mat protection, int groupingWidth)
    {
        var y = (int)(dark.Rows * 0.82);
        var rectangle = new Rect(0, y, dark.Cols, dark.Rows - y);
        using var bottom = new Mat(dark, rectangle);
        using var target = new Mat(protection, rectangle);
        using var grouped = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(groupingWidth, 3));
        Cv2.MorphologyEx(bottom, grouped, MorphTypes.Close, kernel);
        Cv2.FindContours(grouped, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            if (box.Width is < 8 or > 260 || box.Height is < 5 or > 90) continue;
            using var sourceRoi = new Mat(bottom, box);
            using var targetRoi = new Mat(target, box);
            Cv2.BitwiseOr(targetRoi, sourceRoi, targetRoi);
        }
    }
}

public sealed class OnnxContentProtectionDetector : IContentProtectionDetector
{
    public ContentProtectionResult Detect(ImageBuffer source, CandidateEdgeRegions edgeRegions, CleanupSettingsSnapshot settings, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("未配置经过许可和验证的 ONNX 模型；请使用 OpenCV 保守内容保护检测器。");
}
