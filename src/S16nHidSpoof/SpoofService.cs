using HIDMaestro;
using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof;

/// <summary>One physical → up to four virtual devices for a single Devices[] entry.</summary>
public sealed class SpoofService : IAsyncDisposable
{
    private readonly DeviceOptions _device;
    private readonly int _deviceIndex;
    private readonly HMContext _context;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private PhysicalHidReader? _reader;
    private VirtualDeviceBank? _bank;
    private Task? _pump;
    private Task? _bringUp;
    private string? _baseDescriptorHex;
    private bool _started;

    public SpoofService(DeviceOptions device, int deviceIndex, HMContext sharedContext)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _deviceIndex = deviceIndex;
        _context = sharedContext ?? throw new ArgumentNullException(nameof(sharedContext));
    }

    public DeviceOptions Device => _device;
    public string DeviceId => string.IsNullOrWhiteSpace(_device.Id) ? $"device-{_deviceIndex}" : _device.Id;
    public int DeviceIndex => _deviceIndex;

    public VirtualDeviceBank Bank =>
        _bank ?? throw new InvalidOperationException(
            PhysicalProfileCache.ExistsFor(DeviceId, _device.Physical)
                ? "Virtual bank not ready yet."
                : "No cached physical descriptor yet — plug the controller in once so it can be recorded.");

    public PhysicalHidReader Reader =>
        _reader ?? throw new InvalidOperationException("Service not started.");

    public bool IsRunning => _started;
    public bool IsReady => _bank is not null;
    public bool HasCachedDescriptor => PhysicalProfileCache.ExistsFor(DeviceId, _device.Physical);
    public bool IsEnabled => _device.Enabled;

    public async Task StartAsync()
    {
        if (!_device.Enabled)
        {
            Console.WriteLine($"[spoof:{DeviceId}] disabled — not starting");
            return;
        }

        if (_device.VirtualDevices.Count != 4)
            throw new InvalidOperationException($"Devices[{_deviceIndex}].VirtualDevices must contain exactly 4 entries.");

        _reader = new PhysicalHidReader(_device.Physical) { StripReportId = true };
        _reader.Start(_cts.Token);
        _started = true;

        if (TryResolveLiveProfile(out var live, out var info))
        {
            PhysicalProfileCache.Save(DeviceId, _device.Physical, live, info);
            CreateBank(live);
            _pump = PumpAsync(_cts.Token);
        }
        else if (PhysicalProfileCache.TryLoad(DeviceId, _device.Physical, out var cached))
        {
            LogProfile("cache", cached);
            CreateBank(cached);
            _pump = PumpAsync(_cts.Token);
            _bringUp = WatchAndRefreshFromPhysicalAsync(_cts.Token);
        }
        else
        {
            Console.WriteLine(
                $"[spoof:{DeviceId}] no cached descriptor for {_device.Physical.Vid}:{_device.Physical.Pid} — " +
                "waiting for physical (virtual devices will appear after first connect)");
            _bringUp = BringUpWhenPhysicalLearnedAsync(_cts.Token);
        }

        await Task.Yield();
        Console.WriteLine(
            _bank is null
                ? $"[spoof:{DeviceId}] learning mode — plug in {_device.Physical.Vid}:{_device.Physical.Pid} once"
                : $"[spoof:{DeviceId}] ready — active slot {_bank.ActiveSlot}; physical may hotplug");
    }

    private bool TryResolveLiveProfile(out HMProfile profile, out HMHidDeviceInfo info)
    {
        profile = null!;
        info = null!;
        var reader = _reader!;
        if (!reader.TryResolveDeviceInfo(out info!))
            return false;

        profile = HMDeviceExtractor.Extract(info);
        profile = HMDeviceExtractor.ResolveDeviceModel(profile);
        LogProfile("physical", profile);
        return !string.IsNullOrWhiteSpace(profile.DescriptorHex);
    }

    private async Task BringUpWhenPhysicalLearnedAsync(CancellationToken token)
    {
        Console.WriteLine(
            $"[spoof:{DeviceId}] waiting for physical {_device.Physical.Vid}:{_device.Physical.Pid} to record descriptor…");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (TryResolveLiveProfile(out var live, out var info))
                {
                    PhysicalProfileCache.Save(DeviceId, _device.Physical, live, info);
                    CreateBank(live);
                    _pump = PumpAsync(token);
                    Console.WriteLine($"[spoof:{DeviceId}] descriptor learned — virtual bank up, active slot {_bank!.ActiveSlot}");
                    await WatchAndRefreshFromPhysicalAsync(token).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[spoof:{DeviceId}] learn wait: {ex.Message}");
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchAndRefreshFromPhysicalAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                if (!TryResolveLiveProfile(out var live, out var info))
                    continue;

                PhysicalProfileCache.Save(DeviceId, _device.Physical, live, info);

                var hex = live.DescriptorHex ?? "";
                string? current;
                lock (_gate) current = _baseDescriptorHex;
                if (string.Equals(current, hex, StringComparison.OrdinalIgnoreCase))
                    continue;

                Console.WriteLine($"[spoof:{DeviceId}] physical descriptor changed — refreshing virtual bank");
                CreateBank(live);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[spoof:{DeviceId}] profile watch: {ex.Message}");
            }
        }
    }

    private void CreateBank(HMProfile baseProfile)
    {
        var reader = _reader ?? throw new InvalidOperationException("Physical reader missing.");

        lock (_gate)
        {
            _bank?.Dispose();
            _bank = null;

            var bank = new VirtualDeviceBank(
                _context,
                baseProfile,
                _device.VirtualDevices,
                _device.ActiveSlot,
                transportBase: _deviceIndex * 4,
                identityPrefix: $"s16n-hid-spoof/{DeviceId}");
            bank.AttachOutputHandlers();

            if (_device.ForwardOutputToPhysical)
            {
                bank.OutputFromActive += (_, packet) =>
                {
                    var data = packet.Data.Span;
                    var framed = new byte[data.Length + 1];
                    framed[0] = packet.ReportId;
                    data.CopyTo(framed.AsSpan(1));
                    reader.WriteOutputReport(framed);
                };
            }

            _bank = bank;
            _baseDescriptorHex = baseProfile.DescriptorHex;
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var reader = _reader!;
        await foreach (var payload in reader.Reports.ReadAllAsync(token).ConfigureAwait(false))
        {
            VirtualDeviceBank? bank;
            lock (_gate) bank = _bank;
            if (bank is null)
                continue;

            try
            {
                bank.SubmitToActive(payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[spoof:{DeviceId}] submit failed: {ex.Message}");
            }
        }
    }

    private void LogProfile(string source, HMProfile profile)
    {
        var descriptorHex = profile.DescriptorHex ?? "";
        Console.WriteLine(
            $"[spoof:{DeviceId}] base profile ({source}): {profile.DisplayName} " +
            $"descriptor={descriptorHex.Length / 2} bytes " +
            $"inputReportSize={profile.InputReportSize} " +
            $"buttons={profile.ButtonCount} axes={profile.AxisCount}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_bringUp is not null)
        {
            try { await _bringUp.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            _bringUp = null;
        }

        if (_reader is not null)
        {
            try { await _reader.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            _reader = null;
        }

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            _pump = null;
        }

        lock (_gate)
        {
            try { _bank?.Dispose(); } catch { /* ignore */ }
            _bank = null;
        }

        _started = false;
        _cts.Dispose();
    }
}
