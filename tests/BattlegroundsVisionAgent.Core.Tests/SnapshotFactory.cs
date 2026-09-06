using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Tests;

internal static class SnapshotFactory
{
    public static GameSnapshot Shopping(
        long layoutVersion = 1,
        int? gold = 10,
        IReadOnlyList<CardObservation>? shop = null,
        IReadOnlyList<CardObservation>? hand = null,
        IReadOnlyList<CardObservation>? board = null,
        IReadOnlyList<CardObservation>? discover = null,
        int handCapacity = 10,
        int boardCapacity = 7,
        bool hasPendingTripleReward = false,
        double confidence = 0.95,
        bool hasUnknownBlockingUi = false) =>
        new(layoutVersion, confidence, DateTimeOffset.UnixEpoch, GamePhase.Shopping, gold, 1,
            shop ?? [], hand ?? [], board ?? [], discover ?? [], handCapacity, boardCapacity,
            hasPendingTripleReward, hasUnknownBlockingUi);

    public static CardObservation Card(string cardId, CardZone zone, int slot = 0) =>
        new(cardId, zone, slot, false, new NormalizedRect(0, 0, 0.1, 0.1), 0.95);

    public static CardObservation Golden(string cardId, CardZone zone = CardZone.Hand, int slot = 0) =>
        new(cardId, zone, slot, true, new NormalizedRect(0, 0, 0.1, 0.1), 0.95);

    public static GameSnapshot FullBoardWithGoldenInHand(string cardId) =>
        Shopping(hand: [Golden(cardId)], board: Enumerable.Range(0, 7)
            .Select(slot => Card($"KEEP_{slot}", CardZone.Board, slot)).ToArray());

    public static AppSettings Settings(params CardRule[] rules) => AppSettings.CreateDefault() with
    {
        MinimumGold = 0,
        Rules = rules
    };

    public static CardRule Keep(string cardId, int discoverPriority = 1, int? purchaseLimit = null,
        bool protectedOnBoard = false) =>
        new(cardId, purchaseLimit is null ? PurchaseLimit.Unlimited : PurchaseLimit.Exactly(purchaseLimit.Value),
            CardDisposition.Keep, CardDisposition.Keep, discoverPriority, protectedOnBoard);

    public static CardRule PlayThenSell(string cardId, int discoverPriority = 1,
        bool protectedOnBoard = false) =>
        new(cardId, PurchaseLimit.Unlimited, CardDisposition.PlayThenSell,
            CardDisposition.PlayThenSell, discoverPriority, protectedOnBoard);
}
