namespace BattlegroundsVisionAgent.Core.Domain;

public enum CardDisposition
{
    Keep,
    PlayThenSell
}

public readonly record struct PurchaseLimit
{
    public PurchaseLimit(int? count)
    {
        if (count is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Count = count;
    }

    public int? Count { get; }

    public static PurchaseLimit Unlimited => new(null);

    public static PurchaseLimit Exactly(int count) => new(count);
}

public sealed record CardRule(
    string CardId,
    PurchaseLimit PurchaseLimit,
    CardDisposition NormalAction,
    CardDisposition TripleAction,
    int DiscoverPriority,
    bool ProtectedOnBoard);
