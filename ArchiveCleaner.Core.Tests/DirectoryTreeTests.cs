using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Core.Tests;

public sealed class DirectoryTreeTests
{
    [Fact]
    public void RecursiveCatalog_HidesEmptyBranchesAndBuildsImageRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerTree_{Guid.NewGuid():N}");
        var first = Path.Combine(root, "year-a", "box-1");
        var second = Path.Combine(root, "year-b");
        var empty = Path.Combine(root, "empty", "nested");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(empty);
        try
        {
            CreateImage(Path.Combine(first, "001.png"));
            CreateImage(Path.Combine(second, "001.png"));
            File.WriteAllText(Path.Combine(empty, "notes.txt"), "not an image");

            var result = new ImageCatalogService().LoadDirectoryTree(root);

            Assert.NotNull(result.Root);
            Assert.Equal(2, result.Images.Count);
            Assert.DoesNotContain(result.Root!.Children.OfType<FolderTreeNode>(), node => node.Name == "empty");
            Assert.Contains(result.Images, item => item.RelativeOutputPath == Path.Combine("year-a", "box-1", "001.png"));
            Assert.Contains(result.Images, item => item.RelativeOutputPath == Path.Combine("year-b", "001.png"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MultipleDirectories_UseDistinctRootOutputPrefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerMultiTree_{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first", "documents");
        var second = Path.Combine(root, "second", "documents");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            CreateImage(Path.Combine(first, "001.png"));
            CreateImage(Path.Combine(second, "001.png"));

            var viewModel = new MainViewModel();
            viewModel.LoadDirectories([first, second], replaceExisting: true);

            Assert.Equal(2, viewModel.Images.Count);
            Assert.Equal(2, viewModel.TreeRoots.Count);
            Assert.Equal(2, viewModel.Images.Select(item => item.RelativeOutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(viewModel.Images, item => item.RelativeOutputPath == Path.Combine("documents", "001.png"));
            Assert.Contains(viewModel.Images, item => item.RelativeOutputPath == Path.Combine("documents_2", "001.png"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RemoveTreeNode_RemovesFilesAndFoldersWithoutDeletingSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerRemoveTree_{Guid.NewGuid():N}");
        var firstPath = Path.Combine(root, "first", "001.png");
        var secondPath = Path.Combine(root, "second", "002.png");
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        try
        {
            CreateImage(firstPath);
            CreateImage(secondPath);
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            var rootNode = Assert.IsType<FolderTreeNode>(Assert.Single(viewModel.TreeRoots));
            var firstFolder = rootNode.Children.OfType<FolderTreeNode>().Single(node => node.Name == "first");
            var firstImage = Assert.IsType<ImageTreeNode>(Assert.Single(firstFolder.Children));

            Assert.Equal(1, viewModel.RemoveTreeNode(firstImage));
            Assert.Single(viewModel.Images);
            Assert.DoesNotContain(rootNode.Children.OfType<FolderTreeNode>(), node => node.Name == "first");
            Assert.True(File.Exists(firstPath));

            var secondFolder = rootNode.Children.OfType<FolderTreeNode>().Single(node => node.Name == "second");
            Assert.Equal(1, viewModel.RemoveTreeNode(secondFolder));
            Assert.Empty(viewModel.Images);
            Assert.Empty(viewModel.TreeRoots);
            Assert.True(File.Exists(secondPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CreateImage(string path)
    {
        var buffer = new ImageBuffer(8, 8, Enumerable.Repeat((byte)230, 8 * 8 * 3).ToArray(), 300, 300, ".png");
        ImageFileCodec.Save(buffer, path, ImageOutputFormat.Png, 95, true);
    }
}
