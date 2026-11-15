# s16n-hid-spoof

Windows app that takes **one physical USB HID controller** and presents it as **up to four virtual devices** with different identities. Input is passed through 1:1. You pick which virtual device is live from the UI or over HTTP.

Made for [FFBeast](https://ffbeast.com/) flight controls that share the same VID:PID — each stick/yoke can look like a different device to the sim.

## Install

Download: https://github.com/stuart11n/s16n-hid-spoof/releases/download/latest/s16n-hid-spoof-setup.exe

The installer is small (framework-dependent). If needed, it downloads and installs the **.NET 10 Desktop** and **ASP.NET Core** runtimes during setup.

Run the app **as Administrator**. Close or minimize sends it to the taskbar (and tray when Windows shows it — elevated apps often have no tray icon). Optional **Start with Windows** starts it minimized at logon. Use the tray menu **Exit**, or close from the taskbar, to quit.

Tip: hide the real hardware from the game (e.g. HidHide) so only the active virtual device is seen.

## Config

Edit:

`%LOCALAPPDATA%\s16n-hid-spoof\appsettings.json`

Created on first launch. Set the physical VID/PID, enable the virtual slots you need, and choose the starting active slot.

| Setting | What it does |
|---------|----------------|
| `Physical.Vid` / `Physical.Pid` | Your real controller |
| `VirtualDevices[0..3]` | Up to four virtual devices (each with its own VID/PID and name) |
| `VirtualDevices[n].Enabled` | Turn a slot on or off |
| `ActiveSlot` | Which slot starts active (`0..3`) |
| `Http.Url` | Control URL (default `http://127.0.0.1:8780`) |
| `ForwardOutputToPhysical` | Send force-feedback / rumble back to the real device |

## HTTP (GET only)

Default: `http://127.0.0.1:8780` (this PC only).

| URL | What it does |
|-----|----------------|
| `/status` | What’s connected and which slot is active |
| `/active` | Current slot |
| `/active?slot=2` or `/active/2` | Switch to that slot |
| `/ffbeast/reset` | FFBeast flight reset (only when physical is `0x045B:0x58F9`) |

```bash
curl -s http://127.0.0.1:8780/status
curl -s "http://127.0.0.1:8780/active?slot=2"
curl -s "http://127.0.0.1:8780/ffbeast/reset"
```

## Building from source

Windows 10/11 x64, .NET 10 SDK, run as Administrator.

```powershell
.\scripts\fetch-hidmaestro.ps1
dotnet run --project src\S16nHidSpoof -c Release

.\scripts\package.ps1    # installer → dist\
.\scripts\release.ps1    # publish GitHub Release (latest)
```
