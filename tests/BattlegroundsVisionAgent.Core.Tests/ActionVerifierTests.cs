using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class ActionVerifierTests
{
    [Fact]
    public void Verify_RejectsActionFromOldLayout()
    {
        var before = SnapshotFactory.Shopping(layoutVersion: 2, shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 3, hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand)]);

        var result = new ActionVerifier().Verify(new BuyAction(1, "CARD_A", 0), before, after);

        Assert.False(result);
    }

    [Fact]
    public void Verify_BuyRequiresTargetToLeaveShopAndEnterHand()
    {
        var before = SnapshotFactory.Shopping(shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, shop: [], hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand)]);

        var result = new ActionVerifier().Verify(new BuyAction(1, "CARD_A", 0), before, after);

        Assert.True(result);
    }

    [Fact]
    public void Verify_BuyRejectsMissingHandResult()
    {
        var before = SnapshotFactory.Shopping(shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, shop: []);

        var result = new ActionVerifier().Verify(new BuyAction(1, "CARD_A", 0), before, after);

        Assert.False(result);
    }

    [Fact]
    public void Verify_BuyRejectsWhenHandAlreadyContainsTargetButCountDoesNotIncrease()
    {
        var before = SnapshotFactory.Shopping(
            shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)],
            hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, shop: [],
            hand: [SnapshotFactory.Card("CARD_A", CardZone.Hand)]);

        var result = new ActionVerifier().Verify(new BuyAction(1, "CARD_A", 0), before, after);

        Assert.False(result);
    }

    [Fact]
    public void Verify_SellRequiresTargetToDisappearFromBoard()
    {
        var before = SnapshotFactory.Shopping(board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 4)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, board: []);

        var result = new ActionVerifier().Verify(new SellAction(1, "CARD_A", 4), before, after);

        Assert.True(result);
    }

    [Fact]
    public void Verify_SellRejectsTargetStillOnBoard()
    {
        var before = SnapshotFactory.Shopping(board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 4)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, board: [SnapshotFactory.Card("CARD_A", CardZone.Board, 4)]);

        var result = new ActionVerifier().Verify(new SellAction(1, "CARD_A", 4), before, after);

        Assert.False(result);
    }

    [Fact]
    public void Verify_SellRejectsWhenDuplicateTargetMovesToAnotherSlotWithoutCountDecrease()
    {
        var before = SnapshotFactory.Shopping(board:
        [
            SnapshotFactory.Card("CARD_A", CardZone.Board, 4),
            SnapshotFactory.Card("CARD_A", CardZone.Board, 5)
        ]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, board:
        [
            SnapshotFactory.Card("CARD_A", CardZone.Board, 3),
            SnapshotFactory.Card("CARD_A", CardZone.Board, 5)
        ]);

        var result = new ActionVerifier().Verify(new SellAction(1, "CARD_A", 4), before, after);

        Assert.False(result);
    }

    [Fact]
    public void Verify_RefreshRequiresShopFingerprintToChange()
    {
        var before = SnapshotFactory.Shopping(shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, shop: [SnapshotFactory.Card("CARD_B", CardZone.Shop)]);

        var result = new ActionVerifier().Verify(new RefreshAction(1), before, after);

        Assert.True(result);
    }

    [Fact]
    public void Verify_RefreshRejectsUnchangedShopFingerprint()
    {
        var before = SnapshotFactory.Shopping(shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);
        var after = SnapshotFactory.Shopping(layoutVersion: 2, shop: [SnapshotFactory.Card("CARD_A", CardZone.Shop)]);

        var result = new ActionVerifier().Verify(new RefreshAction(1), before, after);

        Assert.False(result);
    }
}
