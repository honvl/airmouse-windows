using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AirMouse
{
    enum LinkState
    {
        /// no paired device publishes AirMouse yet
        NotPaired,
        /// paired, but the iPhone isn't connected (out of range, Bluetooth off)
        Away,
        /// connected to the iPhone, but the AirMouse app isn't running
        Waiting,
        /// Windows has AirMouse's mouse
        Ready
    }

    /// Keeps Windows connected to the iPhone running AirMouse, so the mouse comes back by itself.
    ///
    /// AirMouse's mouse service only exists while the app runs. When the app restarts (an update, a
    /// force-quit, iOS closing it in the background), Windows sees the service disappear, removes its
    /// mouse, and never connects to the phone again. Holding a connection open keeps Windows there
    /// when the app comes back, so it hears about the new services; if it misses them, re-reading the
    /// services is all Windows needs to bring the mouse back.
    sealed class Reconnector
    {
        /// AirMouse's companion link service: tells the paired iPhone apart from other devices
        static readonly Guid LinkService = new Guid("fb749375-7d73-4541-a1c1-5c4858d09521");
        static readonly Guid HidService = new Guid("00001812-0000-1000-8000-00805f9b34fb");
        /// Apple's Continuity service, on every iPhone: worth a closer look when no pairing shows AirMouse
        static readonly Guid AppleContinuity = new Guid("d0611e78-bbb4-4591-a5f8-487910ae4366");

        public static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirMouse");
        public static readonly string LogFile = Path.Combine(DataFolder, "airmouse.log");
        static readonly string DeviceFile = Path.Combine(DataFolder, "device.txt");

        static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(20);
        static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
        static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(1);
        static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(10);

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        BluetoothLEDevice device;
        GattSession session;
        /// re-reading the services while the app is closed finds nothing, so back off between tries
        TimeSpan retry = FirstRetry;
        DateTime nextRefresh = DateTime.MinValue;
        DateTime nextSearch = DateTime.MinValue;
        LinkState? reported;
        // set from Bluetooth callbacks and the tray, which must not wait on the loop
        volatile bool changed;
        volatile bool refreshRequested;
        volatile bool stopping;
        int drops;

        /// raised on the Bluetooth thread
        public event Action<LinkState> StateChanged;

        public void Start()
        {
            Directory.CreateDirectory(DataFolder);
            var thread = new Thread(Run) { IsBackground = true, Name = "AirMouse Bluetooth" };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
        }

        /// "Reconnect now": re-read the services, or start the connection over if it's down
        public void RefreshNow()
        {
            refreshRequested = true;
            wake.Set();
        }

        void Run()
        {
            Log("started " + Program.Version);
            while (!stopping)
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    Log("error: " + Describe(e));
                    Release();
                }
                wake.WaitOne(TickInterval);
            }
            Release();
        }

        void Tick()
        {
            if (device != null && !device.DeviceInformation.Pairing.IsPaired)
            {
                Log("pairing removed; looking for the iPhone again");
                Release();
                File.Delete(DeviceFile);
                nextSearch = DateTime.MinValue;
            }
            if (device == null)
            {
                ulong address = Saved();
                if (address == 0 && DateTime.Now >= nextSearch)
                {
                    nextSearch = DateTime.Now + SearchInterval;
                    address = Find();
                }
                if (address == 0)
                {
                    Report(LinkState.NotPaired);
                    return;
                }
                Attach(address);
            }

            int dropped = Interlocked.Exchange(ref drops, 0);
            if (dropped > 0) Log("connection dropped " + dropped + "x");
            bool forced = refreshRequested;
            refreshRequested = false;
            if (changed)
            {
                changed = false;
                retry = FirstRetry;
                nextRefresh = DateTime.Now + TimeSpan.FromSeconds(3);
            }

            // MaintainConnection reconnects on its own whenever the phone is in range
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                if (forced)
                {
                    Log("reconnect requested; starting the connection over");
                    Release();
                }
                Report(LinkState.Away);
                return;
            }
            if (!forced && Has(device, HidService, BluetoothCacheMode.Cached))
            {
                Report(LinkState.Ready);
                return;
            }
            if (!forced && DateTime.Now < nextRefresh)
            {
                Report(LinkState.Waiting);
                return;
            }
            bool back = Has(device, HidService, BluetoothCacheMode.Uncached);
            retry = back ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            nextRefresh = DateTime.Now + retry;
            Report(back ? LinkState.Ready : LinkState.Waiting);
        }

        void Report(LinkState state)
        {
            if (reported == state) return;
            reported = state;
            switch (state)
            {
                case LinkState.Ready: Log("AirMouse is up; Windows has its mouse"); break;
                case LinkState.Waiting: Log("connected; AirMouse isn't running"); break;
                case LinkState.Away: Log("not connected; waiting for the iPhone"); break;
                case LinkState.NotPaired: Log("no paired iPhone with AirMouse yet"); break;
            }
            var handler = StateChanged;
            if (handler != null) handler(state);
        }

        static ulong Saved()
        {
            ulong saved;
            if (File.Exists(DeviceFile) &&
                ulong.TryParse(File.ReadAllText(DeviceFile).Trim(), NumberStyles.HexNumber, null, out saved))
            {
                return saved;
            }
            return 0;
        }

        /// a paired device that publishes AirMouse's link service. Failing that, paired Apple devices
        /// are connected and checked afresh: a pairing goes stale exactly when Windows last looked
        /// while the app wasn't running
        static ulong Find()
        {
            var paired = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask().Result;
            var apple = new List<string>();
            foreach (var info in paired)
            {
                using (var candidate = BluetoothLEDevice.FromIdAsync(info.Id).AsTask().Result)
                {
                    if (candidate == null) continue;
                    if (Has(candidate, LinkService, BluetoothCacheMode.Cached)) return Remember(candidate);
                    if (Has(candidate, AppleContinuity, BluetoothCacheMode.Cached)) apple.Add(info.Id);
                }
            }
            foreach (var id in apple)
            {
                using (var candidate = BluetoothLEDevice.FromIdAsync(id).AsTask().Result)
                {
                    if (candidate == null) continue;
                    using (var probe = GattSession.FromDeviceIdAsync(candidate.BluetoothDeviceId).AsTask().Result)
                    {
                        probe.MaintainConnection = true;
                        var deadline = DateTime.Now + ConnectWait;
                        while (candidate.ConnectionStatus != BluetoothConnectionStatus.Connected && DateTime.Now < deadline)
                        {
                            Thread.Sleep(500);
                        }
                        if (Has(candidate, LinkService, BluetoothCacheMode.Uncached)) return Remember(candidate);
                    }
                }
            }
            return 0;
        }

        static ulong Remember(BluetoothLEDevice found)
        {
            string address = found.BluetoothAddress.ToString("x12");
            File.WriteAllText(DeviceFile, address);
            Log("found " + found.Name + " (" + address + ")");
            return found.BluetoothAddress;
        }

        void Attach(ulong address)
        {
            device = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().Result;
            if (device == null) throw new InvalidOperationException("no device at " + address.ToString("x12"));
            device.ConnectionStatusChanged += (d, o) =>
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Interlocked.Increment(ref drops);
                changed = true;
                wake.Set();
            };
            // iOS announces it when the app adds its services back; re-read them soon after
            device.GattServicesChanged += (d, o) =>
            {
                changed = true;
                wake.Set();
            };
            session = GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().Result;
            session.MaintainConnection = true;
            Log("holding a connection to " + device.Name + " (" + address.ToString("x12") + ")");
        }

        static bool Has(BluetoothLEDevice target, Guid service, BluetoothCacheMode mode)
        {
            var result = target.GetGattServicesAsync(mode).AsTask().Result;
            bool found = result.Status == GattCommunicationStatus.Success && result.Services.Any(s => s.Uuid == service);
            foreach (var s in result.Services) s.Dispose();
            return found;
        }

        void Release()
        {
            if (session != null) session.Dispose();
            if (device != null) device.Dispose();
            session = null;
            device = null;
            reported = null;
        }

        static string Describe(Exception e)
        {
            var aggregate = e as AggregateException;
            return aggregate != null && aggregate.InnerException != null ? aggregate.InnerException.Message : e.Message;
        }

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
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
}
