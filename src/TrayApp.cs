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
        readonly bool welcome;
        string status = "Starting";
        bool explainedPairing;
        bool explainedOutOfSync;
        LinkState current = LinkState.NotPaired;
        /// what clicking the last balloon does, if anything
        Action balloonClick;
        Process pairing;

        public TrayApp(bool welcome, bool downloadedCopy)
        {
            this.welcome = welcome;
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem(status) { Enabled = false };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Reconnect now", null, (s, e) => link.RefreshNow());
            menu.Items.Add("Pair with iPhone…", null, (s, e) => Pair(current == LinkState.OutOfSync));
            menu.Items.Add("Bluetooth settings", null, (s, e) => Open("ms-settings:bluetooth"));
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
                if (balloonClick != null) balloonClick();
            };

            // another copy (the installer, the uninstaller, a second launch) talks to this one through these
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (o, t) => ui.Post(_ => ExitThread(), null), null, -1, true);
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (o, t) => ui.Post(_ => Balloon("AirMouse", status), null), null, -1, false);

            link.StateChanged += state => ui.Post(_ => Show(state), null);
            link.RestartNeeded += () => ui.Post(_ => Restart(), null);
            link.Start();
            if (downloadedCopy)
            {
                Balloon("Remove the older AirMouse", "This copy from the Microsoft Store replaces the AirMouse.exe you downloaded. Click here to remove that one in Installed apps.",
                    () => Open("ms-settings:appsfeatures"));
            }
            else if (welcome)
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
                case LinkState.OutOfSync: status = "Pairing out of sync"; break;
                default: status = "Not paired with AirMouse yet"; break;
            }
            statusItem.Text = status;
            string tip = "AirMouse: " + status;
            // NotifyIcon refuses tooltips longer than 63 characters
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            tray.Icon = state == LinkState.Ready ? readyIcon : idleIcon;
            current = state;
            if (state == LinkState.Ready) explainedOutOfSync = false;
            if (state == LinkState.OutOfSync && !explainedOutOfSync)
            {
                explainedOutOfSync = true;
                Balloon("Pair your iPhone again", "Your iPhone kept an old pairing for this PC, so the mouse can't connect. Click here to fix it.",
                    () => Pair(true));
            }
            if (state == LinkState.NotPaired && !explainedPairing)
            {
                explainedPairing = true;
                // just installed: straight to pairing
                if (welcome)
                {
                    Pair(false);
                    return;
                }
                Balloon("Pair your iPhone", "AirMouse isn't paired with this PC yet. Click here to pair it.", () => Pair(false));
            }
        }

        /// the pairing window, in its own copy of the exe; the link looks again once it closes.
        /// again: the pairing is out of sync, so the window starts by fixing that
        void Pair(bool again)
        {
            if (pairing != null && !pairing.HasExited)
            {
                PairingWindow.BringForward(pairing);
                return;
            }
            try
            {
                pairing = Process.Start(Application.ExecutablePath, again ? "--pair --again" : "--pair");
                pairing.EnableRaisingEvents = true;
                pairing.Exited += (s, e) => link.LookAgain();
            }
            catch (Exception e)
            {
                Reconnector.Log("couldn't open the pairing window: " + e.Message);
            }
        }

        /// hands over to a fresh copy of the app, which waits for this one to let go
        void Restart()
        {
            Process.Start(Application.ExecutablePath, "--restarted");
            ExitThread();
        }

        void Balloon(string title, string text, Action click = null)
        {
            balloonClick = click;
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
                    // the pairing window doesn't listen for quit; a killed copy still holds the exe a moment
                    if (!other.WaitForExit(5000))
                    {
                        other.Kill();
                        other.WaitForExit(5000);
                    }
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
