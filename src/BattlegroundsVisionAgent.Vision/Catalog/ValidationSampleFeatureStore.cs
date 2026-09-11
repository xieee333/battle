using BattlegroundsVisionAgent.Vision.Validation;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Catalog;

/// <summary>
/// Turns confirmed screen crops into additional features. Official catalog
/// features remain untouched; only samples compatible with the current card
/// metadata are loaded.
/// </summary>
public sealed class ValidationSampleFeatureStore : ICardFeatureStore
{
    private readonly string _sampleDirectory;
    private readonly CardCatalog _catalog;
    private readonly object _sync = new();
    private IReadOnlyList<CardFeature>? _features;

    public ValidationSampleFeatureStore(string sampleDirectory, string catalogDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDatabasePath);
        _sampleDirectory = Path.GetFullPath(sampleDirectory);
        _catalog = new CardCatalog(catalogDatabasePath);
    }

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
        var repository = new RecognitionValidationSampleRepository(_sampleDirectory);
        var samples = repository.LoadSamples();
        if (samples.Count == 0)
            return [];

        var current = _catalog.ReadSnapshot().Entries.ToDictionary(entry => entry.CardId, StringComparer.Ordinal);
        var features = new List<CardFeature>();
        foreach (var sample in samples)
        {
            // The current matcher is intentionally shop-only. Keep hand,
            // board and discover samples for future zone-specific matchers,
            // but never let their different geometry pollute shop matching.
            if (!string.Equals(sample.Zone, "商店", StringComparison.Ordinal))
                continue;
            if (!current.TryGetValue(sample.CardId, out var entry)
                || !MetadataEquals(entry, sample))
                continue;
            if (!TryResolveSamplePath(sample.ImagePath, out var imagePath))
                continue;

            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty())
                continue;
            using var thumbnail = CardThumbnailPreprocessor.FromScreenSlot(image);
            if (thumbnail.Empty())
                continue;
            features.Add(CardFeatureFactory.Create(sample.CardId, thumbnail, sample.IsGolden, entry.Kind));
        }
        return features;
    }

    private bool TryResolveSamplePath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;
        var root = _sampleDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(_sampleDirectory, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        fullPath = candidate;
        return true;
    }

    private static bool MetadataEquals(CardCatalogEntry entry, ValidationSampleRecord sample) =>
        string.Equals(entry.NameZhCn, sample.CardName, StringComparison.Ordinal)
        && entry.Tier == sample.CardTier
        && string.Equals(entry.ImagePath, sample.CatalogImagePath, StringComparison.OrdinalIgnoreCase);
}

public sealed class CompositeCardFeatureStore : ICardFeatureStore
{
    private readonly IReadOnlyList<ICardFeatureStore> _stores;
    private IReadOnlyList<CardFeature>? _features;

    public CompositeCardFeatureStore(params ICardFeatureStore[] stores)
    {
        _stores = stores?.Where(store => store is not null).ToArray()
            ?? throw new ArgumentNullException(nameof(stores));
    }

    public IReadOnlyList<CardFeature> GetAll() =>
        _features ??= _stores.SelectMany(store => store.GetAll()).ToArray();
}
