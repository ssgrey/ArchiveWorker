using ArchiveCleaner.Core.Contracts;

namespace ArchiveCleaner.Core.Imaging;

public enum ManualMaskEditKind { Protect, Remove, Erase }

public interface IManualEditChange
{
    int PixelCount { get; }
}

public readonly record struct PixelPoint(int X, int Y);

public sealed class ManualMaskChange : IManualEditChange
{
    internal ManualMaskChange(int[] indices, byte[] beforeStates, byte afterState)
        : this(indices, beforeStates, Enumerable.Repeat(afterState, indices.Length).ToArray()) { }

    internal ManualMaskChange(int[] indices, byte[] beforeStates, byte[] afterStates)
    {
        if (indices.Length != beforeStates.Length || indices.Length != afterStates.Length)
            throw new ArgumentException("人工掩膜变更记录长度不一致。");
        Indices = indices;
        BeforeStates = beforeStates;
        AfterStates = afterStates;
    }

    public int[] Indices { get; }
    public byte[] BeforeStates { get; }
    public byte[] AfterStates { get; }
    public int PixelCount => Indices.Length;
}

public static class ManualMaskEditor
{
    private const byte EmptyState = 0;
    private const byte ProtectedState = 1;
    private const byte RemovalState = 2;

    public static ManualMaskChange ApplyBrush(byte[] protection, byte[] removal, int width, int height,
        ManualMaskEditKind kind, IReadOnlyList<PixelPoint> points, int diameter)
    {
        ValidateMasks(protection, removal, width, height);
        if (points.Count == 0) return new ManualMaskChange([], [], StateFor(kind));
        if (diameter is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(diameter));

        var indices = CollectBrushIndices(points, diameter, width, height);
        return ApplyIndices(protection, removal, indices, StateFor(kind));
    }

    public static ManualMaskChange ApplyRestoreBrush(byte[] protection, byte[] removal, int width, int height,
        IReadOnlyList<PixelPoint> points, int diameter, MaskBuffer underlyingRemovalMask)
    {
        ValidateMasks(protection, removal, width, height);
        if (underlyingRemovalMask.Width != width || underlyingRemovalMask.Height != height)
            throw new ArgumentException("底层去除掩膜尺寸与图片不一致。", nameof(underlyingRemovalMask));
        if (points.Count == 0) return new ManualMaskChange([], [], []);
        if (diameter is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(diameter));
        var indices = CollectBrushIndices(points, diameter, width, height);
        return ApplyIndices(protection, removal, indices,
            index => underlyingRemovalMask.Pixels[index] != 0 ? ProtectedState : EmptyState);
    }

    public static ManualMaskChange ApplyRectangle(byte[] protection, byte[] removal, int width, int height,
        ManualMaskEditKind kind, PixelRect rectangle)
    {
        ValidateMasks(protection, removal, width, height);
        var left = Math.Clamp(rectangle.X, 0, width);
        var top = Math.Clamp(rectangle.Y, 0, height);
        var right = Math.Clamp(rectangle.Right, 0, width);
        var bottom = Math.Clamp(rectangle.Bottom, 0, height);
        var indices = new HashSet<int>();
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++) indices.Add(y * width + x);
        return ApplyIndices(protection, removal, indices, StateFor(kind));
    }

    public static void Undo(byte[] protection, byte[] removal, int width, int height, ManualMaskChange change)
    {
        ValidateMasks(protection, removal, width, height);
        for (var index = 0; index < change.Indices.Length; index++)
            SetState(protection, removal, change.Indices[index], change.BeforeStates[index]);
    }

    public static void Redo(byte[] protection, byte[] removal, int width, int height, ManualMaskChange change)
    {
        ValidateMasks(protection, removal, width, height);
        for (var index = 0; index < change.Indices.Length; index++)
            SetState(protection, removal, change.Indices[index], change.AfterStates[index]);
    }

    private static ManualMaskChange ApplyIndices(byte[] protection, byte[] removal, IEnumerable<int> candidateIndices, byte afterState)
        => ApplyIndices(protection, removal, candidateIndices, _ => afterState);

    private static ManualMaskChange ApplyIndices(byte[] protection, byte[] removal, IEnumerable<int> candidateIndices,
        Func<int, byte> getAfterState)
    {
        var changedIndices = new List<int>();
        var beforeStates = new List<byte>();
        var afterStates = new List<byte>();
        foreach (var index in candidateIndices.OrderBy(value => value))
        {
            var before = GetState(protection, removal, index);
            var afterState = getAfterState(index);
            // Allow manual edits to override previous states - user's latest action wins
            if (before == afterState) continue;
            changedIndices.Add(index);
            beforeStates.Add(before);
            afterStates.Add(afterState);
            SetState(protection, removal, index, afterState);
        }
        return new ManualMaskChange(changedIndices.ToArray(), beforeStates.ToArray(), afterStates.ToArray());
    }

    private static HashSet<int> CollectBrushIndices(IReadOnlyList<PixelPoint> points, int diameter, int width, int height)
    {
        var radius = diameter / 2d;
        var indices = new HashSet<int>();
        AddCircle(indices, points[0].X, points[0].Y, radius, width, height);
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var dx = (double)end.X - start.X;
            var dy = (double)end.Y - start.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, radius / 2d)));
            for (var step = 1; step <= steps; step++)
            {
                var ratio = step / (double)steps;
                AddCircle(indices, (int)Math.Round(start.X + dx * ratio), (int)Math.Round(start.Y + dy * ratio), radius, width, height);
            }
        }
        return indices;
    }

    private static void AddCircle(HashSet<int> indices, int centerX, int centerY, double radius, int width, int height)
    {
        var left = Math.Max(0, (int)Math.Floor(centerX - radius));
        var top = Math.Max(0, (int)Math.Floor(centerY - radius));
        var right = Math.Min(width - 1, (int)Math.Ceiling(centerX + radius));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(centerY + radius));
        var squaredRadius = radius * radius;
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            if (dx * dx + dy * dy <= squaredRadius) indices.Add(y * width + x);
        }
    }

    private static byte StateFor(ManualMaskEditKind kind) => kind switch
    {
        ManualMaskEditKind.Protect => ProtectedState,
        ManualMaskEditKind.Remove => RemovalState,
        _ => EmptyState
    };

    private static byte GetState(byte[] protection, byte[] removal, int index) =>
        (byte)((protection[index] != 0 ? ProtectedState : EmptyState) |
               (removal[index] != 0 ? RemovalState : EmptyState));

    private static void SetState(byte[] protection, byte[] removal, int index, byte state)
    {
        protection[index] = (state & ProtectedState) != 0 ? (byte)255 : (byte)0;
        removal[index] = (state & RemovalState) != 0 ? (byte)255 : (byte)0;
    }

    private static void ValidateMasks(byte[] protection, byte[] removal, int width, int height)
    {
        if (width <= 0 || height <= 0 || protection.Length != checked(width * height) || removal.Length != protection.Length)
            throw new ArgumentException("人工掩膜尺寸与图片不一致。");
    }
}
