using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Rules;

public sealed class ActionVerifier
{
    public bool Verify(AutomationAction action, GameSnapshot before, GameSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (action.LayoutVersion != before.LayoutVersion || before.LayoutVersion == after.LayoutVersion)
        {
            return false;
        }

        return action switch
        {
            BuyAction buy => VerifyBuy(buy, before, after),
            PlayAction play => VerifyPlay(play, before, after),
            SellAction sell => VerifySell(sell, before, after),
            ChooseDiscoverAction discover => VerifyDiscover(discover, before, after),
            RefreshAction => ShopFingerprint(before) != ShopFingerprint(after),
            _ => false
        };
    }

    private static bool VerifyBuy(BuyAction action, GameSnapshot before, GameSnapshot after) =>
        before.Shop.Any(card => IsTarget(card, action.CardId, action.ShopSlot))
        && !after.Shop.Any(card => IsTarget(card, action.CardId, action.ShopSlot))
        && CountCards(after.Hand, action.CardId) >= CountCards(before.Hand, action.CardId) + 1;

    private static bool VerifySell(SellAction action, GameSnapshot before, GameSnapshot after) =>
        before.Board.Any(card => IsTarget(card, action.CardId, action.BoardSlot))
        && !after.Board.Any(card => IsTarget(card, action.CardId, action.BoardSlot))
        && CountCards(after.Board, action.CardId) <= CountCards(before.Board, action.CardId) - 1;

    private static bool VerifyPlay(PlayAction action, GameSnapshot before, GameSnapshot after)
    {
        var targetBefore = before.Hand.Any(card => IsTarget(card, action.CardId, action.HandSlot));
        var handCountDecreased = CountCards(after.Hand, action.CardId) < CountCards(before.Hand, action.CardId);
        var boardCountDidNotDecrease = CountCards(after.Board, action.CardId) >= CountCards(before.Board, action.CardId);
        return targetBefore && handCountDecreased && boardCountDidNotDecrease;
    }

    private static bool VerifyDiscover(ChooseDiscoverAction action, GameSnapshot before, GameSnapshot after) =>
        before.DiscoverOptions.Any(card => IsTarget(card, action.CardId, action.DiscoverSlot))
        && !after.DiscoverOptions.Any(card => card.CardId == action.CardId)
        && after.DiscoverOptions.Count < before.DiscoverOptions.Count;

    private static bool IsTarget(CardObservation card, string cardId, int slot) =>
        card.CardId == cardId && card.SlotIndex == slot;

    private static int CountCards(IReadOnlyList<CardObservation> cards, string cardId) =>
        cards.Count(card => card.CardId == cardId);

    private static string ShopFingerprint(GameSnapshot snapshot) => string.Join('|', snapshot.Shop
        .OrderBy(card => card.SlotIndex)
        .Select(card => $"{card.SlotIndex}:{card.CardId}:{card.IsGolden}"));
}
