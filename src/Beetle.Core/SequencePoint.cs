using System.Collections.Generic;

namespace GuiLabs.Dotnet.Recorder;

public struct SequencePoint
{
    public string FilePath;
    public int ILOffset;
    public int StartLine;
    public int StartColumn;
    public int EndLine;
    public int EndColumn;

    public static SequencePoint Find(IReadOnlyList<SequencePoint> sequencePoints, int ip)
    {
        int index = FindIndex(sequencePoints, ip);
        if (index == -1)
        {
            return default;
        }

        return sequencePoints[index];
    }

    public static int FindIndex(IReadOnlyList<SequencePoint> sequencePoints, int ip)
    {
        if (sequencePoints == null)
        {
            return -1;
        }

        int count = sequencePoints.Count;
        if (count == 0)
        {
            return -1;
        }

        int index = 0;
        while (index < count)
        {
            var current = sequencePoints[index];

            if (index < count - 1)
            {
                if (ip >= current.ILOffset && ip < sequencePoints[index + 1].ILOffset)
                {
                    break;
                }
            }
            else
            {
                if (ip >= current.ILOffset)
                {
                    break;
                }
            }

            index++;
        }

        if (index == count)
        {
            return -1;
        }

        return index;
    }
}
