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
    private bool _started;

    public SpoofService(SpoofOptions options)
    {
        _options = options;
    }

    public VirtualDeviceBank Bank =>
        _bank ?? throw new InvalidOperationException("Waiting for physical device.");

    public PhysicalHidReader Reader =>
        _reader ?? throw new InvalidOperationException("Service not started.");

    /// <summary>Driver/reader started (HTTP/UI up). Physical may still be absent.</summary>
    public bool IsRunning => _started;

    /// <summary>Virtual bank is live — physical was seen at least once this session.</summary>
    public bool IsReady => _bank is not null;

    public async Task StartAsync()
    {
        if (_options.VirtualDevices.Count != 4)
            throw new InvalidOperationException("VirtualDevices must contain exactly 4 entries.");

        Console.WriteLine("[spoof] installing HIDMaestro driver (elevated)...");
        _context = new HMContext();
        _context.InstallDriver();

        _reader = new PhysicalHidReader(_options.Physical) { StripReportId = true };
        _reader.Start(_cts.Token);
        _started = true;

        // Virtual devices need a HID report descriptor from the physical unit.
        // If it isn't plugged in yet, wait — that is hotplug, not a startup error.
        _bringUp = BringUpWhenPhysicalPresentAsync(_cts.Token);

        await Task.Yield();
        Console.WriteLine(
            $"[spoof] ready — waiting for {_options.Physical.Vid}:{_options.Physical.Pid} if needed; " +
            "API selecting slots 0..3 once virtual bank is up");
    }

    private async Task BringUpWhenPhysicalPresentAsync(CancellationToken token)
    {
        var reader = _reader!;
        var context = _context!;

        Console.WriteLine(
            $"[spoof] waiting for physical {_options.Physical.Vid}:{_options.Physical.Pid} (hotplug ok)…");

        HMHidDeviceInfo info;
        while (!reader.TryResolveDeviceInfo(out info!))
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1000, token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();

        var baseProfile = HMDeviceExtractor.Extract(info);
        baseProfile = HMDeviceExtractor.ResolveDeviceModel(baseProfile);

        var descriptorHex = baseProfile.DescriptorHex ?? "";
        Console.WriteLine(
            $"[spoof] base profile from physical: {baseProfile.DisplayName} " +
            $"descriptor={descriptorHex.Length / 2} bytes " +
            $"inputReportSize={baseProfile.InputReportSize}");

        VirtualDeviceBank bank;
        lock (_gate)
        {
            if (_bank is not null || token.IsCancellationRequested)
                return;

            bank = new VirtualDeviceBank(context, baseProfile, _options.VirtualDevices, _options.ActiveSlot);
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
            _pump = PumpAsync(token);
        }

        Console.WriteLine($"[spoof] virtual bank up — active slot {bank.ActiveSlot}");
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var reader = _reader!;
        var bank = _bank!;
        await foreach (var payload in reader.Reports.ReadAllAsync(token).ConfigureAwait(false))
        {
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

        try { _bank?.Dispose(); } catch { /* ignore */ }
        _bank = null;
        try { _context?.Dispose(); } catch { /* ignore */ }
        _context = null;
        _started = false;
        _cts.Dispose();
    }
}
