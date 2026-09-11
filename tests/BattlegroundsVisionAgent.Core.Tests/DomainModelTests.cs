using System.Reflection;
using System.Text.Json;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class DomainModelTests
{
    public static IEnumerable<object[]> NonFiniteComponents()
    {
        yield return [double.NaN, 0d, 0d, 0d];
        yield return [0d, double.NaN, 0d, 0d];
        yield return [0d, 0d, double.NaN, 0d];
        yield return [0d, 0d, 0d, double.NaN];
        yield return [double.PositiveInfinity, 0d, 0d, 0d];
        yield return [0d, double.NegativeInfinity, 0d, 0d];
        yield return [0d, 0d, double.PositiveInfinity, 0d];
        yield return [0d, 0d, 0d, double.NegativeInfinity];
    }

    [Theory]
    [MemberData(nameof(NonFiniteComponents))]
    public void NormalizedRect_RejectsNonFiniteComponents(double x, double y, double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(x, y, width, height));
    }

    [Fact]
    public void NormalizedRect_RejectsBoundsThatExtendPastUnitSquare()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(0.8, 0, 0.3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(0, 0.8, 0, 0.3));
    }

    [Fact]
    public void NormalizedRect_SupportsContractNamedArgumentsAndDeconstruction()
    {
        var rect = new NormalizedRect(X: 0.1, Y: 0.2, Width: 0.3, Height: 0.4);
        var (x, y, width, height) = rect;

        Assert.Equal(0.1, x);
        Assert.Equal(0.2, y);
        Assert.Equal(0.3, width);
        Assert.Equal(0.4, height);
        Assert.False(typeof(NormalizedRect).GetProperty(nameof(NormalizedRect.X))!.CanWrite);
        Assert.False(typeof(NormalizedRect).GetProperty(nameof(NormalizedRect.Y))!.CanWrite);
        Assert.False(typeof(NormalizedRect).GetProperty(nameof(NormalizedRect.Width))!.CanWrite);
        Assert.False(typeof(NormalizedRect).GetProperty(nameof(NormalizedRect.Height))!.CanWrite);
    }

    [Fact]
    public void AutomationAction_BaseConstructor_IsPrivateProtected()
    {
        var constructor = typeof(AutomationAction).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(long)],
            modifiers: null);

        Assert.NotNull(constructor);
        Assert.True(constructor.IsFamilyAndAssembly);
    }

    [Fact]
    public void SnapshotAndObservation_ExposeContractPropertyNames()
    {
        Assert.NotNull(typeof(CardObservation).GetProperty("CardZone"));
        Assert.Null(typeof(CardObservation).GetProperty("Zone"));
        Assert.NotNull(typeof(GameSnapshot).GetProperty("GamePhase"));
        Assert.Null(typeof(GameSnapshot).GetProperty("Phase"));
    }

    [Fact]
    public void Snapshot_JsonRoundTrip_PreservesContractProperties()
    {
        var observation = new CardObservation("CARD_001", CardZone.Shop, 0, false,
            new NormalizedRect(0.1, 0.2, 0.3, 0.4), 0.95);
        var snapshot = new GameSnapshot(7, 0.95, DateTimeOffset.UnixEpoch, GamePhase.Shopping,
            3, 2, [observation], [], [], [], 10, 7, false, false);

        var loaded = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(snapshot));

        Assert.NotNull(loaded);
        Assert.Equal(CardZone.Shop, loaded.Shop[0].CardZone);
        Assert.Equal(GamePhase.Shopping, loaded.GamePhase);
        Assert.Equal(snapshot.Shop[0].Bounds, loaded.Shop[0].Bounds);
    }

    [Fact]
    public void Snapshot_AllowsKnownCardActionWhenAnUnidentifiedCardIsUnrelated()
    {
        var unknownBuddy = new CardObservation("UNKNOWN", CardZone.Shop, 0, false,
            new NormalizedRect(0, 0, 0.1, 0.1), 0.20, CardKind.Unknown);
        var known = new CardObservation("CARD_A", CardZone.Shop, 1, false,
            new NormalizedRect(0.1, 0, 0.1, 0.1), 0.95, CardKind.Minion);
        var snapshot = new GameSnapshot(7, 0.95, DateTimeOffset.UnixEpoch, GamePhase.Shopping,
            10, 2, [unknownBuddy, known], [], [], [], 10, 7, false, false, hasUnknownCard: true);

        Assert.True(snapshot.IsActionable);
        Assert.True(snapshot.CanPerform(new BuyAction(7, "CARD_A", 1)));
        Assert.False(snapshot.CanPerform(new BuyAction(7, "UNKNOWN", 0)));
    }

    [Fact]
    public void Snapshot_UnknownDiscoverOptionCannotBeSelected()
    {
        var unknown = new CardObservation("UNKNOWN", CardZone.Discover, 0, false,
            new NormalizedRect(0, 0, 0.1, 0.1), 0.20, CardKind.Unknown);
        var snapshot = new GameSnapshot(7, 0.95, DateTimeOffset.UnixEpoch, GamePhase.Discover,
            10, 2, [], [], [], [unknown], 10, 7, false, false, hasUnknownCard: true);

        Assert.True(snapshot.IsActionable);
        Assert.False(snapshot.CanPerform(new ChooseDiscoverAction(7, "UNKNOWN", 0)));
    }
}
