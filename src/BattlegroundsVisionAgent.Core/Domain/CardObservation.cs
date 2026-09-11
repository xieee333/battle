using System.Text.Json.Serialization;

namespace BattlegroundsVisionAgent.Core.Domain;

public enum CardZone
{
    Shop,
    Hand,
    Board,
    Discover
}

public enum CardKind
{
    Unknown,
    Minion,
    Spell
}

public readonly record struct NormalizedRect
{
    [JsonConstructor]
    public NormalizedRect(double X, double Y, double Width, double Height)
    {
        var right = X + Width;
        var bottom = Y + Height;
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || !double.IsFinite(right) || !double.IsFinite(bottom)
            || X is < 0 or > 1 || Y is < 0 or > 1 || Width is < 0 or > 1 || Height is < 0 or > 1
            || right > 1 || bottom > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(X), "Normalized bounds must remain within 0..1.");
        }

        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public void Deconstruct(out double X, out double Y, out double Width, out double Height)
    {
        X = this.X;
        Y = this.Y;
        Width = this.Width;
        Height = this.Height;
    }
}

public sealed record CardObservation(
    string CardId,
    CardZone CardZone,
    int SlotIndex,
    bool IsGolden,
    NormalizedRect Bounds,
    double Confidence,
    CardKind Kind = CardKind.Unknown,
    bool IsOccupied = true);
