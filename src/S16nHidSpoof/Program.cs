using S16nHidSpoof.Config;
using S16nHidSpoof.Ui;

namespace S16nHidSpoof;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (!SingleInstance.TryAcquire())
        {
            // Already running (often only in the tray) — bring that instance forward.
            SingleInstance.ActivateExisting();
            return;
        }

        try
        {
            var options = LoadOptions(args);
            ValidateOptions(options);
            Application.Run(new MainForm(options, args));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    private static SpoofOptions LoadOptions(string[] args)
    {
        string? explicitConfig = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" && i + 1 < args.Length)
                explicitConfig = Path.GetFullPath(args[++i]);
        }

        // Prefer --config; otherwise %LOCALAPPDATA%\s16n-hid-spoof\appsettings.json
        // (seeded from the bundled install copy on first run — same idea as s16n-hud).
        var settingsPath = explicitConfig ?? AppPaths.EnsureUserAppSettings();
        var config = new ConfigurationBuilder()
            .AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "S16N_")
            .AddCommandLine(args)
            .Build();

        var options = new SpoofOptions();
        config.Bind(options);
        return options;
    }

    private static void ValidateOptions(SpoofOptions options)
    {
        if (options.VirtualDevices.Count != 4)
            throw new InvalidOperationException(
                $"Expected exactly 4 VirtualDevices, found {options.VirtualDevices.Count}.");

        _ = options.Physical.VendorId;
        _ = options.Physical.ProductId;
        foreach (var v in options.VirtualDevices)
        {
            if (!v.Enabled)
                continue;
            _ = v.VendorId;
            _ = v.ProductId;
        }

        if (!options.VirtualDevices.Any(v => v.Enabled))
            throw new InvalidOperationException("At least one VirtualDevices entry must have Enabled=true.");

        if (options.ActiveSlot is < 0 or > 3)
            throw new InvalidOperationException("ActiveSlot must be 0..3.");

        if (!options.VirtualDevices[options.ActiveSlot].Enabled)
            throw new InvalidOperationException(
                $"ActiveSlot {options.ActiveSlot} is disabled. Set ActiveSlot to an Enabled virtual device.");

        HttpSecurity.ValidateListenUrl(options.Http.Url, options.Http.AllowNonLocal);
    }
}
