using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

static class Engine
{
    public static readonly string Config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grsync", "grsync.ini");
    private const string Format = "SYNC:%i|%n%L";
    private const string Ssh = "ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=15 -o ServerAliveInterval=20 -o ServerAliveCountMax=3";
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "check_time", "check_verbose", "check_progr", "check_delete", "check_windows" };


    public record Profile(string Name, string Source, string Target, bool WindowsTimes, bool Mirror, string[] Extra, string? Unsupported)
    {
        public override string ToString() => Name;
    }
    public record Result(int Code, int Files, int Extra, int Other, int Issues, long? TransferBytes = null)
    {
        public bool Clean => Code == 0 && Issues == 0 && Files == 0 && Extra == 0 && Other == 0;
        public bool Failed => Code != 0 || Issues != 0;
    }

    public static List<SavedTask> Import(string config, bool preserveActions = false)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? section = null;
        foreach (var line in File.ReadLines(config))
        {
            if (line.StartsWith('[') && line.EndsWith(']')) { section = new(StringComparer.Ordinal); sections.Add(line[1..^1], section); }
            else if (section != null && line.IndexOf('=') is int i && i > 0) section[line[..i]] = line[(i + 1)..];
        }
        var result = new List<SavedTask>();
        foreach (var (name, data) in sections)
        {
            if (name == "__CONFIG" || string.IsNullOrWhiteSpace(name)) continue;
            string Get(string key) => DecodeKeyFileValue(data.GetValueOrDefault(key) ?? "");
            bool On(string key) => Get(key) == "true";
            var extra = new[] { Get("text_addit") };
            string? unsupported = data.Keys.FirstOrDefault(k => k.StartsWith("check_") && On(k) && !Supported.Contains(k) && !TaskOptions.Flags.ContainsKey(k[6..]) && k is not ("check_sum" or "check_browse_files" or "check_com_before" or "check_com_after" or "check_com_halt"));
            var flags = TaskOptions.Flags.Keys.Where(k => On("check_" + k)).ToArray();
            var options = new TaskOptions(flags, On("check_time"), On("check_progr"), Get("text_com_before"), Get("text_com_after"), preserveActions && On("check_com_before"), preserveActions && On("check_com_after"), On("check_com_halt"), On("check_browse_files"));
            result.Add(new SavedTask(new Profile(name, Get("text_source"), Get("text_dest"), On("check_windows"), preserveActions && On("check_delete"), extra, unsupported), On("check_sum"), 0, options));
        }
        return result;
    }

    // Grsync's INI is written by GLib GKeyFile. Leave unknown escapes alone.
    private static string DecodeKeyFileValue(string value) => Regex.Replace(value, @"\\([\\ntrs])", match => match.Groups[1].Value[0] switch
    {
        '\\' => "\\", 'n' => "\n", 't' => "\t", 'r' => "\r", 's' => " ", _ => match.Value
    });

    public enum Transport { Local, Ssh, Daemon }
    public record Endpoint(string Path, Transport Mode, bool Protect = false);
    public record Job(string Executable, Endpoint Source, Endpoint Target, int SshPort = 0, SshProxy? Proxy = null);
    private const string Host = @"(?:[a-zA-Z0-9_][a-zA-Z0-9_.-]*@)?(?:[a-zA-Z0-9][a-zA-Z0-9_.-]*|\[[a-fA-F0-9:]+\])";

    internal static Endpoint ParseEndpoint(string text, bool file = false)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsControl)) throw new IOException(L.T("请填写有效的来源和目标目录。"));
        if (Regex.IsMatch(text, @"\A(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)", RegexOptions.CultureInvariant))
            return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)), Transport.Local);
        var daemon = Regex.Match(text, @"\A(?:rsync://" + Host + @"(?::[0-9]{1,5})?/|" + Host + @"::)[a-zA-Z0-9_.-]+(?:/[^\r\n]*)?\z", RegexOptions.IgnoreCase);
        if (daemon.Success && !text.Contains('?') && !text.Contains('#')) return new(text.TrimEnd('/') + (file ? "" : "/"), Transport.Daemon);
        var ssh = Regex.Match(text, @"\A" + Host + @":(?!:)(.+)\z");
        if (ssh.Success)
        {
            string path = ssh.Groups[1].Value;
            if (path.IndexOfAny(['*', '?', '[', ']', '\\']) >= 0 || path.StartsWith('-')) throw new IOException(L.T("SSH 路径请填写明确的目录，不能使用通配符或反斜线。"));
            bool protect = !Regex.IsMatch(path, @"\A[a-zA-Z0-9_./-]+\z");
            if (path.StartsWith('~')) throw new IOException(L.T("SSH 路径请使用绝对路径，或填写相对于远端用户主目录的路径。"));
            return new(text.TrimEnd('/') + (file ? "" : "/"), Transport.Ssh, protect);
        }
        throw new IOException(L.T("请输入 Windows 绝对目录、user@host:/目录，或 host::模块/目录。"));
    }

    internal static string? FindOnPath(string file)
    {
        foreach (string location in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string full = Path.Combine(location.Trim('"'), file); if (Path.IsPathFullyQualified(full) && File.Exists(full)) return full; }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static string BundledEngine => Path.Combine(AppContext.BaseDirectory, "engine", "usr", "bin", "rsync.exe");
    internal static bool IsBundledEngine(string path) => string.Equals(path, BundledEngine, StringComparison.OrdinalIgnoreCase);
    internal static string ResolveEngine(string preferred, bool bundled = true)
    {
        if (Path.IsPathFullyQualified(preferred) && File.Exists(preferred)) return preferred;
        if (bundled && File.Exists(BundledEngine)) return BundledEngine;
        foreach (string location in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = location.Trim('"');
            if (Path.IsPathFullyQualified(dir) && File.Exists(Path.Combine(dir, "rsync.exe")) && File.Exists(Path.Combine(dir, "cygpath.exe")))
                return Path.Combine(dir, "rsync.exe");
        }
        return "";
    }

    public static async Task<Job> Validate(Profile profile, string executable, bool write, int sshPort, CancellationToken token, bool sourceFile = false, SshProxy? proxy = null)
    {
        if (profile.Unsupported != null) throw new InvalidOperationException(L.T("导入的会话包含暂不支持的选项：") + profile.Unsupported + L.T("。可新建会话并手动配置。"));
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "rsync.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("请在窗口上方选择一个有效的 rsync.exe。"));
        var source = ParseEndpoint(profile.Source, sourceFile);
        var target = ParseEndpoint(profile.Target);
        ValidateProxy(source, target, proxy);
        if (proxy != null && !IsBundledEngine(executable)) throw new IOException(L.T("使用代理时，请在「文件」菜单选择「使用内置 rsync」。"));
        if (source.Mode != Transport.Local && target.Mode != Transport.Local) throw new IOException(L.T("rsync 要求来源或目标至少有一端在本机。"));
        if (source.Mode == Transport.Local && !(sourceFile ? File.Exists(source.Path) : Directory.Exists(source.Path))) throw new IOException(sourceFile ? L.T("本地来源文件不存在。") : L.T("本地来源目录不存在。"));
        if (target.Mode == Transport.Local && File.Exists(target.Path)) throw new IOException(L.T("本地目标是文件，请填写目录。"));
        if (source.Mode == Transport.Local && target.Mode == Transport.Local)
        {
            static bool Includes(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (Includes(source.Path, target.Path) || Includes(target.Path, source.Path)) throw new IOException(L.T("来源与目标不能相同或互相包含。"));
        }
        if (profile.Mirror)
        {
            if (sourceFile) throw new IOException(L.T("单文件模式不支持镜像删除。"));
            if (target.Mode == Transport.Local && string.Equals(Path.GetPathRoot(target.Path), target.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("镜像目标不能是磁盘或共享根目录。"));
            if (target.Mode == Transport.Ssh)
            {
                var remotePath = Regex.Match(target.Path, @"\A" + Host + @":(.+)\z").Groups[1].Value;
                if (!remotePath.Split('/').Any(part => part.Length > 0 && part is not "." and not "..") || remotePath.Split('/').Contains("..")) throw new IOException(L.T("镜像目标必须是明确的远端子目录。"));
            }
            if (write && source.Mode == Transport.Local && !HasRegularFile(source.Path, token)) throw new IOException(L.T("源目录里没有找到普通文件，已阻止镜像删除。"));
        }
        if ((source.Mode == Transport.Ssh || target.Mode == Transport.Ssh) && !File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "ssh.exe")) && FindOnPath("ssh.exe") == null)
            throw new IOException(L.T("未找到 ssh.exe，请使用自带 SSH 的 rsync 套件，或把 OpenSSH 加到 PATH。"));
        if (source.Mode == Transport.Local) source = source with { Path = await Posix(executable, source.Path, token, !sourceFile) };
        if (target.Mode == Transport.Local) target = target with { Path = await Posix(executable, target.Path, token) };
        return new(executable, source, target, sshPort, proxy);
    }
    internal static void ValidateProxy(Endpoint source, Endpoint target, SshProxy? proxy)
    {
        if (proxy == null) return;
        proxy.Validate();
        if (source.Mode != Transport.Ssh && target.Mode != Transport.Ssh)
            throw new IOException(L.T("代理仅适用于 SSH 任务。请关闭代理或填写 SSH 路径。"));
    }
    private static bool HasRegularFile(string directory, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out string? current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                var a = File.GetAttributes(entry);
                if ((a & FileAttributes.ReparsePoint) != 0) continue;
                if ((a & FileAttributes.Directory) == 0) return true;
                pending.Push(entry);
            }
        return false;
    }

    private static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args)
    {
        var p = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        p.Environment["PATH"] = Path.GetDirectoryName(executable) + ";" + AppContext.BaseDirectory + ";" + Environment.GetEnvironmentVariable("PATH");
        if (IsBundledEngine(executable)) p.Environment["SHELL"] = "/usr/bin/bash";
        foreach (string key in new[] { "RSYNC_RSH", "RSYNC_ICONV", "RSYNC_OLD_ARGS", "RSYNC_PROTECT_ARGS", "RSYNC_PARTIAL_DIR", "RSYNC_CHECKSUM_LIST", "RSYNC_CONNECT_PROG", "RSYNC_SHELL" }) p.Environment.Remove(key);
        foreach (var arg in args) p.ArgumentList.Add(arg);
        return p;
    }

    internal static async Task<string> Posix(string rsync, string path, CancellationToken token, bool directory = true)
    {
        token.ThrowIfCancellationRequested();
        string pathTool = Path.Combine(Path.GetDirectoryName(rsync)!, "cygpath.exe");
        if (!File.Exists(pathTool)) throw new IOException(L.T("所选 rsync 同目录没有 cygpath.exe。请选择包含 cygpath 的 Windows rsync 套件。"));
        using var p = Process.Start(StartInfo(pathTool, ["-u", "--", path])) ?? throw new IOException(L.T("无法启动路径转换程序。"));
        using var stop = token.Register(() => Kill(p));
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        token.ThrowIfCancellationRequested();
        var s = (await output).TrimEnd('\r', '\n');
        if (p.ExitCode != 0 || !s.StartsWith('/') || s.IndexOfAny(['\r', '\n']) >= 0) throw new IOException(L.T("无法转换源目录：") + await error);
        return s.TrimEnd('/') + (directory ? "/" : "");
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static async Task<int> RunHook(string command, Action<string> log, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\n') || command.Contains('\r') || command.Contains('\0')) throw new IOException(L.T("请输入一行 Windows CMD 命令。"));
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/d /s /v:off /c \"" + command + "\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        using var p = Process.Start(info) ?? throw new IOException(L.T("无法启动本地命令。"));
        using var stop = token.Register(() => Kill(p));
        p.StandardInput.Close();
        async Task Drain(StreamReader reader, string prefix)
        {
            while (await reader.ReadLineAsync() is string line) log(prefix + line);
        }
        await Task.WhenAll(Drain(p.StandardOutput, L.T("[命令] ")), Drain(p.StandardError, L.T("[命令错误] ")), p.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        log(L.T("命令退出码：") + p.ExitCode);
        return p.ExitCode;
    }

    // OpenSSH emits this three-line advisory on stderr even when the connection succeeds.
    internal static bool IsSshKeyExchangeWarning(string line) => line.TrimEnd() is
        "** WARNING: connection is not using a post-quantum key exchange algorithm."
        or "** This session may be vulnerable to \"store now, decrypt later\" attacks."
        or "** The server may need to be upgraded. See https://openssh.com/pq.html";

    internal static async Task<Result> Run(Job job, bool dry, bool checksum, bool mirror, bool windowsTimes, IEnumerable<string> extra, Action<string> log, CancellationToken token, int? maxDeletes = null, TaskOptions? options = null, bool verify = false, bool planProgress = false, Action<string>? progressOutput = null)
    {
        options ??= new TaskOptions(Progress: false);
        bool tracked = planProgress || progressOutput != null;
        var format = tracked ? "SYNC:%i|%l|%n%L" : Format + (dry ? "" : "|%b");
        var args = new List<string> { "-r", "--itemize-changes", "--out-format=" + format, "--timeout=120" };
        bool usesSsh = job.Source.Mode == Transport.Ssh || job.Target.Mode == Transport.Ssh;
        if (usesSsh)
        {
            args.AddRange(["-e", Ssh + (job.SshPort > 0 ? " -p " + job.SshPort : "") + job.Proxy?.BuildOption()]);
            if (job.Source.Protect || job.Target.Protect) args.Add("-s");
        }
        if (job.Source.Mode == Transport.Daemon || job.Target.Mode == Transport.Daemon) args.Add("--contimeout=15");
        if (checksum) args.Add("--checksum");
        if (windowsTimes && !checksum && !verify) args.Add("--modify-window=1");
        if (dry) args.Add("--dry-run");
        if (mirror) args.Add("--delete-delay");
        if (!dry && mirror && maxDeletes > 0) args.Add("--max-delete=" + maxDeletes.Value);
        var custom = ExtraArgs.Parse(string.Join(" ", extra));
        if (mirror && ((options.Enabled ?? []).Contains("backup") || custom.Any(x => x is "--backup" or "-b"))) throw new IOException(L.T("镜像删除与备份请分开执行，以免校验将备份文件识别为待删除项。"));
        if (!dry && ((options.Enabled ?? []).Contains("keepart") || custom.Any(x => x is "--partial" or "-P"))) args.Add("--partial-dir=.rsyncdeck-partial");
        args.AddRange(options.ToArgs(verify, dry));
        args.AddRange(ExtraArgs.ForRun(custom, verify, dry));
        if (progressOutput != null) args.Add("--no-human-readable");
        args.AddRange(["--", job.Source.Path, job.Target.Path]);
        token.ThrowIfCancellationRequested();
        using var p = Process.Start(StartInfo(job.Executable, args)) ?? throw new IOException(L.T("无法启动 rsync。"));
        using var stop = token.Register(() => Kill(p));
        int files = 0, other = 0, extras = 0, issues = 0, sshWarningLines = 0;
        long? transferBytes = tracked ? 0L : null;
        async Task Drain(StreamReader reader, bool stderr)
        {
            while (await reader.ReadLineAsync() is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (stderr)
                {
                    if (usesSsh && IsSshKeyExchangeWarning(line))
                    {
                        sshWarningLines++;
                        log(L.T("[SSH 警告] ") + line);
                    }
                    else
                    {
                        Interlocked.Increment(ref issues);
                        log(L.T("[错误输出] ") + line);
                    }
                    continue;
                }
                if (line.StartsWith("SYNC:") && line.Length >= 17 && line[16] == '|')
                {
                    if (line[5..16].TrimEnd() == "*deleting") extras++;
                    else if (line[5] is '<' or '>' && line[6] == 'f')
                    {
                        files++;
                        if (transferBytes is long total)
                            transferBytes = TransferProgress.TryFile(line, out long bytes, out _) && bytes <= long.MaxValue - total ? total + bytes : null;
                    }
                    else other++;
                }
                else if (Regex.IsMatch(line, @"^(?:skipping (?:non-regular|directory)|file has vanished:|rsync (?:warning|error):|ERROR:)", RegexOptions.IgnoreCase)) Interlocked.Increment(ref issues);
                progressOutput?.Invoke(line);
                log(line);
            }
        }
        try { await Task.WhenAll(Drain(p.StandardOutput, false), Drain(p.StandardError, true), p.WaitForExitAsync()); }
        finally { Kill(p); }
        log(L.F("rsync 退出码: {0} | 文件: {1} | 远端多余/删除: {2} | 其他: {3} | 诊断: {4}", p.ExitCode, files, extras, other, issues));
        if (sshWarningLines != 0) log(L.F("SSH 密钥交换警告：{0} 行。", sshWarningLines));
        token.ThrowIfCancellationRequested();
        return new(p.ExitCode, files, extras, other, issues, transferBytes);
    }
}




