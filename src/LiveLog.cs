using System.Text;

// If UI rendering falls behind, retain the newest preview lines.
sealed class LiveLog(int limit = 180)
{
    private readonly Queue<string> lines = new();
    internal void Add(string line)
    {
        lock (lines)
        {
            if (lines.Count == limit) lines.Dequeue();
            lines.Enqueue(line.Length > 800 ? line[..800] + "…" : line);
        }
    }
    internal string Take(int count)
    {
        var result = new StringBuilder();
        lock (lines) for (int i = 0; i < count && lines.TryDequeue(out var line); i++) result.AppendLine(line);
        return result.ToString();
    }
    internal void Clear() { lock (lines) lines.Clear(); }
}
