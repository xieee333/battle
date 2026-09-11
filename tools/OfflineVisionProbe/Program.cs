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

if (string.Equals(args[0], "--recognize-screenshot", StringComparison.OrdinalIgnoreCase))
    return RecognizeScreenshot(args);

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
    foreach (var slot in CardZoneSlotDetector.DetectHand(
                 annotated,
                 assets.Layout.Regions.Hand,
                 assets.Layout.HandSlots))
        DrawRegion(annotated, slot, new Scalar(0, 255, 0), "");
    foreach (var slot in CardZoneSlotDetector.DetectBoard(
                 annotated,
                 assets.Layout.Regions.Board,
                 assets.Layout.BoardSlots))
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

if (string.Equals(args[0], "--aura-debug", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --aura-debug <profile.json> <image>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var source = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    var board = assets.Layout.Regions.Board.ToPixels(source.Width, source.Height);
    using var crop = new Mat(source, new Rect(board.X, board.Y, board.Width, board.Height));
    using var hsv = new Mat();
    Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
    using var mask = new Mat();
    Cv2.InRange(hsv, new Scalar(75, 75, 65), new Scalar(125, 255, 255), mask);
    using var labels = new Mat();
    using var stats = new Mat();
    using var centroids = new Mat();
    var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
    for (var component = 1; component < count; component++)
    {
        var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
        var x = stats.At<int>(component, (int)ConnectedComponentsTypes.Left);
        var y = stats.At<int>(component, (int)ConnectedComponentsTypes.Top);
        var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
        var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
        var centerX = centroids.At<double>(component, 0) + board.X;
        var centerY = centroids.At<double>(component, 1) + board.Y;
        Console.WriteLine($"component area={area} rect={x},{y},{width},{height} center={centerX:F1},{centerY:F1}");
    }

    return 0;
}

if (string.Equals(args[0], "--zone-score", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --zone-score <profile.json> <image>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var source = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    foreach (var (zone, slots) in new[]
    {
        (CardZone.Shop, assets.Layout.ShopSlots),
        (CardZone.Hand, assets.Layout.HandSlots),
        (CardZone.Board, assets.Layout.BoardSlots)
    })
    {
        Console.WriteLine(zone);
        for (var index = 0; index < slots.Count; index++)
        {
            var pixels = slots[index].ToPixels(source.Width, source.Height);
            using var crop = new Mat(source, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
            using var hsv = new Mat();
            Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
            using var mask = new Mat();
            Cv2.InRange(hsv, new Scalar(zone == CardZone.Hand ? 35 : 75, 90, 70), new Scalar(125, 255, 255), mask);
            var ratio = Cv2.CountNonZero(mask) / (double)(mask.Width * mask.Height);
            using var center = new Mat(mask, new Rect(mask.Width / 10, mask.Height / 10,
                Math.Max(1, mask.Width * 8 / 10), Math.Max(1, mask.Height * 8 / 10)));
            var centerRatio = Cv2.CountNonZero(center) / (double)(center.Width * center.Height);
            using var gray = new Mat();
            Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 80, 160);
            var leftEdges = new Mat(edges, new Rect(0, edges.Height * 63 / 100,
                Math.Max(1, edges.Width * 42 / 100), Math.Max(1, edges.Height * 31 / 100)));
            var rightEdges = new Mat(edges, new Rect(edges.Width * 58 / 100, edges.Height * 63 / 100,
                Math.Max(1, edges.Width * 42 / 100), Math.Max(1, edges.Height * 31 / 100)));
            var edgeScore = (Cv2.CountNonZero(leftEdges) + Cv2.CountNonZero(rightEdges)) /
                (double)(leftEdges.Width * leftEdges.Height + rightEdges.Width * rightEdges.Height);
            leftEdges.Dispose();
            rightEdges.Dispose();
            var kind = CardKindDetector.DetectShopCard(crop);
            Console.WriteLine($"  slot={index} ratio={ratio:P2} center={centerRatio:P2} edge={edgeScore:P2} kind={kind.Kind} kindConf={kind.Confidence:P1}");
        }
    }

    return 0;
}

if (string.Equals(args[0], "--hand-debug", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --hand-debug <profile.json> <image>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var source = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    var hand = assets.Layout.Regions.Hand.ToPixels(source.Width, source.Height);
    using var crop = new Mat(source, new Rect(hand.X, hand.Y, hand.Width, hand.Height));
    using var hsv = new Mat();
    Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
    using var mask = new Mat();
    Cv2.InRange(hsv, new Scalar(5, 100, 100), new Scalar(50, 255, 255), mask);
    using var labels = new Mat();
    using var stats = new Mat();
    using var centroids = new Mat();
    var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
    for (var component = 1; component < count; component++)
    {
        var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
        var x = stats.At<int>(component, (int)ConnectedComponentsTypes.Left);
        var y = stats.At<int>(component, (int)ConnectedComponentsTypes.Top);
        var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
        var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
        if (area >= 20 && width >= 4 && height >= 4)
            Console.WriteLine($"component area={area} rect={x},{y},{width},{height} center={centroids.At<double>(component,0)+hand.X:F1},{centroids.At<double>(component,1)+hand.Y:F1}");
    }

    using var auraMask = new Mat();
    Cv2.InRange(hsv, new Scalar(35, 90, 70), new Scalar(125, 255, 255), auraMask);
    var runs = new List<(int Start, int End)>();
    var runStart = -1;
    var maskWidth = auraMask.Width;
    var maskHeight = auraMask.Height;
    for (var x = 0; x < maskWidth; x++)
    {
        using var column = new Mat(auraMask, new Rect(x, 20, 1, Math.Min(60, maskHeight - 20)));
        var hits = Cv2.CountNonZero(column);
        if (hits >= 4 && runStart < 0)
            runStart = x;
        if ((hits < 4 || x == maskWidth - 1) && runStart >= 0)
        {
            var end = hits < 4 ? x - 1 : x;
            if (end - runStart >= 3)
                runs.Add((runStart + hand.X, end + hand.X));
            runStart = -1;
        }
    }
    Console.WriteLine("top-band-runs");
    foreach (var run in runs)
        Console.WriteLine($"  {run.Start}-{run.End} center={(run.Start + run.End) / 2d:F1}");

    using var purpleMask = new Mat();
    Cv2.InRange(hsv, new Scalar(125, 80, 45), new Scalar(179, 255, 255), purpleMask);
    using var purpleLabels = new Mat();
    using var purpleStats = new Mat();
    using var purpleCentroids = new Mat();
    var purpleCount = Cv2.ConnectedComponentsWithStats(purpleMask, purpleLabels, purpleStats, purpleCentroids);
    Console.WriteLine("purple-components");
    for (var component = 1; component < purpleCount; component++)
    {
        var area = purpleStats.At<int>(component, (int)ConnectedComponentsTypes.Area);
        var x = purpleStats.At<int>(component, (int)ConnectedComponentsTypes.Left);
        var y = purpleStats.At<int>(component, (int)ConnectedComponentsTypes.Top);
        var width = purpleStats.At<int>(component, (int)ConnectedComponentsTypes.Width);
        var height = purpleStats.At<int>(component, (int)ConnectedComponentsTypes.Height);
        if (area >= 50 && width >= 5 && height >= 5 && y < maskHeight * 0.72)
            Console.WriteLine($"  area={area} rect={x + hand.X},{y + hand.Y},{width},{height} center={purpleCentroids.At<double>(component,0)+hand.X:F1},{purpleCentroids.At<double>(component,1)+hand.Y:F1}");
    }

    using var grayHand = new Mat();
    Cv2.CvtColor(crop, grayHand, ColorConversionCodes.BGR2GRAY);
    using var blurred = new Mat();
    Cv2.MedianBlur(grayHand, blurred, 5);
    var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1, 45, 80, 22, 8, 28);
    Console.WriteLine("circles");
    foreach (var circle in circles)
        Console.WriteLine($"  center={circle.Center.X + hand.X:F1},{circle.Center.Y + hand.Y:F1} radius={circle.Radius:F1}");
    var detectedHandSlots = CardZoneSlotDetector.DetectHand(source, assets.Layout.Regions.Hand,
        assets.Layout.HandSlots);
    Console.WriteLine($"detected-hand-slots={detectedHandSlots.Count}");
    foreach (var slot in detectedHandSlots)
        Console.WriteLine($"  slot={slot.X:P1},{slot.Y:P1},{slot.Width:P1},{slot.Height:P1}");

    return 0;
}

if (string.Equals(args[0], "--tier-debug", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --tier-debug <profile.json> <image>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var source = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    var bounds = assets.Layout.TavernTierBounds.ToPixels(source.Width, source.Height);
    using var crop = new Mat(source, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    using var hsv = new Mat();
    Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
    using var mask = new Mat();
    Cv2.InRange(hsv, new Scalar(0, 35, 100), new Scalar(50, 255, 255), mask);
    using var roi = new Mat(mask, new Rect(mask.Width / 10, mask.Height / 10,
        Math.Max(1, mask.Width * 8 / 10), Math.Max(1, mask.Height * 8 / 10)));
    using var labels = new Mat();
    using var stats = new Mat();
    using var centroids = new Mat();
    var components = Cv2.ConnectedComponentsWithStats(roi, labels, stats, centroids);
    Console.WriteLine($"bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} components={components - 1}");
    for (var component = 1; component < components; component++)
    {
        var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
        var x = stats.At<int>(component, (int)ConnectedComponentsTypes.Left);
        var y = stats.At<int>(component, (int)ConnectedComponentsTypes.Top);
        var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
        var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
        var accepted = area >= 8 && width >= 3 && height >= 3 && width <= mask.Width / 2 && height <= mask.Height / 2;
        Console.WriteLine($"  area={area} rect={x},{y},{width},{height} accepted={accepted}");
    }
    return 0;
}

if (string.Equals(args[0], "--hand-purple-annotate", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --hand-purple-annotate <profile.json> <image> <output>");
        return 2;
    }

    using var assets = VisionProfileAssets.Load(Path.GetFullPath(args[1]));
    using var annotated = Cv2.ImRead(Path.GetFullPath(args[2]), ImreadModes.Color);
    var handPixels = assets.Layout.Regions.Hand.ToPixels(annotated.Width, annotated.Height);
    using var handImage = new Mat(annotated, new Rect(handPixels.X, handPixels.Y, handPixels.Width, handPixels.Height));
    using var hsv = new Mat();
    Cv2.CvtColor(handImage, hsv, ColorConversionCodes.BGR2HSV);
    using var mask = new Mat();
    Cv2.InRange(hsv, new Scalar(125, 80, 45), new Scalar(179, 255, 255), mask);
    using var labels = new Mat();
    using var stats = new Mat();
    using var centroids = new Mat();
    var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
    DrawRegion(annotated, assets.Layout.Regions.Hand, Scalar.Green, "hand");
    for (var component = 1; component < count; component++)
    {
        var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
        var x = stats.At<int>(component, (int)ConnectedComponentsTypes.Left);
        var y = stats.At<int>(component, (int)ConnectedComponentsTypes.Top);
        var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
        var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
        if (area < 200 || width < 12 || height < 12 || y > handPixels.Height * 0.72)
            continue;
        Cv2.Rectangle(annotated, new Rect(handPixels.X + x, handPixels.Y + y, width, height),
            new Scalar(255, 0, 255), 2);
    }
    Cv2.ImWrite(Path.GetFullPath(args[3]), annotated);
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
    Console.WriteLine($"{Path.GetFileName(imagePath)} {image.Width}x{image.Height} scene={result.Scene.GamePhase}({result.Scene.Confidence:P1}) gold={Format(result.Gold.Value, result.Gold.Confidence)} tier={Format(result.TavernTier.Value, result.TavernTier.Confidence)} armor={Format(result.Armor?.Value, result.Armor?.Confidence ?? 0)} hand={snapshot.Hand.Count}/{snapshot.HandCapacity} board={snapshot.Board.Count}/{snapshot.BoardCapacity} actionable={snapshot.IsActionable}");
    foreach (var card in result.Cards)
    {
        var observation = card.Observation;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {observation.CardZone,-8} slot={observation.SlotIndex} occupied={observation.IsOccupied,-5} kind={observation.Kind,-7} id={observation.CardId,-28} confidence={observation.Confidence:P1} rect={FormatBounds(observation.Bounds)}"));
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
    Console.WriteLine("  OfflineVisionProbe --recognize-screenshot <image> [--profile <profile.json>] [--catalog <catalog.db>] [--json <result.json>]");
}

static int RecognizeScreenshot(string[] arguments)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine("Usage: OfflineVisionProbe --recognize-screenshot <image> [--profile <profile.json>] [--catalog <catalog.db>] [--json <result.json>]");
        return 2;
    }

    var profilePath = Path.Combine(Directory.GetCurrentDirectory(), "data", "vision", "profile.json");
    var catalogPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "catalog", "catalog.db");
    string? jsonPath = null;
    for (var index = 2; index < arguments.Length; index++)
    {
        if (index + 1 >= arguments.Length)
        {
            Console.Error.WriteLine("识别参数缺少值。");
            return 2;
        }

        switch (arguments[index].ToLowerInvariant())
        {
            case "--profile":
                profilePath = Path.GetFullPath(arguments[++index]);
                break;
            case "--catalog":
                catalogPath = Path.GetFullPath(arguments[++index]);
                break;
            case "--json":
                jsonPath = Path.GetFullPath(arguments[++index]);
                break;
            default:
                Console.Error.WriteLine($"未知识别参数：{arguments[index]}");
                return 2;
        }
    }

    try
    {
        using var service = ScreenshotRecognitionService.Load(profilePath, catalogPath);
        var report = service.RecognizeFile(arguments[1]);
        Console.WriteLine(report.ToText());
        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            File.WriteAllText(jsonPath, report.ToJson());
            Console.WriteLine($"JSON 已保存：{jsonPath}");
        }

        return report.Quality == FrameQuality.Garbage
            && string.Equals(report.QualityReason, "decode-failed", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine($"截图识别失败：{exception.Message}");
        return 1;
    }
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
