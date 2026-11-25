using System.Text;
using System.Text.RegularExpressions;

sealed class TemporaryLogs(string folder) : IDisposable
{
    internal static TemporaryLogs Desktop { get; } = new(Path.Combine(Path.GetTempPath(), "RsyncDeck", "logs"));
    internal string Folder { get; } = folder;
    internal string LatestPath => Path.Combine(Folder, "latest.log");

    internal void Start(string legacyFolder)
    {
        Dispose(); // An earlier process may have been terminated before it could clean up.
        TryFileAction(() =>
        {
            if (!Directory.Exists(legacyFolder) || File.GetAttributes(legacyFolder).HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (string path in Directory.EnumerateFiles(legacyFolder, "*.log"))
                if (Regex.IsMatch(Path.GetFileName(path), @"\A[0-9]{8}-[0-9]{6}-[0-9]{3}-[0-9]+\.log\z"))
                    TryFileAction(() => File.Delete(path));
        });
    }

    internal JobLog OpenLatest()
    {
        StreamWriter? writer = null;
        TryFileAction(() =>
        {
            Directory.CreateDirectory(Folder);
            var stream = new FileStream(LatestPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(true), 65536);
        });
        return new JobLog(writer);
    }

    public void Dispose() => TryFileAction(() => File.Delete(LatestPath));

    private static void TryFileAction(Action action)
    {
        try { action(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed class JobLog(StreamWriter? writer) : IDisposable
    {
        private readonly object gate = new();
        private StreamWriter? active = writer;
        internal bool IsAvailable => active != null;

        internal void WriteLine(string line)
        {
            lock (gate)
            {
                if (active == null) return;
                try { active.WriteLine(line); }
                catch (IOException) { Dispose(); }
                catch (UnauthorizedAccessException) { Dispose(); }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (active is not { } current) return;
                active = null;
                TryFileAction(current.Dispose);
            }
        }
    }
}
