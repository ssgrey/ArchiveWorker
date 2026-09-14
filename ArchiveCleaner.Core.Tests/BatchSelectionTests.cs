using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Core.Tests;

public sealed class BatchSelectionTests
{
    [Fact]
    public void TreeChecks_DefaultToSelectedAndPropagateThreeStates()
    {
        var root = CreateImageTree();
        try
        {
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            var treeRoot = Assert.Single(viewModel.TreeRoots.OfType<FolderTreeNode>());
            var images = EnumerateImages(treeRoot).ToArray();

            Assert.Equal(3, viewModel.SelectedCount);
            Assert.True(treeRoot.IsChecked);

            images[0].IsChecked = false;
            Assert.Null(treeRoot.IsChecked);
            Assert.Equal(2, viewModel.SelectedCount);

            images[1].IsChecked = false;
            Assert.Equal(1, viewModel.SelectedCount);
            var changedProperties = new List<string?>();
            treeRoot.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
            images[2].PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
            viewModel.SetAllImagesChecked(false);
            Assert.False(treeRoot.IsChecked);
            Assert.Equal(0, viewModel.SelectedCount);
            Assert.False(viewModel.CanRunSelectedBatch);
            Assert.Contains(nameof(CatalogTreeNode.IsChecked), changedProperties);

            viewModel.SetAllImagesChecked(true);
            Assert.True(treeRoot.IsChecked);
            Assert.Equal(3, viewModel.SelectedCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnalyzeAndExport_OnlyUseCheckedImages()
    {
        var root = CreateImageTree();
        var output = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerSelectedOutput_{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            var nodes = viewModel.TreeRoots.SelectMany(EnumerateImages).ToArray();
            nodes[1].IsChecked = false;
            nodes[2].IsChecked = false;

            await viewModel.AnalyzeBatchAsync();
            Assert.NotNull(nodes[0].Image.Analysis);
            Assert.Null(nodes[1].Image.Analysis);
            Assert.Null(nodes[2].Image.Analysis);

            viewModel.OutputDirectory = output;
            var report = await viewModel.ProcessBatchAsync();
            Assert.Equal(1, report.TotalCount);
            Assert.Equal(1, report.SuccessCount);
            Assert.Single(Directory.GetFiles(output, "*.png", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void PlannedOutputs_PreserveNestedSourceDirectoriesUnderSelectedRoot()
    {
        var root = CreateImageTree();
        var output = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerFlatOutput_{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            var nodes = viewModel.TreeRoots.SelectMany(EnumerateImages).ToArray();
            viewModel.SetAllImagesChecked(false);
            nodes[2].IsChecked = true;
            viewModel.OutputDirectory = output;

            var planned = Assert.Single(viewModel.GetPlannedOutputPaths(output));

            Assert.Equal(Path.Combine(output, "nested", "three.png"), planned);
            Assert.StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, planned,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeAndRemoveAll_ApprovesEveryDetectedReviewRegion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerAutoRemove_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SaveConflictImage(Path.Combine(root, "review.png"));
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            ConfigureReviewDetection(viewModel);

            var removedCount = await viewModel.AnalyzeAndRemoveAllReviewRegionsAsync();
            var image = Assert.Single(viewModel.Images);

            Assert.True(removedCount > 0);
            Assert.NotEmpty(image.ReviewRegions);
            Assert.All(image.ReviewRegions, region =>
                Assert.Equal(ReviewDecision.Remove, image.Decisions[region.Id]));
            Assert.Equal(0, viewModel.UnresolvedReviewRegionCount);
            Assert.Equal(100, viewModel.ProgressPercent);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnalyzeAndRemoveAll_PreCanceledTaskEndsWithoutCancellationException()
    {
        var root = CreateImageTree();
        try
        {
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var removedCount = await viewModel.AnalyzeAndRemoveAllReviewRegionsAsync(cancellation.Token);

            Assert.Equal(0, removedCount);
            Assert.Contains("已取消", viewModel.EngineStatus);
            Assert.All(viewModel.Images, image => Assert.Null(image.Analysis));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RemoveCheckedImages_OnlyChangesQueue()
    {
        var root = CreateImageTree();
        try
        {
            var sourceFiles = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            var nodes = viewModel.TreeRoots.SelectMany(EnumerateImages).ToArray();
            viewModel.SetAllImagesChecked(false);
            nodes[1].IsChecked = true;

            Assert.Equal(1, viewModel.RemoveCheckedImages());
            Assert.Equal(2, viewModel.TotalCount);
            Assert.All(sourceFiles, path => Assert.True(File.Exists(path)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateImageTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerSelection_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        SaveImage(Path.Combine(root, "one.png"), 232);
        SaveImage(Path.Combine(root, "two.png"), 236);
        SaveImage(Path.Combine(root, "nested", "three.png"), 240);
        return root;
    }

    private static void SaveImage(string path, byte shade)
    {
        var pixels = Enumerable.Repeat(shade, 80 * 100 * 3).ToArray();
        ImageFileCodec.Save(new ImageBuffer(80, 100, pixels, 300, 300, ".png"), path,
            ImageOutputFormat.Png, 95, preserveDpi: true);
    }

    private static void SaveConflictImage(string path)
    {
        const int width = 160;
        const int height = 120;
        var pixels = Enumerable.Repeat((byte)235, width * height * 3).ToArray();
        for (var y = 50; y < 64; y++)
        for (var x = 12; x < 26; x++)
        {
            var index = (y * width + x) * 3;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 20;
        }
        ImageFileCodec.Save(new ImageBuffer(width, height, pixels, 300, 300, ".png"), path,
            ImageOutputFormat.Png, 95, preserveDpi: true);
    }

    private static void ConfigureReviewDetection(MainViewModel viewModel)
    {
        var settings = viewModel.Settings;
        settings.CleanLeft = true;
        settings.CleanRight = settings.CleanTop = settings.CleanBottom = false;
        settings.LeftMarginMm = 8;
        settings.UseAdvancedDetectionSettings = true;
        settings.RelativeContrastSensitivity = 75;
        settings.AbsoluteDarknessSensitivity = 75;
        settings.SmallDefectSensitivity = 50;
        settings.TraceConnectionPixels = 0;
        settings.EdgeShadowTracePixels = 0;
        settings.AutoRemoveConfidencePercent = 95;
        settings.DetectRepeatedDefects = false;
        settings.DetectPageEdgeShadow = false;
        settings.MaskExpansionPixels = 0;
        settings.ProtectPrintedText = false;
        settings.ProtectHandwriting = false;
        settings.ProtectStamps = false;
        settings.ProtectTablesAndPageNumbers = false;
        settings.ProtectionPaddingPixels = 0;
    }

    private static IEnumerable<ImageTreeNode> EnumerateImages(CatalogTreeNode node)
    {
        if (node is ImageTreeNode image)
        {
            yield return image;
            yield break;
        }
        foreach (var child in ((FolderTreeNode)node).Children)
            foreach (var descendant in EnumerateImages(child)) yield return descendant;
    }
}
