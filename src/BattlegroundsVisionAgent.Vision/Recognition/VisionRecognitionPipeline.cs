using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed class VisionRecognitionPipeline : IDisposable
{
    private readonly TemplateLayoutRecognizer _layoutRecognizer;
    private bool _disposed;

    private VisionRecognitionPipeline(
        TemplateLayoutRecognizer layoutRecognizer,
        SnapshotRecognizer recognizer)
    {
        _layoutRecognizer = layoutRecognizer;
        Recognizer = recognizer;
    }

    public SnapshotRecognizer Recognizer { get; }

    public static VisionRecognitionPipeline Load(string profilePath, string catalogDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDatabasePath);
        var assets = VisionProfileAssets.Load(profilePath);
        try
        {
            var layoutRecognizer = new TemplateLayoutRecognizer(assets.Layout, assets.AnchorTemplates);
            var recognizer = new SnapshotRecognizer(
                layoutRecognizer,
                new CardMatcher(new CardFeatureStore(catalogDatabasePath)),
                new DigitRecognizer(assets.DigitTemplates),
                new SceneRecognizer(assets.SceneTemplates));
            return new VisionRecognitionPipeline(layoutRecognizer, recognizer);
        }
        catch
        {
            assets.Dispose();
            throw;
        }
        finally
        {
            assets.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _layoutRecognizer.Dispose();
    }
}

public sealed class VisionProfileAssets : IDisposable
{
    private static readonly string[] RequiredAnchors = ["shop", "hand", "board"];
    private readonly List<Mat> _ownedTemplates;
    private bool _disposed;

    private VisionProfileAssets(
        LayoutTemplateProfile layout,
        IReadOnlyDictionary<string, Mat> anchorTemplates,
        IReadOnlyList<SceneTemplate> sceneTemplates,
        IReadOnlyList<DigitTemplate> digitTemplates,
        List<Mat> ownedTemplates)
    {
        Layout = layout;
        AnchorTemplates = anchorTemplates;
        SceneTemplates = sceneTemplates;
        DigitTemplates = digitTemplates;
        _ownedTemplates = ownedTemplates;
    }

    public LayoutTemplateProfile Layout { get; }
    public IReadOnlyDictionary<string, Mat> AnchorTemplates { get; }
    public IReadOnlyList<SceneTemplate> SceneTemplates { get; }
    public IReadOnlyList<DigitTemplate> DigitTemplates { get; }

    public static VisionProfileAssets Load(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        var fullProfilePath = Path.GetFullPath(profilePath);
        if (!File.Exists(fullProfilePath))
            throw new FileNotFoundException("Vision profile was not found.", fullProfilePath);

        var profileDirectory = Path.GetDirectoryName(fullProfilePath)!;
        using var stream = File.OpenRead(fullProfilePath);
        var document = JsonSerializer.Deserialize<VisionProfileDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("Vision profile is empty.");
        document.Validate();

        var ownedTemplates = new List<Mat>();
        try
        {
            var anchors = new Dictionary<string, Mat>(StringComparer.OrdinalIgnoreCase);
            foreach (var anchor in document.Anchors)
            {
                var path = ResolveContainedPath(profileDirectory, anchor.Value);
                var image = ReadTemplate(path, $"anchor:{anchor.Key}");
                ownedTemplates.Add(image);
                anchors[anchor.Key] = image;
            }

            var scenes = document.Scenes.Select(scene =>
            {
                using var image = ReadTemplate(ResolveContainedPath(profileDirectory, scene.ImagePath), $"scene:{scene.GamePhase}");
                return new SceneTemplate(scene.GamePhase, PerceptualHash.Create(image));
            }).ToArray();
            var digits = document.Digits.Select(digit =>
            {
                using var image = ReadTemplate(ResolveContainedPath(profileDirectory, digit.ImagePath), $"digit:{digit.Value}");
                return new DigitTemplate(digit.Value, PerceptualHash.Create(image));
            }).ToArray();

            return new VisionProfileAssets(document.Layout, anchors, scenes, digits, ownedTemplates);
        }
        catch
        {
            foreach (var template in ownedTemplates)
                template.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var template in _ownedTemplates)
            template.Dispose();
    }

    private static Mat ReadTemplate(string path, string name)
    {
        var image = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidDataException($"Vision template cannot be decoded ({name}): {path}");
        }

        return image;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Vision template path must be relative: {relativePath}");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Vision template path escapes the profile directory: {relativePath}");
        return candidate;
    }

    private sealed class VisionProfileDocument
    {
        public LayoutTemplateProfile Layout { get; init; } = null!;
        public Dictionary<string, string> Anchors { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SceneTemplateFile> Scenes { get; init; } = [];
        public List<DigitTemplateFile> Digits { get; init; } = [];

        public void Validate()
        {
            Layout?.Validate();
            if (Anchors is null || RequiredAnchors.Any(required =>
                    !Anchors.Keys.Any(key => string.Equals(key, required, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Vision profile must define shop, hand and board anchor images.");
            if (Anchors.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
                throw new InvalidDataException("Vision profile anchor paths cannot be empty.");
            if (Scenes.Any(scene => scene is null || string.IsNullOrWhiteSpace(scene.ImagePath)))
                throw new InvalidDataException("Vision profile scene templates are invalid.");
            if (Digits.Any(digit => digit is null || digit.Value < 0 || string.IsNullOrWhiteSpace(digit.ImagePath)))
                throw new InvalidDataException("Vision profile digit templates are invalid.");
        }
    }

    private sealed class SceneTemplateFile
    {
        public GamePhase GamePhase { get; init; }
        public string ImagePath { get; init; } = string.Empty;
    }

    private sealed class DigitTemplateFile
    {
        public int Value { get; init; }
        public string ImagePath { get; init; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
