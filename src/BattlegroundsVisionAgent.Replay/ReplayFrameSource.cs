using System.Text.Json;
using System.Text.Json.Serialization;
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;
using BattlegroundsVisionAgent.Core.Runtime;

namespace BattlegroundsVisionAgent.Replay;

public sealed record ReplayFrame(
    string Name,
    TimeSpan Timestamp,
    GameSnapshot Snapshot,
    string? ExpectedAction = null);

public sealed class ReplayFrameSource : IAutomationSnapshotSource
{
    private readonly IReadOnlyList<ReplayFrame> _frames;
    private int _nextFrame;

    public ReplayFrameSource(IReadOnlyList<ReplayFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("A replay must contain at least one frame.", nameof(frames));

        _frames = frames.ToArray();
    }

    public int Position => Volatile.Read(ref _nextFrame);

    public IReadOnlyList<ReplayFrame> Frames => _frames;

    public Task<GameSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Interlocked.Increment(ref _nextFrame) - 1;
        if (index >= _frames.Count)
            throw new InvalidOperationException("The replay ran out of frames.");

        return Task.FromResult(_frames[index].Snapshot);
    }

    public void Reset() => Interlocked.Exchange(ref _nextFrame, 0);
}

public sealed class ReplayScenario
{
    private ReplayScenario(string name, AppSettings settings, IReadOnlyList<ReplayFrame> frames,
        IReadOnlyList<string> expectedActionLabels)
    {
        Name = name;
        Settings = settings;
        Frames = frames;
        ExpectedActionLabels = expectedActionLabels;
    }

    public string Name { get; }
    public AppSettings Settings { get; }
    public IReadOnlyList<ReplayFrame> Frames { get; }
    public IReadOnlyList<string> ExpectedActionLabels { get; }

    public static ReplayScenario Load(string manifestPath)
    {
        var path = ResolvePath(manifestPath);
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ReplayManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Replay manifest is empty.");

        if (manifest.Frames.Count < 2)
            throw new InvalidDataException("Replay manifest must contain an initial frame and a post-action frame.");

        var rules = manifest.Rules.Select(ToRule).ToArray();
        var settings = AppSettings.CreateDefault() with
        {
            ReservedHandSlots = Math.Max(0, manifest.ReservedHandSlots),
            MinimumGold = Math.Max(0, manifest.MinimumGold),
            Rules = rules
        };

        var frames = manifest.Frames.Select((frame, index) =>
            new ReplayFrame(
                frame.Name ?? $"frame-{index:00}",
                TimeSpan.FromMilliseconds(Math.Max(0, frame.TimestampMs)),
                ToSnapshot(frame),
                frame.ExpectedAction)).ToArray();
        var expected = manifest.ExpectedActions.Count > 0
            ? manifest.ExpectedActions.ToArray()
            : frames.Take(frames.Length - 1)
                .Select(frame => frame.ExpectedAction)
                .Where(label => label is not null)
                .Cast<string>()
                .ToArray();

        return new ReplayScenario(manifest.Name ?? Path.GetFileNameWithoutExtension(path), settings, frames, expected);
    }

    private static CardRule ToRule(ReplayRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.CardId))
            throw new InvalidDataException("Every replay rule must have a cardId.");

        var purchaseLimit = rule.PurchaseLimit is null
            ? PurchaseLimit.Unlimited
            : PurchaseLimit.Exactly(rule.PurchaseLimit.Value);
        return new CardRule(
            rule.CardId,
            purchaseLimit,
            rule.NormalAction,
            rule.TripleAction,
            Math.Max(0, rule.DiscoverPriority),
            rule.ProtectedOnBoard);
    }

    private static GameSnapshot ToSnapshot(ReplayFrameManifest frame) => new(
        frame.LayoutVersion,
        frame.Confidence,
        DateTimeOffset.UnixEpoch.AddMilliseconds(Math.Max(0, frame.TimestampMs)),
        frame.GamePhase,
        frame.Gold,
        frame.TavernTier,
        ToCards(CardZone.Shop, frame.Shop),
        ToCards(CardZone.Hand, frame.Hand),
        ToCards(CardZone.Board, frame.Board),
        ToCards(CardZone.Discover, frame.Discover),
        Math.Max(0, frame.HandCapacity),
        Math.Max(0, frame.BoardCapacity),
        frame.HasPendingTripleReward,
        frame.HasUnknownBlockingUi);

    private static IReadOnlyList<CardObservation> ToCards(CardZone zone, IReadOnlyList<ReplayCard> cards) =>
        cards.Select(card => new CardObservation(
            card.CardId,
            zone,
            card.SlotIndex,
            card.IsGolden,
            CreateBounds(zone, card.SlotIndex),
            card.Confidence)).ToArray();

    private static NormalizedRect CreateBounds(CardZone zone, int slot)
    {
        var y = zone switch
        {
            CardZone.Shop => 0.08,
            CardZone.Discover => 0.28,
            CardZone.Board => 0.50,
            _ => 0.78
        };
        var x = Math.Clamp(0.02 + Math.Max(0, slot) * 0.075, 0.0, 0.90);
        return new NormalizedRect(x, y, 0.07, 0.10);
    }

    private static string ResolvePath(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        if (Path.IsPathRooted(manifestPath))
            candidates.Add(manifestPath);

        foreach (var start in starts)
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                candidates.Add(Path.Combine(directory.FullName, manifestPath));
            }
        }

        var resolved = candidates.FirstOrDefault(File.Exists);
        return resolved is null
            ? throw new FileNotFoundException($"Replay manifest was not found: {manifestPath}", manifestPath)
            : Path.GetFullPath(resolved);
    }

    private static CardDisposition ParseDisposition(string? value, string propertyName)
    {
        if (Enum.TryParse<CardDisposition>(value, ignoreCase: true, out var disposition))
            return disposition;

        throw new InvalidDataException($"Replay rule {propertyName} must be Keep or PlayThenSell.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class ReplayManifest
    {
        public string? Name { get; set; }
        public int ReservedHandSlots { get; set; } = 2;
        public int MinimumGold { get; set; }
        public List<ReplayRule> Rules { get; set; } = [];
        public List<string> ExpectedActions { get; set; } = [];
        public List<ReplayFrameManifest> Frames { get; set; } = [];
    }

    private sealed class ReplayRule
    {
        public string CardId { get; set; } = string.Empty;
        public int? PurchaseLimit { get; set; }
        public CardDisposition NormalAction { get; set; } = CardDisposition.Keep;
        public CardDisposition TripleAction { get; set; } = CardDisposition.Keep;
        public int DiscoverPriority { get; set; } = 99;
        public bool ProtectedOnBoard { get; set; }
    }

    private sealed class ReplayFrameManifest
    {
        public string? Name { get; set; }
        public int TimestampMs { get; set; }
        public long LayoutVersion { get; set; }
        public double Confidence { get; set; } = 0.99;
        public GamePhase GamePhase { get; set; } = GamePhase.Shopping;
        public int? Gold { get; set; }
        public int? TavernTier { get; set; } = 2;
        public List<ReplayCard> Shop { get; set; } = [];
        public List<ReplayCard> Hand { get; set; } = [];
        public List<ReplayCard> Board { get; set; } = [];
        public List<ReplayCard> Discover { get; set; } = [];
        public int HandCapacity { get; set; } = 10;
        public int BoardCapacity { get; set; } = 7;
        public bool HasPendingTripleReward { get; set; }
        public bool HasUnknownBlockingUi { get; set; }
        public string? ExpectedAction { get; set; }
    }

    private sealed class ReplayCard
    {
        public string CardId { get; set; } = string.Empty;
        public int SlotIndex { get; set; }
        public bool IsGolden { get; set; }
        public double Confidence { get; set; } = 0.99;
    }
}

public sealed record ReplayStepResult(
    AutomationAction Action,
    long BeforeLayoutVersion,
    long AfterLayoutVersion,
    bool UsedStaleLayout,
    bool Verified);

public sealed record ReplayResult(
    IReadOnlyList<AutomationAction> Actions,
    IReadOnlyList<string> ActionLabels,
    IReadOnlyList<long> LayoutVersions,
    IReadOnlyList<ReplayStepResult> Steps,
    IReadOnlyList<string> ExpectedActionLabels)
{
    public bool MatchesExpectedActions => ActionLabels.SequenceEqual(ExpectedActionLabels);
}

public static class ReplayRunner
{
    public static async Task<ReplayResult> RunAsync(ReplayScenario scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Frames.Count < 2)
            throw new ArgumentException("A replay runner needs at least two frames.", nameof(scenario));

        var source = new ReplayFrameSource(BuildCaptureSequence(scenario.Frames));
        var coordinator = new AutomationCoordinator(
            source,
            new AutomationPlanner(),
            new ActionVerifier(),
            () => scenario.Settings,
            new RunState(),
            new SuccessfulReplayExecutor(),
            options: new AutomationCoordinatorOptions { ObservationMode = false, TickInterval = TimeSpan.Zero });

        var actions = new List<AutomationAction>();
        var labels = new List<string>();
        var steps = new List<ReplayStepResult>();
        for (var index = 0; index < scenario.Frames.Count - 1; index++)
        {
            var tick = await coordinator.TickAsync(cancellationToken).ConfigureAwait(false);
            actions.Add(tick.Action);
            labels.Add(DescribeAction(tick.Action, tick.BeforeSnapshot));
            var afterVersion = tick.AfterSnapshot?.LayoutVersion ?? tick.BeforeSnapshot.LayoutVersion;
            steps.Add(new ReplayStepResult(
                tick.Action,
                tick.BeforeSnapshot.LayoutVersion,
                afterVersion,
                tick.Action.LayoutVersion != tick.BeforeSnapshot.LayoutVersion,
                tick.Verified));

            if (tick.Action is StopAction or PauseForUserAction || (tick.Sent && !tick.Verified))
                break;
        }

        return new ReplayResult(
            actions.ToArray(),
            labels.ToArray(),
            scenario.Frames.Select(frame => frame.Snapshot.LayoutVersion).ToArray(),
            steps.ToArray(),
            scenario.ExpectedActionLabels);
    }

    private static IReadOnlyList<ReplayFrame> BuildCaptureSequence(IReadOnlyList<ReplayFrame> frames)
    {
        var captures = new List<ReplayFrame>((frames.Count - 1) * 2);
        for (var index = 0; index < frames.Count - 1; index++)
        {
            captures.Add(frames[index]);
            captures.Add(frames[index + 1]);
        }

        return captures;
    }

    private static string DescribeAction(AutomationAction action, GameSnapshot snapshot) => action switch
    {
        BuyAction buy => $"Buy:{buy.CardId}",
        PlayAction play when play.CardId == "TRIPLE_REWARD" => "Play:TRIPLE_REWARD",
        PlayAction play => $"Play:{play.CardId}:{(snapshot.Hand.FirstOrDefault(card => card.SlotIndex == play.HandSlot)?.IsGolden == true ? "Golden" : "Normal")}",
        SellAction sell => $"Sell:{sell.CardId}",
        ChooseDiscoverAction choose => $"ChooseDiscover:{choose.CardId}",
        RefreshAction => "Refresh",
        PauseForUserAction pause => $"Pause:{pause.Reason}",
        StopAction stop => $"Stop:{stop.Reason}",
        NoneAction none => $"None:{none.Reason}",
        _ => action.GetType().Name
    };

    private sealed class SuccessfulReplayExecutor : IAutomationActionExecutor
    {
        public Task<AutomationExecutionResult> ExecuteAsync(
            AutomationAction action,
            GameSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AutomationExecutionResult.Succeeded);
        }
    }
}
