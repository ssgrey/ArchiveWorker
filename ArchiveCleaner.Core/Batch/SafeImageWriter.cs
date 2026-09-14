using System.Drawing;
using System.Security.Cryptography;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Batch;

public sealed record SafeWriteResult(string OutputPath, string Sha256, int Width, int Height, double DpiX, double DpiY, string Format);

public sealed class SafeImageWriter
{
    public static string GetDestinationPath(string sourcePath, string outputDirectory, CleanupSettingsSnapshot settings,
        string? relativeOutputPath = null)
    {
        settings.Validate();
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var outputFullPath = Path.GetFullPath(outputDirectory);
        var extension = ImageFileCodec.GetExtension(settings.OutputFormat, Path.GetExtension(sourceFullPath));
        var relativePath = relativeOutputPath ?? Path.GetFileName(sourceFullPath);
        if (Path.IsPathRooted(relativePath)) throw new CleanupValidationException("输出相对路径不能是绝对路径。");
        relativePath = Path.ChangeExtension(relativePath, extension);
        var destination = Path.GetFullPath(Path.Combine(outputFullPath, relativePath));
        var outputPrefix = outputFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
            throw new CleanupValidationException("输出相对路径不能离开输出目录。");
        if (destination.Equals(sourceFullPath, StringComparison.OrdinalIgnoreCase))
            throw new CleanupValidationException("输出文件不能覆盖源图片。");
        return destination;
    }

    public SafeWriteResult Write(ImageBuffer image, string sourcePath, string outputDirectory, CleanupSettingsSnapshot settings,
        string? relativeOutputPath = null)
    {
        var outputFullPath = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(outputFullPath)) throw new CleanupValidationException("请选择已经存在的输出文件夹。");
        var destination = GetDestinationPath(sourcePath, outputFullPath, settings, relativeOutputPath);
        var extension = Path.GetExtension(destination);
        var destinationDirectory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("无法确定输出目录。");
        var normalizedOutput = outputFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDestinationDirectory = destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedDestinationDirectory.Equals(normalizedOutput, StringComparison.OrdinalIgnoreCase))
            Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            ImageFileCodec.Save(image, temporaryPath, settings.OutputFormat, settings.JpegQuality, settings.PreserveDimensionsAndDpi);
            var info = new FileInfo(temporaryPath);
            if (!info.Exists || info.Length == 0) throw new InvalidDataException("临时输出文件为空。");
            using (var decoded = Image.FromFile(temporaryPath))
            {
                if (decoded.Width != image.Width || decoded.Height != image.Height) throw new InvalidDataException("输出图片尺寸验证失败。");
            }
            File.Move(temporaryPath, destination, overwrite: true);
            using var verified = Image.FromFile(destination);
            return new SafeWriteResult(destination, Hashing.ComputeSha256(destination), verified.Width, verified.Height,
                verified.HorizontalResolution, verified.VerticalResolution, extension.TrimStart('.').ToUpperInvariant());
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public static class Hashing
{
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
