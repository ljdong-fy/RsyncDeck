using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

sealed record TaskOptions(string[]? Enabled = null, bool KeepTimes = true, bool Progress = true,
    string Before = "", string After = "", bool RunBefore = false, bool RunAfter = false, bool HaltOnFailure = true, bool SingleFile = false, SshProxy? Proxy = null)
{
    internal static readonly (string Key, string Label, string Arg, bool Scope)[] Basic =
    [
        ("perm", "保留权限", "--perms", false), ("owner", "保留所有者", "--owner", false),
        ("group", "保留组", "--group", false), ("onefs", "限制在同一文件系统", "--one-file-system", true),
        ("verbose", "详细输出", "--verbose", false), ("skipnew", "跳过目标端较新的文件", "--update", false),
        ("exist", "跳过目标端已有文件", "--ignore-existing", false), ("size", "仅比较文件大小", "--size-only", false)
    ];
    internal static readonly (string Key, string Label, string Arg, bool Scope)[] Advanced =
    [
        ("compr", "压缩文件数据", "--compress", false), ("dev", "保留设备文件", "--devices", true),
        ("update", "仅更新目标端已有文件", "--existing", false), ("keepart", "保留未传完的文件", "--partial", false),
        ("mapuser", "不映射 UID/GID", "--numeric-ids", false), ("symlink", "复制符号链接", "--links", true),
        ("hardlink", "保留硬链接关系", "--hard-links", false), ("backup", "备份被替换的文件", "--backup", false)
    ];
    internal static readonly Dictionary<string, (string Arg, bool Scope)> Flags = Basic.Concat(Advanced).ToDictionary(x => x.Key, x => (x.Arg, x.Scope));
    internal IEnumerable<string> ToArgs(bool verify, bool dry)
    {
        foreach (var key in Enabled ?? []) if (Flags.TryGetValue(key, out var flag) && (!verify || flag.Scope) && key != "keepart") yield return flag.Arg;
        if (KeepTimes && !verify) yield return "--times";
        if (Progress && !dry) yield return "--progress";
    }
}

static class ExtraArgs
{
    private static readonly HashSet<string> Short = new(StringComparer.Ordinal) { "-r", "-t", "-p", "-o", "-g", "-x", "-v", "-u", "-z", "-D", "-l", "-H", "-b", "-W", "-h", "-m", "-c", "-P" };
    private static readonly HashSet<string> Long = new(StringComparer.Ordinal) {
        "--recursive", "--times", "--perms", "--owner", "--group", "--one-file-system", "--verbose", "--update", "--compress", "--devices", "--specials", "--links", "--hard-links", "--backup", "--whole-file", "--human-readable", "--prune-empty-dirs", "--checksum", "--partial", "--progress", "--stats", "--numeric-ids", "--ignore-existing", "--existing", "--size-only", "--omit-dir-times", "--no-p", "--no-o", "--no-g", "--no-t", "--no-perms", "--no-owner", "--no-group", "--no-times" };
    private static readonly HashSet<string> WithValue = new(StringComparer.Ordinal) { "--chmod", "--timeout", "--bwlimit", "--include", "--exclude", "--modify-window", "--suffix", "--max-size", "--min-size", "--compress-level" };
    private static readonly HashSet<string> Scope = new(StringComparer.Ordinal) { "-x", "-D", "-l", "-m", "--one-file-system", "--devices", "--specials", "--links", "--prune-empty-dirs", "--include", "--exclude", "--timeout" };
    private static readonly Regex Token = new("\\G\\s*((?:[^\\s\\\"']+|\\\"[^\\\"]*\\\"|'[^']*')+)", RegexOptions.CultureInvariant);
    internal static string[] Parse(string text)
    {
        if (text.Any(c => char.IsControl(c) && c != '\t')) throw new IOException(L.T("附加参数请写在一行内。"));
        var raw = new List<string>();
        text = text.Trim();
        int pos = 0;
        while (pos < text.Length)
        {
            var m = Token.Match(text, pos);
            if (!m.Success) throw new IOException(L.T("附加参数有未闭合的引号。"));
            raw.Add(Regex.Replace(m.Groups[1].Value, "\\\"([^\\\"]*)\\\"|'([^']*)'", q => q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value));
            pos += m.Length;
        }
        var result = new List<string>();
        using var iter = raw.GetEnumerator();
        while (iter.MoveNext())
        {
            string arg = iter.Current;
            string key = arg.Split('=', 2)[0];
            if (WithValue.Contains(key))
            {
                if (arg == key) { if (!iter.MoveNext() || iter.Current.StartsWith('-')) throw new IOException(L.T("缺少参数值：") + key); arg += "=" + iter.Current; }
                string val = arg[(key.Length + 1)..];
                if (val.Length == 0 || (key == "--timeout" && (!int.TryParse(val, out int t) || t <= 0))) throw new IOException(L.T("无效参数值：") + key);
                if (key == "--suffix" && (val.Contains('/') || val.Contains('\\'))) throw new IOException(L.T("备份后缀不能包含路径分隔符。"));
                result.Add(arg);
            }
            else if (Long.Contains(arg)) result.Add(arg);
            else if (arg.Length > 1 && arg[0] == '-' && arg.Skip(1).All(ch => Short.Contains("-" + ch))) result.AddRange(arg.Skip(1).Select(ch => "-" + ch));
            else throw new IOException(L.T("不支持的附加 rsync 参数：") + key + L.T("。删除模式、传输命令、路径和执行方式由界面管理。"));
        }
        return result.ToArray();
    }
    internal static IEnumerable<string> ForRun(IEnumerable<string> args, bool verify, bool dry)
    {
        foreach (var arg in args)
        {
            if (arg is "--partial" or "-r" or "--recursive") continue; // lifecycle and recursion are owned by the app
            if (arg == "-P") { if (!dry) yield return "--progress"; continue; }
            if (dry && arg == "--progress") continue;
            if (!verify || Scope.Contains(arg.Split('=', 2)[0])) yield return arg;
        }
    }
}


