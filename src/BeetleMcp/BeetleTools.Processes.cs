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
    [McpServerTool(Name = "list_processes", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists processes recorded in a .beetle, with optional filtering and paging. Each line:
  startTime  name  pid=N  exitCode=E  exceptions=N  [pi]

processIndex (the [pi] in brackets) is the canonical handle — pass it to other tools. PIDs are not unique within a session (Windows reuses them) so prefer processIndex.

Filters are AND-combined. Regexes are case-insensitive and matched against ImageFileName / FilePath basename / CommandLine.")]
    public static string ListProcesses(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Optional regex matched against the process image file name / file path basename")] string? processNameRegex = null,
        [Description("Optional regex matched against the command line")] string? commandLineRegex = null,
        [Description("Optional explicit list of PIDs to include (note: PIDs are reused — prefer processIndices)")] int[]? processIds = null,
        [Description("Optional ISO 8601 lower bound (UTC) on process StartTime")] DateTime? startedAfter = null,
        [Description("Optional ISO 8601 upper bound (UTC) on process StartTime")] DateTime? startedBefore = null,
        [Description("Optional: only include processes that have at least N exceptions")] int? minExceptionCount = null,
        [Description("Sort order: 'startTime' (default), 'exceptions', 'name'")] string? sortBy = null,
        [Description("Number of leading entries to skip (default 0)")] int? skip = null,
        [Description("Maximum number of entries to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = Cache.Load(path);

        var filter = CompiledFilter.Build(
            processIndices: null, excludeProcessIndices: null,
            processIds: processIds, excludeProcessIds: null,
            processNameRegex: processNameRegex, excludeProcessNameRegex: null,
            commandLineRegex: commandLineRegex,
            exceptionTypeRegex: null, excludeExceptionTypeRegex: null,
            messageRegex: null, excludeMessageRegex: null,
            startTime: null, endTime: null, aroundTime: null, windowMs: null);

        var matches = filter.FilterProcesses(entry.Session)
            .Where(t => !startedAfter.HasValue || entry.ToAbsolute(t.process.StartTimeRelativeMSec) >= startedAfter.Value)
            .Where(t => !startedBefore.HasValue || entry.ToAbsolute(t.process.StartTimeRelativeMSec) <= startedBefore.Value)
            .Where(t => !minExceptionCount.HasValue || t.process.Exceptions.Count >= minExceptionCount.Value)
            .ToList();

        IEnumerable<(int pi, Process p)> ordered = (sortBy ?? "startTime").ToLowerInvariant() switch
        {
            "exceptions" => matches.OrderByDescending(t => t.process.Exceptions.Count).ThenBy(t => t.process.StartTimeRelativeMSec),
            "name" => matches.OrderBy(t => Format.ProcessName(t.process), StringComparer.OrdinalIgnoreCase),
            _ => matches.OrderBy(t => t.process.StartTimeRelativeMSec)
        };

        var orderedList = ordered.ToList();
        int total = orderedList.Count;
        var page = orderedList.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("processes: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(total)
          .Append(", totalInSession=").Append(entry.Session.Processes.Count)
          .AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(no processes)");
            return sb.ToString();
        }

        foreach (var (pi, p) in page)
        {
            sb.Append(Format.Iso(entry.ToAbsolute(p.StartTimeRelativeMSec))).Append("  ");
            sb.AppendLine(Format.ProcessLine(pi, p));
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_process", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns details for a single process: name, PID, parent (PID + image file name + start time so reused PIDs disambiguate), command line, file path, start/stop/duration timestamps, exit code, module count, native image count, and exception count.

processIndex is the canonical handle — get it from list_processes or query_exceptions output (the [pi] / [pi/ei] suffix).")]
    public static string GetProcess(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("processIndex (the number in [pi] brackets), 0-based")] int processIndex) => Run(() =>
    {
        var entry = Cache.Load(path);
        var p = ResolveProcess(entry, processIndex);

        var sb = new StringBuilder();
        sb.Append(Format.ProcessLine(processIndex, p)).AppendLine();
        sb.Append("commandLine: ").AppendLine(p.CommandLine ?? "<null>");
        sb.Append("filePath: ").AppendLine(p.FilePath ?? "<null>");
        sb.Append("imageFileName: ").AppendLine(p.ImageFileName ?? "<null>");
        sb.Append("parentPid: ").Append(p.ParentId);
        if (!string.IsNullOrEmpty(p.ParentImageFileName))
        {
            sb.Append("  parentImage: ").Append(p.ParentImageFileName);
        }

        if (p.ParentStartTimeRelativeMSec > 0)
        {
            sb.Append("  parentStart: ").Append(Format.Iso(entry.ToAbsolute(p.ParentStartTimeRelativeMSec)));
        }

        sb.AppendLine();
        sb.Append("startTime: ").AppendLine(Format.Iso(entry.ToAbsolute(p.StartTimeRelativeMSec)));
        if (p.StopTimeRelativeMSec > 0)
        {
            sb.Append("stopTime: ").AppendLine(Format.Iso(entry.ToAbsolute(p.StopTimeRelativeMSec)));
            sb.Append("durationMs: ").AppendLine((p.StopTimeRelativeMSec - p.StartTimeRelativeMSec).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("exitCode: ").AppendLine(p.ExitCode.ToString());
        }
        else
        {
            sb.AppendLine("stopTime: <still running at session end>");
        }

        sb.Append("modules: ").AppendLine(p.Modules.Count.ToString());
        sb.Append("nativeImages: ").AppendLine(p.NativeImages.Count.ToString());
        sb.Append("exceptions: ").Append(p.Exceptions.Count);
        return sb.ToString();
    });

    [McpServerTool(Name = "get_process_tree", ReadOnly = true, Idempotent = true)]
    [Description(@"Renders the parent/child process hierarchy as an indented tree. Each line: 'name pid=N exceptions=N [pi]'. Roots are processes whose parent isn't recorded in the session.

Parents are matched on (parentPid, parentStartTimeRelativeMSec) so PID reuse doesn't cross-link unrelated processes.")]
    public static string GetProcessTree(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Optional regex to limit which processes (and their descendants) are shown")] string? processNameRegex = null,
        [Description("Optional: only show subtrees that contain at least N exceptions")] int? minExceptionCount = null) => Run(() =>
    {
        var entry = Cache.Load(path);
        var s = entry.Session;
        var processes = s.Processes;

        // Index processes by (pid, startTimeRelativeMSec) so parent links can disambiguate reused PIDs.
        var byKey = new Dictionary<(int pid, double start), int>();
        for (int i = 0; i < processes.Count; i++)
        {
            byKey[(processes[i].Id, processes[i].StartTimeRelativeMSec)] = i;
        }

        var children = new List<int>[processes.Count];
        var roots = new List<int>();
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            int parentIdx = -1;
            if (p.ParentId != 0 && byKey.TryGetValue((p.ParentId, p.ParentStartTimeRelativeMSec), out var idx))
            {
                parentIdx = idx;
            }

            if (parentIdx >= 0)
            {
                (children[parentIdx] ??= new List<int>()).Add(i);
            }
            else
            {
                roots.Add(i);
            }
        }

        // Subtree exception totals (for min-count filtering).
        var subtreeExceptions = new int[processes.Count];
        void ComputeSubtree(int i)
        {
            int total = processes[i].Exceptions.Count;
            if (children[i] is { } cs)
            {
                foreach (var c in cs)
                {
                    ComputeSubtree(c);
                    total += subtreeExceptions[c];
                }
            }

            subtreeExceptions[i] = total;
        }

        foreach (var r in roots)
        {
            ComputeSubtree(r);
        }

        System.Text.RegularExpressions.Regex? nameRx = string.IsNullOrEmpty(processNameRegex)
            ? null
            : new System.Text.RegularExpressions.Regex(processNameRegex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        bool MatchesName(int i)
        {
            if (nameRx == null)
            {
                return true;
            }

            var p = processes[i];
            if (p.ImageFileName != null && nameRx.IsMatch(p.ImageFileName))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(p.FilePath) && nameRx.IsMatch(System.IO.Path.GetFileName(p.FilePath)!))
            {
                return true;
            }

            return false;
        }

        bool ShowSubtree(int i)
        {
            if (minExceptionCount.HasValue && subtreeExceptions[i] < minExceptionCount.Value)
            {
                return false;
            }

            if (MatchesName(i))
            {
                return true;
            }

            if (children[i] is { } cs)
            {
                foreach (var c in cs)
                {
                    if (ShowSubtree(c))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var sb = new StringBuilder();
        int rendered = 0;
        void Write(int i, int depth)
        {
            if (!ShowSubtree(i))
            {
                return;
            }

            for (int d = 0; d < depth * 2; d++)
            {
                sb.Append(' ');
            }

            sb.AppendLine(Format.ProcessLine(i, processes[i]));
            rendered++;

            if (children[i] is { } cs)
            {
                foreach (var c in cs.OrderBy(c => processes[c].StartTimeRelativeMSec))
                {
                    Write(c, depth + 1);
                }
            }
        }

        foreach (var r in roots.OrderBy(r => processes[r].StartTimeRelativeMSec))
        {
            Write(r, 0);
        }

        if (rendered == 0)
        {
            sb.AppendLine("(no processes match)");
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "list_modules", ReadOnly = true, Idempotent = true)]
    [Description("Lists managed modules (assemblies) loaded in a process. Each line: 'name  filePath  pdbGuid  methods=N'.")]
    public static string ListModules(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("processIndex (the number in [pi] brackets)")] int processIndex,
        [Description("Optional case-insensitive substring filter on file path")] string? pathFilter = null,
        [Description("Number of leading entries to skip (default 0)")] int? skip = null,
        [Description("Maximum number of entries to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = Cache.Load(path);
        var p = ResolveProcess(entry, processIndex);

        IEnumerable<Module> modules = p.Modules;
        if (!string.IsNullOrEmpty(pathFilter))
        {
            modules = modules.Where(m => m.FilePath != null &&
                m.FilePath.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var ordered = modules
            .OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int total = ordered.Count;
        var page = ordered.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("process: ").AppendLine(Format.ProcessLine(processIndex, p));
        sb.Append("modules: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(total)
          .Append(", totalInProcess=").Append(p.Modules.Count)
          .AppendLine(")");

        foreach (var m in page)
        {
            sb.Append(m.Name ?? "<null>").Append('\t')
              .Append(m.FilePath ?? "<null>").Append('\t')
              .Append(m.PdbGuid).Append('\t')
              .Append("methods=").Append(m.Methods.Count).AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "list_native_images", ReadOnly = true, Idempotent = true)]
    [Description("Lists native images (DLLs/EXEs) loaded into a process's address space. Each line: 'filePath  startAddress  size'.")]
    public static string ListNativeImages(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("processIndex (the number in [pi] brackets)")] int processIndex,
        [Description("Optional case-insensitive substring filter on file path")] string? pathFilter = null,
        [Description("Number of leading entries to skip (default 0)")] int? skip = null,
        [Description("Maximum number of entries to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = Cache.Load(path);
        var p = ResolveProcess(entry, processIndex);

        IEnumerable<Image> images = p.NativeImages;
        if (!string.IsNullOrEmpty(pathFilter))
        {
            images = images.Where(im => im.FilePath != null &&
                im.FilePath.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var ordered = images
            .OrderBy(im => im.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int total = ordered.Count;
        var page = ordered.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("process: ").AppendLine(Format.ProcessLine(processIndex, p));
        sb.Append("nativeImages: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(total)
          .Append(", totalInProcess=").Append(p.NativeImages.Count)
          .AppendLine(")");

        foreach (var im in page)
        {
            sb.Append(im.FilePath ?? "<null>").Append('\t')
              .Append("0x").Append(im.StartAddress.ToString("x")).Append('\t')
              .Append("size=").Append(im.Size).AppendLine();
        }

        return sb.ToString();
    });

    internal static Process ResolveProcess(LoadedBeetle entry, int processIndex)
    {
        if ((uint)processIndex >= (uint)entry.Session.Processes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(processIndex),
                $"processIndex {processIndex} is out of range. Session has {entry.Session.Processes.Count} processes.");
        }

        return entry.Session.Processes[processIndex];
    }
}
