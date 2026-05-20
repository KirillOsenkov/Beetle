using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace GuiLabs.Dotnet.Recorder;

public class Beetle
{
    public const string SessionPrefix = "DotNetBeetleRecorder";

    public static void Main(string[] args)
    {
        string logFilePath = null;

        if (args.Length > 0)
        {
            string arg = args[0];
            if (arg.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length > 1)
                {
                    logFilePath = args[1];
                }

                Start(logFilePath);
                return;
            }
            else if (arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                Stop();
                return;
            }
            else if (arg.EndsWith(".beetle", StringComparison.OrdinalIgnoreCase))
            {
                logFilePath = arg;
            }
            else if (IsHelpArg(arg))
            {
                PrintHelp(Console.Out);
                return;
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument: {arg}");
                PrintHelp(Console.Error);
                Environment.Exit(1);
                return;
            }
        }

        if (!IsAdmin)
        {
            StartProcessAsAdmin();
            return;
        }

        if (logFilePath == null)
        {
            logFilePath = Path.Combine(Path.GetTempPath(), "AllExceptions.beetle");
        }
        else
        {
            logFilePath = Path.GetFullPath(logFilePath);
        }

        var logDir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        StopExistingSessions();

        using var traceEventSession = new TraceEventSession(SessionPrefix);
        traceEventSession.BufferSizeMB = 4096;

        traceEventSession.EnableKernelProvider(
            KernelTraceEventParser.Keywords.ImageLoad |
            KernelTraceEventParser.Keywords.Process,
            stackCapture: KernelTraceEventParser.Keywords.None);

        var etwSource = traceEventSession.Source;

        var session = new Session();

        session.SubscribeToEtwEvents(etwSource, traceEventSession);

        traceEventSession.EnableProvider(
            ClrTraceEventParser.ProviderGuid,
            TraceEventLevel.Verbose,
            (ulong)(
            ClrTraceEventParser.Keywords.Binder |
            ClrTraceEventParser.Keywords.Exception |
            ClrTraceEventParser.Keywords.Jit |
            ClrTraceEventParser.Keywords.JITSymbols |
            ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
            ClrTraceEventParser.Keywords.Loader |
            ClrTraceEventParser.Keywords.Stack));

        var task = Task.Run(() =>
        {
            try
            {
                etwSource.Process();
            }
            catch
            {
            }
        });

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            try
            {
                session.EventsLost = traceEventSession.EventsLost;
                traceEventSession.Stop(noThrow: true);
            }
            catch
            {
            }
        };

        // Wire up state for the OS console control handler so CTRL_CLOSE_EVENT
        // (window closed), CTRL_LOGOFF_EVENT, and CTRL_SHUTDOWN_EVENT can save
        // the recording before we're killed. The OS gives the handler ~5s on
        // CLOSE and ~20s on LOGOFF/SHUTDOWN before TerminateProcess.
        _session = session;
        _traceEventSession = traceEventSession;
        _logFilePath = logFilePath;
        _task = task;
        _consoleHandler = OnConsoleCtrl;
        SetConsoleCtrlHandler(_consoleHandler, true);

        Console.WriteLine($"Log: {logFilePath}");
        Console.WriteLine("Press Ctrl+C to stop recording...");

        task.Wait();

        SaveNow();
    }

    private static readonly string[] HelpArgs = { "-?", "/?", "-h", "/h", "help", "-help", "/help", "--help" };

    private static bool IsHelpArg(string arg)
    {
        foreach (var h in HelpArgs)
        {
            if (arg.Equals(h, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void PrintHelp(System.IO.TextWriter writer)
    {
        writer.WriteLine(
            """
            Usage: beetle.exe [<log-file>.beetle]
                   Starts recording, press Ctrl+C to stop. Optional log path defaults to
                   %TEMP%\AllExceptions.beetle.

                   beetle.exe start [<log-file>.beetle]
                   Starts recording and returns immediately, while another instance of
                   beetle.exe continues recording in its own console.

                   beetle.exe stop
                   Finds the running instance of beetle.exe, stops recording, waits for it
                   to finish writing the log, then returns.
            """);
    }

    public static void Start(string filePath)
    {
        // Always launch via ShellExecute so the recorder gets its own console and is
        // fully detached from the caller. Otherwise the recorder inherits the caller's console
        // and gets killed by CTRL_CLOSE_EVENT when the caller (or the console host above it) is
        // torn down — losing the recording with no chance to save.
        var psi = new System.Diagnostics.ProcessStartInfo(CurrentExecutable, filePath)
        {
            UseShellExecute = true,
        };

        if (!IsAdmin)
        {
            psi.Verb = "runas";
        }

        TryStartProcess(psi);
    }

    public static System.Diagnostics.Process StartProcessAsAdmin(string arguments = "")
    {
        var psi = new System.Diagnostics.ProcessStartInfo(CurrentExecutable, arguments);
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        return TryStartProcess(psi);
    }

    // ERROR_CANCELLED = user clicked No on the UAC prompt. Surface as a clean
    // message instead of letting it propagate as an unhandled Win32Exception.
    private const int ERROR_CANCELLED = 1223;

    private static System.Diagnostics.Process TryStartProcess(System.Diagnostics.ProcessStartInfo psi)
    {
        try
        {
            return System.Diagnostics.Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            Console.Error.WriteLine("Elevation was declined.");
            Environment.Exit(2);
            return null;
        }
    }

    public static void Stop()
    {
        if (!IsAdmin)
        {
            var process = StartProcessAsAdmin("stop");
            process.WaitForExit();
            return;
        }

        var otherInstance = FindOtherInstance();
        if (otherInstance == null)
        {
            Console.Error.WriteLine($"Couldn't find another instance of {CurrentExecutable}");
            return;
        }

        StopExistingSessions();

        otherInstance.WaitForExit();
    }

    public static string CurrentExecutable => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

    public static System.Diagnostics.Process FindOtherInstance()
    {
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var currentProcessId = currentProcess.Id;

        int parentId = GetParentProcessId(currentProcess.Handle);

        var processName = currentProcess.ProcessName;

        System.Diagnostics.Process match = null;
        var allProcesses = System.Diagnostics.Process.GetProcesses();

        foreach (var process in allProcesses)
        {
            try
            {
                var id = process.Id;
                if (id == 0 || id == 4 || id == currentProcessId || id == parentId)
                {
                    process.Dispose();
                    continue;
                }

                if (match == null && process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                {
                    match = process;
                    continue;
                }

                process.Dispose();
            }
            catch
            {
                try { process.Dispose(); } catch { }
            }
        }

        return match;
    }

    private static int GetParentProcessId(IntPtr processHandle)
    {
        PROCESS_BASIC_INFORMATION pbi = default;
        int returnLength = 0;

        int status = NtQueryInformationProcess(
            processHandle,
            0,
            ref pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            ref returnLength);

        // Best-effort: if the syscall fails we just skip parent-process exclusion
        // in the caller's scan, which is harmless.
        if (status != 0)
        {
            return 0;
        }

        return pbi.InheritedFromUniqueProcessId.ToInt32();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        ref int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    // Shared state used by both the normal exit path and the OS console control
    // handler. SaveNow() guards against concurrent / double save.
    private static Session _session;
    private static TraceEventSession _traceEventSession;
    private static string _logFilePath;
    private static Task _task;
    private static int _saveStarted;

    // Rooted as a static field so the GC doesn't collect the delegate while
    // the OS still holds a native function pointer to it.
    private static HandlerRoutine _consoleHandler;

    private delegate bool HandlerRoutine(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        HandlerRoutine handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);

    private const uint CTRL_C_EVENT        = 0;
    private const uint CTRL_BREAK_EVENT    = 1;
    private const uint CTRL_CLOSE_EVENT    = 2;
    private const uint CTRL_LOGOFF_EVENT   = 5;
    private const uint CTRL_SHUTDOWN_EVENT = 6;

    private static bool OnConsoleCtrl(uint ctrlType)
    {
        if (ctrlType == CTRL_CLOSE_EVENT ||
            ctrlType == CTRL_LOGOFF_EVENT ||
            ctrlType == CTRL_SHUTDOWN_EVENT)
        {
            // We have ~5s (CLOSE) or ~20s (LOGOFF/SHUTDOWN) before the OS
            // terminates us. Try to save synchronously inside the handler.
            SaveNow();
            return true;
        }

        // For Ctrl+C / Ctrl+Break, let Console.CancelKeyPress fire and the
        // normal Main flow drive the save.
        return false;
    }

    private static void SaveNow()
    {
        if (Interlocked.Exchange(ref _saveStarted, 1) != 0)
        {
            return;
        }

        var session = _session;
        var traceEventSession = _traceEventSession;
        var logFilePath = _logFilePath;
        var task = _task;

        if (session == null || traceEventSession == null || logFilePath == null)
        {
            return;
        }

        try
        {
            session.EventsLost = traceEventSession.EventsLost;
            traceEventSession.Stop(noThrow: true);

            // Best-effort drain of remaining buffered ETW events before serializing.
            // Bounded so we don't blow past the OS shutdown timeout.
            try { task?.Wait(TimeSpan.FromSeconds(3)); } catch { }

            var source = traceEventSession.Source;
            session.StartTime = source.SessionStartTime;
            session.SessionEndTimeRelativeMSec = source.SessionEndTimeRelativeMSec;

            session.FinalizeSession();

            // Atomic write: serialize to a temp file then rename, so a kill
            // mid-serialize never leaves a partially-written .beetle.
            var tmp = logFilePath + ".tmp";
            try
            {
                SessionSerializer.Save(session, tmp);
                if (File.Exists(logFilePath))
                {
                    File.Delete(logFilePath);
                }

                File.Move(tmp, logFilePath);
            }
            catch
            {
                try { SessionSerializer.Save(session, logFilePath); } catch { }
            }

            Console.WriteLine($"Wrote {logFilePath}");
        }
        catch
        {
        }
    }

    public static void StopExistingSessions()
    {
        var sessions = TraceEventSession.GetActiveSessionNames();
        foreach (var sessionName in sessions)
        {
            if (sessionName.StartsWith(SessionPrefix))
            {
                using var session = TraceEventSession.GetActiveSession(sessionName);
                Console.WriteLine($"Stopping session: {sessionName}");
                try
                {
                    session.Stop(noThrow: true);
                }
                catch
                {
                }
            }
        }
    }

    private static bool? isAdmin;
    public static bool IsAdmin
    {
        get
        {
            if (isAdmin == null)
            {
                isAdmin = Environment.IsPrivilegedProcess;
            }

            return isAdmin.Value;
        }
    }
}
