using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class CardMatcherTests
{
    [Fact]
    public void ThumbnailMatcher_RecognizesConfiguredCardInOtherZonesForInspection()
    {
        using var image = SyntheticImages.FeaturedCard();
        var store = InMemoryFeatureStore.WithCard("CARD_A", image);
        using var matcher = new CardThumbnailMatcher(store, minimumConfidence: 0, minimumMargin: 0);

        var result = matcher.Match(image, CardZone.Board);

        Assert.Equal("CARD_A", result.CardId);
        Assert.Equal(CardKind.Minion, result.Kind);
    }

    [Fact]
    public void Constructor_RejectsThresholdBelowSafetyFloor()
    {
        using var image = SyntheticImages.FeaturedCard();
        var store = InMemoryFeatureStore.WithCard("CARD_A", image);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CardMatcher(store, minimumConfidence: 0.91));
    }
    [Fact]
    public void SqliteStore_RoundTripsOrbKeypointsAndDescriptors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.db");
        try
        {
            new CardCatalog(path).Initialize();
            using var image = SyntheticImages.FeaturedCard();
            var feature = CardFeatureFactory.Create("CARD_A", image, isGolden: true);
            new CardFeatureStore(path).Upsert(feature);

            var restored = Assert.Single(new CardFeatureStore(path).GetAll());

            Assert.Equal(feature.PerceptualHash, restored.PerceptualHash);
            Assert.Equal(feature.Descriptor, restored.Descriptor);
            Assert.Equal(feature.Keypoints, restored.Keypoints);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    [Fact]
    public void Match_ReturnsUnknown_WhenBestCandidateIsBelowThreshold()
    {
        using var query = SyntheticImages.Noise(160, 220, seed: 42);
        using var template = SyntheticImages.SolidCard();
        var store = InMemoryFeatureStore.WithCard("CARD_A", template);
        var matcher = new CardMatcher(store, minimumConfidence: 0.92);

        var result = matcher.Match(query);

        Assert.False(result.IsKnown);
        Assert.Null(result.CardId);
        Assert.False(result.IsGolden);
        Assert.InRange(result.Confidence, 0, 0.92);
    }

    [Fact]
    public void Match_MapsGoldenTemplateToTheSameCardId()
    {
        using var template = SyntheticImages.FeaturedCard();
        using var query = template.Clone();
        var store = InMemoryFeatureStore.WithCard("CARD_A", template, isGolden: true);
        var matcher = new CardMatcher(store, minimumConfidence: 0.92);

        var result = matcher.Match(query);

        Assert.True(result.IsKnown);
        Assert.Equal("CARD_A", result.CardId);
        Assert.True(result.IsGolden);
        Assert.InRange(result.Confidence, 0.92, 1);
    }
}

internal static class SyntheticImages
{
    public static Mat Noise(int width, int height, int seed)
    {
        var image = new Mat(height, width, MatType.CV_8UC1);
        var random = new Random(seed);
        image.SetArray(Enumerable.Range(0, width * height).Select(_ => (byte)random.Next(256)).ToArray());
        return image;
    }

    public static Mat SolidCard() => new(220, 160, MatType.CV_8UC1, Scalar.All(32));

    public static Mat FeaturedCard()
    {
        var image = new Mat(220, 160, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(image, new Rect(15, 15, 130, 190), Scalar.All(210), 3);
        Cv2.Circle(image, new Point(80, 110), 42, Scalar.All(180), 2);
        Cv2.PutText(image, "A", new Point(55, 130), HersheyFonts.HersheySimplex, 2, Scalar.All(255), 3);
        return image;
    }
}
