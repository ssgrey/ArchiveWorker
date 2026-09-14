using ArchiveCleaner.Core.Imaging;

namespace ArchiveCleaner.Core.Tests;

public sealed class ManualMaskEditorTests
{
    [Fact]
    public void CircularBrush_HasRoundCornersAndClipsAtImageBoundary()
    {
        var protection = new byte[21 * 21];
        var removal = new byte[21 * 21];
        var change = ManualMaskEditor.ApplyBrush(protection, removal, 21, 21,
            ManualMaskEditKind.Protect, [new PixelPoint(10, 10)], 10);

        Assert.True(change.PixelCount > 0);
        Assert.Equal(255, protection[10 * 21 + 10]);
        Assert.Equal(0, protection[5 * 21 + 5]);
        Assert.All(removal, value => Assert.Equal(0, value));

        var edgeProtection = new byte[21 * 21];
        var edgeRemoval = new byte[21 * 21];
        var edgeChange = ManualMaskEditor.ApplyBrush(edgeProtection, edgeRemoval, 21, 21,
            ManualMaskEditKind.Protect, [new PixelPoint(0, 0)], 10);
        Assert.InRange(edgeChange.PixelCount, 1, change.PixelCount - 1);
    }

    [Fact]
    public void BrushPath_IsContinuousAndUndoRedoRestoresExactStates()
    {
        const int width = 60;
        const int height = 30;
        var protection = new byte[width * height];
        var removal = new byte[width * height];
        var protectChange = ManualMaskEditor.ApplyBrush(protection, removal, width, height,
            ManualMaskEditKind.Protect, [new PixelPoint(5, 15), new PixelPoint(55, 15)], 6);
        for (var x = 5; x <= 55; x++) Assert.Equal(255, protection[15 * width + x]);

        var removeChange = ManualMaskEditor.ApplyBrush(protection, removal, width, height,
            ManualMaskEditKind.Remove, [new PixelPoint(20, 15), new PixelPoint(40, 15)], 6);
        Assert.Equal(0, removeChange.PixelCount);
        Assert.Equal(255, protection[15 * width + 30]);
        Assert.Equal(0, removal[15 * width + 30]);

        var eraseChange = ManualMaskEditor.ApplyBrush(protection, removal, width, height,
            ManualMaskEditKind.Erase, [new PixelPoint(20, 15), new PixelPoint(40, 15)], 6);
        Assert.Equal(0, protection[15 * width + 30]);
        ManualMaskEditor.Undo(protection, removal, width, height, eraseChange);
        Assert.Equal(255, protection[15 * width + 30]);
        ManualMaskEditor.Redo(protection, removal, width, height, eraseChange);
        Assert.Equal(0, protection[15 * width + 30]);

        var appliedRemove = ManualMaskEditor.ApplyBrush(protection, removal, width, height,
            ManualMaskEditKind.Remove, [new PixelPoint(20, 15), new PixelPoint(40, 15)], 6);
        Assert.Equal(255, removal[15 * width + 30]);

        ManualMaskEditor.Undo(protection, removal, width, height, appliedRemove);
        ManualMaskEditor.Undo(protection, removal, width, height, eraseChange);
        ManualMaskEditor.Undo(protection, removal, width, height, protectChange);
        Assert.All(protection, value => Assert.Equal(0, value));
        Assert.All(removal, value => Assert.Equal(0, value));
    }

    [Fact]
    public void RestoreBrush_ClearsManualRemovalAndProtectsUnderlyingAutomaticRemoval()
    {
        const int width = 50;
        const int height = 20;
        var protection = new byte[width * height];
        var removal = new byte[width * height];
        var underlying = new byte[width * height];
        protection[10 * width + 5] = 255;
        removal[10 * width + 15] = 255;
        underlying[10 * width + 25] = 255;
        var beforeProtection = (byte[])protection.Clone();
        var beforeRemoval = (byte[])removal.Clone();

        var change = ManualMaskEditor.ApplyRestoreBrush(protection, removal, width, height,
            [new PixelPoint(5, 10), new PixelPoint(30, 10)], 4,
            new ArchiveCleaner.Core.Contracts.MaskBuffer(width, height, underlying));

        Assert.Equal(0, protection[10 * width + 5]);
        Assert.Equal(0, removal[10 * width + 15]);
        Assert.Equal(255, protection[10 * width + 25]);
        Assert.Equal(0, removal[10 * width + 25]);

        ManualMaskEditor.Undo(protection, removal, width, height, change);
        Assert.Equal(beforeProtection, protection);
        Assert.Equal(beforeRemoval, removal);
        ManualMaskEditor.Redo(protection, removal, width, height, change);
        Assert.Equal(0, protection[10 * width + 5]);
        Assert.Equal(0, removal[10 * width + 15]);
        Assert.Equal(255, protection[10 * width + 25]);
    }
}
