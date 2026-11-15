using HidSharp;
using S16nHidSpoof.Config;

namespace S16nHidSpoof.Hid;

/// <summary>
/// FFBeast flight-control reset (send 0xA3 0x01 on HID interface 0).
/// Ported from https://github.com/stuart11n/ffbeast-flight-reset
/// </summary>
public static class FfbeastFlightReset
{
    public const ushort VendorId = 0x045B;
    public const ushort FlightStickProductId = 0x58F9;

    /// <summary>65-byte output report used by reset-ffb (flightstick/yoke).</summary>
    private static readonly byte[] Payload = BuildPayload();

    public static bool IsSupported(PhysicalDeviceOptions physical) =>
        physical.VendorId == VendorId && physical.ProductId == FlightStickProductId;

    /// <summary>
    /// Opens the physical FFBeast HID interface 0 and writes the reset payload.
    /// Matches reset-ffb.c / reset-ffb.py (VID 045B, PID 58F9, mi_00).
    /// </summary>
    public static (bool ok, string message) TryReset()
    {
        var devices = DeviceList.Local.GetHidDevices(VendorId, FlightStickProductId).ToList();
        if (devices.Count == 0)
            return (false, "no FFBeast devices found. Check connection.");

        var targets = devices
            .Where(IsInterfaceZero)
            .OrderBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
            return (false, "FFBeast device found but HID interface 0 (mi_00) was not available.");

        Exception? lastError = null;
        var wrote = 0;
        foreach (var device in targets)
        {
            try
            {
                if (!device.TryOpen(out var stream))
                {
                    lastError = new IOException($"Failed to open {device.DevicePath}");
                    continue;
                }

                using (stream)
                {
                    stream.WriteTimeout = 1000;
                    stream.Write(Payload);
                    wrote++;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (wrote > 0)
            return (true, $"reset sent to {wrote} interface(s).");

        return (false, lastError?.Message ?? "failed to write reset report.");
    }

    private static bool IsInterfaceZero(HidDevice device)
    {
        var path = device.DevicePath ?? "";
        // Windows multi-interface HID paths encode "&mi_XX". Prefer mi_00; if no marker, treat as single interface 0.
        var lower = path.ToLowerInvariant();
        if (lower.Contains("&mi_", StringComparison.Ordinal))
            return lower.Contains("&mi_00", StringComparison.Ordinal);
        return true;
    }

    private static byte[] BuildPayload()
    {
        var payload = new byte[65];
        payload[0] = 0xA3;
        payload[1] = 0x01;
        return payload;
    }
}
