using System.Text.Json;
using System.Globalization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

if (args.Length < 1 || string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase))
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

if (string.Equals(args[0], "--curate-logs", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4 || !string.Equals(args[2], "--manifest", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --curate-logs <logsDirectory> --manifest <manifestPath> [--source-commit <sha>]");
        return 2;
    }

    var sourceCommit = "unknown";
    for (var index = 4; index < args.Length; index++)
    {
        if (!string.Equals(args[index], "--source-commit", StringComparison.OrdinalIgnoreCase)
            || index + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: OfflineVisionProbe --curate-logs <logsDirectory> --manifest <manifestPath> [--source-commit <sha>]");
            return 2;
        }

        sourceCommit = args[++index];
    }

    try
    {
        var logsRoot = Path.GetFullPath(args[1]);
        var pathPrefix = Path.GetFileName(logsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var manifest = LogCurationScanner.Scan(logsRoot, sourceCommit, pathPrefix: pathPrefix);
        manifest.Save(Path.GetFullPath(args[3]));
        Console.WriteLine(LogCurationReportFormatter.Format(manifest));
        return manifest.Samples.Any(sample => sample.Quality is FrameQuality.Garbage or FrameQuality.Review) ? 1 : 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or DirectoryNotFoundException)
    {
        Console.Error.WriteLine($"日志筛选失败：{exception.Message}");
        return 1;
    }
}

if (string.Equals(args[0], "--validate-manifest", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length is < 2 or > 4 || (args.Length == 4 && !string.Equals(args[2], "--repo-root", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --validate-manifest <manifestPath> [--repo-root <path>]");
        return 2;
    }

    var repositoryRoot = args.Length == 4 ? Path.GetFullPath(args[3]) : Directory.GetCurrentDirectory();
    return ValidateManifest(Path.GetFullPath(args[1]), repositoryRoot);
}

if (string.Equals(args[0], "--crop", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 7 || !int.TryParse(args[2], out var x) || !int.TryParse(args[3], out var y)
        || !int.TryParse(args[4], out var width) || !int.TryParse(args[5], out var height))
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --crop <image> <x> <y> <width> <height> <output>");
        return 2;
    }

    using var source = Cv2.ImRead(Path.GetFullPath(args[1]), ImreadModes.Color);
    using var crop = new Mat(source, new Rect(x, y, width, height)).Clone();
    Cv2.ImWrite(Path.GetFullPath(args[6]), crop);
    return 0;
}

if (string.Equals(args[0], "--annotate", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --annotate <profile.json> <image> <output>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var annotated = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    if (annotated.Empty())
    {
        Console.Error.WriteLine("Cannot decode image.");
        return 2;
    }

    DrawRegion(annotated, assets.Layout.Regions.Shop, Scalar.Red, "shop");
    DrawRegion(annotated, assets.Layout.Regions.Hand, Scalar.Green, "hand");
    DrawRegion(annotated, assets.Layout.Regions.Board, Scalar.Blue, "board");
    foreach (var slot in ShopSlotDetector.Detect(annotated, assets.Layout.Regions.Shop, assets.Layout.ShopSlots))
        DrawRegion(annotated, slot, new Scalar(0, 128, 255), "");
    foreach (var slot in assets.Layout.HandSlots)
        DrawRegion(annotated, slot, new Scalar(0, 255, 0), "");
    foreach (var slot in assets.Layout.BoardSlots)
        DrawRegion(annotated, slot, new Scalar(255, 0, 0), "");
    DrawRegion(annotated, assets.Layout.GoldBounds, new Scalar(255, 255, 0), "gold");
    if (assets.Layout.GoldCoinBounds.Width > 0 && assets.Layout.GoldCoinBounds.Height > 0)
        DrawRegion(annotated, assets.Layout.GoldCoinBounds, new Scalar(0, 200, 255), "gold-coins");
    DrawRegion(annotated, assets.Layout.TavernTierBounds, new Scalar(255, 0, 255), "tier");
    Cv2.ImWrite(Path.GetFullPath(args[3]), annotated);
    Console.WriteLine($"Annotated: {Path.GetFullPath(args[3])}");
    return 0;
}

if (string.Equals(args[0], "--shop-crops", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --shop-crops <profile.json> <image> <outputDirectory>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var source = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    if (source.Empty())
    {
        Console.Error.WriteLine("Cannot decode image.");
        return 2;
    }

    var outputDirectory = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(outputDirectory);
    var shopSlots = ShopSlotDetector.Detect(source, assets.Layout.Regions.Shop, assets.Layout.ShopSlots);
    for (var index = 0; index < shopSlots.Count; index++)
    {
        var pixels = shopSlots[index].ToPixels(source.Width, source.Height);
        var pixelRect = ClampRect(new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height), source.Width, source.Height);
        using var crop = new Mat(source, pixelRect).Clone();
        var outputPath = Path.Combine(outputDirectory, $"shop-slot-{index}.png");
        Cv2.ImWrite(outputPath, crop);
        Console.WriteLine($"shop-slot-{index}: {pixelRect.X},{pixelRect.Y},{pixelRect.Width},{pixelRect.Height} -> {outputPath}");
    }

    return 0;
}

if (string.Equals(args[0], "--match-thumbnail-crops", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --match-thumbnail-crops <catalog.db> <crop1> [crop2 ...]");
        return 2;
    }

    var matcher = new CardThumbnailMatcher(
        new CardThumbnailFeatureStore(Path.GetFullPath(args[1])),
        minimumConfidence: 0,
        minimumMargin: 0);
    foreach (var input in args.Skip(2))
    {
        var imagePath = Path.GetFullPath(input);
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            Console.WriteLine($"{Path.GetFileName(imagePath)}: cannot decode");
            continue;
        }

        var match = matcher.Match(image);
        Console.WriteLine($"{Path.GetFileName(imagePath)} id={match.CardId ?? "UNKNOWN"} confidence={match.Confidence:P1}");
    }

    return 0;
}

using var pipeline = VisionRecognitionPipeline.Load(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
foreach (var input in args.Skip(2))
{
    var imagePath = Path.GetFullPath(input);
    using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
    if (image.Empty())
    {
        Console.WriteLine($"{Path.GetFileName(imagePath)}: cannot decode");
        continue;
    }

    var result = pipeline.Recognizer.Recognize(image, File.GetLastWriteTimeUtc(imagePath));
    var snapshot = result.Snapshot;
    Console.WriteLine($"{Path.GetFileName(imagePath)} {image.Width}x{image.Height} scene={result.Scene.GamePhase}({result.Scene.Confidence:P1}) gold={Format(result.Gold.Value, result.Gold.Confidence)} tier={Format(result.TavernTier.Value, result.TavernTier.Confidence)} armor={Format(result.Armor?.Value, result.Armor?.Confidence ?? 0)} actionable={snapshot.IsActionable}");
    foreach (var card in result.Cards)
    {
        var observation = card.Observation;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {observation.CardZone,-8} slot={observation.SlotIndex} kind={observation.Kind,-7} id={observation.CardId,-28} confidence={observation.Confidence:P1} rect={FormatBounds(observation.Bounds)}"));
    }
}

static string Format(int? value, double confidence) =>
    value is null ? $"unknown({confidence:P1})" : $"{value.Value}({confidence:P1})";

static string FormatBounds(NormalizedRect bounds) =>
    $"({bounds.X:P1},{bounds.Y:P1},{bounds.Width:P1},{bounds.Height:P1})";

static void DrawRegion(Mat image, NormalizedRect bounds, Scalar color, string label)
{
    var rect = bounds.ToPixels(image.Width, image.Height);
    Cv2.Rectangle(image, new Rect(rect.X, rect.Y, rect.Width, rect.Height), color, 2);
    if (!string.IsNullOrWhiteSpace(label))
        Cv2.PutText(image, label, new Point(rect.X, Math.Max(18, rect.Y - 4)), HersheyFonts.HersheySimplex, 0.6, color, 2);
}

static Rect ClampRect(Rect rect, int width, int height)
{
    var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
    var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
    var right = Math.Clamp(rect.X + rect.Width, x + 1, width);
    var bottom = Math.Clamp(rect.Y + rect.Height, y + 1, height);
    return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  OfflineVisionProbe <profile.json> <catalog.db> <image1> [image2 ...]");
    Console.WriteLine("  OfflineVisionProbe --annotate <profile.json> <image> <output>");
    Console.WriteLine("  OfflineVisionProbe --shop-crops <profile.json> <image> <outputDirectory>");
    Console.WriteLine("  OfflineVisionProbe --match-thumbnail-crops <catalog.db> <crop1> [crop2 ...]");
    Console.WriteLine("  OfflineVisionProbe --crop <image> <x> <y> <width> <height> <output>");
    Console.WriteLine("  OfflineVisionProbe --curate-logs <logsDirectory> --manifest <manifestPath> [--source-commit <sha>]");
    Console.WriteLine("  OfflineVisionProbe --validate-manifest <manifestPath> [--repo-root <path>]");
}

static int ValidateManifest(string manifestPath, string repositoryRoot)
{
    try
    {
        var manifest = LogCurationManifest.Load(manifestPath);
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var errors = new List<string>();
        foreach (var sample in manifest.Samples)
        {
            if (Path.IsPathRooted(sample.Path))
            {
                errors.Add($"绝对路径不允许：{sample.Path}");
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, sample.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                errors.Add($"路径越界：{sample.Path}");
            else if (!File.Exists(fullPath))
                errors.Add($"文件不存在：{sample.Path}");
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                Console.Error.WriteLine(error);
            return 1;
        }

        Console.WriteLine($"Manifest 有效：{manifest.Samples.Count} 个样本");
        Console.WriteLine(LogCurationReportFormatter.Format(manifest));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
    {
        Console.Error.WriteLine($"Manifest 校验失败：{exception.Message}");
        return 1;
    }
}

return 0;
