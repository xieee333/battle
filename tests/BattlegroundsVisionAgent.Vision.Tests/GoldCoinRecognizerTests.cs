using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class GoldCoinRecognizerTests
{
    [Fact]
    public void Recognize_CountsBrightCoinsWithoutNeedingValueTemplates()
    {
        using var image = new Mat(50, 200, MatType.CV_8UC3, Scalar.All(25));
        for (var index = 0; index < 8; index++)
            Cv2.Circle(image, new Point(index * 20 + 10, 25), 7, new Scalar(0, 180, 255), -1);

        var result = GoldCoinRecognizer.Recognize(image, new NormalizedRect(0, 0, 1, 1));

        Assert.True(result.IsVisible);
        Assert.Equal(8, result.Value);
        Assert.True(result.Confidence >= 0.82);
    }

    [Fact]
    public void Recognize_ReturnsUnknownWhenTheGoldBarIsAbsent()
    {
        using var image = new Mat(50, 200, MatType.CV_8UC3, Scalar.All(25));

        var result = GoldCoinRecognizer.Recognize(image, new NormalizedRect(0, 0, 1, 1));

        Assert.False(result.IsVisible);
        Assert.False(result.IsKnown);
    }
}
