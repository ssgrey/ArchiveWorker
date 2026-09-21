using ArchiveCleaner.Core.Contracts;

namespace ArchiveCleaner.Core.Imaging;

public static class ImageRotation
{
    public static int Normalize(int quarterTurns) => (quarterTurns % 4 + 4) % 4;

    // Normalized edge coordinates also work for canvas positions and boundary guides.
    public static (double X, double Y) Map(double x, double y, int quarterTurns) => Normalize(quarterTurns) switch
    {
        1 => (1 - y, x),
        2 => (1 - x, 1 - y),
        3 => (y, 1 - x),
        _ => (x, y)
    };

    public static EdgeSide MapSide(EdgeSide side, int quarterTurns)
    {
        for (var i = 0; i < Normalize(quarterTurns); i++)
            side = side switch
            {
                EdgeSide.Left => EdgeSide.Top,
                EdgeSide.Top => EdgeSide.Right,
                EdgeSide.Right => EdgeSide.Bottom,
                _ => EdgeSide.Left
            };
        return side;
    }

    public static ImageBuffer Apply(ImageBuffer source, int quarterTurns)
    {
        var turns = Normalize(quarterTurns);
        if (turns == 0) return source;
        var swap = turns % 2 != 0;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var pixels = new byte[source.Pixels.Length];
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var (dx, dy) = turns switch
            {
                1 => (source.Height - 1 - y, x),
                2 => (source.Width - 1 - x, source.Height - 1 - y),
                _ => (y, source.Width - 1 - x)
            };
            Buffer.BlockCopy(source.Pixels, (y * source.Width + x) * 3, pixels, (dy * width + dx) * 3, 3);
        }
        return new ImageBuffer(width, height, pixels, swap ? source.DpiY : source.DpiX,
            swap ? source.DpiX : source.DpiY, source.Extension);
    }
}
