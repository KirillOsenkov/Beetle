# Beetle

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