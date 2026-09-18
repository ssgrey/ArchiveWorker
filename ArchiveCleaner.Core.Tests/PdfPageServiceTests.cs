using ArchiveCleaner.Core.Pdf;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Services;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Contracts;

namespace ArchiveCleaner.Core.Tests;

public sealed class PdfPageServiceTests
{
    [Fact]
    public void SamplePdf_ExposesEightRenderablePages()
    {
        var path = FindSamplePdf();
        var service = new PdfPageService();
        var pages = service.GetPages(path);
        Assert.Equal(8, pages.Count);
        Assert.Contains(pages, page => page.WidthPoints > page.HeightPoints);
        var rendered = service.RenderPageToFile(path, 1);
        Assert.True(File.Exists(rendered));
        Assert.True(new FileInfo(rendered).Length > 0);
        var pageImages = pages.Select(page =>
        {
            var pagePath = service.RenderPageToFile(path, page.PageNumber);
            return (Image: ImageFileCodec.Load(pagePath, page.DpiX, page.DpiY), page.WidthPoints, page.HeightPoints);
        }).ToArray();
        var output = Path.Combine(Path.GetTempPath(), $"archive-cleaner-test-{Guid.NewGuid():N}.pdf");
        try
        {
            new PdfDocumentWriter().Write(output, pageImages);
            Assert.True(new FileInfo(output).Length > 0);
            Assert.Equal(8, PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(output), null));
        }
        finally { try { File.Delete(output); } catch { } }
    }

    [Fact]
    public void Catalog_ExpandsPdfIntoVirtualFolderAndPageItems()
    {
        var path = FindSamplePdf();
        var result = new ImageCatalogService().LoadDirectoryTree(Path.GetDirectoryName(path)!);
        var pdfFolder = result.Root!.Children.OfType<FolderTreeNode>().Single(node => node.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        Assert.True(pdfFolder.IsVirtual);
        Assert.Equal(8, pdfFolder.Children.OfType<ImageTreeNode>().Count());
        Assert.All(pdfFolder.Children.OfType<ImageTreeNode>(), node => Assert.True(node.Image.IsPdfPage));
    }

    [Fact]
    public async Task MainViewModel_ExportsPdfPagesAsSinglePdf()
    {
        var path = FindSamplePdf();
        var sourceDirectory = Path.GetDirectoryName(path)!;
        var output = Path.Combine(Path.GetTempPath(), $"archive-cleaner-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(sourceDirectory, replaceExisting: true);
            viewModel.OutputDirectory = output;
            var report = await viewModel.ProcessBatchAsync();
            Assert.Contains(report.Pages, page => page.Status == ArchiveCleaner.Core.Audit.BatchItemStatus.Success);
            var result = Path.Combine(output, Path.ChangeExtension(Path.GetFileName(path), ".pdf"));
            Assert.True(File.Exists(result), $"Expected PDF output at {result}");
            Assert.Equal(8, PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(result), null));
            var renderedOutput = Path.Combine(output, "output-page.png");
            PDFtoImage.Conversion.SavePng(renderedOutput, File.ReadAllBytes(result), 0, null, new PDFtoImage.RenderOptions(Dpi: 300));
            var original = ImageFileCodec.Load(new PdfPageService().RenderPageToFile(path, 1), 300, 300);
            var processed = ImageFileCodec.Load(renderedOutput, 300, 300);
            var changed = original.Pixels.Zip(processed.Pixels, (left, right) => Math.Abs(left - right)).Count(delta => delta > 12);
            Assert.True(changed > 100, $"The exported PDF page appears unchanged; changed samples: {changed}");
            var pageItem = viewModel.Images.First(item => item.IsPdfPage && item.PdfPageNumber == 1);
            var expectedAnalysis = await new DocumentCleanupEngine().AnalyzeAsync(
                new CleanupRequest(pageItem.FilePath, pageItem.DpiX, pageItem.DpiY, pageItem.ApplyBoundary(viewModel.Settings.CreateSnapshot())), null, default);
            var expectedRepair = await new DocumentCleanupEngine().RepairAsync(expectedAnalysis, new ReviewDecisionSet(), default);
            ImageFileCodec.Save(expectedRepair.RepairedImage, "D:/data/repair-page1.png", ImageOutputFormat.Png, 95, true);
            var removeAll = expectedAnalysis.Regions.Where(region => region.Status == RegionStatus.NeedsReview)
                .ToDictionary(region => region.Id, _ => ReviewDecision.Remove, StringComparer.Ordinal);
            var fullRepair = await new DocumentCleanupEngine().RepairAsync(expectedAnalysis, new ReviewDecisionSet(removeAll), default);
            ImageFileCodec.Save(fullRepair.RepairedImage, "D:/data/repair-page1-all.png", ImageOutputFormat.Png, 95, true);
            var matching = expectedRepair.RepairedImage.Pixels.Zip(processed.Pixels, (left, right) => Math.Abs(left - right)).Count(delta => delta < 28);
            Assert.True(matching > expectedRepair.RepairedImage.Pixels.Length * 0.75, $"Exported PDF does not match repaired page closely enough: {matching}");
        }
        finally { try { Directory.Delete(output, recursive: true); } catch { } }
    }

    [Fact]
    public void MainViewModel_RejectsPdfOutputInSourceDirectory()
    {
        var path = FindSamplePdf();
        var sourceDirectory = Path.GetDirectoryName(path)!;
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sourceDirectory, replaceExisting: true);

        Assert.Throws<CleanupValidationException>(() => viewModel.GetPlannedOutputPaths(sourceDirectory));
    }

    private static string FindSamplePdf()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(directory, "Files", "87-WS·1998·02-0001.pdf");
            if (File.Exists(candidate)) return candidate;
            directory = Directory.GetParent(directory)?.FullName ?? directory;
        }
        var root = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new FileNotFoundException("测试样例 PDF 不存在。");
        return Path.Combine(root, "Files", "87-WS·1998·02-0001.pdf");
    }
}
