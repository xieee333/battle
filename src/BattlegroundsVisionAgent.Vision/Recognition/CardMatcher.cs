using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record CardMatch(string? CardId, bool IsGolden, double Confidence)
{
    public bool IsKnown => CardId is not null;
    public static CardMatch Unknown(double confidence = 0) => new(null, false, Math.Clamp(confidence, 0, 1));
}

public interface ICardMatcher
{
    CardMatch Match(Mat cardImage);
}

public sealed class CardMatcher : ICardMatcher
{
    private const int CandidateLimit = 5;
    private readonly ICardFeatureStore _store;
    private readonly double _minimumConfidence;

    public CardMatcher(ICardFeatureStore store, double minimumConfidence = 0.92)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0.92 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        _minimumConfidence = minimumConfidence;
    }

    public CardMatch Match(Mat cardImage)
    {
        ArgumentNullException.ThrowIfNull(cardImage);
        if (cardImage.Empty())
            return CardMatch.Unknown();
        var queryHash = PerceptualHash.Create(cardImage);
        var candidates = _store.GetAll()
            .OrderBy(feature => HammingDistance(queryHash, feature.PerceptualHash))
            .ThenBy(feature => feature.CardId, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .ToArray();
        if (candidates.Length == 0)
            return CardMatch.Unknown();

        using var orb = ORB.Create();
        using var queryDescriptor = new Mat();
        orb.DetectAndCompute(cardImage, null, out var queryPoints, queryDescriptor);
        {
            if (queryDescriptor.Empty() || queryPoints.Length < 4)
                return CardMatch.Unknown();
            var best = CardMatch.Unknown();
            foreach (var candidate in candidates)
            {
                if (candidate.DescriptorRows < 4 || candidate.DescriptorColumns == 0 || candidate.Keypoints.Length < 4)
                    continue;
                using var descriptor = candidate.ToDescriptorMat();
                if (descriptor.Empty() || descriptor.Rows < 4)
                    continue;
                var score = Verify(queryHash, queryDescriptor, queryPoints, candidate, descriptor);
                if (score > best.Confidence)
                    best = new CardMatch(candidate.CardId, candidate.IsGolden, score);
            }
            return best.Confidence >= _minimumConfidence ? best : CardMatch.Unknown(best.Confidence);
        }
    }

    private static double Verify(ulong queryHash, Mat queryDescriptor, KeyPoint[] queryPoints, CardFeature candidate, Mat candidateDescriptor)
    {
        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);
        var matches = matcher.Match(queryDescriptor, candidateDescriptor);
        var good = matches.Where(match => match.Distance <= 64).ToArray();
        if (good.Length < 4)
            return 0;
        var query = good.Select(match => queryPoints[match.QueryIdx].Pt).ToArray();
        var template = good.Select(match => candidate.Keypoints[match.TrainIdx]).ToArray();
        using var mask = new Mat();
        using var queryMat = Mat.FromArray(query);
        using var templateMat = Mat.FromArray(template);
        Cv2.FindHomography(queryMat, templateMat, HomographyMethods.Ransac, 3, mask);
        var inliers = mask.Empty() ? 0 : Cv2.CountNonZero(mask);
        var inlierRatio = (double)inliers / good.Length;
        var normalizedHamming = 1 - good.Average(match => match.Distance) / 256d;
        var hashSimilarity = 1 - HammingDistance(queryHash, candidate.PerceptualHash) / 64d;
        return Math.Clamp(0.20 * hashSimilarity + 0.40 * normalizedHamming + 0.40 * inlierRatio, 0, 1);
    }

    private static int HammingDistance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);
}
