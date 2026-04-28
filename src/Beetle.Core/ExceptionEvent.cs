using System;

namespace GuiLabs.Dotnet.Recorder;

public class ExceptionEvent
{
    public string ExceptionType { get; set; }
    public string ExceptionMessage { get; set; }
    public double TimestampMS { get; set; }
    public DateTime Timestamp;
    public int ThreadId { get; set; }
    public int CallStackIndex { get; set; }
}
