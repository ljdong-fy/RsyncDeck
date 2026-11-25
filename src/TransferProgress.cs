using System.Globalization;
using System.Text.RegularExpressions;

sealed partial class TransferProgress(long? totalBytes, int totalFiles)
{
    internal readonly record struct Snapshot(string FileName, int? FilePercent, int? OverallPercent, int FilesDone, int TotalFiles);
    private readonly object gate = new();
    private string file = "";
    private long fileSize, currentBytes, completedBytes;
    private int completed, starts;
    private int? filePercent;
    private bool valid = totalBytes.HasValue;
    private bool active;

    internal static bool TryFile(string line, out long size, out string name)
    {
        size = 0; name = "";
        if (line.Length < 19 || !line.StartsWith("SYNC:", StringComparison.Ordinal) || line[5] is not ('>' or '<') || line[6] != 'f' || line[16] != '|') return false;
        int separator = line.IndexOf('|', 17);
        if (separator < 18 || separator >= line.Length - 1 || !long.TryParse(line.AsSpan(17, separator - 17), NumberStyles.None, CultureInfo.InvariantCulture, out size)) return false;
        name = line[(separator + 1)..];
        return true;
    }

    internal void Observe(string line)
    {
        if (TryFile(line, out long size, out string name))
        {
            lock (gate)
            {
                if (active) valid = false; // a record was missing; don't continue to claim an overall estimate
                if (++starts > totalFiles) valid = false;
                file = name; fileSize = size; currentBytes = 0; filePercent = null; active = true;
            }
            return;
        }
        if (line.Length == 0 || !char.IsWhiteSpace(line[0])) return;
        var m = ProgressLine().Match(line);
        if (!m.Success || !long.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
            || !int.TryParse(m.Groups[2].Value, out int percent) || percent > 100) return;
        lock (gate)
        {
            if (!active) { valid = false; return; }
            filePercent = percent; currentBytes = Math.Min(bytes, fileSize);
            if (m.Groups[3].Success)
            {
                completed++;
                if (long.MaxValue - completedBytes < fileSize) valid = false;
                else completedBytes += fileSize;
                currentBytes = 0; active = false;
            }
        }
    }

    internal Snapshot Read()
    {
        lock (gate)
        {
            int? overall = null;
            if (valid && totalFiles > 0 && completed <= totalFiles && completedBytes <= totalBytes && currentBytes <= totalBytes - completedBytes)
            {
                double pct = totalBytes > 0 ? 100d * (completedBytes + currentBytes) / totalBytes.Value : 100d * completed / totalFiles;
                overall = Math.Min(99, (int)pct); // only a successful rsync exit may set transfer to 100%
            }
            return new(file, filePercent, overall, completed, totalFiles);
        }
    }

    [GeneratedRegex(@"^\s+([\d,]+)B?\s+(\d{1,3})%\s+\S+\s+\d+:\d{2}:\d{2}(?:\s+\((xf(?:e)?r#)\d+,\s+(?:ir-chk|to-chk|to-check)=\d+/\d+\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();
}
