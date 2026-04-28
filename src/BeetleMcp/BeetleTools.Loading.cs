using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using GuiLabs.Dotnet.Recorder;
using ModelContextProtocol.Server;

namespace BeetleMcp;

[McpServerToolType]
public static partial class BeetleTools
{
    [McpServerTool(Name = "load_beetle", ReadOnly = true, Idempotent = true)]
    [Description(@"Loads a .beetle exception trace into memory and returns a session summary. Optional: any tool that takes a beetle path will load it implicitly if not already cached. Use this to warm the cache or to inspect the summary up front.")]
    public static string LoadBeetle(
        [Description("Absolute path to a .beetle file")] string path)
        => Run(() => Describe(Cache.Load(path)));

    [McpServerTool(Name = "reload_beetle", ReadOnly = true, Idempotent = true)]
    [Description(@"Re-reads a .beetle from disk, replacing the cached version. Use this if the file has been updated. processIndex / exceptionIndex values are scoped to one file's bytes — discard previously returned ids after a reload that produced different content.")]
    public static string ReloadBeetle(
        [Description("Absolute path to a .beetle file")] string path)
        => Run(() => Describe(Cache.Load(path, forceReload: true)));

    [McpServerTool(Name = "unload_beetle", ReadOnly = true, Idempotent = true)]
    [Description("Evicts a single .beetle from the cache to free memory.")]
    public static string UnloadBeetle(
        [Description("Absolute path to the .beetle file to evict")] string path)
        => Run(() => Cache.Unload(path) ? $"unloaded {path}" : $"not loaded: {path}");

    [McpServerTool(Name = "unload_all_beetles", ReadOnly = true, Idempotent = true)]
    [Description("Evicts all loaded .beetle files from the cache to free memory.")]
    public static string UnloadAllBeetles()
        => Run(() => $"unloaded {Cache.UnloadAll()} beetle(s)");

    [McpServerTool(Name = "list_loaded_beetles", ReadOnly = true, Idempotent = true)]
    [Description("Lists all .beetle files currently loaded in the cache, with file size and last-access timestamp.")]
    public static string ListLoadedBeetles() => Run(() =>
    {
        var entries = Cache.List();
        if (entries.Count == 0)
        {
            return "no beetles loaded";
        }

        return string.Join("\n", entries
            .OrderByDescending(e => e.LastAccessedUtc)
            .Select(e => $"{e.Path}\tfileSize={e.FileSize:n0}\tlastAccessed={Format.Iso(e.LastAccessedUtc)}"));
    });

    [McpServerTool(Name = "get_session_summary", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns a one-page overview of a loaded .beetle session: file size, recording window (start / end / duration), events lost, total processes, total exceptions, and the count of distinct exception types. Always cheap — call this first on any unfamiliar file.")]
    public static string GetSessionSummary(
        [Description("Absolute path to a .beetle file")] string path)
        => Run(() => Describe(Cache.Load(path)));

    private static string Describe(LoadedBeetle entry)
    {
        var s = entry.Session;

        int totalExceptions = 0;
        var distinctTypes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var p in s.Processes)
        {
            totalExceptions += p.Exceptions.Count;
            foreach (var ex in p.Exceptions)
            {
                if (ex.ExceptionType != null)
                {
                    distinctTypes.Add(ex.ExceptionType);
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("path: ").AppendLine(entry.Path);
        sb.Append("fileSize: ").Append(entry.FileSize.ToString("n0")).AppendLine(" bytes");
        sb.Append("startTime: ").AppendLine(Format.Iso(s.StartTime));
        sb.Append("endTime: ").AppendLine(Format.Iso(s.EndTime));
        sb.Append("durationMs: ").AppendLine(s.SessionEndTimeRelativeMSec.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("eventsLost: ").AppendLine(s.EventsLost.ToString());
        sb.Append("processes: ").AppendLine(s.Processes.Count.ToString());
        sb.Append("exceptions: ").AppendLine(totalExceptions.ToString());
        sb.Append("distinctExceptionTypes: ").Append(distinctTypes.Count);
        return sb.ToString();
    }
}
