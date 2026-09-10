using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Validation;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class RecognitionValidationSampleRepositoryTests
{
    [Fact]
    public void Save_StoresApprovedCropAndCatalogBaseline()
    {
        var root = CreateRoot();
        try
        {
            var catalog = Catalog("v1", [new CardCatalogEntry("CARD_A", "测试牌", 1, "cards/a.png")]);
            var feedback = Feedback("v1", "正确", "正确", "CARD_A");
            using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(40, 80, 120));
            Cv2.ImEncode(".png", image, out var png);

            var repository = new RecognitionValidationSampleRepository(root);
            var result = repository.Save(feedback, png, catalog);

            Assert.Equal(1, result.SavedSampleCount);
            Assert.Equal(0, result.SkippedSampleCount);
            var sample = Assert.Single(repository.LoadSamples());
            Assert.Equal("CARD_A", sample.CardId);
            Assert.True(File.Exists(Path.Combine(root, sample.ImagePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(1, result.Compatibility.UsableSampleCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateAgainstCatalog_ReportsAddedChangedAndRemovedCards()
    {
        var root = CreateRoot();
        try
        {
            var firstCatalog = Catalog("v1", [new CardCatalogEntry("CARD_A", "旧名称", 1, "cards/a.png")]);
            var repository = new RecognitionValidationSampleRepository(root);
            using var image = new Mat(80, 80, MatType.CV_8UC3, new Scalar(20, 20, 20));
            Cv2.ImEncode(".png", image, out var png);
            repository.Save(Feedback("v1", "正确", "正确", "CARD_A"), png, firstCatalog);

            var secondCatalog = Catalog("v2", [
                new CardCatalogEntry("CARD_A", "新名称", 2, "cards/a-new.png"),
                new CardCatalogEntry("CARD_B", "新增牌", 1, "cards/b.png")
            ]);
            var report = repository.ValidateAgainstCatalog(secondCatalog);

            Assert.Contains("CARD_B", report.AddedCardIds);
            Assert.Contains("CARD_A", report.ChangedCardIds);
            Assert.Contains("CARD_A", report.StaleSampleCardIds);
            Assert.Equal(0, report.UsableSampleCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FeatureStore_LoadsCompatibleShopSamples()
    {
        var root = CreateRoot();
        var databasePath = Path.Combine(root, "catalog.db");
        try
        {
            var catalog = Catalog("v1", [new CardCatalogEntry("CARD_A", "测试牌", 1, "cards/a.png")]);
            CreateCatalogDatabase(databasePath, catalog.Entries[0]);
            using var image = SyntheticFeaturedImage();
            Cv2.ImEncode(".png", image, out var png);
            var repository = new RecognitionValidationSampleRepository(root);
            repository.Save(Feedback("v1", "正确", "正确", "CARD_A"), png, catalog);

            var features = new ValidationSampleFeatureStore(root, databasePath).GetAll();

            Assert.Single(features);
            Assert.Equal("CARD_A", features[0].CardId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CardCatalogSnapshot Catalog(string version, IReadOnlyList<CardCatalogEntry> entries) =>
        new(new CatalogMetadata(version, DateTimeOffset.UnixEpoch), entries);

    private static RecognitionValidationFeedback Feedback(
        string catalogVersion,
        string recognitionStatus,
        string positionStatus,
        string predictedCardId) => new(
        1,
        DateTimeOffset.UnixEpoch,
        "test.png",
        GamePhase.Shopping,
        0.95,
        5,
        0.95,
        3,
        0.95,
        null,
        false,
        [new RecognitionValidationCardFeedback(
            "商店", 0, predictedCardId, "测试牌", CardKind.Minion, false, 0.95,
            new NormalizedRect(0.1, 0.1, 0.5, 0.5), recognitionStatus, positionStatus, string.Empty, string.Empty)],
        [],
        string.Empty,
        catalogVersion);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "battlegrounds-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Mat SyntheticFeaturedImage()
    {
        var image = new Mat(220, 160, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(image, new Rect(15, 15, 130, 190), Scalar.All(210), 3);
        Cv2.Circle(image, new Point(80, 110), 42, Scalar.All(180), 2);
        Cv2.PutText(image, "A", new Point(55, 130), HersheyFonts.HersheySimplex, 2, Scalar.All(255), 3);
        return image;
    }

    private static void CreateCatalogDatabase(string path, CardCatalogEntry entry)
    {
        new CardCatalog(path).Initialize();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO cards(card_id, name_zh_cn, tier, image_path) VALUES($id, $name, $tier, $image)";
        command.Parameters.AddWithValue("$id", entry.CardId);
        command.Parameters.AddWithValue("$name", entry.NameZhCn);
        command.Parameters.AddWithValue("$tier", entry.Tier);
        command.Parameters.AddWithValue("$image", entry.ImagePath);
        command.ExecuteNonQuery();
    }
}
