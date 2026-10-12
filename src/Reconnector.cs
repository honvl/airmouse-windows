using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;

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
        Ready,
        /// paired here, but the iPhone kept an older pairing for this PC: the link keeps dropping
        /// and the iPhone never answers. Only forgetting the PC on the iPhone and pairing again
        /// fixes it
        OutOfSync
    }

    /// Keeps Windows connected to the iPhone running AirMouse, so the mouse comes back by itself.
    ///
    /// AirMouse's mouse service only exists while the app runs. When the app restarts (an update, a
    /// force-quit, iOS closing it in the background), Windows sees the service disappear, removes its
    /// mouse, and never connects to the phone again. Holding a connection open keeps Windows there
    /// when the app comes back, so it hears about the new services; if it misses them, re-reading the
    /// services is all Windows needs to bring the mouse back.
    ///
    /// This process only holds the connection and reads Windows' device list. Anything that opens
    /// GATT service objects runs in a short-lived copy of the exe (see Probe): in a long-running
    /// process those objects piled up threads until Windows' Bluetooth stopped answering.
    sealed class Reconnector
    {
        /// AirMouse's companion link service: tells the paired iPhone apart from other devices
        public static readonly Guid LinkService = new Guid("fb749375-7d73-4541-a1c1-5c4858d09521");
        public static readonly Guid HidService = new Guid("00001812-0000-1000-8000-00805f9b34fb");
        /// Apple's Continuity service, on every iPhone: worth a closer look when no pairing shows AirMouse
        static readonly Guid AppleContinuity = new Guid("d0611e78-bbb4-4591-a5f8-487910ae4366");
        /// enabled HID interfaces: AirMouse's mouse, keyboard and media keys show up here while Windows has them
        const string HidInterfaces =
            "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND " +
            "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

        public static readonly string DataFolder = Packaged.IsPackaged
            ? Packaged.DataFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirMouse");
        public static readonly string LogFile = Path.Combine(DataFolder, "airmouse.log");
        static readonly string DeviceFile = Path.Combine(DataFolder, "device.txt");

        static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(20);
        static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
        static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(1);
        static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(10);
        static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
        /// a healthy copy runs on a couple of dozen threads; far past that, something is stuck
        const int MaxThreads = 150;
        /// a one-sided pairing connects and drops about every 10 s, and its services never come
        /// back; a phone walking off or AirMouse closing does neither
        static readonly TimeSpan ChurnWindow = TimeSpan.FromMinutes(1);
        const int ChurnForOutOfSync = 6;
        const int UnreadableForOutOfSync = 3;

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        BluetoothLEDevice device;
        GattSession session;
        string address;
        /// re-reading the services while the app is closed finds nothing, so back off between tries
        TimeSpan retry = FirstRetry;
        DateTime nextRefresh = DateTime.MinValue;
        DateTime nextSearch = DateTime.MinValue;
        LinkState? reported;
        /// when the connection came or went lately, from the Bluetooth callback
        readonly Queue<DateTime> churn = new Queue<DateTime>();
        /// refreshes in a row that couldn't read the iPhone's services at all
        int unreadable;
        // set from Bluetooth callbacks and the tray, which must not wait on the loop or call back
        // into the device
        volatile bool changed;
        volatile bool refreshRequested;
        volatile bool lookRequested;
        volatile bool stopping;

        /// raised on the Bluetooth thread
        public event Action<LinkState> StateChanged;
        /// raised on the Bluetooth thread when this copy should be replaced by a fresh one
        public event Action RestartNeeded;

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

        /// after the pairing window closes: pick up the iPhone it paired, if any, right away
        public void LookAgain()
        {
            lookRequested = true;
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
                if (ThreadCount() > MaxThreads)
                {
                    Log("restarting: " + ThreadCount() + " threads");
                    var handler = RestartNeeded;
                    if (handler != null) handler();
                    return;
                }
                wake.WaitOne(TickInterval);
            }
            Release();
        }

        void Tick()
        {
            if (lookRequested)
            {
                lookRequested = false;
                nextSearch = DateTime.MinValue;
                // the window may have paired the same iPhone afresh, and a connection held from the
                // old pairing doesn't carry over
                if (device != null)
                {
                    Log("pairing window closed; connecting afresh");
                    Release();
                }
            }
            if (device != null && !StillPaired())
            {
                Log("pairing removed; looking for the iPhone again");
                Release();
                File.Delete(DeviceFile);
                nextSearch = DateTime.MinValue;
            }
            // the pairing window saved a different iPhone
            ulong saved = Saved();
            if (device != null && saved != 0 && saved.ToString("x12") != address)
            {
                Log("switching to the newly paired iPhone");
                Release();
            }
            if (device == null)
            {
                ulong found = saved;
                if (found == 0 && DateTime.Now >= nextSearch)
                {
                    nextSearch = DateTime.Now + SearchInterval;
                    if (Probe.Run("find") == Probe.Found) found = Saved();
                }
                if (found == 0)
                {
                    Report(LinkState.NotPaired);
                    return;
                }
                Attach(found);
            }

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
                Report(OutOfSync() ? LinkState.OutOfSync : LinkState.Away);
                return;
            }
            if (!forced && HasMouse(address))
            {
                unreadable = 0;
                Report(LinkState.Ready);
                return;
            }
            if (!forced && DateTime.Now < nextRefresh)
            {
                Report(OutOfSync() ? LinkState.OutOfSync : LinkState.Waiting);
                return;
            }
            int outcome = Probe.Run("refresh", address);
            bool back = outcome == Probe.Found;
            unreadable = outcome == Probe.Failed ? unreadable + 1 : 0;
            retry = back ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            nextRefresh = DateTime.Now + retry;
            Report(back ? LinkState.Ready : OutOfSync() ? LinkState.OutOfSync : LinkState.Waiting);
        }

        /// the link keeps dropping and the iPhone's services can't be read: see LinkState.OutOfSync
        bool OutOfSync()
        {
            if (unreadable < UnreadableForOutOfSync) return false;
            lock (churn)
            {
                while (churn.Count > 0 && DateTime.Now - churn.Peek() > ChurnWindow) churn.Dequeue();
                return churn.Count >= ChurnForOutOfSync;
            }
        }

        /// from Windows' device list: the device object's own pairing state is a snapshot from when
        /// it was opened, so it never sees the pairing removed
        bool StillPaired()
        {
            try
            {
                return Await(DeviceInformation.CreateFromIdAsync(device.DeviceId)).Pairing.IsPaired;
            }
            catch (AggregateException)
            {
                // gone from the list altogether
                return false;
            }
        }

        /// from Windows' device list alone, so it opens nothing on the device
        public static bool HasMouse(string address)
        {
            var interfaces = Await(DeviceInformation.FindAllAsync(HidInterfaces));
            return interfaces.Any(d => d.Id.IndexOf("_" + address, StringComparison.OrdinalIgnoreCase) >= 0);
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
                case LinkState.OutOfSync: Log("pairing out of sync: the link keeps dropping and the iPhone doesn't answer"); break;
            }
            var handler = StateChanged;
            if (handler != null) handler(state);
        }

        void Attach(ulong found)
        {
            address = found.ToString("x12");
            device = Await(BluetoothLEDevice.FromBluetoothAddressAsync(found));
            if (device == null) throw new InvalidOperationException("no device at " + address);
            // flags only: calling back into the device from its own callback can deadlock
            device.ConnectionStatusChanged += (d, o) =>
            {
                lock (churn) churn.Enqueue(DateTime.Now);
                changed = true;
                wake.Set();
            };
            // iOS announces it when the app adds its services back; re-read them soon after
            device.GattServicesChanged += (d, o) =>
            {
                changed = true;
                wake.Set();
            };
            session = Await(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId));
            session.MaintainConnection = true;
            Log("holding a connection to the iPhone (" + address + ")");
        }

        void Release()
        {
            if (session != null) session.Dispose();
            if (device != null) device.Dispose();
            session = null;
            device = null;
            reported = null;
            unreadable = 0;
            lock (churn) churn.Clear();
        }

        /// the iPhone the tray last attached to or the pairing window paired, 0 if none
        public static ulong Saved()
        {
            ulong saved;
            if (File.Exists(DeviceFile) &&
                ulong.TryParse(File.ReadAllText(DeviceFile).Trim(), NumberStyles.HexNumber, null, out saved))
            {
                return saved;
            }
            return 0;
        }

        // MARK: - in the probe process

        /// a paired device that publishes AirMouse's link service, saved for next time. Failing that,
        /// paired Apple devices are connected and checked afresh: a pairing goes stale exactly when
        /// Windows last looked while the app wasn't running
        public static bool Find()
        {
            var paired = Await(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
            var apple = new List<string>();
            foreach (var info in paired)
            {
                using (var candidate = Open(info.Id))
                {
                    if (candidate == null) continue;
                    if (Has(candidate, LinkService, BluetoothCacheMode.Cached)) return Remember(candidate);
                    if (Has(candidate, AppleContinuity, BluetoothCacheMode.Cached)) apple.Add(info.Id);
                }
            }
            foreach (var id in apple)
            {
                using (var candidate = Open(id))
                {
                    if (candidate != null && Connect(candidate, () => Has(candidate, LinkService, BluetoothCacheMode.Uncached)))
                    {
                        return Remember(candidate);
                    }
                }
            }
            return false;
        }

        /// null for a pairing Windows can't open, such as one made through another Bluetooth adapter
        static BluetoothLEDevice Open(string id)
        {
            try
            {
                return Await(BluetoothLEDevice.FromIdAsync(id));
            }
            catch (AggregateException)
            {
                return null;
            }
        }

        /// re-reads the iPhone's services past Windows' cache: Probe.Found if AirMouse's mouse is
        /// among them, NotFound if not (AirMouse isn't running), Failed if they couldn't be read
        public static int Refresh(ulong found)
        {
            using (var target = Await(BluetoothLEDevice.FromBluetoothAddressAsync(found)))
            {
                if (target == null) return Probe.Failed;
                int outcome = Probe.Failed;
                Connect(target, () =>
                {
                    var services = Services(target, BluetoothCacheMode.Uncached);
                    outcome = services == null ? Probe.Failed : services.Contains(HidService) ? Probe.Found : Probe.NotFound;
                    return true;
                });
                return outcome;
            }
        }

        static bool Connect(BluetoothLEDevice target, Func<bool> then)
        {
            using (var probe = Await(GattSession.FromDeviceIdAsync(target.BluetoothDeviceId)))
            {
                probe.MaintainConnection = true;
                var deadline = DateTime.Now + ConnectWait;
                while (target.ConnectionStatus != BluetoothConnectionStatus.Connected && DateTime.Now < deadline)
                {
                    Thread.Sleep(500);
                }
                return then();
            }
        }

        public static bool Remember(BluetoothLEDevice found)
        {
            string hex = found.BluetoothAddress.ToString("x12");
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(DeviceFile, hex);
            Log("found the iPhone (" + hex + ")");
            return true;
        }

        public static bool Has(BluetoothLEDevice target, Guid service, BluetoothCacheMode mode)
        {
            var services = Services(target, mode);
            return services != null && services.Contains(service);
        }

        /// the device's service UUIDs, or null if it didn't answer
        public static List<Guid> Services(BluetoothLEDevice target, BluetoothCacheMode mode)
        {
            var result = Await(target.GetGattServicesAsync(mode));
            var uuids = result.Status == GattCommunicationStatus.Success ? result.Services.Select(s => s.Uuid).ToList() : null;
            foreach (var s in result.Services) s.Dispose();
            return uuids;
        }

        // MARK: - helpers

        /// waits for a Windows Runtime call, but not forever
        public static T Await<T>(IAsyncOperation<T> operation)
        {
            return Await(operation, CallTimeout);
        }

        public static T Await<T>(IAsyncOperation<T> operation, TimeSpan limit)
        {
            var task = operation.AsTask();
            if (task.Wait(limit)) return task.Result;
            operation.Cancel();
            throw new TimeoutException("Bluetooth didn't answer");
        }

        static int ThreadCount()
        {
            using (var self = Process.GetCurrentProcess())
            {
                return self.Threads.Count;
            }
        }

        public static string Describe(Exception e)
        {
            var aggregate = e as AggregateException;
            return aggregate != null && aggregate.InnerException != null ? aggregate.InnerException.Message : e.Message;
        }

        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine;
            // the tray app, its probes and the pairing window share the file; one may have it open
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(DataFolder);
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 256 * 1024)
                    {
                        File.Copy(LogFile, LogFile + ".old", true);
                        File.Delete(LogFile);
                    }
                    File.AppendAllText(LogFile, line);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    /// GATT work in a short-lived copy of this exe (AirMouse.exe --probe ...), so whatever Windows'
    /// GATT objects leave behind goes away with it, and a copy that hangs can be killed
    static class Probe
    {
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);
        public const int Found = 0;
        public const int NotFound = 1;
        public const int Failed = 2;

        /// in the parent: Found, NotFound, or Failed (also when it timed out)
        public static int Run(params string[] args)
        {
            var start = new ProcessStartInfo(Application.ExecutablePath, "--probe " + string.Join(" ", args))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var child = Process.Start(start))
            {
                if (child.WaitForExit((int)Limit.TotalMilliseconds)) return child.ExitCode;
                Reconnector.Log("probe " + args[0] + " timed out");
                try
                {
                    child.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                return Failed;
            }
        }

        /// in the child
        public static int RunHere(string[] args)
        {
            try
            {
                switch (args.Length > 1 ? args[1] : "")
                {
                    case "find": return Reconnector.Find() ? Found : NotFound;
                    case "refresh": return Reconnector.Refresh(Convert.ToUInt64(args[2], 16));
                }
            }
            catch (Exception e)
            {
                Reconnector.Log("probe " + string.Join(" ", args) + " failed: " + Reconnector.Describe(e));
            }
            return Failed;
        }
    }
}
