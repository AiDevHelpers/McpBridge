using System.Globalization;
using System.IO;

namespace AiDevHelpers.Tray;

/// <summary>
/// Remembers when the tray crashed, in a file, so a crash loop at logon is noticed across process starts. Tripped when
/// <see cref="maxCrashes"/> crashes fall inside <see cref="window"/>. An unreadable file counts as no crashes.
/// </summary>
public sealed class CrashLoopGuard(string path, int maxCrashes, TimeSpan window)
{
    public void RecordCrash(DateTime utcNow)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.AppendAllText(path, utcNow.ToString("O") + Environment.NewLine);
    }

    public bool IsTripped(DateTime utcNow)
    {
        var recent = ReadCrashes()
            .Count(time => time <= utcNow && utcNow - time <= window);
        return recent >= maxCrashes;
    }

    private IEnumerable<DateTime> ReadCrashes()
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
            {
                yield return time;
            }
        }
    }
}

