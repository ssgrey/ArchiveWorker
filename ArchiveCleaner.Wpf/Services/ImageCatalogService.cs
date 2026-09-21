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

    public CatalogLoadResult LoadDirectoryTree(string directory, string? relativeOutputPrefix = null,
        IProgress<CatalogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(directory);
        if (!Directory.Exists(fullRoot)) return new CatalogLoadResult(null, [], 0, 0);
        var images = new List<ArchiveImageItem>();
        var skippedDirectories = 0;
        var skippedFiles = 0;
        var root = BuildFolder(fullRoot, fullRoot, images, ref skippedDirectories, ref skippedFiles,
            includeEmptyRoot: true, relativeOutputPrefix, progress, cancellationToken);
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
        ref int skippedDirectories, ref int skippedFiles, bool includeEmptyRoot = false, string? relativeOutputPrefix = null,
        IProgress<CatalogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new($"正在扫描文件夹：{directory}"));
        var folder = new FolderTreeNode(new DirectoryInfo(directory).Name, directory);
        string[] files;
        string[] directories;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Select(path => { cancellationToken.ThrowIfCancellationRequested(); return path; })
                .Where(IsSupportedFile)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            directories = Directory.EnumerateDirectories(directory)
                .Select(path => { cancellationToken.ThrowIfCancellationRequested(); return path; })
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
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new($"正在加载：{Path.GetFileName(file)} · 已读取 {images.Count} 张图片 / 页面"));
            try
            {
                var relativePath = Path.GetRelativePath(root, file);
                if (!string.IsNullOrWhiteSpace(relativeOutputPrefix))
                    relativePath = Path.Combine(relativeOutputPrefix, relativePath);
                if (IsPdf(file))
                {
                    var pdfFolder = new FolderTreeNode(Path.GetFileName(file), file, isVirtual: true);
                    var pages = CreatePdfPages(file, relativePath, progress, cancellationToken);
                    foreach (var item in pages)
                    {
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
                                               InvalidDataException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException or FormatException or PDFtoImage.Exceptions.PdfException)
            {
                skippedFiles++;
            }
        }

        foreach (var childDirectory in directories)
        {
            var child = BuildFolder(childDirectory, root, images, ref skippedDirectories, ref skippedFiles,
                relativeOutputPrefix: relativeOutputPrefix, progress: progress, cancellationToken: cancellationToken);
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

    public IReadOnlyList<ArchiveImageItem> CreatePdfPages(string pdfPath, string relativeOutputPrefix,
        IProgress<CatalogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = _pdfPages.GetPages(pdfPath);
        var items = new List<ArchiveImageItem>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new($"正在加载 {Path.GetFileName(pdfPath)}，第 {page.PageNumber} / {pages.Count} 页"));
            var rendered = _pdfPages.RenderPageToFile(pdfPath, page.PageNumber);
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(CreatePdfPageItem(pdfPath, page, rendered,
                Path.Combine(relativeOutputPrefix, $"第{page.PageNumber:000}页.png")));
        }
        return items;
    }

    public CatalogLoadResult LoadFiles(IEnumerable<string> filePaths, HashSet<string> existingSources,
        HashSet<string> usedOutputPaths, IProgress<CatalogLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = new FolderTreeNode("单独添加", "standalone://images", isVirtual: true);
        var images = new List<ArchiveImageItem>();
        var errors = new List<Exception>();
        var files = filePaths.Where(IsSupportedFile).ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = files[index];
            progress?.Report(new($"正在加载文件 {index + 1} / {files.Length}：{Path.GetFileName(filePath)}"));
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                if (existingSources.Contains(fullPath)) continue;
                var parentName = new DirectoryInfo(Path.GetDirectoryName(fullPath)!).Name;
                var outputDirectory = Path.Combine("单独添加", string.IsNullOrWhiteSpace(parentName) ? "未分组" : parentName);
                var relativePath = Path.Combine(outputDirectory, Path.GetFileName(fullPath));
                for (var suffix = 2; usedOutputPaths.Contains(relativePath); suffix++)
                    relativePath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(fullPath)}_{suffix}{Path.GetExtension(fullPath)}");
                var items = IsSupportedImage(fullPath)
                    ? new[] { CreateItem(fullPath, relativePath) }
                    : CreatePdfPages(fullPath, relativePath, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var parent = root;
                if (IsPdf(fullPath))
                {
                    parent = new FolderTreeNode(Path.GetFileName(fullPath), fullPath, isVirtual: true);
                    root.AddChild(parent);
                }
                foreach (var item in items)
                {
                    images.Add(item);
                    parent.AddChild(new ImageTreeNode(item));
                }
                existingSources.Add(fullPath);
                usedOutputPaths.Add(relativePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or
                InvalidDataException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException or FormatException or PDFtoImage.Exceptions.PdfException)
            {
                errors.Add(new IOException($"{Path.GetFileName(filePath)}：{exception.Message}", exception));
            }
            progress?.Report(new($"已读取 {index + 1} / {files.Length} 个文件"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new CatalogLoadResult(root.ImageCount > 0 ? root : null, images, 0, errors.Count) { Errors = errors };
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
    int SkippedDirectoryCount, int SkippedFileCount)
{
    public IReadOnlyList<Exception> Errors { get; init; } = [];
}

public sealed record CatalogLoadProgress(string Message);
