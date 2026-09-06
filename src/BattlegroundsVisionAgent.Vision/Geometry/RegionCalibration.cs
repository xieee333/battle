using System.Text.Json;

namespace BattlegroundsVisionAgent.Vision.Geometry;

public sealed record CalibrationRect(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1.00000001 || Y + Height > 1.00000001)
            throw new InvalidDataException("校准框必须为图像内的非空区域。");
    }

    public static CalibrationRect FromDrag(double x1, double y1, double x2, double y2, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (new[] { x1, y1, x2, y2 }.Any(v => !double.IsFinite(v))) throw new InvalidDataException("坐标无效。");
        var left = Math.Clamp(Math.Min(x1, x2), 0, width);
        var top = Math.Clamp(Math.Min(y1, y2), 0, height);
        var right = Math.Clamp(Math.Max(x1, x2), 0, width);
        var bottom = Math.Clamp(Math.Max(y1, y2), 0, height);
        if (right - left < 4 || bottom - top < 4) throw new InvalidDataException("选框太小，请至少框选 4 × 4 像素。");
        return new(left / width, top / height, (right - left) / width, (bottom - top) / height);
    }
}

// Region drafts are deliberately separate from runnable recognition profiles.
public sealed record RegionCalibration(int Version, int ImageWidth, int ImageHeight, Dictionary<string, CalibrationRect> Regions)
{
    public static readonly IReadOnlyList<string> RegionNames = Array.AsReadOnly(new[] { "shop", "hand", "board", "gold", "tier", "discover" });
    public void Validate()
    {
        if (Version != 1 || ImageWidth <= 0 || ImageHeight <= 0 || ImageWidth > 16384 || ImageHeight > 16384)
            throw new InvalidDataException("不支持的校准版本或图像尺寸。");
        if (Regions is null || Regions.Any(p => !RegionNames.Contains(p.Key) || p.Value is null))
            throw new InvalidDataException("校准区域名称无效。");
        foreach (var rectangle in Regions.Values) rectangle.Validate();
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    public static RegionCalibration Parse(string json, int expectedWidth, int expectedHeight)
    {
        var draft = JsonSerializer.Deserialize<RegionCalibration>(json) ?? throw new InvalidDataException("校准文件为空。");
        draft.Validate();
        if (draft.ImageWidth != expectedWidth || draft.ImageHeight != expectedHeight)
            throw new InvalidDataException("校准文件与当前截图尺寸不同，请使用相同尺寸截图或重新校准。");
        return draft;
    }
}
