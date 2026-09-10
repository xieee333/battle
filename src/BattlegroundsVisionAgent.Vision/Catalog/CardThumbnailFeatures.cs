using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Catalog;

/// <summary>
/// Normalizes card presentations used by the game and the official catalog.
/// The catalog contains a complete card image, while runtime zones expose a
/// visible portrait plus varying amounts of frame, tier and stat chrome.
/// </summary>
public static class CardThumbnailPreprocessor
{
    public static Mat FromCatalogCard(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CropRelative(image, 0.22, 0.08, 0.56, 0.54);
    }

    public static Mat FromScreenSlot(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CropRelative(image, 0.13, 0.20, 0.74, 0.65);
    }

    private static Mat CropRelative(Mat image, double x, double y, double width, double height)
    {
        if (image.Empty())
            return new Mat();

        var pixelX = Math.Clamp((int)Math.Round(image.Width * x), 0, Math.Max(0, image.Width - 1));
        var pixelY = Math.Clamp((int)Math.Round(image.Height * y), 0, Math.Max(0, image.Height - 1));
        var pixelRight = Math.Clamp((int)Math.Round(image.Width * (x + width)), pixelX + 1, image.Width);
        var pixelBottom = Math.Clamp((int)Math.Round(image.Height * (y + height)), pixelY + 1, image.Height);
        return new Mat(image, new Rect(pixelX, pixelY, pixelRight - pixelX, pixelBottom - pixelY)).Clone();
    }
}

/// <summary>
/// Builds screen-thumbnail features directly from the currently installed
/// catalog images. This keeps card-version updates automatic without requiring
/// a second manually maintained feature package.
/// </summary>
public sealed class CardThumbnailFeatureStore(string databasePath) : ICardFeatureStore
{
    private readonly object _sync = new();
    private IReadOnlyList<CardFeature>? _features;

    public IReadOnlyList<CardFeature> GetAll()
    {
        if (_features is not null)
            return _features;

        lock (_sync)
        {
            if (_features is not null)
                return _features;

            _features = BuildFeatures();
            return _features;
        }
    }

    private IReadOnlyList<CardFeature> BuildFeatures()
    {
        if (!File.Exists(databasePath))
            return [];

        var catalog = new CardCatalog(databasePath);
        var root = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var features = new List<CardFeature>();
        foreach (var entry in catalog.GetEntries())
        {
            if (!TryResolveImagePath(root, entry.ImagePath, out var imagePath))
                continue;

            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty())
                continue;
            using var thumbnail = CardThumbnailPreprocessor.FromCatalogCard(image);
            if (thumbnail.Empty())
                continue;
            features.Add(CardFeatureFactory.Create(entry.CardId, thumbnail, isGolden: false));
        }

        return features;
    }

    private static bool TryResolveImagePath(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }
}
