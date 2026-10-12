using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace AirMouse
{
    /// the notification-area icon: bright while Windows has AirMouse's mouse, faded otherwise, with a
    /// menu that says what is going on
    sealed class TrayApp : ApplicationContext
    {
        const string QuitEventName = @"Local\AirMouseForWindows.Quit";
        const string ShowEventName = @"Local\AirMouseForWindows.Show";
        const string PairingSteps =
            "1. On the iPhone, open AirMouse, tap the connection status at the top, then Pair a new device.\n" +
            "2. On Windows, open Settings > Bluetooth & devices > Add device > Bluetooth.\n" +
            "   Don't see AirMouse? Set \"Bluetooth devices discovery\" to Advanced in Bluetooth & devices > Devices.\n" +
            "3. Pick AirMouse and confirm the code on both screens.";

        readonly Reconnector link = new Reconnector();
        readonly SynchronizationContext ui = new WindowsFormsSynchronizationContext();
        readonly NotifyIcon tray;
        readonly Icon readyIcon = LoadIcon("AirMouse.ico");
        readonly Icon idleIcon = LoadIcon("AirMouseIdle.ico");
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem startItem;
        readonly EventWaitHandle quitEvent;
        readonly EventWaitHandle showEvent;
        readonly RegisteredWaitHandle quitWait;
        readonly RegisteredWaitHandle showWait;
        string status = "Starting";
        bool explainedPairing;

        public TrayApp(bool welcome)
        {
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem(status) { Enabled = false };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Reconnect now", null, (s, e) => link.RefreshNow());
            menu.Items.Add("Bluetooth settings", null, (s, e) => Open("ms-settings:bluetooth"));
            menu.Items.Add("How to pair", null, (s, e) => MessageBox.Show(PairingSteps, "Pair your iPhone with AirMouse"));
            menu.Items.Add(new ToolStripSeparator());
            startItem = new ToolStripMenuItem("Start when I sign in", null, (s, e) => ToggleStart());
            startItem.Checked = Installer.StartsWithWindows;
            menu.Items.Add(startItem);
            menu.Items.Add("Open log", null, (s, e) => Open(Reconnector.LogFile));
            menu.Items.Add("Quit", null, (s, e) => ExitThread());

            tray = new NotifyIcon { Icon = idleIcon, Text = "AirMouse", ContextMenuStrip = menu, Visible = true };
            // a left click opens the same menu; NotifyIcon only does that for right clicks itself
            tray.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(tray, null);
            };
            tray.BalloonTipClicked += (s, e) =>
            {
                if (!explainedPairing) return;
                Open("ms-settings:bluetooth");
            };

            // another copy (the installer, the uninstaller, a second launch) talks to this one through these
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (o, t) => ui.Post(_ => ExitThread(), null), null, -1, true);
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (o, t) => ui.Post(_ => Balloon("AirMouse", status), null), null, -1, false);

            link.StateChanged += state => ui.Post(_ => Show(state), null);
            link.Start();
            if (welcome)
            {
                Balloon("AirMouse is running", "It lives here in the notification area and keeps your iPhone connected as a mouse.");
            }
        }

        void Show(LinkState state)
        {
            // not the device's name: Windows calls it "iPhone" or "AirMouse" depending on what it read last
            switch (state)
            {
                case LinkState.Ready: status = "Connected to your iPhone"; break;
                case LinkState.Waiting: status = "Open AirMouse on your iPhone"; break;
                case LinkState.Away: status = "Looking for your iPhone"; break;
                default: status = "Not paired with AirMouse yet"; break;
            }
            statusItem.Text = status;
            string tip = "AirMouse: " + status;
            // NotifyIcon refuses tooltips longer than 63 characters
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            tray.Icon = state == LinkState.Ready ? readyIcon : idleIcon;
            if (state == LinkState.NotPaired && !explainedPairing)
            {
                explainedPairing = true;
                Balloon("Pair your iPhone", "Open AirMouse on the iPhone and tap Pair a new device, then add AirMouse in Bluetooth settings. Click here to open them.");
            }
        }

        void Balloon(string title, string text)
        {
            tray.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
        }

        void ToggleStart()
        {
            Installer.StartsWithWindows = !Installer.StartsWithWindows;
            startItem.Checked = Installer.StartsWithWindows;
        }

        static void Open(string target)
        {
            try
            {
                Process.Start(target);
            }
            catch (Exception e)
            {
                Reconnector.Log("couldn't open " + target + ": " + e.Message);
            }
        }

        static Icon LoadIcon(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }

        protected override void ExitThreadCore()
        {
            link.Stop();
            quitWait.Unregister(null);
            showWait.Unregister(null);
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        /// asks a running copy to quit and waits for it, so its exe can be replaced or removed
        public static void QuitRunning()
        {
            EventWaitHandle quit;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out quit))
            {
                quit.Set();
                quit.Dispose();
            }
            var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName("AirMouse"))
            {
                // copies in other sessions belong to other accounts, and Windows won't let us wait on them
                if (other.Id == self.Id || other.SessionId != self.SessionId) continue;
                try
                {
                    if (!other.WaitForExit(5000)) other.Kill();
                }
                catch (Win32Exception)
                {
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }
        }

        /// a second launch (from Start, say) just has the running copy show where things stand
        public static void ShowRunning()
        {
            EventWaitHandle show;
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out show)) return;
            show.Set();
            show.Dispose();
        }
    }
}
