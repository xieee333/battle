using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Recognition;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class SceneQualityPolicyTests
{
    [Fact]
    public void ShoppingAtThreshold_IsCandidate()
    {
        Assert.True(SceneQualityPolicy.IsTrainingCandidate(
            new SceneRecognition(GamePhase.Shopping, SceneQualityPolicy.ShoppingMinimumConfidence)));
    }

    [Fact]
    public void LowConfidenceDiscover_IsRejected()
    {
        Assert.False(SceneQualityPolicy.IsTrainingCandidate(new SceneRecognition(GamePhase.Discover, 0.719)));
    }

    [Fact]
    public void CombatAndUnknown_AreNotTrainingCandidates()
    {
        Assert.False(SceneQualityPolicy.IsTrainingCandidate(new SceneRecognition(GamePhase.Combat, 0.99)));
        Assert.False(SceneQualityPolicy.IsTrainingCandidate(new SceneRecognition(GamePhase.Unknown, 0.99)));
    }

    [Fact]
    public void PhaseMustBeSeenTwiceBeforeConfirmation()
    {
        var tracker = new PhaseConfirmationTracker();
        var scene = new SceneRecognition(GamePhase.Shopping, 0.90);

        Assert.Null(tracker.Observe(scene));
        Assert.Equal(GamePhase.Shopping, tracker.Observe(scene));
    }

    [Fact]
    public void InvalidFrameResetsPendingCandidate()
    {
        var tracker = new PhaseConfirmationTracker();

        Assert.Null(tracker.Observe(new SceneRecognition(GamePhase.Shopping, 0.90)));
        Assert.Null(tracker.Observe(new SceneRecognition(GamePhase.Discover, 0.71)));
        Assert.Null(tracker.Observe(new SceneRecognition(GamePhase.Shopping, 0.90)));
    }
}
