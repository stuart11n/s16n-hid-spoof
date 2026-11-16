using System.Threading.Channels;
using HIDMaestro;
using HidSharp;
using S16nHidSpoof.Config;

namespace S16nHidSpoof.Hid;

/// <summary>
/// Reads raw HID input reports from one physical VID:PID device.
/// Supports hotplug: missing device at start is waiting, not failure.
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
    private bool _loggedWaiting;

    public PhysicalHidReader(PhysicalDeviceOptions options)
    {
        _options = options;
    }

    public string? DevicePath { get; private set; }
    public string? ProductString { get; private set; }
    public int MaxInputReportLength { get; private set; }
    public bool StripReportId { get; set; } = true;
    public HMHidDeviceInfo? SelectedInfo { get; private set; }
    public bool IsOpen => _stream is not null;

    public ChannelReader<byte[]> Reports => _reports.Reader;

    public bool TryResolveDeviceInfo(out HMHidDeviceInfo info)
    {
        var matches = HMDeviceExtractor.ListDevices()
            .Where(d => d.VendorId == _options.VendorId && d.ProductId == _options.ProductId)
            .ToList();

        if (_options.UsagePage is ushort page)
            matches = matches.Where(d => d.TopLevelUsagePage == page).ToList();
        if (_options.Usage is ushort usage)
            matches = matches.Where(d => d.TopLevelUsage == usage).ToList();
        if (!string.IsNullOrWhiteSpace(_options.DevicePathContains))
        {
            var needle = _options.DevicePathContains.Trim();
            matches = matches
                .Where(d => (d.DevicePath ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Prefer a gamepad (0x05) or joystick (0x04) collection when several interfaces exist.
        var preferred = matches.FirstOrDefault(d =>
                d.TopLevelUsagePage == 0x01 && d.TopLevelUsage is 0x04 or 0x05)
            ?? matches.FirstOrDefault();

        if (preferred is null)
        {
            info = null!;
            return false;
        }

        info = preferred;
        return true;
    }

    public HMHidDeviceInfo ResolveDeviceInfo()
    {
        if (TryResolveDeviceInfo(out var info))
            return info;

        throw new InvalidOperationException(
            $"No HID device found for VID {_options.Vid} PID {_options.Pid}.");
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
        // Break a blocking HidStream.Read before awaiting the loop,
        // otherwise tray Exit hangs forever with the process still running.
        ClearOpenState();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        _reports.Writer.TryComplete();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("[physical] reader did not stop in time");
            }
            catch (OperationCanceledException) { }
            _loop = null;
        }
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
                _loggedWaiting = false;
                var buffer = new byte[Math.Max(MaxInputReportLength, 64)];
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
                ClearOpenState();
                if (IsDeviceMissing(ex))
                {
                    if (!_loggedWaiting)
                    {
                        Console.WriteLine(
                            $"[physical] waiting for {_options.Vid}:{_options.Pid} (hotplug)…");
                        _loggedWaiting = true;
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[physical] read error: {ex.Message}; retrying in 1s");
                    _loggedWaiting = false;
                }

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
        ClearOpenState();

        var info = ResolveDeviceInfo();
        SelectedInfo = info;

        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => string.Equals(d.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase))
            ?? DeviceList.Local.GetHidDevices(_options.VendorId, _options.ProductId).FirstOrDefault()
            ?? throw new InvalidOperationException($"HidSharp could not open path {info.DevicePath}.");

        if (!device.TryOpen(out var stream))
            throw new InvalidOperationException($"Failed to open {device.DevicePath}.");

        // Finite timeout so cancel/dispose can reclaim the reader without killing the process.
        stream.ReadTimeout = 500;
        stream.WriteTimeout = 1000;
        _stream = stream;
        DevicePath = device.DevicePath;
        ProductString = string.IsNullOrWhiteSpace(info.ProductString) ? "unknown" : info.ProductString;
        MaxInputReportLength = Math.Max(device.GetMaxInputReportLength(), (int)info.InputReportByteLength);
        Console.WriteLine(
            $"[physical] opened {ProductString} {_options.Vid}:{_options.Pid} " +
            $"usage={info.TopLevelUsagePage:X4}/{info.TopLevelUsage:X2} ({DevicePath})");
    }

    private void ClearOpenState()
    {
        try { _stream?.Dispose(); }
        catch { /* ignore */ }
        _stream = null;
        DevicePath = null;
        ProductString = null;
        SelectedInfo = null;
        MaxInputReportLength = 0;
    }

    private static bool IsDeviceMissing(Exception ex) =>
        ex.Message.Contains("No HID device found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("could not open path", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Failed to open", StringComparison.OrdinalIgnoreCase);

    private byte[] BuildPayload(ReadOnlySpan<byte> report)
    {
        if (!StripReportId)
            return report.ToArray();

        if (report.Length <= 1)
            return [];

        return report[1..].ToArray();
    }
}
