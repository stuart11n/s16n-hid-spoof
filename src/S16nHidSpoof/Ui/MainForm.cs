using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof.Ui;

internal sealed class MainForm : Form
{
    private readonly SpoofOptions _options;
    private readonly bool _startInTray;
    private readonly CheckBox _startWithWindows;
    private readonly Label _httpBanner;
    private readonly TabControl _tabs;
    private readonly DeviceTabPage[] _deviceTabs;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private Image? _logo;
    private SpoofHost? _host;
    private WebApplication? _web;
    private CancellationTokenSource? _runCts;
    private bool _starting;
    private bool _exitRequested;
    private bool _allowShow;
    private bool _uiStarted;
    private bool _hidingToTray;

    private bool PreferTrayOnly =>
        _startInTray || _startWithWindows.Checked;

    public MainForm(SpoofOptions options, string[] args)
    {
        _options = options;
        _startInTray = Autostart.HasTrayArg(args);

        Text = "s16n HID Spoof";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(Brand.ClientW, Brand.ClientH);
        BackColor = Brand.PanelBg;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
        ShowInTaskbar = !_startInTray;
        _allowShow = !_startInTray;
        TryLoadIcon();
        _logo = TryLoadLogo();

        const int folderBtn = 28;
        _startWithWindows = new CheckBox
        {
            Left = 16,
            Top = Brand.HeaderH + 10,
            Width = Brand.ClientW - 32 - folderBtn - 8,
            Height = 22,
            Text = "Start with Windows (elevated tray task)",
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Checked = Autostart.IsEnabled()
        };
        _startWithWindows.CheckedChanged += StartWithWindowsChanged;
        Controls.Add(_startWithWindows);

        // Label + MDL2 glyph — Button text padding clips Segoe MDL2 Assets icons.
        var openConfig = new Label
        {
            Left = Brand.ClientW - 16 - folderBtn,
            Top = Brand.HeaderH + 7,
            Width = folderBtn,
            Height = folderBtn,
            Text = "\uE8B7", // Segoe MDL2 OpenFolder
            Font = new Font("Segoe MDL2 Assets", 14f),
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        openConfig.Click += (_, _) => OpenConfigFolder();
        openConfig.MouseEnter += (_, _) => openConfig.ForeColor = Brand.Muted;
        openConfig.MouseLeave += (_, _) => openConfig.ForeColor = Brand.Text;
        Controls.Add(openConfig);
        var folderTip = new ToolTip();
        folderTip.SetToolTip(openConfig, "Open config folder");

        _httpBanner = new Label
        {
            Left = 16,
            Top = Brand.HeaderH + 34,
            Width = Brand.ClientW - 32,
            Height = 18,
            ForeColor = Brand.Muted,
            BackColor = Brand.PanelBg,
            Text = options.Http.Url
        };
        Controls.Add(_httpBanner);

        var tabsTop = Brand.HeaderH + 56;
        var tabsH = Brand.ClientH - tabsTop - Brand.StatusBarH - 4;
        _tabs = new TabControl
        {
            Left = 8,
            Top = tabsTop,
            Width = Brand.ClientW - 16,
            Height = tabsH,
            Font = new Font("Segoe UI", 9f)
        };

        // Use form width — TabControl.ClientSize is often 0 before the handle exists.
        // TabControl is ClientW - 16 wide; the page inside loses ~4px per side to the border.
        var tabInnerW = Brand.ClientW - 24;
        _deviceTabs = new DeviceTabPage[SpoofOptions.DeviceCount];
        for (var i = 0; i < SpoofOptions.DeviceCount; i++)
        {
            var device = options.Devices[i];
            _deviceTabs[i] = new DeviceTabPage(device, tabInnerW, 0);
            _tabs.TabPages.Add(_deviceTabs[i]);
        }

        Controls.Add(_tabs);

        var status = new StatusStrip
        {
            BackColor = Brand.PanelBg,
            SizingGrip = false,
            Font = new Font("Segoe UI", 8.25f),
            Padding = new Padding(2, 0, 8, 0)
        };
        var site = new ToolStripStatusLabel
        {
            Text = "www.s16n.com",
            IsLink = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = Brand.Muted,
            ActiveLinkColor = Brand.Text,
            ToolTipText = "https://www.s16n.com"
        };
        site.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.s16n.com") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"[ui] could not open www.s16n.com: {ex.Message}");
            }
        };
        status.Items.Add(site);
        status.Items.Add(new ToolStripStatusLabel
        {
            Text = $"Build {AppVersion.Current}",
            ForeColor = Brand.Muted,
            Spring = true,
            TextAlign = ContentAlignment.MiddleRight
        });
        Controls.Add(status);

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Show", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitFromTray());

        _tray = new NotifyIcon
        {
            Text = "s16n HID Spoof",
            Icon = (Icon ?? SystemIcons.Application).Clone() as Icon ?? SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _refresh = new System.Windows.Forms.Timer { Interval = 500 };
        _refresh.Tick += (_, _) => RefreshStatus();

        Console.SetOut(new UiTextWriter(AppendLog));
        Console.SetError(new UiTextWriter(AppendLog));
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow)
            value = false;
        base.SetVisibleCore(value);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        EnsureUiStarted();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureUiStarted();
    }

    private void EnsureUiStarted()
    {
        if (_uiStarted)
            return;
        _uiStarted = true;

        if (_startInTray)
        {
            _tray.Visible = true;
            ShowInTaskbar = false;
        }

        foreach (var tab in _deviceTabs)
            _ = tab.ProbeHidHideAsync();
        _ = StartBackendAsync();
        _refresh.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_hidingToTray)
            return;
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _refresh.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _trayMenu.Dispose();
        _runCts?.Cancel();

        foreach (var tab in _deviceTabs)
            tab.DisposeHidHide();

        try
        {
            _web?.StopAsync(TimeSpan.FromSeconds(2))
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }

        try
        {
            _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* ignore */ }

        try
        {
            _web?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch { /* ignore */ }

        base.OnFormClosing(e);

        if (_exitRequested)
        {
            try { SingleInstance.Release(); } catch { /* ignore */ }
            Environment.Exit(0);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var gold = new SolidBrush(Brand.Gold))
            g.FillRectangle(gold, 0, 0, ClientSize.Width, Brand.HeaderH);
        using (var line = new SolidBrush(Brand.Line))
            g.FillRectangle(line, 0, Brand.HeaderH - 1, ClientSize.Width, 1);

        var textLeft = 16;
        if (_logo is not null)
        {
            const int logoDraw = 36;
            var y = (Brand.HeaderH - logoDraw) / 2;
            g.DrawImage(_logo, 12, y, logoDraw, logoDraw);
            textLeft = 12 + logoDraw + 10;
        }

        using var brandFont = new Font("Segoe UI Semibold", 14f, FontStyle.Bold);
        using var titleFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var red = new SolidBrush(Brand.Red);
        using var text = new SolidBrush(Brand.Text);
        g.DrawString("s16n", brandFont, red, textLeft, 6);
        g.DrawString("HID Spoof", titleFont, text, textLeft, 28);
    }

    private void StartWithWindowsChanged(object? sender, EventArgs e)
    {
        if (!Autostart.SetEnabled(_startWithWindows.Checked))
        {
            MessageBox.Show(this, "Could not update Start with Windows setting.", "s16n HID Spoof",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _startWithWindows.CheckedChanged -= StartWithWindowsChanged;
            _startWithWindows.Checked = Autostart.IsEnabled();
            _startWithWindows.CheckedChanged += StartWithWindowsChanged;
        }
    }

    private static void OpenConfigFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.DataDirectory,
            UseShellExecute = true
        });
    }

    private void HideToTray()
    {
        if (_hidingToTray)
            return;
        _hidingToTray = true;
        try
        {
            _tray.Visible = true;
            if (!PreferTrayOnly)
            {
                ShowInTaskbar = true;
                _allowShow = true;
                Show();
                WindowState = FormWindowState.Minimized;
                return;
            }

            ShowInTaskbar = false;
            _allowShow = false;
            WindowState = FormWindowState.Normal;
            Hide();
        }
        finally
        {
            _hidingToTray = false;
        }
    }

    private void ShowFromTray()
    {
        _allowShow = true;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private async Task StartBackendAsync()
    {
        if (_starting)
            return;
        _starting = true;
        _runCts = new CancellationTokenSource();

        try
        {
            AppendLog("[ui] starting…");
            AppendLog($"[config] {AppPaths.AppSettingsPath}");
            _host = new SpoofHost(_options);
            await _host.StartAsync().ConfigureAwait(true);

            for (var i = 0; i < SpoofOptions.DeviceCount; i++)
                _deviceTabs[i].Bind(_host.Services[i]);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(_options.Http.Url);
            builder.Logging.ClearProviders();
            var web = builder.Build();
            MapApi(web, _host);
            await web.StartAsync(_runCts.Token).ConfigureAwait(true);
            _web = web;

            AppendLog($"[http] listening on {_options.Http.Url}");
            AppendLog("[http] GET-only control API (loopback)");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            await RollbackPartialStartAsync().ConfigureAwait(true);
            AppendLog(ex.Message);
            _httpBanner.Text = "Failed to start — see log.";
            _httpBanner.ForeColor = Brand.Red;
            if (_startInTray)
            {
                _tray.Visible = true;
                _tray.ShowBalloonTip(4000, "s16n HID Spoof", ex.Message, ToolTipIcon.Error);
            }
            else
            {
                MessageBox.Show(this, ex.Message, "s16n HID Spoof", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private async Task RollbackPartialStartAsync()
    {
        try { _runCts?.Cancel(); } catch { /* ignore */ }

        if (_web is not null)
        {
            try { await _web.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true); } catch { /* ignore */ }
            try { await _web.DisposeAsync().ConfigureAwait(true); } catch { /* ignore */ }
            _web = null;
        }

        if (_host is not null)
        {
            try { await _host.DisposeAsync().ConfigureAwait(true); } catch { /* ignore */ }
            _host = null;
        }
    }

    private void RefreshStatus()
    {
        if (_host is null || !_host.IsRunning)
            return;

        _httpBanner.Text = _options.Http.Url;
        _httpBanner.ForeColor = Brand.Muted;
        foreach (var tab in _deviceTabs)
            tab.RefreshStatus(_options.Http.Url);
    }

    private void AppendLog(string line)
    {
        if (IsDisposed)
            return;

        // Route [spoof:id] / [virtual:…/id] to the matching tab; otherwise fan out.
        DeviceTabPage? target = null;
        if (line.StartsWith("[spoof:", StringComparison.Ordinal))
        {
            var end = line.IndexOf(']');
            if (end > 7)
            {
                var id = line[7..end];
                target = _deviceTabs.FirstOrDefault(t =>
                    string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (line.StartsWith("[virtual:s16n-hid-spoof/", StringComparison.Ordinal))
        {
            var end = line.IndexOf(']');
            if (end > 24)
            {
                var rest = line[24..end];
                var slash = rest.IndexOf('/');
                var id = slash >= 0 ? rest[..slash] : rest;
                target = _deviceTabs.FirstOrDefault(t =>
                    string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (target is not null)
        {
            target.AppendLog(line);
            return;
        }

        foreach (var tab in _deviceTabs)
            tab.AppendLog(line);
    }

    private void TryLoadIcon()
    {
        var icon = BrandAssets.LoadAppIcon();
        if (icon is not null)
            Icon = icon;
    }

    private static Image? TryLoadLogo() => BrandAssets.LoadLogo();

    private static void MapApi(WebApplication app, SpoofHost host)
    {
        var options = host.Options;
        var allowNonLocal = options.Http.AllowNonLocal;

        var endpoints = new List<string>
        {
            "GET  /health",
            "GET  /status",
            "GET  /devices/{id}/status",
            "GET  /devices/{id}/slots",
            "GET  /devices/{id}/active",
            "GET  /devices/{id}/active?slot=0",
            "GET  /devices/{id}/active/{slot}",
            "GET  /devices/{id}/ffbeast/reset"
        };

        app.MapGet("/", () => Results.Json(new
        {
            name = "s16n-hid-spoof",
            description = "Up to 3 physical HID controllers → 4 virtual devices each. HTTP selects the active virtual per device.",
            endpoints
        }));

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            running = host.IsRunning,
            devices = host.Services
                .Where(s => s is not null)
                .Select(s => new
                {
                    id = s!.DeviceId,
                    enabled = s.IsEnabled,
                    running = s.IsRunning,
                    ready = s.IsReady
                })
        }));

        app.MapGet("/status", () => Results.Ok(new
        {
            ready = host.Services.Any(s => s is { IsReady: true }),
            devices = host.Services
                .Where(s => s is not null)
                .Select(s => DeviceStatusPayload(s!))
        }));

        app.MapGet("/devices/{id}/status", (string id) =>
        {
            var spoof = host.TryGet(id);
            if (spoof is null)
                return Results.Json(new { ok = false, error = $"unknown device '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            return Results.Ok(DeviceStatusPayload(spoof));
        });

        app.MapGet("/devices/{id}/slots", (string id) =>
        {
            var spoof = host.TryGet(id);
            if (spoof is null)
                return Results.Json(new { ok = false, error = $"unknown device '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            if (!spoof.IsEnabled)
                return Results.Json(new { ok = false, error = "device disabled" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return spoof.IsReady
                ? Results.Ok(spoof.Bank.Slots)
                : Results.Json(new { ok = false, error = "waiting for physical device" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/devices/{id}/active", (HttpRequest req, string id, int? slot) =>
        {
            var spoof = host.TryGet(id);
            if (spoof is null)
                return Results.Json(new { ok = false, error = $"unknown device '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            if (slot is int s)
                return AuthorizeLocal(req, allowNonLocal, () => SelectSlotHttp(spoof, s));
            return ReadActive(spoof);
        });

        app.MapGet("/devices/{id}/active/{slot:int}", (HttpRequest req, string id, int slot) =>
        {
            var spoof = host.TryGet(id);
            if (spoof is null)
                return Results.Json(new { ok = false, error = $"unknown device '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            return AuthorizeLocal(req, allowNonLocal, () => SelectSlotHttp(spoof, slot));
        });

        app.MapGet("/devices/{id}/ffbeast/reset", (HttpRequest req, string id) =>
        {
            var spoof = host.TryGet(id);
            if (spoof is null)
                return Results.Json(new { ok = false, error = $"unknown device '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            if (!FfbeastFlightReset.IsSupported(spoof.Device.Physical))
            {
                return Results.Json(
                    new
                    {
                        ok = false,
                        error = "FFBeast reset is only enabled when Physical is VID 0x045B PID 0x58F9."
                    },
                    statusCode: StatusCodes.Status404NotFound);
            }

            return AuthorizeLocal(req, allowNonLocal, () =>
            {
                var (ok, message) = FfbeastFlightReset.TryReset(spoof.Device.Physical);
                if (!ok)
                    return Results.Json(new { ok = false, error = message }, statusCode: StatusCodes.Status502BadGateway);
                return Results.Ok(new { ok = true, message, device = spoof.DeviceId });
            });
        });

    }

    private static object DeviceStatusPayload(SpoofService spoof)
    {
        if (!spoof.IsEnabled)
        {
            return new
            {
                id = spoof.DeviceId,
                name = spoof.Device.DisplayName,
                enabled = false,
                ready = false
            };
        }

        var reader = spoof.Reader;
        object? slots = spoof.IsReady ? spoof.Bank.Slots : null;
        int? activeSlot = spoof.IsReady ? spoof.Bank.ActiveSlot : null;
        var ffbeast = FfbeastFlightReset.IsSupported(spoof.Device.Physical);
        return new
        {
            id = spoof.DeviceId,
            name = spoof.Device.DisplayName,
            enabled = true,
            ready = spoof.IsReady,
            physical = new
            {
                vid = spoof.Device.Physical.Vid,
                pid = spoof.Device.Physical.Pid,
                devicePathContains = spoof.Device.Physical.DevicePathContains,
                present = !string.IsNullOrEmpty(reader.DevicePath),
                product = reader.ProductString,
                path = reader.DevicePath,
                maxInputReportLength = reader.MaxInputReportLength,
                stripReportId = reader.StripReportId
            },
            activeSlot,
            slots,
            ffbeastReset = new
            {
                enabled = ffbeast,
                vid = $"0x{FfbeastFlightReset.VendorId:X4}",
                pid = $"0x{FfbeastFlightReset.FlightStickProductId:X4}",
                source = "https://github.com/stuart11n/ffbeast-flight-reset"
            }
        };
    }

    private static IResult ReadActive(SpoofService spoof)
    {
        if (!spoof.IsEnabled || !spoof.IsRunning)
        {
            return Results.Json(new { ok = false, error = "device unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!spoof.IsReady)
        {
            return Results.Json(new { ok = false, error = "waiting for physical device" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var current = spoof.Bank.ActiveSlot;
        return Results.Ok(spoof.Bank.Slots[current]);
    }

    private static IResult AuthorizeLocal(
        HttpRequest request,
        bool allowNonLocal,
        Func<IResult> action)
    {
        if (!allowNonLocal && !HttpSecurity.IsLoopbackConnection(request.HttpContext.Connection))
            return Results.Json(new { ok = false, error = "loopback only" }, statusCode: StatusCodes.Status403Forbidden);

        return action();
    }

    private static IResult SelectSlotHttp(SpoofService spoof, int slot)
    {
        if (!spoof.IsEnabled || !spoof.IsReady)
        {
            return Results.Json(new { ok = false, error = "waiting for physical device" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!spoof.Bank.TrySelect(slot, out var error))
            return Results.BadRequest(new { ok = false, error });
        return Results.Ok(new { ok = true, device = spoof.DeviceId, active = spoof.Bank.Slots[slot] });
    }

    private sealed class UiTextWriter : TextWriter
    {
        private readonly Action<string> _append;
        private readonly System.Text.StringBuilder _buf = new();

        public UiTextWriter(Action<string> append) => _append = append;
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value is '\n')
            {
                _append(_buf.ToString());
                _buf.Clear();
                return;
            }

            if (value != '\r')
                _buf.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            foreach (var ch in value)
                Write(ch);
        }

        public override void Flush()
        {
            if (_buf.Length == 0)
                return;
            _append(_buf.ToString());
            _buf.Clear();
        }
    }
}
