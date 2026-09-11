namespace BattlegroundsVisionAgent.Core.Domain;

public enum GamePhase
{
    Unknown,
    Shopping,
    Combat,
    Discover
}

public sealed record GameSnapshot
{
    private const double MinimumActionableConfidence = 0.85;
    private IReadOnlyList<CardObservation> _shop;
    private IReadOnlyList<CardObservation> _hand;
    private IReadOnlyList<CardObservation> _board;
    private IReadOnlyList<CardObservation> _discoverOptions;

    public GameSnapshot(
        long layoutVersion,
        double confidence,
        DateTimeOffset capturedAt,
        GamePhase gamePhase,
        int? gold,
        int? tavernTier,
        IReadOnlyList<CardObservation> shop,
        IReadOnlyList<CardObservation> hand,
        IReadOnlyList<CardObservation> board,
        IReadOnlyList<CardObservation> discoverOptions,
        int handCapacity,
        int boardCapacity,
        bool hasPendingTripleReward,
        bool hasUnknownBlockingUi,
        int? armor = null,
        bool hasUnknownCard = false)
    {
        LayoutVersion = layoutVersion;
        Confidence = confidence;
        CapturedAt = capturedAt;
        GamePhase = gamePhase;
        Gold = gold;
        TavernTier = tavernTier;
        _shop = Freeze(shop);
        _hand = Freeze(hand);
        _board = Freeze(board);
        _discoverOptions = Freeze(discoverOptions);
        HandCapacity = handCapacity;
        BoardCapacity = boardCapacity;
        HasPendingTripleReward = hasPendingTripleReward;
        HasUnknownBlockingUi = hasUnknownBlockingUi;
        Armor = armor;
        HasUnknownCard = hasUnknownCard;
    }

    public long LayoutVersion { get; }
    public double Confidence { get; }
    public DateTimeOffset CapturedAt { get; }
    public GamePhase GamePhase { get; }
    public int? Gold { get; }
    public int? TavernTier { get; }
    public IReadOnlyList<CardObservation> Shop => _shop;
    public IReadOnlyList<CardObservation> Hand => _hand;
    public IReadOnlyList<CardObservation> Board => _board;
    public IReadOnlyList<CardObservation> DiscoverOptions => _discoverOptions;
    public int HandCapacity { get; }
    public int BoardCapacity { get; }
    public bool HasPendingTripleReward { get; }
    public bool HasUnknownBlockingUi { get; }
    public int? Armor { get; }
    public bool HasUnknownCard { get; }
    /// <summary>
    /// Indicates that the scene and its global resources are stable enough to
    /// consider an action. An unidentified card is intentionally not a global
    /// blocker: it may be an unsupported buddy while an unrelated known card
    /// is still safe to buy or sell.
    /// </summary>
    public bool IsActionable => (GamePhase is GamePhase.Shopping or GamePhase.Discover)
        && !HasUnknownBlockingUi
        && Confidence >= MinimumActionableConfidence;

    /// <summary>
    /// Applies the safety gate to the exact card involved in an action. This
    /// keeps unknown cards observable without ever guessing their identity.
    /// </summary>
    public bool CanPerform(AutomationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsActionable || action.LayoutVersion != LayoutVersion)
            return false;

        return action switch
        {
            RefreshAction => GamePhase == GamePhase.Shopping,
            BuyAction buy => IsKnownTarget(Shop, buy.CardId, buy.ShopSlot),
            SellAction sell => IsKnownTarget(Board, sell.CardId, sell.BoardSlot),
            PlayAction play => IsKnownTarget(Hand, play.CardId, play.HandSlot),
            ChooseDiscoverAction discover => GamePhase == GamePhase.Discover
                && IsKnownTarget(DiscoverOptions, discover.CardId, discover.DiscoverSlot),
            _ => false
        };
    }
    public int FreeHandSlots => Math.Max(0, HandCapacity - Hand.Count);
    public int FreeBoardSlots => Math.Max(0, BoardCapacity - Board.Count);

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static bool IsKnownTarget(IReadOnlyList<CardObservation> cards, string cardId, int slotIndex) =>
        cards.Any(card => card.SlotIndex == slotIndex
            && string.Equals(card.CardId, cardId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(card.CardId)
            && !string.Equals(card.CardId, "UNKNOWN", StringComparison.OrdinalIgnoreCase));
}
