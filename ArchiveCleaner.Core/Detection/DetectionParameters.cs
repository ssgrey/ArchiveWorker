using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Detection;

internal sealed record DetectionParameters(
    double DifferenceThreshold,
    double DarknessThreshold,
    double AutoRemoveConfidence,
    int BackgroundSigma,
    int NoiseFilterRadius,
    int ConnectionRadius,
    int MinimumArea,
    int MinimumDimension,
    int EdgeSeedDepth,
    int EdgeTraceDistance,
    int MaskExpansion,
    int ProtectionPadding,
    int EdgeClassificationLength,
    int HoleMinimumSize,
    int HoleMaximumSize,
    int StainMinimumArea,
    int ProtectionAdaptiveBlockSize,
    double ProtectionAdaptiveConstant,
    double ProtectionDarknessThreshold,
    int TextGroupingWidth,
    int LongLineLength,
    int PageNumberGroupingWidth,
    double StampMinimumSaturation)
{
    public static DetectionParameters Create(CleanupSettingsSnapshot settings, double dpiX, double dpiY)
    {
        var dpi = EffectiveDpi(dpiX, dpiY);
        var relativeSensitivity = settings.UseAdvancedDetectionSettings
            ? settings.RelativeContrastSensitivity
            : settings.DetectionSensitivity;
        var darknessSensitivity = settings.UseAdvancedDetectionSettings
            ? settings.AbsoluteDarknessSensitivity
            : settings.DetectionSensitivity;
        var smallDefectSensitivity = settings.UseAdvancedDetectionSettings
            ? settings.SmallDefectSensitivity
            : settings.DetectionSensitivity;
        var connectionPixels = settings.UseAdvancedDetectionSettings ? settings.TraceConnectionPixels : 2;
        var edgeTracePixels = settings.UseAdvancedDetectionSettings ? settings.EdgeShadowTracePixels : 8;
        var autoRemoveConfidence = settings.UseAdvancedDetectionSettings
            ? settings.AutoRemoveConfidencePercent / 100d
            : Math.Clamp(0.87 - settings.DetectionSensitivity * 0.0022, 0.64, 0.87);

        var noiseRadiusAt300 = smallDefectSensitivity >= 80 ? 0 : smallDefectSensitivity >= 30 ? 1 : 2;
        var minimumAreaAt300 = Math.Clamp((int)Math.Round(20 - smallDefectSensitivity * 0.26), 1, 20);
        var protectionStrength = settings.ContentProtectionStrength;

        return new DetectionParameters(
            Math.Clamp(30 - relativeSensitivity * 0.22, 8, 30),
            Math.Clamp(112 + darknessSensitivity * 0.68, 112, 180),
            autoRemoveConfidence,
            ScalePixels(24, dpi, 1),
            ScalePixels(noiseRadiusAt300, dpi, noiseRadiusAt300 == 0 ? 0 : 1),
            ScalePixels(connectionPixels, dpi, connectionPixels == 0 ? 0 : 1),
            ScaleArea(minimumAreaAt300, dpi),
            ScalePixels(2, dpi, 1),
            ScalePixels(Math.Min(8, edgeTracePixels), dpi, edgeTracePixels == 0 ? 0 : 1),
            ScalePixels(edgeTracePixels, dpi, edgeTracePixels == 0 ? 0 : 1),
            ScalePixels(settings.MaskExpansionPixels, dpi, settings.MaskExpansionPixels == 0 ? 0 : 1),
            ScalePixels(settings.ProtectionPaddingPixels, dpi, settings.ProtectionPaddingPixels == 0 ? 0 : 1),
            ScalePixels(30, dpi, 1),
            ScalePixels(8, dpi, 1),
            ScalePixels(150, dpi, 1),
            ScaleArea(80, dpi),
            EnsureOdd(ScalePixels(41, dpi, 3)),
            Math.Clamp(20 - protectionStrength * 0.15, 5, 20),
            Math.Clamp(120 + protectionStrength, 120, 220),
            ScalePixels((int)Math.Round(14 + protectionStrength * 0.15), dpi, 3),
            ScalePixels(55, dpi, 3),
            ScalePixels(19, dpi, 3),
            Math.Clamp(100 - protectionStrength * 0.5, 45, 100));
    }

    private static double EffectiveDpi(double dpiX, double dpiY)
    {
        var x = ImageGeometry.IsValidDpi(dpiX) ? dpiX : ImageGeometry.DefaultDpi;
        var y = ImageGeometry.IsValidDpi(dpiY) ? dpiY : ImageGeometry.DefaultDpi;
        return (x + y) / 2d;
    }

    private static int ScalePixels(int pixelsAt300Dpi, double dpi, int minimum) =>
        Math.Max(minimum, (int)Math.Round(pixelsAt300Dpi * dpi / ImageGeometry.DefaultDpi, MidpointRounding.AwayFromZero));

    private static int ScaleArea(int areaAt300Dpi, double dpi)
    {
        var scale = dpi / ImageGeometry.DefaultDpi;
        return Math.Max(1, (int)Math.Round(areaAt300Dpi * scale * scale, MidpointRounding.AwayFromZero));
    }

    private static int EnsureOdd(int value) => value % 2 == 0 ? value + 1 : value;
}
