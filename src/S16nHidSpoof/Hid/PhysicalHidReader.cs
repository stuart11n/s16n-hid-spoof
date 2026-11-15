using System.Threading.Channels;
using HIDMaestro;
using HidSharp;
using S16nHidSpoof.Config;

namespace S16nHidSpoof.Hid;

/// <summary>
/// Reads raw HID input reports from one physical VID:PID device.
/// HidSharp prefixes Report ID as byte 0; HIDMaestro SubmitRawReport wants
/// data bytes only when the profile declares a Report ID.
/// </summary>
public sealed class PhysicalHidReader : IAsyncDisposable
{
    private readonly PhysicalDeviceOptions _options;
    private readonly Channel<byte[]> _reports =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HidStream? _stream;

    public PhysicalHidReader(PhysicalDeviceOptions options)
    {
        _options = options;
    }

    public string? DevicePath { get; private set; }
    public string? ProductString { get; private set; }
    public int MaxInputReportLength { get; private set; }
    public bool StripReportId { get; set; } = true;
    public HMHidDeviceInfo? SelectedInfo { get; private set; }

    public ChannelReader<byte[]> Reports => _reports.Reader;

    public HMHidDeviceInfo ResolveDeviceInfo()
    {
        var matches = HMDeviceExtractor.ListDevices()
            .Where(d => d.VendorId == _options.VendorId && d.ProductId == _options.ProductId)
            .ToList();

        if (_options.UsagePage is ushort page)
            matches = matches.Where(d => d.TopLevelUsagePage == page).ToList();
        if (_options.Usage is ushort usage)
            matches = matches.Where(d => d.TopLevelUsage == usage).ToList();

        // Prefer a gamepad (0x05) or joystick (0x04) collection when several interfaces exist.
        var preferred = matches.FirstOrDefault(d =>
                d.TopLevelUsagePage == 0x01 && d.TopLevelUsage is 0x04 or 0x05)
            ?? matches.FirstOrDefault();

        if (preferred is not null)
            return preferred;

        var seen = HMDeviceExtractor.ListDevices()
            .GroupBy(d => (d.VendorId, d.ProductId, d.TopLevelUsagePage, d.TopLevelUsage, d.ProductString))
            .Select(g => g.Key)
            .OrderBy(d => d.VendorId)
            .ThenBy(d => d.ProductId)
            .ToList();
        var gamepads = seen
            .Where(d => d.TopLevelUsagePage == 0x01 && d.TopLevelUsage is 0x04 or 0x05)
            .ToList();
        var lines = (gamepads.Count > 0 ? gamepads : seen)
            .Select(d =>
                $"  {d.VendorId:X4}:{d.ProductId:X4} usage {d.TopLevelUsagePage:X4}/{d.TopLevelUsage:X2} {d.ProductString}");

        throw new InvalidOperationException(
            $"No HID device found for VID {_options.Vid} PID {_options.Pid}.{Environment.NewLine}" +
            (gamepads.Count > 0
                ? "Connected gamepads:"
                : "No gamepad or joystick was enumerated. Other HID devices:") +
            Environment.NewLine +
            string.Join(Environment.NewLine, lines));
    }

    public void Start(CancellationToken hostToken)
    {
        if (_loop is not null)
            throw new InvalidOperationException("Physical reader already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _loop = Task.Factory.StartNew(
            () => ReadLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _loop = null;
        }

        _stream?.Dispose();
        _stream = null;
        _reports.Writer.TryComplete();
    }

    public void WriteOutputReport(ReadOnlySpan<byte> reportWithId)
    {
        var stream = _stream;
        if (stream is null)
            return;

        var buffer = reportWithId.ToArray();
        try
        {
            stream.Write(buffer);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private async Task ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                OpenDevice();
                var buffer = new byte[MaxInputReportLength];
                while (!token.IsCancellationRequested)
                {
                    int count;
                    try
                    {
                        count = _stream!.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (count <= 0)
                        continue;

                    var payload = BuildPayload(buffer.AsSpan(0, count));
                    if (payload.Length == 0)
                        continue;

                    await _reports.Writer.WriteAsync(payload, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[physical] read error: {ex.Message}; retrying in 1s");
                _stream?.Dispose();
                _stream = null;
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void OpenDevice()
    {
        _stream?.Dispose();
        _stream = null;

        var info = ResolveDeviceInfo();
        SelectedInfo = info;

        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => string.Equals(d.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase))
            ?? DeviceList.Local.GetHidDevices(_options.VendorId, _options.ProductId).FirstOrDefault()
            ?? throw new InvalidOperationException($"HidSharp could not open path {info.DevicePath}.");

        if (!device.TryOpen(out var stream))
            throw new InvalidOperationException($"Failed to open {device.DevicePath}.");

        stream.ReadTimeout = Timeout.Infinite;
        stream.WriteTimeout = 1000;
        _stream = stream;
        DevicePath = device.DevicePath;
        ProductString = string.IsNullOrWhiteSpace(info.ProductString) ? "unknown" : info.ProductString;
        MaxInputReportLength = Math.Max(device.GetMaxInputReportLength(), (int)info.InputReportByteLength);
        Console.WriteLine(
            $"[physical] opened {ProductString} {_options.Vid}:{_options.Pid} " +
            $"usage={info.TopLevelUsagePage:X4}/{info.TopLevelUsage:X2} ({DevicePath})");
    }

    private byte[] BuildPayload(ReadOnlySpan<byte> report)
    {
        if (!StripReportId)
            return report.ToArray();

        if (report.Length <= 1)
            return [];

        return report[1..].ToArray();
    }
}
