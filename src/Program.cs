using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AirMouse for Windows")]
[assembly: AssemblyDescription("Pairs the AirMouse iPhone app as a Bluetooth mouse and keeps it connected")]
[assembly: AssemblyProduct("AirMouse")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]

namespace AirMouse
{
    /// AirMouse for Windows. Run from a download, it installs itself (see Installer); installed, or
    /// from the Microsoft Store (see Packaged), it sits in the notification area and keeps the
    /// iPhone connected (see Reconnector).
    ///
    ///   AirMouse.exe [--quiet]          install, or update an installed copy
    ///   AirMouse.exe --uninstall [--quiet]
    ///   AirMouse.exe --pair              the pairing window (see PairingWindow)
    ///   AirMouse.exe --probe ...         Bluetooth work for the running copy (see Probe)
    static class Program
    {
        public const string Version = "1.2.0";

        [STAThread]
        static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--probe") == 0) return Probe.RunHere(args);
            if (Array.IndexOf(args, "--pair") == 0) return PairingWindow.RunHere();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            if (!Packaged.IsPackaged && (Array.IndexOf(args, "--uninstall") >= 0 || !Installer.IsInstalledCopy))
            {
                try
                {
                    if (Array.IndexOf(args, "--uninstall") >= 0) Installer.Uninstall(quiet);
                    else Installer.Install(quiet);
                }
                catch (Exception e)
                {
                    Reconnector.Log("setup failed: " + e);
                    if (!quiet) MessageBox.Show(e.Message, Installer.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                return 0;
            }
            bool downloadedCopy = Packaged.IsPackaged && Packaged.RetireDownloadedCopy();
            bool welcome = Array.IndexOf(args, "--welcome") >= 0 || (Packaged.IsPackaged && Packaged.FirstRun());
            using (var running = new Mutex(false, @"Local\AirMouseForWindows"))
            {
                // a restart hands over from a copy that is still on its way out
                bool restarted = Array.IndexOf(args, "--restarted") >= 0;
                if (!Acquire(running, restarted ? 15000 : 0))
                {
                    TrayApp.ShowRunning();
                    return 0;
                }
                Application.Run(new TrayApp(welcome, downloadedCopy));
                running.ReleaseMutex();
            }
            return 0;
        }

        /// a copy that crashed leaves the mutex abandoned, which still hands it over
        static bool Acquire(Mutex mutex, int milliseconds)
        {
            try
            {
                return mutex.WaitOne(milliseconds);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }
}
