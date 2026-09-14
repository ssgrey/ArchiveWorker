using System.Runtime.InteropServices;
using ArchiveCleaner.Core.Contracts;
using OpenCvSharp;

namespace ArchiveCleaner.Core.Imaging;

internal static class OpenCvBuffers
{
    public static Mat ToMat(ImageBuffer buffer)
    {
        var mat = new Mat(buffer.Height, buffer.Width, MatType.CV_8UC3);
        Marshal.Copy(buffer.Pixels, 0, mat.Data, buffer.Pixels.Length);
        return mat;
    }

    public static Mat ToMat(MaskBuffer buffer)
    {
        var mat = new Mat(buffer.Height, buffer.Width, MatType.CV_8UC1);
        Marshal.Copy(buffer.Pixels, 0, mat.Data, buffer.Pixels.Length);
        return mat;
    }

    public static ImageBuffer ToImageBuffer(Mat source, double dpiX, double dpiY, string extension)
    {
        using var converted = EnsureBgr(source);
        using var continuous = converted.IsContinuous() ? converted.Clone() : converted.Clone();
        var pixels = new byte[checked(continuous.Rows * continuous.Cols * 3)];
        Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);
        return new ImageBuffer(continuous.Cols, continuous.Rows, pixels, dpiX, dpiY, extension);
    }

    public static MaskBuffer ToMaskBuffer(Mat source)
    {
        using var gray = EnsureGray(source);
        using var continuous = gray.Clone();
        var pixels = new byte[checked(continuous.Rows * continuous.Cols)];
        Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);
        return new MaskBuffer(continuous.Cols, continuous.Rows, pixels);
    }

    private static Mat EnsureBgr(Mat source)
    {
        if (source.Type() == MatType.CV_8UC3) return source.Clone();
        var result = new Mat();
        if (source.Channels() == 4) Cv2.CvtColor(source, result, ColorConversionCodes.BGRA2BGR);
        else if (source.Channels() == 1) Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGR);
        else throw new NotSupportedException("只支持灰度、BGR 或 BGRA 图像。");
        return result;
    }

    private static Mat EnsureGray(Mat source)
    {
        if (source.Type() == MatType.CV_8UC1) return source.Clone();
        var result = new Mat();
        Cv2.CvtColor(source, result, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return result;
    }
}
