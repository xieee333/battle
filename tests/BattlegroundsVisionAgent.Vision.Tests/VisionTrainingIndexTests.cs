using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Recognition;
using BattlegroundsVisionAgent.Vision.Validation;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class VisionTrainingIndexTests
{
    [Fact]
    public void Build_UsesOnlyGoodLabeledFullFramesAndTracksCatalogFingerprint()
    {
        var root = CreateTempDirectory("training-index");
        try
        {
            WriteFile(root, "logs/live-frames/shopping.png");
            WriteFile(root, "logs/live-frames/discover.png");
            WriteFile(root, "logs/shop-crops/crop.png");

            var metrics = Metrics();
            var manifest = new LogCurationManifest(
                1,
                "abc123",
                7,
                [
                    new LogCurationSample("logs/live-frames/shopping.png", "Shopping", "Shopping", FrameQuality.Good, true, true, null, metrics, "ok"),
                    new LogCurationSample("logs/live-frames/discover.png", "Discover", "Discover", FrameQuality.Review, true, false, "frame-quality-review", metrics, "low-texture"),
                    new LogCurationSample("logs/live-frames/unknown.png", "Unknown", "Unknown", FrameQuality.Good, true, false, "scene-not-actionable", metrics, "ok"),
                    new LogCurationSample("logs/shop-crops/crop.png", "Shopping", "Shopping", FrameQuality.Good, true, true, null, metrics, "ok"),
                    new LogCurationSample("logs/live-frames/not-included.png", "Combat", "Combat", FrameQuality.Good, false, false, "frame-quality-review", metrics, "manual-exclusion")
                ]);
            var catalog = new CardCatalogSnapshot(
                new CatalogMetadata("v-test", DateTimeOffset.UnixEpoch),
                [new CardCatalogEntry("CARD_A", "测试甲", 2, "cards/CARD_A.png")]);

            var index = VisionTrainingIndexBuilder.Build(manifest, root, catalog);

            var sample = Assert.Single(index.SceneSamples);
            Assert.Equal("logs/live-frames/shopping.png", sample.Path);
            Assert.Equal(GamePhase.Shopping, sample.GamePhase);
            Assert.Equal("filename", sample.LabelSource);
            Assert.Equal("abc123", index.SourceCommit);
            Assert.Equal(7, index.QualityPolicyVersion);
            Assert.Equal("v-test", index.CatalogVersion);
            Assert.Equal(1, index.CatalogCardCount);
            Assert.Equal(VisionTrainingIndex.ModelKind, index.FeatureModel);
            Assert.Equal(64, index.CatalogFingerprint.Length);
            Assert.Equal(0, index.ValidatedCardSampleCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Build_SelectsDeterministicRepresentativesAndCountsCompatibleSamples()
    {
        var root = CreateTempDirectory("training-index-selection");
        try
        {
            var metrics = Metrics();
            var samples = Enumerable.Range(0, 5)
                .Select(index =>
                {
                    var path = $"logs/live-frames/20260907-20000{index}-000-Shopping.png";
                    WriteFile(root, path);
                    return new LogCurationSample(path, "Shopping", "Shopping", FrameQuality.Good, true, true, null, metrics, "ok");
                })
                .ToList();
            var manifest = new LogCurationManifest(1, "commit", 1, samples);
            var catalog = new CardCatalogSnapshot(
                new CatalogMetadata("v1", DateTimeOffset.UnixEpoch),
                [new CardCatalogEntry("CARD_A", "测试甲", 2, "cards/a.png")]);
            var validationSamples = new[]
            {
                new ValidationSampleRecord(1, DateTimeOffset.UnixEpoch, "CARD_A", "测试甲", 2, "cards/a.png", false, "商店", 0, "v1", "images/a.png", 0.95),
                new ValidationSampleRecord(1, DateTimeOffset.UnixEpoch, "CARD_A", "旧名称", 2, "cards/a.png", false, "商店", 1, "old", "images/b.png", 0.95)
            };

            var first = VisionTrainingIndexBuilder.Build(manifest, root, catalog, validationSamples, maxPerScene: 3);
            var second = VisionTrainingIndexBuilder.Build(manifest, root, catalog, validationSamples, maxPerScene: 3);

            Assert.Equal(first.SceneSamples.Select(sample => sample.Path), second.SceneSamples.Select(sample => sample.Path));
            Assert.Equal(3, first.SceneSamples.Count);
            Assert.Equal(2, first.ValidatedCardSampleCount);
            Assert.Equal(1, first.CompatibleValidatedCardSampleCount);
            Assert.Equal(new[] { "logs/live-frames/20260907-200000-000-Shopping.png", "logs/live-frames/20260907-200002-000-Shopping.png", "logs/live-frames/20260907-200004-000-Shopping.png" }, first.SceneSamples.Select(sample => sample.Path));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesIndexAndRejectsUnsupportedSchema()
    {
        var root = CreateTempDirectory("training-index-json");
        try
        {
            var manifest = new LogCurationManifest(1, "commit", 1, []);
            var index = VisionTrainingIndexBuilder.Build(
                manifest,
                root,
                new CardCatalogSnapshot(new CatalogMetadata("v1", DateTimeOffset.UnixEpoch), []),
                generatedAt: DateTimeOffset.UnixEpoch);
            var path = Path.Combine(root, "index.json");

            index.Save(path);
            var restored = VisionTrainingIndex.Load(path);

            Assert.Equal(index.ToJson(), restored.ToJson());
            File.WriteAllText(path, index.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => VisionTrainingIndex.Load(path));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static FrameQualityMetrics Metrics() => new(1920, 1080, 100, 30, 0.1, 0.1, 0.05, 42);

    private static void WriteFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "sample");
    }

    private static string CreateTempDirectory(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
