using System.Runtime.InteropServices;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public sealed record LayoutTemplateProfile(
    LayoutRegions Regions,
    IReadOnlyList<NormalizedRect> ShopSlots,
    IReadOnlyList<NormalizedRect> HandSlots,
    IReadOnlyList<NormalizedRect> BoardSlots,
    IReadOnlyList<NormalizedRect> DiscoverSlots,
    NormalizedRect GoldBounds,
    NormalizedRect TavernTierBounds,
    int HandCapacity,
    int BoardCapacity,
    NormalizedRect ArmorBounds = default,
    NormalizedRect GoldCoinBounds = default)
{
    public void Validate()
    {
        if (HandCapacity < 0 || BoardCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(HandCapacity));
        if (ShopSlots is null || HandSlots is null || BoardSlots is null || DiscoverSlots is null)
            throw new InvalidDataException("Layout slot collections cannot be null.");
        if (ShopSlots.Count == 0 || HandSlots.Count == 0 || BoardSlots.Count == 0)
            throw new InvalidDataException("Shop, hand and board must each define at least one slot.");
        if (ShopSlots.Count > 20 || HandSlots.Count > 20 || BoardSlots.Count > 20 || DiscoverSlots.Count > 20)
            throw new InvalidDataException("A layout cannot contain more than 20 slots per zone.");
        if (HandSlots.Count > HandCapacity || BoardSlots.Count > BoardCapacity)
            throw new InvalidDataException("Layout slot count cannot exceed the configured capacity.");

        ValidateSlots(Regions.Shop, ShopSlots, "shop");
        ValidateSlots(Regions.Hand, HandSlots, "hand");
        ValidateSlots(Regions.Board, BoardSlots, "board");
        if (DiscoverSlots.Any(slot => !IsInside(slot, new NormalizedRect(0, 0, 1, 1))))
            throw new InvalidDataException("Discover slots must remain within the frame.");
        if (GoldCoinBounds.Width > 0 && !IsInside(GoldCoinBounds, new NormalizedRect(0, 0, 1, 1)))
            throw new InvalidDataException("Gold coin bounds must remain within the frame.");
    }

    private static void ValidateSlots(NormalizedRect region, IReadOnlyList<NormalizedRect> slots, string zone)
    {
        if (slots.Any(slot => !IsInside(slot, region)))
            throw new InvalidDataException($"Every {zone} slot must remain inside its configured region.");
        for (var index = 0; index < slots.Count; index++)
        {
            for (var other = index + 1; other < slots.Count; other++)
            {
                if (Overlaps(slots[index], slots[other]))
                    throw new InvalidDataException($"{zone} slots cannot overlap.");
            }
        }
    }

    private static bool IsInside(NormalizedRect child, NormalizedRect parent) =>
        child.X >= parent.X
        && child.Y >= parent.Y
        && child.X + child.Width <= parent.X + parent.Width
        && child.Y + child.Height <= parent.Y + parent.Height;

    private static bool Overlaps(NormalizedRect left, NormalizedRect right) =>
        left.X < right.X + right.Width
        && left.X + left.Width > right.X
        && left.Y < right.Y + right.Height
        && left.Y + left.Height > right.Y;
}

public sealed class TemplateLayoutRecognizer : ILayoutRecognizer, IDisposable
{
    private static readonly string[] RequiredAnchors = ["shop", "hand", "board"];
    private readonly LayoutTemplateProfile _profile;
    private readonly LayoutLocator _layoutLocator;
    private readonly IReadOnlyDictionary<string, Mat> _anchorTemplates;
    private readonly double _minimumAnchorConfidence;
    private bool _disposed;

    public TemplateLayoutRecognizer(
        LayoutTemplateProfile profile,
        IReadOnlyDictionary<string, Mat> anchorTemplates,
        double minimumAnchorConfidence = 0.80,
        LayoutLocator? layoutLocator = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(anchorTemplates);
        profile.Validate();
        if (!double.IsFinite(minimumAnchorConfidence) || minimumAnchorConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumAnchorConfidence));
        if (RequiredAnchors.Any(name => !anchorTemplates.TryGetValue(name, out var template) || template.Empty()))
            throw new InvalidDataException("Layout anchor templates must contain non-empty shop, hand and board images.");

        _profile = profile;
        _layoutLocator = layoutLocator ?? new LayoutLocator(minimumAnchorConfidence);
        _anchorTemplates = anchorTemplates.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        _minimumAnchorConfidence = minimumAnchorConfidence;
    }

    public LayoutRecognition Recognize(Mat frame)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            return LayoutRecognition.Failed();

        using var gray = ToGray(frame);
        var matches = new List<AnchorMatch>(_anchorTemplates.Count);
        foreach (var pair in _anchorTemplates)
        {
            if (pair.Value.Width > gray.Width || pair.Value.Height > gray.Height)
                continue;
            using var result = new Mat();
            Cv2.MatchTemplate(gray, pair.Value, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maximum, out _, out _);
            matches.Add(new AnchorMatch(pair.Key, double.IsFinite(maximum) ? maximum : 0));
        }

        var grayFrame = ToGrayFrame(gray);
        var layout = _layoutLocator.Locate(grayFrame, matches, _profile.Regions);
        if (!layout.IsSuccess)
            return LayoutRecognition.Failed();

        var shopSlots = ShopSlotDetector.Detect(frame, _profile.Regions.Shop, _profile.ShopSlots);
        var slots = new List<CardSlot>(
            shopSlots.Count + _profile.HandSlots.Count + _profile.BoardSlots.Count + _profile.DiscoverSlots.Count);
        AddSlots(slots, CardZone.Shop, shopSlots);
        AddSlots(slots, CardZone.Hand, _profile.HandSlots);
        AddSlots(slots, CardZone.Board, _profile.BoardSlots);
        AddSlots(slots, CardZone.Discover, _profile.DiscoverSlots);
        var confidence = matches
            .Where(match => RequiredAnchors.Contains(match.Name, StringComparer.OrdinalIgnoreCase))
            .Select(match => match.Confidence)
            .DefaultIfEmpty(0)
            .Min();

        return LayoutRecognition.Succeeded(
            layout.Layout!.Version,
            confidence,
            slots,
            _profile.GoldBounds,
            _profile.TavernTierBounds,
            _profile.HandCapacity,
            _profile.BoardCapacity,
            hasPendingTripleReward: false,
            hasUnknownBlockingUi: confidence < _minimumAnchorConfidence,
            armorBounds: _profile.ArmorBounds,
            goldCoinBounds: _profile.GoldCoinBounds);
    }

    /// <summary>
    /// Uses the calibrated normalized coordinates while a transition, combat
    /// animation, or discover overlay temporarily hides one of the shopping anchors.
    /// The result is deliberately blocking so it can provide read-only state without
    /// allowing an action to run on an uncertain layout.
    /// </summary>
    public LayoutRecognition RecognizeUsingProfileFallback()
        => RecognizeUsingProfileFallback(frame: null);

    public LayoutRecognition RecognizeUsingProfileFallback(Mat? frame)
    {
        ThrowIfDisposed();
        var shopSlots = frame is not null && !frame.Empty()
            ? ShopSlotDetector.Detect(frame, _profile.Regions.Shop, _profile.ShopSlots)
            : _profile.ShopSlots;
        var slots = new List<CardSlot>(
            shopSlots.Count + _profile.HandSlots.Count + _profile.BoardSlots.Count + _profile.DiscoverSlots.Count);
        AddSlots(slots, CardZone.Shop, shopSlots);
        AddSlots(slots, CardZone.Hand, _profile.HandSlots);
        AddSlots(slots, CardZone.Board, _profile.BoardSlots);
        AddSlots(slots, CardZone.Discover, _profile.DiscoverSlots);
        return LayoutRecognition.Succeeded(
            layoutVersion: 0,
            confidence: 0.70,
            slots,
            _profile.GoldBounds,
            _profile.TavernTierBounds,
            _profile.HandCapacity,
            _profile.BoardCapacity,
            hasPendingTripleReward: false,
            hasUnknownBlockingUi: true,
            armorBounds: _profile.ArmorBounds,
            goldCoinBounds: _profile.GoldCoinBounds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var template in _anchorTemplates.Values)
            template.Dispose();
    }

    private static void AddSlots(List<CardSlot> target, CardZone zone, IReadOnlyList<NormalizedRect> slots)
    {
        for (var index = 0; index < slots.Count; index++)
            target.Add(new CardSlot(zone, index, slots[index]));
    }

    private static Mat ToGray(Mat frame)
    {
        var gray = new Mat();
        if (frame.Channels() == 1)
            frame.CopyTo(gray);
        else
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static GrayFrame ToGrayFrame(Mat gray)
    {
        using var contiguous = gray.Clone();
        var pixels = new byte[checked(contiguous.Width * contiguous.Height)];
        if (pixels.Length > 0)
            Marshal.Copy(contiguous.Data, pixels, 0, pixels.Length);
        return new GrayFrame(contiguous.Width, contiguous.Height, pixels);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
