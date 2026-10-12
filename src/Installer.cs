using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirMouse
{
    /// One exe installs itself for the current user: no admin, no installer framework. Run from a
    /// download it copies itself to %LOCALAPPDATA%\Programs\AirMouse, starts at sign-in, and shows up
    /// in Start and in Apps and features, whose Uninstall runs it with --uninstall.
    static class Installer
    {
        public const string DisplayName = "AirMouse for Windows";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "AirMouse";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AirMouse";

        static readonly string ProgramDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AirMouse");
        public static readonly string InstalledExe = Path.Combine(ProgramDir, "AirMouse.exe");
        static readonly string Shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AirMouse.lnk");

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe, StringComparison.OrdinalIgnoreCase); }
        }

        public static bool StartsWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(RunValue) != null;
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(RunValue, Quote(InstalledExe));
                    else key.DeleteValue(RunValue, false);
                }
            }
        }

        /// quiet: no questions, for scripted installs (winget and the like)
        public static void Install(bool quiet)
        {
            if (!quiet)
            {
                var answer = MessageBox.Show(
                    "AirMouse for Windows pairs your iPhone as a mouse and keeps it connected, even after the AirMouse app restarts.\n\n" +
                    "It installs for your account only, lives in the notification area, and starts when you sign in.",
                    "Install " + DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (answer != DialogResult.OK) return;
            }
            // a running copy holds the exe open
            TrayApp.QuitRunning();
            Directory.CreateDirectory(ProgramDir);
            File.Copy(Application.ExecutablePath, InstalledExe, true);
            StartsWithWindows = true;
            CreateShortcut();
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", Program.Version);
                key.SetValue("Publisher", "AirMouse");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", ProgramDir);
                key.SetValue("UninstallString", Quote(InstalledExe) + " --uninstall");
                key.SetValue("QuietUninstallString", Quote(InstalledExe) + " --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
            }
            Reconnector.Log("installed " + Program.Version + " to " + ProgramDir);
            Process.Start(InstalledExe, "--welcome");
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet)
            {
                var answer = MessageBox.Show(
                    "Remove " + DisplayName + "? Your iPhone stays paired; Windows just stops reconnecting it after AirMouse restarts.",
                    DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
            }
            TrayApp.QuitRunning();
            StartsWithWindows = false;
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            try
            {
                Directory.Delete(Reconnector.DataFolder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            // the running exe can't delete itself; cmd removes the folder once this process has exited
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q " + Quote(ProgramDir))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        /// a Start menu shortcut, made through the shell's scripting object so there's no interop library
        static void CreateShortcut()
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut });
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { InstalledExe });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { DisplayName });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }

        static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
