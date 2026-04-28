using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using ModelContextProtocol.Server;

namespace BeetleMcp;

public static partial class BeetleTools
{
    /// <summary>
    /// Histogram grouping mode. <c>Type</c> = ExceptionType; <c>TypeAndMessage</c> = "ExceptionType: message";
    /// <c>Message</c> = message only.
    /// </summary>
    private enum HistogramGrouping
    {
        Type,
        Message,
        TypeAndMessage,
    }

    private const string DefaultGroupBy = "type";

    [McpServerTool(Name = "count_exceptions", ReadOnly = true, Idempotent = true)]
    [Description(@"Counts distinct exceptions across the (filtered) .beetle, sorted by count descending then key ascending. No result cap on the matching pass — meant to give the true histogram for triage.

groupBy controls the histogram key:
  'type'             — count by ExceptionType only (default; use for triage)
  'type+message'     — count by 'ExceptionType: message' (sharper signal when many sites share a type)
  'message'          — count by message only (rarely useful)

Output:
  totalExceptions: N
  distinctKeys: M
  <count>\t<key>
  ...

Filters mirror query_exceptions but the output is a histogram, not individual events.")]
    public static string CountExceptions(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Histogram grouping: 'type' (default), 'type+message', or 'message'")] string? groupBy = null,
        [Description("Restrict to these processIndex values")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Regex matched against process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex matched against process image file name / file path basename — exclusion")] string? excludeProcessNameRegex = null,
        [Description("Regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Regex on exception type — exclusion")] string? excludeExceptionTypeRegex = null,
        [Description("Regex on exception message")] string? messageRegex = null,
        [Description("Inclusive lower bound on exception timestamp (UTC)")] DateTime? startTime = null,
        [Description("Inclusive upper bound on exception timestamp (UTC)")] DateTime? endTime = null,
        [Description("Center of a time window (UTC). Combine with windowMs.")] DateTime? aroundTime = null,
        [Description("Center of a time window expressed as offset from session start, e.g. '30m'. Mutually exclusive with aroundTime.")] string? aroundOffset = null,
        [Description("Half-window in milliseconds around aroundTime / aroundOffset. Default 5000 when an around* value is set.")] double? windowMs = null,
        [Description("Maximum rows to return (default 500, max 5000). Histogram is truncated after sorting.")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);
        var grouping = ParseGrouping(groupBy);
        var entry = Cache.Load(path);
        var resolvedAround = ResolveAroundTime(entry, aroundTime, aroundOffset);
        if (resolvedAround.HasValue && !windowMs.HasValue)
        {
            windowMs = 5000;
        }

        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices, null, null,
            processNameRegex, excludeProcessNameRegex, null,
            exceptionTypeRegex, excludeExceptionTypeRegex, messageRegex, null,
            startTime, endTime, resolvedAround, windowMs);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (var r in filter.FilterExceptions(entry))
        {
            total++;
            var key = HistogramKey(r, grouping);
            counts.TryGetValue(key, out var n);
            counts[key] = n + 1;
        }

        var sorted = counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToList();
        bool capHit = sorted.Count > take;

        var sb = new StringBuilder();
        sb.Append("totalExceptions: ").AppendLine(total.ToString());
        sb.Append("distinctKeys: ").Append(sorted.Count).AppendLine(capHit ? $" (showing top {take})" : string.Empty);
        foreach (var kvp in sorted.Take(take))
        {
            sb.Append(kvp.Value).Append('\t').AppendLine(kvp.Key);
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "diff_exceptions", ReadOnly = true, Idempotent = true)]
    [Description(@"Diffs the exception histograms between two .beetle files (or two filtered slices). Designed for the 'good build vs bad build' workflow: which keys appear only in the bad run, and which appear in both but with different counts?

groupBy controls the histogram key (same as count_exceptions):
  'type'         — diff by ExceptionType (default)
  'type+message' — diff by 'ExceptionType: message' (sharper)
  'message'      — diff by message only

Output sections:
  ONLY IN LEFT (n)
    <leftCount>\t<key>
  ONLY IN RIGHT (n)
    <rightCount>\t<key>
  COMMON DELTA (n) — keys in both, sorted by |rightCount-leftCount| desc
    <leftCount>\t<rightCount>\t<delta>\t<key>

Filters apply to BOTH sides. To diff with different filters per side, run count_exceptions on each and diff yourself.")]
    public static string DiffExceptions(
        [Description("Absolute path to the LEFT .beetle (typically the 'good' / baseline)")] string leftPath,
        [Description("Absolute path to the RIGHT .beetle (typically the 'bad' / comparison)")] string rightPath,
        [Description("Histogram grouping: 'type' (default), 'type+message', or 'message'")] string? groupBy = null,
        [Description("Regex on process image file name / file path basename, applied to BOTH sides")] string? processNameRegex = null,
        [Description("Regex on exception type, applied to BOTH sides")] string? exceptionTypeRegex = null,
        [Description("Regex on exception message, applied to BOTH sides")] string? messageRegex = null,
        [Description("Maximum rows per section (default 500, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);
        var grouping = ParseGrouping(groupBy);
        var left = BuildHistogram(leftPath, processNameRegex, exceptionTypeRegex, messageRegex, grouping);
        var right = BuildHistogram(rightPath, processNameRegex, exceptionTypeRegex, messageRegex, grouping);
        return RenderDiff(leftPath, rightPath, left, right, take);
    });

    private static HistogramGrouping ParseGrouping(string? groupBy)
    {
        return (groupBy ?? DefaultGroupBy).ToLowerInvariant() switch
        {
            "type" => HistogramGrouping.Type,
            "message" => HistogramGrouping.Message,
            "type+message" or "typeandmessage" or "type_message" => HistogramGrouping.TypeAndMessage,
            _ => throw new ModelContextProtocol.McpException(
                $"Invalid groupBy '{groupBy}'. Expected 'type', 'type+message', or 'message'.")
        };
    }

    private static string HistogramKey(ExceptionRef r, HistogramGrouping grouping)
    {
        var type = r.Exception.ExceptionType ?? "<unknown>";
        return grouping switch
        {
            HistogramGrouping.Type => type,
            HistogramGrouping.Message => Format.Shorten(r.Exception.ExceptionMessage, 300),
            HistogramGrouping.TypeAndMessage => type + ": " + Format.Shorten(r.Exception.ExceptionMessage, 300),
            _ => type
        };
    }

    private static Dictionary<string, int> BuildHistogram(
        string path,
        string? processNameRegex,
        string? exceptionTypeRegex,
        string? messageRegex,
        HistogramGrouping grouping)
    {
        var entry = Cache.Load(path);
        var filter = CompiledFilter.Build(
            null, null, null, null,
            processNameRegex, null, null,
            exceptionTypeRegex, null, messageRegex, null,
            null, null, null, null);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in filter.FilterExceptions(entry))
        {
            var key = HistogramKey(r, grouping);
            counts.TryGetValue(key, out var n);
            counts[key] = n + 1;
        }

        return counts;
    }

    private static string RenderDiff(
        string leftPath,
        string rightPath,
        Dictionary<string, int> left,
        Dictionary<string, int> right,
        int take)
    {
        var onlyLeft = left.Where(kvp => !right.ContainsKey(kvp.Key))
            .OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.Ordinal).ToList();
        var onlyRight = right.Where(kvp => !left.ContainsKey(kvp.Key))
            .OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.Ordinal).ToList();
        var common = left.Where(kvp => right.ContainsKey(kvp.Key))
            .Select(kvp => (key: kvp.Key, l: kvp.Value, r: right[kvp.Key]))
            .Where(t => t.l != t.r)
            .OrderByDescending(t => Math.Abs(t.r - t.l))
            .ThenBy(t => t.key, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("left:  ").AppendLine(leftPath);
        sb.Append("right: ").AppendLine(rightPath);
        sb.AppendLine();

        sb.Append("ONLY IN LEFT (").Append(onlyLeft.Count).AppendLine(")");
        foreach (var kvp in onlyLeft.Take(take))
        {
            sb.Append("  ").Append(kvp.Value).Append('\t').AppendLine(kvp.Key);
        }

        sb.AppendLine();
        sb.Append("ONLY IN RIGHT (").Append(onlyRight.Count).AppendLine(")");
        foreach (var kvp in onlyRight.Take(take))
        {
            sb.Append("  ").Append(kvp.Value).Append('\t').AppendLine(kvp.Key);
        }

        sb.AppendLine();
        sb.Append("COMMON DELTA (").Append(common.Count).AppendLine(")");
        foreach (var t in common.Take(take))
        {
            sb.Append("  ").Append(t.l).Append('\t').Append(t.r).Append('\t')
              .Append((t.r - t.l).ToString("+#;-#;0")).Append('\t').AppendLine(t.key);
        }

        return sb.ToString();
    }
}
