using BattlegroundsVisionAgent.App.ViewModels;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;

namespace BattlegroundsVisionAgent.Replay.Tests;

public sealed class RecognitionValidationUiTests
{
    [Fact]
    public void CardReferenceImageLoader_ResolvesExistingImageInsideCatalogRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "battlegrounds-vision-ui-tests", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "cards", "CARD_A.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        try
        {
            var entry = new CardCatalogEntry("CARD_A", "测试随从", 2, "cards/CARD_A.png");

            var resolved = CardReferenceImageLoader.ResolvePath(entry, root);

            Assert.Equal(Path.GetFullPath(imagePath), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CardReferenceImageLoader_RejectsPathOutsideCatalogRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "battlegrounds-vision-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var entry = new CardCatalogEntry("CARD_A", "测试随从", 2, "../CARD_A.png");

            Assert.Null(CardReferenceImageLoader.ResolvePath(entry, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CardReferenceImageLoader_ReturnsNullForCorruptImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "battlegrounds-vision-ui-tests", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "cards", "CARD_A.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x01, 0x02, 0x03]);

        try
        {
            var entry = new CardCatalogEntry("CARD_A", "测试随从", 2, "cards/CARD_A.png");

            var image = CardReferenceImageLoader.Load(entry, root);

            Assert.Null(image);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
