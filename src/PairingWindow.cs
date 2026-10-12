using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AirMouse
{
    /// Pairs the iPhone from here instead of Bluetooth settings, whose Add device list leaves AirMouse
    /// out until "Bluetooth devices discovery" is set to Advanced.
    ///
    /// Listens for devices advertising a mouse, keeps the one that publishes AirMouse's link service,
    /// and pairs it once the code matches on both screens. It runs in its own copy of the exe
    /// (AirMouse.exe --pair), so nothing Bluetooth it opens stays behind in the tray app.
    sealed class PairingWindow : Form
    {
        static readonly TimeSpan SearchLimit = TimeSpan.FromMinutes(3);
        static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(10);
        static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(60);
        static readonly TimeSpan PairLimit = TimeSpan.FromMinutes(2);
        /// Windows sets up the mouse a few seconds after pairing
        static readonly TimeSpan SetupLimit = TimeSpan.FromSeconds(30);
        const string Steps =
            "On your iPhone, open AirMouse, tap the connection status at the top, then Pair a new device. " +
            "Keep AirMouse on screen until pairing finishes.";
        const string SettingsFallback =
            "You can also pair in Bluetooth settings: Add device > Bluetooth. If AirMouse isn't listed there, " +
            "set Bluetooth devices discovery to Advanced under Bluetooth & devices > Devices.";

        /// the DPI the window's text is drawn at; Control.DeviceDpi says 96 unless an app config
        /// turns on WinForms' DPI handling, which a lone exe can't ship
        readonly float dpi;
        readonly Label heading;
        readonly Label body;
        readonly Label code;
        readonly Label status;
        readonly Button primary;
        readonly Button secondary;
        Action onPrimary;
        Action onSecondary;
        CancellationTokenSource attempt;
        /// the code question the Bluetooth thread is waiting on, if any
        Answer question;

        public bool Paired { get; private set; }

        public static int RunHere()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var window = new PairingWindow())
            {
                Application.Run(window);
                return window.Paired ? 0 : 1;
            }
        }

        PairingWindow()
        {
            Text = "Pair your iPhone with AirMouse";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            // the screen's, not the form's: making the form's handle this early would skip CenterScreen
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                dpi = graphics.DpiX;
            }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            int width = Px(400);
            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(Px(20), Px(16), Px(20), Px(16))
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            heading = NewLabel(width, new Font(Font.FontFamily, Font.SizeInPoints * 1.3f, FontStyle.Bold), Px(8));
            body = NewLabel(width, Font, Px(8));
            code = NewLabel(width, new Font(Font.FontFamily, Font.SizeInPoints * 2.6f, FontStyle.Bold), Px(8));
            status = NewLabel(width, Font, 0);
            status.ForeColor = SystemColors.GrayText;
            primary = new Button { AutoSize = true, MinimumSize = new Size(Px(88), 0), Margin = new Padding(Px(8), 0, 0, 0) };
            secondary = new Button { AutoSize = true, MinimumSize = new Size(Px(88), 0), Margin = new Padding(Px(8), 0, 0, 0) };
            primary.Click += (s, e) => { if (onPrimary != null) onPrimary(); };
            secondary.Click += (s, e) => { if (onSecondary != null) onSecondary(); };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, Px(16), 0, 0)
            };
            buttons.Controls.Add(secondary);
            buttons.Controls.Add(primary);
            layout.Controls.Add(heading);
            layout.Controls.Add(body);
            layout.Controls.Add(code);
            layout.Controls.Add(status);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = primary;
            CancelButton = secondary;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Search();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (attempt != null) attempt.Cancel();
            if (question != null) question.Give(false);
            base.OnFormClosed(e);
        }

        // MARK: - what the window shows

        void Search()
        {
            if (attempt != null) attempt.Cancel();
            attempt = new CancellationTokenSource();
            var token = attempt.Token;
            Present("Pair your iPhone", Steps, null, "Looking for your iPhone…");
            Buttons(null, null, "Cancel", Close);
            new Thread(() => Look(token)) { IsBackground = true, Name = "AirMouse pairing" }.Start();
        }

        void Ask(DevicePairingKinds kind, string pin, string name, Answer answer)
        {
            question = answer;
            if (kind == DevicePairingKinds.ConfirmPinMatch)
            {
                Present("Check the code",
                    "Your iPhone asks to pair with this PC and shows a code. If it's the same as this one, tap Pair on the iPhone, then Pair here.",
                    pin, null);
            }
            else
            {
                Present("Pair with “" + name + "”?", "Tap Pair on the iPhone too.", null, null);
            }
            Buttons("Pair", () => Answered(true), "Cancel", () => Answered(false));
            Activate();
        }

        void Answered(bool accepted)
        {
            if (question != null) question.Give(accepted);
            question = null;
            Present("Pairing…", "Tap Pair on the iPhone if you haven't yet.", null, accepted ? "Finishing up…" : "Cancelling…");
            Buttons(null, null, "Cancel", Close);
        }

        void Done(string name, bool already, bool ready)
        {
            Paired = true;
            if (already)
            {
                Present("Already paired", "This PC is already paired with “" + name + "”. AirMouse for Windows keeps it connected.", null, null);
            }
            else if (ready)
            {
                Present("Paired", "Your iPhone works as a mouse and keyboard now. AirMouse for Windows keeps it connected.", null, null);
            }
            else
            {
                Present("Paired", "Keep AirMouse open on the iPhone. Windows sets up the mouse as soon as it connects.", null, null);
            }
            Buttons("Done", Close, null, null);
            Activate();
        }

        void Failed(string reason)
        {
            Present("Pairing didn't finish", reason + "\n\n" + SettingsFallback, null, null);
            Buttons("Try again", Search, "Close", Close);
        }

        void Present(string title, string text, string pin, string progress)
        {
            heading.Text = title;
            body.Text = text;
            code.Text = pin ?? "";
            code.Visible = pin != null;
            status.Text = progress ?? "";
            status.Visible = progress != null;
        }

        void Buttons(string first, Action firstAction, string second, Action secondAction)
        {
            primary.Text = first ?? "";
            primary.Visible = first != null;
            onPrimary = firstAction;
            secondary.Text = second ?? "";
            secondary.Visible = second != null;
            onSecondary = secondAction;
            AcceptButton = first != null ? primary : secondary;
        }

        void Status(string text)
        {
            status.Text = text;
            status.Visible = true;
        }

        /// from the Bluetooth thread
        void Ui(Action action)
        {
            try
            {
                if (!IsDisposed) BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // the window closed
            }
        }

        // MARK: - on the Bluetooth thread

        sealed class Heard
        {
            public ulong Address;
            public string Name;
        }

        enum Outcome { NotAirMouse, Unreachable, Finished }

        void Look(CancellationToken token)
        {
            var heard = new BlockingCollection<Heard>();
            var queued = new HashSet<ulong>();
            BluetoothError radio = BluetoothError.Success;
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            // the iPhone puts the mouse service and AirMouse's name in the advertisement itself, while
            // AirMouse is on screen
            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(Reconnector.HidService);
            watcher.Received += (w, e) =>
            {
                lock (queued)
                {
                    if (heard.IsAddingCompleted || !queued.Add(e.BluetoothAddress)) return;
                    heard.Add(new Heard { Address = e.BluetoothAddress, Name = e.Advertisement.LocalName });
                }
            };
            watcher.Stopped += (w, e) =>
            {
                if (e.Error == BluetoothError.Success) return;
                radio = e.Error;
                lock (queued) heard.CompleteAdding();
            };
            Reconnector.Log("pairing: looking for the iPhone");
            watcher.Start();
            try
            {
                var deadline = DateTime.Now + SearchLimit;
                Heard next;
                while (heard.TryTake(out next, Remaining(deadline), token))
                {
                    var outcome = TryPair(next, watcher, token);
                    if (outcome == Outcome.Finished) return;
                    // not AirMouse stays in queued, so it isn't looked at again
                    if (outcome == Outcome.Unreachable)
                    {
                        lock (queued) queued.Remove(next.Address);
                    }
                }
                if (radio != BluetoothError.Success)
                {
                    Reconnector.Log("pairing: Bluetooth stopped: " + radio);
                    Ui(() => Failed("Bluetooth is off or unavailable on this PC. Turn it on, then try again."));
                }
                else
                {
                    Reconnector.Log("pairing: no iPhone found");
                    Ui(() => Failed("Didn't find your iPhone. Check that AirMouse is open on the iPhone and Bluetooth is on for both."));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Reconnector.Log("pairing failed: " + Reconnector.Describe(e));
                Ui(() => Failed("Something went wrong: " + Reconnector.Describe(e)));
            }
            finally
            {
                watcher.Stop();
            }
        }

        Outcome TryPair(Heard heard, BluetoothLEAdvertisementWatcher watcher, CancellationToken token)
        {
            string hex = heard.Address.ToString("x12");
            using (var device = Reconnector.Await(BluetoothLEDevice.FromBluetoothAddressAsync(heard.Address)))
            {
                if (device == null) return Outcome.Unreachable;
                string name = string.IsNullOrEmpty(heard.Name) ? device.Name : heard.Name;
                using (var session = Reconnector.Await(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)))
                {
                    session.MaintainConnection = true;
                    var deadline = DateTime.Now + ConnectWait;
                    while (device.ConnectionStatus != BluetoothConnectionStatus.Connected && DateTime.Now < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        Thread.Sleep(250);
                    }
                    if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    {
                        Reconnector.Log("pairing: couldn't connect to " + hex);
                        return Outcome.Unreachable;
                    }
                    // a keyboard or mouse nearby also advertises a mouse service
                    if (!Reconnector.Has(device, Reconnector.LinkService, BluetoothCacheMode.Uncached))
                    {
                        Reconnector.Log("pairing: " + hex + " (" + name + ") isn't AirMouse");
                        return Outcome.NotAirMouse;
                    }
                    watcher.Stop();
                    token.ThrowIfCancellationRequested();
                    if (device.DeviceInformation.Pairing.IsPaired)
                    {
                        Reconnector.Log("pairing: already paired with " + hex);
                        Reconnector.Remember(device);
                        Ui(() => Done(name, true, true));
                        return Outcome.Finished;
                    }
                    Pair(device, name, token);
                    return Outcome.Finished;
                }
            }
        }

        void Pair(BluetoothLEDevice device, string name, CancellationToken token)
        {
            string hex = device.BluetoothAddress.ToString("x12");
            Ui(() => Status("Pairing with “" + name + "”…"));
            Reconnector.Log("pairing: found AirMouse at " + hex + " (" + name + ")");
            var custom = device.DeviceInformation.Pairing.Custom;
            bool unanswered = false;
            custom.PairingRequested += (s, request) =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var answer = new Answer();
                    var kind = request.PairingKind;
                    var pin = request.Pin;
                    Ui(() => Ask(kind, pin, name, answer));
                    if (answer.Wait(AnswerLimit, token)) request.Accept();
                    else unanswered = !answer.Given;
                }
                finally
                {
                    deferral.Complete();
                }
            };
            // a code on both screens, as Bluetooth settings does it, so nothing nearby can pose as the iPhone
            var result = Reconnector.Await(
                custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch,
                    DevicePairingProtectionLevel.EncryptionAndAuthentication),
                PairLimit);
            Reconnector.Log("pairing: " + result.Status + " (" + result.ProtectionLevelUsed + ")");
            if (result.Status != DevicePairingResultStatus.Paired && result.Status != DevicePairingResultStatus.AlreadyPaired)
            {
                string reason = unanswered ? "The code wasn't confirmed in time. Tap Pair on both screens within half a minute." : Explain(result.Status);
                Ui(() => Failed(reason));
                return;
            }
            Reconnector.Remember(device);
            Ui(() =>
            {
                Present("Paired", "Windows is setting up your iPhone as a mouse and keyboard.", null, "Almost done…");
                Buttons(null, null, "Close", Close);
            });
            var setup = DateTime.Now + SetupLimit;
            bool ready;
            while (!(ready = Reconnector.HasMouse(hex)) && DateTime.Now < setup && !token.IsCancellationRequested)
            {
                Thread.Sleep(1000);
            }
            Reconnector.Log(ready ? "pairing: Windows has the mouse" : "pairing: no mouse yet");
            Ui(() => Done(name, false, ready));
        }

        static string Explain(DevicePairingResultStatus status)
        {
            switch (status)
            {
                case DevicePairingResultStatus.PairingCanceled:
                case DevicePairingResultStatus.RejectedByHandler:
                    return "Pairing was cancelled.";
                case DevicePairingResultStatus.AuthenticationTimeout:
                    return "The iPhone didn't answer in time.";
                case DevicePairingResultStatus.AuthenticationFailure:
                case DevicePairingResultStatus.AccessDenied:
                    return "The iPhone didn't accept the pairing, or the codes didn't match.";
                case DevicePairingResultStatus.NotReadyToPair:
                case DevicePairingResultStatus.ConnectionRejected:
                    return "Couldn't reach the iPhone. Keep AirMouse open on it and try again.";
                default:
                    return "Windows couldn't pair with the iPhone (" + status + ").";
            }
        }

        static int Remaining(DateTime deadline)
        {
            return (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
        }

        // MARK: - helpers

        /// the user's answer to the code, handed from the window to the Bluetooth thread
        sealed class Answer
        {
            readonly ManualResetEventSlim given = new ManualResetEventSlim(false);
            volatile bool accepted;

            public bool Given
            {
                get { return given.IsSet; }
            }

            public void Give(bool accept)
            {
                accepted = accept;
                given.Set();
            }

            /// true only if the user chose Pair in time
            public bool Wait(TimeSpan limit, CancellationToken token)
            {
                try
                {
                    return given.Wait(limit, token) && accepted;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        static Label NewLabel(int width, Font font, int bottom)
        {
            return new Label
            {
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Font = font,
                // "Bluetooth & devices" keeps its &
                UseMnemonic = false,
                Margin = new Padding(0, 0, 0, bottom)
            };
        }

        int Px(int dips)
        {
            return (int)Math.Round(dips * dpi / 96.0);
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr window);

        /// brings an open pairing window forward; the tray app may, since the user just clicked it
        public static void BringForward(System.Diagnostics.Process pairing)
        {
            pairing.Refresh();
            if (pairing.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(pairing.MainWindowHandle);
        }
    }
}
