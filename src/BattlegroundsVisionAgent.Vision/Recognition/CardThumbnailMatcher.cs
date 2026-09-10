using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

/// <summary>
/// Matches the visible portrait in a shop, hand, board, or discover slot
/// against normalized artwork features. It deliberately requires both an
/// absolute score and a margin over the runner-up; a visually similar card
/// remains UNKNOWN.
/// </summary>
public sealed class CardThumbnailMatcher : ICardMatcher, IZoneAwareCardMatcher, IDisposable
{
    private const int MinimumGoodMatches = 4;
    private readonly ICardFeatureStore _store;
    private readonly double _minimumConfidence;
    private readonly double _minimumMargin;
    private readonly object _featureSync = new();
    private PreparedFeature[]? _preparedFeatures;
    private bool _disposed;

    public CardThumbnailMatcher(
        ICardFeatureStore store,
        double minimumConfidence = 0.795,
        double minimumMargin = 0.08)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        if (!double.IsFinite(minimumMargin) || minimumMargin is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumMargin));
        _minimumConfidence = minimumConfidence;
        _minimumMargin = minimumMargin;
    }

    public CardMatch Match(Mat cardImage)
        => Match(cardImage, CardZone.Shop);

    public CardMatch Match(Mat cardImage, CardZone zone)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cardImage);
        if (cardImage.Empty())
            return CardMatch.Unknown();

        using var query = CardThumbnailPreprocessor.FromScreenSlot(cardImage);
        if (query.Empty())
            return CardMatch.Unknown();

        using var orb = ORB.Create();
        using var queryDescriptor = new Mat();
        orb.DetectAndCompute(query, null, out var queryPoints, queryDescriptor);
        if (queryDescriptor.Empty() || queryPoints.Length < MinimumGoodMatches)
            return CardMatch.Unknown();

        var preparedFeatures = GetPreparedFeatures();
        if (preparedFeatures.Length == 0)
            return CardMatch.Unknown();

        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        matcher.Add(preparedFeatures.Select(feature => feature.Descriptor).ToArray());
        matcher.Train();
        var groupedMatches = new Dictionary<int, List<DMatch>>();
        foreach (var matches in matcher.KnnMatch(queryDescriptor, k: 2))
        {
            if (matches.Length == 0 || matches[0].Distance > 64)
                continue;

            var match = matches[0];
            if (!groupedMatches.TryGetValue(match.ImgIdx, out var candidateMatches))
            {
                candidateMatches = [];
                groupedMatches[match.ImgIdx] = candidateMatches;
            }
            candidateMatches.Add(match);
        }

        var ranked = new List<(CardFeature Feature, double Score)>();
        foreach (var candidate in groupedMatches.OrderByDescending(pair => pair.Value.Count).Take(12))
        {
            if (candidate.Key < 0 || candidate.Key >= preparedFeatures.Length
                || candidate.Value.Count < MinimumGoodMatches)
                continue;

            var prepared = preparedFeatures[candidate.Key];
            var score = Verify(queryDescriptor, queryPoints, prepared.Feature, candidate.Value);
            ranked.Add((prepared.Feature, score));
        }

        if (ranked.Count == 0)
            return CardMatch.Unknown();

        var ordered = ranked.OrderByDescending(item => item.Score).ToArray();
        var best = ordered[0];
        var runnerUp = ordered.Length > 1 ? ordered[1].Score : 0;
        var margin = best.Score - runnerUp;
        if (best.Score < _minimumConfidence || margin < _minimumMargin)
            return CardMatch.Unknown(best.Score);

        return new CardMatch(best.Feature.CardId, best.Feature.IsGolden, best.Score, CardKind.Minion);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_featureSync)
        {
            if (_preparedFeatures is null)
                return;
            foreach (var prepared in _preparedFeatures)
                prepared.Descriptor.Dispose();
            _preparedFeatures = null;
        }
    }

    private PreparedFeature[] GetPreparedFeatures()
    {
        if (_preparedFeatures is not null)
            return _preparedFeatures;

        lock (_featureSync)
        {
            if (_preparedFeatures is not null)
                return _preparedFeatures;

            _preparedFeatures = _store.GetAll()
                .Where(feature => feature.DescriptorRows >= MinimumGoodMatches
                    && feature.DescriptorColumns > 0
                    && feature.Keypoints.Length >= MinimumGoodMatches)
                .Select(feature => new PreparedFeature(feature, feature.ToDescriptorMat()))
                .ToArray();
            return _preparedFeatures;
        }
    }

    private static double Verify(
        Mat queryDescriptor,
        IReadOnlyList<KeyPoint> queryPoints,
        CardFeature candidate,
        IReadOnlyList<DMatch> good)
    {
        if (good.Count < MinimumGoodMatches)
            return 0;

        var query = good.Select(match => queryPoints[match.QueryIdx].Pt).ToArray();
        var template = good.Select(match => candidate.Keypoints[match.TrainIdx]).ToArray();
        using var mask = new Mat();
        using var queryMat = Mat.FromArray(query);
        using var templateMat = Mat.FromArray(template);
        Cv2.FindHomography(queryMat, templateMat, HomographyMethods.Ransac, 3, mask);
        var inliers = mask.Empty() ? 0 : Cv2.CountNonZero(mask);
        var inlierRatio = (double)inliers / good.Count;
        var descriptorSimilarity = 1 - good.Average(match => match.Distance) / 256d;
        var matchCoverage = Math.Min(1, good.Count / 12d);
        return Math.Clamp(
            0.42 * inlierRatio + 0.38 * descriptorSimilarity + 0.20 * matchCoverage,
            0,
            1);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PreparedFeature(CardFeature Feature, Mat Descriptor);
}
