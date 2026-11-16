using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof.Ui;

/// <summary>Per-device tab: source status, HidHide, virtual slots, optional FFBeast reset, log.</summary>
internal sealed class DeviceTabPage : TabPage
{
    private readonly DeviceOptions _device;
    private readonly Label _physicalStatus;
    private readonly Label _httpStatus;
    private readonly CheckBox _hidHide;
    private readonly Button? _ffbeastReset;
    private readonly TextBox _log;
    private readonly SlotRow[] _slots;
    private readonly HidHidePhysical _hidHidePhysical;
    private SpoofService? _spoof;
    private bool _hidHideProbed;

    public DeviceTabPage(DeviceOptions device, int tabInnerWidth, int tabInnerHeight)
    {
        _device = device;
        Text = device.DisplayName;
        BackColor = Brand.PanelBg;
        Padding = new Padding(8);
        UseVisualStyleBackColor = false;

        var contentW = tabInnerWidth - 16;
        var y = 8;

        _physicalStatus = MakeLabel(8, y, contentW, 36, "Starting…");
        y += 40;

        _httpStatus = MakeLabel(8, y, contentW, 18, "");
        _httpStatus.ForeColor = Brand.Muted;

        if (device.Enabled && FfbeastFlightReset.IsSupported(device.Physical))
        {
            const int btnW = 110;
            _httpStatus.Width = contentW - btnW - 8;
            _ffbeastReset = new Button
            {
                Left = 8 + contentW - btnW,
                Top = y - 4,
                Width = btnW,
                Height = 26,
                Text = "FFBeast reset",
                FlatStyle = FlatStyle.System,
                Enabled = false
            };
            _ffbeastReset.Click += FfbeastResetClicked;
            Controls.Add(_ffbeastReset);
        }

        y += 26;

        _hidHidePhysical = new HidHidePhysical(device.Physical);
        _hidHide = new CheckBox
        {
            Left = 8,
            Top = y,
            Width = contentW,
            Height = 22,
            Text = device.Enabled
                ? "Hide physical device with HidHide (checking…)"
                : "Hide physical device with HidHide (device disabled)",
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Enabled = false
        };
        _hidHide.CheckedChanged += HidHideChanged;
        Controls.Add(_hidHide);
        y += 28;

        if (!device.Enabled)
        {
            var disabled = MakeLabel(8, y, contentW, 40,
                "This device is disabled in appsettings.json (Devices[].Enabled = false).");
            disabled.ForeColor = Brand.Muted;
            _log = new TextBox { Visible = false };
            _slots = [];
            return;
        }

        Controls.Add(MakeSectionTitle(8, y, "Virtual slots"));
        y += 22;

        _slots = new SlotRow[4];
        for (var i = 0; i < 4; i++)
        {
            _slots[i] = new SlotRow(this, i, 8, y, contentW);
            if (i < device.VirtualDevices.Count)
                _slots[i].SetConfig(device.VirtualDevices[i]);
            y += 52;
        }

        Controls.Add(MakeSectionTitle(8, y, "Log"));
        y += 22;

        var logH = Math.Max(80, tabInnerHeight - y - 12);
        _log = new TextBox
        {
            Left = 8,
            Top = y,
            Width = contentW,
            Height = logH,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Brand.Text,
            Font = new Font("Consolas", 8.25f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_log);
    }

    public string DeviceId =>
        string.IsNullOrWhiteSpace(_device.Id) ? Text : _device.Id;

    public void Bind(SpoofService? spoof) => _spoof = spoof;

    public async Task ProbeHidHideAsync()
    {
        if (!_device.Enabled || _hidHideProbed)
            return;
        _hidHideProbed = true;
        try
        {
            var ok = await HidHidePhysical.ProbeAvailableAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            if (IsDisposed)
                return;
            _hidHide.Enabled = ok;
            _hidHide.Text = ok
                ? "Hide physical device with HidHide"
                : "Hide physical device with HidHide (not available)";
            if (!ok)
                AppendLog("[hidhide] driver not reachable (is HidHide Client closed / service running?)");
        }
        catch (Exception ex)
        {
            if (IsDisposed)
                return;
            _hidHide.Enabled = false;
            _hidHide.Text = "Hide physical device with HidHide (not available)";
            AppendLog($"[hidhide] {ex.Message}");
        }
    }

    public void RefreshStatus(string httpUrl)
    {
        if (!_device.Enabled)
        {
            _physicalStatus.Text = "Disabled";
            _physicalStatus.ForeColor = Brand.Muted;
            _httpStatus.Text = httpUrl;
            return;
        }

        if (_spoof is null || !_spoof.IsRunning)
            return;

        try
        {
            var reader = _spoof.Reader;
            var waiting = string.IsNullOrEmpty(reader.DevicePath);
            var path = waiting ? "waiting for device (hotplug)…" : reader.DevicePath!;
            var product = waiting ? "—" : (reader.ProductString ?? "—");
            _physicalStatus.Text =
                $"Source  {_device.Physical.Vid}:{_device.Physical.Pid}  ·  {product}\n{Elide(path, 72)}";
            _physicalStatus.ForeColor = waiting ? Brand.Muted : Brand.Text;

            if (_ffbeastReset is not null)
                _ffbeastReset.Enabled = !waiting;

            if (_spoof.IsReady)
            {
                _httpStatus.Text = $"{httpUrl}  ·  active {_spoof.Bank.ActiveSlot}" +
                    (waiting ? "  ·  cached descriptor" : "");
                var slots = _spoof.Bank.Slots;
                for (var i = 0; i < 4; i++)
                    _slots[i].Update(slots[i]);
            }
            else
            {
                _httpStatus.Text = _spoof.HasCachedDescriptor
                    ? $"{httpUrl}  ·  starting…"
                    : $"{httpUrl}  ·  plug in physical once to learn descriptor";
            }
        }
        catch (Exception ex)
        {
            _physicalStatus.Text = ex.Message;
            _physicalStatus.ForeColor = Brand.Red;
        }
    }

    public void AppendLog(string line)
    {
        if (IsDisposed || !_log.Visible)
            return;

        void Write()
        {
            if (_log.IsDisposed)
                return;
            var text = line.Replace("\r\n", "\n").TrimEnd();
            if (text.Length == 0)
                return;
            if (_log.TextLength > 0)
                _log.AppendText(Environment.NewLine);
            _log.AppendText(text);
        }

        if (InvokeRequired)
            BeginInvoke(Write);
        else
            Write();
    }

    public void DisposeHidHide()
    {
        try { _hidHidePhysical.Dispose(); }
        catch { /* ignore */ }
    }

    private void SelectSlot(int slot)
    {
        if (_spoof is null || !_spoof.IsReady)
            return;
        if (!_spoof.Bank.TrySelect(slot, out var error))
        {
            AppendLog(error ?? "select failed");
            return;
        }

        RefreshStatus(""); // caller refresh will fill URL; keep slot state current
        if (_spoof.IsReady)
        {
            var slots = _spoof.Bank.Slots;
            for (var i = 0; i < 4; i++)
                _slots[i].Update(slots[i]);
        }
    }

    private async void HidHideChanged(object? sender, EventArgs e)
    {
        var want = _hidHide.Checked;
        _hidHide.Enabled = false;
        try
        {
            await _hidHidePhysical.SetHiddenAsync(want, TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            AppendLog(want
                ? "[hidhide] physical device hidden from other apps"
                : "[hidhide] physical device visible again");
        }
        catch (Exception ex)
        {
            AppendLog($"[hidhide] {ex.Message}");
            MessageBox.Show(FindForm(), ex.Message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _hidHide.CheckedChanged -= HidHideChanged;
            _hidHide.Checked = _hidHidePhysical.IsApplied;
            _hidHide.CheckedChanged += HidHideChanged;
        }
        finally
        {
            if (!IsDisposed)
                _hidHide.Enabled = true;
        }
    }

    private void FfbeastResetClicked(object? sender, EventArgs e)
    {
        if (_ffbeastReset is null)
            return;

        _ffbeastReset.Enabled = false;
        try
        {
            var (ok, message) = FfbeastFlightReset.TryReset(_device.Physical);
            AppendLog(ok ? $"[ffbeast] {message}" : $"[ffbeast] reset failed: {message}");
            if (!ok)
                MessageBox.Show(FindForm(), message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"[ffbeast] {ex.Message}");
            MessageBox.Show(FindForm(), ex.Message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && _ffbeastReset is not null)
                _ffbeastReset.Enabled = _spoof?.Reader.DevicePath is { Length: > 0 };
        }
    }

    private Label MakeLabel(int x, int y, int w, int h, string text)
    {
        var lbl = new Label
        {
            Left = x,
            Top = y,
            Width = w,
            Height = h,
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Text = text
        };
        Controls.Add(lbl);
        return lbl;
    }

    private static Label MakeSectionTitle(int x, int y, string text) => new()
    {
        Left = x,
        Top = y,
        Width = 200,
        Height = 18,
        Text = text,
        ForeColor = Brand.Text,
        BackColor = Brand.PanelBg,
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
    };

    private static string Elide(string value, int max)
    {
        if (value.Length <= max)
            return value;
        if (max < 8)
            return value[..max];
        var keep = max - 3;
        var head = keep / 2;
        var tail = keep - head;
        return value[..head] + "..." + value[^tail..];
    }

    private sealed class SlotRow
    {
        private readonly DeviceTabPage _tab;
        private readonly int _index;
        private readonly Panel _panel;
        private readonly CheckBox _enabled;
        private readonly Label _title;
        private readonly Label _meta;
        private readonly Button _activate;
        private readonly Panel _dot;

        public SlotRow(DeviceTabPage tab, int index, int x, int y, int width)
        {
            _tab = tab;
            _index = index;

            _panel = new Panel
            {
                Left = x,
                Top = y,
                Width = width,
                Height = 46,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            _dot = new Panel
            {
                Left = 10,
                Top = 16,
                Width = 12,
                Height = 12,
                BackColor = Brand.Muted
            };

            _enabled = new CheckBox
            {
                Left = 30,
                Top = 12,
                Width = 78,
                Height = 20,
                Text = "Enabled",
                Enabled = false,
                ForeColor = Brand.Muted
            };

            _title = new Label
            {
                Left = 112,
                Top = 6,
                Width = width - 220,
                Height = 18,
                ForeColor = Brand.Text,
                BackColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Text = $"slot {index}"
            };

            _meta = new Label
            {
                Left = 112,
                Top = 24,
                Width = width - 220,
                Height = 16,
                ForeColor = Brand.Muted,
                BackColor = Color.White,
                Text = ""
            };

            _activate = new Button
            {
                Left = width - 92,
                Top = 10,
                Width = 80,
                Height = 26,
                Text = "Activate",
                FlatStyle = FlatStyle.System
            };
            _activate.Click += (_, _) => _tab.SelectSlot(_index);

            _panel.Controls.Add(_dot);
            _panel.Controls.Add(_enabled);
            _panel.Controls.Add(_title);
            _panel.Controls.Add(_meta);
            _panel.Controls.Add(_activate);
            tab.Controls.Add(_panel);
        }

        public void SetConfig(VirtualDeviceOptions opt)
        {
            _enabled.Checked = opt.Enabled;
            _title.Text = string.IsNullOrWhiteSpace(opt.ProductString)
                ? $"slot {opt.Id}"
                : opt.ProductString!;
            _meta.Text = $"{opt.Vid}:{opt.Pid}  ·  {opt.Id}";
            _activate.Enabled = opt.Enabled;
            if (!opt.Enabled)
            {
                _panel.BackColor = Color.FromArgb(0xF0, 0xF0, 0xEE);
                _title.ForeColor = Brand.Muted;
            }
        }

        public void Update(VirtualSlotInfo info)
        {
            _enabled.Checked = info.Enabled;
            _title.Text = info.ProductString;
            _meta.Text = $"{info.Vid}:{info.Pid}  ·  {info.Id}" +
                         (info.TransportIndex >= 0 ? $"  ·  t{info.TransportIndex}" : "");
            _activate.Enabled = info.Enabled;
            _activate.Text = info.Active ? "Active" : "Activate";
            _dot.BackColor = !info.Enabled ? Brand.Muted : info.Active ? Brand.Green : Brand.Line;
        }
    }
}
