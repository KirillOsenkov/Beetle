using System;
using ModelContextProtocol;

namespace BeetleMcp;

/// <summary>
/// All MCP tools live under <see cref="BeetleTools"/> as a partial class
/// (one file per concern: Loading, Processes, Exceptions, Diff, Help).
/// </summary>
public static partial class BeetleTools
{
    internal static readonly BeetleCache Cache = new();

    public const int DefaultMaxResults = 200;

    public const int MaxAllowedResults = 5000;

    /// <summary>
    /// The MCP SDK only forwards the original message when the thrown
    /// exception derives from <see cref="McpException"/>; anything else
    /// becomes "An error occurred invoking '&lt;tool&gt;'." Wrap every tool
    /// body so LLMs see actionable diagnostics.
    /// </summary>
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
