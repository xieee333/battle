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
        int? armor = null)
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
    public bool IsActionable => (GamePhase is GamePhase.Shopping or GamePhase.Discover)
        && !HasUnknownBlockingUi
        && Confidence >= MinimumActionableConfidence;
    public int FreeHandSlots => Math.Max(0, HandCapacity - Hand.Count);
    public int FreeBoardSlots => Math.Max(0, BoardCapacity - Board.Count);

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());
}
