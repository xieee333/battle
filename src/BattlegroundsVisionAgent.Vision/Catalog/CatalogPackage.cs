using System.Text.Json;

namespace BattlegroundsVisionAgent.Vision.Catalog;

public sealed record CatalogPackageCard(string CardId, string NameZhCn, int Tier, string ImagePath);

public sealed record CatalogPackageManifest(
    string Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<CatalogPackageCard> Cards)
{
    public const string FileName = "catalog.manifest.json";

    public CatalogManifest ToValidationManifest() =>
        new(Version, Cards.Select(card => card.CardId).ToArray(), Cards.Select(card => card.ImagePath).ToArray());

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Cards is null)
            throw new InvalidDataException("The catalog package manifest is invalid.");

        if (Cards.Any(card => string.IsNullOrWhiteSpace(card.CardId)
            || string.IsNullOrWhiteSpace(card.NameZhCn)
            || card.Tier < 0
            || string.IsNullOrWhiteSpace(card.ImagePath)))
            throw new InvalidDataException("Every catalog card must contain a valid id, name, tier and image path.");

        if (Cards.Select(card => card.CardId).Distinct(StringComparer.Ordinal).Count() != Cards.Count)
            throw new InvalidDataException("Catalog card ids must be unique.");

        if (Cards.Select(card => card.ImagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Cards.Count)
            throw new InvalidDataException("Catalog image paths must be unique.");
    }

    public static CatalogPackageManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<CatalogPackageManifest>(stream, SerializerOptions);
        if (manifest is null)
            throw new InvalidDataException("The catalog package manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record CatalogUpdateResult(
    bool Applied,
    string Version,
    DateTimeOffset UpdatedAt,
    int CardCount,
    string Message);
