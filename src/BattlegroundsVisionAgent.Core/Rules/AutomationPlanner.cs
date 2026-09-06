using System.Collections.ObjectModel;
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Rules;

public interface IRandomSelector
{
    int SelectIndex(int optionCount);
}

public sealed record PlanningContext
{
    private readonly IReadOnlyDictionary<string, int> _successfulPurchaseCounts;

    public PlanningContext(IReadOnlyDictionary<string, int> successfulPurchaseCounts)
    {
        ArgumentNullException.ThrowIfNull(successfulPurchaseCounts);
        if (successfulPurchaseCounts.Any(pair => pair.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(successfulPurchaseCounts));
        }

        _successfulPurchaseCounts = new ReadOnlyDictionary<string, int>(
            successfulPurchaseCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public static PlanningContext Empty { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, int> SuccessfulPurchaseCounts => _successfulPurchaseCounts;
}

public sealed class AutomationPlanner
{
    private const int PurchaseCost = 3;
    private const int RefreshCost = 1;

    private readonly IRandomSelector _randomSelector;

    public AutomationPlanner(IRandomSelector? randomSelector = null) =>
        _randomSelector = randomSelector ?? new FirstOptionSelector();

    public AutomationAction Plan(GameSnapshot snapshot, AppSettings settings, PlanningContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        context ??= PlanningContext.Empty;

        if (!snapshot.IsActionable)
        {
            var reason = snapshot.GamePhase == GamePhase.Shopping
                ? "shopping-data-unknown"
                : "scene-not-actionable";
            return new StopAction(snapshot.LayoutVersion, reason);
        }

        if (snapshot.DiscoverOptions.Count > 0)
        {
            return PlanDiscover(snapshot, settings);
        }

        var tripleOrReward = PlanTripleOrReward(snapshot, settings);
        if (tripleOrReward is not null)
        {
            return tripleOrReward;
        }

        if (snapshot.FreeHandSlots < settings.ReservedHandSlots)
        {
            return PlanSpaceRecovery(snapshot, settings);
        }

        return PlanPurchase(snapshot, settings, context)
            ?? PlanRefresh(snapshot, settings)
            ?? new NoneAction(snapshot.LayoutVersion, "waiting");
    }

    private AutomationAction PlanDiscover(GameSnapshot snapshot, AppSettings settings)
    {
        var prioritized = snapshot.DiscoverOptions
            .Select(card => new { Card = card, Rule = FindRule(settings, card.CardId) })
            .Where(candidate => candidate.Rule is not null)
            .OrderBy(candidate => candidate.Rule!.DiscoverPriority)
            .ThenBy(candidate => candidate.Card.SlotIndex)
            .FirstOrDefault();

        var selected = prioritized?.Card ?? SelectDiscover(snapshot.DiscoverOptions);
        return new ChooseDiscoverAction(snapshot.LayoutVersion, selected.CardId, selected.SlotIndex);
    }

    private AutomationAction? PlanTripleOrReward(GameSnapshot snapshot, AppSettings settings)
    {
        if (snapshot.HasPendingTripleReward)
        {
            var reward = snapshot.Hand.FirstOrDefault(card => card.CardId == "TRIPLE_REWARD");
            return reward is null
                ? new StopAction(snapshot.LayoutVersion, "triple-reward-not-observed")
                : PlanHandPlay(snapshot, reward);
        }

        var golden = snapshot.Hand.FirstOrDefault(card => card.IsGolden);
        if (golden is not null)
        {
            return PlanHandPlay(snapshot, golden);
        }

        return null;
    }

    private AutomationAction PlanSpaceRecovery(GameSnapshot snapshot, AppSettings settings)
    {
        var handCandidate = snapshot.Hand.FirstOrDefault(card =>
            IsPlayThenSell(settings, card));
        if (handCandidate is not null)
        {
            return PlanHandPlay(snapshot, handCandidate);
        }

        var boardCandidate = snapshot.Board.FirstOrDefault(card =>
            IsPlayThenSell(settings, card));
        if (boardCandidate is not null)
        {
            return new SellAction(snapshot.LayoutVersion, boardCandidate.CardId, boardCandidate.SlotIndex);
        }

        return new PauseForUserAction(snapshot.LayoutVersion, "hand-space-recovery-required", null);
    }

    private AutomationAction PlanHandPlay(GameSnapshot snapshot, CardObservation card) =>
        snapshot.FreeBoardSlots > 0
            ? new PlayAction(snapshot.LayoutVersion, card.CardId, card.SlotIndex, null)
            : new PauseForUserAction(snapshot.LayoutVersion, "board-full-before-play", card.CardId);

    private AutomationAction? PlanPurchase(GameSnapshot snapshot, AppSettings settings, PlanningContext context)
    {
        if (!CanSpend(snapshot.Gold, PurchaseCost, settings.MinimumGold)
            || snapshot.FreeHandSlots - 1 < settings.ReservedHandSlots)
        {
            return null;
        }

        var candidate = snapshot.Shop.FirstOrDefault(card =>
        {
            var rule = FindRule(settings, card.CardId);
            return rule is not null && CanPurchase(rule, context);
        });

        return candidate is null
            ? null
            : new BuyAction(snapshot.LayoutVersion, candidate.CardId, candidate.SlotIndex);
    }

    private static AutomationAction? PlanRefresh(GameSnapshot snapshot, AppSettings settings) =>
        CanSpend(snapshot.Gold, RefreshCost, settings.MinimumGold)
            ? new RefreshAction(snapshot.LayoutVersion)
            : null;

    private CardObservation SelectDiscover(IReadOnlyList<CardObservation> options)
    {
        var selectedIndex = _randomSelector.SelectIndex(options.Count);
        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            throw new InvalidOperationException("Random selector returned an index outside the available discover options.");
        }

        return options[selectedIndex];
    }

    private static CardRule? FindRule(AppSettings settings, string cardId) =>
        settings.Rules.FirstOrDefault(rule => string.Equals(rule.CardId, cardId, StringComparison.Ordinal));

    private static bool IsPlayThenSell(AppSettings settings, CardObservation card)
    {
        var rule = FindRule(settings, card.CardId);
        return rule is not null
            && (card.IsGolden ? rule.TripleAction : rule.NormalAction) == CardDisposition.PlayThenSell;
    }

    private static bool CanPurchase(CardRule rule, PlanningContext context) =>
        rule.PurchaseLimit.Count is null
        || !context.SuccessfulPurchaseCounts.TryGetValue(rule.CardId, out var count)
        || count < rule.PurchaseLimit.Count.Value;

    private static bool CanSpend(int? gold, int cost, int minimumGold) =>
        gold is int available && available - cost >= minimumGold;

    private sealed class FirstOptionSelector : IRandomSelector
    {
        public int SelectIndex(int optionCount) => 0;
    }
}
