using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Configuration;

public sealed record AppSettings
{
    private IReadOnlyList<CardRule> _rules = Array.AsReadOnly(Array.Empty<CardRule>());

    public int ReservedHandSlots { get; init; }
    public int MinimumGold { get; init; }
    public string PauseHotkey { get; init; } = string.Empty;
    public string EmergencyStopHotkey { get; init; } = string.Empty;
    public bool ObservationMode { get; init; }
    public IReadOnlyList<CardRule> Rules
    {
        get => _rules;
        init => _rules = Array.AsReadOnly(value.ToArray());
    }

    public static AppSettings CreateDefault() => new()
    {
        ReservedHandSlots = 2,
        PauseHotkey = "F7",
        EmergencyStopHotkey = "F8",
        ObservationMode = true,
        Rules = Array.Empty<CardRule>()
    };

    public bool Equals(AppSettings? other) => other is not null
        && ReservedHandSlots == other.ReservedHandSlots
        && MinimumGold == other.MinimumGold
        && PauseHotkey == other.PauseHotkey
        && EmergencyStopHotkey == other.EmergencyStopHotkey
        && ObservationMode == other.ObservationMode
        && Rules.SequenceEqual(other.Rules);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReservedHandSlots);
        hash.Add(MinimumGold);
        hash.Add(PauseHotkey);
        hash.Add(EmergencyStopHotkey);
        hash.Add(ObservationMode);
        foreach (var rule in Rules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }
}
