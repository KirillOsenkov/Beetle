using System;
using System.Globalization;
using System.IO;
using System.Text;
using GuiLabs.Dotnet.Recorder;
using ModelContextProtocol;

namespace BeetleMcp;

[Flags]
internal enum ExceptionFields
{
    Timestamp = 1 << 0,
    Process = 1 << 1,
    Type = 1 << 2,
    Message = 1 << 3,
    Id = 1 << 4,

    Full = Timestamp | Process | Type | Message | Id
}

internal static class Format
{
    public static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static string Ms(double milliseconds) => milliseconds.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a relative-time expression into milliseconds. Accepts a leading '+' or '-' and
    /// one of the unit suffixes <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c>. A bare number is
    /// interpreted as milliseconds. Throws <see cref="McpException"/> on a malformed input.
    /// </summary>
    public static double ParseRelativeMs(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new McpException("Empty relative-time value.");
        }

        var s = input.Trim();
        double sign = 1;
        if (s.StartsWith("+", StringComparison.Ordinal))
        {
            s = s.Substring(1);
        }
        else if (s.StartsWith("-", StringComparison.Ordinal))
        {
            sign = -1;
            s = s.Substring(1);
        }

        double multiplier = 1;
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 2);
            multiplier = 1;
        }
        else if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 1);
            multiplier = 1000;
        }
        else if (s.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 1);
            multiplier = 60_000;
        }
        else if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 1);
            multiplier = 3_600_000;
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new McpException(
                $"Invalid relative-time value '{input}'. Expected forms: '30s', '+5m', '1800ms', '-2h', or a bare number (ms).");
        }

        return sign * value * multiplier;
    }

    public static ExceptionFields ParseExceptionFields(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return ExceptionFields.Full;
        }

        ExceptionFields fields = 0;
        foreach (var raw in csv.Split(','))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0)
            {
                continue;
            }

            fields |= token switch
            {
                "timestamp" or "time" or "ts" => ExceptionFields.Timestamp,
                "process" or "proc" => ExceptionFields.Process,
                "type" => ExceptionFields.Type,
                "message" or "msg" => ExceptionFields.Message,
                "id" => ExceptionFields.Id,
                "all" or "full" => ExceptionFields.Full,
                _ => throw new McpException(
                    $"Unknown exception field '{token}'. Valid: timestamp, process, type, message, id, all.")
            };
        }

        return fields == 0 ? ExceptionFields.Full : fields;
    }

    public static string ProcessName(Process p)
    {
        if (!string.IsNullOrEmpty(p.ImageFileName))
        {
            return p.ImageFileName;
        }

        if (!string.IsNullOrEmpty(p.FilePath))
        {
            return Path.GetFileName(p.FilePath);
        }

        return $"pid{p.Id}";
    }

    /// <summary>
    /// True if the process was still alive when the recording stopped.
    /// Two cases: (1) no stop event was captured at all (StopTimeRelativeMSec &lt;= 0);
    /// (2) the recorder stamped a synthetic STILL_ACTIVE (0x103 = 259) exit at trace-stop time.
    /// </summary>
    public static bool IsStillRunning(Process p, Session session)
    {
        if (p.StopTimeRelativeMSec <= 0)
        {
            return true;
        }

        return p.ExitCode == 259
            && p.StopTimeRelativeMSec >= session.SessionEndTimeRelativeMSec - 200;
    }

    /// <summary>
    /// One-line process header: <c>"name pid=N exitCode=E durationMs=D exceptions=N [pi]"</c>.
    /// </summary>
    public static string ProcessLine(int processIndex, Process p, Session session)
    {
        var sb = new StringBuilder();
        sb.Append(ProcessName(p));
        sb.Append(" pid=").Append(p.Id);
        if (IsStillRunning(p, session))
        {
            sb.Append(" stillRunningAtSessionEnd");
        }
        else
        {
            sb.Append(" exitCode=").Append(p.ExitCode);
            sb.Append(" durationMs=").Append(Ms(p.StopTimeRelativeMSec - p.StartTimeRelativeMSec));
        }

        sb.Append(" exceptions=").Append(p.Exceptions.Count);
        sb.Append(" [").Append(processIndex).Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// One-line exception summary used by query/diff/around tools.
    /// Default: <c>"timestamp processName pid=N ExceptionType: message [pi/ei]"</c>.
    /// </summary>
    public static string ExceptionLine(LoadedBeetle loaded, ExceptionRef r, bool includeProcessName = true)
    {
        var fields = ExceptionFields.Timestamp | ExceptionFields.Type | ExceptionFields.Message | ExceptionFields.Id;
        if (includeProcessName)
        {
            fields |= ExceptionFields.Process;
        }

        return ExceptionLine(r, fields);
    }

    /// <summary>
    /// Projected exception line: emits only the requested fields, separated by tabs (or
    /// "<c>: </c>" between Type and Message). Keeps the trailing <c>[pi/ei]</c> id when included.
    /// </summary>
    public static string ExceptionLine(ExceptionRef r, ExceptionFields fields)
    {
        var sb = new StringBuilder();
        bool first = true;

        void Sep()
        {
            if (!first)
            {
                sb.Append('\t');
            }

            first = false;
        }

        if ((fields & ExceptionFields.Timestamp) != 0)
        {
            Sep();
            sb.Append(Iso(r.Exception.Timestamp));
        }

        if ((fields & ExceptionFields.Process) != 0)
        {
            Sep();
            sb.Append(ProcessName(r.Process)).Append(" pid=").Append(r.Process.Id);
        }

        if ((fields & ExceptionFields.Type) != 0)
        {
            Sep();
            sb.Append(r.Exception.ExceptionType ?? "<unknown>");
            if ((fields & ExceptionFields.Message) != 0 && !string.IsNullOrEmpty(r.Exception.ExceptionMessage))
            {
                sb.Append(": ").Append(Shorten(r.Exception.ExceptionMessage, 300));
            }
        }
        else if ((fields & ExceptionFields.Message) != 0)
        {
            Sep();
            sb.Append(Shorten(r.Exception.ExceptionMessage, 300));
        }

        if ((fields & ExceptionFields.Id) != 0)
        {
            Sep();
            sb.Append('[').Append(r.ProcessIndex).Append('/').Append(r.ExceptionIndex).Append(']');
        }

        return sb.ToString();
    }

    public static string Shorten(string? text, int max)
    {
        if (text == null)
        {
            return string.Empty;
        }

        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length <= max)
        {
            return text;
        }

        return text.Substring(0, max) + "...";
    }
}
