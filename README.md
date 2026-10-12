# AirMouse for Windows

A small companion app for Windows PCs that use the AirMouse iPhone app as a Bluetooth mouse. It
lives in the notification area and keeps the iPhone connected.

**What it fixes.** AirMouse's mouse service only exists while the iPhone app is running. If the
app restarts (an update, a force-quit, iOS closing it in the background), Windows sees the service
disappear and removes its mouse. It then never connects to the phone again: the pairing stays,
showing "iPhone, not connected", until it is removed and paired again. Macs and the Shield recover
by themselves.

**How.** It keeps a Bluetooth connection to the paired iPhone open
(`GattSession.MaintainConnection`). That way Windows is still connected when the app comes back,
and it picks up the app's services again. If it misses them, the helper re-reads the iPhone's
services, which is all Windows needs to bring the mouse back. It finds the iPhone by AirMouse's
companion link service. If a pairing has already gone stale, it connects to paired Apple devices
and looks again. It backs off while the app is closed.

## For users

Download `AirMouse.exe` and run it. It asks once, then installs for your account only, without
admin rights:

- the app goes to `%LOCALAPPDATA%\Programs\AirMouse`
- it starts when you sign in, and you can turn that off from its menu
- it appears in Start, and you remove it from Settings > Apps > Installed apps

Its icon is bright while Windows has the mouse, and faded otherwise. Click it to see what's going on,
reconnect, open Bluetooth settings, or read how to pair.

Running it again updates an installed copy. For scripted installs:

```
AirMouse.exe --quiet                 install or update, no questions
AirMouse.exe --uninstall --quiet     remove
```

Needs Windows 10 (1703 or later) or 11. It runs on the .NET Framework that comes with Windows.

## Building

On Windows with the Windows 10/11 SDK installed (Visual Studio installs it):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `dist\AirMouse.exe`, using the C# compiler that ships in Windows. The icons in `assets`
come from the iPhone app icon; to regenerate them, run `python3 windows/assets/make_icons.py` from
the repo root.

The exe isn't code-signed yet, so the first run from a download shows Windows SmartScreen's
"Windows protected your PC" (More info > Run anyway).
