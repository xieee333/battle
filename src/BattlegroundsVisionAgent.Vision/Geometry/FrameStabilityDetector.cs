namespace BattlegroundsVisionAgent.Vision.Geometry;

public sealed class GrayFrame
{
    private readonly byte[] _pixels;

    public GrayFrame(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (pixels.Length != checked(width * height))
            throw new ArgumentException("Pixel count must equal width multiplied by height.", nameof(pixels));

        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<byte> Pixels => _pixels;

    public static GrayFrame FromBytes(int width, int height, params byte[] pixels) => new(width, height, pixels);

    public static GrayFrame Solid(int width, int height, byte value)
    {
        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, value);
        return new GrayFrame(width, height, pixels);
    }
}

public readonly record struct FrameStabilityResult(bool IsStable, double AverageAbsoluteDifference);

public sealed class FrameStabilityDetector
{
    public const double DifferenceThreshold = 2.0;
    public static readonly TimeSpan RequiredStableDuration = TimeSpan.FromMilliseconds(120);

    private GrayFrame? _previous;
    private TimeSpan _previousTimestamp;
    private TimeSpan? _similarSince;

    public FrameStabilityResult Observe(GrayFrame frame, TimeSpan timestamp)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_previous is not null && timestamp < _previousTimestamp)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamps cannot move backwards.");

        if (_previous is null || _previous.Width != frame.Width || _previous.Height != frame.Height)
        {
            _previous = frame;
            _previousTimestamp = timestamp;
            _similarSince = null;
            return new FrameStabilityResult(false, double.PositiveInfinity);
        }

        var difference = CalculateAverageAbsoluteDifference(_previous.Pixels, frame.Pixels);
        var previousTimestamp = _previousTimestamp;
        _previous = frame;
        _previousTimestamp = timestamp;

        if (difference >= DifferenceThreshold)
        {
            _similarSince = null;
            return new FrameStabilityResult(false, difference);
        }

        _similarSince ??= previousTimestamp;
        return new FrameStabilityResult(timestamp - _similarSince >= RequiredStableDuration, difference);
    }

    private static double CalculateAverageAbsoluteDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        long total = 0;
        for (var index = 0; index < left.Length; index++)
            total += Math.Abs(left[index] - right[index]);

        return (double)total / left.Length;
    }
}
