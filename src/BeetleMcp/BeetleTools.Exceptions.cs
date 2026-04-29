using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using GuiLabs.Dotnet.Recorder;
using ModelContextProtocol.Server;

namespace BeetleMcp;

public static partial class BeetleTools
{
    [McpServerTool(Name = "query_exceptions", ReadOnly = true, Idempotent = true)]
    [Description(@"Queries managed exceptions across all processes in a .beetle, with rich filters and paging. Default output is one line per exception:
  <iso-timestamp>  <processName> pid=N  ExceptionType: message [pi/ei]

Use [pi/ei] with get_exception / get_stack_trace.

Filters are AND-combined. All regexes are case-insensitive.

Time window options:
  startTime / endTime  — absolute ISO-8601 UTC bounds
  aroundTime + windowMs — center +/- windowMs (great for correlating with timestamps from a test log)
  aroundOffset — offset from session start, e.g. '30m', '+1800s', '5400000ms'. Resolves to an aroundTime; combine with windowMs.
If both absolute and around forms are supplied the intersection is used.

fields controls projection (compact output for large dumps):
  default = full line with all fields. Pass a comma-separated subset of
  'timestamp,process,type,message,id' to keep only those columns. Aliases: 'ts','time','proc','msg'. 'all' / 'full' = default.

sortBy controls result order:
    'process' (default) streams by processIndex, then exceptionIndex within each process.
    'time' sorts matching exceptions chronologically across processes.

If the result count exceeds the cap, the header includes 'nextSkip=K' so a follow-up call can pass skip=K to continue. A '+' after matched means the streaming process-order query stopped after proving there are more results; use count_exceptions for a cap-free total.")]
    public static string QueryExceptions(
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null,
        [Description("Restrict to these processIndex values (canonical handle; PIDs are reused so prefer this)")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Restrict to these PIDs (note: not unique within a session)")] int[]? processIds = null,
        [Description("Exclude these PIDs")] int[]? excludeProcessIds = null,
        [Description("Regex matched against process image file name / file path basename")] string? processNameRegex = null,
        [Description("Regex matched against process image file name / file path basename — exclusion")] string? excludeProcessNameRegex = null,
        [Description("Regex matched against the command line")] string? commandLineRegex = null,
        [Description("Regex matched against the .NET ExceptionType (e.g. 'TaskCanceledException')")] string? exceptionTypeRegex = null,
        [Description(@"Regex against the .NET ExceptionType — exclusion (e.g. '^System\.OperationCanceledException$')")] string? excludeExceptionTypeRegex = null,
        [Description("Regex matched against the exception message")] string? messageRegex = null,
        [Description("Regex against the exception message — exclusion")] string? excludeMessageRegex = null,
        [Description("Inclusive lower bound on exception timestamp (UTC, ISO 8601)")] DateTime? startTime = null,
        [Description("Inclusive upper bound on exception timestamp (UTC, ISO 8601)")] DateTime? endTime = null,
        [Description("Center of a time window (UTC, ISO 8601). Combine with windowMs.")] DateTime? aroundTime = null,
        [Description("Center of a time window expressed as offset from session start, e.g. '30m', '+1800s', '5400000ms'. Combine with windowMs. Mutually exclusive with aroundTime.")] string? aroundOffset = null,
        [Description("Half-window in milliseconds around aroundTime / aroundOffset. Default 5000 (10s window).")] double? windowMs = null,
        [Description("Comma-separated subset of 'timestamp,process,type,message,id' (aliases: ts, time, proc, msg). Default: all.")] string? fields = null,
        [Description("Include each result's full stack trace inline (expensive). Default false.")] bool? includeStackTrace = null,
        [Description("Sort order: 'process' (default; processIndex then exceptionIndex) or 'time' (chronological across processes).")] string? sortBy = null,
        [Description("Number of leading results to skip (default 0)")] int? skip = null,
        [Description("Maximum number of results to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        bool withStack = includeStackTrace ?? false;
        var projected = Format.ParseExceptionFields(fields);
        string order = (sortBy ?? "process").ToLowerInvariant();
        if (order is not ("process" or "time"))
        {
            throw new ModelContextProtocol.McpException(
                $"Unknown sortBy '{sortBy}'. Expected one of: process, time.");
        }

        var entry = ResolveBeetle(path);
        var resolvedAround = ResolveAroundTime(entry, aroundTime, aroundOffset);
        if (resolvedAround.HasValue && !windowMs.HasValue)
        {
            windowMs = 5000;
        }

        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices,
            processIds, excludeProcessIds,
            processNameRegex, excludeProcessNameRegex,
            commandLineRegex,
            exceptionTypeRegex, excludeExceptionTypeRegex,
            messageRegex, excludeMessageRegex,
            startTime, endTime, resolvedAround, windowMs);

        int matched = 0;
        var page = new List<ExceptionRef>(take);
        bool hasMore = false;
        bool matchedIsLowerBound = false;

        if (order == "time")
        {
            var ordered = filter.FilterExceptions(entry)
                .OrderBy(r => r.Exception.Timestamp)
                .ThenBy(r => r.ProcessIndex)
                .ThenBy(r => r.ExceptionIndex)
                .ToList();
            matched = ordered.Count;
            page.AddRange(ordered.Skip(offset).Take(take));
            hasMore = offset + page.Count < matched;
        }
        else
        {
            // Stream the filter, skipping `offset`, collecting up to `take` into the page.
            // We peek one extra item after the page is full to detect overflow ("matched=N+").
            foreach (var r in filter.FilterExceptions(entry))
            {
                matched++;
                if (matched <= offset)
                {
                    continue;
                }

                if (page.Count < take)
                {
                    page.Add(r);
                }
                else
                {
                    hasMore = true;
                    matchedIsLowerBound = true;
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("exceptions: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(matched);
        if (matchedIsLowerBound)
        {
            sb.Append('+');
        }

        if (hasMore)
        {
            sb.Append(", nextSkip=").Append(offset + page.Count);
        }

        sb.AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(no exceptions match)");
            return sb.ToString();
        }

        foreach (var r in page)
        {
            sb.AppendLine(Format.ExceptionLine(r, projected));
            if (withStack)
            {
                var trace = entry.Session.ComputeStackTrace(r.Process, r.Exception);
                if (!string.IsNullOrEmpty(trace))
                {
                    foreach (var line in trace.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (trimmed.Length > 0)
                        {
                            sb.Append("    ").AppendLine(trimmed);
                        }
                    }
                }
            }
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_exception", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns one exception with its full managed stack trace. Pass id as 'pi/ei' (the suffix you saw in [pi/ei] brackets), e.g. '17/3'.")]
    public static string GetException(
        [Description("Exception id in 'processIndex/exceptionIndex' form (e.g. '17/3')")] string exceptionId,
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null) => Run(() =>
    {
        var entry = ResolveBeetle(path);
        var (pi, ei) = ParseExceptionId(exceptionId);
        var p = ResolveProcess(entry, pi);
        if ((uint)ei >= (uint)p.Exceptions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(exceptionId),
                $"exceptionIndex {ei} is out of range. Process [{pi}] has {p.Exceptions.Count} exception(s).");
        }

        var ex = p.Exceptions[ei];

        var sb = new StringBuilder();
        sb.Append("process: ").AppendLine(Format.ProcessLine(pi, p, entry.Session));
        sb.Append("exceptionId: ").Append(pi).Append('/').AppendLine(ei.ToString());
        sb.Append("timestamp: ").AppendLine(Format.Iso(ex.Timestamp));
        sb.Append("threadId: ").AppendLine(ex.ThreadId.ToString());
        sb.Append("type: ").AppendLine(ex.ExceptionType ?? "<unknown>");
        sb.Append("message: ").AppendLine(ex.ExceptionMessage ?? string.Empty);
        sb.AppendLine("stackTrace:");
        var trace = entry.Session.ComputeStackTrace(p, ex);
        if (string.IsNullOrEmpty(trace))
        {
            sb.AppendLine("  (no stack captured)");
        }
        else
        {
            foreach (var line in trace.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    sb.Append("  ").AppendLine(trimmed);
                }
            }
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_stack_trace", ReadOnly = true, Idempotent = true)]
    [Description("Returns just the formatted managed stack trace for one exception. Lighter than get_exception when the type/message are already known.")]
    public static string GetStackTrace(
        [Description("Exception id in 'processIndex/exceptionIndex' form (e.g. '17/3')")] string exceptionId,
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null) => Run(() =>
    {
        var entry = ResolveBeetle(path);
        var (pi, ei) = ParseExceptionId(exceptionId);
        var p = ResolveProcess(entry, pi);
        if ((uint)ei >= (uint)p.Exceptions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(exceptionId),
                $"exceptionIndex {ei} is out of range. Process [{pi}] has {p.Exceptions.Count} exception(s).");
        }

        var trace = entry.Session.ComputeStackTrace(p, p.Exceptions[ei]);
        return string.IsNullOrEmpty(trace) ? "(no stack captured)" : trace;
    });

    [McpServerTool(Name = "exceptions_around_time", ReadOnly = true, Idempotent = true)]
    [Description(@"Convenience over query_exceptions: return all exceptions within +/- windowMs of a target time, ordered by timestamp. Use this to correlate a .beetle with timestamps from an external test/CI log.

Provide either aroundTime (absolute UTC) or aroundOffset (relative to session start, e.g. '30m', '+1800s'). Filters mirror query_exceptions.")]
    public static string ExceptionsAroundTime(
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null,
        [Description("Target time (UTC, ISO 8601). Provide this OR aroundOffset.")] DateTime? aroundTime = null,
        [Description("Target time as offset from session start, e.g. '30m', '+1800s'. Provide this OR aroundTime.")] string? aroundOffset = null,
        [Description("Half-window in milliseconds (default 5000 = 10s window)")] double? windowMs = null,
        [Description("Restrict to these processIndex values")] int[]? processIndices = null,
        [Description("Exclude these processIndex values")] int[]? excludeProcessIndices = null,
        [Description("Optional regex on process image file name / file path basename")] string? processNameRegex = null,
        [Description("Optional regex on process image file name / file path basename — exclusion")] string? excludeProcessNameRegex = null,
        [Description("Optional regex on exception type")] string? exceptionTypeRegex = null,
        [Description("Optional regex on exception type — exclusion")] string? excludeExceptionTypeRegex = null,
        [Description("Comma-separated subset of 'timestamp,process,type,message,id'. Default: all.")] string? fields = null,
        [Description("Number of leading results to skip (default 0)")] int? skip = null,
        [Description("Maximum number of results (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        double half = windowMs ?? 5000;
        var projected = Format.ParseExceptionFields(fields);

        var entry = ResolveBeetle(path);
        var resolvedAround = ResolveAroundTime(entry, aroundTime, aroundOffset)
            ?? throw new ModelContextProtocol.McpException(
                "exceptions_around_time requires either aroundTime (absolute UTC) or aroundOffset (offset from session start).");

        var filter = CompiledFilter.Build(
            processIndices, excludeProcessIndices, null, null,
            processNameRegex, excludeProcessNameRegex, null,
            exceptionTypeRegex, excludeExceptionTypeRegex, null, null,
            null, null, resolvedAround, half);

        // Order by timestamp, then page. Filter is bounded by the +/- window so
        // the materialized list is small enough to sort in memory.
        var ordered = filter.FilterExceptions(entry)
            .OrderBy(r => r.Exception.Timestamp)
            .ToList();
        int matched = ordered.Count;
        var page = ordered.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("around: ").AppendLine(Format.Iso(resolvedAround));
        sb.Append("window: +/-").Append(Format.Ms(half)).AppendLine(" ms");
        sb.Append("exceptions: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(matched)
          .Append(offset + page.Count < matched ? $", nextSkip={offset + page.Count}" : string.Empty)
          .AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(no exceptions in window)");
            return sb.ToString();
        }

        foreach (var r in page)
        {
            sb.AppendLine(Format.ExceptionLine(r, projected));
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "exceptions_before", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns the exceptions in the same process that occurred before a given exception, within an optional window. Use this for root-cause walk-back: 'this exception broke things — what fired in this process just before it?'")]
    public static string ExceptionsBefore(
        [Description("Exception id in 'processIndex/exceptionIndex' form (e.g. '17/3')")] string exceptionId,
        [Description("Absolute path to a .beetle file. Optional: defaults to the most recently loaded .beetle.")] string? path = null,
        [Description("Optional max look-back window in milliseconds (default unlimited within the process)")] double? withinMs = null,
        [Description("Maximum number of preceding exceptions to return (default 50, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 50, 1, MaxAllowedResults);
        var entry = ResolveBeetle(path);
        var (pi, ei) = ParseExceptionId(exceptionId);
        var p = ResolveProcess(entry, pi);
        if ((uint)ei >= (uint)p.Exceptions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(exceptionId), $"exceptionIndex {ei} is out of range.");
        }

        var anchor = p.Exceptions[ei];
        var lo = withinMs.HasValue ? anchor.Timestamp - TimeSpan.FromMilliseconds(withinMs.Value) : DateTime.MinValue;

        var preceding = new List<ExceptionRef>();
        for (int i = ei - 1; i >= 0 && preceding.Count < take; i--)
        {
            var ex = p.Exceptions[i];
            if (ex.Timestamp < lo)
            {
                break;
            }

            preceding.Add(new ExceptionRef(pi, p, i, ex));
        }

        preceding.Reverse();

        var sb = new StringBuilder();
        sb.Append("anchor: ").AppendLine(Format.ExceptionLine(entry, new ExceptionRef(pi, p, ei, anchor)));
        sb.Append("preceding: ").Append(preceding.Count);
        if (withinMs.HasValue)
        {
            sb.Append(" (within ").Append(Format.Ms(withinMs.Value)).Append(" ms)");
        }

        sb.AppendLine();
        if (preceding.Count == 0)
        {
            sb.AppendLine("(none)");
            return sb.ToString();
        }

        foreach (var r in preceding)
        {
            sb.AppendLine(Format.ExceptionLine(entry, r, includeProcessName: false));
        }

        return sb.ToString();
    });

    internal static (int pi, int ei) ParseExceptionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Exception id is empty. Expected 'processIndex/exceptionIndex' (e.g. '17/3').", nameof(id));
        }

        int slash = id.IndexOf('/');
        if (slash <= 0 || slash == id.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid exception id: '{id}'. Expected 'processIndex/exceptionIndex' (e.g. '17/3').", nameof(id));
        }

        if (!int.TryParse(id.AsSpan(0, slash), out int pi) ||
            !int.TryParse(id.AsSpan(slash + 1), out int ei))
        {
            throw new ArgumentException(
                $"Invalid exception id: '{id}'. Both parts must be integers.", nameof(id));
        }

        return (pi, ei);
    }

    /// <summary>
    /// Resolve aroundTime from either an absolute UTC timestamp or a relative offset
    /// against <c>Session.StartTime</c>. Returns null if neither is supplied; throws
    /// <see cref="ModelContextProtocol.McpException"/> if both are supplied.
    /// </summary>
    internal static DateTime? ResolveAroundTime(LoadedBeetle entry, DateTime? aroundTime, string? aroundOffset)
    {
        if (aroundTime.HasValue && !string.IsNullOrWhiteSpace(aroundOffset))
        {
            throw new ModelContextProtocol.McpException(
                "Provide either aroundTime (absolute UTC) or aroundOffset (offset from session start), not both.");
        }

        if (aroundTime.HasValue)
        {
            return aroundTime;
        }

        if (!string.IsNullOrWhiteSpace(aroundOffset))
        {
            return entry.ToAbsolute(Format.ParseRelativeMs(aroundOffset));
        }

        return null;
    }
}
