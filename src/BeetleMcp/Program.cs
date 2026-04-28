using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeetleMcp;

internal sealed class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // MCP servers communicate over stdio; route logs to stderr so they
        // don't corrupt the JSON-RPC protocol stream on stdout.
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions;
                options.ServerInfo = new()
                {
                    Name = "beetlemcp",
                    Version = Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0",
                    WebsiteUrl = "https://github.com/KirillOsenkov/Beetle"
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    private const string ServerInstructions = """
        Beetle .beetle exception-log navigator. Each loaded session is a recording of all .NET
        exceptions thrown system-wide during a window of time, with process tree and stacks.

        Identifiers:
          [pi]      = processIndex (NOT the OS PID — PIDs are reused). Always pass processIndex.
          [pi/ei]   = exceptionIndex within process pi.

        Standard flow: load_beetle -> get_session_summary / list_processes -> query_exceptions
        with filters (process, time window, type/message regex) -> get_exception for stacks.

        Unsure how to proceed? Call `get_llm_guide` for the full field manual: workflow,
        recipes (good-vs-bad diff, correlate-with-test-log-timestamp, root-cause walk-back),
        filter reference, and pitfalls.
        """;
}
