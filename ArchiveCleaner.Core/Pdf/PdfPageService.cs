using PDFtoImage;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Pdf;

public sealed record PdfPageInfo(int PageNumber, int PixelWidth, int PixelHeight, double DpiX, double DpiY, double WidthPoints, double HeightPoints);

public sealed class PdfPageService
{
    private const int DefaultDpi = 300;
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "ArchiveCleaner", "pdf-pages");

    public IReadOnlyList<PdfPageInfo> GetPages(string pdfPath, int dpi = DefaultDpi)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("找不到 PDF 文件。", pdfPath);
        var pdfBytes = File.ReadAllBytes(pdfPath);
        var sizes = Conversion.GetPageSizes(pdfBytes, null);
        var pages = new List<PdfPageInfo>(sizes.Count);
        for (var index = 0; index < sizes.Count; index++)
        {
            var size = sizes[index];
            pages.Add(new PdfPageInfo(index + 1,
                Math.Max(1, (int)Math.Round(size.Width / 72d * dpi)),
                Math.Max(1, (int)Math.Round(size.Height / 72d * dpi)),
                dpi, dpi, size.Width, size.Height));
        }
        return pages;
    }

    public string GetCachedPagePath(string pdfPath, int pageNumber, int dpi = DefaultDpi)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        Directory.CreateDirectory(_cacheDirectory);
        var info = new FileInfo(pdfPath);
        var key = $"{Path.GetFullPath(pdfPath).GetHashCode():X8}-{info.Length:X}-{info.LastWriteTimeUtc.Ticks:X}-{pageNumber}-{dpi}.png";
        return Path.Combine(_cacheDirectory, key);
    }

    public string RenderPageToFile(string pdfPath, int pageNumber, int dpi = DefaultDpi)
    {
        var destination = GetCachedPagePath(pdfPath, pageNumber, dpi);
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Conversion.SavePng(temporary, File.ReadAllBytes(pdfPath), pageNumber - 1, null, new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false));
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally { TryDelete(temporary); }
    }

    public ImageBuffer LoadPage(string pdfPath, int pageNumber, int dpi = DefaultDpi)
    {
        var path = RenderPageToFile(pdfPath, pageNumber, dpi);
        return ImageFileCodec.Load(path, dpi, dpi);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

public sealed class PdfDocumentWriter
{
    public void Write(string outputPath, IReadOnlyList<(ImageBuffer Image, double WidthPoints, double HeightPoints)> pages, string? sourcePdfPath = null)
    {
        if (pages.Count == 0) throw new InvalidOperationException("PDF 至少需要一页。");
        if (sourcePdfPath is not null)
        {
            var source = Path.GetFullPath(sourcePdfPath);
            var output = Path.GetFullPath(outputPath);
            var sourceDirectory = Path.GetDirectoryName(source)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outputDirectory = Path.GetDirectoryName(output)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                (sourceDirectory is not null && outputDirectory is not null &&
                 outputDirectory.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("PDF 输出不能覆盖源文件或写入源文件所在目录。");
        }
        var document = new PdfSharpCore.Pdf.PdfDocument();
        var temporaryFiles = new List<string>();
        var temporaryOutput = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            foreach (var entry in pages)
            {
                var page = document.AddPage();
                page.Width = entry.WidthPoints;
                page.Height = entry.HeightPoints;
                var imagePath = Path.Combine(Path.GetTempPath(), $"archive-cleaner-pdf-{Guid.NewGuid():N}.jpg");
                ImageFileCodec.Save(entry.Image, imagePath, ImageOutputFormat.Jpeg, 95, true);
                temporaryFiles.Add(imagePath);
                using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page);
                using var image = PdfSharpCore.Drawing.XImage.FromFile(imagePath);
                gfx.DrawImage(image, 0, 0, page.Width, page.Height);
            }
            var directory = Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("无法确定 PDF 输出目录。");
            Directory.CreateDirectory(directory);
            document.Save(temporaryOutput);
            File.Move(temporaryOutput, outputPath, overwrite: true);
        }
        finally
        {
            foreach (var file in temporaryFiles) TryDelete(file);
            TryDelete(temporaryOutput);
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
