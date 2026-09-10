using System.Globalization;
using System.Text;

namespace BattlegroundsVisionAgent.Vision.Recognition;

public static class LogCurationReportFormatter
{
    private static readonly string[] SceneOrder = ["Shopping", "Discover", "Combat", "Unknown"];
    private static readonly FrameQuality[] QualityOrder =
        [FrameQuality.Good, FrameQuality.Review, FrameQuality.Garbage];

    public static string Format(LogCurationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();

        var builder = new StringBuilder();
        builder.AppendLine($"源提交: {manifest.SourceCommit}");
        builder.AppendLine($"样本总数: {manifest.Samples.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("场景统计:");
        foreach (var scene in SceneOrder)
            builder.AppendLine($"  {scene}: {manifest.Samples.Count(sample => sample.ExpectedScene == scene)}");

        builder.AppendLine("质量统计:");
        foreach (var quality in QualityOrder)
            builder.AppendLine($"  {quality}: {manifest.Samples.Count(sample => sample.Quality == quality)}");

        builder.AppendLine("垃圾原因:");
        foreach (var reason in manifest.Samples
                     .Where(sample => sample.Quality == FrameQuality.Garbage)
                     .GroupBy(sample => sample.Reason, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
            builder.AppendLine($"  {reason.Key}: {reason.Count()}");

        builder.AppendLine($"待复核: {manifest.Samples.Count(sample => sample.Quality == FrameQuality.Review)}");
        builder.AppendLine("样本:");
        foreach (var sample in manifest.Samples.OrderBy(sample => sample.Path, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(sample.Path)
                .Append(" scene=").Append(sample.ExpectedScene)
                .Append(" quality=").Append(sample.Quality)
                .Append(" include=").Append(sample.Include ? "true" : "false")
                .Append(" reason=").AppendLine(sample.Reason);
        }

        return builder.ToString().TrimEnd();
    }
}
