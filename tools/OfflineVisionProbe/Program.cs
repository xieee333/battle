using System.Globalization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: OfflineVisionProbe <profile.json> <catalog.db> <image1> [image2 ...] | --annotate <profile.json> <image> <output> | --crop <image> <x> <y> <width> <height> <output>");
    return 2;
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
    foreach (var slot in assets.Layout.ShopSlots)
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
            $"  {observation.CardZone,-8} slot={observation.SlotIndex} id={observation.CardId,-28} confidence={observation.Confidence:P1} rect={FormatBounds(observation.Bounds)}"));
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

return 0;
