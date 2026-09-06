using BattlegroundsVisionAgent.Vision.Geometry;

namespace BattlegroundsVisionAgent.Vision.Tests;

public class RegionCalibrationTests
{
    [Fact]
    public void ReverseDragUsesImageCoordinates()
    {
        var rect = CalibrationRect.FromDrag(1500, 800, 500, 200, 2000, 1000);
        Assert.Equal(new CalibrationRect(.25, .2, .5, .6), rect);
        rect.Validate();
    }

    [Fact]
    public void DragClampsToImageEdges()
    {
        Assert.Equal(new CalibrationRect(0, 0, 1, 1), CalibrationRect.FromDrag(-100, -100, 2500, 1300, 2000, 1000));
    }

    [Theory]
    [InlineData(0, 0, 2, 2)]
    [InlineData(10, 10, 10, 200)]
    [InlineData(double.NaN, 0, 100, 100)]
    public void RejectsEmptyTinyOrInvalidDrag(double x1, double y1, double x2, double y2)
    {
        Assert.Throws<InvalidDataException>(() => CalibrationRect.FromDrag(x1, y1, x2, y2, 1920, 1080));
    }

    [Fact]
    public void DraftRoundTripsAndRejectsWrongResolution()
    {
        var draft = new RegionCalibration(1, 1920, 1080, new() { ["shop"] = new(.1, .2, .6, .3) });
        var loaded = RegionCalibration.Parse(draft.ToJson(), 1920, 1080);
        Assert.Equal(draft.Regions["shop"], loaded.Regions["shop"]);
        Assert.Throws<InvalidDataException>(() => RegionCalibration.Parse(draft.ToJson(), 1280, 720));
    }

    [Fact]
    public void RejectsUnknownRegionsAndOutOfBounds()
    {
        Assert.Throws<InvalidDataException>(() => new RegionCalibration(1, 1920, 1080, new() { ["unknown"] = new(0, 0, 1, 1) }).Validate());
        Assert.Throws<InvalidDataException>(() => new CalibrationRect(.8, 0, .5, .1).Validate());
        Assert.Throws<InvalidDataException>(() => new RegionCalibration(2, 1920, 1080, new()).Validate());
    }
}
