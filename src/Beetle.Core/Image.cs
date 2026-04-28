using System;

namespace GuiLabs.Dotnet.Recorder;

public class Image : IMemorySpan, IComparable<Image>
{
    public string FilePath;
    public ulong StartAddress { get; set; }
    public int Size { get; set; }

    int IComparable<Image>.CompareTo(Image other)
    {
        int comparison = FilePath.CompareTo(other.FilePath);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StartAddress.CompareTo(other.StartAddress);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Size.CompareTo(other.Size);
        return comparison;
    }
}
