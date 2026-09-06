using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class NormalizedRectTests
{
    [Theory]
    [InlineData(1280, 720, 128, 72, 256, 144)]
    [InlineData(1600, 900, 160, 90, 320, 180)]
    [InlineData(1920, 1080, 192, 108, 384, 216)]
    [InlineData(2560, 1440, 256, 144, 512, 288)]
    public void ToPixels_ScalesAcrossSupported16By9(
        int width, int height, int x, int y, int rectangleWidth, int rectangleHeight)
    {
        var rect = new NormalizedRect(0.10, 0.10, 0.20, 0.20);

        Assert.Equal(new PixelRect(x, y, rectangleWidth, rectangleHeight), rect.ToPixels(width, height));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void ToPixels_RejectsNonPositiveFrameDimensions(int width, int height)
    {
        var rect = new NormalizedRect(0.10, 0.10, 0.20, 0.20);

        Assert.Throws<ArgumentOutOfRangeException>(() => rect.ToPixels(width, height));
    }

    [Theory]
    [InlineData(-0.01, 0.10, 0.20, 0.20)]
    [InlineData(0.90, 0.10, 0.20, 0.20)]
    [InlineData(0.10, 0.90, 0.20, 0.20)]
    public void Constructor_RejectsRectanglesOutsideNormalizedFrame(double x, double y, double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(x, y, width, height));
    }

    [Fact]
    public void ToPixels_RejectsRoundingThatWouldPlaceRectangleBeyondAnExtremeFrame()
    {
        var rect = new NormalizedRect(0.50, 0.00, 0.50, 1.00);

        Assert.Throws<OverflowException>(() => rect.ToPixels(int.MaxValue, 1));
    }

    [Fact]
    public void PixelRect_UsesNonOverflowingEdgesForExtremeCoordinates()
    {
        var rect = new PixelRect(int.MaxValue, int.MaxValue, 1, 1);

        Assert.Equal((long)int.MaxValue + 1, rect.Right);
        Assert.Equal((long)int.MaxValue + 1, rect.Bottom);
    }
}
