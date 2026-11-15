using Nefarius.Drivers.HidHide;
using Nefarius.Drivers.HidHide.Exceptions;
using Nefarius.Utilities.DeviceManagement.PnP;
using S16nHidSpoof.Config;
using HidSharp;

namespace S16nHidSpoof.Hid;

/// <summary>
/// Hide the configured physical HID from other apps via HidHide, while keeping this process able to open it.
/// HidHide's control IOCTLs can block for a long time if the driver is busy — never call from the UI thread
/// without a timeout / background hop. Only one process may hold the control handle at a time
/// (close HidHide Client first).
/// </summary>
internal sealed class HidHidePhysical : IDisposable
{
    public const string BusyHint =
        "HidHide is busy or denied access. Close the HidHide Client (and any other app using HidHide), then try again.";

    private readonly PhysicalDeviceOptions _physical;
    private readonly List<string> _blockedByUs = [];
    private readonly object _gate = new();
    private bool _applied;

    public HidHidePhysical(PhysicalDeviceOptions physical)
    {
        _physical = physical;
    }

    public bool IsApplied
    {
        get { lock (_gate) return _applied; }
    }

    /// <summary>Probe driver presence off the caller's thread; returns false on timeout/error.</summary>
    public static Task<bool> ProbeAvailableAsync(TimeSpan timeout, CancellationToken ct = default) =>
        RunWithTimeoutAsync(() =>
        {
            try
            {
                var svc = new HidHideControlService();
                return svc.IsInstalled && svc.IsOperational;
            }
            catch (Exception ex) when (IsAccessConflict(ex))
            {
                // Installed, but Client (or another process) holds the exclusive handle.
                // Still offer the checkbox so the user can retry after closing Client.
                return true;
            }
        }, timeout, ct);

    public Task SetHiddenAsync(bool hide, TimeSpan timeout, CancellationToken ct = default) =>
        RunWithTimeoutAsync(() =>
        {
            try
            {
                if (hide)
                    Apply();
                else
                    Clear();
                return true;
            }
            catch (Exception ex) when (IsAccessConflict(ex))
            {
                throw new InvalidOperationException(BusyHint, ex);
            }
        }, timeout, ct);

    public void Dispose()
    {
        // Never block process exit on HidHide — its IOCTL can hang while Client holds the handle.
        // Fire-and-forget; blocked list entries may linger until next successful clear (acceptable).
        _ = Task.Run(() =>
        {
            try { Clear(); }
            catch { /* ignore */ }
        });
    }

    private void Apply()
    {
        var svc = new HidHideControlService();
        if (!svc.IsInstalled || !svc.IsOperational)
            throw new InvalidOperationException("HidHide is not installed or not running.");

        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve this app's path for HidHide whitelist.");

        // Don't enumerate ApplicationPaths first (that IOCTL can hang); add and ignore duplicates.
        try { svc.AddApplicationPath(exe, throwIfInvalid: true); }
        catch { /* already listed or transient */ }

        var ids = ResolveInstanceIds();
        if (ids.Count == 0)
            throw new InvalidOperationException(
                $"No PnP instance IDs found for {_physical.Vid}:{_physical.Pid}. Is the device plugged in?");

        lock (_gate)
        {
            foreach (var id in ids)
            {
                try { svc.AddBlockedInstanceId(id); }
                catch { /* already blocked */ }
                if (!_blockedByUs.Contains(id, StringComparer.OrdinalIgnoreCase))
                    _blockedByUs.Add(id);
            }
        }

        svc.IsActive = true;
        lock (_gate) _applied = true;
    }

    private void Clear()
    {
        List<string> ids;
        lock (_gate)
        {
            if (_blockedByUs.Count == 0 && !_applied)
                return;
            ids = _blockedByUs.ToList();
        }

        try
        {
            var svc = new HidHideControlService();
            if (svc.IsInstalled)
            {
                foreach (var id in ids)
                {
                    try { svc.RemoveBlockedInstanceId(id); }
                    catch { /* best effort */ }
                }
            }
        }
        catch
        {
            /* best effort on shutdown */
        }

        lock (_gate)
        {
            _blockedByUs.Clear();
            _applied = false;
        }
    }

    private List<string> ResolveInstanceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in DeviceList.Local.GetHidDevices(_physical.VendorId, _physical.ProductId))
        {
            try
            {
                var path = device.DevicePath;
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var instanceId = PnPDevice.GetInstanceIdFromInterfaceId(path);
                if (!string.IsNullOrWhiteSpace(instanceId))
                    ids.Add(instanceId);
            }
            catch
            {
                /* skip interfaces we can't resolve */
            }
        }

        return ids.ToList();
    }

    private static bool IsAccessConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HidHideDriverAccessFailedException)
                return true;
            if (e.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                return true;
            if (e.Message.Contains("no other process is using the API", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task<T> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        var task = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return work();
        }, token);

        try
        {
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"HidHide did not respond within {timeout.TotalSeconds:0}s. {BusyHint}");
        }
    }
}
