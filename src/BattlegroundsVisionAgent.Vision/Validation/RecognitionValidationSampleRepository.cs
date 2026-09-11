using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Validation;

public sealed record ValidationSampleRecord(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string CardId,
    string CardName,
    int CardTier,
    string CatalogImagePath,
    bool IsGolden,
    string Zone,
    int SlotIndex,
    string CatalogVersion,
    string ImagePath,
    double Confidence);

public sealed record ValidationCatalogSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    string Version,
    IReadOnlyList<CardCatalogEntry> Entries);

public sealed record ValidationSampleCompatibilityReport(
    string CurrentCatalogVersion,
    int SampleCount,
    int UsableSampleCount,
    IReadOnlyList<string> AddedCardIds,
    IReadOnlyList<string> RemovedCardIds,
    IReadOnlyList<string> ChangedCardIds,
    IReadOnlyList<string> MissingSampleCardIds,
    IReadOnlyList<string> StaleSampleCardIds)
{
    public string ToDisplayText() =>
        $"卡库 {CurrentCatalogVersion} · 正样本 {UsableSampleCount}/{SampleCount} 可用 · " +
        $"新增 {AddedCardIds.Count} · 移除 {RemovedCardIds.Count} · 资料变更 {ChangedCardIds.Count} · " +
        $"需复核 {MissingSampleCardIds.Count + StaleSampleCardIds.Count}";
}

public sealed record ValidationSampleSaveResult(
    string FeedbackPath,
    int SavedSampleCount,
    int SkippedSampleCount,
    ValidationSampleCompatibilityReport Compatibility)
{
    public string ToDisplayText() =>
        $"反馈已保存；新增可复用正样本 {SavedSampleCount} 个，未采纳 {SkippedSampleCount} 个。{Compatibility.ToDisplayText()}";
}

/// <summary>
/// Stores human-confirmed screen crops separately from the official catalog.
/// Samples are additive and are never written into catalog.db, so updating the
/// official catalog can be rolled back without losing user feedback.
/// </summary>
public sealed class RecognitionValidationSampleRepository
{
    private const int MaxSamplesPerCardVariant = 8;
    private const string SamplesFileName = "samples.json";
    private const string CatalogSnapshotFileName = "catalog-snapshot.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();

    public RecognitionValidationSampleRepository(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultDirectory : rootDirectory;
    }

    public string RootDirectory { get; }
    public string SamplesFilePath => Path.Combine(RootDirectory, SamplesFileName);
    public string CatalogSnapshotFilePath => Path.Combine(RootDirectory, CatalogSnapshotFileName);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BattlegroundsVisionAgent",
        "validation-samples");

    public IReadOnlyList<ValidationSampleRecord> LoadSamples()
    {
        lock (_sync)
        {
            return LoadSamplesCore();
        }
    }

    public ValidationSampleCompatibilityReport ValidateAgainstCatalog(CardCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_sync)
        {
            return BuildCompatibilityReport(catalog, LoadSamplesCore(), LoadCatalogSnapshotCore());
        }
    }

    public ValidationSampleSaveResult Save(
        RecognitionValidationFeedback feedback,
        byte[] screenshotPng,
        CardCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(screenshotPng);
        ArgumentNullException.ThrowIfNull(catalog);
        if (screenshotPng.Length == 0)
            throw new InvalidDataException("截图内容为空，无法保存正样本。");

        lock (_sync)
        {
            Directory.CreateDirectory(RootDirectory);
            var sampleImageDirectory = Path.Combine(RootDirectory, "images");
            Directory.CreateDirectory(sampleImageDirectory);
            using var frame = Cv2.ImDecode(screenshotPng, ImreadModes.Color);
            if (frame.Empty())
                throw new InvalidDataException("截图无法解码，无法保存正样本。");

            var currentEntries = catalog.Entries.ToDictionary(entry => entry.CardId, StringComparer.Ordinal);
            var samples = LoadSamplesCore().ToList();
            var previousCatalogSnapshot = LoadCatalogSnapshotCore();
            var saved = 0;
            var skipped = 0;
            foreach (var item in feedback.Cards)
            {
                if (!TryResolveApprovedCard(item, currentEntries, out var entry, out var cardId)
                    || !TryCrop(frame, item.Bounds, out var crop))
                {
                    skipped++;
                    continue;
                }

                using (crop)
                {
                    var cardDirectory = Path.Combine(sampleImageDirectory, SafeFilePart(cardId));
                    Directory.CreateDirectory(cardDirectory);
                    var relativeImagePath = Path.Combine(
                        "images",
                        SafeFilePart(cardId),
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
                    var fullImagePath = Path.Combine(RootDirectory, relativeImagePath);
                    if (!Cv2.ImEncode(".png", crop, out var encoded))
                    {
                        skipped++;
                        continue;
                    }

                    File.WriteAllBytes(fullImagePath, encoded);
                    samples.Add(new ValidationSampleRecord(
                        SchemaVersion: 1,
                        CreatedAt: DateTimeOffset.UtcNow,
                        CardId: cardId,
                        CardName: entry.NameZhCn,
                        CardTier: entry.Tier,
                        CatalogImagePath: entry.ImagePath,
                        IsGolden: item.IsGolden,
                        Zone: item.Zone,
                        SlotIndex: item.SlotIndex,
                        CatalogVersion: catalog.Metadata?.Version ?? feedback.CatalogVersion,
                        ImagePath: relativeImagePath.Replace(Path.DirectorySeparatorChar, '/'),
                        Confidence: item.Confidence));
                    saved++;
                }
            }

            var retained = samples
                .Where(sample => File.Exists(Path.Combine(RootDirectory, sample.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
                .GroupBy(sample => $"{sample.CardId}\u001f{sample.IsGolden}", StringComparer.Ordinal)
                .SelectMany(group => group.OrderByDescending(sample => sample.CreatedAt).Take(MaxSamplesPerCardVariant))
                .OrderBy(sample => sample.CardId, StringComparer.Ordinal)
                .ThenBy(sample => sample.CreatedAt)
                .ToArray();
            WriteJsonAtomically(SamplesFilePath, retained);
            WriteJsonAtomically(CatalogSnapshotFilePath, new ValidationCatalogSnapshot(
                SchemaVersion: 1,
                SavedAt: DateTimeOffset.UtcNow,
                Version: catalog.Metadata?.Version ?? feedback.CatalogVersion,
                Entries: catalog.Entries));

            var feedbackDirectory = GetFeedbackDirectory();
            Directory.CreateDirectory(feedbackDirectory);
            var feedbackPath = Path.Combine(feedbackDirectory, $"validation-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            WriteJsonAtomically(feedbackPath, feedback);

            var compatibility = BuildCompatibilityReport(catalog, retained, previousCatalogSnapshot);
            return new ValidationSampleSaveResult(feedbackPath, saved, skipped, compatibility);
        }
    }

    private IReadOnlyList<ValidationSampleRecord> LoadSamplesCore()
    {
        if (!File.Exists(SamplesFilePath))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ValidationSampleRecord>>(
                       File.ReadAllText(SamplesFilePath), JsonOptions)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private ValidationCatalogSnapshot? LoadCatalogSnapshotCore()
    {
        if (!File.Exists(CatalogSnapshotFilePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ValidationCatalogSnapshot>(
                File.ReadAllText(CatalogSnapshotFilePath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValidationSampleCompatibilityReport BuildCompatibilityReport(
        CardCatalogSnapshot catalog,
        IReadOnlyList<ValidationSampleRecord> samples,
        ValidationCatalogSnapshot? previousSnapshot)
    {
        var current = catalog.Entries.ToDictionary(entry => entry.CardId, StringComparer.Ordinal);
        var previous = previousSnapshot?.Entries.ToDictionary(entry => entry.CardId, StringComparer.Ordinal)
                       ?? new Dictionary<string, CardCatalogEntry>(StringComparer.Ordinal);
        var added = current.Keys.Except(previous.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var removed = previous.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = current.Keys.Intersect(previous.Keys, StringComparer.Ordinal)
            .Where(cardId => !MetadataEquals(current[cardId], previous[cardId]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingSamples = samples.Where(sample => !current.ContainsKey(sample.CardId))
            .Select(sample => sample.CardId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var staleSamples = samples.Where(sample => current.TryGetValue(sample.CardId, out var entry)
                && !MetadataEquals(entry, new CardCatalogEntry(sample.CardId, sample.CardName, sample.CardTier, sample.CatalogImagePath)))
            .Select(sample => sample.CardId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var usable = samples.Count(sample => current.TryGetValue(sample.CardId, out var entry)
            && MetadataEquals(entry, new CardCatalogEntry(sample.CardId, sample.CardName, sample.CardTier, sample.CatalogImagePath)));

        return new ValidationSampleCompatibilityReport(
            catalog.Metadata?.Version ?? "未标注版本",
            samples.Count,
            usable,
            added,
            removed,
            changed,
            missingSamples,
            staleSamples);
    }

    private static bool TryResolveApprovedCard(
        RecognitionValidationCardFeedback item,
        IReadOnlyDictionary<string, CardCatalogEntry> currentEntries,
        out CardCatalogEntry entry,
        out string cardId)
    {
        entry = null!;
        cardId = string.Empty;
        if (item.RecognitionStatus == "空槽" || item.PositionStatus != "正确")
            return false;

        if (item.RecognitionStatus == "正确"
            && item.PredictedCardId != "UNKNOWN"
            && currentEntries.TryGetValue(item.PredictedCardId, out var predictedEntry))
        {
            entry = predictedEntry;
            cardId = entry.CardId;
            return true;
        }

        if (item.RecognitionStatus != "错误" || string.IsNullOrWhiteSpace(item.ExpectedText))
            return false;

        var expected = item.ExpectedText.Trim();
        var matchedEntry = currentEntries.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.CardId, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.NameZhCn, expected, StringComparison.Ordinal));
        if (matchedEntry is null)
            return false;
        entry = matchedEntry;
        cardId = entry.CardId;
        return true;
    }

    private static bool TryCrop(Mat frame, NormalizedRect bounds, out Mat crop)
    {
        crop = new Mat();
        var x = Math.Clamp((int)Math.Floor(bounds.X * frame.Width), 0, Math.Max(0, frame.Width - 1));
        var y = Math.Clamp((int)Math.Floor(bounds.Y * frame.Height), 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * frame.Width), x + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * frame.Height), y + 1, frame.Height);
        if (right <= x || bottom <= y)
            return false;
        crop = new Mat(frame, new Rect(x, y, right - x, bottom - y)).Clone();
        return !crop.Empty();
    }

    private static bool MetadataEquals(CardCatalogEntry left, CardCatalogEntry right) =>
        string.Equals(left.CardId, right.CardId, StringComparison.Ordinal)
        && string.Equals(left.NameZhCn, right.NameZhCn, StringComparison.Ordinal)
        && left.Tier == right.Tier
        && string.Equals(left.ImagePath, right.ImagePath, StringComparison.OrdinalIgnoreCase);

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown-card" : result;
    }

    private string GetFeedbackDirectory()
    {
        var fullRoot = Path.GetFullPath(RootDirectory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(fullRoot), "validation-samples", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(fullRoot)!, "validation-feedback")
            : Path.Combine(fullRoot, "feedback");
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
