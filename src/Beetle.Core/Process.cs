using System;
using System.Collections.Generic;
using System.IO;

namespace GuiLabs.Dotnet.Recorder;

public class Process
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public double ParentStartTimeRelativeMSec { get; set; }
    public string ParentImageFileName { get; set; }
    public string FilePath { get; set; }
    public string ImageFileName { get; set; }
    public string CommandLine { get; set; }
    public double StartTimeRelativeMSec { get; set; }
    public double StopTimeRelativeMSec { get; set; }
    public int ExitCode { get; set; }

    public AddressSpace<Image> NativeImages { get; } = new();
    public List<Module> Modules { get; } = new();
    public List<ExceptionEvent> Exceptions { get; } = new();
    public CallStacks CallStacks { get; } = new();

    // populated during finalization
    public AddressSpace<Method> JittedMethods { get; } = new();

    // temporary and used during live recording
    private Dictionary<int, ExceptionEvent> exceptionsOnThreads = new();
    private Dictionary<long, Method> methodsById = new();
    private Dictionary<long, Module> modulesById = new();

    public void ModuleLoad(
        long moduleId,
        string moduleILFileName,
        double timeStampRelativeMSec,
        Guid managedPdbSignature)
    {
        var module = new Module();
        module.FilePath = moduleILFileName;
        module.Timestamp = timeStampRelativeMSec;
        module.PdbGuid = managedPdbSignature;
        modulesById[moduleId] = module;
        AddModule(module);
    }

    public void AddModule(Module module)
    {
        module.Name = Path.GetFileNameWithoutExtension(module.FilePath);
        Modules.Add(module);
    }

    public void ImageLoad(string imageFilePath, ulong imageBase, int imageSize)
    {
        var image = new Image
        {
            FilePath = imageFilePath,
            StartAddress = imageBase,
            Size = imageSize
        };

        AddImage(image);

        if (FilePath == null &&
            ImageFileName != null &&
            imageFilePath.EndsWith(ImageFileName, StringComparison.OrdinalIgnoreCase))
        {
            FilePath = imageFilePath;
        }
    }

    public void AddImage(Image image)
    {
        NativeImages.Add(image);
    }

    public void Exception(
        string exceptionType,
        string exceptionMessage,
        int threadId,
        double timeStampRelativeMSec,
        DateTime timeStamp)
    {
        var exception = new ExceptionEvent
        {
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            TimestampMS = timeStampRelativeMSec,
            Timestamp = timeStamp,
            ThreadId = threadId
        };

        AddException(exception);

        exceptionsOnThreads[threadId] = exception;
    }

    public void AddException(ExceptionEvent exception)
    {
        Exceptions.Add(exception);
    }

    public void CallStack(int threadId, int frameCount, int pointerSize, nint dataStart)
    {
        if (!exceptionsOnThreads.TryGetValue(threadId, out var exception))
        {
            return;
        }

        exceptionsOnThreads.Remove(threadId);

        var callStackIndex = CallStacks.GetStackIndexForStackEvent(dataStart, frameCount, pointerSize);
        exception.CallStackIndex = callStackIndex;
    }

    public void MethodLoad(
        long methodID,
        long moduleID,
        int methodToken,
        ulong methodStartAddress,
        int methodSize,
        string methodNamespace,
        string methodName)
    {
        if (!modulesById.TryGetValue(moduleID, out var module))
        {
            return;
        }

        var method = module.MethodLoad(
            methodToken,
            methodStartAddress,
            methodSize,
            methodNamespace,
            methodName);

        methodsById[methodID] = method;
    }

    public void MethodILToNativeMap(long methodId, (int il, int native)[] array)
    {
        if (methodsById.TryGetValue(methodId, out var method))
        {
            method.ILToNativeMap = array;
        }
    }
}