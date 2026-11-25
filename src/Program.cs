using System.Diagnostics;
using System.Text;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--ssh-proxy") return SshProxy.Relay(args).GetAwaiter().GetResult();
        if (args.Length == 4 && args[0] == "--test-proxy") return ProxySelfTest.Run(args[1], args[2], args[3]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--self-test") return SelfTest(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--migrate-local-grsync") return MigrateLocalGrsync(args[1], args[2]);
        ApplicationConfiguration.Initialize();
        if (args.Length is >= 2 and <= 4 && args[0] == "--render-ui")
        {
            if (args.Length >= 3) L.SetSession(args[2]);
            using var window = new MainWindow(loadSettings: false);
            var scenario = args.Length == 4 ? args[3] : "";
            window.RenderUi(args[1], scenario.Contains("minimum"), scenario.Contains("progress"), scenario.Contains("preview-changes") ? new Engine.Result(0, 3, 1, 2, 0) : scenario.Contains("preview-clean") ? new Engine.Result(0, 0, 0, 0, 0) : null);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--test-language-ui") return TestLanguageUi(args[1]);
        L.Initialize();
        using var instance = new Mutex(true, "Local\\RsyncDeckDesktop", out bool first);
        if (!first) { MessageBox.Show(L.T("RsyncDeck 已经打开。"), "RsyncDeck"); return 0; }
        using var logs = TemporaryLogs.Desktop;
        logs.Start(Path.Combine(Settings.DataFolder, "logs"));
        MainWindow.LanguageDraft? draft = null;
        do
        {
            using var window = new MainWindow();
            if (draft != null) window.RestoreDraft(draft);
            Application.Run(window);
            draft = window.ReopenWith;
        } while (draft != null);
        return 0;
    }

    private static int MigrateLocalGrsync(string report, string fallbackEngine)
    {
        try
        {
            using var mutex = new Mutex(true, "Local\\RsyncDeckDesktop", out bool first);
            if (!first) throw new IOException("Close RsyncDeck before migrating local settings.");
            var saved = Settings.Load();
            string engine = saved.Rsync;
            if (!File.Exists(engine) && File.Exists(fallbackEngine) && File.Exists(Path.Combine(Path.GetDirectoryName(fallbackEngine)!, "cygpath.exe"))) engine = fallbackEngine;
            var (next, added) = saved.MergeTasks(Engine.Import(Engine.Config, preserveActions: true), engine);
            foreach (var task in added)
            {
                if (task.Profile.Unsupported != null) throw new IOException("The Grsync configuration contains an unmapped option.");
                Engine.ParseEndpoint(task.Profile.Source, task.Options?.SingleFile == true);
                Engine.ParseEndpoint(task.Profile.Target);
                _ = ExtraArgs.Parse(string.Join(" ", task.Profile.Extra));
            }
            if (added.Count > 0 || !string.Equals(saved.Rsync, engine, StringComparison.OrdinalIgnoreCase)) next.Save();
            File.WriteAllText(report, $"Added={added.Count}; Tasks={next.Tasks.Count}; ExtraArgs={added.Count(t => t.Profile.Extra.Any(a => !string.IsNullOrWhiteSpace(a)))}; Hooks={added.Sum(t => (string.IsNullOrWhiteSpace(t.Options?.Before) ? 0 : 1) + (string.IsNullOrWhiteSpace(t.Options?.After) ? 0 : 1))}; Mirror={added.Count(t => t.Profile.Mirror)}; EngineConfigured={File.Exists(engine)}");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, ex.Message); return 1; }
    }

    static int TestLanguageUi(string report)
    {
        try
        {
            L.SetSession("en");
            using var first = new MainWindow(loadSettings: false);
            first.PrepareOffscreen();
            first.Shown += (_, _) => first.BeginInvoke(first.TestSwitchLanguage);
            Application.Run(first);
            var draft = first.ReopenWith ?? throw new Exception("Missing draft on language switch.");
            using var second = new MainWindow(loadSettings: false);
            second.PrepareOffscreen();
            second.RestoreDraft(draft);
            bool correct = false;
            second.Shown += (_, _) => second.BeginInvoke(() => { correct = second.HasExpectedLanguageDraft(); second.Close(); });
            Application.Run(second);
            if (!correct) throw new Exception("The translated window did not restore the draft.");
            File.WriteAllText(report, "PASS live English-to-Chinese window recreation and unsaved draft restoration");
            return 0;
        }
        catch (Exception e) { File.WriteAllText(report, e.ToString()); return 1; }
    }
    static async Task<int> SelfTest(string report, string rsync)
    {
        var lines = new List<string>();
        L.SetSession("en");
        void TestLog(string line) { lock (lines) lines.Add(line); }
        var root = Path.Combine(Path.GetTempPath(), "ResourceSync-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(L.Resolve("auto", "en-US") == "en" && L.Resolve("auto", "zh-CN") == "zh-Hans" && L.Resolve("en", "zh-CN") == "en", "language preference and Windows locale fallback");
            Check(L.T("基本选项") == "Basic options" && L.F("已导入 {0} 个会话", 3) == "Sessions imported: 3" && L.F("{0}    已用 {1:hh\\:mm\\:ss}    已传文件 {2:N0}    日志 {3:N0} 行", "Sync", TimeSpan.FromSeconds(5), 13L, 20L).Contains("00:00:05"), "English labels and formatted status text");
            L.SetSession("zh-Hans");
            Check(L.T("基本选项") == "基本选项", "switch back to Simplified Chinese");
            L.SetSession("en");
            var a = Directory.CreateDirectory(Path.Combine(root, "source space")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(root, "target space")).FullName;
            var profile = new Engine.Profile("测试任务", a, b, false, false, [], null);
            var job = await Engine.Validate(profile, rsync, false, 0, default);
            var imported = File.Exists(Engine.Config) ? Engine.Import(Engine.Config) : [];
            Check(imported.All(p => p.Profile.Name != "__CONFIG" && p.Options is { RunBefore: false, RunAfter: false }), "可从旧 Grsync 导入任务");
            var legacyFixture = Path.Combine(root, "grsync.ini");
            File.WriteAllText(legacyFixture, """
                [__CONFIG]
                [Fixture]
                text_source=\\\\server\\share\\folder\\
                text_dest=sample@host:/destination/
                check_time=true
                check_delete=true
                check_com_before=true
                check_com_after=false
                check_com_halt=true
                text_com_before=echo "C:\\with spaces\\before.cmd"
                text_com_after=echo after
                text_addit=--exclude="a b" --partial
                """);
            var legacyTask = Engine.Import(legacyFixture, preserveActions: true).Single();
            var portableTask = Engine.Import(legacyFixture).Single();
            Check(legacyTask.Profile.Source == @"\\server\share\folder\" && legacyTask.Profile.Mirror && legacyTask.Profile.Unsupported == null
                && legacyTask.Options is { KeepTimes: true, RunBefore: true, RunAfter: false, HaltOnFailure: true, Before: "echo \"C:\\with spaces\\before.cmd\"", After: "echo after" }
                && ExtraArgs.Parse(legacyTask.Profile.Extra.Single()).SequenceEqual(["--exclude=a b", "--partial"]), "local Grsync migration preserves escaped paths, flags, args, and hook settings");
            Check(!portableTask.Profile.Mirror && portableTask.Options is { RunBefore: false, RunAfter: false, After: "echo after" }, "ordinary import copies command text with actions disabled");
            var merged = new Settings { Tasks = [legacyTask], DefaultTask = legacyTask.Profile.Name }.MergeTasks([portableTask], rsync);
            Check(merged.Added.Count == 0 && merged.Next.StartupTask == legacyTask, "migration does not replace an existing task or clear its startup selection");
            var settingsPath = Path.Combine(root, "settings.json");
            new Settings { Rsync = rsync, Tasks = [new(profile, true, 2222)], DefaultTask = profile.Name }.Write(settingsPath);
            var restored = Settings.Read(settingsPath);
            Check(restored.Rsync == rsync && restored.Tasks.Single().Profile.Source == a && restored.Tasks.Single().Profile.Target == b && restored.Tasks.Single().SshPort == 2222 && restored.Tasks.Single().UploadChecksum, "自定义任务与引擎保存和重读");
            Check(restored.StartupTask?.Profile.Name == profile.Name && new Settings { Tasks = [new(profile)] }.StartupTask == null, "startup task persists; an unset preference opens blank");
            var renamed = profile with { Name = "Renamed task" };
            var renamedSettings = restored.WithTasks([new(renamed)], rsync, profile.Name, renamed.Name);
            Check(renamedSettings.StartupTask?.Profile.Name == renamed.Name && renamedSettings.WithTasks([], rsync).DefaultTask == null, "startup selection follows rename and clears when removed");
            var oldLogFolder = Path.Combine(root, "old logs");
            Directory.CreateDirectory(oldLogFolder);
            string generatedLog = Path.Combine(oldLogFolder, "20261125-150000-123-456.log");
            string manualLog = Path.Combine(oldLogFolder, "saved-manually.log");
            File.WriteAllText(generatedLog, "old");
            File.WriteAllText(manualLog, "keep");
            string latestLog;
            using (var logs = new TemporaryLogs(Path.Combine(root, "temp logs")))
            {
                latestLog = logs.LatestPath;
                using (var leftover = logs.OpenLatest()) leftover.WriteLine("abandoned");
                logs.Start(oldLogFolder);
                Check(!File.Exists(latestLog) && !File.Exists(generatedLog) && File.Exists(manualLog), "startup cleans abandoned and generated logs selectively");
                using (var first = logs.OpenLatest()) first.WriteLine("first job");
                using (var second = logs.OpenLatest()) second.WriteLine("latest job");
                Check(File.ReadAllLines(latestLog).SequenceEqual(["latest job"]) && Directory.GetFiles(logs.Folder).Length == 1, "only the latest completed job remains available");
            }
            Check(!File.Exists(latestLog), "application exit removes the temporary log");
            using (var unavailable = new TemporaryLogs(manualLog).OpenLatest())
            {
                unavailable.WriteLine("output still flows to the window");
                Check(!unavailable.IsAvailable, "unavailable temporary storage does not interfere with the job");
            }
            var live = new LiveLog(2);
            live.Add("old"); live.Add("warning"); live.Add("final status");
            Check(live.Take(2) == $"warning{Environment.NewLine}final status{Environment.NewLine}", "live log retains newest output under load");
            Check(Engine.ParseEndpoint("user@example.test:/some path/") is { Mode: Engine.Transport.Ssh, Protect: true } && Engine.ParseEndpoint("rsync://example.test:8873/module/path").Mode == Engine.Transport.Daemon && Engine.ParseEndpoint("example.test::module").Mode == Engine.Transport.Daemon, "识别 SSH 和两种 rsync 模块格式");
            try { await Engine.Validate(profile with { Target = Path.Combine(a, "sub") }, rsync, false, 0, default); throw new Exception("未阻止嵌套目录"); }
            catch (IOException) { lines.Add("PASS 拒绝重叠的本地目录"); }
            async Task<Engine.Result> Run(bool dry, bool check = false, bool mirror = false, string? source = null) => await Engine.Run(source == null ? job : job with { Source = job.Source with { Path = source } }, dry, check, mirror, false, [], TestLog, default, verify: dry && check);
            var s = Path.Combine(a, "fixture.txt"); var d = Path.Combine(b, "fixture.txt");
            var e = Path.Combine(b, "extra.txt");
            File.WriteAllText(s, "AAA"); File.WriteAllText(e, "extra");
            var preview = await Run(true, true, true);
            Check(preview.Code == 0 && preview.Files == 1 && preview.Extra == 1 && !File.Exists(d) && File.Exists(e), "预览识别缺失和远端多余文件，未修改目标");
            var upload = await Run(false);
            Check(!upload.Failed && upload.Files == 1 && File.ReadAllText(d) == "AAA" && File.Exists(e), "增量上传保留多余文件");
            Check((await Run(true, true)).Clean, "上传后内容校验通过");
            File.WriteAllText(d, "BBB"); File.SetLastWriteTimeUtc(d, File.GetLastWriteTimeUtc(s));
            Check((await Run(true)).Clean && (await Run(true, true)).Files == 1, "校验识别同大小同时间的内容差异");
            Check(Engine.IsSshKeyExchangeWarning("** WARNING: connection is not using a post-quantum key exchange algorithm.")
                && Engine.IsSshKeyExchangeWarning("** This session may be vulnerable to \"store now, decrypt later\" attacks.")
                && Engine.IsSshKeyExchangeWarning("** The server may need to be upgraded. See https://openssh.com/pq.html")
                && !Engine.IsSshKeyExchangeWarning("WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!")
                && !Engine.IsSshKeyExchangeWarning("Permission denied (publickey)."), "only the known OpenSSH key exchange advisory is nonfatal");
            var skipExisting = new TaskOptions(Enabled: ["exist"]);
            Check((await Engine.Run(job, true, true, false, false, [], TestLog, default)).Files == 1
                && (await Engine.Run(job, true, true, false, false, [], TestLog, default, options: skipExisting)).Clean
                && (await Engine.Run(job, true, true, false, false, [], TestLog, default, options: skipExisting, verify: true)).Files == 1
                && File.ReadAllText(d) == "BBB", "checksum preview obeys skip rules while verification reports skipped differences without writes");
            Check(!(await Run(false, true, true)).Failed && !File.Exists(e) && (await Run(true, true, true)).Clean, "内容重传及镜像删除后校验通过");
            Check((await Run(true, true, source: job.Source.Path + "missing/")).Failed, "源读取错误不会显示通过");
            Check(ExtraArgs.Parse("-rt --exclude=\"cache dir/**\" --bwlimit 4096").SequenceEqual(new[] { "-r", "-t", "--exclude=cache dir/**", "--bwlimit=4096" }), "附加参数支持短选项组合、引号和分开的值");
            foreach (var blocked in new[] { "--delete", "--rsync-path=cmd", "--no-dry-run", "--suffix=/file" })
                try { _ = ExtraArgs.Parse(blocked); throw new Exception("未阻止附加参数 " + blocked); } catch (IOException) { }
            lines.Add("PASS 阻止覆盖模拟、删除和远端命令的附加参数");
            File.WriteAllText(Path.Combine(a, "cache.tmp"), "excluded");
            File.WriteAllText(Path.Combine(a, "error notes.txt"), "included");
            var filtered = await Engine.Run(job, false, false, false, false, ["--exclude=*.tmp --stats"], TestLog, default, options: new TaskOptions());
            Check(!filtered.Failed && File.Exists(Path.Combine(b, "error notes.txt")) && !File.Exists(Path.Combine(b, "cache.tmp")), "带排除规则及进度的实际传输");
            Check((await Engine.Run(job, true, true, false, false, ["--exclude=*.tmp"], TestLog, default, verify: true)).Clean, "只读校验沿用排除范围");
            string singleTarget = Directory.CreateDirectory(Path.Combine(root, "single target")).FullName;
            var single = await Engine.Validate(profile with { Source = s, Target = singleTarget }, rsync, false, 0, default, sourceFile: true);
            Check(!(await Engine.Run(single, false, false, false, false, [], TestLog, default)).Failed && File.ReadAllText(Path.Combine(singleTarget, "fixture.txt")) == "AAA", "单文件同步到目标目录");
            Check(await Engine.RunHook("echo RSYNCDECK_HOOK & exit /b 7", TestLog, default) == 7 && lines.Any(l => l.Contains("RSYNCDECK_HOOK")), "本机命令捕获输出和退出码");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await Engine.Run(job, true, false, false, false, [], TestLog, canceled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { lines.Add("PASS 取消请求阻止进程启动"); }
            var progressSource = Directory.CreateDirectory(Path.Combine(root, "progress input")).FullName;
            var progressTarget = Directory.CreateDirectory(Path.Combine(root, "progress output")).FullName;
            File.WriteAllBytes(Path.Combine(progressSource, "name with spaces.bin"), new byte[192 * 1024]);
            File.WriteAllBytes(Path.Combine(progressSource, "zero.bin"), []);
            var progressJob = await Engine.Validate(profile with { Source = progressSource, Target = progressTarget }, rsync, false, 0, default);
            var scan = await Engine.Run(progressJob, true, false, false, false, [], TestLog, default, planProgress: true);
            Check(!scan.Failed && scan.Files == 2 && scan.TransferBytes == 192 * 1024 && !Directory.EnumerateFiles(progressTarget).Any(), "read-only scan counts planned transfer bytes and includes empty files");
            var tracker = new TransferProgress(scan.TransferBytes, scan.Files);
            bool sawIntermediate = false;
            var progressUpload = await Engine.Run(progressJob, false, false, false, false, ["--bwlimit=96"], TestLog, default,
                options: new TaskOptions(), progressOutput: line =>
                {
                    tracker.Observe(line);
                    if (tracker.Read() is { FileName: "name with spaces.bin", FilePercent: > 0 and < 100, OverallPercent: > 0 and < 100 }) sawIntermediate = true;
                });
            Check(!progressUpload.Failed && progressUpload.Files == 2 && sawIntermediate && tracker.Read() is { FilesDone: 2, FilePercent: 100, OverallPercent: 99 }, "live rsync emits filename, file percentage and bounded overall estimate, including empty files");
            tracker.Observe("SYNC:>f+++++++++|1|unexpected.bin");
            Check(tracker.Read().OverallPercent == null, "an outdated scan switches to an unknown overall total");
            lines.Add("ALL PASS");
            return 0;
        }
        catch (Exception ex) { lines.Add(ex.ToString()); return 1; }
        finally
        {
            File.WriteAllLines(report, lines);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // isolated GUID fixture created above
        }
        void Check(bool passed, string label) { if (!passed) throw new Exception(label); lines.Add("PASS " + label); }
    }
}


sealed class MainWindow : Form
{
    private readonly ComboBox tasks = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = L.IsEnglish ? 190 : 215 };
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly TextBox source = new() { Dock = DockStyle.Fill };
    private readonly TextBox target = new() { Dock = DockStyle.Fill };
    private readonly TextBox executable = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly ComboBox proxyKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
    private readonly TextBox proxyHost = new() { Width = 182, MaxLength = 253, PlaceholderText = "127.0.0.1" };
    private readonly NumericUpDown proxyPort = new() { Minimum = 1, Maximum = 65535, Value = 1080, Width = 82 };
    private readonly NumericUpDown port = new() { Minimum = 0, Maximum = 65535, Width = 72 };
    private readonly CheckBox mirror = new() { Text = L.T("删除目标端多余项"), AutoSize = true };
    private readonly CheckBox windowsTimes = new() { Text = L.T("Windows 兼容模式（1 秒容差）"), AutoSize = true };
    private readonly Dictionary<string, CheckBox> flagBoxes = new();
    private readonly TextBox extra = new() { Dock = DockStyle.Fill, MaxLength = 6000 };
    private readonly TextBox before = new() { Dock = DockStyle.Fill, MaxLength = 6000 };
    private readonly TextBox after = new() { Dock = DockStyle.Fill, MaxLength = 6000 };
    private readonly CheckBox keepTimes = Choice(L.T("保留修改时间"));
    private readonly CheckBox progress = Choice(L.T("显示传输进度"));
    private readonly CheckBox beforeEnabled = Choice(L.T("同步前执行命令"));
    private readonly CheckBox afterEnabled = Choice(L.T("同步后执行命令"));
    private readonly CheckBox halt = Choice(L.T("同步前命令失败时停止"));
    private readonly CheckBox singleFile = Choice(L.T("单文件模式（目标仍为目录）"));
    private readonly TabControl optionsTabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
    private readonly CheckBox uploadChecksum = new() { Text = L.T("按文件内容比较"), AutoSize = true };
    private readonly Button sync = Button(L.T("同步并校验"));
    private readonly Button simulate = Button(L.T("同步预览"));
    private readonly Button cancel = Button(L.T("取消"));
    private readonly Label status = new() { AutoSize = true, Text = L.T("准备就绪"), Font = new Font(L.IsEnglish ? "Segoe UI" : "Microsoft YaHei UI", 13, FontStyle.Bold) };
    private readonly TableLayoutPanel statusBand;
    private readonly Label detail = new() { AutoSize = true, Text = L.T("填写来源和目标即可开始。") };
    private readonly ProgressBar activity = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Visible = false };
    private readonly ProgressBar fileBar = new() { Dock = DockStyle.Fill, Height = 13 };
    private readonly ProgressBar overallBar = new() { Dock = DockStyle.Fill, Height = 13 };
    private readonly Label fileProgressText = ProgressLabel();
    private readonly Label overallProgressText = ProgressLabel();
    private readonly TableLayoutPanel transferPanel = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 1, 0, 1), Visible = false };
    private TransferProgress? transferProgress;
    private bool transferSucceeded;
    private bool transferInterrupted;
    private readonly TextBox logView = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BackColor = Color.White };
    private readonly LiveLog pending = new();
    private readonly ToolTip optionHints = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 350 };
    private readonly List<Control> editControls = [];
    private Settings settings = new();
    private bool canSave = true;
    private bool updatingTasks;
    private SavedTask? selected;
    private CancellationTokenSource? running;
    private Stopwatch? watch;
    private string phase = "";
    private long seen, transferred;
    private string? unsupported;
    private readonly Label startupHint = new() { AutoSize = true };

    public MainWindow(bool loadSettings = true)
    {
        Text = "RsyncDeck";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1050, 870);
        MinimumSize = new Size(910, 885);
        Font = new Font(L.IsEnglish ? "Segoe UI" : "Microsoft YaHei UI", 10);
        BackColor = Canvas;
        ForeColor = Ink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(18, 10, 18, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(i switch { 3 => new(SizeType.Absolute, 282), 5 => new(SizeType.Percent, 100), _ => new(SizeType.AutoSize) });
        Controls.Add(layout);

        var title = new Label { Text = "RsyncDeck", AutoSize = true, Font = new Font("Segoe UI", 21, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 0, 0, 0) };
        var tagline = Label(L.T("增量同步 · 传输后验证"));
        tagline.Margin = new Padding(1, 0, 0, 2);
        var brand = Stack(title, tagline);
        var pickEngine = Button(L.T("更换 rsync…"), ChooseEngine);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 0, 0, 5), Margin = new Padding(0, 0, 0, 4) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 250)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(brand, 0, 0);
        executable.PlaceholderText = L.T("未找到 rsync");
        executable.Dock = DockStyle.None; executable.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pickEngine.Anchor = AnchorStyles.Left;
        header.Controls.Add(executable, 1, 0); header.Controls.Add(pickEngine, 2, 0);
        layout.Controls.Add(header);

        var create = Button(L.T("新建会话"), () => ShowTask(null));
        var save = Button(L.T("保存会话"), SaveTask);
        var import = Button(L.T("导入…"), ImportTasks);
        var setDefault = Button(L.T("设为启动会话"), SetStartupTask);
        startupHint.ForeColor = Muted;
        startupHint.Margin = new Padding(12, 0, 3, 0);
        startupHint.Anchor = AnchorStyles.Left;
        layout.Controls.Add(Card(Section(L.T("会话"), tasks, create, save, setDefault, import), Field(L.T("会话名称"), name, startupHint)));

        var browseSource = Button(L.T("浏览…"), BrowseSource);
        var browseTarget = Button(L.T("浏览…"), () => ChooseFolder(target));
        browseSource.MinimumSize = browseTarget.MinimumSize = new Size(88, 0);
        var swap = Button(L.T("交换"), SwapPaths);
        var reset = Button(L.T("恢复默认选项"), ResetOptions);
        source.PlaceholderText = L.T("本地目录或 user@host:/path");
        target.PlaceholderText = L.T("本地目录或 user@host:/path");
        layout.Controls.Add(Card(Section(L.T("来源与目标"), swap, reset), Field(L.T("来源"), source, browseSource), Field(L.T("目标"), target, browseTarget)));

        BuildTabs();
        var optionsGroup = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(8, 3, 8, 6), Margin = new Padding(0, 0, 0, 12)
        };
        optionsGroup.ColumnStyles.Add(new(SizeType.Percent, 100));
        optionsGroup.RowStyles.Add(new(SizeType.AutoSize));
        optionsGroup.RowStyles.Add(new(SizeType.Percent, 100));
        optionsGroup.Controls.Add(Section(L.T("同步选项")), 0, 0);
        optionsGroup.Controls.Add(optionsTabs, 0, 1);
        layout.Controls.Add(optionsGroup);
        var openLog = Button(L.T("打开日志目录"), OpenLogs);
        activity.Height = 6;
        detail.ForeColor = Muted;
        status.ForeColor = Ink;
        logView.Font = new Font("Consolas", 9.5f);
        logView.BorderStyle = BorderStyle.FixedSingle;
        transferPanel.ColumnStyles.Add(new(SizeType.Percent, 52));
        transferPanel.ColumnStyles.Add(new(SizeType.Percent, 48));
        transferPanel.RowStyles.Add(new(SizeType.Absolute, 23));
        transferPanel.RowStyles.Add(new(SizeType.Absolute, 16));
        transferPanel.Controls.Add(fileProgressText, 0, 0);
        transferPanel.Controls.Add(overallProgressText, 1, 0);
        transferPanel.Controls.Add(fileBar, 0, 1);
        transferPanel.Controls.Add(overallBar, 1, 1);
        statusBand = Row(status, detail);
        statusBand.BackColor = StatusNeutral;
        statusBand.Padding = new Padding(8, 4, 8, 4);
        statusBand.Margin = new Padding(3, 5, 3, 3);
        statusBand.SizeChanged += (_, _) => FitStatusDetail();
        status.SizeChanged += (_, _) => FitStatusDetail();
        var actionCard = Card(Row(sync, simulate, cancel, openLog), statusBand, transferPanel, activity);
        actionCard.BackColor = ActionSurface;
        layout.Controls.Add(actionCard);
        var logs = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 4, 10, 8), Margin = new Padding(0) };
        logs.ColumnStyles.Add(new(SizeType.Percent, 100)); logs.RowStyles.Add(new(SizeType.AutoSize)); logs.RowStyles.Add(new(SizeType.Percent, 100));
        logs.Controls.Add(Section(L.T("运行日志"), Label(L.T("仅临时保存最近一次日志，退出时清理。")))); logs.Controls.Add(logView);
        layout.Controls.Add(logs);
        editControls.AddRange([tasks, name, source, target, create, save, setDefault, import, pickEngine, browseSource, browseTarget, swap, reset, sync, simulate, optionsTabs]);
        var menu = new MenuStrip { BackColor = MenuSurface, ForeColor = Ink };
        var fileMenu = new ToolStripMenuItem(L.T("文件"));
        var taskMenu = new ToolStripMenuItem(L.T("会话"));
        var helpMenu = new ToolStripMenuItem(L.T("帮助"));
        fileMenu.DropDownItems.AddRange([MakeMenuItem(L.T("浏览来源"), BrowseSource, Keys.Control | Keys.S), MakeMenuItem(L.T("浏览目标"), () => ChooseFolder(target), Keys.Control | Keys.D), MakeMenuItem(L.T("交换来源与目标"), SwapPaths, Keys.Control | Keys.W), new ToolStripSeparator(), MakeMenuItem(L.T("同步预览"), () => _ = Execute(Mode.Preview), Keys.Alt | Keys.S), MakeMenuItem(L.T("同步并校验"), () => _ = Execute(Mode.Sync), Keys.Alt | Keys.E), MakeMenuItem(L.T("校验文件内容"), () => _ = Execute(Mode.Verify)), MakeMenuItem(L.T("更换 rsync…"), ChooseEngine), MakeMenuItem(L.T("使用内置 rsync"), UseBundledEngine), new ToolStripSeparator(), MakeMenuItem(L.T("退出"), Close, Keys.Control | Keys.Q)]);
        taskMenu.DropDownItems.AddRange([MakeMenuItem(L.T("新建"), () => ShowTask(null)), MakeMenuItem(L.T("保存"), SaveTask), MakeMenuItem(L.T("删除"), RemoveTask), MakeMenuItem(L.T("设为启动会话"), SetStartupTask), MakeMenuItem(L.T("启动时打开空白会话"), ClearStartupTask), MakeMenuItem(L.T("恢复默认选项"), ResetOptions), new ToolStripSeparator(), MakeMenuItem(L.T("迁移本机 Grsync 会话"), ImportLocalGrsync), MakeMenuItem(L.T("导入…"), ImportTasks), MakeMenuItem(L.T("导出当前会话…"), ExportTask)]);
        helpMenu.DropDownItems.AddRange([MakeMenuItem(L.T("附加参数示例"), ShowExtraHelp), MakeMenuItem(L.T("打开日志目录"), OpenLogs), MakeMenuItem(L.T("关于 RsyncDeck"), () => MessageBox.Show(this, $"RsyncDeck {typeof(Program).Assembly.GetName().Version?.ToString(3)}\n{L.T("Windows 上的 rsync 图形界面")}", Text))]);
        var languageMenu = new ToolStripMenuItem(L.T("语言"));
        languageMenu.DropDownItems.AddRange([
            LanguageOption(L.T("跟随系统"), "auto"), LanguageOption("English", "en"), LanguageOption("简体中文", "zh-Hans")]);
        menu.Items.AddRange([fileMenu, taskMenu, languageMenu, helpMenu]);
        MainMenuStrip = menu;
        Controls.Add(menu);
        cancel.Enabled = false;
        sync.BackColor = Accent; sync.ForeColor = Color.White;
        sync.FlatAppearance.BorderColor = Accent;
        sync.FlatAppearance.MouseOverBackColor = Color.FromArgb(27, 55, 126);
        sync.FlatAppearance.MouseDownBackColor = Color.FromArgb(22, 46, 106);
        sync.EnabledChanged += (_, _) =>
        {
            sync.BackColor = sync.Enabled ? Accent : StatusNeutral;
            sync.FlatAppearance.BorderColor = sync.Enabled ? Accent : Border;
        };
        simulate.BackColor = PreviewSurface; simulate.ForeColor = Accent;
        simulate.FlatAppearance.BorderColor = Color.FromArgb(170, 192, 231);
        simulate.FlatAppearance.MouseOverBackColor = Color.FromArgb(213, 228, 252);
        simulate.FlatAppearance.MouseDownBackColor = Color.FromArgb(201, 220, 250);
        tasks.SelectedIndexChanged += (_, _) => { if (!updatingTasks) ShowTask(tasks.SelectedItem as SavedTask); };
        sync.Click += async (_, _) => await Execute(Mode.Sync);
        simulate.Click += async (_, _) => await Execute(Mode.Preview);
        cancel.Click += (_, _) => { cancel.Enabled = false; SetPhase(L.T("正在取消")); _ = running?.CancelAsync(); };
        FormClosing += (_, e) => { if (running != null) { e.Cancel = true; cancel.PerformClick(); } };
        timer.Tick += (_, _) => Flush(); timer.Start();
        FormClosed += (_, _) => { timer.Dispose(); optionHints.Dispose(); };
        try { if (loadSettings) settings = Settings.Load(); }
        catch (Exception e) { canSave = false; Finish(L.T("读取设置失败"), e.Message, Color.Firebrick); }
        executable.Text = loadSettings ? Engine.ResolveEngine(settings.Rsync) : "";
        RefreshTasks(settings.StartupTask);
    }

    internal void RenderUi(string folder, bool minimum, bool showProgress, Engine.Result? preview = null)
    {
        Directory.CreateDirectory(folder);
        PrepareOffscreen();
        Show();
        if (minimum) Size = MinimumSize;
        if (showProgress)
        {
            SetPhase(L.T("正在传输")); detail.Text = L.F("已用 {0:hh\\:mm\\:ss} · 已传文件 {1:N0} · 日志 {2:N0} 行", TimeSpan.FromSeconds(14), 1, 9);
            transferProgress = new(1500, 3);
            transferProgress.Observe("SYNC:>f+++++++++|500|readme.txt");
            transferProgress.Observe("        500 100% 7kB/s 0:00:00 (xfr#1, to-chk=2/4)");
            transferProgress.Observe("SYNC:>f+++++++++|500|assets/ui-package.dat");
            transferProgress.Observe("        230 46% 7kB/s 0:00:01");
            transferPanel.Visible = true; activity.Visible = false; PaintTransferProgress();
        }
        if (preview != null) FinishPreview(preview);
        var bounds = new List<string>();
        for (int i = 0; i < optionsTabs.TabCount; i++)
        {
            optionsTabs.SelectedIndex = i;
            Application.DoEvents();
            bounds.Add($"tab {i + 1}: client={ClientSize}, log={logView.Size}, tab content={optionsTabs.SelectedTab?.ClientSize}, scroll={optionsTabs.SelectedTab?.VerticalScroll.Visible}");
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            bitmap.Save(Path.Combine(folder, $"tab-{i + 1}.png"));
        }
        File.WriteAllLines(Path.Combine(folder, "layout.txt"), bounds);
        Close();
    }
    internal void PrepareOffscreen()
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        ShowInTaskbar = false;
    }

    internal void TestSwitchLanguage()
    {
        source.Text = @"C:\unsaved-source";
        target.Text = @"D:\unsaved-target";
        before.Text = "echo test draft";
        proxyKind.SelectedIndex = 2; proxyHost.Text = "127.0.0.1"; proxyPort.Value = 7890;
        optionsTabs.SelectedIndex = 3;
        SwitchLanguage("zh-Hans", L.SetSession);
    }

    internal bool HasExpectedLanguageDraft() => sync.Text == "同步并校验" && flagBoxes["perm"].Text == "保留权限" &&
        source.Text == @"C:\unsaved-source" && target.Text == @"D:\unsaved-target" && before.Text == "echo test draft" && optionsTabs.SelectedIndex == 3 && ReadTaskFields().Options?.Proxy == new SshProxy("http", "127.0.0.1", 7890);
    internal sealed record LanguageDraft(SavedTask Draft, string? SelectedName, int Tab);
    internal LanguageDraft? ReopenWith { get; private set; }

    private ToolStripMenuItem LanguageOption(string title, string language)
    {
        var item = MakeMenuItem(title, () => SwitchLanguage(language, L.Save));
        item.Checked = L.Preference == language;
        return item;
    }

    private void SwitchLanguage(string language, Action<string> updatePreference)
    {
        if (L.Preference == language) return;
        var draft = new LanguageDraft(ReadTaskFields(), selected?.Profile.Name, optionsTabs.SelectedIndex);
        updatePreference(language);
        ReopenWith = draft;
        Close();
    }
    internal void RestoreDraft(LanguageDraft state)
    {
        ShowTask(settings.Tasks.FirstOrDefault(t => t.Profile.Name == state.SelectedName));
        SetTaskFields(state.Draft);
        optionsTabs.SelectedIndex = state.Tab;
    }

    private static readonly Color Canvas = Color.FromArgb(222, 231, 243);
    private static readonly Color MenuSurface = Color.FromArgb(235, 241, 249);
    private static readonly Color TabSurface = Color.FromArgb(233, 239, 248);
    private static readonly Color ActionSurface = Color.FromArgb(247, 250, 255);
    private static readonly Color PreviewSurface = Color.FromArgb(228, 238, 255);
    private static readonly Color StatusNeutral = Color.FromArgb(235, 241, 248);
    private static readonly Color StatusRunning = Color.FromArgb(228, 238, 255);
    private static readonly Color StatusSuccess = Color.FromArgb(228, 244, 235);
    private static readonly Color StatusAttention = Color.FromArgb(255, 244, 218);
    private static readonly Color StatusError = Color.FromArgb(253, 235, 235);
    private static readonly Color Ink = Color.FromArgb(23, 41, 70);
    private static readonly Color Muted = Color.FromArgb(70, 87, 112);
    private static readonly Color Accent = Color.FromArgb(35, 69, 151);
    private static readonly Color Border = Color.FromArgb(199, 211, 227);

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var group = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = controls.Length, Margin = new Padding(0) };
        group.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var control in controls) { group.RowStyles.Add(new(SizeType.AutoSize)); group.Controls.Add(control); }
        return group;
    }
    private static TableLayoutPanel Card(params Control[] controls)
    {
        var card = Stack(controls);
        card.BackColor = Color.White; card.Padding = new Padding(10, 3, 10, 4); card.Margin = new Padding(0, 0, 0, 6);
        return card;
    }
    private static TableLayoutPanel Section(string title, params Control[] more)
    {
        var heading = Label(title); heading.Margin = new Padding(4, 2, 8, 2); heading.ForeColor = Ink; heading.Font = new Font(L.IsEnglish ? "Segoe UI" : "Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        return Row([heading, .. more]);
    }
    private void ShowStartupHint() => startupHint.Text = settings.StartupTask is { } task
        ? L.F("启动时打开：{0}", task.Profile.Name.Length <= 28 ? task.Profile.Name : task.Profile.Name[..28] + "…")
        : L.T("启动时打开空白会话");
    private void SetStartupTask()
    {
        if (selected == null) throw new IOException(L.T("请先保存会话，再设为启动会话。"));
        Commit(new Settings { Rsync = EnginePreference, Tasks = settings.Tasks, DefaultTask = selected.Profile.Name });
        ShowStartupHint();
        Finish(L.T("启动会话已设置"), L.T("打开程序时只载入会话，不会自动同步。"), Color.DarkGreen);
    }
    private void ClearStartupTask()
    {
        Commit(new Settings { Rsync = EnginePreference, Tasks = settings.Tasks });
        ShowStartupHint();
        Finish(L.T("启动会话已设置"), L.T("下次打开程序时显示空白会话。"), Color.DarkGreen);
    }
    private void ResetOptions()
    {
        var draft = ReadTaskFields();
        SetTaskFields(draft with { Profile = draft.Profile with { Mirror = false, WindowsTimes = false, Extra = [], Unsupported = null }, UploadChecksum = false, SshPort = 0, Options = new TaskOptions() });
        Finish(L.T("已恢复默认选项"), L.T("来源、目标和会话名称保留。点击保存会话可保留这些选项。"), Color.DarkGreen);
    }
    private static CheckBox Choice(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(4, 2, 4, 2) };
    private ToolStripMenuItem MakeMenuItem(string title, Action action, Keys keys = Keys.None)
    {
        var item = new ToolStripMenuItem(title) { ShortcutKeys = keys };
        item.Click += (_, _) => { if (running != null) return; try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        return item;
    }
    private void OpenLogs()
    {
        string folder = TemporaryLogs.Desktop.Folder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, L.T("无法打开临时日志目录。") + Environment.NewLine + ex.Message, Text); }
    }
    private void SwapPaths()
    {
        if (singleFile.Checked) throw new IOException(L.T("单文件模式的目标必须是目录，不能直接交换。"));
        (source.Text, target.Text) = (target.Text, source.Text);
    }
    private void BrowseSource()
    {
        if (!singleFile.Checked) { ChooseFolder(source); return; }
        using var dialog = new OpenFileDialog { Title = L.T("选择来源文件"), CheckFileExists = true, FileName = File.Exists(source.Text) ? source.Text : "" };
        if (dialog.ShowDialog(this) == DialogResult.OK) source.Text = dialog.FileName;
    }
    private void ShowExtraHelp() => MessageBox.Show(this,
        L.T("示例：\n--exclude=*.tmp --bwlimit=4096 --stats\n--exclude=\"有空格的目录/\" --timeout=120\n-rt --no-p --no-o --no-g --chmod=Du=rwx,Dg=rx,Do=rx,Fu=rw,Fg=r,Fo=r\n\n参数直接传给 rsync，支持常用选项。删除、执行模式和连接命令由界面管理；本地脚本请写在「附加选项」。"), L.T("附加 rsync 参数"));
    private void BuildTabs()
    {
        optionsTabs.SizeMode = TabSizeMode.Fixed;
        optionsTabs.ItemSize = new Size(140, 31);
        optionsTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        optionsTabs.DrawItem += (_, e) =>
        {
            var bounds = e.Bounds;
            bool active = e.Index == optionsTabs.SelectedIndex;
            using var background = new SolidBrush(active ? Color.White : TabSurface);
            e.Graphics.FillRectangle(background, bounds);
            if (active) { using var highlight = new SolidBrush(Accent); e.Graphics.FillRectangle(highlight, bounds.X, bounds.Bottom - 3, bounds.Width, 3); }
            TextRenderer.DrawText(e.Graphics, optionsTabs.TabPages[e.Index].Text, Font, bounds, active ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        TabPage BasicPage(string title, IEnumerable<Control> fields)
        {
            var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(6, 4, 6, 4), AutoScroll = true };
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(5, 1, 5, 2) };
            panel.ColumnStyles.Add(new(SizeType.Percent, 50)); panel.ColumnStyles.Add(new(SizeType.Percent, 50));
            foreach (var field in fields)
            {
                if (field is CheckBox)
                {
                    field.Anchor = AnchorStyles.Left;
                    field.Margin = new Padding(4, 1, 4, 1);
                }
                panel.Controls.Add(field);
            }
            page.Controls.Add(panel);
            return page;
        }
        foreach (var flag in TaskOptions.Basic.Concat(TaskOptions.Advanced))
            flagBoxes.Add(flag.Key, Choice(L.T(flag.Label)));
        CheckBox Flag(string key) => flagBoxes[key];
        // Follow Grsync's row order so imported options are easy to find.
        optionsTabs.TabPages.Add(BasicPage(L.T("基本选项"), [
            keepTimes, Flag("perm"), Flag("owner"), Flag("group"),
            mirror, Flag("onefs"), Flag("verbose"), progress,
            Flag("exist"), Flag("size"), Flag("skipnew"), windowsTimes]));
        optionHints.SetToolTip(uploadChecksum, L.T("预览、同步和传后复查时，使用校验和比较文件内容。大目录可能耗时较长。"));
        var advanced = BasicPage(L.T("高级选项"), [
            uploadChecksum, Flag("compr"), Flag("dev"), Flag("update"),
            Flag("keepart"), Flag("mapuser"), Flag("symlink"), Flag("hardlink"),
            Flag("backup"), Row(Label(L.T("SSH 端口（0 = 默认）")), port)]);
        var extraPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
        extraPanel.ColumnStyles.Add(new(SizeType.AutoSize));
        extraPanel.ColumnStyles.Add(new(SizeType.Percent, 100));
        extraPanel.ColumnStyles.Add(new(SizeType.AutoSize));
        extra.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        extraPanel.Controls.Add(Label(L.T("附加 rsync 参数")), 0, 0);
        extraPanel.Controls.Add(extra, 1, 0);
        extraPanel.Controls.Add(Button(L.T("示例…"), ShowExtraHelp), 2, 0);
        extraPanel.Padding = new Padding(5, 0, 5, 1);
        extra.PlaceholderText = L.T("例如 --exclude=*.tmp --bwlimit=4096 --stats");
        var advancedChoices = advanced.Controls[0];
        advanced.Controls.Remove(advancedChoices);
        var advancedContent = Stack(advancedChoices, extraPanel);
        advancedContent.Dock = DockStyle.Top;
        advanced.Controls.Add(advancedContent);
        optionsTabs.TabPages.Add(advanced);
        var extend = new TabPage(L.T("附加选项")) { BackColor = Color.White, Padding = new Padding(10), AutoScroll = true };
        var script = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        script.ColumnStyles.Add(new(SizeType.Percent, 100));
        script.Controls.Add(Label(L.T("命令在本机通过 Windows CMD 运行，仅实际同步执行。")));
        script.Controls.Add(Row(beforeEnabled, halt));
        script.Controls.Add(before);
        script.Controls.Add(afterEnabled);
        script.Controls.Add(after);
        script.Controls.Add(singleFile);
        before.PlaceholderText = L.T("例如 C:\\scripts\\prepare.cmd");
        after.PlaceholderText = L.T("例如 C:\\scripts\\post-sync.cmd");
        extend.Controls.Add(script);
        optionsTabs.TabPages.Add(extend);

        var connection = new TabPage(L.T("连接与代理")) { BackColor = Color.White, Padding = new Padding(10), AutoScroll = true };
        proxyKind.Items.AddRange([L.T("不使用代理"), "SOCKS5", "HTTP CONNECT"]);
        proxyKind.SelectedIndexChanged += (_, _) => proxyHost.Enabled = proxyPort.Enabled = proxyKind.SelectedIndex > 0;
        proxyKind.SelectedIndex = 0;
        var connectionForm = Stack(Row(Label(L.T("SSH 代理")), proxyKind, Label(L.T("地址")), proxyHost, Label(L.T("端口")), proxyPort),
            Label(L.T("只代理 SSH 同步及校验。请填写代理软件显示的本地地址和端口。")),
            Label(L.T("支持无需用户名和密码的代理，设置按会话保存。")));
        connectionForm.Dock = DockStyle.Top;
        connection.Controls.Add(connectionForm);
        optionsTabs.TabPages.Add(connection);
    }
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 8, 2) };
    private Button Button(string title, Action action)
    {
        var button = Button(title);
        button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        return button;
    }
    private static Button Button(string title)
    {
        var button = new Button { Text = title, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Padding = new Padding(7, 1, 7, 1), Margin = new Padding(3, 2, 5, 2) };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(231, 239, 255);
        return button;
    }
    private static TableLayoutPanel Row(params Control[] controls)
    {
        var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = controls.Length, RowCount = 1, Margin = new Padding(0, 1, 0, 1) };
        panel.RowStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < controls.Length; i++)
        {
            panel.ColumnStyles.Add(new(SizeType.AutoSize));
            controls[i].Anchor = AnchorStyles.Left;
            panel.Controls.Add(controls[i], i, 0);
        }
        return panel;
    }
    private static TableLayoutPanel Field(string title, Control field, Control? trailing = null)
    {
        var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new(SizeType.Absolute, L.IsEnglish ? 108 : 92)); panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.ColumnStyles.Add(new(SizeType.AutoSize));
        panel.Controls.Add(Label(title), 0, 0);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(field, 1, 0);
        if (trailing != null) { trailing.Anchor = AnchorStyles.Left; panel.Controls.Add(trailing, 2, 0); }
        return panel;
    }
    private string EnginePreference => Engine.IsBundledEngine(executable.Text) ? "" : executable.Text;
    private void UseBundledEngine()
    {
        if (!File.Exists(Engine.BundledEngine)) throw new IOException(L.T("此版本没有内置 rsync。"));
        Commit(settings.WithTasks(settings.Tasks, ""));
        executable.Text = Engine.BundledEngine;
    }
    private void ChooseEngine()
    {
        using var dialog = new OpenFileDialog { Title = L.T("选择 rsync.exe"), Filter = "rsync.exe|rsync.exe", CheckFileExists = true, FileName = executable.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Commit(settings.WithTasks(settings.Tasks, dialog.FileName));
        executable.Text = dialog.FileName;
    }
    private void ChooseFolder(TextBox field)
    {
        using var dialog = new FolderBrowserDialog { Description = L.T("选择本地目录"), UseDescriptionForTitle = true, SelectedPath = Directory.Exists(field.Text) ? field.Text : "" };
        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
    }
    private void RefreshTasks(SavedTask? task)
    {
        updatingTasks = true;
        try
        {
            tasks.Items.Clear();
            tasks.Items.AddRange(settings.Tasks.OrderBy(t => t.Profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
        }
        finally { updatingTasks = false; }
        ShowTask(task);
        ShowStartupHint();
    }
    private void ShowTask(SavedTask? task)
    {
        selected = task;
        updatingTasks = true;
        try { tasks.SelectedItem = task; }
        finally { updatingTasks = false; }
        SetTaskFields(task);
    }

    private void SetTaskFields(SavedTask? task)
    {
        unsupported = task?.Profile.Unsupported;
        name.Text = task?.Profile.Name ?? "";
        source.Text = task?.Profile.Source ?? "";
        target.Text = task?.Profile.Target ?? "";
        windowsTimes.Checked = task?.Profile.WindowsTimes ?? false;
        uploadChecksum.Checked = task?.UploadChecksum ?? false;
        mirror.Checked = task?.Profile.Mirror ?? false;
        port.Value = Math.Clamp(task?.SshPort ?? 0, 0, 65535);
        var opt = task?.Options ?? new TaskOptions();
        keepTimes.Checked = opt.KeepTimes; progress.Checked = opt.Progress;
        foreach (var (key, box) in flagBoxes) box.Checked = (opt.Enabled ?? []).Contains(key);
        before.Text = opt.Before; after.Text = opt.After;
        beforeEnabled.Checked = opt.RunBefore; afterEnabled.Checked = opt.RunAfter;
        halt.Checked = opt.HaltOnFailure; singleFile.Checked = opt.SingleFile;
        extra.Text = string.Join(" ", task?.Profile.Extra ?? []);
        proxyKind.SelectedIndex = opt.Proxy?.Kind switch { "socks5" => 1, "http" => 2, _ => 0 };
        proxyHost.Text = opt.Proxy?.Host ?? "";
        proxyPort.Value = Math.Clamp(opt.Proxy?.Port ?? 1080, 1, 65535);
    }
    private SavedTask ReadTaskFields()
        => new(new Engine.Profile(name.Text.Trim(), source.Text.Trim(), target.Text.Trim(), windowsTimes.Checked, mirror.Checked,
            [extra.Text.Trim()], unsupported), uploadChecksum.Checked, (int)port.Value,
            new TaskOptions(flagBoxes.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToArray(), keepTimes.Checked, progress.Checked, before.Text, after.Text, beforeEnabled.Checked, afterEnabled.Checked, halt.Checked, singleFile.Checked, proxyKind.SelectedIndex > 0 ? new SshProxy(proxyKind.SelectedIndex == 1 ? "socks5" : "http", proxyHost.Text.Trim(), (int)proxyPort.Value) : null));
    private void Commit(Settings next)
    {
        if (!canSave) throw new IOException(L.T("现有设置文件读不出来，为避免覆盖，暂不保存。请检查 %LOCALAPPDATA%\\RsyncDeck\\settings.json。"));
        next.Save(); settings = next;
    }
    private void SaveTask()
    {
        var task = ReadTaskFields();
        if (string.IsNullOrWhiteSpace(task.Profile.Name) || task.Profile.Name.Any(char.IsControl)) throw new IOException(L.T("请填写会话名称。"));
        Engine.ValidateProxy(Engine.ParseEndpoint(task.Profile.Source, task.Options?.SingleFile == true), Engine.ParseEndpoint(task.Profile.Target), task.Options?.Proxy);
        _ = ExtraArgs.Parse(extra.Text);
        var all = settings.Tasks.ToList();
        int index = selected == null ? -1 : all.IndexOf(selected);
        if (all.Where((_, i) => i != index).Any(t => string.Equals(t.Profile.Name, task.Profile.Name, StringComparison.OrdinalIgnoreCase))) throw new IOException(L.T("会话名称已经存在。请另取一个名称，或从列表选择该会话后编辑。"));
        if (index < 0) all.Add(task); else all[index] = task;
        Commit(settings.WithTasks(all, EnginePreference, selected?.Profile.Name, task.Profile.Name));
        RefreshTasks(task);
        Finish(L.T("会话已保存"), task.Profile.Name, Color.DarkGreen);
    }
    private void RemoveTask()
    {
        if (selected == null) return;
        if (MessageBox.Show(this, L.F("删除会话「{0}」？来源和目标中的文件不会被修改。", selected.Profile.Name), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Commit(settings.WithTasks(settings.Tasks.Where(t => t != selected).ToList(), EnginePreference));
        RefreshTasks(null);
    }
    private void ImportTasks()
    {
        using var dialog = new OpenFileDialog { Title = L.T("导入 RsyncDeck 会话或旧 Grsync 配置"), Filter = L.T("同步会话|*.json;*.ini|所有文件|*.*"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var sourceTasks = Path.GetExtension(dialog.FileName).Equals(".ini", StringComparison.OrdinalIgnoreCase) ? Engine.Import(dialog.FileName) : Settings.Read(dialog.FileName).Tasks;
        AddImportedTasks(sourceTasks.Select(t => t with { Profile = t.Profile with { Mirror = false }, Options = (t.Options ?? new TaskOptions()) with { RunBefore = false, RunAfter = false } }),
            L.T("导入的镜像删除及前后命令默认关闭。"));
    }
    private void ImportLocalGrsync()
    {
        if (!File.Exists(Engine.Config)) throw new IOException(L.T("本机未找到 Grsync 配置。可以通过普通导入选择文件。"));
        AddImportedTasks(Engine.Import(Engine.Config, preserveActions: true), L.T("已复制 Grsync 的选项及命令。迁移没有执行任何同步或命令。"));
    }
    private void AddImportedTasks(IEnumerable<SavedTask> source, string message)
    {
        var (next, added) = settings.MergeTasks(source, EnginePreference);
        if (added.Count == 0) { Finish(L.T("没有新增会话"), L.T("已存在的同名会话会保留。"), ForeColor); return; }
        Commit(next);
        RefreshTasks(added.FirstOrDefault(t => t.Profile.Unsupported == null) ?? added[0]);
        Finish(L.F("已导入 {0} 个会话", added.Count), message, Color.DarkGreen);
    }

    private void ExportTask()
    {
        var task = ReadTaskFields();
        if (string.IsNullOrWhiteSpace(task.Profile.Name)) throw new IOException(L.T("请先填写会话名称。"));
        if (MessageBox.Show(this, L.T("配置文件包含当前会话的来源、目标和 rsync 参数。导出前请确认这些信息可以共享。前后命令不会导出。"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        using var dialog = new SaveFileDialog { Title = L.T("导出当前会话"), Filter = L.T("RsyncDeck 会话|*.json"), FileName = "rsyncdeck-task.json", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        new Settings { Tasks = [task with { Options = (task.Options ?? new TaskOptions()) with { Before = "", After = "", RunBefore = false, RunAfter = false } }] }.Write(dialog.FileName);
        Finish(L.T("会话已导出"), Path.GetFileName(dialog.FileName), Color.DarkGreen);
    }
    private static Label ProgressLabel() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = new Padding(3, 0, 8, 1) };
    private void PaintTransferProgress()
    {
        if (transferProgress == null) return;
        var view = transferProgress.Read();
        fileBar.Value = view.FilePercent ?? 0;
        overallBar.Style = !transferSucceeded && !transferInterrupted && view.OverallPercent == null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        int completed = (int)Math.Min(int.MaxValue, transferSucceeded ? Interlocked.Read(ref transferred) : view.FilesDone);
        overallBar.Value = transferSucceeded ? (completed > 0 ? 100 : 0) : view.OverallPercent ?? 0;
        fileProgressText.Text = view.FileName.Length == 0 ? L.T("当前文件：—") : L.F("当前文件：{0} · {1}", view.FilePercent is int pct ? $"{pct}%" : "—", view.FileName);
        overallProgressText.Text = transferInterrupted ? L.F("整批传输未完成 · 已处理 {0:N0} 个文件", completed)
            : transferSucceeded ? (completed == 0 ? L.T("整批传输：无文件需要传输") : L.F("整批传输：100% · 已传 {0:N0} 个文件", completed))
            : view.OverallPercent is int overall ? L.F("整批传输（估算）：{0}% · {1:N0}/{2:N0} 个文件", overall, completed, view.TotalFiles)
            : L.F("整批传输：总量待定 · 已处理 {0:N0} 个文件", completed);
    }
    private void Flush(bool force = false)
    {
        if (running != null) detail.Text = phase == L.T("正在传输")
            ? L.F("已用 {0:hh\\:mm\\:ss} · 已传文件 {1:N0} · 日志 {2:N0} 行", watch?.Elapsed,
                transferProgress is { } activeTransfer ? activeTransfer.Read().FilesDone : Interlocked.Read(ref transferred), Interlocked.Read(ref seen))
            : L.F("已用 {0:hh\\:mm\\:ss} · 日志 {1:N0} 行", watch?.Elapsed, Interlocked.Read(ref seen));
        PaintTransferProgress();
        if (!force && logView.Focused) return;
        var text = pending.Take(force ? 180 : 48);
        if (text.Length == 0) return;
        const int limit = 42000;
        if (text.Length > limit) text = text[^limit..];
        if (logView.TextLength + text.Length > limit)
        {
            int keep = Math.Min(logView.TextLength, Math.Max(0, limit - text.Length - 4000));
            logView.Text = keep == 0 ? "" : logView.Text[^keep..];
        }
        logView.AppendText(text);
    }
    private void AddLine(string line)
    {
        Interlocked.Increment(ref seen);
        pending.Add(line);
    }
    private void FitStatusDetail()
    {
        int available = statusBand.ClientSize.Width - statusBand.Padding.Horizontal - status.Width - status.Margin.Horizontal - detail.Margin.Horizontal;
        var limit = new Size(Math.Max(120, available), 0);
        if (detail.MaximumSize != limit) detail.MaximumSize = limit;
    }
    private void SetPhase(string next)
    {
        phase = next;
        status.Text = next;
        status.ForeColor = Accent;
        statusBand.BackColor = StatusRunning;
    }
    private void Finish(string title, string explanation, Color color)
    {
        Flush(force: true); status.Text = title; status.ForeColor = color; detail.Text = explanation;
        statusBand.BackColor = color == Color.DarkGreen ? StatusSuccess : color == Color.DarkGoldenrod ? StatusAttention
            : color == Color.Firebrick ? StatusError : StatusNeutral;
    }
    private void FinishPreview(Engine.Result result)
    {
        if (result.Clean)
            Finish(L.T("无需同步"), L.T("按当前选项未发现变更，目标端未修改。"), Color.DarkGreen);
        else
            Finish(L.T("发现待同步变更"), L.F("待传文件 {0} · 其他变更 {1} · 待删除 {2}。", result.Files, result.Other, result.Extra) + L.T(" 未修改目标。"), Color.DarkGoldenrod);
    }
    private static string FormatSyncLine(string line, bool dry, bool tracked)
    {
        if (line.Length < 18 || !line.StartsWith("SYNC:", StringComparison.Ordinal) || line[16] != '|') return line;
        string name;
        bool file = (line[5] is '<' or '>') && line[6] == 'f';
        if (tracked)
        {
            int separator = line.IndexOf('|', 17);
            if (separator < 18 || separator == line.Length - 1) return line;
            name = line[(separator + 1)..];
        }
        else if (!dry)
        {
            int separator = line.LastIndexOf('|');
            if (separator <= 17) return line;
            name = line[17..separator];
        }
        else name = line[17..];
        string action = line.AsSpan(5, 11).TrimEnd().SequenceEqual("*deleting")
            ? (dry ? "待删除" : "删除")
            : file ? (dry ? "待传输" : "传输") : (dry ? "待更新" : "更新");
        return $"{L.T(action)}: {name}";
    }

    private enum Mode { Sync, Preview, Verify }
    private async Task Execute(Mode mode)
    {
        if (running != null) return;
        var task = ReadTaskFields();
        var p = task.Profile;
        var rsync = executable.Text;
        var opt = task.Options ?? new TaskOptions();
        bool removeExtras = p.Mirror;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        running = cts;
        transferProgress = null; transferPanel.Visible = transferSucceeded = transferInterrupted = false; activity.Visible = true;
        pending.Clear();
        Interlocked.Exchange(ref seen, 0); Interlocked.Exchange(ref transferred, 0);
        logView.Clear(); watch = Stopwatch.StartNew();
        foreach (var control in editControls) control.Enabled = false;
        cancel.Enabled = true; status.Text = L.T("正在处理"); status.ForeColor = ForeColor;
        SetPhase(L.T("检查设置")); activity.Style = ProgressBarStyle.Marquee;
        try
        {
            var logs = TemporaryLogs.Desktop;
            using var writer = logs.OpenLatest();
            void Log(string line) { writer.WriteLine(line); AddLine(line); }
            string logLocation = writer.IsAvailable ? $" | {logs.LatestPath}" : "";
            Log($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {p.Name}{logLocation}");
            if (!writer.IsAvailable) AddLine(L.T("无法写入临时日志；仍可在此窗口查看输出。"));
            Log(L.F("来源：{0} | 目标：{1}", p.Source, p.Target));
            if (opt.Proxy is { } proxy) Log(L.F("SSH 代理：{0} {1}:{2}", proxy.Kind, proxy.Host, proxy.Port));
            _ = ExtraArgs.Parse(string.Join(" ", p.Extra));
            bool hasBefore = mode == Mode.Sync && opt.RunBefore;
            var job = await Task.Run(() => Engine.Validate(p, rsync, mode == Mode.Sync && !hasBefore, task.SshPort, token, opt.SingleFile, opt.Proxy));
            int hookFailure = 0;
            if (mode == Mode.Sync && opt.RunBefore)
            {
                SetPhase(L.T("执行同步前命令")); Log(phase);
                hookFailure = await Task.Run(() => Engine.RunHook(opt.Before, Log, token));
                if (hookFailure != 0 && opt.HaltOnFailure) { Finish(L.T("同步前命令失败"), L.F("命令退出码 {0}。尚未开始同步。", hookFailure), Color.Firebrick); return; }
            }
            if (hasBefore) job = await Task.Run(() => Engine.Validate(p, rsync, true, task.SshPort, token, opt.SingleFile, opt.Proxy));
            int? maxDeletes = null;
            Task<Engine.Result> Run(bool dry, bool checksum, bool delete, bool verifyContent = false, bool plan = false, Action<string>? observe = null)
                => Task.Run(() => Engine.Run(job, dry, checksum, delete, p.WindowsTimes, p.Extra, line =>
                {
                    if (!dry && (line.StartsWith("SYNC:<f", StringComparison.Ordinal) || line.StartsWith("SYNC:>f", StringComparison.Ordinal)))
                        Interlocked.Increment(ref transferred);
                    writer.WriteLine(line);
                    AddLine(FormatSyncLine(line, dry, plan || observe != null));
                }, token, maxDeletes, opt, verifyContent, plan, observe));
            Engine.Result? transferPlan = null;
            if (mode == Mode.Preview || (mode == Mode.Sync && (removeExtras || opt.Progress)))
            {
                SetPhase(mode == Mode.Sync ? L.T(removeExtras ? "正在检查待同步变更" : "正在扫描待传文件") : L.T("同步预览")); Log(phase);
                var preview = await Run(true, task.UploadChecksum, removeExtras, plan: mode == Mode.Sync && opt.Progress);
                token.ThrowIfCancellationRequested();
                if (preview.Failed) { Finish(L.T("预览失败"), L.F("rsync 退出码 {0}，诊断 {1} 行。请查看日志。", preview.Code, preview.Issues), Color.Firebrick); return; }
                if (mode == Mode.Preview) { FinishPreview(preview); return; }
                transferPlan = preview;
                if (removeExtras) maxDeletes = preview.Extra;
                if (removeExtras && preview.Extra == 0) { removeExtras = false; Log(L.T("预览没有待删除项目；本次同步不启用删除。")); }
            }
            if (mode == Mode.Sync)
            {
                SetPhase(L.T("正在传输")); Log(phase);
                if (opt.Progress)
                {
                    transferProgress = new(transferPlan?.TransferBytes, transferPlan?.Files ?? 0);
                    transferPanel.Visible = true;
                    activity.Visible = false;
                    PaintTransferProgress();
                }
                Action<string>? observe = transferProgress == null ? null : transferProgress.Observe;
                var sent = await Run(false, task.UploadChecksum, removeExtras, observe: observe);
                Interlocked.Exchange(ref transferred, sent.Files);
                if (sent.Failed) { transferInterrupted = true; Finish(L.T("传输异常"), L.F("rsync 退出码 {0}，诊断 {1} 行。请查看日志。", sent.Code, sent.Issues), Color.Firebrick); return; }
                transferSucceeded = true;
                PaintTransferProgress();
                activity.Visible = true; activity.Style = ProgressBarStyle.Marquee;
            }
            if (mode == Mode.Sync && opt.RunAfter)
            {
                SetPhase(L.T("执行同步后命令")); Log(phase);
                int code = await Task.Run(() => Engine.RunHook(opt.After, Log, token));
                if (code != 0) hookFailure = code;
            }
            bool verifyContents = mode == Mode.Verify;
            SetPhase(L.T(verifyContents ? "正在校验文件内容" : "正在复查同步结果")); Log(phase);
            var check = await Run(true, verifyContents || task.UploadChecksum, p.Mirror, verifyContent: verifyContents);
            token.ThrowIfCancellationRequested();
            if (check.Failed) Finish(L.T("校验执行异常"), L.F("rsync 退出码 {0}，诊断 {1} 行。请查看日志。", check.Code, check.Issues), Color.Firebrick);
            else if (!check.Clean) Finish(L.T("校验发现差异"), L.F("文件差异 {0} · 其他差异 {1} · 目标端多余项 {2}。", check.Files, check.Other, check.Extra) + L.T("请查看日志。"), Color.DarkGoldenrod);
            else if (hookFailure != 0) Finish(L.T("同步复查通过，命令失败"), L.F("命令退出码 {0}，请查看日志。", hookFailure), Color.DarkGoldenrod);
            else Finish(mode == Mode.Sync ? L.T("同步完成，校验通过") : L.T("内容校验通过"), L.T(verifyContents ? "校验范围内未发现差异。" : "按当前同步选项，未发现待处理的变更。"), Color.DarkGreen);
        }
        catch (OperationCanceledException) { transferInterrupted = !transferSucceeded; Finish(L.T("已取消"), L.T("已传输的文件不会回滚。可以重新同步，或从「文件」菜单校验。"), ForeColor); }
        catch (Exception ex) { transferInterrupted = !transferSucceeded; AddLine(ex.ToString()); Finish(L.T("任务未完成"), ex.Message, Color.Firebrick); }
        finally
        {
            running = null; watch = null;
            foreach (var control in editControls) control.Enabled = true;
            cancel.Enabled = false; activity.Visible = false; activity.Style = ProgressBarStyle.Continuous; activity.Value = 0;
        }
    }
}











