using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Capture;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class VisualSnapshotSourceTests
{
    [Fact]
    public async Task CaptureStableAsync_ReturnsRecognizedSnapshotAfterFramesSettle()
    {
        using var frameSource = new ConstantFrameSource();
        using var source = new VisualSnapshotSource(
            frameSource,
            CreateRecognizer(),
            new StableSnapshotOptions
            {
                MaxWait = TimeSpan.FromMilliseconds(500),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });

        var snapshot = await source.CaptureStableAsync(CancellationToken.None);

        Assert.Equal(GamePhase.Shopping, snapshot.GamePhase);
        Assert.True(snapshot.IsActionable);
        Assert.True(frameSource.CaptureCount >= 3);
    }

    [Fact]
    public async Task CaptureStableAsync_ReturnsUnknownSnapshotWhenFramesNeverSettle()
    {
        using var frameSource = new ChangingFrameSource();
        using var source = new VisualSnapshotSource(
            frameSource,
            CreateRecognizer(),
            new StableSnapshotOptions
            {
                MaxWait = TimeSpan.FromMilliseconds(150),
                PollInterval = TimeSpan.FromMilliseconds(15)
            });

        var snapshot = await source.CaptureStableAsync(CancellationToken.None);

        Assert.Equal(GamePhase.Unknown, snapshot.GamePhase);
        Assert.True(snapshot.HasUnknownBlockingUi);
        Assert.False(snapshot.IsActionable);
    }

    private static SnapshotRecognizer CreateRecognizer() => new(
        new StubLayoutRecognizer(),
        new StubCardMatcher(),
        new StubDigitRecognizer(),
        new StubSceneRecognizer());

    private sealed class StubLayoutRecognizer : ILayoutRecognizer
    {
        public LayoutRecognition Recognize(Mat frame) => LayoutRecognition.Succeeded(
            1,
            0.99,
            [],
            new NormalizedRect(0, 0, 0.1, 0.1),
            new NormalizedRect(0.1, 0, 0.1, 0.1),
            10,
            7);
    }

    private sealed class StubCardMatcher : ICardMatcher
    {
        public CardMatch Match(Mat cardImage) => CardMatch.Unknown();
    }

    private sealed class StubDigitRecognizer : IDigitRecognizer
    {
        public DigitRecognition Recognize(Mat image, NormalizedRect bounds, string label) =>
            new(5, bounds, 0.99);
    }

    private sealed class StubSceneRecognizer : ISceneRecognizer
    {
        public SceneRecognition Recognize(Mat frame) => new(GamePhase.Shopping, 0.99);
    }

    private sealed class ConstantFrameSource : IFrameSource
    {
        private readonly Mat _template = new(90, 160, MatType.CV_8UC3, Scalar.All(10));

        public int CaptureCount { get; private set; }

        public IntPtr WindowHandle => IntPtr.Zero;

        public Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(new CapturedFrame(_template.Clone(), DateTimeOffset.UtcNow));
        }

        public void Dispose() => _template.Dispose();
    }

    private sealed class ChangingFrameSource : IFrameSource
    {
        private int _value;

        public IntPtr WindowHandle => IntPtr.Zero;

        public Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = (Interlocked.Increment(ref _value) * 30) % 255;
            return Task.FromResult(new CapturedFrame(
                new Mat(90, 160, MatType.CV_8UC3, Scalar.All(value)),
                DateTimeOffset.UtcNow));
        }

        public void Dispose() { }
    }
}
