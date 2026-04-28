using System;
using ModelContextProtocol;

namespace BeetleMcp;

public static partial class BeetleTools
{
    internal static readonly BeetleCache Cache = new();

    public const int DefaultMaxResults = 200;

    public const int MaxAllowedResults = 5000;

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
