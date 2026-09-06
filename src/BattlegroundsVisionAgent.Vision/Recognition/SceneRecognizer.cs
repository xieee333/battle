using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record SceneRecognition(GamePhase GamePhase, double Confidence);

public interface ISceneRecognizer
{
    SceneRecognition Recognize(Mat frame);
}

public sealed record SceneTemplate(GamePhase GamePhase, ulong PerceptualHash);

public sealed class SceneRecognizer : ISceneRecognizer
{
    // A live Hearthstone scene changes continuously: the timer, card art,
    // glow effects and board animations can all change while the phase stays
    // the same.  A 0.92 full-frame hash threshold rejected those legitimate
    // shopping frames.  Keep the threshold conservative enough to reject a
    // substantially different screen while tolerating normal compositor drift.
    public const double MinimumConfidence = 0.74;

    private readonly IReadOnlyList<SceneTemplate> _templates;
    public SceneRecognizer(IEnumerable<SceneTemplate>? templates = null) => _templates = templates?.ToArray() ?? [];

    public SceneRecognition Recognize(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_templates.Count == 0)
            return new SceneRecognition(GamePhase.Unknown, 0);
        var hash = PerceptualHash.Create(frame);
        var best = _templates.Select(template => new { template, confidence = 1 - System.Numerics.BitOperations.PopCount(hash ^ template.PerceptualHash) / 64d })
            .OrderByDescending(match => match.confidence).First();
        return best.confidence >= MinimumConfidence
            ? new SceneRecognition(best.template.GamePhase, best.confidence)
            : new SceneRecognition(GamePhase.Unknown, best.confidence);
    }
}
