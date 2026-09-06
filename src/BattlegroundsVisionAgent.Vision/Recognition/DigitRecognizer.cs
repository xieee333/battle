using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record DigitRecognition(int? Value, NormalizedRect Bounds, double Confidence)
{
    public bool IsKnown => Value.HasValue;
}

public interface IDigitRecognizer
{
    DigitRecognition Recognize(Mat image, NormalizedRect bounds, string label);
}

public sealed record DigitTemplate(int Value, ulong PerceptualHash);

public sealed class DigitRecognizer : IDigitRecognizer
{
    private readonly IReadOnlyList<DigitTemplate> _templates;

    public DigitRecognizer(IEnumerable<DigitTemplate>? templates = null) => _templates = templates?.ToArray() ?? [];

    public DigitRecognition Recognize(Mat image, NormalizedRect bounds, string label)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (_templates.Count == 0)
            return new DigitRecognition(null, bounds, 0);
        using var crop = Crop(image, bounds);
        var hash = PerceptualHash.Create(crop);
        var best = _templates.Select(template => new { template, confidence = 1 - System.Numerics.BitOperations.PopCount(hash ^ template.PerceptualHash) / 64d })
            .OrderByDescending(match => match.confidence).First();
        return best.confidence >= 0.92 ? new DigitRecognition(best.template.Value, bounds, best.confidence) : new DigitRecognition(null, bounds, best.confidence);
    }

    private static Mat Crop(Mat image, NormalizedRect bounds) =>
        new Mat(image, new Rect((int)(bounds.X * image.Width), (int)(bounds.Y * image.Height), Math.Max(1, (int)(bounds.Width * image.Width)), Math.Max(1, (int)(bounds.Height * image.Height)))).Clone();
}
