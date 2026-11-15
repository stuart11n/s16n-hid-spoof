using HIDMaestro;
using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof;

public sealed class SpoofService : IAsyncDisposable
{
    private readonly SpoofOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private HMContext? _context;
    private PhysicalHidReader? _reader;
    private VirtualDeviceBank? _bank;
    private Task? _pump;

    public SpoofService(SpoofOptions options)
    {
        _options = options;
    }

    public VirtualDeviceBank Bank =>
        _bank ?? throw new InvalidOperationException("Service not started.");

    public PhysicalHidReader Reader =>
        _reader ?? throw new InvalidOperationException("Service not started.");

    public bool IsRunning => _bank is not null;

    public async Task StartAsync()
    {
        if (_options.VirtualDevices.Count != 4)
            throw new InvalidOperationException("VirtualDevices must contain exactly 4 entries.");

        Console.WriteLine("[spoof] installing HIDMaestro driver (elevated)...");
        _context = new HMContext();
        _context.InstallDriver();

        _reader = new PhysicalHidReader(_options.Physical);
        var info = _reader.ResolveDeviceInfo();
        var baseProfile = HMDeviceExtractor.Extract(info);
        // Prefer catalog device-model answers when the physical pad is a known model.
        baseProfile = HMDeviceExtractor.ResolveDeviceModel(baseProfile);

        var descriptorHex = baseProfile.DescriptorHex ?? "";
        Console.WriteLine(
            $"[spoof] base profile from physical: {baseProfile.DisplayName} " +
            $"descriptor={descriptorHex.Length / 2} bytes " +
            $"inputReportSize={baseProfile.InputReportSize}");

        // HidSharp includes Report ID as byte 0. Strip when the profile size is one less
        // than the physical max input length, or when InputReportSize is known.
        _reader.StripReportId = true;

        _bank = new VirtualDeviceBank(_context, baseProfile, _options.VirtualDevices, _options.ActiveSlot);
        _bank.AttachOutputHandlers();

        if (_options.ForwardOutputToPhysical)
        {
            _bank.OutputFromActive += (_, packet) =>
            {
                // HidSharp Write expects Report ID in byte 0.
                var data = packet.Data.Span;
                var framed = new byte[data.Length + 1];
                framed[0] = packet.ReportId;
                data.CopyTo(framed.AsSpan(1));
                _reader.WriteOutputReport(framed);
            };
        }

        _reader.Start(_cts.Token);
        _pump = PumpAsync(_cts.Token);

        // Give the reader a moment to open; selection API works either way.
        await Task.Delay(50).ConfigureAwait(false);
        Console.WriteLine($"[spoof] ready — active slot {_bank.ActiveSlot}; API selecting slots 0..3");
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

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (_reader is not null)
            await _reader.DisposeAsync().ConfigureAwait(false);

        _bank?.Dispose();
        _context?.Dispose();
        _cts.Dispose();
    }
}
