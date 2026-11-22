namespace Eclipse;

internal static class LauncherUpdates
{
    internal const string Repository = "deluti/eclipse-launcher";
    internal static Version CurrentVersion
    {
        get
        {
            var value = typeof(LauncherUpdates).Assembly.GetName().Version!;
            return new(value.Major, value.Minor, Math.Max(0, value.Build));
        }
    }
}
