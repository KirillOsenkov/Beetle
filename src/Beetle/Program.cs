using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            if (arg == "start")
            {
                if (args.Length > 1)
                {
                    logFilePath = args[1];
                }

                Start(logFilePath);
                return;
            }
            else if (arg == "stop")
            {
                Stop();
                return;
            }
            else if (arg.EndsWith(".beetle", StringComparison.OrdinalIgnoreCase))
            {
                logFilePath = arg;
            }
            else if (arg == "-?" || arg == "/?" || arg == "-h" || arg == "/h" || arg == "help" || arg == "-help" || arg == "/help")
            {
                Console.WriteLine(
                    """
                    Usage: beetle.exe [<log-file>.beetle]
                           Starts recording, press Ctrl+C to stop, optional log path defaults to %TEMP%\AllExceptions.beetle.

                           beetle.exe start [<log-file>.beetle]
                           Starts recording and returns immediately, while another instance of beetle.exe continues recording.

                           beetle.exe stop
                           Finds an existing instance of beetle.exe and stops recording, waits for it to finish
                           writing the log, then returns.                   
                    """
                    );
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

        StopExistingSessions();

        using var traceEventSession = new TraceEventSession($"{SessionPrefix}");
        traceEventSession.BufferSizeMB = 4096;

        if (IsAdmin)
        {
            traceEventSession.EnableKernelProvider(
                KernelTraceEventParser.Keywords.ImageLoad |
                KernelTraceEventParser.Keywords.Process,
                stackCapture: KernelTraceEventParser.Keywords.None);
        }

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

        Console.WriteLine("Press Ctrl+C to stop recording");

        task.Wait();

        session.StartTime = etwSource.SessionStartTime;
        session.SessionEndTimeRelativeMSec = etwSource.SessionEndTimeRelativeMSec;

        session.FinalizeSession();
        SessionSerializer.Save(session, logFilePath);

        Console.WriteLine($"Wrote {logFilePath}");
    }

    public static void Start(string filePath)
    {
        var executable = CurrentExecutable;
        var arguments = filePath;
        var psi = new System.Diagnostics.ProcessStartInfo(executable, arguments);
        if (!IsAdmin)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }

        System.Diagnostics.Process.Start(psi);
    }

    public static System.Diagnostics.Process StartProcessAsAdmin(string arguments = "")
    {
        var psi = new System.Diagnostics.ProcessStartInfo(CurrentExecutable, arguments);
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        var process = System.Diagnostics.Process.Start(psi);
        return process;
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

        var executable = currentProcess.ProcessName;

        var allProcesses = System.Diagnostics.Process.GetProcesses();

        foreach (var process in allProcesses)
        {
            try
            {
                var id = process.Id;
                if (id == 0 || id == 4 || id == currentProcessId || id == parentId)
                {
                    continue;
                }

                if (process.ProcessName.Equals(executable, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
            }
        }

        return null;
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

        if (status != 0)
            throw new Win32Exception($"NtQueryInformationProcess failed with status 0x{status:X}");

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

    public static void StopExistingSessions()
    {
        var sessions = TraceEventSession.GetActiveSessionNames();
        foreach (var sessionName in sessions)
        {
            if (sessionName.StartsWith(SessionPrefix))
            {
                var session = TraceEventSession.GetActiveSession(sessionName);
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
