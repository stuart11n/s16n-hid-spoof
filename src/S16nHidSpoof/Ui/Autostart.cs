using System.Diagnostics;
using Microsoft.Win32;

namespace S16nHidSpoof.Ui;

/// <summary>
/// Logon autostart via an elevated Scheduled Task (required because the app
/// requests administrator). Migrates away from the old HKCU Run key.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "s16n-hid-spoof";
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "s16n-hid-spoof";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /FO LIST /V",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0)
                return false;

            var exe = ResolveExecutablePath();
            return output.Contains("--tray", StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrEmpty(exe)
                       || output.Contains(exe, StringComparison.OrdinalIgnoreCase)
                       || output.Contains("s16n-hid-spoof.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enable)
    {
        try
        {
            ClearLegacyRunKey();

            if (!enable)
                return RunSchtasks($"/Delete /TN \"{TaskName}\" /F", allowMissing: true);

            var exe = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return false;

            // ONLOGON + HIGHEST so requireAdministrator apps actually start after login.
            // /TR wants the full command; quote the exe path when it contains spaces.
            var tr = exe.Contains(' ', StringComparison.Ordinal)
                ? $"\\\"{exe}\\\" --tray"
                : $"{exe} --tray";
            var args =
                $"/Create /F /TN \"{TaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST";
            return RunSchtasks(args, allowMissing: false);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasTrayArg(string[] args) =>
        args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

    /// <summary>Prefer the WinExe next to the app base, never dotnet.exe.</summary>
    public static string ResolveExecutablePath()
    {
        var named = Path.Combine(AppContext.BaseDirectory, "s16n-hid-spoof.exe");
        if (File.Exists(named))
            return Path.GetFullPath(named);

        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process)
            && process.EndsWith("s16n-hid-spoof.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(process))
        {
            return Path.GetFullPath(process);
        }

        return named;
    }

    private static void ClearLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            if (key?.GetValue(LegacyRunValueName) is not null)
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // best effort
        }
    }

    private static bool RunSchtasks(string arguments, bool allowMissing)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null)
            return false;
        p.WaitForExit(15000);
        if (p.ExitCode == 0)
            return true;

        var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        if (allowMissing
            && (err.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                || err.Contains("ERROR: The system cannot find", StringComparison.OrdinalIgnoreCase)
                || p.ExitCode == 1))
        {
            return true;
        }

        return false;
    }
}
