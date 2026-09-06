using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Geometry;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;

    public long Bottom => (long)Y + Height;

    internal bool IsWithin(int width, int height) =>
        X >= 0 && Y >= 0 && Width >= 0 && Height >= 0 && Right <= width && Bottom <= height;

    internal bool Overlaps(PixelRect other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public static class NormalizedRectExtensions
{
    public static PixelRect ToPixels(this NormalizedRect rect, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        if (frameHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameHeight));

        var pixelRect = new PixelRect(
            Scale(rect.X, frameWidth),
            Scale(rect.Y, frameHeight),
            Scale(rect.Width, frameWidth),
            Scale(rect.Height, frameHeight));

        if (!pixelRect.IsWithin(frameWidth, frameHeight))
            throw new OverflowException("Rounded normalized bounds cannot be represented within the frame.");

        return pixelRect;
    }

    private static int Scale(double value, int dimension) =>
        checked((int)Math.Round(value * dimension, MidpointRounding.AwayFromZero));
}
