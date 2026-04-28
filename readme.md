# Beetle

(**B**inary-**E**ncoded **E**vent **T**racing **L**og for **E**xceptions)

```
dotnet tool update -g beetle
```

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

## Beetle.Core

Beetle.Core is a NuGet package that lets you read (or write!) .beetle files and provides an object model around them.

https://www.nuget.org/packages/Beetle.Core

## What is a .beetle file?

A `.beetle` file is a binary recording of every managed (.NET) exception thrown system-wide during a recording window on a Windows machine, captured via kernel + CLR ETW providers. For each exception it stores:

- the **process** that threw it (full process tree, image file name, command line, parent),
- the **timestamp** (absolute UTC),
- the **exception type and message**,
- the **managed stack trace** (resolved against the JIT'd methods + module symbols recorded in the same file).

It also captures every process's loaded managed modules and native images.

The file is produced by the `beetle` global tool (`dotnet tool update -g beetle`, then `beetle out.beetle`, Ctrl+C to stop). It can be read programmatically via the [`Beetle.Core`](https://www.nuget.org/packages/Beetle.Core) NuGet package, or analyzed by an AI assistant via the [`beetlemcp`](https://www.nuget.org/packages/BeetleMcp) MCP server.

The on-disk envelope is a GZip stream; the file is not human-readable. Treat the `.beetle` extension as the canonical signal.
