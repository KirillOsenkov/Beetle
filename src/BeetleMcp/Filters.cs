using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GuiLabs.Dotnet.Recorder;

namespace BeetleMcp;

internal readonly record struct ExceptionRef(int ProcessIndex, Process Process, int ExceptionIndex, ExceptionEvent Exception);

/// <summary>
/// Compiled filter ready to evaluate against processes and exceptions.
/// Created by <see cref="Build"/> from raw user inputs (regexes / id arrays / time bounds).
/// All conditions are AND-combined. Regexes are case-insensitive.
/// </summary>
internal sealed class CompiledFilter
{
    public HashSet<int>? IncludeProcessIndex;
    public HashSet<int>? ExcludeProcessIndex;
    public HashSet<int>? IncludePid;
    public HashSet<int>? ExcludePid;
    public Regex? IncludeProcessName;
    public Regex? ExcludeProcessName;
    public Regex? CommandLine;
    public Regex? IncludeType;
    public Regex? ExcludeType;
    public Regex? IncludeMessage;
    public Regex? ExcludeMessage;
    public DateTime? StartTime;
    public DateTime? EndTime;

    public static CompiledFilter Build(
        int[]? processIndices,
        int[]? excludeProcessIndices,
        int[]? processIds,
        int[]? excludeProcessIds,
        string? processNameRegex,
        string? excludeProcessNameRegex,
        string? commandLineRegex,
        string? exceptionTypeRegex,
        string? excludeExceptionTypeRegex,
        string? messageRegex,
        string? excludeMessageRegex,
        DateTime? startTime,
        DateTime? endTime,
        DateTime? aroundTime,
        double? windowMs)
    {
        DateTime? lo = startTime;
        DateTime? hi = endTime;
        if (aroundTime is { } center && windowMs is { } w)
        {
            var span = TimeSpan.FromMilliseconds(w);
            var aLo = center - span;
            var aHi = center + span;
            lo = lo.HasValue && lo.Value > aLo ? lo : aLo;
            hi = hi.HasValue && hi.Value < aHi ? hi : aHi;
        }

        return new CompiledFilter
        {
            IncludeProcessIndex = ToSet(processIndices),
            ExcludeProcessIndex = ToSet(excludeProcessIndices),
            IncludePid = ToSet(processIds),
            ExcludePid = ToSet(excludeProcessIds),
            IncludeProcessName = Compile(processNameRegex),
            ExcludeProcessName = Compile(excludeProcessNameRegex),
            CommandLine = Compile(commandLineRegex),
            IncludeType = Compile(exceptionTypeRegex),
            ExcludeType = Compile(excludeExceptionTypeRegex),
            IncludeMessage = Compile(messageRegex),
            ExcludeMessage = Compile(excludeMessageRegex),
            StartTime = lo,
            EndTime = hi
        };
    }

    public IEnumerable<(int processIndex, Process process)> FilterProcesses(Session session)
    {
        for (int i = 0; i < session.Processes.Count; i++)
        {
            var p = session.Processes[i];

            if (IncludeProcessIndex is { } incIdx && !incIdx.Contains(i))
            {
                continue;
            }

            if (ExcludeProcessIndex is { } excIdx && excIdx.Contains(i))
            {
                continue;
            }

            if (IncludePid is { } incPid && !incPid.Contains(p.Id))
            {
                continue;
            }

            if (ExcludePid is { } excPid && excPid.Contains(p.Id))
            {
                continue;
            }

            if (IncludeProcessName is { } incName && !MatchProcessName(incName, p))
            {
                continue;
            }

            if (ExcludeProcessName is { } excName && MatchProcessName(excName, p))
            {
                continue;
            }

            if (CommandLine is { } cl && (p.CommandLine == null || !cl.IsMatch(p.CommandLine)))
            {
                continue;
            }

            yield return (i, p);
        }
    }

    public IEnumerable<ExceptionRef> FilterExceptions(LoadedBeetle loaded)
    {
        foreach (var (pi, p) in FilterProcesses(loaded.Session))
        {
            for (int ei = 0; ei < p.Exceptions.Count; ei++)
            {
                var ex = p.Exceptions[ei];

                if (IncludeType is { } incT && (ex.ExceptionType == null || !incT.IsMatch(ex.ExceptionType)))
                {
                    continue;
                }

                if (ExcludeType is { } excT && ex.ExceptionType != null && excT.IsMatch(ex.ExceptionType))
                {
                    continue;
                }

                if (IncludeMessage is { } incM && (ex.ExceptionMessage == null || !incM.IsMatch(ex.ExceptionMessage)))
                {
                    continue;
                }

                if (ExcludeMessage is { } excM && ex.ExceptionMessage != null && excM.IsMatch(ex.ExceptionMessage))
                {
                    continue;
                }

                if (StartTime is { } lo && ex.Timestamp < lo)
                {
                    continue;
                }

                if (EndTime is { } hi && ex.Timestamp > hi)
                {
                    continue;
                }

                yield return new ExceptionRef(pi, p, ei, ex);
            }
        }
    }

    private static bool MatchProcessName(Regex regex, Process p)
    {
        if (p.ImageFileName != null && regex.IsMatch(p.ImageFileName))
        {
            return true;
        }

        if (p.FilePath != null)
        {
            var name = Path.GetFileName(p.FilePath);
            if (!string.IsNullOrEmpty(name) && regex.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex? Compile(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HashSet<int>? ToSet(int[]? values) =>
        values is { Length: > 0 } ? new HashSet<int>(values) : null;
}
