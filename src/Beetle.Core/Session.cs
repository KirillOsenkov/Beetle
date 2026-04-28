using System;
using System.Collections.Generic;
using System.IO;

namespace GuiLabs.Dotnet.Recorder;

public class Session
{
    private List<Process> processes = new();
    private Dictionary<int, Process> currentProcesses = new();
    public Symbols Symbols { get; } = new();

    public IReadOnlyList<Process> Processes => processes;

    public DateTime StartTime { get; set; }
    public double SessionEndTimeRelativeMSec { get; set; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(SessionEndTimeRelativeMSec);
    public DateTime EndTime => StartTime + Duration;
    public int EventsLost { get; set; }

    public Process ProcessStart(
        int id,
        int parentId,
        string commandLine,
        string imageFileName,
        double timeStampRelativeMSec)
    {
        string parentImageFileName = null;
        double parentStartTime = 0;
        if (currentProcesses.TryGetValue(parentId, out var parentProcess))
        {
            parentImageFileName = parentProcess.ImageFileName;
            parentStartTime = parentProcess.StartTimeRelativeMSec;
        }

        var process = new Process()
        {
            Id = id,
            ParentId = parentId,
            ParentImageFileName = parentImageFileName,
            ParentStartTimeRelativeMSec = parentStartTime,
            CommandLine = commandLine,
            ImageFileName = imageFileName,
            StartTimeRelativeMSec = timeStampRelativeMSec
        };
        currentProcesses[id] = process;
        processes.Add(process);

        return process;
    }

    public void AddProcess(Process process)
    {
        processes.Add(process);
    }

    public void ProcessStop(int id, double timeStampRelativeMSec, int exitCode)
    {
        if (currentProcesses.TryGetValue(id, out var process))
        {
            process.StopTimeRelativeMSec = timeStampRelativeMSec;
            process.ExitCode = exitCode;
        }
    }

    public Process GetOrCreateProcess(int id)
    {
        if (!currentProcesses.TryGetValue(id, out var process))
        {
            process = ProcessStart(id, 0, null, null, default);
        }

        return process;
    }

    public void FinalizeSession(bool deserializing = false)
    {
        foreach (var process in processes)
        {
            if (!deserializing && process.StopTimeRelativeMSec == 0)
            {
                process.StopTimeRelativeMSec = SessionEndTimeRelativeMSec;
            }

            foreach (var module in process.Modules)
            {
                Symbols.LoadSymbols(module);

                foreach (var method in module.Methods)
                {
                    process.JittedMethods.Add(method);
                    if (!deserializing)
                    {
                        method.SortILToNativeMap();
                    }
                }
            }

            process.JittedMethods.Sort();
            process.NativeImages.Sort();
        }

        //foreach (var process in processes)
        //{
        //    foreach (var exception in process.Exceptions)
        //    {
        //        ComputeStackTrace(process, exception);
        //    }
        //}
    }

    public string ComputeStackTrace(Process process, ExceptionEvent exception)
    {
        if (exception.CallStackIndex < 0)
        {
            return "";
        }

        var sw = new StringWriter();
        sw.WriteLine($"{exception.ExceptionType}: {exception.ExceptionMessage}");
        sw.WriteLine();

        var stack = process.CallStacks.GetCallStack(exception.CallStackIndex);
        foreach (var nativeAddress in stack)
        {
            string frameText;

            var method = process.JittedMethods.FindSpan(nativeAddress);
            if (method != null)
            {
                frameText = $"{Path.GetFileName(method.Module.FilePath)}: {method.Namespace}.{method.Name}";

                if (method.ILToNativeMap != null)
                {
                    string sourceLocation = null;
                    int ilOffset = method.LookupILOffset(nativeAddress);

                    if (method.Module.Symbols is { } symbols)
                    {
                        if (symbols.GetSequencePoints(method.Token) is { } sequencePoints)
                        {
                            var sequencePoint = SequencePoint.Find(sequencePoints, ilOffset);
                            if (sequencePoint.FilePath is string filePath)
                            {
                                sourceLocation = $"in {filePath} ({sequencePoint.StartLine}, {sequencePoint.StartColumn})";
                            }
                        }
                    }

                    if (sourceLocation == null)
                    {
                        sourceLocation = $"0x{ilOffset:x}";
                    }

                    frameText = $"{frameText} {sourceLocation}";
                }
            }
            else
            {
                frameText = $"0x{nativeAddress:x}";

                var image = process.NativeImages.FindSpan(nativeAddress);
                if (image != null && image.FilePath != null)
                {
                    frameText = $"{Path.GetFileName(image.FilePath)}: {frameText}";
                }
            }

            sw.WriteLine(frameText);
        }

        return sw.ToString();
    }
}
