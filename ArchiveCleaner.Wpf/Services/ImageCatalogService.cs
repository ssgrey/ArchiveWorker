using System.IO;
using System.Windows.Media.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Pdf;
using System.Windows.Media;

namespace ArchiveCleaner.Wpf.Services;

public sealed class ImageCatalogService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp"
    };
    private readonly PdfPageService _pdfPages = new();

    public CatalogLoadResult LoadDirectoryTree(string directory, string? relativeOutputPrefix = null)
    {
        var fullRoot = Path.GetFullPath(directory);
        if (!Directory.Exists(fullRoot)) return new CatalogLoadResult(null, [], 0, 0);
        var images = new List<ArchiveImageItem>();
        var skippedDirectories = 0;
        var skippedFiles = 0;
        var root = BuildFolder(fullRoot, fullRoot, images, ref skippedDirectories, ref skippedFiles,
            includeEmptyRoot: true, relativeOutputPrefix);
        return new CatalogLoadResult(root?.Children.Count > 0 ? root : null, images, skippedDirectories, skippedFiles);
    }

    public ArchiveImageItem CreateItem(string filePath, string? relativeOutputPath = null)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return new ArchiveImageItem
        {
            FilePath = Path.GetFullPath(filePath),
            FileName = Path.GetFileName(filePath),
            RelativeOutputPath = relativeOutputPath ?? Path.GetFileName(filePath),
            PixelWidth = frame.PixelWidth,
            PixelHeight = frame.PixelHeight,
            DpiX = frame.DpiX,
            DpiY = frame.DpiY
        };
    }

    private FolderTreeNode? BuildFolder(string directory, string root, List<ArchiveImageItem> images,
        ref int skippedDirectories, ref int skippedFiles, bool includeEmptyRoot = false, string? relativeOutputPrefix = null)
    {
        var folder = new FolderTreeNode(new DirectoryInfo(directory).Name, directory);
        string[] files;
        string[] directories;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Where(IsSupportedFile)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            directories = Directory.EnumerateDirectories(directory)
                .Where(path => !IsReparsePoint(path))
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            skippedDirectories++;
            return null;
        }
        catch (IOException)
        {
            skippedDirectories++;
            return null;
        }

        foreach (var file in files)
        {
            try
            {
                var relativePath = Path.GetRelativePath(root, file);
                if (!string.IsNullOrWhiteSpace(relativeOutputPrefix))
                    relativePath = Path.Combine(relativeOutputPrefix, relativePath);
                if (IsPdf(file))
                {
                    var pdfFolder = new FolderTreeNode(Path.GetFileName(file), file, isVirtual: true);
                    var pages = _pdfPages.GetPages(file);
                    foreach (var page in pages)
                    {
                        var rendered = _pdfPages.RenderPageToFile(file, page.PageNumber);
                        var item = CreatePdfPageItem(file, page, rendered, Path.Combine(relativePath, $"第{page.PageNumber:000}页.png"));
                        images.Add(item);
                        pdfFolder.AddChild(new ImageTreeNode(item));
                    }
                    folder.AddChild(pdfFolder);
                }
                else
                {
                    var item = CreateItem(file, relativePath);
                    images.Add(item);
                    folder.AddChild(new ImageTreeNode(item));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or
                                               InvalidDataException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException or FormatException)
            {
                skippedFiles++;
            }
        }

        foreach (var childDirectory in directories)
        {
            var child = BuildFolder(childDirectory, root, images, ref skippedDirectories, ref skippedFiles,
                relativeOutputPrefix: relativeOutputPrefix);
            if (child is not null) folder.AddChild(child);
        }

        return folder.Children.Count > 0 || includeEmptyRoot ? folder : null;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    public BitmapSource LoadPreview(string filePath)
    {
        return LoadBitmap(filePath, 1400);
    }

    public bool IsSupportedImage(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    public bool IsSupportedFile(string filePath) => IsSupportedImage(filePath) || IsPdf(filePath);

    public BitmapSource LoadPreview(ArchiveImageItem item) => LoadPreview(item.FilePath);

    public IReadOnlyList<ArchiveImageItem> CreatePdfPages(string pdfPath, string relativeOutputPrefix)
    {
        var pages = _pdfPages.GetPages(pdfPath);
        return pages.Select(page => CreatePdfPageItem(
            pdfPath,
            page,
            _pdfPages.RenderPageToFile(pdfPath, page.PageNumber),
            Path.Combine(relativeOutputPrefix, $"第{page.PageNumber:000}页.png"))).ToArray();
    }

    private ArchiveImageItem CreatePdfPageItem(string pdfPath, PdfPageInfo page, string renderedPath, string relativeOutputPath) => new()
    {
        FilePath = renderedPath,
        FileName = $"第 {page.PageNumber} 页",
        RelativeOutputPath = relativeOutputPath,
        PdfSourcePath = Path.GetFullPath(pdfPath),
        PdfPageNumber = page.PageNumber,
        PdfWidthPoints = page.WidthPoints,
        PdfHeightPoints = page.HeightPoints,
        PixelWidth = page.PixelWidth,
        PixelHeight = page.PixelHeight,
        DpiX = page.DpiX,
        DpiY = page.DpiY
    };

    private static bool IsPdf(string filePath) => Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public BitmapSource CreateBitmapSource(ImageBuffer image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height,
            image.DpiX > 0 ? image.DpiX : 96, image.DpiY > 0 ? image.DpiY : 96,
            PixelFormats.Bgr24, null, image.Pixels, image.Width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadBitmap(string filePath, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.UriSource = new Uri(Path.GetFullPath(filePath), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed record CatalogLoadResult(FolderTreeNode? Root, IReadOnlyList<ArchiveImageItem> Images,
    int SkippedDirectoryCount, int SkippedFileCount);
