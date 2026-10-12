using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

// Keeps Windows connected to the iPhone running AirMouse, so the mouse comes back by itself.
//
// AirMouse's mouse service only exists while the app runs. When the app restarts (an update, a
// force-quit, iOS closing it in the background), Windows sees the service disappear, removes its
// mouse, and never connects to the phone again. Holding a connection open keeps Windows there
// when the app comes back, so it hears about the new services; if it misses them, this re-reads
// them, which is all Windows needs to bring the mouse back.
//
// Windows-only, C# 5, built with the compiler that ships in Windows (see install.ps1).
static class Program
{
    /// AirMouse's companion link service: tells the paired iPhone apart from other devices
    static readonly Guid LinkService = new Guid("fb749375-7d73-4541-a1c1-5c4858d09521");
    static readonly Guid HidService = new Guid("00001812-0000-1000-8000-00805f9b34fb");

    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirMouse");
    static readonly string DeviceFile = Path.Combine(Folder, "device.txt");
    static readonly string LogFile = Path.Combine(Folder, "reconnect.log");

    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(20);
    static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    static BluetoothLEDevice device;
    static GattSession session;
    /// re-reading the services while the app is closed finds nothing, so back off between tries
    static TimeSpan retry = FirstRetry;
    static DateTime nextRefresh = DateTime.MinValue;
    static bool? lastSawMouse;
    /// set from Bluetooth callbacks, which must not wait on the loop while it waits on Bluetooth
    static volatile bool changed;

    static void Main()
    {
        bool first;
        using (new Mutex(true, @"Local\AirMouseReconnect", out first))
        {
            if (!first) return;
            Directory.CreateDirectory(Folder);
            Log("started");
            while (true)
            {
                try { Tick(); }
                catch (Exception e) { Log("error: " + e.Message); Release(); }
                Thread.Sleep(TickInterval);
            }
        }
    }

    static void Tick()
    {
        if (device != null && !device.DeviceInformation.Pairing.IsPaired)
        {
            // unpaired, maybe to pair again; look for the iPhone afresh
            Log("pairing removed, looking for the iPhone again");
            Release();
            File.Delete(DeviceFile);
        }
        if (device == null)
        {
            ulong address = SavedOrFound();
            if (address == 0) return;
            Attach(address);
        }
        if (changed)
        {
            changed = false;
            retry = FirstRetry;
            nextRefresh = DateTime.Now + TimeSpan.FromSeconds(3);
        }
        // MaintainConnection reconnects on its own whenever the phone is in range
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return;
        if (Has(HidService, BluetoothCacheMode.Cached))
        {
            Report(true);
            return;
        }
        if (DateTime.Now < nextRefresh) return;
        bool back = Has(HidService, BluetoothCacheMode.Uncached);
        Report(back);
        retry = back ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
        nextRefresh = DateTime.Now + retry;
    }

    static void Report(bool sawMouse)
    {
        if (lastSawMouse == sawMouse) return;
        lastSawMouse = sawMouse;
        Log(sawMouse ? "AirMouse is up; Windows has its mouse" : "connected, AirMouse not running yet");
    }

    /// the address saved last time, or a paired device that publishes AirMouse's link service
    static ulong SavedOrFound()
    {
        ulong saved;
        if (File.Exists(DeviceFile) &&
            ulong.TryParse(File.ReadAllText(DeviceFile).Trim(), NumberStyles.HexNumber, null, out saved))
        {
            return saved;
        }
        var paired = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask().Result;
        foreach (var info in paired)
        {
            using (var candidate = BluetoothLEDevice.FromIdAsync(info.Id).AsTask().Result)
            {
                if (candidate == null || !Has(candidate, LinkService, BluetoothCacheMode.Cached)) continue;
                File.WriteAllText(DeviceFile, candidate.BluetoothAddress.ToString("x12"));
                Log("found " + candidate.Name + " at " + candidate.BluetoothAddress.ToString("x12"));
                return candidate.BluetoothAddress;
            }
        }
        return 0;
    }

    static void Attach(ulong address)
    {
        device = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().Result;
        if (device == null) throw new InvalidOperationException("no device at " + address.ToString("x12"));
        device.ConnectionStatusChanged += (d, _) =>
        {
            Log("status " + d.ConnectionStatus);
            changed = true;
        };
        // iOS announces it when the app adds its services back; re-read them soon after
        device.GattServicesChanged += (d, _) => changed = true;
        session = GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().Result;
        session.MaintainConnection = true;
        Log("holding a connection to " + device.Name + " (" + address.ToString("x12") + ")");
    }

    static bool Has(Guid service, BluetoothCacheMode mode)
    {
        return Has(device, service, mode);
    }

    static bool Has(BluetoothLEDevice target, Guid service, BluetoothCacheMode mode)
    {
        var result = target.GetGattServicesAsync(mode).AsTask().Result;
        bool found = result.Status == GattCommunicationStatus.Success && result.Services.Any(s => s.Uuid == service);
        foreach (var s in result.Services) s.Dispose();
        return found;
    }

    static void Release()
    {
        if (session != null) session.Dispose();
        if (device != null) device.Dispose();
        session = null;
        device = null;
        lastSawMouse = null;
    }

    static void Log(string message)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 256 * 1024)
            {
                File.Copy(LogFile, LogFile + ".old", true);
                File.Delete(LogFile);
            }
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
