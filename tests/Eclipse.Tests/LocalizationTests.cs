using System.Text.RegularExpressions;
using Eclipse;

internal static class LocalizationTests
{
    internal static Task SettingsAsync(string root, string initialLanguage)
    {
        var previous = L.Language;
        try
        {
            Check(initialLanguage == "en" && new Settings().Language == "en", "Fresh startup must default to English regardless of Windows language");
            var paths = new AppPaths(Path.Combine(root, "localization", Guid.NewGuid().ToString("N")));
            File.WriteAllText(paths.FilePath("settings.json"), "{\"Nickname\":\"Locale_Test\",\"MemoryMb\":6144}");
            var settings = JsonStore.Read<Settings>(paths.FilePath("settings.json"))!;
            Check(settings.Language == "en", "Existing settings without a language must use English");
            settings.Language = "ru";
            JsonStore.Write(paths.FilePath("settings.json"), settings);
            var restored = JsonStore.Read<Settings>(paths.FilePath("settings.json"))!;
            Check(restored.Language == "ru" && restored.Nickname == "Locale_Test" && restored.MemoryMb == 6144, "Language persists without losing other settings");
            L.SetLanguage("unsupported"); Check(L.Language == "en", "Unknown languages fall back to English");
        }
        finally { L.SetLanguage(previous); }
        return Task.CompletedTask;
    }
    internal static Task CatalogAsync()
    {
        Check(L.English.Count >= 180, "All application messages must be covered");
        foreach (var (key, english) in L.English)
        {
            Check(!string.IsNullOrWhiteSpace(english), "Empty translation: " + key);
            Check(!Regex.IsMatch(english, "[А-Яа-яЁё]"), "Russian text leaked into English: " + key);
            string[] Tokens(string value) => Regex.Matches(value, @"\{\d+(?:[^}]+)?\}").Select(m => m.Value).OrderBy(v => v).ToArray();
            Check(Tokens(key).SequenceEqual(Tokens(english)), "Formatting placeholders changed: " + key);
        }
        return Task.CompletedTask;
    }
    internal static Task MessagesAsync()
    {
        var previous = L.Language;
        try
        {
            L.SetLanguage("en");
            Check(L.T("ИГРАТЬ") == "PLAY" && PlaytimeCounter.Format(1.26) == "1.3 h", "English controls and decimal separator");
            try { OfflineAccount.Create("x"); throw new Exception("Expected validation error"); }
            catch (InvalidOperationException ex) { Check(ex.Message.StartsWith("Nickname:"), "Errors must be localized"); }
            L.SetLanguage("ru");
            Check(L.T("ИГРАТЬ") == "ИГРАТЬ" && PlaytimeCounter.Format(1.26) == "1,3 ч", "Russian controls and decimal separator");
            Check(L.F("Скин {0:00}", 4) == "Скин 04", "Russian format tokens");
            L.SetLanguage("en"); Check(L.F("Скин {0:00}", 4) == "Skin 04", "English format tokens");
        }
        finally { L.SetLanguage(previous); }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
