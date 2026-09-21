using ArchiveCleaner.Core.Batch;
using ArchiveCleaner.Core.Contracts;
using ArchiveCleaner.Core.Imaging;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class ImageRotationTests
{
    [Theory]
    [InlineData(0, 3, 2, new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(1, 2, 3, new byte[] { 4, 1, 5, 2, 6, 3 })]
    [InlineData(2, 3, 2, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(3, 2, 3, new byte[] { 3, 6, 2, 5, 1, 4 })]
    [InlineData(4, 3, 2, new byte[] { 1, 2, 3, 4, 5, 6 })]
    public void Rotation_PreservesPixelsAndPhysicalSize(int turns, int width, int height, byte[] expected)
    {
        var source = new ImageBuffer(3, 2, Enumerable.Range(1, 6).SelectMany(i => new[] { (byte)i, (byte)i, (byte)i }).ToArray(), 200, 300, ".png");
        var rotated = ImageRotation.Apply(source, turns);
        Assert.Equal(width, rotated.Width);
        Assert.Equal(height, rotated.Height);
        Assert.Equal(expected, rotated.Pixels.Where((_, i) => i % 3 == 0));
        Assert.Equal(turns % 2 == 0 ? 200 : 300, rotated.DpiX);
        Assert.Equal(turns % 2 == 0 ? 300 : 200, rotated.DpiY);
        Assert.Equal(source.Pixels, ImageRotation.Apply(rotated, -turns).Pixels);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, source.Pixels.Where((_, i) => i % 3 == 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CanvasCoordinates_MatchRotatedPixelCentersAndEdges(int turns)
    {
        const int width = 5, height = 3;
        var source = new ImageBuffer(width, height, Enumerable.Range(0, width * height)
            .SelectMany(i => new[] { (byte)i, (byte)i, (byte)i }).ToArray(), 300, 300, ".png");
        var rotated = ImageRotation.Apply(source, turns);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var display = ImageRotation.Map((x + 0.5) / width, (y + 0.5) / height, turns);
            var offset = ((int)(display.Y * rotated.Height) * rotated.Width + (int)(display.X * rotated.Width)) * 3;
            Assert.Equal(source.Pixels[(y * width + x) * 3], rotated.Pixels[offset]);
            var original = ImageRotation.Map(display.X, display.Y, -turns);
            Assert.Equal(x, (int)(original.X * width));
            Assert.Equal(y, (int)(original.Y * height));
        }
        Assert.Equal(new[] { EdgeSide.Left, EdgeSide.Top, EdgeSide.Right, EdgeSide.Bottom }[turns],
            ImageRotation.MapSide(EdgeSide.Left, turns));
    }

    [Fact]
    public async Task ViewModel_RotationRetainsEditsHistoryAndExportsTheDisplayedOrientation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ArchiveCleanerRotation_{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var path = Path.Combine(sourceDirectory, "page.png");
            var pixels = Enumerable.Range(0, 120 * 80).SelectMany(i => new[] { (byte)(i % 211), (byte)190, (byte)230 }).ToArray();
            ImageFileCodec.Save(new ImageBuffer(120, 80, pixels, 200, 300, ".png"), path, ImageOutputFormat.Png, 95, true);
            var originalHash = Hashing.ComputeSha256(path);
            var vm = new MainViewModel();
            vm.ClearImageQueue();
            Assert.False(vm.CanRotateSelected);
            vm.AddFiles([path]);
            vm.Settings.DetectRepeatedDefects = false;
            vm.Settings.OutputFormat = "PNG";
            vm.OutputDirectory = outputDirectory;
            await vm.RefreshSelectedAsync();
            await vm.RestoreSelectedToOriginalAsync();
            await vm.ApplyManualEditsAsync(ManualEditMode.ProtectBrush, [new PixelPoint(40, 30)], 10);
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [new PixelPoint(70, 45)], 10, new PixelPoint(20, 20));
            var item = vm.SelectedImage!;
            var unrotated = ReadPixels(item.RepairedPreview!);
            var mask = (byte[])item.ManualProtectionPixels!.Clone();
            var turnsSeenWhileBusy = -1;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MainViewModel.IsBusy) || !vm.IsBusy) return;
                Assert.False(vm.CanRotateSelected);
                Assert.False(vm.RotateSelectedClockwise());
                turnsSeenWhileBusy = item.RotationQuarterTurns;
            };

            for (var turns = 1; turns <= 4; turns++)
            {
                Assert.True(vm.RotateSelectedClockwise());
                var expected = ImageRotation.Apply(new ImageBuffer(120, 80, unrotated, item.DpiX, item.DpiY, ".png"), turns);
                Assert.Equal(expected.Pixels, ReadPixels(vm.SelectedRepairedPreview!));
                Assert.Equal(expected.Width, vm.SelectedRepairedPreview!.PixelWidth);
                Assert.Equal(expected.Height, vm.SelectedMaskPreview!.PixelHeight);
                Assert.Equal(mask, item.ManualProtectionPixels);
                Assert.Single(item.ActiveCloneStamps);
                Assert.True(vm.CanUndoManualEdit);
                vm.SelectedImage = null;
                vm.SelectedImage = item;
                Assert.Equal(turns % 4, item.RotationQuarterTurns);
                Assert.Equal(expected.Pixels, ReadPixels(vm.SelectedRepairedPreview!));

                var report = await vm.ProcessBatchAsync();
                Assert.Equal(1, report.SuccessCount);
                var exported = ImageFileCodec.Load(Assert.Single(report.Pages).OutputPath!);
                Assert.Equal(expected.Pixels, exported.Pixels);
                Assert.Equal(expected.Width, exported.Width);
                Assert.Equal(expected.Height, exported.Height);
                Assert.Equal(expected.DpiX, exported.DpiX, 1);
                Assert.Equal(expected.DpiY, exported.DpiY, 1);
                Assert.Equal(turns % 4, turnsSeenWhileBusy);
            }

            vm.RotateSelectedClockwise();
            await vm.UndoManualEditAsync();
            Assert.Empty(item.ActiveCloneStamps);
            await vm.RedoManualEditAsync();
            Assert.Equal(ImageRotation.Apply(new ImageBuffer(120, 80, unrotated, 200, 300, ".png"), 1).Pixels,
                ReadPixels(vm.SelectedRepairedPreview!));
            // A new edit made while rotated uses original pixel coordinates and rotates with the preview.
            await vm.ApplyManualEditsAsync(ManualEditMode.CloneStamp, [new PixelPoint(90, 55)], 6, new PixelPoint(10, 10));
            var afterEdit = ReadPixels(vm.SelectedRepairedPreview!);
            var finalReport = await vm.ProcessBatchAsync();
            Assert.Equal(afterEdit, ImageFileCodec.Load(Assert.Single(finalReport.Pages).OutputPath!).Pixels);
            Assert.Equal(originalHash, Hashing.ComputeSha256(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 3];
        converted.CopyPixels(pixels, converted.PixelWidth * 3, 0);
        return pixels;
    }
}
