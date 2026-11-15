using HIDMaestro;
using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof;

public sealed class SpoofService : IAsyncDisposable
{
    private readonly SpoofOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private HMContext? _context;
    private PhysicalHidReader? _reader;
    private VirtualDeviceBank? _bank;
    private Task? _pump;
    private Task? _bringUp;
    private string? _baseDescriptorHex;
    private bool _started;

    public SpoofService(SpoofOptions options)
    {
        _options = options;
    }

    public VirtualDeviceBank Bank =>
        _bank ?? throw new InvalidOperationException(
            PhysicalProfileCache.ExistsFor(_options.Physical)
                ? "Virtual bank not ready yet."
                : "No cached physical descriptor yet — plug the controller in once so it can be recorded.");

    public PhysicalHidReader Reader =>
        _reader ?? throw new InvalidOperationException("Service not started.");

    /// <summary>Driver/reader started (HTTP/UI up). Virtual bank may still be waiting on first learn.</summary>
    public bool IsRunning => _started;

    /// <summary>Virtual bank is live (from live device or cached descriptor).</summary>
    public bool IsReady => _bank is not null;

    public bool HasCachedDescriptor => PhysicalProfileCache.ExistsFor(_options.Physical);

    public async Task StartAsync()
    {
        if (_options.VirtualDevices.Count != 4)
            throw new InvalidOperationException("VirtualDevices must contain exactly 4 entries.");

        Console.WriteLine("[spoof] installing HIDMaestro driver (elevated)...");
        _context = new HMContext();
        _context.InstallDriver();

        Console.WriteLine("[spoof] reclaiming leftover virtual controllers…");
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true);

        _reader = new PhysicalHidReader(_options.Physical) { StripReportId = true };
        _reader.Start(_cts.Token);
        _started = true;

        if (TryResolveLiveProfile(out var live, out var info))
        {
            PhysicalProfileCache.Save(_options.Physical, live, info);
            CreateBank(live);
            _pump = PumpAsync(_cts.Token);
        }
        else if (PhysicalProfileCache.TryLoad(_options.Physical, out var cached))
        {
            LogProfile("cache", cached);
            CreateBank(cached);
            _pump = PumpAsync(_cts.Token);
            // When the stick appears, refresh cache + bank if the descriptor changed.
            _bringUp = WatchAndRefreshFromPhysicalAsync(_cts.Token);
        }
        else
        {
            Console.WriteLine(
                $"[spoof] no cached descriptor for {_options.Physical.Vid}:{_options.Physical.Pid} — " +
                "waiting for physical (virtual devices will appear after first connect)");
            _bringUp = BringUpWhenPhysicalLearnedAsync(_cts.Token);
        }

        await Task.Yield();
        Console.WriteLine(
            _bank is null
                ? $"[spoof] learning mode — plug in {_options.Physical.Vid}:{_options.Physical.Pid} once"
                : $"[spoof] ready — active slot {_bank.ActiveSlot}; physical may hotplug");
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
        var reader = _reader!;
        Console.WriteLine(
            $"[spoof] waiting for physical {_options.Physical.Vid}:{_options.Physical.Pid} to record descriptor…");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (TryResolveLiveProfile(out var live, out var info))
                {
                    PhysicalProfileCache.Save(_options.Physical, live, info);
                    CreateBank(live);
                    _pump = PumpAsync(token);
                    Console.WriteLine($"[spoof] descriptor learned — virtual bank up, active slot {_bank!.ActiveSlot}");
                    // Keep watching for descriptor changes while running.
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
                Console.Error.WriteLine($"[spoof] learn wait: {ex.Message}");
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

                PhysicalProfileCache.Save(_options.Physical, live, info);

                var hex = live.DescriptorHex ?? "";
                string? current;
                lock (_gate) current = _baseDescriptorHex;
                if (string.Equals(current, hex, StringComparison.OrdinalIgnoreCase))
                    continue;

                Console.WriteLine("[spoof] physical descriptor changed — refreshing virtual bank");
                CreateBank(live);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[spoof] profile watch: {ex.Message}");
            }
        }
    }

    private void CreateBank(HMProfile baseProfile)
    {
        var context = _context ?? throw new InvalidOperationException("HIDMaestro context missing.");
        var reader = _reader ?? throw new InvalidOperationException("Physical reader missing.");

        lock (_gate)
        {
            _bank?.Dispose();
            _bank = null;

            var bank = new VirtualDeviceBank(context, baseProfile, _options.VirtualDevices, _options.ActiveSlot);
            bank.AttachOutputHandlers();

            if (_options.ForwardOutputToPhysical)
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
                Console.Error.WriteLine($"[spoof] submit failed: {ex.Message}");
            }
        }
    }

    private static void LogProfile(string source, HMProfile profile)
    {
        var descriptorHex = profile.DescriptorHex ?? "";
        Console.WriteLine(
            $"[spoof] base profile ({source}): {profile.DisplayName} " +
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

        try { _context?.Dispose(); } catch { /* ignore */ }
        _context = null;
        _started = false;
        _cts.Dispose();
    }
}
