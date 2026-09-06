using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Geometry;

public sealed record AnchorMatch(string Name, double Confidence);

public sealed record LayoutRegions(NormalizedRect Shop, NormalizedRect Hand, NormalizedRect Board);

public sealed record GameLayout(long Version, PixelRect Shop, PixelRect Hand, PixelRect Board);

public enum LayoutFailure
{
    InvalidAspectRatio,
    InsufficientAnchors,
    InvalidRegions,
    VersionExhausted
}

public sealed record LayoutResult(GameLayout? Layout, LayoutFailure? Failure)
{
    public bool IsSuccess => Layout is not null;

    public static LayoutResult Success(GameLayout layout) => new(layout, null);

    public static LayoutResult Failed(LayoutFailure failure) => new(null, failure);
}

public sealed class LayoutLocator
{
    private const double ExpectedAspectRatio = 16.0 / 9.0;
    private const double AspectRatioTolerance = 0.01;
    private static readonly string[] RequiredAnchors = ["shop", "hand", "board"];

    private readonly double _minimumAnchorConfidence;
    private long _nextVersion;

    public LayoutLocator(double minimumAnchorConfidence = 0.80)
    {
        if (!double.IsFinite(minimumAnchorConfidence) || minimumAnchorConfidence < 0 || minimumAnchorConfidence > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumAnchorConfidence));

        _minimumAnchorConfidence = minimumAnchorConfidence;
    }

    public LayoutResult Locate(GrayFrame windowImage, IReadOnlyCollection<AnchorMatch>? anchorTemplates, LayoutRegions? regions)
    {
        if (windowImage is null || !HasSupportedAspectRatio(windowImage))
            return LayoutResult.Failed(LayoutFailure.InvalidAspectRatio);
        if (!HasRequiredAnchors(anchorTemplates))
            return LayoutResult.Failed(LayoutFailure.InsufficientAnchors);
        if (regions is null)
            return LayoutResult.Failed(LayoutFailure.InvalidRegions);

        PixelRect shop;
        PixelRect hand;
        PixelRect board;
        try
        {
            shop = regions.Shop.ToPixels(windowImage.Width, windowImage.Height);
            hand = regions.Hand.ToPixels(windowImage.Width, windowImage.Height);
            board = regions.Board.ToPixels(windowImage.Width, windowImage.Height);
        }
        catch (OverflowException)
        {
            return LayoutResult.Failed(LayoutFailure.InvalidRegions);
        }
        catch (ArgumentOutOfRangeException)
        {
            return LayoutResult.Failed(LayoutFailure.InvalidRegions);
        }

        if (!AreValidRegions(windowImage, shop, hand, board))
            return LayoutResult.Failed(LayoutFailure.InvalidRegions);

        if (!TryAllocateVersion(out var version))
            return LayoutResult.Failed(LayoutFailure.VersionExhausted);

        return LayoutResult.Success(new GameLayout(version, shop, hand, board));
    }

    private bool TryAllocateVersion(out long version)
    {
        while (true)
        {
            var current = Volatile.Read(ref _nextVersion);
            if (current == long.MaxValue)
            {
                version = 0;
                return false;
            }

            var next = current + 1;
            if (Interlocked.CompareExchange(ref _nextVersion, next, current) == current)
            {
                version = next;
                return true;
            }
        }
    }

    private static bool HasSupportedAspectRatio(GrayFrame frame) =>
        Math.Abs(((double)frame.Width / frame.Height) - ExpectedAspectRatio) / ExpectedAspectRatio <= AspectRatioTolerance;

    private bool HasRequiredAnchors(IReadOnlyCollection<AnchorMatch>? anchorTemplates) =>
        anchorTemplates is not null && RequiredAnchors.All(required =>
            anchorTemplates.Any(anchor => string.Equals(anchor.Name, required, StringComparison.OrdinalIgnoreCase)
                && double.IsFinite(anchor.Confidence)
                && anchor.Confidence >= _minimumAnchorConfidence));

    private static bool AreValidRegions(GrayFrame frame, PixelRect shop, PixelRect hand, PixelRect board) =>
        shop.IsWithin(frame.Width, frame.Height)
        && hand.IsWithin(frame.Width, frame.Height)
        && board.IsWithin(frame.Width, frame.Height)
        && !shop.Overlaps(hand)
        && !shop.Overlaps(board)
        && !hand.Overlaps(board);
}
