using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class ManualPixelEditorTests
{
    [Fact]
    public void CloneStamp_CopiesSampledPixelsAndCanUndoRedo()
    {
        const int width = 9;
        const int height = 9;
        var pixels = new byte[width * height * 3];
        for (var index = 0; index < pixels.Length; index += 3)
        {
            var pixel = index / 3;
            pixels[index] = (byte)(pixel % width);
            pixels[index + 1] = (byte)(pixel / width);
            pixels[index + 2] = 200;
        }
        var original = (byte[])pixels.Clone();

        var change = ManualPixelEditor.ApplyCloneStamp(pixels, width, height,
            new PixelPoint(2, 2), [new PixelPoint(6, 6)], 3);

        var targetOffset = (6 * width + 6) * 3;
        var sampleOffset = (2 * width + 2) * 3;
        Assert.Equal(original[sampleOffset], pixels[targetOffset]);
        Assert.Equal(original[sampleOffset + 1], pixels[targetOffset + 1]);
        Assert.Equal(original[targetOffset], change.BeforePixels[Array.IndexOf(change.Indices, 6 * width + 6) * 3]);

        ManualPixelEditor.ApplyChange(pixels, change, useAfter: false);
        Assert.Equal(original, pixels);
        ManualPixelEditor.ApplyChange(pixels, change, useAfter: true);
        Assert.Equal(original[sampleOffset], pixels[targetOffset]);
    }
}
