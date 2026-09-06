using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class AutomationPlannerTests
{
    [Fact]
    public void Plan_DiscoverPreemptsTripleAndShop()
    {
        var snapshot = SnapshotFactory.Shopping(
            shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)],
            hand: [SnapshotFactory.Golden("CARD_A")],
            discover: [SnapshotFactory.Card("CARD_B", CardZone.Discover)]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_A"), SnapshotFactory.Keep("CARD_B"));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        var choose = Assert.IsType<ChooseDiscoverAction>(action);
        Assert.Equal("CARD_B", choose.CardId);
    }

    [Fact]
    public void Plan_FullBoardRequestsUserBeforeTemporarySale()
    {
        var snapshot = SnapshotFactory.FullBoardWithGoldenInHand("CARD_A");

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.IsType<PauseForUserAction>(action);
    }

    [Fact]
    public void Plan_SelectsConfiguredDiscoverByAscendingPriority()
    {
        var snapshot = SnapshotFactory.Shopping(discover:
        [
            SnapshotFactory.Card("CARD_SLOW", CardZone.Discover, 0),
            SnapshotFactory.Card("CARD_FAST", CardZone.Discover, 1)
        ]);
        var settings = SnapshotFactory.Settings(
            SnapshotFactory.Keep("CARD_SLOW", discoverPriority: 9),
            SnapshotFactory.Keep("CARD_FAST", discoverPriority: 2));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new ChooseDiscoverAction(1, "CARD_FAST", 1), action);
    }

    [Fact]
    public void Plan_UsesInjectedSelectorForUnconfiguredDiscover()
    {
        var snapshot = SnapshotFactory.Shopping(discover:
        [
            SnapshotFactory.Card("CARD_A", CardZone.Discover, 0),
            SnapshotFactory.Card("CARD_B", CardZone.Discover, 1)
        ]);

        var action = new AutomationPlanner(new FixedSelector(1)).Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new ChooseDiscoverAction(1, "CARD_B", 1), action);
    }

    [Fact]
    public void Plan_StopsWhenPendingRewardIsNotObservedInHand()
    {
        var snapshot = SnapshotFactory.Shopping(hasPendingTripleReward: true);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new StopAction(1, "triple-reward-not-observed"), action);
    }

    [Fact]
    public void Plan_PlaysPendingRewardBeforeOtherGoldenCard()
    {
        var snapshot = SnapshotFactory.Shopping(hasPendingTripleReward: true, hand:
        [
            SnapshotFactory.Golden("CARD_A", slot: 0),
            SnapshotFactory.Card("TRIPLE_REWARD", CardZone.Hand, 1)
        ]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new PlayAction(1, "TRIPLE_REWARD", 1, null), action);
    }

    [Fact]
    public void Plan_PlaysGoldenCardWhenBoardHasSpace()
    {
        var snapshot = SnapshotFactory.Shopping(hand: [SnapshotFactory.Golden("CARD_A", slot: 2)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new PlayAction(1, "CARD_A", 2, null), action);
    }

    [Fact]
    public void Plan_PlaysPlayThenSellCardWhenHandSpaceNeedsRecovery()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3, hand:
        [
            SnapshotFactory.Card("CARD_A", CardZone.Hand, 0),
            SnapshotFactory.Card("CARD_B", CardZone.Hand, 1)
        ]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.PlayThenSell("CARD_A"));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new PlayAction(1, "CARD_A", 0, null), action);
    }

    [Fact]
    public void Plan_SellsPlayThenSellCardDuringSpaceRecoveryEvenWhenProtected()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3,
            hand:
            [
                SnapshotFactory.Card("CARD_B", CardZone.Hand),
                SnapshotFactory.Card("CARD_C", CardZone.Hand, 1)
            ],
            board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 4)]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.PlayThenSell("CARD_A", protectedOnBoard: true));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new SellAction(1, "CARD_A", 4), action);
    }

    [Fact]
    public void Plan_DoesNotProcessPlayThenSellWhenHandSpaceIsSufficient()
    {
        var snapshot = SnapshotFactory.Shopping(
            shop: [SnapshotFactory.Card("CARD_BUY", CardZone.Shop, 2)],
            board: [SnapshotFactory.Card("CARD_SELL", CardZone.Board, 4)]);
        var settings = SnapshotFactory.Settings(
            SnapshotFactory.PlayThenSell("CARD_SELL"),
            SnapshotFactory.Keep("CARD_BUY"));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new BuyAction(1, "CARD_BUY", 2), action);
    }

    [Fact]
    public void Plan_PausesWhenHandSpaceNeedsRecoveryButNoSafeActionExists()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3,
            hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand), SnapshotFactory.Card("CARD_B", CardZone.Hand)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new PauseForUserAction(1, "hand-space-recovery-required", null), action);
    }

    [Fact]
    public void Plan_BuysConfiguredCardOnlyWhenGoldAndReservedSlotsRemain()
    {
        var snapshot = SnapshotFactory.Shopping(gold: 5,
            shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop, 3)]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_A")) with { MinimumGold = 2 };

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new BuyAction(1, "CARD_A", 3), action);
    }

    [Fact]
    public void Plan_DoesNotBuyWhenPurchaseWouldBreakReservedHandSlots()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3,
            hand: [SnapshotFactory.Card("CARD_X", CardZone.Hand)],
            shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_A")));

        Assert.IsNotType<BuyAction>(action);
    }

    [Fact]
    public void Plan_UsesSuccessfulPurchaseContextForPurchaseLimit()
    {
        var snapshot = SnapshotFactory.Shopping(shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_A", purchaseLimit: 1));
        var context = new PlanningContext(new Dictionary<string, int> { ["CARD_A"] = 1 });

        var action = new AutomationPlanner().Plan(snapshot, settings, context);

        Assert.IsNotType<BuyAction>(action);
    }

    [Fact]
    public void Plan_RefreshesOnlyWhenNoPurchaseExistsAndGoldFloorRemains()
    {
        var snapshot = SnapshotFactory.Shopping(gold: 3, shop: [SnapshotFactory.Card("UNKNOWN", CardZone.Shop)]);
        var settings = SnapshotFactory.Settings() with { MinimumGold = 2 };

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new RefreshAction(1), action);
    }

    [Fact]
    public void Plan_WaitsWhenRefreshWouldBreakGoldFloor()
    {
        var snapshot = SnapshotFactory.Shopping(gold: 2, shop: [SnapshotFactory.Card("UNKNOWN", CardZone.Shop)]);
        var settings = SnapshotFactory.Settings() with { MinimumGold = 2 };

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new NoneAction(1, "waiting"), action);
    }

    [Fact]
    public void Plan_StopsWhenSnapshotIsNotActionable()
    {
        var snapshot = SnapshotFactory.Shopping(confidence: 0.5);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new StopAction(1, "scene-not-actionable"), action);
    }

    [Fact]
    public void Plan_StopPreemptsDiscover()
    {
        var snapshot = SnapshotFactory.Shopping(confidence: 0.5,
            discover: [SnapshotFactory.Card("CARD_A", CardZone.Discover)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_A")));

        Assert.Equal(new StopAction(1, "scene-not-actionable"), action);
    }

    [Fact]
    public void Plan_TripleRewardPreemptsSpaceRecovery()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3, hasPendingTripleReward: true,
            hand:
            [
                SnapshotFactory.Card("TRIPLE_REWARD", CardZone.Hand, 0),
                SnapshotFactory.Card("CARD_B", CardZone.Hand, 1)
            ],
            board: [SnapshotFactory.Card("CARD_SELL", CardZone.Board, 0)]);
        var settings = SnapshotFactory.Settings(SnapshotFactory.PlayThenSell("CARD_SELL"));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new PlayAction(1, "TRIPLE_REWARD", 0, null), action);
    }

    [Fact]
    public void Plan_SpaceRecoveryPreemptsPurchase()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 3,
            hand:
            [
                SnapshotFactory.Card("CARD_SELL", CardZone.Hand, 0),
                SnapshotFactory.Card("CARD_X", CardZone.Hand, 1)
            ],
            shop: [SnapshotFactory.Card("CARD_BUY", CardZone.Shop, 3)]);
        var settings = SnapshotFactory.Settings(
            SnapshotFactory.PlayThenSell("CARD_SELL"),
            SnapshotFactory.Keep("CARD_BUY"));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        Assert.Equal(new PlayAction(1, "CARD_SELL", 0, null), action);
    }

    [Fact]
    public void Plan_PurchasePreemptsRefresh()
    {
        var snapshot = SnapshotFactory.Shopping(shop:
        [
            SnapshotFactory.Card("UNKNOWN", CardZone.Shop, 0),
            SnapshotFactory.Card("CARD_BUY", CardZone.Shop, 1)
        ]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_BUY")));

        Assert.Equal(new BuyAction(1, "CARD_BUY", 1), action);
    }

    [Fact]
    public void Plan_DoesNotBuyOrRefreshWhenGoldIsUnknown()
    {
        var snapshot = SnapshotFactory.Shopping(gold: null,
            shop: [SnapshotFactory.Card("CARD_BUY", CardZone.Shop)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_BUY")));

        Assert.Equal(new NoneAction(1, "waiting"), action);
    }

    [Fact]
    public void Plan_PausesConservativelyWhenHandCountExceedsCapacity()
    {
        var snapshot = SnapshotFactory.Shopping(handCapacity: 1,
            hand:
            [
                SnapshotFactory.Card("CARD_A", CardZone.Hand, 0),
                SnapshotFactory.Card("CARD_B", CardZone.Hand, 1)
            ],
            shop: [SnapshotFactory.Card("CARD_BUY", CardZone.Shop)]);

        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings(SnapshotFactory.Keep("CARD_BUY")));

        Assert.Equal(new PauseForUserAction(1, "hand-space-recovery-required", null), action);
    }

    [Fact]
    public void Plan_DoesNotCallSelectorForEmptyDiscover()
    {
        var snapshot = SnapshotFactory.Shopping(gold: 0, discover: []);

        var action = new AutomationPlanner(new ThrowingSelector()).Plan(snapshot, SnapshotFactory.Settings());

        Assert.Equal(new NoneAction(1, "waiting"), action);
    }

    private sealed class FixedSelector(int selectedIndex) : IRandomSelector
    {
        public int SelectIndex(int optionCount) => selectedIndex;
    }

    private sealed class ThrowingSelector : IRandomSelector
    {
        public int SelectIndex(int optionCount) => throw new Xunit.Sdk.XunitException("Selector should not be called.");
    }
}
