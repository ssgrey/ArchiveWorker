using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;
using System.Windows.Media.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class RestoreCloneStampTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eraser_RestoresOverlappingStampsAndMasksInOneUndoStep(bool manualOnly)
    {
        var root = CreatePage();
        try
        {
            var vm = await LoadPageAsync(root);
            if (manualOnly) await vm.RestoreSelectedToOriginalAsync();
            var item = vm.SelectedImage!;
            var target = new PixelPoint(60, 40);
            var index = target.Y * item.PixelWidth + target.X;
            if (!manualOnly) item.Analysis!.AutoRemoveMask.Pixels[index] = 255;
            var original = ImageFileCodec.Load(item.FilePath, item.DpiX, item.DpiY).Pixels;
            await vm.ApplyManualEditsAsync(ManualEditMode.RemoveBrush, [target], 3);
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [target], 11, new PixelPoint(20, 20));
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [target], 11, new PixelPoint(30, 20));
            var stamped = Pixels(item.RepairedPreview!);
            Assert.NotEqual(original[index * 3], stamped[index * 3]);

            await vm.ApplyManualEditsAsync(ManualEditMode.Eraser, [target], 3);

            var restored = Pixels(item.RepairedPreview!);
            Assert.Equal(original.AsSpan(index * 3, 3).ToArray(), restored.AsSpan(index * 3, 3).ToArray());
            Assert.All(item.ActiveCloneStamps, stamp => Assert.DoesNotContain(index, stamp.Indices));
            var outside = (40 * item.PixelWidth + 64) * 3;
            Assert.Equal(stamped.AsSpan(outside, 3).ToArray(), restored.AsSpan(outside, 3).ToArray());
            Assert.Equal(0, item.ManualRemovalPixels![index]);
            Assert.Equal(manualOnly ? 0 : 255, item.ManualProtectionPixels![index]);

            await vm.UndoManualEditAsync();
            Assert.Equal(stamped, Pixels(item.RepairedPreview!));
            Assert.Equal(255, item.ManualRemovalPixels[index]);
            await vm.RedoManualEditAsync();
            Assert.Equal(restored, Pixels(item.RepairedPreview!));

            // A fresh stamp after restoring must still be allowed, and undo must reveal the restoration.
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [target], 3, new PixelPoint(10, 10));
            Assert.NotEqual(original[index * 3], Pixels(item.RepairedPreview!)[index * 3]);
            await vm.UndoManualEditAsync();
            Assert.Equal(restored, Pixels(item.RepairedPreview!));

            vm.OutputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(vm.OutputDirectory);
            vm.Settings.OutputFormat = "PNG";
            var report = await vm.ProcessBatchAsync();
            Assert.Equal(1, report.SuccessCount);
            var exported = ImageFileCodec.Load(Assert.Single(Directory.GetFiles(vm.OutputDirectory, "*.png", SearchOption.AllDirectories)), 300, 300);
            Assert.Equal(original.AsSpan(index * 3, 3).ToArray(), exported.Pixels.AsSpan(index * 3, 3).ToArray());
            Assert.Equal(restored.AsSpan(outside, 3).ToArray(), exported.Pixels.AsSpan(outside, 3).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Eraser_OnCloneOnlyArea_CanRemoveTheEntireStampAndUndo()
    {
        var root = CreatePage();
        try
        {
            var vm = await LoadPageAsync(root);
            await vm.RestoreSelectedToOriginalAsync();
            var item = vm.SelectedImage!;
            var original = Pixels(item.RepairedPreview!);
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [new PixelPoint(60, 40)], 9, new PixelPoint(20, 20));
            var stamped = Pixels(item.RepairedPreview!);
            await vm.ApplyManualEditsAsync(ManualEditMode.Eraser, [new PixelPoint(60, 40)], 11);
            Assert.Empty(item.ActiveCloneStamps);
            Assert.Equal(original, Pixels(item.RepairedPreview!));
            await vm.UndoManualEditAsync();
            Assert.Equal(stamped, Pixels(item.RepairedPreview!));
            await vm.UndoManualEditAsync();
            Assert.Equal(original, Pixels(item.RepairedPreview!));
            await vm.RedoManualEditAsync();
            await vm.RedoManualEditAsync();
            Assert.Equal(original, Pixels(item.RepairedPreview!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RestoreOriginal_CanBeRepeatedAfterFurtherEditsOrWithoutEdits()
    {
        var root = CreatePage();
        try
        {
            var vm = await LoadPageAsync(root);
            var item = vm.SelectedImage!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.True(vm.CanRestoreOriginal);
                await vm.RestoreSelectedToOriginalAsync();
                Assert.True(vm.CanRestoreOriginal);
                Assert.True(item.ManualOnly);
                Assert.Empty(item.ActiveCloneStamps);
                Assert.Null(item.ManualRemovalPixels);
                Assert.False(vm.CanUndoManualEdit);
                Assert.False(vm.CanRedoManualEdit);
                Assert.Equal(ImageFileCodec.Load(item.FilePath, 300, 300).Pixels, Pixels(item.RepairedPreview!));
                if (attempt == 0)
                {
                    await vm.ApplyManualEditsAsync(ManualEditMode.RemoveBrush, [new PixelPoint(60, 40)], 3);
                    await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [new PixelPoint(60, 40)], 9, new PixelPoint(20, 20));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reanalyze_KeepsTheRequestedImageWhenSelectionChangesDuringAnalysis()
    {
        var root = CreatePage();
        try
        {
            File.Copy(Path.Combine(root, "page.png"), Path.Combine(root, "second.png"));
            var vm = new MainViewModel();
            vm.LoadDirectory(root, replaceExisting: true);
            var target = vm.SelectedImage!;
            var other = vm.Images.Single(item => !ReferenceEquals(item, target));
            other.ManualOnly = true;
            other.Decisions["keep-other"] = ReviewDecision.Keep;

            var operation = vm.RefreshSelectedAsync();
            vm.SelectTreeNode(new ImageTreeNode(other));
            await operation;

            Assert.NotNull(target.Analysis);
            Assert.Null(other.Analysis);
            Assert.True(other.ManualOnly);
            Assert.Equal(ReviewDecision.Keep, other.Decisions["keep-other"]);
            Assert.Same(other, vm.SelectedImage);
            Assert.Contains(target.FileName, vm.EngineStatus);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HistoryLimit_DoesNotDiscardVisibleCloneStampPixels()
    {
        var item = new ArchiveImageItem { FilePath = "page.png", FileName = "page.png", RelativeOutputPath = "page.png" };
        for (var i = 0; i < 101; i++)
            item.RecordCloneStamp(new ManualPixelChange([i], [0, 0, 0], [1, 2, 3]));
        Assert.Equal(101, item.ActiveCloneStamps.Count);
        for (var i = 0; i < 100; i++)
            item.RemoveCloneStamp(Assert.IsType<ManualPixelChange>(item.TakeUndoManualEdit()));
        Assert.Null(item.TakeUndoManualEdit());
        Assert.Single(item.ActiveCloneStamps);
    }

    private static string CreatePage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerRestore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var pixels = new byte[80 * 60 * 3];
        for (var y = 0; y < 60; y++)
        for (var x = 0; x < 80; x++)
        {
            var offset = (y * 80 + x) * 3;
            pixels[offset] = (byte)(150 + x);
            pixels[offset + 1] = (byte)(150 + y);
            pixels[offset + 2] = 235;
        }
        ImageFileCodec.Save(new ImageBuffer(80, 60, pixels, 300, 300, ".png"),
            Path.Combine(root, "page.png"), ImageOutputFormat.Png, 95, true);
        return root;
    }

    private static async Task<MainViewModel> LoadPageAsync(string root)
    {
        var vm = new MainViewModel();
        vm.LoadDirectory(root, replaceExisting: true);
        await vm.RefreshSelectedAsync();
        return vm;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 3];
        image.CopyPixels(pixels, image.PixelWidth * 3, 0);
        return pixels;
    }
}
