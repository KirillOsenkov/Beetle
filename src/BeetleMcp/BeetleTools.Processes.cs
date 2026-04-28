using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GuiLabs.Dotnet.Recorder;
using ModelContextProtocol.Server;

namespace BeetleMcp;

public static partial class BeetleTools
{
    // A process is considered managed (.NET) if any of its loaded modules is the
    // runtime's core library. Mirrors the heuristic used by the Beetle viewer.
    private static readonly string[] ManagedRuntimeModuleNames =
    {
        "mscorlib",
        "mscorlib.ni",
        "System.Private.CoreLib",
    };

    internal static bool IsManagedProcess(Process p)
    {
        foreach (var m in p.Modules)
        {
            var name = m.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            for (int i = 0; i < ManagedRuntimeModuleNames.Length; i++)
            {
                if (string.Equals(name, ManagedRuntimeModuleNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [McpServerTool(Name = "list_processes", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists processes recorded in a .beetle, with optional filtering and paging. Each line:
  startTime  name  pid=N  exitCode=E  durationMs=D  exceptions=N  [pi]

If a process was still running at session end, 'exitCode' / 'durationMs' are replaced by 'stillRunningAtSessionEnd'.

processIndex (the [pi] in brackets) is the canonical handle — pass it to other tools. PIDs are not unique within a session (Windows reuses them) so prefer processIndex.

Filters are AND-combined. Regexes are case-insensitive and matched against ImageFileName / FilePath basename / CommandLine.")]
    public static string ListProcesses(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("Optional regex matched against the process image file name / file path basename")] string? processNameRegex = null,
        [Description("Optional regex matched against the process image file name / file path basename — exclusion")] string? excludeProcessNameRegex = null,
        [Description("Optional regex matched against the command line")] string? commandLineRegex = null,
        [Description("Optional explicit list of PIDs to include (note: PIDs are reused — prefer processIndices)")] int[]? processIds = null,
        [Description("Optional ISO 8601 lower bound (UTC) on process StartTime")] DateTime? startedAfter = null,
        [Description("Optional ISO 8601 upper bound (UTC) on process StartTime")] DateTime? startedBefore = null,
        [Description("Optional: only include processes that have at least N exceptions")] int? minExceptionCount = null,
        [Description("Optional: only include processes whose recorded duration (StopTime-StartTime) is at least this many ms. Processes still running at session end are excluded.")] double? minDurationMs = null,
        [Description("Optional: only include processes that did NOT exit cleanly (exitCode != 0 OR still running at session end). Useful when looking for killed/timed-out processes.")] bool? notExitedCleanly = null,
        [Description("Optional: filter by managed-ness. true = only managed (.NET) processes (those that loaded mscorlib / System.Private.CoreLib); false = only unmanaged; null = both. Note: a process that crashed before loading the runtime can look unmanaged.")] bool? managed = null,
        [Description("Sort order: 'startTime' (default), 'exceptions', 'duration', 'name'")] string? sortBy = null,
        [Description("Number of leading entries to skip (default 0)")] int? skip = null,
        [Description("Maximum number of entries to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = Cache.Load(path);

        var filter = CompiledFilter.Build(
            processIndices: null, excludeProcessIndices: null,
            processIds: processIds, excludeProcessIds: null,
            processNameRegex: processNameRegex, excludeProcessNameRegex: excludeProcessNameRegex,
            commandLineRegex: commandLineRegex,
            exceptionTypeRegex: null, excludeExceptionTypeRegex: null,
            messageRegex: null, excludeMessageRegex: null,
            startTime: null, endTime: null, aroundTime: null, windowMs: null);

        static double Duration(Process p) =>
            p.StopTimeRelativeMSec > 0 ? p.StopTimeRelativeMSec - p.StartTimeRelativeMSec : 0;

        static bool ExitedCleanly(Process p) =>
            p.StopTimeRelativeMSec > 0 && p.ExitCode == 0;

        var matches = filter.FilterProcesses(entry.Session)
            .Where(t => !startedAfter.HasValue || entry.ToAbsolute(t.process.StartTimeRelativeMSec) >= startedAfter.Value)
            .Where(t => !startedBefore.HasValue || entry.ToAbsolute(t.process.StartTimeRelativeMSec) <= startedBefore.Value)
            .Where(t => !minExceptionCount.HasValue || t.process.Exceptions.Count >= minExceptionCount.Value)
            .Where(t => !minDurationMs.HasValue || Duration(t.process) >= minDurationMs.Value)
            .Where(t => !(notExitedCleanly ?? false) || !ExitedCleanly(t.process))
            .Where(t => !managed.HasValue || IsManagedProcess(t.process) == managed.Value)
            .ToList();

        IEnumerable<(int pi, Process p)> ordered = (sortBy ?? "startTime").ToLowerInvariant() switch
        {
            "exceptions" => matches.OrderByDescending(t => t.process.Exceptions.Count).ThenBy(t => t.process.StartTimeRelativeMSec),
            "duration" => matches.OrderByDescending(t => Duration(t.process)).ThenBy(t => t.process.StartTimeRelativeMSec),
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
            sb.Append("durationMs: ").AppendLine(Format.Ms(p.StopTimeRelativeMSec - p.StartTimeRelativeMSec));
            sb.Append("exitCode: ").AppendLine(p.ExitCode.ToString());
        }
        else
        {
            sb.AppendLine("stopTime: <still running at session end>");
        }

        sb.Append("managed: ").AppendLine(IsManagedProcess(p) ? "true" : "false");
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
        [Description("Optional: only show subtrees that contain at least N exceptions")] int? minExceptionCount = null,
        [Description("Optional: only show subtrees that contain at least one managed (.NET) process. Useful for hiding the OS/agent noise.")] bool? managedOnly = null) => Run(() =>
    {
        var entry = Cache.Load(path);
        var s = entry.Session;
        var processes = s.Processes;

        // Index processes by (pid, startTimeRelativeMSec) so parent links can disambiguate reused PIDs.
        var byKey = BuildKeyMap(processes);

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

        // Subtree exception totals — only needed when filtering by min count.
        int[]? subtreeExceptions = null;
        if (minExceptionCount.HasValue)
        {
            subtreeExceptions = new int[processes.Count];
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
        }

        // Subtree managed marker — only needed when managedOnly is set.
        bool[]? subtreeHasManaged = null;
        if (managedOnly == true)
        {
            subtreeHasManaged = new bool[processes.Count];
            bool ComputeManaged(int i)
            {
                bool any = IsManagedProcess(processes[i]);
                if (children[i] is { } cs)
                {
                    foreach (var c in cs)
                    {
                        if (ComputeManaged(c))
                        {
                            any = true;
                        }
                    }
                }

                subtreeHasManaged[i] = any;
                return any;
            }

            foreach (var r in roots)
            {
                ComputeManaged(r);
            }
        }

        Regex? nameRx = CompiledFilter.CompileProcessNameRegex(processNameRegex);

        bool MatchesName(int i) => nameRx == null || CompiledFilter.ProcessNameMatches(nameRx, processes[i]);

        bool MeetsMinCount(int i) =>
            subtreeExceptions == null || subtreeExceptions[i] >= minExceptionCount!.Value;

        bool MeetsManaged(int i) =>
            subtreeHasManaged == null || subtreeHasManaged[i];

        // Does this subtree contain any name-matching node? Only consulted when
        // a name regex is in use; collapses to "always true" otherwise.
        bool SubtreeHasNameMatch(int i)
        {
            if (MatchesName(i))
            {
                return true;
            }

            if (children[i] is { } cs)
            {
                foreach (var c in cs)
                {
                    if (SubtreeHasNameMatch(c))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var sb = new StringBuilder();
        int rendered = 0;
        void Write(int i, int depth, bool ancestorMatched)
        {
            // minExceptionCount prunes the subtree structurally regardless of name matching.
            if (!MeetsMinCount(i))
            {
                return;
            }

            if (!MeetsManaged(i))
            {
                return;
            }

            // Show node when: no name filter, OR an ancestor matched (so the user
            // can see what matched and what it spawned), OR this subtree contains
            // a name match somewhere below.
            bool selfMatches = MatchesName(i);
            bool show = nameRx == null || ancestorMatched || selfMatches || SubtreeHasNameMatch(i);
            if (!show)
            {
                return;
            }

            for (int d = 0; d < depth * 2; d++)
            {
                sb.Append(' ');
            }

            sb.AppendLine(Format.ProcessLine(i, processes[i]));
            rendered++;

            bool subtreeAncestorMatched = ancestorMatched || selfMatches;
            if (children[i] is { } cs)
            {
                foreach (var c in cs.OrderBy(c => processes[c].StartTimeRelativeMSec))
                {
                    Write(c, depth + 1, subtreeAncestorMatched);
                }
            }
        }

        foreach (var r in roots.OrderBy(r => processes[r].StartTimeRelativeMSec))
        {
            Write(r, 0, ancestorMatched: false);
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
              .Append(m.PdbGuid == Guid.Empty ? "<no-pdb>" : m.PdbGuid.ToString()).Append('\t')
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

    [McpServerTool(Name = "get_process_parent_chain", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns the chain of ancestor processes for a given process, from the recorded root down to the target. Each line is one process, indented by depth:
  name pid=N [exitCode=E] exceptions=N [pi]

The last (most-indented) line is the target. Useful for answering 'who started this process?' without manually walking parent links. Parents are matched on (parentPid, parentStartTimeRelativeMSec) so PID reuse can't cross-link unrelated processes. If the parent isn't recorded in the session the chain stops there.")]
    public static string GetProcessParentChain(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("processIndex (the [pi] number) of the process whose parent chain you want")] int processIndex) => Run(() =>
    {
        var entry = Cache.Load(path);
        var processes = entry.Session.Processes;
        ResolveProcess(entry, processIndex);

        var byKey = BuildKeyMap(processes);

        // Walk parents, guarding against cycles (shouldn't happen, but cheap).
        var chain = new List<int>();
        var seen = new HashSet<int>();
        int cur = processIndex;
        while (cur >= 0 && seen.Add(cur))
        {
            chain.Add(cur);
            var p = processes[cur];
            if (p.ParentId == 0)
            {
                break;
            }

            if (!byKey.TryGetValue((p.ParentId, p.ParentStartTimeRelativeMSec), out var parentIdx))
            {
                break;
            }

            cur = parentIdx;
        }

        chain.Reverse();

        var sb = new StringBuilder();
        sb.Append("parent chain for [").Append(processIndex).Append("]: ")
          .Append(chain.Count).AppendLine(" level(s)");

        var rootProc = processes[chain[0]];
        if (rootProc.ParentId != 0 && !byKey.ContainsKey((rootProc.ParentId, rootProc.ParentStartTimeRelativeMSec)))
        {
            sb.Append("(parent of root in chain not recorded: parentPid=").Append(rootProc.ParentId);
            if (!string.IsNullOrEmpty(rootProc.ParentImageFileName))
            {
                sb.Append(" parentImage=").Append(rootProc.ParentImageFileName);
            }

            if (rootProc.ParentStartTimeRelativeMSec > 0)
            {
                sb.Append(" parentStart=").Append(Format.Iso(entry.ToAbsolute(rootProc.ParentStartTimeRelativeMSec)));
            }

            sb.AppendLine(")");
        }

        for (int depth = 0; depth < chain.Count; depth++)
        {
            for (int d = 0; d < depth * 2; d++)
            {
                sb.Append(' ');
            }

            sb.AppendLine(Format.ProcessLine(chain[depth], processes[chain[depth]]));
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_process_children", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists processes spawned by a given parent process. With recursive=false (default) returns only direct children, flat. With recursive=true returns the full descendant subtree, indented by depth. Useful for answering 'what did this process spawn?' in one call.

Children are matched on (parentPid, parentStartTimeRelativeMSec) so PID reuse can't cross-link unrelated processes. Filters apply only to descendants — the parent line is always shown for context.")]
    public static string GetProcessChildren(
        [Description("Absolute path to a .beetle file")] string path,
        [Description("processIndex (the [pi] number) of the parent process whose children you want")] int processIndex,
        [Description("If true, include the entire descendant subtree (indented). If false (default), only direct children.")] bool? recursive = null,
        [Description("Optional regex filter on descendant ImageFileName / FilePath basename")] string? processNameRegex = null,
        [Description("Number of leading entries to skip (default 0)")] int? skip = null,
        [Description("Maximum number of entries to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        bool deep = recursive ?? false;

        var entry = Cache.Load(path);
        var processes = entry.Session.Processes;
        var parent = ResolveProcess(entry, processIndex);

        var byKey = BuildKeyMap(processes);
        var children = new List<int>[processes.Count];
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (p.ParentId == 0)
            {
                continue;
            }

            if (byKey.TryGetValue((p.ParentId, p.ParentStartTimeRelativeMSec), out var parentIdx))
            {
                (children[parentIdx] ??= new List<int>()).Add(i);
            }
        }

        Regex? nameRx = CompiledFilter.CompileProcessNameRegex(processNameRegex);

        bool MatchesName(int i) => nameRx == null || CompiledFilter.ProcessNameMatches(nameRx, processes[i]);

        var collected = new List<(int pi, int depth)>();
        void Walk(int i, int depth)
        {
            if (depth > 0 && MatchesName(i))
            {
                collected.Add((i, depth));
            }

            if (depth == 0 || deep)
            {
                if (children[i] is { } cs)
                {
                    foreach (var c in cs.OrderBy(c => processes[c].StartTimeRelativeMSec))
                    {
                        Walk(c, depth + 1);
                    }
                }
            }
        }

        Walk(processIndex, 0);

        int total = collected.Count;
        var page = collected.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("parent: ").AppendLine(Format.ProcessLine(processIndex, parent));
        sb.Append(deep ? "descendants: " : "direct children: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(total)
          .AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(none)");
            return sb.ToString();
        }

        foreach (var (pi, depth) in page)
        {
            int indent = deep ? depth * 2 : 2;
            for (int d = 0; d < indent; d++)
            {
                sb.Append(' ');
            }

            sb.AppendLine(Format.ProcessLine(pi, processes[pi]));
        }

        return sb.ToString();
    });

    private static Dictionary<(int pid, double start), int> BuildKeyMap(IReadOnlyList<Process> processes)
    {
        var byKey = new Dictionary<(int pid, double start), int>(processes.Count);
        for (int i = 0; i < processes.Count; i++)
        {
            byKey[(processes[i].Id, processes[i].StartTimeRelativeMSec)] = i;
        }

        return byKey;
    }

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
