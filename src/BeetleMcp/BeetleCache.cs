using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.Dotnet.Recorder;

namespace BeetleMcp;

public sealed class LoadedBeetle
{
    public required string Path { get; init; }

    public required Session Session { get; init; }

    public long FileSize { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    public DateTime LoadedAtUtc { get; init; }

    public DateTime LastAccessedUtc { get; set; }

    /// <summary>
    /// Convert a relative-MSec offset (Process / Exception fields) to absolute UTC.
    /// </summary>
    public DateTime ToAbsolute(double relativeMSec) =>
        Session.StartTime + TimeSpan.FromMilliseconds(relativeMSec);
}

/// <summary>
/// Caches loaded .beetle files keyed by full path. LRU eviction on a coarse
/// memory budget. Mirrors the BinlogCache design.
/// </summary>
public sealed class BeetleCache
{
    // Loose heuristic: a .beetle is gzip'd and expands non-trivially in memory
    // (string table, callstack interning, jitted-method maps).
    public const long MemoryMultiplier = 20;

    private readonly object syncRoot = new();
    private readonly Dictionary<string, LoadedBeetle> entries = new(PathComparer);

    public BeetleCache(long? memoryBudgetBytes = null)
    {
        MemoryBudgetBytes = memoryBudgetBytes ?? GetDefaultMemoryBudget();
    }

    public long MemoryBudgetBytes { get; }

    public LoadedBeetle Load(string path, bool forceReload = false)
    {
        path = NormalizePath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Beetle log not found: {path}", path);
        }

        var info = new FileInfo(path);
        long estimated = info.Length * MemoryMultiplier;

        lock (syncRoot)
        {
            if (!forceReload &&
                entries.TryGetValue(path, out var cached) &&
                cached.FileSize == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                cached.LastAccessedUtc = DateTime.UtcNow;
                return cached;
            }

            if (entries.Remove(path))
            {
                ForceCollect();
            }

            EvictToFit(estimated);
        }

        // Slow path outside the lock.
        var session = SessionSerializer.Load(path);

        var entry = new LoadedBeetle
        {
            Path = path,
            Session = session,
            FileSize = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            LoadedAtUtc = DateTime.UtcNow,
            LastAccessedUtc = DateTime.UtcNow
        };

        lock (syncRoot)
        {
            entries[path] = entry;
        }

        return entry;
    }

    public bool Unload(string path)
    {
        path = NormalizePath(path);
        lock (syncRoot)
        {
            if (entries.Remove(path))
            {
                ForceCollect();
                return true;
            }

            return false;
        }
    }

    public int UnloadAll()
    {
        lock (syncRoot)
        {
            int n = entries.Count;
            entries.Clear();
            if (n > 0)
            {
                ForceCollect();
            }

            return n;
        }
    }

    public IReadOnlyList<LoadedBeetle> List()
    {
        lock (syncRoot)
        {
            return entries.Values.ToArray();
        }
    }

    // Caller holds syncRoot.
    private void EvictToFit(long incoming)
    {
        if (incoming >= MemoryBudgetBytes)
        {
            if (entries.Count > 0)
            {
                entries.Clear();
                ForceCollect();
            }

            return;
        }

        long used = entries.Values.Sum(e => e.FileSize * MemoryMultiplier);
        if (used + incoming <= MemoryBudgetBytes)
        {
            return;
        }

        var lru = entries.Values.OrderBy(e => e.LastAccessedUtc).ToList();
        foreach (var e in lru)
        {
            entries.Remove(e.Path);
            used -= e.FileSize * MemoryMultiplier;
            if (used + incoming <= MemoryBudgetBytes)
            {
                break;
            }
        }

        ForceCollect();
    }

    private static void ForceCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static long GetDefaultMemoryBudget()
    {
        var info = GC.GetGCMemoryInfo();
        return (long)(info.TotalAvailableMemoryBytes * 0.75);
    }
}
