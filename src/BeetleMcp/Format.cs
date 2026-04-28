using System;
using System.Globalization;
using System.IO;
using System.Text;
using GuiLabs.Dotnet.Recorder;

namespace BeetleMcp;

internal static class Format
{
    public static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

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
    /// One-line process header: <c>"name pid=N exitCode=E [pi]"</c>.
    /// </summary>
    public static string ProcessLine(int processIndex, Process p)
    {
        var sb = new StringBuilder();
        sb.Append(ProcessName(p));
        sb.Append(" pid=").Append(p.Id);
        if (p.StopTimeRelativeMSec > 0)
        {
            sb.Append(" exitCode=").Append(p.ExitCode);
        }

        sb.Append(" exceptions=").Append(p.Exceptions.Count);
        sb.Append(" [").Append(processIndex).Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// One-line exception summary used by query/diff/around tools.
    /// <c>"timestamp ExceptionType: message [pi/ei]"</c>.
    /// </summary>
    public static string ExceptionLine(LoadedBeetle loaded, ExceptionRef r, bool includeProcessName = true)
    {
        var sb = new StringBuilder();
        sb.Append(Iso(r.Exception.Timestamp));
        sb.Append("  ");
        if (includeProcessName)
        {
            sb.Append(ProcessName(r.Process)).Append(" pid=").Append(r.Process.Id).Append("  ");
        }

        sb.Append(r.Exception.ExceptionType ?? "<unknown>");
        if (!string.IsNullOrEmpty(r.Exception.ExceptionMessage))
        {
            sb.Append(": ").Append(Shorten(r.Exception.ExceptionMessage, 300));
        }

        sb.Append(" [").Append(r.ProcessIndex).Append('/').Append(r.ExceptionIndex).Append(']');
        return sb.ToString();
    }

    public static string Shorten(string text, int max)
    {
        if (text == null)
        {
            return string.Empty;
        }

        // Collapse newlines so a single line stays single.
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length <= max)
        {
            return text;
        }

        return text.Substring(0, max) + "...";
    }
}
