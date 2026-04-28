using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using ModelContextProtocol.Server;

namespace BeetleMcp;

public static partial class BeetleTools
{
    [McpServerTool(Name = "count_exception_types", ReadOnly = true, Idempotent = true)]
    [Description(@"Counts distinct exception types across the (filtered) .beetle, sorted by count descending then name ascending. No result cap on the matching pass — meant to give the true type histogram for triage and for diffing two files.

Output:
  totalExceptions: N
  distinctTypes: M
  <count>\t<ExceptionType>
  ...

Filters mirror query_exceptions but the output is a histogram, not individual events.")]
    public static string CountExceptionTypes(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Restrict to these processIndex values")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Regex matched against process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Regex on exception message")] string? messageRegex = null,
        [Description("Inclusive lower bound on exception timestamp (UTC)")] DateTime? startTime = null,
        [Description("Inclusive upper bound on exception timestamp (UTC)")] DateTime? endTime = null,
        [Description("Maximum rows to return (default 500, max 5000). Histogram is truncated after sorting.")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);
        var entry = Cache.Load(path);
        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices, null, null,
            processNameRegex, null, null,
            exceptionTypeRegex, null, messageRegex, null,
            startTime, endTime, null, null);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (var r in filter.FilterExceptions(entry))
        {
            total++;
            var key = r.Exception.ExceptionType ?? "<unknown>";
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
        sb.Append("distinctTypes: ").Append(sorted.Count).AppendLine(capHit ? $" (showing top {take})" : string.Empty);
        foreach (var kvp in sorted.Take(take))
        {
            sb.Append(kvp.Value).Append('\t').AppendLine(kvp.Key);
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "count_exception_messages", ReadOnly = true, Idempotent = true)]
    [Description(@"Counts distinct exception messages across the (filtered) .beetle, sorted by count descending. Useful when many exceptions share a type but the messages tell them apart (e.g. 'Object reference not set' vs which call site). Long messages are shortened.

Output:
  totalExceptions: N
  distinctMessages: M
  <count>\t<message>
  ...")]
    public static string CountExceptionMessages(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Restrict to these processIndex values")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Regex matched against process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Regex on exception message")] string? messageRegex = null,
        [Description("Inclusive lower bound on exception timestamp (UTC)")] DateTime? startTime = null,
        [Description("Inclusive upper bound on exception timestamp (UTC)")] DateTime? endTime = null,
        [Description("Group messages by ExceptionType + message (default true). If false, group by message only.")] bool? groupByType = null,
        [Description("Maximum rows to return (default 500, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);
        bool byType = groupByType ?? true;
        var entry = Cache.Load(path);
        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices, null, null,
            processNameRegex, null, null,
            exceptionTypeRegex, null, messageRegex, null,
            startTime, endTime, null, null);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (var r in filter.FilterExceptions(entry))
        {
            total++;
            var msg = Format.Shorten(r.Exception.ExceptionMessage ?? string.Empty, 300);
            var key = byType
                ? (r.Exception.ExceptionType ?? "<unknown>") + ": " + msg
                : msg;
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
        sb.Append("distinctMessages: ").Append(sorted.Count).AppendLine(capHit ? $" (showing top {take})" : string.Empty);
        foreach (var kvp in sorted.Take(take))
        {
            sb.Append(kvp.Value).Append('\t').AppendLine(kvp.Key);
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "diff_exception_types", ReadOnly = true, Idempotent = true)]
    [Description(@"Diffs the distinct exception types between two .beetle files (or two filtered slices of the same file). Designed for the 'good build vs bad build' workflow: which exception types appear only in the bad run, and which appear in both but with different counts?

Output sections:
  ONLY IN LEFT (n)
    <leftCount>\t<ExceptionType>
  ONLY IN RIGHT (n)
    <rightCount>\t<ExceptionType>
  COMMON DELTA (n) — types in both, sorted by |rightCount-leftCount| desc
    <leftCount>\t<rightCount>\t<delta>\t<ExceptionType>

Filters apply to BOTH sides (same regex/time/process scope on each file). To diff with different filters per side, run count_exception_types on each and diff the outputs yourself.")]
    public static string DiffExceptionTypes(
        [Description("Absolute path to the LEFT .beetle (typically the 'good' / baseline)")] string leftPath,
        [Description("Absolute path to the RIGHT .beetle (typically the 'bad' / comparison)")] string rightPath,
        [Description("Regex on process image file name / file path basename, applied to BOTH sides")] string? processNameRegex = null,
        [Description("Regex on exception type, applied to BOTH sides")] string? exceptionTypeRegex = null,
        [Description("Regex on exception message, applied to BOTH sides")] string? messageRegex = null,
        [Description("Maximum rows per section (default 500, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);

        Dictionary<string, int> Histogram(string p)
        {
            var entry = Cache.Load(p);
            var filter = CompiledFilter.Build(
                null, null, null, null,
                processNameRegex, null, null,
                exceptionTypeRegex, null, messageRegex, null,
                null, null, null, null);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in filter.FilterExceptions(entry))
            {
                var key = r.Exception.ExceptionType ?? "<unknown>";
                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
            }

            return counts;
        }

        var left = Histogram(leftPath);
        var right = Histogram(rightPath);

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
    });

    [McpServerTool(Name = "diff_exception_messages", ReadOnly = true, Idempotent = true)]
    [Description(@"Like diff_exception_types, but groups by 'ExceptionType: message'. Sharper signal when two runs throw the same types but at different sites / with different arguments.")]
    public static string DiffExceptionMessages(
        [Description("Absolute path to the LEFT .beetle")] string leftPath,
        [Description("Absolute path to the RIGHT .beetle")] string rightPath,
        [Description("Regex on process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Regex on exception message")] string? messageRegex = null,
        [Description("Maximum rows per section (default 500, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);

        Dictionary<string, int> Histogram(string p)
        {
            var entry = Cache.Load(p);
            var filter = CompiledFilter.Build(
                null, null, null, null,
                processNameRegex, null, null,
                exceptionTypeRegex, null, messageRegex, null,
                null, null, null, null);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in filter.FilterExceptions(entry))
            {
                var key = (r.Exception.ExceptionType ?? "<unknown>") + ": " + Format.Shorten(r.Exception.ExceptionMessage ?? string.Empty, 300);
                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
            }

            return counts;
        }

        var left = Histogram(leftPath);
        var right = Histogram(rightPath);

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
    });
}
