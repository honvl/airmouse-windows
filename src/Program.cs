using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AirMouse for Windows")]
[assembly: AssemblyDescription("Keeps the AirMouse iPhone app connected as a Bluetooth mouse")]
[assembly: AssemblyProduct("AirMouse")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace AirMouse
{
    /// AirMouse for Windows. Run from a download, it installs itself (see Installer); installed, it
    /// sits in the notification area and keeps the iPhone connected (see Reconnector).
    ///
    ///   AirMouse.exe [--quiet]          install, or update an installed copy
    ///   AirMouse.exe --uninstall [--quiet]
    static class Program
    {
        public const string Version = "1.0.0";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            if (Array.IndexOf(args, "--uninstall") >= 0 || !Installer.IsInstalledCopy)
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
                    Environment.ExitCode = 1;
                }
                return;
            }
            bool first;
            using (var running = new Mutex(true, @"Local\AirMouseForWindows", out first))
            {
                if (!first)
                {
                    TrayApp.ShowRunning();
                    return;
                }
                Application.Run(new TrayApp(Array.IndexOf(args, "--welcome") >= 0));
                GC.KeepAlive(running);
            }
        }
    }
}
