# Beetle

(**B**inary-**E**ncoded **E**vent **T**racing **L**og for **E**xceptions)

Beetle is a tool that records all .NET exceptions with callstacks system-wide into a binary log file that can be viewed on another machine.

Beetle starts a copy of itself under admin if it's not running as administrator. It needs admin permissions to create a kernel ETW session.

Usage:

`beetle [<log-file>.beetle]`
Starts recording, press Ctrl+C to stop, optional log path defaults to %TEMP%\AllExceptions.beetle.

`beetle.exe start [<log-file>.beetle]`
Starts recording and returns immediately, while another instance of beetle.exe continues recording.

`beetle.exe stop`
Finds an existing instance of beetle.exe and stops recording, waits for it to finish
writing the log, then returns.

## BeetleMcp (MCP server for AI)

`beetlemcp` lets an AI assistant (Claude / Copilot / etc.) read and analyze `.beetle` files: list processes, query/count/bin exceptions, walk parent chains, fetch stack traces, diff two recordings, and more.

```
dotnet tool update -g beetlemcp
```

Configure your MCP-aware client to launch it. For VS Code, add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "beetlemcp": {
      "type": "stdio",
      "command": "beetlemcp"
    }
  }
}
```
