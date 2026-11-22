using System.Globalization;
using System.Text.Json;

namespace Eclipse;

internal static class L
{
    private static readonly Dictionary<string, string> english = Load();
    private static string language = "en";
    internal static string Language => Volatile.Read(ref language);
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-US");
    internal static void SetLanguage(string? value) => Volatile.Write(ref language, value == "ru" ? "ru" : "en");
    internal static string T(string key) => Language == "ru" ? key : english.GetValueOrDefault(key, key);
    internal static string F(string key, params object?[] values) => string.Format(Culture, T(key), values);
    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("Eclipse.Localization.en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
