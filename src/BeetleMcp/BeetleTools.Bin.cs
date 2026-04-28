using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace BeetleMcp;

public static partial class BeetleTools
{
    [McpServerTool(Name = "bin_exceptions", ReadOnly = true, Idempotent = true)]
    [Description(@"Bins matching exceptions into time buckets. Returns one row per bucket:
  <iso-bucket-start>\t<count>

Use this to see how exception activity changes over time without dumping every event. Filters mirror query_exceptions / count_exceptions.

binSize accepts a duration like '1m', '30s', '500ms', '1h'. A bare number is interpreted as milliseconds. Default 60000 (1 minute).

Empty buckets between the first and last non-empty bucket are emitted with count 0 so the timeline reads naturally.")]
    public static string BinExceptions(
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null,
        [Description("Bucket size, e.g. '1m', '30s', '500ms'. Default '60s'.")] string? binSize = null,
        [Description("Restrict to these processIndex values")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Regex matched against process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex matched against process image file name / file path basename — exclusion")] string? excludeProcessNameRegex = null,
        [Description("Regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Regex on exception type — exclusion")] string? excludeExceptionTypeRegex = null,
        [Description("Regex on exception message")] string? messageRegex = null,
        [Description("Regex on exception message — exclusion")] string? excludeMessageRegex = null,
        [Description("Inclusive lower bound on exception timestamp (UTC)")] DateTime? startTime = null,
        [Description("Inclusive upper bound on exception timestamp (UTC)")] DateTime? endTime = null,
        [Description("Maximum bucket rows to return (default 500, max 5000). The histogram is truncated from the end if exceeded.")] int? maxResults = null) => Run(() =>
    {
        double binMs = string.IsNullOrWhiteSpace(binSize) ? 60_000 : Format.ParseRelativeMs(binSize!);
        if (binMs <= 0)
        {
            throw new ModelContextProtocol.McpException("binSize must be positive.");
        }

        int take = Math.Clamp(maxResults ?? 500, 1, MaxAllowedResults);

        var entry = ResolveBeetle(path);
        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices, null, null,
            processNameRegex, excludeProcessNameRegex, null,
            exceptionTypeRegex, excludeExceptionTypeRegex,
            messageRegex, excludeMessageRegex,
            startTime, endTime, null, null);

        var sessionStart = entry.Session.StartTime;
        var buckets = new SortedDictionary<long, int>();
        int total = 0;

        foreach (var r in filter.FilterExceptions(entry))
        {
            total++;
            long key = (long)Math.Floor((r.Exception.Timestamp - sessionStart).TotalMilliseconds / binMs);
            buckets.TryGetValue(key, out var n);
            buckets[key] = n + 1;
        }

        var sb = new StringBuilder();
        sb.Append("totalExceptions: ").AppendLine(total.ToString());
        sb.Append("binSizeMs: ").AppendLine(Format.Ms(binMs));
        sb.Append("buckets: ").Append(buckets.Count);

        if (buckets.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("(no exceptions match)");
            return sb.ToString();
        }

        long first = long.MaxValue;
        long last = long.MinValue;
        foreach (var k in buckets.Keys)
        {
            if (k < first)
            {
                first = k;
            }

            if (k > last)
            {
                last = k;
            }
        }

        long span = last - first + 1;
        bool capHit = span > take;
        long emitLast = capHit ? first + take - 1 : last;
        sb.Append(" (timeline buckets: ").Append(span);
        if (capHit)
        {
            sb.Append(", showing first ").Append(take);
        }

        sb.AppendLine(")");

        for (long k = first; k <= emitLast; k++)
        {
            buckets.TryGetValue(k, out var count);
            var bucketStart = sessionStart + TimeSpan.FromMilliseconds(k * binMs);
            sb.Append(Format.Iso(bucketStart)).Append('\t').AppendLine(count.ToString());
        }

        return sb.ToString();
    });
}
