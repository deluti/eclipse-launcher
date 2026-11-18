using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Eclipse;

internal static class L
{
    private static readonly Dictionary<string, string> english = Load();
    private static readonly Dictionary<string, string> reverse = english.GroupBy(p => p.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Control, Binding> bindings = new();
    private static string language = "en";
    internal static string Language => Volatile.Read(ref language);
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-US");
    internal static IReadOnlyDictionary<string, string> English => english;
    internal static void SetLanguage(string? value) => Volatile.Write(ref language, value == "ru" ? "ru" : "en");
    internal static string T(string key) => Language == "ru" ? key : english.GetValueOrDefault(key, key);
    internal static string F(string key, params object?[] values) => string.Format(Culture, T(key), values);
    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("Eclipse.Localization.en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    private sealed class Binding
    {
        public Binding() { }
        internal string? TextKey, AccessibleKey;
    }
    private static string? Key(string? value) => value == null ? null : english.ContainsKey(value) ? value : reverse.GetValueOrDefault(value);
    internal static void CaptureTree(Control root)
    {
        var binding = bindings.GetOrCreateValue(root);
        if (root is not TextBoxBase and not ListBox and not TreeView and not PixelComboBox)
            binding.TextKey ??= Key(root.Text);
        binding.AccessibleKey ??= Key(root.AccessibleName);
        foreach (Control child in root.Controls) CaptureTree(child);
    }
    internal static void RefreshTree(Control root)
    {
        if (bindings.TryGetValue(root, out var binding))
        {
            if (binding.TextKey is { } key && (root.Text == key || root.Text == english[key])) root.Text = T(key);
            if (binding.AccessibleKey is { } accessible && (root.AccessibleName == accessible || root.AccessibleName == english[accessible])) root.AccessibleName = T(accessible);
        }
        foreach (Control child in root.Controls) RefreshTree(child);
        root.Invalidate();
    }
    internal static void PreserveUserText(Control control)
    {
        if (bindings.TryGetValue(control, out var binding)) binding.TextKey = null;
    }
    internal static void SetText(Control control, string key)
    {
        bindings.GetOrCreateValue(control).TextKey = key;
        control.Text = T(key);
    }
}
