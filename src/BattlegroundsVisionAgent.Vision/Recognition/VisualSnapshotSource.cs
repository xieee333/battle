using System.Diagnostics;
using System.Runtime.InteropServices;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Runtime;
using BattlegroundsVisionAgent.Vision.Capture;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record StableSnapshotOptions
{
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public void Validate()
    {
        if (MaxWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxWait));
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
    }
}

public sealed class VisualSnapshotSource : IAutomationSnapshotSource, IDisposable
{
    private readonly IFrameSource _frameSource;
    private readonly SnapshotRecognizer _recognizer;
    private readonly StableSnapshotOptions _options;
    private bool _disposed;

    public VisualSnapshotSource(
        IFrameSource frameSource,
        SnapshotRecognizer recognizer,
        StableSnapshotOptions? options = null)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _options = options ?? new StableSnapshotOptions();
        _options.Validate();
    }

    public async Task<GameSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var detector = new FrameStabilityDetector();
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < _options.MaxWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = await _frameSource.CaptureAsync(cancellationToken).ConfigureAwait(false);
            var gray = CreateGrayFrame(frame.Image);
            var stability = detector.Observe(gray, stopwatch.Elapsed);
            if (stability.IsStable)
                return _recognizer.Recognize(frame.Image, frame.CapturedAt).Snapshot;

            var remaining = _options.MaxWait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < _options.PollInterval ? remaining : _options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return UnknownSnapshot(DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frameSource.Dispose();
    }

    private static GrayFrame CreateGrayFrame(Mat image)
    {
        using var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        using var contiguous = gray.Clone();
        var pixels = new byte[checked(contiguous.Width * contiguous.Height)];
        if (pixels.Length > 0)
            Marshal.Copy(contiguous.Data, pixels, 0, pixels.Length);
        return new GrayFrame(contiguous.Width, contiguous.Height, pixels);
    }

    private static GameSnapshot UnknownSnapshot(DateTimeOffset capturedAt) => new(
        layoutVersion: 0,
        confidence: 0,
        capturedAt,
        gamePhase: GamePhase.Unknown,
        gold: null,
        tavernTier: null,
        shop: [],
        hand: [],
        board: [],
        discoverOptions: [],
        handCapacity: 0,
        boardCapacity: 0,
        hasPendingTripleReward: false,
        hasUnknownBlockingUi: true);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
