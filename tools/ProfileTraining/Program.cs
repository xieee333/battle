using System.Text.Json;
using System.Text.Json.Nodes;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: ProfileTraining <profile-dir> <shopping.png> <combat.png> <discover.png> <training-output-name>");
    return 2;
}

var profileDirectory = Path.GetFullPath(args[0]);
var profilePath = Path.Combine(profileDirectory, "profile.json");
var shoppingPath = Path.GetFullPath(args[1]);
var combatPath = Path.GetFullPath(args[2]);
var discoverPath = Path.GetFullPath(args[3]);
var outputName = args[4];
if (!File.Exists(profilePath) || !File.Exists(shoppingPath) || !File.Exists(combatPath) || !File.Exists(discoverPath))
{
    Console.Error.WriteLine("profile.json and all three training frames are required.");
    return 2;
}

var scenesDirectory = Path.Combine(profileDirectory, "scenes");
var digitsDirectory = Path.Combine(profileDirectory, "digits");
Directory.CreateDirectory(scenesDirectory);
Directory.CreateDirectory(digitsDirectory);

var profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath))?.AsObject()
    ?? throw new InvalidDataException("profile.json is invalid.");
var layout = profile["layout"]?.AsObject() ?? throw new InvalidDataException("profile.layout is missing.");

// These boxes isolate the numeral itself, rather than the surrounding game icon.
// The profile is normalized to the 1920x1080 capture size used by the current game client.
var goldBounds = new NormalizedRect(0.642, 0.894, 0.022, 0.038);
var tierBounds = new NormalizedRect(0.579, 0.114, 0.023, 0.040);
var armorBounds = new NormalizedRect(0.397, 0.114, 0.030, 0.040);
SetRect(layout, "goldBounds", goldBounds);
SetRect(layout, "tavernTierBounds", tierBounds);
SetRect(layout, "armorBounds", armorBounds);

// The hand is a fan, so the old calibration (a narrow strip) could not contain cards.
// Keep ten stable probe columns across the full hand fan; later card matching can
// discard empty columns while the whole hand remains visible to the recognizer.
var handRegion = new NormalizedRect(0.332, 0.833, 0.336, 0.167);
SetRect(layout["regions"]!.AsObject(), "hand", handRegion);
layout["handSlots"] = CreateSlots(handRegion, 10);
layout["discoverSlots"] = new JsonArray(
    RectNode(new NormalizedRect(0.218, 0.288, 0.145, 0.405)),
    RectNode(new NormalizedRect(0.427, 0.288, 0.145, 0.405)),
    RectNode(new NormalizedRect(0.635, 0.288, 0.145, 0.405)));

var scenes = profile["scenes"]?.AsArray() ?? new JsonArray();
AddScene(scenes, "Combat", "scenes/combat.png");
AddScene(scenes, "Discover", "scenes/discover.png");
profile["scenes"] = scenes;

using (var combat = Read(combatPath))
using (var discover = Read(discoverPath))
{
    WriteGray(combat, Path.Combine(scenesDirectory, "combat.png"));
    WriteGray(discover, Path.Combine(scenesDirectory, "discover.png"));
}

var digitFiles = profile["digits"]?.AsArray() ?? new JsonArray();
var samples = new (string Label, int Value, string Frame)[ ]
{
    ("gold", 0, discoverPath),
    ("gold", 4, Path.Combine(Path.GetDirectoryName(discoverPath)!, "20260907-193419-343-Shopping.png")),
    ("gold", 6, Path.Combine(Path.GetDirectoryName(discoverPath)!, "20260907-193405-455-Shopping.png")),
    ("gold", 9, shoppingPath),
    ("tavern-tier", 1, shoppingPath),
    ("armor", 5, Path.Combine(Path.GetDirectoryName(discoverPath)!, "20260907-193405-455-Shopping.png")),
    ("armor", 10, Path.Combine(Path.GetDirectoryName(discoverPath)!, "20260907-193426-148-Shopping.png")),
    ("armor", 7, shoppingPath)
};

foreach (var sample in samples)
{
    if (!File.Exists(sample.Frame))
    {
        Console.WriteLine($"跳过缺失样本：{Path.GetFileName(sample.Frame)}");
        continue;
    }

    using var image = Read(sample.Frame);
    var bounds = sample.Label switch
    {
        "gold" => goldBounds,
        "tavern-tier" => tierBounds,
        "armor" => armorBounds,
        _ => throw new InvalidOperationException(sample.Label)
    };
    var fileName = $"{sample.Label}-{sample.Value}-{Path.GetFileNameWithoutExtension(sample.Frame)}.png";
    var relative = $"digits/{fileName}";
    using var crop = Crop(image, bounds);
    using var gray = ToGray(crop);
    Cv2.ImWrite(Path.Combine(digitsDirectory, fileName), gray);
    digitFiles.Add(new JsonObject
    {
        ["label"] = sample.Label,
        ["value"] = sample.Value,
        ["imagePath"] = relative
    });
}
profile["digits"] = digitFiles;

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var temporary = profilePath + ".training.tmp";
await File.WriteAllTextAsync(temporary, profile.ToJsonString(options));
File.Move(temporary, profilePath, overwrite: true);
Console.WriteLine($"训练完成：{outputName} -> {profilePath}");
Console.WriteLine($"数字样本：{digitFiles.Count}，场景模板：{scenes.Count}");
return 0;

static Mat Read(string path)
{
    var image = Cv2.ImRead(path, ImreadModes.Color);
    if (image.Empty())
    {
        image.Dispose();
        throw new InvalidDataException($"无法读取截图：{path}");
    }
    return image;
}

static Mat Crop(Mat image, NormalizedRect bounds)
{
    var pixels = bounds.ToPixels(image.Width, image.Height);
    return new Mat(image, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height)).Clone();
}

static Mat ToGray(Mat image)
{
    if (image.Channels() == 1)
        return image.Clone();
    var gray = new Mat();
    Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
    return gray;
}

static void WriteGray(Mat image, string path)
{
    using var gray = ToGray(image);
    Cv2.ImWrite(path, gray);
}

static void SetRect(JsonObject parent, string name, NormalizedRect rect) => parent[name] = RectNode(rect);

static JsonObject RectNode(NormalizedRect rect) => new()
{
    ["x"] = rect.X,
    ["y"] = rect.Y,
    ["width"] = rect.Width,
    ["height"] = rect.Height
};

static JsonArray CreateSlots(NormalizedRect region, int count)
{
    var width = region.Width / count;
    var inset = width * 0.08;
    return new JsonArray(Enumerable.Range(0, count)
        .Select(index => RectNode(new NormalizedRect(
            region.X + index * width + inset,
            region.Y + 0.004,
            width - inset * 2,
            region.Height - 0.008)))
        .ToArray());
}

static void AddScene(JsonArray scenes, string phase, string imagePath)
{
    if (scenes.Any(node => string.Equals(node?["gamePhase"]?.GetValue<string>(), phase, StringComparison.OrdinalIgnoreCase)))
        return;
    scenes.Add(new JsonObject { ["gamePhase"] = phase, ["imagePath"] = imagePath });
}
