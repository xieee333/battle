namespace BattlegroundsVisionAgent.Core.Domain;

public abstract record AutomationAction
{
    private protected AutomationAction(long layoutVersion) => LayoutVersion = layoutVersion;

    public long LayoutVersion { get; }
}

public sealed record NoneAction(long LayoutVersion, string Reason) : AutomationAction(LayoutVersion);
public sealed record RefreshAction(long LayoutVersion) : AutomationAction(LayoutVersion);
public sealed record BuyAction(long LayoutVersion, string CardId, int ShopSlot) : AutomationAction(LayoutVersion);
public sealed record PlayAction(long LayoutVersion, string CardId, int HandSlot, int? BoardSlot) : AutomationAction(LayoutVersion);
public sealed record SellAction(long LayoutVersion, string CardId, int BoardSlot) : AutomationAction(LayoutVersion);
public sealed record ChooseDiscoverAction(long LayoutVersion, string CardId, int DiscoverSlot) : AutomationAction(LayoutVersion);
public sealed record PauseForUserAction(long LayoutVersion, string Reason, string? CardId) : AutomationAction(LayoutVersion);
public sealed record StopAction(long LayoutVersion, string Reason) : AutomationAction(LayoutVersion);
