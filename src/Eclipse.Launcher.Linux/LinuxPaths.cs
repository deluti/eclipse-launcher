namespace Eclipse;

internal static class LinuxPaths
{
    internal static string DataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ECLIPSE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("HOME is not available.");
            xdg = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(Path.GetFullPath(xdg), "eclipse-launcher");
    }
}
