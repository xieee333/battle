using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record VisionProfileBuildResult(
    string ProfilePath,
    int ShopSlotCount,
    int HandSlotCount,
    int BoardSlotCount,
    bool HasGoldRegion,
    bool HasTierRegion);

/// <summary>
/// Turns a shopping-stage screenshot and region draft into the profile format consumed by the runtime.
/// The generated scene and anchors are sample templates from that screenshot; later captures should be
/// validated against them before enabling real input.
/// </summary>
public static class VisionProfileBuilder
{
    private const int ShopSlotCount = 7;
    private const int HandSlotCount = 10;
    private const int BoardSlotCount = 7;
    private const int DiscoverSlotCount = 3;
    private static readonly NormalizedRect DefaultGoldCoinBounds = new(0.681, 0.895, 0.22, 0.054);

    public static VisionProfileBuildResult BuildShoppingProfile(
        Mat screenshot,
        RegionCalibration calibration,
        string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        if (screenshot.Empty())
            throw new InvalidDataException("校准截图为空。");
        if (screenshot.Width != calibration.ImageWidth || screenshot.Height != calibration.ImageHeight)
            throw new InvalidDataException("校准草稿与当前截图尺寸不一致。");

        calibration.Validate();
        var shop = Require(calibration, "shop");
        var hand = Require(calibration, "hand");
        var board = Require(calibration, "board");
        var outputDirectory = Path.GetFullPath(profileDirectory);
        Directory.CreateDirectory(outputDirectory);
        var anchorsDirectory = Path.Combine(outputDirectory, "anchors");
        var scenesDirectory = Path.Combine(outputDirectory, "scenes");
        Directory.CreateDirectory(anchorsDirectory);
        Directory.CreateDirectory(scenesDirectory);

        var regions = new LayoutRegions(ToNormalized(shop), ToNormalized(hand), ToNormalized(board));
        var shopSlots = CreateHorizontalSlots(regions.Shop, ShopSlotCount);
        var handSlots = CreateHorizontalSlots(regions.Hand, HandSlotCount);
        var boardSlots = CreateHorizontalSlots(regions.Board, BoardSlotCount);
        var discoverSlots = calibration.Regions.TryGetValue("discover", out var discover)
            ? CreateHorizontalSlots(ToNormalized(discover), DiscoverSlotCount)
            : [];
        var emptyBounds = new NormalizedRect(0, 0, 0, 0);
        var profile = new LayoutTemplateProfile(
            regions,
            shopSlots,
            handSlots,
            boardSlots,
            discoverSlots,
            calibration.Regions.TryGetValue("gold", out var gold) ? ToNormalized(gold) : emptyBounds,
            calibration.Regions.TryGetValue("tier", out var tier) ? ToNormalized(tier) : emptyBounds,
            HandCapacity: HandSlotCount,
            BoardCapacity: BoardSlotCount,
            GoldCoinBounds: calibration.Regions.ContainsKey("gold") ? DefaultGoldCoinBounds : emptyBounds);
        profile.Validate();

        foreach (var (name, region) in new[] { ("shop", regions.Shop), ("hand", regions.Hand), ("board", regions.Board) })
        {
            using var anchor = Crop(screenshot, CreateAnchorRect(region));
            using var grayAnchor = ToGray(anchor);
            Cv2.ImWrite(Path.Combine(anchorsDirectory, $"{name}.png"), grayAnchor);
        }

        using (var scene = ToGray(screenshot))
            Cv2.ImWrite(Path.Combine(scenesDirectory, "shopping.png"), scene);

        var profilePath = Path.Combine(outputDirectory, "profile.json");
        var temporaryProfilePath = Path.Combine(outputDirectory, $"profile.{Guid.NewGuid():N}.tmp");
        var document = new
        {
            layout = profile,
            anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["shop"] = "anchors/shop.png",
                ["hand"] = "anchors/hand.png",
                ["board"] = "anchors/board.png"
            },
            scenes = new[] { new { gamePhase = GamePhase.Shopping, imagePath = "scenes/shopping.png" } },
            digits = Array.Empty<object>()
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(temporaryProfilePath, JsonSerializer.Serialize(document, options));
        ReplaceProfile(temporaryProfilePath, profilePath);

        return new VisionProfileBuildResult(
            profilePath,
            shopSlots.Count,
            handSlots.Count,
            boardSlots.Count,
            calibration.Regions.ContainsKey("gold"),
            calibration.Regions.ContainsKey("tier"));
    }

    private static CalibrationRect Require(RegionCalibration calibration, string name)
    {
        if (!calibration.Regions.TryGetValue(name, out var region))
            throw new InvalidDataException($"请先标定{ToChinese(name)}区域。");
        return region;
    }

    private static string ToChinese(string name) => name switch
    {
        "shop" => "商店",
        "hand" => "手牌",
        "board" => "战场",
        _ => name
    };

    private static NormalizedRect ToNormalized(CalibrationRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static IReadOnlyList<NormalizedRect> CreateHorizontalSlots(NormalizedRect region, int count)
    {
        var cellWidth = region.Width / count;
        var insetX = Math.Min(cellWidth * 0.08, region.Width * 0.03);
        var insetY = Math.Min(region.Height * 0.05, 0.02);
        var slotWidth = cellWidth - insetX * 2;
        var slotHeight = region.Height - insetY * 2;
        if (slotWidth <= 0 || slotHeight <= 0)
            throw new InvalidDataException("区域太小，无法生成卡槽，请扩大框选区域。");

        return Enumerable.Range(0, count)
            .Select(index => new NormalizedRect(
                region.X + index * cellWidth + insetX,
                region.Y + insetY,
                slotWidth,
                slotHeight))
            .ToArray();
    }

    private static NormalizedRect CreateAnchorRect(NormalizedRect region)
    {
        var width = Math.Min(region.Width * 0.20, 0.12);
        var height = Math.Min(region.Height * 0.20, 0.12);
        return new NormalizedRect(region.X, region.Y, width, height);
    }

    private static Mat Crop(Mat image, NormalizedRect bounds)
    {
        var pixels = bounds.ToPixels(image.Width, image.Height);
        var width = Math.Max(1, Math.Min(pixels.Width, image.Width - pixels.X));
        var height = Math.Max(1, Math.Min(pixels.Height, image.Height - pixels.Y));
        return new Mat(image, new Rect(pixels.X, pixels.Y, width, height)).Clone();
    }

    private static Mat ToGray(Mat image)
    {
        if (image.Channels() == 1)
            return image.Clone();
        var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static void ReplaceProfile(string temporaryPath, string profilePath)
    {
        try
        {
            if (File.Exists(profilePath))
            {
                var backupPath = profilePath + ".bak";
                File.Replace(temporaryPath, profilePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, profilePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
