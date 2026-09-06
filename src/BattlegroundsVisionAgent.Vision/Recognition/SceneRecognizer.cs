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
        return best.confidence >= 0.92 ? new SceneRecognition(best.template.GamePhase, best.confidence) : new SceneRecognition(GamePhase.Unknown, best.confidence);
    }
}
