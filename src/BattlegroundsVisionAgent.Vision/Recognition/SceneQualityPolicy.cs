using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Recognition;

/// <summary>
/// Keeps low-quality transition frames out of the live training stream.
/// Scene templates are intentionally permissive because the game animates
/// continuously, so a phase label by itself is not enough to treat a frame as
/// a reliable shopping/discover observation.
/// </summary>
public static class SceneQualityPolicy
{
    public const double ShoppingMinimumConfidence = 0.78125;
    public const double DiscoverMinimumConfidence = 0.80;

    public static bool IsTrainingCandidate(SceneRecognition scene) =>
        scene.GamePhase switch
        {
            GamePhase.Shopping => scene.Confidence >= ShoppingMinimumConfidence,
            GamePhase.Discover => scene.Confidence >= DiscoverMinimumConfidence,
            _ => false
        };
}

/// <summary>
/// Requires the same high-quality actionable phase twice in a row before it
/// is allowed into the live stream. This prevents a single animation or
/// reconnect frame from becoming a saved phase screenshot or purchase event.
/// </summary>
public sealed class PhaseConfirmationTracker
{
    public const int RequiredConsecutiveFrames = 2;

    private GamePhase? _candidate;
    private int _candidateCount;

    public GamePhase? Observe(SceneRecognition scene)
    {
        if (!SceneQualityPolicy.IsTrainingCandidate(scene))
        {
            Reset();
            return null;
        }

        if (_candidate != scene.GamePhase)
        {
            _candidate = scene.GamePhase;
            _candidateCount = 1;
        }
        else
        {
            _candidateCount++;
        }

        return _candidateCount >= RequiredConsecutiveFrames ? _candidate : null;
    }

    public void Reset()
    {
        _candidate = null;
        _candidateCount = 0;
    }
}
