using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Input;

public enum InputActionKind
{
    Refresh,
    Buy,
    Play,
    Sell,
    ChooseDiscover
}

public sealed record BindingResolution(InputBinding? Binding, bool AllowMouseFallback)
{
    public static BindingResolution Unavailable { get; } = new(null, false);
}

public readonly record struct KeyShortcut(string DisplayText, ushort VirtualKey, ushort Modifiers)
{
    private const ushort Alt = 0x0001;
    private const ushort Control = 0x0002;
    private const ushort Shift = 0x0004;

    public static bool TryParse(string? value, out KeyShortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length != value.Split('+').Length)
        {
            return false;
        }

        ushort modifiers = 0;
        ushort? key = null;
        foreach (var rawPart in parts)
        {
            var part = rawPart.ToUpperInvariant();
            var modifier = part switch
            {
                "ALT" => Alt,
                "CTRL" or "CONTROL" => Control,
                "SHIFT" => Shift,
                _ => (ushort)0
            };

            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryGetVirtualKey(part, out var virtualKey))
            {
                return false;
            }

            key = virtualKey;
        }

        if (key is null)
        {
            return false;
        }

        var text = string.Join('+', parts.Select(static part => part.ToUpperInvariant()));
        shortcut = new KeyShortcut(text, key.Value, modifiers);
        return true;
    }

    private static bool TryGetVirtualKey(string text, out ushort virtualKey)
    {
        if (text.Length == 1 && text[0] is >= 'A' and <= 'Z')
        {
            virtualKey = text[0];
            return true;
        }

        if (text.Length == 1 && text[0] is >= '0' and <= '9')
        {
            virtualKey = text[0];
            return true;
        }

        if (text.Length is 2 or 3 && text.StartsWith('F')
            && int.TryParse(text[1..], out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            virtualKey = checked((ushort)(0x70 + functionNumber - 1));
            return true;
        }

        virtualKey = 0;
        return false;
    }
}

public sealed class ActionBindingResolver
{
    private static readonly NormalizedPoint RefreshPoint = new(0.5, 0.88);
    private static readonly NormalizedPoint BoardPoint = new(0.5, 0.67);
    private static readonly NormalizedPoint SellPoint = new(0.5, 0.92);
    private readonly IReadOnlyDictionary<InputActionKind, string> _configuredShortcuts;
    private readonly bool _allowRefreshMouseFallback;

    public ActionBindingResolver(
        IReadOnlyDictionary<InputActionKind, string>? configuredShortcuts = null,
        bool allowRefreshMouseFallback = false)
    {
        _configuredShortcuts = configuredShortcuts ?? new Dictionary<InputActionKind, string>();
        _allowRefreshMouseFallback = allowRefreshMouseFallback;
    }

    public BindingResolution Resolve(AutomationAction action, GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!TryGetActionKind(action, out var kind))
        {
            return BindingResolution.Unavailable;
        }

        if (_configuredShortcuts.TryGetValue(kind, out var configuredShortcut)
            && KeyShortcut.TryParse(configuredShortcut, out var shortcut))
        {
            return new BindingResolution(new KeyboardBinding(shortcut), kind == InputActionKind.Refresh && _allowRefreshMouseFallback);
        }

        var mouse = CreateMouseBinding(action, snapshot);
        return new BindingResolution(mouse, false);
    }

    public static bool TryGetActionKind(AutomationAction action, out InputActionKind kind)
    {
        kind = action switch
        {
            RefreshAction => InputActionKind.Refresh,
            BuyAction => InputActionKind.Buy,
            PlayAction => InputActionKind.Play,
            SellAction => InputActionKind.Sell,
            ChooseDiscoverAction => InputActionKind.ChooseDiscover,
            _ => default
        };

        return action is RefreshAction or BuyAction or PlayAction or SellAction or ChooseDiscoverAction;
    }

    private static MouseBinding? CreateMouseBinding(AutomationAction action, GameSnapshot snapshot) => action switch
    {
        RefreshAction => new MouseBinding(action, RefreshPoint),
        BuyAction buy => CreateCardBinding(action, snapshot.Shop, buy.CardId, buy.ShopSlot),
        PlayAction play => CreateCardBinding(action, snapshot.Hand, play.CardId, play.HandSlot, BoardPoint),
        SellAction sell => CreateCardBinding(action, snapshot.Board, sell.CardId, sell.BoardSlot, SellPoint),
        ChooseDiscoverAction discover => CreateCardBinding(action, snapshot.DiscoverOptions, discover.CardId, discover.DiscoverSlot),
        _ => null
    };

    private static MouseBinding? CreateCardBinding(
        AutomationAction action,
        IReadOnlyList<CardObservation> cards,
        string cardId,
        int slot,
        NormalizedPoint? destination = null)
    {
        var card = cards.FirstOrDefault(card => card.CardId == cardId && card.SlotIndex == slot);
        return card is null
            ? null
            : new MouseBinding(action, new NormalizedPoint(
                card.Bounds.X + (card.Bounds.Width / 2),
                card.Bounds.Y + (card.Bounds.Height / 2)), destination);
    }
}
