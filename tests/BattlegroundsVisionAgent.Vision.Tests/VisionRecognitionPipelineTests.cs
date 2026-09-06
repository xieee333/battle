using System.Text.Json;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class VisionRecognitionPipelineTests
{
    [Fact]
    public void Load_ReadsLayoutAnchorsAndBuildsPipelineFromProfileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vision-profile-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var anchorsDirectory = Path.Combine(root, "anchors");
            Directory.CreateDirectory(anchorsDirectory);
            foreach (var name in new[] { "shop", "hand", "board" })
            {
                using var image = new Mat(7, 7, MatType.CV_8UC1, Scalar.All(0));
                Cv2.Rectangle(image, new Rect(1, 1, 5, 5), Scalar.All(255), 1);
                Cv2.ImWrite(Path.Combine(anchorsDirectory, $"{name}.png"), image);
            }

            var profilePath = Path.Combine(root, "profile.json");
            File.WriteAllText(profilePath, JsonSerializer.Serialize(new
            {
                layout = Profile(),
                anchors = new Dictionary<string, string>
                {
                    ["shop"] = "anchors/shop.png",
                    ["hand"] = "anchors/hand.png",
                    ["board"] = "anchors/board.png"
                },
                scenes = Array.Empty<object>(),
                digits = Array.Empty<object>()
            }));

            using var assets = VisionProfileAssets.Load(profilePath);
            using var pipeline = VisionRecognitionPipeline.Load(profilePath, Path.Combine(root, "catalog.db"));

            Assert.Equal(3, assets.AnchorTemplates.Count);
            Assert.Empty(assets.SceneTemplates);
            Assert.Empty(assets.DigitTemplates);
            Assert.NotNull(pipeline.Recognizer);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsTemplatePathOutsideProfileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vision-profile-unsafe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var profilePath = Path.Combine(root, "profile.json");
            File.WriteAllText(profilePath, JsonSerializer.Serialize(new
            {
                layout = Profile(),
                anchors = new Dictionary<string, string>
                {
                    ["shop"] = "../shop.png",
                    ["hand"] = "hand.png",
                    ["board"] = "board.png"
                },
                scenes = Array.Empty<object>(),
                digits = Array.Empty<object>()
            }));

            Assert.Throws<InvalidDataException>(() => VisionProfileAssets.Load(profilePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static LayoutTemplateProfile Profile() => new(
        new LayoutRegions(
            new NormalizedRect(0.05, 0.05, 0.40, 0.25),
            new NormalizedRect(0.05, 0.40, 0.35, 0.25),
            new NormalizedRect(0.55, 0.40, 0.40, 0.25)),
        [new NormalizedRect(0.08, 0.08, 0.08, 0.12)],
        [new NormalizedRect(0.08, 0.45, 0.08, 0.12)],
        [new NormalizedRect(0.58, 0.45, 0.08, 0.12)],
        [],
        new NormalizedRect(0.90, 0.02, 0.05, 0.05),
        new NormalizedRect(0.82, 0.02, 0.05, 0.05),
        10,
        7);
}
