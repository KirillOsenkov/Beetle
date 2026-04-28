using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GuiLabs.Dotnet.Recorder;

// Typical Stats
// strings 18,923,125
// symbols 32,662,279
// processes 112,619,971
// modules 100,425,447
// callstacks 4,298,406
// images 4,654,563
// exceptions 3,133,144

// Version 2
// strings 18,555,535
// symbols 36,158,392
// processes 49,298,364
// modules 33,961,198
// callstacks 3,565,477
// images 8,539,703
// exceptions 3,078,547

public class SessionSerializer : BinarySerializer
{
    public const int LatestFileFormatVersion = 2;
    public const string FileExtension = ".beetle";
    public const bool GZipCompress = true;

    public int FileVersion;
    protected Session session;
    protected StringTable stringTable = new();
    protected string[] strings;
    private Dictionary<ILToNativeMap, int> ilToNativeMapIndices = new();
    private (int il, int native)[][] ilToNativeMaps;

    public static void Save(Session session, string filePath, int fileVersion = LatestFileFormatVersion)
    {
        var sessionWriter = new SessionWriter(session, filePath, fileVersion);
        sessionWriter.Write();
    }

    public static Session Load(string filePath)
    {
        var sessionReader = new SessionReader(filePath);
        var session = sessionReader.Read();

        session.FinalizeSession(deserializing: true);

        return session;
    }

    private void PopulateStringTable()
    {
        foreach (var process in session.Processes)
        {
            AddString(process.CommandLine);
            AddString(process.FilePath);
            AddString(process.ImageFileName);
            AddString(process.ParentImageFileName);

            foreach (var module in process.Modules)
            {
                AddString(module.FilePath);

                foreach (var method in module.Methods)
                {
                    AddString(method.Namespace);
                    AddString(method.Name);
                }
            }

            foreach (var image in process.NativeImages)
            {
                AddString(image.FilePath);
            }

            foreach (var exception in process.Exceptions)
            {
                AddString(exception.ExceptionType);
                AddString(exception.ExceptionMessage);
            }
        }

        foreach (var moduleSymbols in session.Symbols.EnumerateSymbols())
        {
            foreach (var kvp in moduleSymbols.SequencePointsPerMethodToken)
            {
                foreach (var sequencePoint in kvp.Value)
                {
                    AddString(sequencePoint.FilePath);
                }
            }
        }

        stringTable.Seal();

        void AddString(string text)
        {
            stringTable.AddString(text);
        }
    }

    public void WriteSession()
    {
        PopulateStringTable();

        writer.Write(FileVersion);
        WriteStringTable();
        WriteHeader();
        if (FileVersion > 1)
        {
            WriteILToNativeMaps();
        }

        WriteProcesses();
        WriteSymbols();

        writer.Write("The end");
    }

    private void WriteILToNativeMaps()
    {
        var maps = new List<(int il, int native)[]>();
        maps.Add([]);

        foreach (var process in session.Processes)
        {
            foreach (var module in process.Modules)
            {
                foreach (var method in module.Methods)
                {
                    if (method.ILToNativeMap is { } map && map.Length > 0)
                    {
                        var mapStruct = new ILToNativeMap(map);
                        if (!ilToNativeMapIndices.TryGetValue(mapStruct, out int index))
                        {
                            maps.Add(map);
                            index = maps.Count - 1;
                            ilToNativeMapIndices[mapStruct] = index;
                        }
                    }
                }
            }
        }

        Write(maps.Count);
        for (int i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            Write(map.Length);

            for (int j = 0; j < map.Length; j++)
            {
                Write(map[j].il);
                Write(map[j].native);
            }
        }
    }

    private void ReadILToNativeMaps()
    {
        int count = Read7BitEncodedInt();
        ilToNativeMaps = new (int il, int native)[count][];

        for (int i = 0; i < count; i++)
        {
            int length = Read7BitEncodedInt();
            var array = new (int il, int native)[length];
            for (int j = 0; j < length; j++)
            {
                int il = Read7BitEncodedInt();
                int native = Read7BitEncodedInt();
                array[j] = (il, native);
            }

            ilToNativeMaps[i] = array;
        }
    }

    private int GetILToNativeMapIndex(Method method)
    {
        if (method.ILToNativeMap == null || method.ILToNativeMap.Length == 0)
        {
            return 0;
        }

        var mapStruct = new ILToNativeMap(method.ILToNativeMap);
        if (ilToNativeMapIndices.TryGetValue(mapStruct, out int index))
        {
            return index;
        }

        return 0;
    }

    public void ReadSession()
    {
        FileVersion = reader.ReadInt32();
        if (FileVersion > LatestFileFormatVersion)
        {
            return;
        }

        ReadStringTable();
        ReadHeader();
        if (FileVersion > 1)
        {
            ReadILToNativeMaps();
        }

        ReadProcesses();
        ReadSymbols();

        string theEnd = reader.ReadString();
    }

    private void WriteStringTable()
    {
        Write(stringTable.Count);

        foreach (var text in stringTable.EnumerateStrings())
        {
            Write(text);
        }
    }

    private void ReadStringTable()
    {
        int count = Read7BitEncodedInt() + 2;

        strings = new string[count];
        strings[0] = null;
        strings[1] = "";

        for (int i = 2; i < count; i++)
        {
            strings[i] = ReadString();
        }
    }

    public void WriteHeader()
    {
        Write(session.StartTime);
        writer.Write(session.SessionEndTimeRelativeMSec);
        Write(session.EventsLost);
    }

    public void ReadHeader()
    {
        session.StartTime = ReadTimestamp();
        session.SessionEndTimeRelativeMSec = reader.ReadDouble();
        session.EventsLost = Read7BitEncodedInt();
    }

    private void WriteProcesses()
    {
        int count = session.Processes.Count;
        Write(count);

        for (int i = 0; i < count; i++)
        {
            Write(session.Processes[i]);
        }
    }

    private void ReadProcesses()
    {
        int count = Read7BitEncodedInt();

        for (int i = 0; i < count; i++)
        {
            ReadProcess();
        }
    }

    private void Write(Process process)
    {
        Write(process.Id);
        Write(process.ParentId);
        Write(process.ParentStartTimeRelativeMSec);
        WriteStringFromTable(process.ParentImageFileName);
        WriteStringFromTable(process.CommandLine);
        WriteStringFromTable(process.FilePath);
        WriteStringFromTable(process.ImageFileName);
        Write(process.StartTimeRelativeMSec);
        Write(process.StopTimeRelativeMSec);
        Write(process.ExitCode);

        int moduleCount = process.Modules.Count;
        Write(moduleCount);

        foreach (var module in process.Modules)
        {
            Write(module);
        }

        int imageCount = process.NativeImages.Count;
        Write(imageCount);

        foreach (var image in process.NativeImages)
        {
            Write(image);
        }

        int exceptionCount = process.Exceptions.Count;
        Write(exceptionCount);

        foreach (var exception in process.Exceptions)
        {
            Write(exception);
        }

        WriteCallStacks(process);
    }

    private void ReadProcess()
    {
        var process = new Process();
        process.Id = Read7BitEncodedInt();
        process.ParentId = Read7BitEncodedInt();
        process.ParentStartTimeRelativeMSec = ReadDouble();
        process.ParentImageFileName = ReadStringFromTable();
        process.CommandLine = ReadStringFromTable();
        process.FilePath = ReadStringFromTable();
        process.ImageFileName = ReadStringFromTable();
        process.StartTimeRelativeMSec = ReadDouble();
        process.StopTimeRelativeMSec = ReadDouble();
        process.ExitCode = Read7BitEncodedInt();

        int moduleCount = Read7BitEncodedInt();
        for (int i = 0; i < moduleCount; i++)
        {
            ReadModule(process);
        }

        int imageCount = Read7BitEncodedInt();
        for (int i = 0; i < imageCount; i++)
        {
            ReadImage(process);
        }

        int exceptionCount = Read7BitEncodedInt();
        for (int i = 0; i < exceptionCount; i++)
        {
            ReadException(process);
        }

        ReadCallStacks(process);

        session.AddProcess(process);
    }

    private void Write(Module module)
    {
        WriteStringFromTable(module.FilePath);
        Write(module.Timestamp);
        Write(module.PdbGuid);

        int methodCount = module.Methods.Count;
        Write(methodCount);

        foreach (var method in module.Methods)
        {
            Write(method);
        }
    }

    private void ReadModule(Process process)
    {
        var module = new Module();
        module.FilePath = ReadStringFromTable();
        module.Timestamp = ReadDouble();
        module.PdbGuid = ReadGuid();

        int methodCount = Read7BitEncodedInt();
        for (int i = 0; i < methodCount; i++)
        {
            ReadMethod(module);
        }

        process.AddModule(module);
    }

    private void Write(Method method)
    {
        Write(method.Token);
        Write(method.StartAddress);
        Write(method.Size);
        WriteStringFromTable(method.Namespace);
        WriteStringFromTable(method.Name);

        if (FileVersion == 1)
        {
            var map = method.ILToNativeMap;
            int mapCount = map != null ? map.Length : 0;
            Write(mapCount);

            for (int i = 0; i < mapCount; i++)
            {
                Write(map[i].il);
                Write(map[i].native);
            }
        }
        else
        {
            int index = GetILToNativeMapIndex(method);
            Write(index);
        }
    }

    private void ReadMethod(Module module)
    {
        var method = new Method();
        method.Token = Read7BitEncodedInt();
        method.StartAddress = reader.ReadUInt64();
        method.Size = Read7BitEncodedInt();
        method.Namespace = ReadStringFromTable();
        method.Name = ReadStringFromTable();

        if (FileVersion == 1)
        {
            int mapCount = Read7BitEncodedInt();
            (int il, int native)[] map = new (int il, int native)[mapCount];

            for (int i = 0; i < mapCount; i++)
            {
                int il = Read7BitEncodedInt();
                int native = Read7BitEncodedInt();
                map[i] = (il, native);
            }

            method.ILToNativeMap = map;
        }
        else
        {
            int index = Read7BitEncodedInt();
            if (index >= 0 && index < ilToNativeMaps.Length)
            {
                method.ILToNativeMap = ilToNativeMaps[index];
            }
        }

        module.AddMethod(method);
    }

    private void Write(Image image)
    {
        WriteStringFromTable(image.FilePath);
        Write(image.StartAddress);
        Write(image.Size);
    }

    private void ReadImage(Process process)
    {
        var image = new Image();
        image.FilePath = ReadStringFromTable();
        image.StartAddress = reader.ReadUInt64();
        image.Size = Read7BitEncodedInt();
        process.AddImage(image);
    }

    private void Write(ExceptionEvent exception)
    {
        WriteStringFromTable(exception.ExceptionType);
        WriteStringFromTable(exception.ExceptionMessage);
        Write((int)exception.CallStackIndex);
        Write(exception.ThreadId);
        Write(exception.Timestamp);
        Write(exception.TimestampMS);
    }

    private void ReadException(Process process)
    {
        var exception = new ExceptionEvent();
        exception.ExceptionType = ReadStringFromTable();
        exception.ExceptionMessage = ReadStringFromTable();
        exception.CallStackIndex = Read7BitEncodedInt();
        exception.ThreadId = Read7BitEncodedInt();
        exception.Timestamp = ReadTimestamp();
        exception.TimestampMS = ReadDouble();
        process.AddException(exception);
    }

    private void WriteCallStacks(Process process)
    {
        int count = process.CallStacks.Count;
        Write(count);

        foreach (var stack in process.CallStacks.EnumerateCallStacks())
        {
            Write(stack.address);
            Write(stack.callerIndex);
        }
    }

    private void ReadCallStacks(Process process)
    {
        int count = Read7BitEncodedInt();
        var stacks = process.CallStacks;
        stacks.Initialize(count);

        for (int i = 0; i < count; i++)
        {
            ulong address = reader.ReadUInt64();
            int callerIndex = Read7BitEncodedInt();
            stacks.AddStack(address, callerIndex);
        }
    }

    protected void WriteStringFromTable(string text)
    {
        int index = stringTable.GetStringIndex(text);
        Write(index);
    }

    private void WriteSymbols()
    {
        Write(session.Symbols.Count);

        foreach (var symbols in session.Symbols.EnumerateSymbols())
        {
            Write(symbols);
        }
    }

    private void ReadSymbols()
    {
        int count = Read7BitEncodedInt();

        for (int i = 0; i < count; i++)
        {
            ReadModuleSymbols();
        }
    }

    private void Write(ModuleSymbols symbols)
    {
        Write(symbols.PdbGuid);

        int tokenCount = symbols.SequencePointsPerMethodToken.Count;
        Write(tokenCount);

        foreach (var kvp in symbols.SequencePointsPerMethodToken.OrderBy(k => k.Key))
        {
            Write(kvp.Key);

            var sequencePoints = kvp.Value;
            int sequencePointCount = sequencePoints.Count;
            Write(sequencePointCount);

            foreach (var sequencePoint in sequencePoints)
            {
                Write(sequencePoint);
            }
        }
    }

    private void ReadModuleSymbols()
    {
        var symbols = new ModuleSymbols();
        symbols.PdbGuid = ReadGuid();

        int tokenCount = Read7BitEncodedInt();
        for (int i = 0; i < tokenCount; i++)
        {
            int token = Read7BitEncodedInt();

            int sequencePointCount = Read7BitEncodedInt();
            var sequencePoints = new SequencePoint[sequencePointCount];
            for (int j = 0; j < sequencePointCount; j++)
            {
                var sequencePoint = ReadSequencePoint();
                sequencePoints[j] = sequencePoint;
            }

            symbols.AddSequencePoints(token, sequencePoints);
        }

        session.Symbols.Add(symbols);
    }

    private void Write(SequencePoint sequencePoint)
    {
        WriteStringFromTable(sequencePoint.FilePath);
        Write(sequencePoint.ILOffset);
        Write(sequencePoint.StartLine);
        Write(sequencePoint.StartColumn);
        Write(sequencePoint.EndLine);
        Write(sequencePoint.EndColumn);
    }

    private SequencePoint ReadSequencePoint()
    {
        var result = new SequencePoint();
        result.FilePath = ReadStringFromTable();
        result.ILOffset = Read7BitEncodedInt();
        result.StartLine = Read7BitEncodedInt();
        result.StartColumn = Read7BitEncodedInt();
        result.EndLine = Read7BitEncodedInt();
        result.EndColumn = Read7BitEncodedInt();
        return result;
    }

    protected string ReadStringFromTable()
    {
        int index = Read7BitEncodedInt();
        return strings[index];
    }
}

public class SessionWriter : SessionSerializer
{
    private readonly string filePath;

    public SessionWriter(Session session, string filePath, int fileVersion = LatestFileFormatVersion)
    {
        this.session = session;

        filePath = Path.GetFullPath(filePath);
        this.filePath = filePath;
        FileVersion = fileVersion;
    }

    public void Write()
    {
        var directory = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(directory);

        var fileStream = new FileStream(filePath, FileMode.Create);
        Stream streamToWrite = fileStream;

        if (GZipCompress)
        {
            streamToWrite = new GZipStream(streamToWrite, CompressionLevel.Optimal);
        }

        using var bufferedStream = new BufferedStream(streamToWrite, bufferSize: 32768);

        writer = new BinaryWriter(bufferedStream);

        WriteSession();

        writer.Flush();
    }
}

public class SessionReader : SessionSerializer
{
    private readonly string filePath;

    public SessionReader(string filePath)
    {
        this.filePath = filePath;
    }

    public Session Read()
    {
        var fileStream = new FileStream(filePath, FileMode.Open);

        Stream streamToReadFrom = fileStream;

        if (fileStream.Length > 2 &&
            fileStream.ReadByte() == 0x1F &&
            fileStream.ReadByte() == 0x8B)
        {
            streamToReadFrom.Position = 0;
            streamToReadFrom = new GZipStream(streamToReadFrom, CompressionMode.Decompress);
        }
        else
        {
            streamToReadFrom.Position = 0;
        }

        using var bufferedStream = new BufferedStream(streamToReadFrom, bufferSize: 32768);

        stream = bufferedStream;

        reader = new BinaryReader(bufferedStream);

        session = new Session();

        ReadSession();

        return session;
    }
}

public class BinarySerializer
{
    protected BinaryWriter writer;
    protected BinaryReader reader;
    protected Stream stream;

    public void Write(DateTime timestamp)
    {
        writer.Write(timestamp.Ticks);
    }

    public DateTime ReadTimestamp()
    {
        long timestampTicks = reader.ReadInt64();
        var timestamp = new DateTime(timestampTicks, DateTimeKind.Utc);
        return timestamp;
    }

    public void Write(int value)
    {
        // Write out an int 7 bits at a time.  The high bit of the byte,
        // when on, tells reader to continue reading more bytes.
        uint v = (uint)value;   // support negative numbers
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }

        writer.Write((byte)v);
    }

    public void Write(string text)
    {
        writer.Write(text);
    }

    public string ReadString()
    {
        return reader.ReadString();
    }

    public void Write(double value)
    {
        writer.Write(value);
    }

    public void Write(long value)
    {
        writer.Write(value);
    }

    public void Write(ulong value)
    {
        writer.Write(value);
    }

    public unsafe void Write(Guid guid)
    {
        ulong* ptr = (ulong*)&guid;
        writer.Write(ptr[0]);
        writer.Write(ptr[1]);
    }

    public int Read7BitEncodedInt()
    {
        // Read out an Int32 7 bits at a time.  The high bit
        // of the byte when on means to continue reading more bytes.
        int count = 0;
        int shift = 0;
        byte b;
        do
        {
            // Check for a corrupted stream.  Read a max of 5 bytes.
            // In a future version, add a DataFormatException.
            if (shift == 5 * 7)  // 5 bytes max per Int32, shift += 7
            {
                throw new FormatException();
            }

            // ReadByte handles end of stream cases for us.
            b = reader.ReadByte();
            count |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return count;
    }

    public double ReadDouble()
    {
        return reader.ReadDouble();
    }

    public Guid ReadGuid()
    {
        int a = reader.ReadInt32();
        short b = reader.ReadInt16();
        short c = reader.ReadInt16();
        byte d = reader.ReadByte();
        byte e = reader.ReadByte();
        byte f = reader.ReadByte();
        byte g = reader.ReadByte();
        byte h = reader.ReadByte();
        byte i = reader.ReadByte();
        byte j = reader.ReadByte();
        byte k = reader.ReadByte();
        return new Guid(a, b, c, d, e, f, g, h, i, j, k);
    }
}

public class StringTable
{
    private Dictionary<string, int> stringIndices = new(StringComparer.Ordinal);
    private Dictionary<string, string> strings = new(StringComparer.Ordinal);

    public int Count => stringIndices.Count;

    public IEnumerable<string> EnumerateStrings() => stringIndices.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key);

    public void AddString(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        strings[text] = text;
    }

    public void Seal()
    {
        foreach (var text in strings.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            int index = stringIndices.Count + 2;
            stringIndices[text] = index;
        }
    }

    public int GetStringIndex(string text)
    {
        if (text == null)
        {
            return 0;
        }
        else if (text == "")
        {
            return 1;
        }

        return stringIndices[text];
    }
}
