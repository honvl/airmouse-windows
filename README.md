# AirMouse for Windows

A small companion app for Windows PCs that use the AirMouse iPhone app as a Bluetooth mouse. It
pairs the iPhone, then lives in the notification area and keeps it connected.

**[Download the latest release](https://github.com/honvl/airmouse-windows/releases/latest)**

## Why

AirMouse turns an iPhone into a Bluetooth mouse and keyboard. Its mouse only exists while the
iPhone app is running. When the app restarts (an update, swiping it away, iOS closing it in the
background), Windows sees the mouse disappear. It then never connects to the phone again: the
pairing stays, showing "iPhone, not connected", until you remove it and pair again. Macs recover on
their own; Windows doesn't.

AirMouse for Windows keeps a Bluetooth connection to the paired iPhone open
(`GattSession.MaintainConnection`). That way Windows is still connected when the app comes back,
and it picks up the mouse again. If it misses it, the companion re-reads the iPhone's services,
which is all Windows needs. It finds the iPhone by AirMouse's companion link service. If a pairing
has already gone stale, it connects to paired Apple devices and looks again. It backs off while the
app is closed.

It also pairs the iPhone itself. Bluetooth settings' Add device list leaves AirMouse out unless
"Bluetooth devices discovery" is set to Advanced. The pairing window instead listens for devices
advertising a mouse and keeps the one that publishes AirMouse's link service. It then pairs with a
code you check on both screens (`DeviceInformationCustomPairing`, numeric comparison), the same way
Settings does.

## Install

Download `AirMouse.exe` and run it. It asks once, then installs for your account only, without
admin rights:

- the app goes to `%LOCALAPPDATA%\Programs\AirMouse`
- it starts when you sign in, and you can turn that off from its menu
- it appears in Start, and you remove it from Settings > Apps > Installed apps

If the PC isn't paired with AirMouse yet, the pairing window opens. On the iPhone, open AirMouse,
tap the connection status at the top, then **Pair a new device**. When both screens show the same
code, tap Pair on each. To pair again later, choose **Pair with iPhone…** from the app's menu. You can
also pair in Settings > Bluetooth & devices > Add device > Bluetooth, with "Bluetooth devices
discovery" set to Advanced.

Its icon is bright while Windows has the mouse, and faded otherwise. Click it to see what's going on,
reconnect, pair, or open Bluetooth settings. Running a newer `AirMouse.exe` updates it. For
scripted installs:

```
AirMouse.exe --quiet                 install or update, no questions
AirMouse.exe --uninstall --quiet     remove
```

Needs Windows 10 (1703 or later) or Windows 11. It runs on the .NET Framework that comes with
Windows. The exe isn't code-signed, so the first run shows SmartScreen's "Windows protected your
PC": choose More info, then Run anyway. Windows 11 with Smart App Control on blocks it outright; the
Microsoft Store version doesn't have either problem.

## Microsoft Store

The same exe also ships as an MSIX package for the Microsoft Store, which signs it, so it installs
without SmartScreen or Smart App Control prompts and updates by itself. Inside the package
(`src/Packaged.cs`):

- Windows installs and removes it, so the self-installer and `--uninstall` are skipped
- the package's startup task starts it at sign-in instead of the Run key; the menu item turns the
  task on and off, and so do Settings > Apps > Startup and Task Manager
- the log and saved iPhone live in the package's `LocalState` folder
- the first run opens the pairing window if nothing is paired yet
- it stops a copy installed from the downloaded `AirMouse.exe`, which would otherwise hold the
  running-copy lock, and asks you to remove that copy in Installed apps

`msix/AppxManifest.xml` is the package manifest. Its Identity `Name` and `Publisher`, its
`PublisherDisplayName` and its `DisplayName` must match the reserved app in Partner Center (Product
management > Product identity). `msix/listing.md` has the Store listing text. To try the package
without the Store, turn on Developer Mode and register the build folder:

```powershell
Add-AppxPackage -Register dist\msix\AppxManifest.xml
```

## Build

On Windows with the Windows 10/11 SDK installed (Visual Studio installs it):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `dist\AirMouse.exe`, using the C# compiler that ships in Windows, and the Store package
`dist\AirMouse.msix` (unpacked in `dist\msix`), using the SDK's makepri and makeappx. GitHub Actions
builds every push and keeps both as artifacts, and publishes the exe as a release for each `v*` tag;
the tag must match `Version` in `src/Program.cs`. The icons come from the iPhone app's icon via
`assets/make_icons.py`, and the package's tile images from those icons via
`assets/make_msix_assets.py`.

## License

MIT
