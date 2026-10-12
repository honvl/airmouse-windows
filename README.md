# AirMouse Reconnect (Windows)

A small background helper for Windows PCs that use the iPhone app as a Bluetooth mouse.

**The problem it fixes.** AirMouse's mouse service only exists while the iPhone app is running. If the
app restarts (an update, a force-quit, iOS closing it in the background), Windows sees the service
disappear and removes its mouse. It then never connects to the phone again: the pairing stays,
showing "iPhone, not connected", until it is removed and paired again. Macs and the Shield recover
by themselves.

**What it does.** It keeps a Bluetooth connection to the paired iPhone open
(`GattSession.MaintainConnection`). That way Windows is still connected when the app comes back,
and it picks up the app's services again. If it misses them, the helper re-reads the iPhone's
services, which is all Windows needs to bring the mouse back. It finds the iPhone by AirMouse's
companion link service, and it backs off while the app is closed.

## Install

Pair the PC with AirMouse first, then run this in PowerShell from this folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

The script builds `AirMouseReconnect.exe` with the C# compiler that ships in Windows. It needs the
Windows 10/11 SDK, which Visual Studio installs. It then installs to `%LOCALAPPDATA%\AirMouse`,
starts the helper, and adds it to your Windows sign-in startup items. There is no window: what it
does goes to `%LOCALAPPDATA%\AirMouse\reconnect.log`.

To remove it: `install.ps1 -Uninstall`.

If you pair the PC again, the helper notices that the old pairing is gone and finds the new one.
