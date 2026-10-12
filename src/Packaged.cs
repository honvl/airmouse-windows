using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Windows.ApplicationModel;
using Windows.Storage;

namespace AirMouse
{
    /// The Microsoft Store copy: AirMouse.exe inside an MSIX package (see msix\). Windows installs,
    /// updates and removes it, so the self-installer stays out of the way. Signing in starts it
    /// through the package's startup task rather than the Run key, and it keeps its files in the
    /// package's own data folder.
    static class Packaged
    {
        /// the startup task in msix\AppxManifest.xml
        const string StartupTaskId = "AirMouseStartup";
        const int NoPackage = 15700;

        public static readonly bool IsPackaged = Detect();

        /// the package's LocalState folder, a real folder other apps (Notepad, for the log) can open
        public static string DataFolder
        {
            get { return ApplicationData.Current.LocalFolder.Path; }
        }

        /// true the first time the Store copy runs for this user
        public static bool FirstRun()
        {
            string marker = Path.Combine(DataFolder, "welcomed");
            if (File.Exists(marker)) return false;
            File.WriteAllText(marker, Program.Version);
            return true;
        }

        public static bool StartsWithWindows
        {
            get
            {
                var state = Startup().State;
                return state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy;
            }
            set
            {
                var task = Startup();
                if (!value)
                {
                    task.Disable();
                    return;
                }
                // no prompt for a desktop app, but Windows won't override the user turning it off
                var state = Reconnector.Await(task.RequestEnableAsync());
                if (state == StartupTaskState.DisabledByUser || state == StartupTaskState.DisabledByPolicy)
                {
                    MessageBox.Show("Windows has AirMouse turned off at sign-in. Turn it on in Settings > Apps > Startup.",
                        Installer.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        static StartupTask Startup()
        {
            return Reconnector.Await(StartupTask.GetAsync(StartupTaskId));
        }

        /// A copy installed from the downloaded AirMouse.exe starts at sign-in through the Run key,
        /// holds the running-copy mutex, and so keeps this one from starting. Stops it; the tray then
        /// asks the user to remove it in Installed apps.
        public static bool RetireDownloadedCopy()
        {
            if (!File.Exists(Installer.InstalledExe)) return false;
            foreach (var other in Process.GetProcessesByName("AirMouse"))
            {
                try
                {
                    if (!string.Equals(other.MainModule.FileName, Installer.InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                    other.Kill();
                    other.WaitForExit(5000);
                    Reconnector.Log("stopped the downloaded copy (pid " + other.Id + ")");
                }
                catch (Win32Exception)
                {
                    // another account's copy
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }
            return true;
        }

        static bool Detect()
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != NoPackage;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);
    }
}
