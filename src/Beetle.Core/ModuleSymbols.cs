using System;
using System.Collections.Generic;

namespace GuiLabs.Dotnet.Recorder;

public class ModuleSymbols
{
    public Guid PdbGuid { get; set; }

    public Dictionary<int, IReadOnlyList<SequencePoint>> SequencePointsPerMethodToken = new();

    public IReadOnlyList<SequencePoint> GetSequencePoints(int methodToken)
    {
        SequencePointsPerMethodToken.TryGetValue(methodToken, out var result);
        return result;
    }

    public void AddSequencePoints(int methodToken, IReadOnlyList<SequencePoint> sequencePoints)
    {
        SequencePointsPerMethodToken[methodToken] = sequencePoints;
    }
}
