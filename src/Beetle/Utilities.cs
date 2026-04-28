using System;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GuiLabs.Dotnet.Recorder;

public static class SessionExtensions
{
    public static void SubscribeToEtwEvents(this Session session, ETWTraceEventSource etwSource, TraceEventSession traceEventSession = null)
    {
        etwSource.Kernel.ProcessStartGroup += e =>
        {
            session.ProcessStart(
                e.ProcessID,
                e.ParentID,
                e.CommandLine,
                e.ImageFileName,
                e.TimeStampRelativeMSec);
            UpdateEventsLost(session, traceEventSession);
        };

        etwSource.Kernel.ProcessEndGroup += e =>
        {
            session.ProcessStop(e.ProcessID, e.TimeStampRelativeMSec, e.ExitStatus);
        };

        etwSource.Kernel.ImageGroup += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            process.ImageLoad(e.FileName, e.ImageBase, e.ImageSize);
            UpdateEventsLost(session, traceEventSession);
        };

        etwSource.Clr.LoaderModuleLoad += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            process.ModuleLoad(
                e.ModuleID,
                e.ModuleILPath,
                e.TimeStampRelativeMSec,
                e.ManagedPdbSignature);
        };

        etwSource.Clr.ExceptionStart += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            process.Exception(
                e.ExceptionType,
                e.ExceptionMessage,
                e.ThreadID,
                e.TimeStampRelativeMSec,
                e.TimeStamp);
        };

        etwSource.Clr.MethodLoadVerbose += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            process.MethodLoad(
                e.MethodID,
                e.ModuleID,
                e.MethodToken,
                e.MethodStartAddress,
                e.MethodSize,
                e.MethodNamespace,
                e.MethodName);
        };

        etwSource.Clr.MethodILToNativeMap += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            var count = e.CountOfMapEntries;
            var array = new (int il, int native)[count];
            for (int i = 0; i < count; i++)
            {
                var ilOffset = e.ILOffset(i);
                var nativeOffset = e.NativeOffset(i);
                array[i] = (ilOffset, nativeOffset);
            }

            process.MethodILToNativeMap(e.MethodID, array);
        };

        etwSource.Clr.ClrStackWalk += e =>
        {
            var process = session.GetOrCreateProcess(e.ProcessID);
            process.CallStack(e.ThreadID, e.FrameCount, e.PointerSize, e.DataStart + 8);
        };
    }

    private static DateTime lastUpdated = DateTime.MinValue;

    private static void UpdateEventsLost(Session session, TraceEventSession traceEventSession)
    {
        if (traceEventSession == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var intervalSinceLastUpdated = now - lastUpdated;
        if (intervalSinceLastUpdated < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lastUpdated = now;

        try
        {
            session.EventsLost = traceEventSession.EventsLost;
        }
        catch
        {
        }
    }

    public static Session ReadFromEtl(string etlFile)
    {
        if (etlFile.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
        {
            var zipReader = new ZippedETLReader(etlFile);
            zipReader.UnpackArchive();
            etlFile = zipReader.EtlFileName;
        }

        using var etwSource = new ETWTraceEventSource(etlFile);

        var session = new Session();

        session.SubscribeToEtwEvents(etwSource);

        session.EventsLost = etwSource.EventsLost;

        etwSource.Process();

        session.StartTime = etwSource.SessionStartTime;
        session.SessionEndTimeRelativeMSec = etwSource.SessionEndTimeRelativeMSec;

        session.FinalizeSession();

        return session;
    }
}