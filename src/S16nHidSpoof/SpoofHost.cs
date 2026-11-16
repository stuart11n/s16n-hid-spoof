using HIDMaestro;
using S16nHidSpoof.Config;

namespace S16nHidSpoof;

/// <summary>
/// Owns the shared HIDMaestro context and up to three <see cref="SpoofService"/> instances.
/// </summary>
public sealed class SpoofHost : IAsyncDisposable
{
    private readonly SpoofOptions _options;
    private readonly SpoofService?[] _services = new SpoofService?[SpoofOptions.DeviceCount];
    private HMContext? _context;
    private bool _started;

    public SpoofHost(SpoofOptions options)
    {
        _options = options;
    }

    public SpoofOptions Options => _options;
    public bool IsRunning => _started;
    public IReadOnlyList<SpoofService?> Services => _services;

    public SpoofService? TryGet(string deviceId)
    {
        foreach (var s in _services)
        {
            if (s is null)
                continue;
            if (string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    public async Task StartAsync()
    {
        if (_options.Devices.Count != SpoofOptions.DeviceCount)
            throw new InvalidOperationException($"Devices must contain exactly {SpoofOptions.DeviceCount} entries.");

        Console.WriteLine("[spoof] installing HIDMaestro driver (elevated)...");
        _context = new HMContext();
        _context.InstallDriver();

        Console.WriteLine("[spoof] reclaiming leftover virtual controllers…");
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true);

        for (var i = 0; i < SpoofOptions.DeviceCount; i++)
        {
            var device = _options.Devices[i];
            var service = new SpoofService(device, i, _context);
            _services[i] = service;
            await service.StartAsync().ConfigureAwait(false);
        }

        _started = true;
        var enabled = _services.Count(s => s is { IsEnabled: true });
        Console.WriteLine($"[spoof] host ready — {enabled}/{SpoofOptions.DeviceCount} device(s) enabled");
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = 0; i < _services.Length; i++)
        {
            if (_services[i] is null)
                continue;
            try { await _services[i]!.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            _services[i] = null;
        }

        try { _context?.Dispose(); } catch { /* ignore */ }
        _context = null;
        _started = false;
    }
}
