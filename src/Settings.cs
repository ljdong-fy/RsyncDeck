using System.Text.Json;

sealed class Settings
{
    internal static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RsyncDeck");
    private static readonly string FilePath = Path.Combine(DataFolder, "settings.json");
    public string Rsync { get; set; } = "";
    public List<SavedTask> Tasks { get; set; } = [];
    public string? DefaultTask { get; set; }
    internal SavedTask? StartupTask => Tasks.FirstOrDefault(t => SameName(t.Profile.Name, DefaultTask));
    internal static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal Settings WithTasks(List<SavedTask> tasks, string engine, string? oldName = null, string? newName = null)
    {
        var candidate = oldName != null && SameName(DefaultTask, oldName) ? newName : DefaultTask;
        return new Settings { Rsync = engine, Tasks = tasks, DefaultTask = tasks.Any(t => SameName(t.Profile.Name, candidate)) ? candidate : null };
    }
    internal (Settings Next, List<SavedTask> Added) MergeTasks(IEnumerable<SavedTask> incoming, string engine)
    {
        var names = new HashSet<string>(Tasks.Select(t => t.Profile.Name), StringComparer.OrdinalIgnoreCase);
        var added = incoming.Where(t => !string.IsNullOrWhiteSpace(t.Profile.Name) && !string.IsNullOrWhiteSpace(t.Profile.Source)
            && !string.IsNullOrWhiteSpace(t.Profile.Target) && names.Add(t.Profile.Name)).ToList();
        return (WithTasks([.. Tasks, .. added], engine), added);
    }
    internal static Settings Load() => File.Exists(FilePath) ? Read(FilePath) : Read(Path.Combine(Path.GetDirectoryName(DataFolder)!, "GrsyncNext", "settings.json"));
    internal static Settings Read(string path) => File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new IOException(L.T("会话文件为空。")) : new Settings();
    internal void Save() => Write(FilePath);
    internal void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

sealed record SavedTask(Engine.Profile Profile, bool UploadChecksum = false, int SshPort = 0, TaskOptions? Options = null)
{
    public override string ToString() => Profile.Name;
}


