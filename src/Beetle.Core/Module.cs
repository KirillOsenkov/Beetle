using System;
using System.Collections.Generic;

namespace GuiLabs.Dotnet.Recorder;

public class Module
{
    public string FilePath;
    public string Name;
    public double Timestamp;
    public Guid PdbGuid;

    public ModuleSymbols Symbols;

    private List<Method> methods = new();

    public IReadOnlyList<Method> Methods => methods;

    public Method MethodLoad(
        int methodToken,
        ulong methodStartAddress,
        int methodSize,
        string methodNamespace,
        string methodName)
    {
        var method = new Method()
        {
            Token = methodToken,
            StartAddress = methodStartAddress,
            Size = methodSize,
            Namespace = methodNamespace,
            Name = methodName
        };

        AddMethod(method);

        return method;
    }

    public void AddMethod(Method method)
    {
        method.Module = this;
        methods.Add(method);
    }
}
