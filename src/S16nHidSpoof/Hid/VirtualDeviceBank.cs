using HIDMaestro;
using S16nHidSpoof.Config;

namespace S16nHidSpoof.Hid;

public sealed class VirtualDeviceBank : IDisposable
{
    private readonly object _gate = new();
    private readonly HMController?[] _controllers;
    private readonly VirtualDeviceOptions[] _options;
    private int _activeSlot;
    private byte[]? _lastPayload;
    private bool _disposed;

    public VirtualDeviceBank(
        HMContext context,
        HMProfile baseProfile,
        IReadOnlyList<VirtualDeviceOptions> devices,
        int activeSlot,
        int transportBase = 0,
        string identityPrefix = "s16n-hid-spoof")
    {
        if (devices.Count != 4)
            throw new ArgumentException("Exactly four virtual devices are required.", nameof(devices));
        if (transportBase < 0)
            throw new ArgumentOutOfRangeException(nameof(transportBase));

        _options = devices.ToArray();
        _controllers = new HMController?[4];
        var prefix = string.IsNullOrWhiteSpace(identityPrefix) ? "s16n-hid-spoof" : identityPrefix.Trim();

        for (var i = 0; i < 4; i++)
        {
            var opt = _options[i];
            if (!opt.Enabled)
            {
                Console.WriteLine($"[virtual:{prefix}] slot {i}: disabled");
                continue;
            }

            // Catalog id + identity key must be unique; sticky keys from Environment.Exit
            // leftovers make CreateDeviceNode fail even after a partial reclaim.
            var transport = transportBase + i;
            var stamp = $"{Environment.ProcessId}-{transport}";
            var profileId = string.IsNullOrWhiteSpace(opt.Id)
                ? $"{prefix}/slot-{stamp}"
                : $"{prefix}/{opt.Id}-{stamp}";

            var profile = new HMProfileBuilder()
                .FromProfile(baseProfile)
                .Id(profileId)
                .Vid(opt.VendorId)
                .Pid(opt.ProductId)
                .ProductString(string.IsNullOrWhiteSpace(opt.ProductString)
                    ? $"s16n spoof {i}"
                    : opt.ProductString!)
                .Build();

            HMController ctrl;
            try
            {
                ctrl = context.CreateControllerAt(transport, profile, identityKey: $"{prefix}/{stamp}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[virtual:{prefix}] slot {i} create failed ({ex.Message}); reclaim+retry");
                HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true);
                ctrl = context.CreateControllerAt(transport, profile, identityKey: $"{prefix}/{stamp}-r");
            }

            _controllers[i] = ctrl;
            Console.WriteLine(
                $"[virtual:{prefix}] slot {i}: {opt.Vid}:{opt.Pid} \"{ctrl.Profile.DisplayName}\" transport={ctrl.TransportIndex}");
        }

        if (!_options.Any(d => d.Enabled))
            throw new InvalidOperationException("At least one VirtualDevices entry must have Enabled=true.");

        activeSlot = Math.Clamp(activeSlot, 0, 3);
        if (!_options[activeSlot].Enabled)
            throw new InvalidOperationException(
                $"ActiveSlot {activeSlot} is disabled. Choose an Enabled VirtualDevices entry.");

        _activeSlot = activeSlot;
    }

    public int ActiveSlot
    {
        get { lock (_gate) return _activeSlot; }
    }

    public IReadOnlyList<VirtualSlotInfo> Slots
    {
        get
        {
            lock (_gate)
            {
                return Enumerable.Range(0, 4)
                    .Select(i =>
                    {
                        var ctrl = _controllers[i];
                        return new VirtualSlotInfo(
                            i,
                            _options[i].Id,
                            _options[i].Enabled,
                            _options[i].Vid,
                            _options[i].Pid,
                            ctrl?.Profile.DisplayName ?? _options[i].ProductString ?? $"s16n spoof {i}",
                            i == _activeSlot,
                            ctrl?.TransportIndex ?? -1);
                    })
                    .ToList();
            }
        }
    }

    public event Action<int, HMOutputPacket>? OutputFromActive;

    public void AttachOutputHandlers()
    {
        for (var i = 0; i < 4; i++)
        {
            var ctrl = _controllers[i];
            if (ctrl is null)
                continue;

            var slot = i;
            ctrl.OutputReceived += (_, packet) =>
            {
                if (ActiveSlot != slot)
                    return;
                OutputFromActive?.Invoke(slot, packet);
            };
        }
    }

    public bool TrySelect(int slot, out string? error)
    {
        if (slot is < 0 or > 3)
        {
            error = "slot must be 0..3";
            return false;
        }

        if (!_options[slot].Enabled || _controllers[slot] is null)
        {
            error = $"slot {slot} is disabled";
            return false;
        }

        lock (_gate)
        {
            if (_activeSlot == slot)
            {
                error = null;
                return true;
            }

            // Release buttons/axes on the slot we leave so games don't see stuck input.
            Neutralize(_activeSlot);
            _activeSlot = slot;
            if (_lastPayload is not null)
                _controllers[_activeSlot]!.SubmitRawReport(_lastPayload);
        }

        error = null;
        Console.WriteLine($"[virtual] active slot -> {slot}");
        return true;
    }

    public void SubmitToActive(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            _lastPayload = payload.ToArray();
            _controllers[_activeSlot]?.SubmitRawReport(payload);
        }
    }

    private void Neutralize(int slot)
    {
        var ctrl = _controllers[slot];
        if (ctrl is null)
            return;

        var size = ctrl.Profile.InputReportSize;
        if (size <= 0)
            size = _lastPayload?.Length ?? 0;
        if (size <= 0)
            return;

        var zeros = new byte[size];
        try
        {
            ctrl.SubmitRawReport(zeros);
        }
        catch (ArgumentException)
        {
            // Some personas reject zero-length/size mismatches; ignore.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var ctrl in _controllers)
        {
            try { ctrl?.Dispose(); }
            catch { /* best effort */ }
        }
    }
}

public sealed record VirtualSlotInfo(
    int Index,
    string Id,
    bool Enabled,
    string Vid,
    string Pid,
    string ProductString,
    bool Active,
    int TransportIndex);
