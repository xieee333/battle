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

public sealed record DigitTemplate(int Value, ulong PerceptualHash, string Label = "");

public sealed class DigitRecognizer : IDigitRecognizer
{
    // Captured frames contain glow, antialiasing and a changing compositor behind
    // the numeral.  Label-scoped templates keep the candidate set safe, allowing
    // a small amount of visual drift without accepting a different UI field.
    private const double MinimumConfidence = 0.82;
    private readonly IReadOnlyList<DigitTemplate> _templates;

    public DigitRecognizer(IEnumerable<DigitTemplate>? templates = null) => _templates = templates?.ToArray() ?? [];

    public DigitRecognition Recognize(Mat image, NormalizedRect bounds, string label)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var candidates = _templates
            .Where(template => string.IsNullOrWhiteSpace(template.Label)
                || string.Equals(template.Label, label, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            return new DigitRecognition(null, bounds, 0);
        using var crop = Crop(image, bounds);
        var hash = PerceptualHash.Create(crop);
        var best = candidates.Select(template => new { template, confidence = 1 - System.Numerics.BitOperations.PopCount(hash ^ template.PerceptualHash) / 64d })
            .OrderByDescending(match => match.confidence).First();
        return best.confidence >= MinimumConfidence ? new DigitRecognition(best.template.Value, bounds, best.confidence) : new DigitRecognition(null, bounds, best.confidence);
    }

    private static Mat Crop(Mat image, NormalizedRect bounds) =>
        new Mat(image, new Rect((int)(bounds.X * image.Width), (int)(bounds.Y * image.Height), Math.Max(1, (int)(bounds.Width * image.Width)), Math.Max(1, (int)(bounds.Height * image.Height)))).Clone();
}
