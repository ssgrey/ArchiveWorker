using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Detection;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class ManualEditHistoryTests
{
    [Fact]
    public async Task ViewModel_ManualStrokeUndoAndRedoRestoreExactMask()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerHistory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "page.png");
            var pixels = Enumerable.Repeat((byte)235, 120 * 100 * 3).ToArray();
            ImageFileCodec.Save(new ImageBuffer(120, 100, pixels, 300, 300, ".png"), path, ImageOutputFormat.Png, 95, true);
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            await viewModel.RefreshSelectedAsync();

            await viewModel.ApplyManualEditsAsync(ManualEditMode.ProtectBrush,
                [new PixelPoint(35, 50), new PixelPoint(85, 50)], brushDiameter: 20);
            var applied = (byte[])viewModel.SelectedImage!.ManualProtectionPixels!.Clone();
            Assert.True(applied.Count(value => value != 0) > 0);
            Assert.True(viewModel.CanUndoManualEdit);
            Assert.False(viewModel.CanRedoManualEdit);

            await viewModel.UndoManualEditAsync();
            Assert.All(viewModel.SelectedImage.ManualProtectionPixels!, value => Assert.Equal(0, value));
            Assert.True(viewModel.CanRedoManualEdit);

            await viewModel.RedoManualEditAsync();
            Assert.Equal(applied, viewModel.SelectedImage.ManualProtectionPixels);
            Assert.True(viewModel.CanUndoManualEdit);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ToolColors_RecolorManualMaskAndEraserDefaultsToGray()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerColors_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "page.png");
            var pixels = Enumerable.Repeat((byte)235, 80 * 60 * 3).ToArray();
            ImageFileCodec.Save(new ImageBuffer(80, 60, pixels, 300, 300, ".png"), path, ImageOutputFormat.Png, 95, true);
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            await viewModel.RefreshSelectedAsync();
            viewModel.ManualEditMode = ManualEditMode.ProtectBrush;
            viewModel.SetCurrentToolColor(Color.FromRgb(47, 111, 237));
            await viewModel.ApplyManualEditsAsync(ManualEditMode.ProtectBrush, [new PixelPoint(40, 30)], 12);

            var mask = Assert.IsAssignableFrom<BitmapSource>(viewModel.SelectedMaskPreview);
            var buffer = new byte[mask.PixelWidth * mask.PixelHeight * 3];
            mask.CopyPixels(buffer, mask.PixelWidth * 3, 0);
            var offset = (30 * 80 + 40) * 3;
            Assert.Equal(237, buffer[offset]);
            Assert.Equal(111, buffer[offset + 1]);
            Assert.Equal(47, buffer[offset + 2]);

            var eraser = Assert.IsType<SolidColorBrush>(viewModel.EraserToolBrush);
            Assert.NotEqual(Colors.White, eraser.Color);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MaskPreview_LeavesNonCandidatePageCenterDark()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);
        await viewModel.RefreshSelectedAsync();

        var item = Assert.IsType<ArchiveImageItem>(viewModel.SelectedImage);
        var centerX = item.PixelWidth / 2;
        var centerY = item.PixelHeight / 2;
        var centerIndex = centerY * item.PixelWidth + centerX;
        Assert.Equal(0, item.Analysis!.ProtectionMask.Pixels[centerIndex]);
        Assert.Equal(0, item.Analysis.AutoRemoveMask.Pixels[centerIndex]);
        Assert.Equal(0, item.Analysis.ReviewMask.Pixels[centerIndex]);

        var mask = Assert.IsAssignableFrom<BitmapSource>(viewModel.SelectedMaskPreview);
        var pixel = new byte[3];
        mask.CopyPixels(new System.Windows.Int32Rect(centerX, centerY, 1, 1), pixel, 3, 0);
        Assert.Equal(new byte[] { 40, 37, 31 }, pixel);
    }

    [Fact]
    public async Task RestoreEraser_RestoresAutomaticAndManualRemovalWithUndoRedo()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);
        await viewModel.RefreshSelectedAsync();
        var item = Assert.IsType<ArchiveImageItem>(viewModel.SelectedImage);
        var automaticIndex = Array.FindIndex(item.Analysis!.AutoRemoveMask.Pixels, value => value != 0);
        Assert.True(automaticIndex >= 0);
        var automaticPoint = new PixelPoint(automaticIndex % item.PixelWidth, automaticIndex / item.PixelWidth);

        await viewModel.ApplyManualEditsAsync(ManualEditMode.Eraser, [automaticPoint], 4);
        Assert.Equal(255, item.ManualProtectionPixels![automaticIndex]);
        var restoredMask = DocumentCleanupEngine.CreateRepairMask(item.Analysis,
            new ReviewDecisionSet(item.Decisions, item.CreateManualProtectionMask(), item.CreateManualRemovalMask()));
        Assert.Equal(0, restoredMask.Pixels[automaticIndex]);

        await viewModel.UndoManualEditAsync();
        Assert.Equal(0, item.ManualProtectionPixels[automaticIndex]);
        await viewModel.RedoManualEditAsync();
        Assert.Equal(255, item.ManualProtectionPixels[automaticIndex]);

        var center = new PixelPoint(item.PixelWidth / 2, item.PixelHeight / 2);
        var centerIndex = center.Y * item.PixelWidth + center.X;
        Assert.Equal(0, item.Analysis.AutoRemoveMask.Pixels[centerIndex]);
        await viewModel.ApplyManualEditsAsync(ManualEditMode.RemoveBrush, [center], 4);
        Assert.Equal(255, item.ManualRemovalPixels![centerIndex]);
        await viewModel.ApplyManualEditsAsync(ManualEditMode.Eraser, [center], 4);
        Assert.Equal(0, item.ManualRemovalPixels[centerIndex]);
        Assert.Equal(0, item.ManualProtectionPixels[centerIndex]);
        await viewModel.UndoManualEditAsync();
        Assert.Equal(255, item.ManualRemovalPixels[centerIndex]);
    }

    [Fact]
    public async Task RestoreOriginal_SwitchesImageToManualOnlyProcessing()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);
        await viewModel.RefreshSelectedAsync();
        var item = Assert.IsType<ArchiveImageItem>(viewModel.SelectedImage);
        var automaticIndex = Array.FindIndex(item.Analysis!.AutoRemoveMask.Pixels, value => value != 0);
        Assert.True(automaticIndex >= 0);

        await viewModel.RestoreSelectedToOriginalAsync();

        Assert.True(item.ManualOnly);
        Assert.Null(item.ManualProtectionPixels);
        Assert.Null(item.ManualRemovalPixels);
        Assert.Equal(0, DocumentCleanupEngine.CreateRepairMask(item.Analysis,
            new ReviewDecisionSet(item.Decisions, disableAutomaticRepair: item.ManualOnly)).Pixels[automaticIndex]);

        var center = new PixelPoint(item.PixelWidth / 2, item.PixelHeight / 2);
        await viewModel.ApplyManualEditsAsync(ManualEditMode.RemoveBrush, [center], 4);
        var centerIndex = center.Y * item.PixelWidth + center.X;
        Assert.Equal(255, DocumentCleanupEngine.CreateRepairMask(item.Analysis,
            new ReviewDecisionSet(item.Decisions, item.CreateManualProtectionMask(), item.CreateManualRemovalMask(), item.ManualOnly)).Pixels[centerIndex]);
    }

    [Fact]
    public async Task ReanalyzeAfterRestore_LeavesManualOnlyMode()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);
        await viewModel.RefreshSelectedAsync();
        await viewModel.RestoreSelectedToOriginalAsync();

        await viewModel.RefreshSelectedAsync();

        Assert.False(viewModel.SelectedImage!.ManualOnly);
        Assert.NotEqual("仅手工处理", viewModel.SelectedImage.StatusText);
    }

    [Fact]
    public async Task RefreshSelected_AutomaticallyApprovesReviewRegions()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);

        await viewModel.RefreshSelectedAsync();

        var item = Assert.IsType<ArchiveImageItem>(viewModel.SelectedImage);
        Assert.True(item.ReviewRegions.All(region => item.Decisions.TryGetValue(region.Id, out var decision) && decision == ReviewDecision.Remove));
    }

    [Fact]
    public async Task CloneStamp_IsIncludedInManualHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerClone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var pixels = new byte[80 * 60 * 3];
            for (var y = 0; y < 60; y++)
            for (var x = 0; x < 80; x++)
            {
                var offset = (y * 80 + x) * 3;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = 10;
            }
            var path = Path.Combine(root, "page.png");
            ImageFileCodec.Save(new ImageBuffer(80, 60, pixels, 300, 300, ".png"), path, ImageOutputFormat.Png, 95, true);
            var viewModel = new MainViewModel();
            viewModel.LoadDirectory(root, replaceExisting: true);
            await viewModel.RefreshSelectedAsync();

            await viewModel.ApplyManualEditsAsync(ManualEditMode.CloneStamp,
                [new PixelPoint(60, 40)], 9, new PixelPoint(20, 20));

            var item = viewModel.SelectedImage!;
            Assert.Single(item.ActiveCloneStamps);
            Assert.True(viewModel.CanUndoManualEdit);
            await viewModel.UndoManualEditAsync();
            Assert.Empty(item.ActiveCloneStamps);
            await viewModel.RedoManualEditAsync();
            Assert.Single(item.ActiveCloneStamps);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reanalyze_ClearsAllManualCorrections()
    {
        var sampleDirectory = Path.Combine(AppContext.BaseDirectory, "Files");
        var viewModel = new MainViewModel();
        viewModel.LoadDirectory(sampleDirectory, replaceExisting: true);
        await viewModel.RefreshSelectedAsync();
        var item = viewModel.SelectedImage!;
        await viewModel.ApplyManualEditsAsync(ManualEditMode.ProtectBrush, [new PixelPoint(20, 20)], 12);
        await viewModel.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [new PixelPoint(40, 40)], 12, new PixelPoint(20, 20));

        await viewModel.RefreshSelectedAsync();

        Assert.Null(item.ManualProtectionPixels);
        Assert.Null(item.ManualRemovalPixels);
        Assert.Empty(item.ActiveCloneStamps);
        Assert.False(item.CanUndoManualEdit);
    }
}
