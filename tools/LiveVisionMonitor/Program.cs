using System.Globalization;
using BattlegroundsVisionAgent.Input;
using BattlegroundsVisionAgent.Vision.Capture;
using BattlegroundsVisionAgent.Vision.Recognition;
using OpenCvSharp;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: LiveVisionMonitor <profile.json> <catalog.db> [log.txt] [intervalMs]");
    return 2;
}

var profilePath = Path.GetFullPath(args[0]);
var catalogPath = Path.GetFullPath(args[1]);
var logPath = Path.GetFullPath(args.Length >= 3
    ? args[2]
    : Path.Combine(Directory.GetCurrentDirectory(), "logs", "live-vision-monitor.log"));
var frameDirectory = Path.GetFullPath(args.Length >= 5
    ? args[4]
    : Path.Combine(Path.GetDirectoryName(logPath)!, "live-frames"));
var intervalMs = args.Length >= 4 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
    ? Math.Clamp(parsed, 250, 5000)
    : 500;

Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
Directory.CreateDirectory(frameDirectory);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var pipeline = VisionRecognitionPipeline.Load(profilePath, catalogPath);
using var purchaseDetector = new PurchaseEventDetector();
var lastLine = string.Empty;
var lastPhase = string.Empty;
var lastStatus = string.Empty;

await WriteAsync("监视器启动：只读观察，不发送任何游戏输入。", force: true);
while (!cancellation.IsCancellationRequested)
{
    try
    {
        var handle = WindowsGameWindowLocator.FindHearthstoneWindow();
        if (handle == IntPtr.Zero)
        {
            await WriteAsync("等待炉石窗口");
        }
        else if (!new WindowsGameFocusProbe(handle).IsHearthstoneForeground())
        {
            await WriteAsync("炉石未在前台，跳过本次捕获");
        }
        else
        {
            using var source = new WindowsFrameSource(handle);
            using var captured = await source.CaptureAsync(cancellation.Token);
            var result = pipeline.Recognizer.Recognize(captured.Image, captured.CapturedAt);
            var purchase = purchaseDetector.Observe(captured.Image, result);
            var snapshot = result.Snapshot;
            var phase = snapshot.GamePhase.ToString();
            var status = snapshot.IsActionable ? "actionable" : "blocked";
            var knownCards = result.Cards.Count(card => card.Observation.CardId != "UNKNOWN");
            var phaseChanged = !string.Equals(phase, lastPhase, StringComparison.Ordinal);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{captured.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} phase={phase} scene={result.Scene.Confidence:P0} layout={snapshot.Confidence:P0} gold={Format(snapshot.Gold)} tier={Format(snapshot.TavernTier)} armor={Format(snapshot.Armor)} shop={snapshot.Shop.Count} hand={snapshot.Hand.Count} board={snapshot.Board.Count} discover={snapshot.DiscoverOptions.Count} cards={knownCards}/{result.Cards.Count} status={status}");
            if (phaseChanged)
            {
                var fileName = $"{captured.CapturedAt.ToLocalTime():yyyyMMdd-HHmmss-fff}-{phase}.png";
                Cv2.ImWrite(Path.Combine(frameDirectory, fileName), captured.Image);
            }
            if (phaseChanged || status != lastStatus)
                await WriteAsync(line, force: true);
            if (purchase is not null)
                await WriteAsync($"视觉事件：{purchase.ToLogLine()}", force: true);
            lastPhase = phase;
            lastStatus = status;
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        await WriteAsync($"监视异常：{exception.GetType().Name}: {exception.Message}", force: true);
    }

    try
    {
        await Task.Delay(intervalMs, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await WriteAsync("监视器停止。", force: true);
return 0;

string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

async Task WriteAsync(string message, bool force = false)
{
    if (!force && string.Equals(message, lastLine, StringComparison.Ordinal))
        return;

    lastLine = message;
    var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellation.Token);
    Console.WriteLine(line);
}
