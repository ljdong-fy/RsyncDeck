using System.Globalization;

static partial class L
{
    private static readonly string PreferencePath = Path.Combine(Settings.DataFolder, "language.txt");
    private static readonly string SystemLanguage = Resolve("auto", CultureInfo.CurrentUICulture.Name);
    internal static string Preference { get; private set; } = "auto";
    internal static bool IsEnglish => (Preference == "auto" ? SystemLanguage : Preference) == "en";

    internal static string Resolve(string preference, string systemLocale) => preference switch
    {
        "en" => "en", "zh-Hans" => "zh-Hans", _ => systemLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en"
    };

    internal static void Initialize()
    {
        try { if (File.Exists(PreferencePath)) SetSession(File.ReadAllText(PreferencePath).Trim()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void SetSession(string language) => Preference = language is "en" or "zh-Hans" ? language : "auto";

    internal static void Save(string language)
    {
        Directory.CreateDirectory(Settings.DataFolder);
        string temp = PreferencePath + ".new";
        File.WriteAllText(temp, language);
        File.Move(temp, PreferencePath, overwrite: true);
        SetSession(language);
    }

    internal static string T(string chinese) => IsEnglish && English.TryGetValue(chinese, out var translation) ? translation : chinese;
    internal static string F(string chinese, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(chinese), args);
}
