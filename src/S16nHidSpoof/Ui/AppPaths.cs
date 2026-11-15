namespace S16nHidSpoof.Ui;

/// <summary>
/// User data under %LOCALAPPDATA%\s16n-hid-spoof\ (same pattern as s16n-hud).
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "s16n-hid-spoof");

    public static string AppSettingsPath { get; } = Path.Combine(DataDirectory, "appsettings.json");

    public static string BundledAppSettingsPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Ensure LocalAppData has an appsettings.json. First run copies the bundled default.
    /// </summary>
    public static string EnsureUserAppSettings()
    {
        Directory.CreateDirectory(DataDirectory);

        if (File.Exists(AppSettingsPath))
            return AppSettingsPath;

        if (!File.Exists(BundledAppSettingsPath))
        {
            throw new FileNotFoundException(
                $"Missing bundled default config at '{BundledAppSettingsPath}' " +
                $"and no user config at '{AppSettingsPath}'.",
                BundledAppSettingsPath);
        }

        File.Copy(BundledAppSettingsPath, AppSettingsPath);
        return AppSettingsPath;
    }
}
