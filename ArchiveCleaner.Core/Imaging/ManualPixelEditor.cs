namespace ArchiveCleaner.Core.Imaging;

public sealed class ManualPixelChange : IManualEditChange
{
    public ManualPixelChange(int[] indices, byte[] beforePixels, byte[] afterPixels,
        PixelPoint? samplePoint = null, IReadOnlyList<PixelPoint>? targetPoints = null, int diameter = 0)
    {
        if (indices.Length * 3 != beforePixels.Length || beforePixels.Length != afterPixels.Length)
            throw new ArgumentException("仿制图章变更记录长度不一致。");
        Indices = indices;
        BeforePixels = beforePixels;
        AfterPixels = afterPixels;
        SamplePoint = samplePoint;
        TargetPoints = targetPoints ?? [];
        Diameter = diameter;
    }

    public int[] Indices { get; }
    public byte[] BeforePixels { get; }
    public byte[] AfterPixels { get; }
    public PixelPoint? SamplePoint { get; }
    public IReadOnlyList<PixelPoint> TargetPoints { get; }
    public int Diameter { get; }
    public int PixelCount => Indices.Length;
}

public static class ManualPixelEditor
{
    public static ManualPixelChange ApplyCloneStamp(byte[] pixels, int width, int height,
        PixelPoint samplePoint, IReadOnlyList<PixelPoint> targetPoints, int diameter)
    {
        ValidatePixels(pixels, width, height);
        if (targetPoints.Count == 0) return new ManualPixelChange([], [], []);
        if (diameter is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(diameter));

        var source = (byte[])pixels.Clone();
        var radius = diameter / 2d;
        var origin = targetPoints[0];
        var sourceIndices = new Dictionary<int, int>();
        foreach (var point in targetPoints)
        {
            var left = Math.Max(0, (int)Math.Floor(point.X - radius));
            var top = Math.Max(0, (int)Math.Floor(point.Y - radius));
            var right = Math.Min(width - 1, (int)Math.Ceiling(point.X + radius));
            var bottom = Math.Min(height - 1, (int)Math.Ceiling(point.Y + radius));
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var dx = x - point.X;
                var dy = y - point.Y;
                if (dx * dx + dy * dy > radius * radius) continue;
                var sourceX = samplePoint.X + (x - point.X) + (point.X - origin.X);
                var sourceY = samplePoint.Y + (y - point.Y) + (point.Y - origin.Y);
                if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) continue;
                var targetIndex = y * width + x;
                if (!sourceIndices.ContainsKey(targetIndex))
                    sourceIndices[targetIndex] = sourceY * width + sourceX;
            }
        }

        var ordered = sourceIndices.Keys.OrderBy(index => index).ToArray();
        var before = new byte[ordered.Length * 3];
        var after = new byte[before.Length];
        for (var position = 0; position < ordered.Length; position++)
        {
            var index = ordered[position];
            var sourceIndex = sourceIndices[index] * 3;
            var pixelOffset = position * 3;
            var targetOffset = index * 3;
            before[pixelOffset] = pixels[targetOffset];
            before[pixelOffset + 1] = pixels[targetOffset + 1];
            before[pixelOffset + 2] = pixels[targetOffset + 2];
            after[pixelOffset] = source[sourceIndex];
            after[pixelOffset + 1] = source[sourceIndex + 1];
            after[pixelOffset + 2] = source[sourceIndex + 2];
        }
        var change = new ManualPixelChange(ordered, before, after, samplePoint,
            targetPoints.ToArray(), diameter);
        ApplyChange(pixels, change, useAfter: true);
        return change;
    }

    public static void ApplyChange(byte[] pixels, ManualPixelChange change, bool useAfter)
    {
        for (var position = 0; position < change.Indices.Length; position++)
        {
            var source = useAfter ? change.AfterPixels : change.BeforePixels;
            var sourceOffset = position * 3;
            var targetOffset = change.Indices[position] * 3;
            pixels[targetOffset] = source[sourceOffset];
            pixels[targetOffset + 1] = source[sourceOffset + 1];
            pixels[targetOffset + 2] = source[sourceOffset + 2];
        }
    }

    private static void ValidatePixels(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 3))
            throw new ArgumentException("图像像素尺寸不一致。");
    }
}
