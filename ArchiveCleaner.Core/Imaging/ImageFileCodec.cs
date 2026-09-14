using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ArchiveCleaner.Core.Contracts;
using OpenCvSharp;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace ArchiveCleaner.Core.Imaging;

public static class ImageFileCodec
{
    public static ImageBuffer Load(string filePath, double requestedDpiX = 0, double requestedDpiY = 0)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到源图片。", filePath);
        byte[] encoded;
        using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > int.MaxValue) throw new NotSupportedException("图片文件过大，无法读取。");
            encoded = new byte[stream.Length];
            stream.ReadExactly(encoded);
        }

        double metadataDpiX = 0;
        double metadataDpiY = 0;
        using (var metadataStream = new MemoryStream(encoded, writable: false))
        using (var image = Image.FromStream(metadataStream, useEmbeddedColorManagement: false, validateImageData: true))
        {
            if (string.Equals(Path.GetExtension(filePath), ".tif", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(filePath), ".tiff", StringComparison.OrdinalIgnoreCase))
            {
                var pageDimension = new FrameDimension(image.FrameDimensionsList[0]);
                if (image.GetFrameCount(pageDimension) > 1) throw new NotSupportedException("不支持多页 TIFF，请先拆分为单页图片。");
            }
            metadataDpiX = image.HorizontalResolution;
            metadataDpiY = image.VerticalResolution;
        }

        using var mat = Cv2.ImDecode(encoded, ImreadModes.Color);
        if (mat.Empty()) throw new InvalidDataException("图片无法解码或格式不受支持。");
        var dpiX = ImageGeometry.IsValidDpi(requestedDpiX) ? requestedDpiX : metadataDpiX;
        var dpiY = ImageGeometry.IsValidDpi(requestedDpiY) ? requestedDpiY : metadataDpiY;
        return OpenCvBuffers.ToImageBuffer(mat, dpiX, dpiY, Path.GetExtension(filePath).ToLowerInvariant());
    }

    public static void Save(ImageBuffer image, string filePath, ImageOutputFormat format, int jpegQuality, bool preserveDpi)
    {
        var effectiveFormat = ResolveFormat(format, image.Extension);
        using var bitmap = ToBitmap(image, preserveDpi);
        switch (effectiveFormat)
        {
            case ImageOutputFormat.Jpeg:
                var codec = ImageCodecInfo.GetImageEncoders().Single(item => item.FormatID == DrawingImageFormat.Jpeg.Guid);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(jpegQuality, 70, 100));
                    bitmap.Save(filePath, codec, parameters);
                }
                break;
            case ImageOutputFormat.Png:
                bitmap.Save(filePath, DrawingImageFormat.Png);
                break;
            case ImageOutputFormat.Tiff:
                bitmap.Save(filePath, DrawingImageFormat.Tiff);
                break;
            case ImageOutputFormat.Bmp:
                bitmap.Save(filePath, DrawingImageFormat.Bmp);
                break;
            default:
                throw new NotSupportedException($"不支持输出格式 {effectiveFormat}。");
        }
    }

    public static ImageOutputFormat ResolveFormat(ImageOutputFormat requested, string sourceExtension)
    {
        if (requested != ImageOutputFormat.SameAsSource) return requested;
        return sourceExtension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageOutputFormat.Jpeg,
            ".png" => ImageOutputFormat.Png,
            ".tif" or ".tiff" => ImageOutputFormat.Tiff,
            ".bmp" => ImageOutputFormat.Bmp,
            _ => throw new NotSupportedException($"无法按源格式输出扩展名 {sourceExtension}。")
        };
    }

    public static string GetExtension(ImageOutputFormat requested, string sourceExtension)
    {
        var resolved = ResolveFormat(requested, sourceExtension);
        if (requested == ImageOutputFormat.SameAsSource) return sourceExtension.ToLowerInvariant();
        return resolved switch
        {
            ImageOutputFormat.Jpeg => ".jpg",
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Tiff => ".tif",
            ImageOutputFormat.Bmp => ".bmp",
            _ => throw new NotSupportedException()
        };
    }

    private static Bitmap ToBitmap(ImageBuffer image, bool preserveDpi)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var dpiX = preserveDpi && ImageGeometry.IsValidDpi(image.DpiX) ? image.DpiX : ImageGeometry.DefaultDpi;
        var dpiY = preserveDpi && ImageGeometry.IsValidDpi(image.DpiY) ? image.DpiY : ImageGeometry.DefaultDpi;
        bitmap.SetResolution((float)dpiX, (float)dpiY);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var sourceStride = image.Width * 3;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(image.Pixels, y * sourceStride, data.Scan0 + y * data.Stride, sourceStride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
