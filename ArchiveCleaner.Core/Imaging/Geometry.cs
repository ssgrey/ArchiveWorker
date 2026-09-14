using ArchiveCleaner.Core.Contracts;

namespace ArchiveCleaner.Core.Imaging;

public static class ImageGeometry
{
    public const double DefaultDpi = 300;

    public static int MillimetersToPixels(double millimeters, double dpi, int imageLimit)
    {
        if (imageLimit < 0) throw new ArgumentOutOfRangeException(nameof(imageLimit));
        var effectiveDpi = IsValidDpi(dpi) ? dpi : DefaultDpi;
        return Math.Clamp((int)Math.Round(millimeters / 25.4 * effectiveDpi, MidpointRounding.AwayFromZero), 0, imageLimit);
    }

    public static bool IsValidDpi(double dpi) => dpi > 0 && !double.IsNaN(dpi) && !double.IsInfinity(dpi);

    public static CandidateEdgeRegions CreateCandidateRegions(int width, int height, double dpiX, double dpiY, CleanupSettingsSnapshot settings)
    {
        settings.ValidateForImage(width, height, dpiX, dpiY);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "图像尺寸必须大于零。");
        var regions = new Dictionary<EdgeSide, PixelRect>();
        if (settings.CleanLeft)
        {
            var depth = MillimetersToPixels(Math.Min(settings.LeftMarginMm, width / (IsValidDpi(dpiX) ? dpiX : DefaultDpi) * 25.4 * 0.5), dpiX, width);
            if (depth > 0) regions[EdgeSide.Left] = new PixelRect(0, 0, depth, height);
        }
        if (settings.CleanRight)
        {
            var depth = MillimetersToPixels(Math.Min(settings.RightMarginMm, width / (IsValidDpi(dpiX) ? dpiX : DefaultDpi) * 25.4 * 0.5), dpiX, width);
            if (depth > 0) regions[EdgeSide.Right] = new PixelRect(width - depth, 0, depth, height);
        }
        if (settings.CleanTop)
        {
            var depth = MillimetersToPixels(Math.Min(settings.TopMarginMm, height / (IsValidDpi(dpiY) ? dpiY : DefaultDpi) * 25.4 * 0.5), dpiY, height);
            if (depth > 0) regions[EdgeSide.Top] = new PixelRect(0, 0, width, depth);
        }
        if (settings.CleanBottom)
        {
            var depth = MillimetersToPixels(Math.Min(settings.BottomMarginMm, height / (IsValidDpi(dpiY) ? dpiY : DefaultDpi) * 25.4 * 0.5), dpiY, height);
            if (depth > 0) regions[EdgeSide.Bottom] = new PixelRect(0, height - depth, width, depth);
        }
        return new CandidateEdgeRegions(width, height, regions);
    }

    public static MaskBuffer CreateCandidateMask(CandidateEdgeRegions regions)
    {
        var pixels = new byte[checked(regions.ImageWidth * regions.ImageHeight)];
        foreach (var rectangle in regions.Regions.Values)
        {
            for (var y = rectangle.Y; y < rectangle.Bottom; y++)
            {
                Array.Fill(pixels, (byte)255, y * regions.ImageWidth + rectangle.X, rectangle.Width);
            }
        }
        return new MaskBuffer(regions.ImageWidth, regions.ImageHeight, pixels);
    }
}

public static class MaskMath
{
    public static MaskBuffer Intersect(MaskBuffer left, MaskBuffer right) => Combine(left, right, (a, b) => a != 0 && b != 0);
    public static MaskBuffer Subtract(MaskBuffer source, MaskBuffer excluded) => Combine(source, excluded, (a, b) => a != 0 && b == 0);
    public static MaskBuffer Union(MaskBuffer left, MaskBuffer right) => Combine(left, right, (a, b) => a != 0 || b != 0);

    public static bool IsSubsetOf(MaskBuffer mask, MaskBuffer allowed)
    {
        EnsureSameSize(mask, allowed);
        for (var i = 0; i < mask.Pixels.Length; i++)
            if (mask.Pixels[i] != 0 && allowed.Pixels[i] == 0) return false;
        return true;
    }

    private static MaskBuffer Combine(MaskBuffer left, MaskBuffer right, Func<byte, byte, bool> predicate)
    {
        EnsureSameSize(left, right);
        var result = new byte[left.Pixels.Length];
        for (var i = 0; i < result.Length; i++) result[i] = predicate(left.Pixels[i], right.Pixels[i]) ? (byte)255 : (byte)0;
        return new MaskBuffer(left.Width, left.Height, result);
    }

    private static void EnsureSameSize(MaskBuffer left, MaskBuffer right)
    {
        if (left.Width != right.Width || left.Height != right.Height) throw new ArgumentException("掩膜尺寸不一致。");
    }
}
