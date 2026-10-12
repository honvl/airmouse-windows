# Microsoft Store listing

Text for Partner Center. The name must be the one reserved there and the same as `DisplayName` in
`AppxManifest.xml`.

## Properties

- **Category:** Utilities & tools
- **Privacy policy URL:** https://airmouse-alpha.vercel.app/windows#privacy
- **Website:** https://airmouse-alpha.vercel.app/windows
- **Support contact:** https://github.com/honvl/airmouse-windows/issues
- **Pricing:** free
- **Age rating:** no user-generated content, no purchases, no data collection

## Description

AirMouse for Windows is the companion to the AirMouse app for iPhone, which turns your iPhone into
a Bluetooth mouse and keyboard for your PC.

It pairs your iPhone with this PC: open AirMouse on the iPhone, tap Pair a new device, and confirm
the code on both screens. You don't need to find AirMouse in Bluetooth settings.

It also keeps your iPhone connected. Windows drops the mouse when the AirMouse app restarts (after
an update, or after you swipe it away) and doesn't connect to the iPhone again. AirMouse for Windows
keeps the connection open from the notification area, so the mouse comes back by itself. Its icon is
bright while Windows has the mouse.

It needs an iPhone with the AirMouse app, and a PC with Bluetooth.

## Product features

- Pairs your iPhone as a mouse and keyboard, with a code to confirm on both screens
- Brings the mouse back by itself when the AirMouse app restarts
- Shows in the notification area whether Windows has the mouse
- Starts when you sign in; turn that off from its menu or in Settings
- Doesn't collect or send any data

## Search terms

AirMouse, iPhone mouse, Bluetooth mouse, remote, trackpad, keyboard, iPhone

## Screenshots

`screenshots/`, 1600 x 900, in this order. They're the real windows on a 150% display, rendered with
PrintWindow (so nothing else on the desktop shows) and set on a background with a caption:

1. `1-pair.png`: the pairing window looking for the iPhone
2. `2-code.png`: the code to check on both screens
3. `3-paired.png`: paired, the iPhone working as a mouse and keyboard
4. `4-menu.png`: the notification-area menu, connected

## Notes for certification

AirMouse for Windows is a companion to the AirMouse iPhone app and does its work over Bluetooth Low
Energy, so most of it can only be tried with an iPhone running AirMouse. Without one, launching it
shows its icon in the notification area and opens the pairing window, which keeps looking for the
iPhone; Cancel closes it. The menu on the icon has Pair with iPhone, Bluetooth settings, Start when
I sign in, Open log and Quit.

**runFullTrust:** it's a .NET Framework (Windows Forms) desktop app packaged with MSIX. It runs as a
notification-area app and uses the Windows Bluetooth LE APIs to pair with and stay connected to the
iPhone, and it starts short-lived copies of itself for Bluetooth work.
