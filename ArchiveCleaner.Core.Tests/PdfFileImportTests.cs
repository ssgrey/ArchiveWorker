using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Core.Tests;

public sealed class PdfFileImportTests
{
    [Fact]
    public void AddFiles_LoadsMixedFilesOnceAndRejectsPdfRotation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerImport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var pdf = Path.Combine(root, "document.PDF");
            CreatePdf(pdf);
            var image = Path.Combine(root, "page.png");
            ImageFileCodec.Save(new ImageBuffer(12, 8, new byte[12 * 8 * 3], 300, 300, ".png"), image, ImageOutputFormat.Png, 95, true);
            var vm = new MainViewModel();
            vm.ClearImageQueue();
            vm.AddFiles([pdf.ToLowerInvariant(), image, pdf, image]);
            Assert.Equal(3, vm.Images.Count);
            Assert.Equal(3, vm.SelectedCount);
            var rootNode = Assert.IsType<FolderTreeNode>(Assert.Single(vm.TreeRoots));
            var pdfNode = Assert.Single(rootNode.Children.OfType<FolderTreeNode>());
            Assert.True(pdfNode.IsVirtual);
            Assert.Equal("document.PDF", pdfNode.Name, ignoreCase: true);
            Assert.Equal(new[] { 1, 2 }, pdfNode.Children.OfType<ImageTreeNode>().Select(node => node.Image.PdfPageNumber));
            foreach (var page in vm.Images.Where(item => item.IsPdfPage))
            {
                vm.SelectedImage = page;
                var before = vm.SelectedPreview;
                Assert.True(vm.CanRotateSelected);
                Assert.False(vm.RotateSelectedClockwise());
                Assert.False(page.TryRotateClockwise());
                Assert.Equal("PDF图片节点不支持旋转", vm.EngineStatus);
                Assert.Equal(0, page.RotationQuarterTurns);
                Assert.Same(before, vm.SelectedPreview);
            }

            vm.LoadDirectory(root, replaceExisting: false);
            vm.LoadDirectories([root], replaceExisting: false);
            Assert.Equal(3, vm.Images.Count);
            Assert.Equal(3, vm.SelectedCount);
            vm.ClearImageQueue();
            vm.LoadDirectory(root, replaceExisting: true);
            vm.AddFiles([pdf, image]);
            Assert.Equal(3, vm.Images.Count);
            Assert.Equal(3, vm.SelectedCount);

            // Same filenames from separate folders must get distinct page output directories.
            var other = Path.Combine(root, "nested", new DirectoryInfo(root).Name);
            Directory.CreateDirectory(other);
            var otherPdf = Path.Combine(other, "document.PDF");
            File.Copy(pdf, otherPdf);
            vm.ClearImageQueue();
            vm.AddFiles([pdf, otherPdf]);
            Assert.Equal(4, vm.Images.Count);
            Assert.Equal(4, vm.Images.Select(item => item.RelativeOutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AddFiles_ReportsBrokenPdfAndContinuesLoadingValidFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerBadPdf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var broken = Path.Combine(root, "broken.pdf");
            var valid = Path.Combine(root, "valid.pdf");
            File.WriteAllText(broken, "not a PDF");
            CreatePdf(valid);
            var vm = new MainViewModel();
            vm.ClearImageQueue();
            var error = Assert.Throws<AggregateException>(() => vm.AddFiles([broken, valid]));
            Assert.Contains("broken.pdf", error.Message);
            Assert.Equal(2, vm.Images.Count);
            Assert.Equal(2, vm.SelectedCount);
            Assert.NotNull(vm.SelectedImage);
            vm.LoadDirectory(root, replaceExisting: true);
            Assert.Equal(2, vm.Images.Count);
            Assert.Contains("1 个无法读取文件", vm.EngineStatus);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CreatePdf(string path)
    {
        using var document = new PdfSharpCore.Pdf.PdfDocument();
        for (var i = 0; i < 2; i++)
        {
            var page = document.AddPage();
            page.Width = 24;
            page.Height = 36;
        }
        document.Save(path);
    }
}
