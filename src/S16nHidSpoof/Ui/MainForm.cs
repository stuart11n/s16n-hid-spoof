using System.Drawing.Drawing2D;
using System.Drawing.Text;
using S16nHidSpoof.Config;
using S16nHidSpoof.Hid;

namespace S16nHidSpoof.Ui;

internal sealed class MainForm : Form
{
    private readonly SpoofOptions _options;
    private readonly bool _startInTray;
    private readonly Label _physicalStatus;
    private readonly Label _httpStatus;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _hidHide;
    private readonly Button? _ffbeastReset = null;
    private readonly TextBox _log;
    private readonly SlotRow[] _slots;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly HidHidePhysical _hidHidePhysical;
    private Image? _logo;
    private SpoofService? _spoof;
    private WebApplication? _web;
    private CancellationTokenSource? _runCts;
    private bool _starting;
    private bool _exitRequested;
    private bool _allowShow;
    private bool _uiStarted;
    private bool _hidingToTray;

    /// <summary>
    /// Start-with-Windows / --tray: notification area only (no taskbar button).
    /// Otherwise minimize keeps a taskbar button (elevated tray icons can be flaky).
    /// </summary>
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
        // --tray: never become visible until the user picks Show from the tray menu.
        _allowShow = !_startInTray;
        TryLoadIcon();

        _logo = TryLoadLogo();

        _physicalStatus = MakeLabel(16, Brand.HeaderH + 16, Brand.ClientW - 32, 36);
        _httpStatus = MakeLabel(16, Brand.HeaderH + 52, Brand.ClientW - 32, 18);
        _httpStatus.ForeColor = Brand.Muted;

        if (FfbeastFlightReset.IsSupported(options.Physical))
        {
            const int btnW = 110;
            _httpStatus.Width = Brand.ClientW - 32 - btnW - 8;
            _ffbeastReset = new Button
            {
                Left = Brand.ClientW - 16 - btnW,
                Top = Brand.HeaderH + 48,
                Width = btnW,
                Height = 26,
                Text = "FFBeast reset",
                FlatStyle = FlatStyle.System,
                Enabled = false
            };
            _ffbeastReset.Click += FfbeastResetClicked;
            Controls.Add(_ffbeastReset);
        }

        _hidHidePhysical = new HidHidePhysical(options.Physical);

        _startWithWindows = new CheckBox
        {
            Left = 16,
            Top = Brand.HeaderH + 74,
            Width = Brand.ClientW - 32,
            Height = 22,
            Text = "Start with Windows (elevated tray task)",
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Checked = Autostart.IsEnabled()
        };
        _startWithWindows.CheckedChanged += StartWithWindowsChanged;
        Controls.Add(_startWithWindows);

        // Never touch HidHide on the UI thread during construction — its control
        // IOCTLs can block indefinitely when the driver/client is busy.
        _hidHide = new CheckBox
        {
            Left = 16,
            Top = Brand.HeaderH + 96,
            Width = Brand.ClientW - 32,
            Height = 22,
            Text = "Hide physical device with HidHide (checking…)",
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Enabled = false
        };
        _hidHide.CheckedChanged += HidHideChanged;
        Controls.Add(_hidHide);

        var cardTop = Brand.HeaderH + 126;
        Controls.Add(MakeSectionTitle(16, cardTop, "Virtual slots"));

        _slots = new SlotRow[4];
        for (var i = 0; i < 4; i++)
        {
            var y = cardTop + 28 + i * 52;
            _slots[i] = new SlotRow(this, i, 16, y, Brand.ClientW - 32);
            var opt = options.VirtualDevices[i];
            _slots[i].SetConfig(opt);
        }

        Controls.Add(MakeSectionTitle(16, cardTop + 28 + 4 * 52 + 8, "Log"));
        _log = new TextBox
        {
            Left = 16,
            Top = cardTop + 28 + 4 * 52 + 28,
            Width = Brand.ClientW - 32,
            Height = Brand.ClientH - (cardTop + 28 + 4 * 52 + 28) - 16 - Brand.StatusBarH,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Brand.Text,
            Font = new Font("Consolas", 8.25f)
        };
        Controls.Add(_log);

        var status = new StatusStrip
        {
            BackColor = Brand.PanelBg,
            SizingGrip = false,
            Font = new Font("Segoe UI", 8.25f),
            Padding = new Padding(2, 0, 8, 0)
        };
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

        // Always keep a tray icon while running (close / minimize hide here).
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
        // Application.Run always tries to show the main form; block that for --tray
        // so we never get a minimized taskbar button.
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

        _ = ProbeHidHideAsync();
        _ = StartBackendAsync();
        _refresh.Start();
    }

    private async Task ProbeHidHideAsync()
    {
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

        try { _hidHidePhysical.Dispose(); }
        catch { /* ignore */ }

        try
        {
            _web?.StopAsync(TimeSpan.FromSeconds(2))
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }

        try
        {
            _spoof?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* ignore */ }

        try
        {
            _web?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch { /* ignore */ }

        base.OnFormClosing(e);

        // Guarantee the process leaves after tray Exit even if a HID/HidHide worker is stuck.
        // Release the single-instance mutex first — Environment.Exit skips Program's finally.
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
            return;
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
            MessageBox.Show(this, ex.Message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            var (ok, message) = FfbeastFlightReset.TryReset();
            AppendLog(ok ? $"[ffbeast] {message}" : $"[ffbeast] reset failed: {message}");
            if (!ok)
            {
                MessageBox.Show(this, message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ffbeast] {ex.Message}");
            MessageBox.Show(this, ex.Message, "s16n HID Spoof", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && _ffbeastReset is not null)
                _ffbeastReset.Enabled = _spoof?.Reader.DevicePath is { Length: > 0 };
        }
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
                // Elevated → unelevated Explorer often drops tray icons; keep a taskbar button.
                ShowInTaskbar = true;
                _allowShow = true;
                Show();
                WindowState = FormWindowState.Minimized;
                return;
            }

            // Start with Windows: notification area only — no taskbar button.
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
            _spoof = new SpoofService(_options);
            await _spoof.StartAsync().ConfigureAwait(true);

            // Do not forward app args (--tray / --config) into the ASP.NET host.
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(_options.Http.Url);
            builder.Logging.ClearProviders();
            var web = builder.Build();
            MapApi(web, _spoof, _options);
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
            _physicalStatus.Text = "Failed to start — see log.";
            _physicalStatus.ForeColor = Brand.Red;
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

        if (_spoof is not null)
        {
            try { await _spoof.DisposeAsync().ConfigureAwait(true); } catch { /* ignore */ }
            _spoof = null;
        }
    }

    private void RefreshStatus()
    {
        if (_spoof is null || !_spoof.IsRunning)
            return;

        try
        {
            var reader = _spoof.Reader;
            var waiting = string.IsNullOrEmpty(reader.DevicePath);
            var path = waiting ? "waiting for device (hotplug)…" : reader.DevicePath!;
            var product = waiting ? "—" : (reader.ProductString ?? "—");
            _physicalStatus.Text =
                $"Source  {_options.Physical.Vid}:{_options.Physical.Pid}  ·  {product}\n{Elide(path, 72)}";
            _physicalStatus.ForeColor = waiting ? Brand.Muted : Brand.Text;

            if (_ffbeastReset is not null)
                _ffbeastReset.Enabled = !waiting;

            if (_spoof.IsReady)
            {
                _httpStatus.Text = $"{_options.Http.Url}  ·  active {_spoof.Bank.ActiveSlot}" +
                    (waiting ? "  ·  cached descriptor" : "");
                var slots = _spoof.Bank.Slots;
                for (var i = 0; i < 4; i++)
                    _slots[i].Update(slots[i]);
            }
            else
            {
                _httpStatus.Text = _spoof.HasCachedDescriptor
                    ? $"{_options.Http.Url}  ·  starting…"
                    : $"{_options.Http.Url}  ·  plug in physical once to learn descriptor";
            }
        }
        catch (Exception ex)
        {
            _physicalStatus.Text = ex.Message;
            _physicalStatus.ForeColor = Brand.Red;
        }
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

        RefreshStatus();
    }

    private void AppendLog(string line)
    {
        if (IsDisposed)
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

    private Label MakeLabel(int x, int y, int w, int h)
    {
        var lbl = new Label
        {
            Left = x,
            Top = y,
            Width = w,
            Height = h,
            ForeColor = Brand.Text,
            BackColor = Brand.PanelBg,
            Text = "Starting…"
        };
        Controls.Add(lbl);
        return lbl;
    }

    private Label MakeSectionTitle(int x, int y, string text)
    {
        var lbl = new Label
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
        Controls.Add(lbl);
        return lbl;
    }

    private void TryLoadIcon()
    {
        var icon = BrandAssets.LoadAppIcon();
        if (icon is not null)
            Icon = icon;
    }

    private static Image? TryLoadLogo() => BrandAssets.LoadLogo();

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

    private static void MapApi(WebApplication app, SpoofService spoof, SpoofOptions options)
    {
        var allowNonLocal = options.Http.AllowNonLocal;
        var ffbeastReset = FfbeastFlightReset.IsSupported(options.Physical);

        var endpoints = new List<string>
        {
            "GET  /health",
            "GET  /status",
            "GET  /slots",
            "GET  /active",
            "GET  /active?slot=0",
            "GET  /active/{slot}"
        };
        if (ffbeastReset)
            endpoints.Add("GET  /ffbeast/reset");

        app.MapGet("/", () => Results.Json(new
        {
            name = "s16n-hid-spoof",
            description = "1 physical HID → 4 virtual HIDMaestro devices (raw 1:1). HTTP selects the active virtual.",
            endpoints,
            ffbeastReset
        }));

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            running = spoof.IsRunning,
            ready = spoof.IsReady
        }));

        app.MapGet("/status", () =>
        {
            var reader = spoof.Reader;
            object? slots = spoof.IsReady ? spoof.Bank.Slots : null;
            int? activeSlot = spoof.IsReady ? spoof.Bank.ActiveSlot : null;
            return Results.Ok(new
            {
                ready = spoof.IsReady,
                physical = new
                {
                    vid = options.Physical.Vid,
                    pid = options.Physical.Pid,
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
                    enabled = ffbeastReset,
                    vid = $"0x{FfbeastFlightReset.VendorId:X4}",
                    pid = $"0x{FfbeastFlightReset.FlightStickProductId:X4}",
                    source = "https://github.com/stuart11n/ffbeast-flight-reset"
                }
            });
        });

        app.MapGet("/slots", () =>
            spoof.IsReady
                ? Results.Ok(spoof.Bank.Slots)
                : Results.Json(new { ok = false, error = "waiting for physical device" },
                    statusCode: StatusCodes.Status503ServiceUnavailable));

        // GET-only control plane. /active with ?slot= or /active/{slot} selects; bare /active reads.
        app.MapGet("/active", (HttpRequest req, int? slot) =>
        {
            if (slot is int s)
                return AuthorizeLocal(req, allowNonLocal, () => SelectSlotHttp(spoof, s));

            if (!spoof.IsReady)
            {
                return Results.Json(new { ok = false, error = "waiting for physical device" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var current = spoof.Bank.ActiveSlot;
            return Results.Ok(spoof.Bank.Slots[current]);
        });
        app.MapGet("/active/{slot:int}", (HttpRequest req, int slot) =>
            AuthorizeLocal(req, allowNonLocal, () => SelectSlotHttp(spoof, slot)));

        // FFBeast flight reset — only when Physical is VID 045B / PID 58F9.
        app.MapGet("/ffbeast/reset", (HttpRequest req) =>
        {
            if (!ffbeastReset)
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
                var (ok, message) = FfbeastFlightReset.TryReset();
                if (!ok)
                    return Results.Json(new { ok = false, error = message }, statusCode: StatusCodes.Status502BadGateway);
                return Results.Ok(new { ok = true, message });
            });
        });
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
        if (!spoof.IsReady)
        {
            return Results.Json(new { ok = false, error = "waiting for physical device" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!spoof.Bank.TrySelect(slot, out var error))
            return Results.BadRequest(new { ok = false, error });
        return Results.Ok(new { ok = true, active = spoof.Bank.Slots[slot] });
    }

    private sealed class SlotRow
    {
        private readonly MainForm _form;
        private readonly int _index;
        private readonly Panel _panel;
        private readonly CheckBox _enabled;
        private readonly Label _title;
        private readonly Label _meta;
        private readonly Button _activate;
        private readonly Panel _dot;

        public SlotRow(MainForm form, int index, int x, int y, int width)
        {
            _form = form;
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
            _activate.Click += (_, _) => _form.SelectSlot(_index);

            _panel.Controls.Add(_dot);
            _panel.Controls.Add(_enabled);
            _panel.Controls.Add(_title);
            _panel.Controls.Add(_meta);
            _panel.Controls.Add(_activate);
            form.Controls.Add(_panel);
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
