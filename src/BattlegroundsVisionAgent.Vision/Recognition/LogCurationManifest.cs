using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record LogCurationSample(
    string Path,
    string SourceLabel,
    string ExpectedScene,
    FrameQuality Quality,
    bool Include,
    bool ExpectedActionable,
    string? ExpectedPauseReason,
    FrameQualityMetrics Metrics,
    string Reason,
    string? DuplicateOf = null);

public sealed record LogCurationManifest(
    int SchemaVersion,
    string SourceCommit,
    int QualityPolicyVersion,
    IReadOnlyList<LogCurationSample> Samples)
{
    public const int CurrentSchemaVersion = 1;

    public DateTimeOffset? GeneratedAt { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, ToJson());
    }

    public static LogCurationManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<LogCurationManifest>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The log curation manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported log curation manifest schema: {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(SourceCommit))
            throw new InvalidDataException("The log curation manifest source commit is missing.");
        if (Samples is null)
            throw new InvalidDataException("The log curation manifest samples are missing.");
        if (Samples.Select(sample => sample.Path).Distinct(StringComparer.Ordinal).Count() != Samples.Count)
            throw new InvalidDataException("Log curation sample paths must be unique.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class LogCurationScanner
{
    private static readonly string[] SceneLabels = ["Shopping", "Discover", "Combat", "Unknown"];

    public static LogCurationManifest Scan(
        string root,
        string sourceCommit = "unknown",
        DateTimeOffset? generatedAt = null,
        string? pathPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(fullRoot);

        var files = Directory.EnumerateFiles(fullRoot, "*.png", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Normalize(Path.GetRelativePath(fullRoot, path))
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var samples = new List<LogCurationSample>(files.Length);
        var firstByHash = new Dictionary<ulong, string>();
        foreach (var file in files)
        {
            var manifestPath = string.IsNullOrWhiteSpace(pathPrefix)
                ? file.RelativePath
                : Normalize(Path.Combine(pathPrefix, file.RelativePath));
            var sourceLabel = ParseSceneLabel(manifestPath);
            var expectedScene = IsSceneLabel(sourceLabel) ? sourceLabel : "Unknown";
            var result = FrameQualityAnalyzer.AnalyzeFile(file.FullPath);
            var quality = result.Quality;
            var include = quality != FrameQuality.Garbage;
            var reason = result.Reason;
            string? duplicateOf = null;

            if (include && result.Metrics.PerceptualHash != 0
                && firstByHash.TryGetValue(result.Metrics.PerceptualHash, out var firstPath))
            {
                include = false;
                quality = FrameQuality.Garbage;
                reason = "duplicate";
                duplicateOf = firstPath;
            }
            else if (include && result.Metrics.PerceptualHash != 0)
            {
                firstByHash[result.Metrics.PerceptualHash] = manifestPath;
            }

            var expectedActionable = include && quality == FrameQuality.Good && expectedScene == "Shopping";
            var pauseReason = expectedActionable
                ? null
                : quality == FrameQuality.Garbage
                    ? "frame-quality-garbage"
                    : expectedScene == "Shopping"
                        ? "frame-quality-review"
                        : "scene-not-actionable";

            samples.Add(new LogCurationSample(
                manifestPath,
                sourceLabel,
                expectedScene,
                quality,
                include,
                expectedActionable,
                pauseReason,
                result.Metrics,
                reason,
                duplicateOf));
        }

        return new LogCurationManifest(
            LogCurationManifest.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(sourceCommit) ? "unknown" : sourceCommit,
            QualityPolicyVersion: 1,
            samples)
        {
            GeneratedAt = generatedAt
        };
    }

    private static string ParseSceneLabel(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var candidate = name.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return IsSceneLabel(candidate) ? candidate! : "Unknown";
    }

    private static bool IsSceneLabel(string? value) =>
        value is not null && SceneLabels.Contains(value, StringComparer.Ordinal);

    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
