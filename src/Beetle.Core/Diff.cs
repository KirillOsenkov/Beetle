using System;
using System.Linq;

namespace GuiLabs.Dotnet.Recorder;

public static class SessionDiff
{
    public static void Diff(Session left, Session right, Action<string> log = null)
    {
        Diff(left.StartTime, right.StartTime, log);
        Diff(left.SessionEndTimeRelativeMSec, right.SessionEndTimeRelativeMSec, log);
        Diff(left.EventsLost, right.EventsLost, log);

        Diff(left.Processes.Count, right.Processes.Count, log);

        for (int i = 0; i < left.Processes.Count; i++)
        {
            Diff(left.Processes[i], right.Processes[i], log);
        }

        var leftSymbols = left.Symbols.EnumerateSymbols().ToArray();
        var rightSymbols = right.Symbols.EnumerateSymbols().ToArray();
        Diff(leftSymbols.Length, rightSymbols.Length, log);
        for (int i = 0; i < leftSymbols.Length; i++)
        {
            Diff(leftSymbols[i], rightSymbols[i], log);
        }
    }

    private static void Diff(Process left, Process right, Action<string> log)
    {
        Diff(left.Id, right.Id, log);
        Diff(left.ParentId, right.ParentId, log);
        Diff(left.ParentStartTimeRelativeMSec, right.ParentStartTimeRelativeMSec, log);
        Diff(left.ParentImageFileName, right.ParentImageFileName, log);
        Diff(left.CommandLine, right.CommandLine, log);
        Diff(left.FilePath, right.FilePath, log);
        Diff(left.ImageFileName, right.ImageFileName, log);
        Diff(left.StartTimeRelativeMSec, right.StartTimeRelativeMSec, log);
        Diff(left.StopTimeRelativeMSec, right.StopTimeRelativeMSec, log);
        Diff(left.ExitCode, right.ExitCode, log);

        Diff(left.Modules.Count, right.Modules.Count, log);
        for (int i = 0; i < left.Modules.Count; i++)
        {
            Diff(left.Modules[i], right.Modules[i], log);
        }

        Diff(left.NativeImages.Count, right.NativeImages.Count, log);
        for (int i = 0; i < left.NativeImages.Count; i++)
        {
            Diff(left.NativeImages[i], right.NativeImages[i], log);
        }

        Diff(left.Exceptions.Count, right.Exceptions.Count, log);
        for (int i = 0; i < left.Exceptions.Count; i++)
        {
            Diff(left.Exceptions[i], right.Exceptions[i], log);
        }

        Diff(left.CallStacks.Count, right.CallStacks.Count, log);
        for (int i = 0; i < left.CallStacks.Count; i++)
        {
            Diff(left.CallStacks[i].address, right.CallStacks[i].address, log);
            Diff(left.CallStacks[i].callerIndex, right.CallStacks[i].callerIndex, log);
        }
    }

    private static void Diff(Module left, Module right, Action<string> log)
    {
        Diff(left.FilePath, right.FilePath, log);
        Diff(left.Timestamp, right.Timestamp, log);
        Diff(left.PdbGuid, right.PdbGuid, log);

        Diff(left.Methods.Count, right.Methods.Count, log);

        for (int i = 0; i < left.Methods.Count; i++)
        {
            Diff(left.Methods[i], right.Methods[i], log);
        }
    }

    private static void Diff(Image left, Image right, Action<string> log)
    {
        Diff(left.FilePath, right.FilePath, log);
        Diff(left.StartAddress, right.StartAddress, log);
        Diff(left.Size, right.Size, log);
    }

    private static void Diff(ExceptionEvent left, ExceptionEvent right, Action<string> log)
    {
        Diff(left.ExceptionType, right.ExceptionType, log);
        Diff(left.ExceptionMessage, right.ExceptionMessage, log);
        Diff(left.CallStackIndex, right.CallStackIndex, log);
        Diff(left.ThreadId, right.ThreadId, log);
        Diff(left.Timestamp, right.Timestamp, log);
        Diff(left.TimestampMS, right.TimestampMS, log);
    }

    private static void Diff(Method left, Method right, Action<string> log)
    {
        Diff(left.Token, right.Token, log);
        Diff(left.StartAddress, right.StartAddress, log);
        Diff(left.Size, right.Size, log);
        Diff(left.Namespace, right.Namespace, log);
        Diff(left.Name, right.Name, log);
        Diff(left.ILToNativeMap, right.ILToNativeMap, log);
    }

    private static void Diff((int il, int native)[] left, (int il, int native)[] right, Action<string> log)
    {
        Diff(left != null, right != null, log);
        if (left != null)
        {
            for (int i = 0; i < left.Length; i++)
            {
                Diff(left[i].il, right[i].il, log);
                Diff(left[i].native, right[i].native, log);
            }
        }
    }

    private static void Diff(ModuleSymbols left, ModuleSymbols right, Action<string> log)
    {
        Diff(left.PdbGuid, right.PdbGuid, log);

        var leftPoints = left.SequencePointsPerMethodToken.OrderBy(k => k.Key).ToArray();
        var rightPoints = right.SequencePointsPerMethodToken.OrderBy(k => k.Key).ToArray();
        Diff(leftPoints.Length, rightPoints.Length, log);
        for (int i = 0; i < leftPoints.Length; i++)
        {
            Diff(leftPoints[i].Key, rightPoints[i].Key, log);

            var leftPointList = leftPoints[i].Value;
            var rightPointList = rightPoints[i].Value;

            Diff(leftPointList.Count, rightPointList.Count, log);
            for (int j = 0; j < leftPointList.Count; j++)
            {
                var leftPoint = leftPointList[j];
                var rightPoint = rightPointList[j];

                Diff(leftPoint.FilePath, rightPoint.FilePath, log);
                Diff(leftPoint.ILOffset, rightPoint.ILOffset, log);
                Diff(leftPoint.StartLine, rightPoint.StartLine, log);
                Diff(leftPoint.StartColumn, rightPoint.StartColumn, log);
                Diff(leftPoint.EndLine, rightPoint.EndLine, log);
                Diff(leftPoint.EndColumn, rightPoint.EndColumn, log);
            }
        }
    }

    private static void Diff<T>(T left, T right, Action<string> log) where T : IEquatable<T>
    {
        if (left == null && right == null)
        {
            return;
        }

        if (left == null || right == null)
        {
            log?.Invoke($"{left} != {right}");
            return;
        }

        if (!left.Equals(right))
        {
            log?.Invoke($"{left} != {right}");
        }
    }
}