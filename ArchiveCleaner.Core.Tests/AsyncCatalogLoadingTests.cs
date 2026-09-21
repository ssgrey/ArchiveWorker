using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Core.Tests;

public sealed class AsyncCatalogLoadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerAsyncLoad_{Guid.NewGuid():N}");

    public AsyncCatalogLoadingTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Loading_ShowsBusyImmediatelyAndMergesFilesAfterCompletion()
    {
        var first = CreateImage("first.png");
        var second = CreateImage("second.png");
        var vm = new MainViewModel(loadSamples: false);
        var loading = vm.AddFilesAsync([first]);
        Assert.True(vm.IsLoading);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanLoadFiles);
        Assert.False(vm.CanInteractWithContent);
        Assert.False(vm.CanRunSelectedBatch);
        Assert.False(vm.CanRotateSelected);
        Assert.NotEmpty(vm.LoadingMessage);
        await loading;
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanLoadFiles);
        Assert.True(vm.SelectedPreview!.IsFrozen);
        var selected = vm.SelectedImage;
        await vm.AddFilesAsync([first, second]);
        Assert.Equal(2, vm.Images.Count);
        Assert.Equal(2, vm.SelectedCount);
        Assert.Single(vm.TreeRoots);
        Assert.Same(selected, vm.SelectedImage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelLoading_KeepsExistingSelectionEditsAndTree(bool directory)
    {
        var oldPath = CreateImage("old.png");
        var newPath = CreateImage("new.png");
        var vm = new MainViewModel(loadSamples: false);
        vm.AddFiles([oldPath]);
        vm.RotateSelectedClockwise();
        var selected = vm.SelectedImage;
        var tree = Assert.Single(vm.TreeRoots);
        var preview = vm.SelectedPreview;
        using var cancellation = new CancellationTokenSource();
        var loading = directory
            ? vm.LoadDirectoriesAsync([_root], true, cancellation.Token)
            : vm.AddFilesAsync([newPath], cancellation.Token);
        cancellation.Cancel();
        vm.ShowLoadingCancellation();
        Assert.Contains("正在取消", vm.LoadingMessage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Single(vm.Images);
        Assert.Same(selected, vm.SelectedImage);
        Assert.Same(tree, Assert.Single(vm.TreeRoots));
        Assert.Same(preview, vm.SelectedPreview);
        Assert.Equal(1, selected!.RotationQuarterTurns);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanRotateSelected);
        await vm.AddFilesAsync([newPath]);
        Assert.Equal(2, vm.Images.Count);
    }

    [Fact]
    public async Task LoadDirectories_ReplacesOnlyAfterReadingAndSummarizesBrokenFiles()
    {
        var old = CreateImage("old.png");
        var nested = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(nested);
        var incoming = Path.Combine(nested, "page.png");
        File.Copy(old, incoming);
        File.WriteAllText(Path.Combine(nested, "broken.pdf"), "invalid PDF");
        var vm = new MainViewModel(loadSamples: false);
        vm.AddFiles([old]);
        var oldItem = vm.SelectedImage;
        var loading = vm.LoadDirectoriesAsync([nested], true);
        Assert.Same(oldItem, Assert.Single(vm.Images));
        await loading;
        Assert.Equal(incoming, Assert.Single(vm.Images).FilePath);
        Assert.Equal("incoming", vm.BatchName);
        Assert.Contains("1 个无法读取文件", vm.EngineStatus);
        Assert.True(vm.SelectedPreview!.IsFrozen);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task PartialFailure_CommitsValidFilesAndAlwaysClearsLoadingState()
    {
        var image = CreateImage("valid.png");
        var invalid = Path.Combine(_root, "broken.pdf");
        File.WriteAllText(invalid, "invalid PDF");
        var vm = new MainViewModel(loadSamples: false);
        await Assert.ThrowsAsync<AggregateException>(() => vm.AddFilesAsync([invalid, image]));
        Assert.Equal(image, Assert.Single(vm.Images).FilePath);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanLoadFiles);
        Assert.True(vm.CanRunSelectedBatch);
    }

    [Fact]
    public async Task FatalFailure_PreservesExistingQueueAndClearsLoadingState()
    {
        var vm = new MainViewModel(loadSamples: false);
        vm.AddFiles([CreateImage("original.png")]);
        var selected = vm.SelectedImage;
        // Invalid source paths fail before catalog preparation can commit anything.
        await Assert.ThrowsAnyAsync<Exception>(() => vm.AddFilesAsync(["invalid\0.png"]));
        Assert.Same(selected, Assert.Single(vm.Images));
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PdfProgress_CanCancelBetweenPages(bool directory)
    {
        var path = Path.Combine(_root, "pages.pdf");
        using (var document = new PdfSharpCore.Pdf.PdfDocument())
        {
            for (var i = 0; i < 3; i++)
            {
                var page = document.AddPage();
                page.Width = 24;
                page.Height = 36;
            }
            document.Save(path);
        }
        using var cancellation = new CancellationTokenSource();
        var messages = new List<string>();
        var progress = new InlineProgress(value =>
        {
            messages.Add(value.Message);
            if (value.Message.Contains("第 2 / 3 页")) cancellation.Cancel();
        });
        var service = new ImageCatalogService();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (directory) service.LoadDirectoryTree(_root, progress: progress, cancellationToken: cancellation.Token);
            else service.LoadFiles([path], [], [], progress, cancellation.Token);
        });
        Assert.Contains(messages, value => value.Contains("第 1 / 3 页"));
        Assert.Contains(messages, value => value.Contains("第 2 / 3 页"));
        Assert.DoesNotContain(messages, value => value.Contains("第 3 / 3 页"));
    }

    private string CreateImage(string name)
    {
        var path = Path.Combine(_root, name);
        ImageFileCodec.Save(new ImageBuffer(16, 10, new byte[16 * 10 * 3], 300, 300, ".png"),
            path, ImageOutputFormat.Png, 95, true);
        return path;
    }

    private sealed class InlineProgress(Action<CatalogLoadProgress> report) : IProgress<CatalogLoadProgress>
    {
        public void Report(CatalogLoadProgress value) => report(value);
    }
}
