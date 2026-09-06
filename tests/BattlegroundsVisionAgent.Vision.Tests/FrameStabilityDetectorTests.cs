using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using System.Collections.Concurrent;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class FrameStabilityDetectorTests
{
    [Fact]
    public void Observe_ReturnsStableOnlyAfterTwoSimilarFramesPersistFor120Milliseconds()
    {
        var detector = new FrameStabilityDetector();
        var animated = GrayFrame.FromBytes(2, 2, 0, 0, 0, 0);
        var changed = GrayFrame.FromBytes(2, 2, 10, 10, 10, 10);
        var settled = GrayFrame.FromBytes(2, 2, 10, 10, 10, 10);

        Assert.False(detector.Observe(animated, TimeSpan.FromMilliseconds(0)).IsStable);
        Assert.False(detector.Observe(changed, TimeSpan.FromMilliseconds(60)).IsStable);
        Assert.False(detector.Observe(settled, TimeSpan.FromMilliseconds(119)).IsStable);
        Assert.True(detector.Observe(settled, TimeSpan.FromMilliseconds(180)).IsStable);
    }

    [Fact]
    public void Observe_ResetsTheContinuousWindowWhenAverageDifferenceReachesThreshold()
    {
        var detector = new FrameStabilityDetector();
        var first = GrayFrame.FromBytes(2, 2, 0, 0, 0, 0);
        var near = GrayFrame.FromBytes(2, 2, 1, 1, 1, 1);
        var threshold = GrayFrame.FromBytes(2, 2, 3, 3, 3, 3);

        detector.Observe(first, TimeSpan.Zero);
        detector.Observe(near, TimeSpan.FromMilliseconds(60));
        var result = detector.Observe(threshold, TimeSpan.FromMilliseconds(120));

        Assert.False(result.IsStable);
        Assert.Equal(2.0, result.AverageAbsoluteDifference);
    }

    [Fact]
    public void Locate_ReturnsFailureForNon16By9Frames()
    {
        var locator = new LayoutLocator();
        var frame = GrayFrame.Solid(1000, 700, 0);
        var anchors = ValidAnchors();

        var result = locator.Locate(frame, anchors, ValidRegions());

        Assert.False(result.IsSuccess);
        Assert.Equal(LayoutFailure.InvalidAspectRatio, result.Failure);
    }

    [Fact]
    public void Locate_ReturnsFailureWhenAnyRequiredAnchorIsMissingOrBelowThreshold()
    {
        var locator = new LayoutLocator(minimumAnchorConfidence: 0.80);
        var frame = GrayFrame.Solid(1920, 1080, 0);
        var anchors = new[] { new AnchorMatch("shop", 0.95), new AnchorMatch("hand", 0.79) };

        var result = locator.Locate(frame, anchors, ValidRegions());

        Assert.False(result.IsSuccess);
        Assert.Equal(LayoutFailure.InsufficientAnchors, result.Failure);
    }

    [Fact]
    public void Locate_ReturnsFailureForOverlappingOrOutOfBoundsRegions()
    {
        var locator = new LayoutLocator();
        var frame = GrayFrame.Solid(1920, 1080, 0);
        var overlapping = new LayoutRegions(
            new NormalizedRect(0.10, 0.10, 0.30, 0.20),
            new NormalizedRect(0.30, 0.20, 0.30, 0.20),
            new NormalizedRect(0.10, 0.50, 0.30, 0.20));

        var result = locator.Locate(frame, ValidAnchors(), overlapping);

        Assert.False(result.IsSuccess);
        Assert.Equal(LayoutFailure.InvalidRegions, result.Failure);
    }

    [Fact]
    public void Locate_AssignsMonotonicallyIncreasingVersionsToValidLayouts()
    {
        var locator = new LayoutLocator();
        var frame = GrayFrame.Solid(1920, 1080, 0);

        var first = locator.Locate(frame, ValidAnchors(), ValidRegions());
        var second = locator.Locate(frame, ValidAnchors(), ValidRegions());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(second.Layout!.Version > first.Layout!.Version);
    }

    [Fact]
    public void Locate_AssignsUniqueContinuousVersionsWhenCalledConcurrently()
    {
        const int attempts = 128;
        var locator = new LayoutLocator();
        var frame = GrayFrame.Solid(1920, 1080, 0);
        var versions = new ConcurrentBag<long>();

        Parallel.For(0, attempts, _ =>
        {
            var result = locator.Locate(frame, ValidAnchors(), ValidRegions());
            Assert.True(result.IsSuccess);
            versions.Add(result.Layout!.Version);
        });

        Assert.Equal(Enumerable.Range(1, attempts).Select(value => (long)value), versions.Order());
    }

    [Fact]
    public void Locate_RejectsLayoutsAfterVersionSpaceIsExhausted()
    {
        var locator = new LayoutLocator();
        typeof(LayoutLocator).GetField("_nextVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(locator, long.MaxValue - 1);
        var frame = GrayFrame.Solid(1920, 1080, 0);

        var last = locator.Locate(frame, ValidAnchors(), ValidRegions());
        var exhausted = locator.Locate(frame, ValidAnchors(), ValidRegions());
        var stillExhausted = locator.Locate(frame, ValidAnchors(), ValidRegions());

        Assert.True(last.IsSuccess);
        Assert.Equal(long.MaxValue, last.Layout!.Version);
        Assert.False(exhausted.IsSuccess);
        Assert.Equal("VersionExhausted", exhausted.Failure?.ToString());
        Assert.False(stillExhausted.IsSuccess);
        Assert.Equal("VersionExhausted", stillExhausted.Failure?.ToString());
    }

    [Fact]
    public void Observe_RejectsBackwardTimeWithoutChangingTheStableSequence()
    {
        var detector = new FrameStabilityDetector();
        var stable = GrayFrame.FromBytes(2, 2, 10, 10, 10, 10);
        var changed = GrayFrame.FromBytes(2, 2, 20, 20, 20, 20);

        detector.Observe(stable, TimeSpan.Zero);
        detector.Observe(stable, TimeSpan.FromMilliseconds(60));

        Assert.Throws<ArgumentOutOfRangeException>(() => detector.Observe(changed, TimeSpan.FromMilliseconds(30)));

        Assert.True(detector.Observe(stable, TimeSpan.FromMilliseconds(120)).IsStable);
    }

    [Fact]
    public void Observe_AllowsEqualTimestampsWithoutAdvancingTheStableDuration()
    {
        var detector = new FrameStabilityDetector();
        var stable = GrayFrame.FromBytes(2, 2, 10, 10, 10, 10);

        detector.Observe(stable, TimeSpan.Zero);
        detector.Observe(stable, TimeSpan.FromMilliseconds(60));
        Assert.False(detector.Observe(stable, TimeSpan.FromMilliseconds(60)).IsStable);
        Assert.False(detector.Observe(stable, TimeSpan.FromMilliseconds(119)).IsStable);
        Assert.True(detector.Observe(stable, TimeSpan.FromMilliseconds(120)).IsStable);
    }

    private static AnchorMatch[] ValidAnchors() =>
    [
        new("shop", 0.95),
        new("hand", 0.95),
        new("board", 0.95)
    ];

    private static LayoutRegions ValidRegions() => new(
        new NormalizedRect(0.10, 0.10, 0.20, 0.20),
        new NormalizedRect(0.40, 0.10, 0.20, 0.20),
        new NormalizedRect(0.10, 0.50, 0.20, 0.20));
}
