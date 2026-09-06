using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Input;

namespace BattlegroundsVisionAgent.Input.Tests;

public sealed class ActionBindingResolverTests
{
    [Fact]
    public void Resolve_UsesConfiguredValidatedShortcut()
    {
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Sell] = "CTRL+S"
        });

        var result = resolver.Resolve(new SellAction(7, "CARD_A", 0), Snapshot());

        var keyboard = Assert.IsType<KeyboardBinding>(result.Binding);
        Assert.Equal("CTRL+S", keyboard.Shortcut.DisplayText);
        Assert.False(result.AllowMouseFallback);
    }

    [Fact]
    public void Resolve_UsesMouseWhenShortcutIsInvalid()
    {
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Buy] = "not a shortcut"
        });

        var result = resolver.Resolve(new BuyAction(7, "CARD_A", 0), Snapshot());

        Assert.IsType<MouseBinding>(result.Binding);
    }

    [Fact]
    public void Resolve_AllowsRefreshFallbackOnlyWhenExplicitlyEnabled()
    {
        var resolver = new ActionBindingResolver(new Dictionary<InputActionKind, string>
        {
            [InputActionKind.Refresh] = "F5",
            [InputActionKind.Buy] = "F6"
        }, allowRefreshMouseFallback: true);

        var refresh = resolver.Resolve(new RefreshAction(7), Snapshot());
        var buy = resolver.Resolve(new BuyAction(7, "CARD_A", 0), Snapshot());

        Assert.True(refresh.AllowMouseFallback);
        Assert.False(buy.AllowMouseFallback);
    }

    private static GameSnapshot Snapshot() => new(
        layoutVersion: 7,
        confidence: 0.99,
        capturedAt: DateTimeOffset.UnixEpoch,
        gamePhase: GamePhase.Shopping,
        gold: 10,
        tavernTier: 2,
        shop: [Card("CARD_A", CardZone.Shop, 0)],
        hand: [Card("CARD_A", CardZone.Hand, 0)],
        board: [Card("CARD_A", CardZone.Board, 0)],
        discoverOptions: [],
        handCapacity: 10,
        boardCapacity: 7,
        hasPendingTripleReward: false,
        hasUnknownBlockingUi: false);

    private static CardObservation Card(string cardId, CardZone zone, int slot) =>
        new(cardId, zone, slot, false, new NormalizedRect(0.1, 0.1, 0.1, 0.1), 0.99);
}
