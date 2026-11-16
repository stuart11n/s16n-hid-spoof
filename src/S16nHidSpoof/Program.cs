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

        var settingsPath = explicitConfig ?? AppPaths.EnsureUserAppSettings();
        SpoofOptions.MigrateLegacyFileIfNeeded(settingsPath);

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
        if (options.Devices.Count != SpoofOptions.DeviceCount)
            throw new InvalidOperationException(
                $"Expected exactly {SpoofOptions.DeviceCount} Devices, found {options.Devices.Count}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledCount = 0;

        for (var i = 0; i < options.Devices.Count; i++)
        {
            var device = options.Devices[i];
            if (string.IsNullOrWhiteSpace(device.Id))
                throw new InvalidOperationException($"Devices[{i}].Id is required.");

            if (!ids.Add(device.Id.Trim()))
                throw new InvalidOperationException($"Duplicate Devices Id '{device.Id}'.");

            if (device.VirtualDevices.Count != 4)
                throw new InvalidOperationException(
                    $"Devices[{i}] ('{device.Id}') must have exactly 4 VirtualDevices.");

            if (!device.Enabled)
                continue;

            enabledCount++;
            _ = device.Physical.VendorId;
            _ = device.Physical.ProductId;

            foreach (var v in device.VirtualDevices)
            {
                if (!v.Enabled)
                    continue;
                _ = v.VendorId;
                _ = v.ProductId;
            }

            if (!device.VirtualDevices.Any(v => v.Enabled))
                throw new InvalidOperationException(
                    $"Devices[{i}] ('{device.Id}') is Enabled but has no Enabled VirtualDevices.");

            if (device.ActiveSlot is < 0 or > 3)
                throw new InvalidOperationException(
                    $"Devices[{i}].ActiveSlot must be 0..3.");

            if (!device.VirtualDevices[device.ActiveSlot].Enabled)
                throw new InvalidOperationException(
                    $"Devices[{i}] ('{device.Id}') ActiveSlot {device.ActiveSlot} is disabled.");
        }

        if (enabledCount == 0)
            throw new InvalidOperationException("At least one Devices entry must have Enabled=true.");

        HttpSecurity.ValidateListenUrl(options.Http.Url, options.Http.AllowNonLocal);
    }
}
