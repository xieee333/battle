using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Validation;

namespace BattlegroundsVisionAgent.Vision.Recognition;

/// <summary>
/// A reproducible, version-aware description of the data used by the hybrid
/// recognizer. It is deliberately an index rather than a fixed card classifier:
/// the card labels come from the current catalog and confirmed runtime crops.
/// </summary>
public sealed record VisionTrainingSceneSample(
    string Path,
    GamePhase GamePhase,
    string LabelSource,
    FrameQuality Quality,
    FrameQualityMetrics Metrics);

public sealed record VisionTrainingIndex(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string SourceCommit,
    int QualityPolicyVersion,
    string CatalogVersion,
    string CatalogFingerprint,
    int CatalogCardCount,
    IReadOnlyList<VisionTrainingSceneSample> SceneSamples,
    int ValidatedCardSampleCount,
    int CompatibleValidatedCardSampleCount)
{
    public const int CurrentSchemaVersion = 1;
    public const string ModelKind = "hybrid-dynamic-feature-index";

    public string FeatureModel => ModelKind;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, ToJson());
    }

    public static VisionTrainingIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var index = JsonSerializer.Deserialize<VisionTrainingIndex>(stream, JsonOptions)
            ?? throw new InvalidDataException("The vision training index is empty.");
        index.Validate();
        return index;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported vision training index schema: {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(SourceCommit))
            throw new InvalidDataException("The vision training index source commit is missing.");
        if (QualityPolicyVersion < 1)
            throw new InvalidDataException("The vision training index quality policy is invalid.");
        if (string.IsNullOrWhiteSpace(CatalogFingerprint)
            || CatalogFingerprint.Length != 64
            || CatalogFingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The vision training index catalog fingerprint is invalid.");
        if (CatalogCardCount < 0 || ValidatedCardSampleCount < 0
            || CompatibleValidatedCardSampleCount < 0
            || CompatibleValidatedCardSampleCount > ValidatedCardSampleCount)
            throw new InvalidDataException("The vision training index counts are invalid.");
        if (SceneSamples is null)
            throw new InvalidDataException("The vision training index scene samples are missing.");
        if (SceneSamples.Select(sample => sample.Path).Distinct(StringComparer.Ordinal).Count() != SceneSamples.Count)
            throw new InvalidDataException("Vision training scene sample paths must be unique.");
    }

    public string ToText() =>
        $"训练索引：场景样本 {SceneSamples.Count} · 卡库 {CatalogVersion}（{CatalogCardCount} 张）· "
        + $"已确认卡牌样本 {CompatibleValidatedCardSampleCount}/{ValidatedCardSampleCount} 可兼容 · "
        + "模型类型：动态特征索引（不是固定版本神经网络权重）";
}

public static class VisionTrainingIndexBuilder
{
    private static readonly GamePhase[] LabeledPhases =
        [GamePhase.Shopping, GamePhase.Discover, GamePhase.Combat];

    public static VisionTrainingIndex Build(
        LogCurationManifest manifest,
        string repositoryRoot,
        CardCatalogSnapshot catalog,
        IEnumerable<ValidationSampleRecord>? validationSamples = null,
        int maxPerScene = 0,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        if (maxPerScene < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerScene));

        manifest.Validate();
        var fullRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(fullRoot);

        var candidates = manifest.Samples
            .Where(sample => sample.Include
                && sample.Quality == FrameQuality.Good
                && LabeledPhases.Contains(ParsePhase(sample.ExpectedScene))
                && IsFullFramePath(sample.Path)
                && TryResolveContainedFile(fullRoot, sample.Path))
            .OrderBy(sample => ParsePhase(sample.ExpectedScene))
            .ThenBy(sample => sample.Path, StringComparer.Ordinal)
            .ToArray();

        var selected = LabeledPhases
            .SelectMany(phase => SelectRepresentatives(
                candidates.Where(sample => ParsePhase(sample.ExpectedScene) == phase), maxPerScene))
            .ToArray();

        var entries = catalog.Entries ?? [];
        var entryMap = entries.ToDictionary(entry => entry.CardId, StringComparer.Ordinal);
        var validated = validationSamples?.ToArray() ?? [];
        var compatibleCount = validated.Count(sample =>
            entryMap.TryGetValue(sample.CardId, out var entry)
            && MetadataEquals(entry, sample));

        return new VisionTrainingIndex(
            VisionTrainingIndex.CurrentSchemaVersion,
            generatedAt ?? DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(manifest.SourceCommit) ? "unknown" : manifest.SourceCommit,
            manifest.QualityPolicyVersion,
            catalog.Metadata?.Version ?? "unknown",
            ComputeCatalogFingerprint(entries),
            entries.Count,
            selected.Select(sample => new VisionTrainingSceneSample(
                    sample.Path,
                    ParsePhase(sample.ExpectedScene),
                    "filename",
                    sample.Quality,
                    sample.Metrics))
                .ToArray(),
            validated.Length,
            compatibleCount);
    }

    private static IReadOnlyList<LogCurationSample> SelectRepresentatives(
        IEnumerable<LogCurationSample> source,
        int maxPerScene)
    {
        var ordered = source.OrderBy(sample => sample.Path, StringComparer.Ordinal).ToArray();
        if (maxPerScene == 0 || ordered.Length <= maxPerScene)
            return ordered;
        if (maxPerScene == 1)
            return [ordered[0]];

        return Enumerable.Range(0, maxPerScene)
            .Select(index => ordered[(int)Math.Round(index * (ordered.Length - 1d) / (maxPerScene - 1), MidpointRounding.ToEven)])
            .DistinctBy(sample => sample.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static GamePhase ParsePhase(string? value) =>
        Enum.TryParse<GamePhase>(value, ignoreCase: false, out var phase) ? phase : GamePhase.Unknown;

    private static bool IsFullFramePath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => string.Equals(segment, "live-frames", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveContainedFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;
        var fullRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate);
    }

    private static bool MetadataEquals(CardCatalogEntry entry, ValidationSampleRecord sample) =>
        string.Equals(entry.NameZhCn, sample.CardName, StringComparison.Ordinal)
        && entry.Tier == sample.CardTier
        && string.Equals(entry.ImagePath, sample.CatalogImagePath, StringComparison.OrdinalIgnoreCase);

    public static string ComputeCatalogFingerprint(IEnumerable<CardCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var canonical = string.Join('\n', entries
            .OrderBy(entry => entry.CardId, StringComparer.Ordinal)
            .Select(entry => string.Join('\u001f', entry.CardId, entry.NameZhCn, entry.Tier, entry.ImagePath)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
