using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace GuiLabs.Dotnet.Recorder;

public class Symbols
{
    private Dictionary<Guid, ModuleSymbols> pdbs = new();

    public int Count => pdbs.Count;

    public IEnumerable<ModuleSymbols> EnumerateSymbols() => pdbs.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value);

    public void LoadSymbols(Module module)
    {
        var filePath = module.FilePath;
        var pdbGuid = module.PdbGuid;

        if (pdbs.TryGetValue(pdbGuid, out var moduleSymbols))
        {
            module.Symbols = moduleSymbols;
            return;
        }

        try
        {
            moduleSymbols = GetModuleSymbols(filePath);
        }
        catch
        {
            pdbs[pdbGuid] = null;
            return;
        }

        if (moduleSymbols != null && moduleSymbols.PdbGuid == module.PdbGuid)
        {
            pdbs[pdbGuid] = moduleSymbols;
            module.Symbols = moduleSymbols;
        }
    }

    public void Add(ModuleSymbols symbols)
    {
        pdbs[symbols.PdbGuid] = symbols;
    }

    public ModuleSymbols GetModuleSymbols(string moduleFilePath)
    {
        if (!File.Exists(moduleFilePath))
        {
            return null;
        }

        using var stream = File.OpenRead(moduleFilePath);
        using var peReader = new PEReader(stream);

        ModuleSymbols moduleSymbols = null;
        CodeViewDebugDirectoryData codeView = default;

        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                var metadataReaderProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                moduleSymbols = ReadPortablePdb(metadataReaderProvider);
            }
            else if (entry.Type == DebugDirectoryEntryType.CodeView)
            {
                codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
            }
        }

        if (moduleSymbols == null && codeView.Path is string pdbFilePath)
        {
            moduleSymbols = TryReadPortablePdb(pdbFilePath, codeView.Guid);
            if (moduleSymbols == null)
            {
                pdbFilePath = Path.ChangeExtension(moduleFilePath, ".pdb");
                moduleSymbols = TryReadPortablePdb(pdbFilePath, codeView.Guid);
            }
        }

        return moduleSymbols;
    }

    public static ModuleSymbols TryReadPortablePdb(string filePath, Guid guid)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        using var stream = File.OpenRead(filePath);

        // BSJB
        if (stream.ReadByte() != 0x42 ||
            stream.ReadByte() != 0x53 ||
            stream.ReadByte() != 0x4A ||
            stream.ReadByte() != 0x42)
        {
            return null;
        }

        stream.Position = 0;

        try
        {
            var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var result = ReadPortablePdb(provider, expectedGuid: guid);
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static ModuleSymbols ReadPortablePdb(MetadataReaderProvider metadataReaderProvider, Guid expectedGuid = default)
    {
        var moduleSymbols = new ModuleSymbols();
        var metadataReader = metadataReaderProvider.GetMetadataReader();
        var header = metadataReader.DebugMetadataHeader;
        var id = header.Id;
        if (id.Length >= 16)
        {
            var guid = new Guid(id.Slice(0, 16).ToArray());
            moduleSymbols.PdbGuid = guid;

            if (expectedGuid != default && expectedGuid != guid)
            {
                return null;
            }
        }

        var methodDebugInformations = metadataReader.MethodDebugInformation.ToArray();
        foreach (var methodDebugInformationHandle in methodDebugInformations)
        {
            var methodDebugInformation = metadataReader.GetMethodDebugInformation(methodDebugInformationHandle);
            var methodDefinitionHandle = methodDebugInformationHandle.ToDefinitionHandle();

            int methodToken = MetadataTokens.GetToken(methodDefinitionHandle);

            var documentHandle = methodDebugInformation.Document;

            var sequencePointCollection = methodDebugInformation.GetSequencePoints();
            var sequencePoints = new List<SequencePoint>();
            foreach (var sequencePoint in sequencePointCollection)
            {
                if (sequencePoint.IsHidden)
                {
                    continue;
                }

                var document = metadataReader.GetDocument(sequencePoint.Document.IsNil ? documentHandle : sequencePoint.Document);
                var documentFilePath = metadataReader.GetString(document.Name);
                var sequencePointInfo = new SequencePoint()
                {
                    FilePath = documentFilePath,
                    ILOffset = sequencePoint.Offset,
                    StartLine = sequencePoint.StartLine,
                    StartColumn = sequencePoint.StartColumn,
                    EndLine = sequencePoint.EndLine,
                    EndColumn = sequencePoint.EndColumn
                };
                sequencePoints.Add(sequencePointInfo);
            }

            moduleSymbols.AddSequencePoints(methodToken, sequencePoints);
        }

        return moduleSymbols;
    }
}
