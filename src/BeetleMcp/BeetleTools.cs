using System;
using ModelContextProtocol;

namespace BeetleMcp;

public static partial class BeetleTools
{
    internal static readonly BeetleCache Cache = new();

    public const int DefaultMaxResults = 200;

    public const int MaxAllowedResults = 5000;

    /// <summary>
    /// Resolves the .beetle for a tool call. If <paramref name="path"/> is provided,
    /// loads / returns the cached entry for that path. If null/empty, returns the
    /// most-recently-accessed cached entry, or throws a clear McpException when the
    /// cache is empty. This lets tools accept an optional path argument without
    /// surfacing the SDK's opaque "An error occurred invoking '<tool>'" when the
    /// caller forgot to supply one.
    /// </summary>
    internal static LoadedBeetle ResolveBeetle(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Cache.Load(path);
        }

        var recent = Cache.TryGetMostRecent();
        if (recent != null)
        {
            return recent;
        }

        throw new McpException(
            "No 'path' argument was supplied and no .beetle file is currently loaded. " +
            "Call load_beetle <path> first, or pass an explicit absolute path to this tool.");
    }

    // The MCP SDK only forwards messages from McpException; other exceptions surface as a generic "An error occurred invoking '<tool>'."
    internal static T Run<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
