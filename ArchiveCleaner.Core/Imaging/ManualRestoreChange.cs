namespace ArchiveCleaner.Core.Imaging;

// A restore stroke changes masks and clone stamps together in one undo/redo step.
public sealed class ManualRestoreChange : IManualEditChange
{
    public ManualRestoreChange(ManualMaskChange maskChange,
        IReadOnlyList<ManualPixelChange> beforeStamps, IReadOnlyList<ManualPixelChange> afterStamps)
    {
        MaskChange = maskChange;
        BeforeStamps = beforeStamps.ToArray();
        AfterStamps = afterStamps.ToArray();
        PixelCount = maskChange.PixelCount + BeforeStamps.Sum(stamp => stamp.PixelCount)
            - AfterStamps.Sum(stamp => stamp.PixelCount);
    }

    public ManualMaskChange MaskChange { get; }
    public IReadOnlyList<ManualPixelChange> BeforeStamps { get; }
    public IReadOnlyList<ManualPixelChange> AfterStamps { get; }
    public int PixelCount { get; }
}
