// HPG Mission Companion installer and uninstaller (one executable, .NET Framework 4, no SDK).
// Install: per user, no administrator rights. It writes only the program files, the voice packs through the program's own
// --install-pack (an older version of a pack is archived), the shortcuts and the uninstall entry of the current user.
// It never touches the settings, the Piper engine, the voices, the audio folder or the backups of an earlier install, unless the complete setup is chosen:
// then the program itself (--oneclick) applies the recommended profile and downloads the Piper engine and the voices it needs (the old settings are kept in backups\settings).
// Uninstall: removes what the install wrote; the user data is removed only when the user answers yes (default no). The uninstaller of the folder copies itself to the temp folder
// (/fromtemp /waitpid=PID, internal switches) and ends there, so that it can delete itself and the folder without a shell.
// Switches: /S silent, /dir=PATH install folder, /nodesktop, /nostartmenu, /nolaunch, /oneclick (complete setup; /nooneclick leaves it out), /uninstall, /removedata (silent uninstall).
// The window offers the complete setup, ticked when the folder has no settings yet; the silent install does it only with /oneclick.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("HPG Mission Companion Setup")]
[assembly: AssemblyProduct("HPG Mission Companion")]
[assembly: AssemblyVersion("0.40.0.0")]
[assembly: AssemblyFileVersion("0.40.0.0")]

namespace HpgSetup
{
    static class Info
    {
        public const string Version = "0.40";
        public const string Name = "HPG Mission Companion";
        public const string Author = "Andrea Daviero";
        public const string ExeName = "HPGMissionCompanion.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HPGMissionCompanion";
        public const string Home = "https://github.com/davierosoft/HPG-Mission-Companion";
        public const string Discord = "https://discord.gg/eUqz49jXzT";

        public static string DefaultDir()
        {
            string old = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (k != null) old = k.GetValue("InstallLocation") as string;
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(old)) return old;
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "HPG Mission Companion");
        }

        public static string StartMenuDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HPG Mission Companion");
        }

        public static string DesktopLink()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HPG Mission Companion.lnk");
        }

        public static void Log(string line)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "HPGMissionCompanionSetup.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine); }
            catch (Exception) { }
        }
    }

    class Options
    {
        public bool Silent;
        public bool Uninstall;
        public string Dir;
        public bool Desktop = true;
        public bool StartMenu = true;
        public bool Launch = true;
        public bool RemoveData;
        public bool OneClick;
        public bool OneClickSet;
        public string Screenshot;
        public bool FromTemp;       // the uninstaller runs from a copy in the temp folder (the one in the install folder cannot delete itself)
        public int WaitPid;         // the process to wait for before going on
        public bool ShowErrors;     // silent setup started by the program (the update): a failure is shown, not only logged
        public bool Elevated;       // this copy of the setup was started again with administrator rights to close a program that it could not close
        public static string[] RawArgs = new string[0];

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            RawArgs = args;
            foreach (string raw in args)
            {
                string a = raw.Trim();
                string l = a.ToLowerInvariant();
                if (l == "/s" || l == "/silent" || l == "-s" || l == "--silent") o.Silent = true;
                else if (l == "/uninstall" || l == "--uninstall") o.Uninstall = true;
                else if (l == "/nodesktop") o.Desktop = false;
                else if (l == "/nostartmenu") o.StartMenu = false;
                else if (l == "/nolaunch") o.Launch = false;
                else if (l == "/removedata") o.RemoveData = true;
                else if (l == "/oneclick") { o.OneClick = true; o.OneClickSet = true; }
                else if (l == "/nooneclick") { o.OneClick = false; o.OneClickSet = true; }
                else if (l == "/fromtemp") o.FromTemp = true;
                else if (l == "/showerrors") o.ShowErrors = true;
                else if (l == "/elevated") o.Elevated = true;
                else if (l.StartsWith("/waitpid=")) int.TryParse(a.Substring(9), out o.WaitPid);
                else if (l.StartsWith("/dir=")) o.Dir = a.Substring(5).Trim('"');
                else if (l.StartsWith("/screenshot=")) o.Screenshot = a.Substring(12).Trim('"');
            }
            return o;
        }
    }

    class NeedElevationException : Exception
    {
        public NeedElevationException(string message) : base(message) { }
    }

    static class Ops
    {
        public static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        public static bool HasResource(string name)
        {
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            return false;
        }

        static void Extract(string name, string path)
        {
            // a file that the program has just left can stay locked for a moment (antivirus, the end of the process): try for about ten seconds
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (Stream s = Resource(name))
                    {
                        if (s == null) throw new InvalidOperationException("missing resource " + name);
                        using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write))
                        {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) f.Write(buf, 0, n);
                        }
                    }
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt >= 20) throw new IOException("The file " + path + " is in use and cannot be replaced (" + ex.Message + "). Close the program (the icon in the notification area, Exit) and run the setup again.");
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (attempt >= 20) throw new IOException("The file " + path + " cannot be written (" + ex.Message + "). Close the program and run the setup again.");
                    Thread.Sleep(500);
                }
            }
        }

        public static string ValidateDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "Choose an install folder.";
            try
            {
                string full = Path.GetFullPath(dir);
                if (!Path.IsPathRooted(full)) return "The folder must be an absolute path.";
                if (string.Equals(full.TrimEnd('\\'), Path.GetPathRoot(full).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "Do not install in the root of a drive.";
            }
            catch (Exception) { return "The folder name is not valid."; }
            return null;
        }

        // every copy of the program that runs now, wherever it runs from (the program allows one copy: the one of an older folder, or one whose
        // path cannot be read because it runs with other rights, would hold the port and the files all the same)
        static List<Process> RunningCopies()
        {
            List<Process> list = new List<Process>();
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("HPGMissionCompanion")) if (p.Id != me) list.Add(p);
            return list;
        }

        static string PathOf(Process p)
        {
            try { return p.MainModule.FileName; }
            catch (Exception) { return "path not readable"; }
        }

        public static bool IsRunning(string dir) { return RunningCopies().Count > 0; }

        public static bool IsElevated()
        {
            try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        // ends the Piper processes of the folders of the copies that are closed (the children of the program: a program that is ended by force leaves them behind)
        static void KillPiper(List<string> dirs)
        {
            List<string> roots = new List<string>();
            foreach (string d in dirs) { try { roots.Add(Path.GetFullPath(d).TrimEnd('\\') + "\\"); } catch (Exception) { } }
            foreach (Process q in Process.GetProcessesByName("piper"))
            {
                try
                {
                    string f = q.MainModule.FileName;
                    foreach (string r in roots) if (f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) { q.Kill(); break; }
                }
                catch (Exception) { }
            }
        }

        // the program only hides in the notification area when its window is closed: it is asked once and then ended with the .NET calls (no helper program).
        // Every step goes to the setup log (%TEMP%\HPGMissionCompanionSetup.log). A copy that cannot be ended (it runs with administrator rights and this
        // setup does not) makes the setup start again with those rights; if it still cannot be ended the install stops with the reason, it never goes on
        // over a running program.
        public static void CloseRunning(string dir, Action<string> log, bool elevated)
        {
            List<Process> copies = RunningCopies();
            if (copies.Count == 0) return;
            List<string> dirs = new List<string>();
            dirs.Add(dir);
            foreach (Process p in copies)
            {
                string where = PathOf(p);
                log("Closing the running program (PID " + p.Id + ", " + where + ")");
                try { string d = Path.GetDirectoryName(where); if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d); } catch (Exception) { }
                try { p.CloseMainWindow(); } catch (Exception) { }
            }
            Thread.Sleep(800);
            foreach (Process p in copies)
            {
                try
                {
                    if (p.HasExited) continue;
                    KillPiper(dirs);
                    p.Kill();
                    p.WaitForExit(6000);
                }
                catch (Exception ex) { log("The program (PID " + p.Id + ") could not be ended: " + ex.Message); }
            }
            KillPiper(dirs);
            List<Process> left = RunningCopies();
            if (left.Count == 0) { log("The running program was closed"); return; }
            string detail = "";
            foreach (Process p in left) detail += (detail.Length > 0 ? "; " : "") + "PID " + p.Id + " (" + PathOf(p) + ")";
            if (!elevated && !IsElevated()) throw new NeedElevationException("The running program cannot be closed with these rights: " + detail);
            throw new InvalidOperationException("The running program cannot be closed: " + detail + ". Close it from its icon in the notification area (Exit) or from the Task Manager and run the setup again.");
        }

        static void MakeShortcut(string lnk, string target, string arguments, string workDir, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("WScript.Shell is not available");
            object sh = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { arguments });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        static bool FilesEqual(string a, string b)
        {
            byte[] x = File.ReadAllBytes(a);
            byte[] y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        static long DirSize(string dir)
        {
            long n = 0;
            try { foreach (string f in Directory.GetFiles(dir)) n += new FileInfo(f).Length; }
            catch (Exception) { }
            return n;
        }

        // starts this setup again with administrator rights (the user answers the Windows question) and returns its exit code
        public static int RunElevated(Options o, string dir)
        {
            StringBuilder a = new StringBuilder("/S /elevated /showerrors /dir=\"" + dir.TrimEnd('\\') + "\"");
            if (!o.Desktop) a.Append(" /nodesktop");
            if (!o.StartMenu) a.Append(" /nostartmenu");
            if (!o.Launch) a.Append(" /nolaunch");
            if (o.OneClickSet) a.Append(o.OneClick ? " /oneclick" : " /nooneclick");
            ProcessStartInfo psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, a.ToString());
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Info.Log("starting again with administrator rights: " + a);
            using (Process p = Process.Start(psi))
            {
                if (p == null) return 1;
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static void Install(Options o, Action<string> log)
        {
            string dir = Path.GetFullPath(o.Dir);
            string err = ValidateDir(dir);
            if (err != null) throw new InvalidOperationException(err);
            Info.Log("install to " + dir);
            CleanTempUninstallers();

            if (o.WaitPid > 0)
            {
                try { using (Process old = Process.GetProcessById(o.WaitPid)) { log("Waiting for the program that started the setup to end"); old.WaitForExit(10000); } }
                catch (Exception) { }
            }
            CloseRunning(dir, log, o.Elevated);
            Directory.CreateDirectory(dir);

            log("Copying the program");
            Extract("app.exe", Path.Combine(dir, Info.ExeName));
            if (HasResource("SimConnect.dll"))
            {
                log("Copying SimConnect.dll");
                Extract("SimConnect.dll", Path.Combine(dir, "SimConnect.dll"));
            }
            else log("SimConnect.dll is not part of this installer: copy it from the MSFS SDK next to the program to use L:AUDIOINHIBIT");

            // the voice packs go through the program itself (an older version of the same pack is archived in voicepacks\obsolete)
            string tmp = Path.Combine(Path.GetTempPath(), "hmc-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                foreach (string res in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                {
                    if (!res.StartsWith("pack.", StringComparison.Ordinal)) continue;
                    string file = Path.Combine(tmp, res.Substring(5));
                    Extract(res, file);
                    string installed = Path.Combine(Path.Combine(dir, "voicepacks"), res.Substring(5));
                    if (File.Exists(installed) && FilesEqual(installed, file))
                    {
                        log("Voice pack " + res.Substring(5) + ": already up to date");
                        continue;
                    }
                    string report = file + ".report.txt";
                    ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(dir, Info.ExeName), "--install-pack \"" + file + "\" --report \"" + report + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = dir;
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null && !p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } }
                    }
                    string msg = File.Exists(report) ? File.ReadAllText(report).Trim() : "no report";
                    log("Voice pack " + res.Substring(5) + ": " + msg);
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }

            string uninstaller = Path.Combine(dir, Info.UninstallerName);
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            string exe = Path.Combine(dir, Info.ExeName);
            if (o.StartMenu)
            {
                try
                {
                    Directory.CreateDirectory(Info.StartMenuDir());
                    MakeShortcut(Path.Combine(Info.StartMenuDir(), "HPG Mission Companion.lnk"), exe, "", dir, Info.Name);
                    MakeShortcut(Path.Combine(Info.StartMenuDir(), "Uninstall HPG Mission Companion.lnk"), uninstaller, "/uninstall", dir, "Uninstall " + Info.Name);
                    log("Start menu shortcuts created");
                }
                catch (Exception ex) { log("Start menu shortcut failed: " + ex.Message); }
            }
            if (o.Desktop)
            {
                try { MakeShortcut(Info.DesktopLink(), exe, "", dir, Info.Name); log("Desktop shortcut created"); }
                catch (Exception ex) { log("Desktop shortcut failed: " + ex.Message); }
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Info.UninstallKey))
            {
                k.SetValue("DisplayName", Info.Name);
                k.SetValue("DisplayVersion", Info.Version);
                k.SetValue("Publisher", Info.Author);
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /S");
                k.SetValue("URLInfoAbout", Info.Home);
                k.SetValue("HelpLink", Info.Discord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
            }
            log("Installed version " + Info.Version + " in " + dir);
            if (o.OneClick) OneClick(dir, log);
        }

        static int ShowNewLines(string report, int seen, Action<string> log)
        {
            try
            {
                if (!File.Exists(report)) return seen;
                List<string> lines = new List<string>();
                using (FileStream fs = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null) lines.Add(line);
                }
                for (int i = seen; i < lines.Count; i++) log(lines[i]);
                return Math.Max(seen, lines.Count);
            }
            catch (Exception) { return seen; }
        }

        // the recommended profile and what it needs (Piper engine, voices): the program does it with --oneclick, this shows its report
        public static void OneClick(string dir, Action<string> log)
        {
            string exe = Path.Combine(dir, Info.ExeName);
            string report = Path.Combine(Path.GetTempPath(), "hmc-oneclick-" + Guid.NewGuid().ToString("N") + ".txt");
            log("Complete setup: the recommended settings, the Piper engine and voices (needs internet, can take several minutes)");
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--oneclick \"" + report + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = dir;
            int seen = 0;
            DateTime limit = DateTime.UtcNow.AddMinutes(90);
            using (Process p = Process.Start(psi))
            {
                if (p == null) { log("The program could not be started for the complete setup"); return; }
                while (!p.WaitForExit(400))
                {
                    seen = ShowNewLines(report, seen, log);
                    Application.DoEvents();
                    if (DateTime.UtcNow > limit) { try { p.Kill(); } catch (Exception) { } log("The complete setup took too long and was stopped"); break; }
                }
                seen = ShowNewLines(report, seen, log);
                if (p.HasExited)
                    log(p.ExitCode == 0 ? "Complete setup done" : p.ExitCode == 2 ? "Complete setup done, but some downloads failed: press Set up in one click in the program (Service tab) when the connection works" : "The complete setup failed: press Set up in one click in the program (Service tab) to try again");
            }
            try { File.Delete(report); } catch (Exception) { }
        }

        static void TryDeleteFile(string path, Action<string> log)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { log("Could not delete " + path + ": " + ex.Message); }
        }

        public static void Uninstall(string dir, bool removeData, Action<string> log)
        {
            dir = Path.GetFullPath(dir);
            Info.Log("uninstall from " + dir + " removeData=" + removeData);
            string exe = Path.Combine(dir, Info.ExeName);
            try { CloseRunning(dir, log, true); }
            catch (Exception ex) { log("Could not close the running program: " + ex.Message); }

            // the program removes itself from the EXE.xml of the simulators (its own code edits those files)
            if (File.Exists(exe))
            {
                try
                {
                    string report = Path.Combine(Path.GetTempPath(), "hmc-autostart-off.txt");
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "--autostart off --report \"" + report + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = dir;
                    using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(30000); }
                    if (File.Exists(report))
                    {
                        string txt = File.ReadAllText(report).Trim();
                        if (txt.Length > 0) log("Simulator start entry: " + txt.Replace(Environment.NewLine, "; "));
                        File.Delete(report);
                    }
                }
                catch (Exception ex) { log("Simulator start entry not changed: " + ex.Message); }
            }

            TryDeleteFile(exe, log);
            TryDeleteFile(Path.Combine(dir, "SimConnect.dll"), log);
            TryDeleteFile(Path.Combine(dir, "autostart-report.txt"), log);
            TryDeleteFile(Path.Combine(dir, "install-pack-report.txt"), log);

            try { if (Directory.Exists(Info.StartMenuDir())) Directory.Delete(Info.StartMenuDir(), true); }
            catch (Exception ex) { log("Start menu folder: " + ex.Message); }
            TryDeleteFile(Info.DesktopLink(), log);
            try { Registry.CurrentUser.DeleteSubKeyTree(Info.UninstallKey, false); }
            catch (Exception ex) { log("Registry: " + ex.Message); }

            if (removeData)
            {
                log("Removing the user data");
                TryDeleteFile(Path.Combine(dir, "HPGMissionCompanion.json"), log);
                foreach (string name in new string[] { "piper", "voices", "audio", "backups", "voicepacks", "profiles" })
                {
                    try { string d = Path.Combine(dir, name); if (Directory.Exists(d)) Directory.Delete(d, true); }
                    catch (Exception ex) { log("Could not delete " + name + ": " + ex.Message); }
                }
                try { foreach (string f in Directory.GetFiles(dir, "*.log")) TryDeleteFile(f, log); } catch (Exception) { }
            }

            // the uninstaller runs from a copy in the temp folder (see Program.Main), so the one in the folder is deleted like any other file;
            // the folder goes only when it is empty (a file of the user in it stays)
            TryDeleteFile(Path.Combine(dir, Info.UninstallerName), log);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); }
            catch (Exception ex) { log("The folder is not empty or in use, it stays: " + ex.Message); }
            log("Uninstalled");
        }

        // a copy of this executable in the temp folder: it ends the uninstall after the one in the install folder has ended (no shell, no timer)
        public static bool StartUninstallFromTemp(string[] args, string dir)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string copy = Path.Combine(Path.GetTempPath(), "HPGMissionCompanionUninstall-" + Process.GetCurrentProcess().Id + ".exe");
            File.Copy(self, copy, true);
            StringBuilder a = new StringBuilder("/fromtemp /waitpid=" + Process.GetCurrentProcess().Id + " /dir=\"" + dir.TrimEnd('\\') + "\"");
            foreach (string x in args) { string l = x.Trim().ToLowerInvariant(); if (l == "/s" || l == "/silent" || l == "-s" || l == "--silent" || l == "/uninstall" || l == "--uninstall" || l == "/removedata") a.Append(' ').Append(x.Trim()); }
            ProcessStartInfo psi = new ProcessStartInfo(copy, a.ToString());
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
            return true;
        }

        // the copies of earlier uninstalls (the one that runs is locked and stays)
        public static void CleanTempUninstallers()
        {
            try { foreach (string f in Directory.GetFiles(Path.GetTempPath(), "HPGMissionCompanionUninstall-*.exe")) { try { File.Delete(f); } catch (Exception) { } } }
            catch (Exception) { }
        }
    }

    class SetupForm : Form
    {
        TextBox dirBox = new TextBox();
        CheckBox desktop = new CheckBox();
        CheckBox startMenu = new CheckBox();
        CheckBox launch = new CheckBox();
        CheckBox oneClick = new CheckBox();
        Label oneClickNote = new Label();
        TextBox logBox = new TextBox();
        Button install = new Button();
        Button close = new Button();
        Options opt;
        bool done;

        public SetupForm(Options o)
        {
            opt = o;
            Text = Info.Name + " " + Info.Version + " Setup";
            ClientSize = new Size(560, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch (Exception) { }

            Label title = new Label();
            title.Text = Info.Name + " " + Info.Version;
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            title.SetBounds(16, 12, 528, 32);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Voice service and tools for the HPG H145 missions. Author: " + Info.Author + ". Installs for the current user; no administrator rights needed. Settings, Piper and the voices of an earlier install are kept.";
            sub.SetBounds(16, 48, 528, 48);
            Controls.Add(sub);

            Label lf = new Label();
            lf.Text = "Install folder";
            lf.SetBounds(16, 104, 200, 18);
            Controls.Add(lf);
            dirBox.Text = o.Dir ?? Info.DefaultDir();
            dirBox.SetBounds(16, 124, 440, 24);
            Controls.Add(dirBox);
            Button browse = new Button();
            browse.Text = "Browse...";
            browse.SetBounds(464, 122, 80, 27);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "Choose the install folder";
                    if (d.ShowDialog(this) == DialogResult.OK) { dirBox.Text = d.SelectedPath; ShowOneClickDefault(opt); }
                }
            };
            Controls.Add(browse);

            startMenu.Text = "Start menu shortcut";
            startMenu.Checked = o.StartMenu;
            startMenu.SetBounds(16, 160, 260, 22);
            Controls.Add(startMenu);
            desktop.Text = "Desktop shortcut";
            desktop.Checked = o.Desktop;
            desktop.SetBounds(16, 184, 260, 22);
            Controls.Add(desktop);
            launch.Text = "Start the program when the install ends";
            launch.Checked = o.Launch;
            launch.SetBounds(16, 208, 360, 22);
            Controls.Add(launch);
            oneClick.Text = "Complete setup: the recommended voices and settings, with the Piper engine and voices they need (internet, about 500 MB)";
            oneClick.SetBounds(16, 234, 528, 36);
            Controls.Add(oneClick);
            oneClickNote.ForeColor = SystemColors.GrayText;
            oneClickNote.SetBounds(34, 270, 510, 18);
            Controls.Add(oneClickNote);
            ShowOneClickDefault(o);

            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.SetBounds(16, 294, 528, 56);
            Controls.Add(logBox);

            install.Text = "Install";
            install.SetBounds(372, 360, 84, 28);
            install.Click += OnInstall;
            Controls.Add(install);
            close.Text = "Cancel";
            close.SetBounds(464, 360, 80, 28);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            AcceptButton = install;
        }

        // ticked when the folder has no settings yet (a new install); with settings the choice is left to the user and says what it replaces
        void ShowOneClickDefault(Options o)
        {
            bool has = File.Exists(Path.Combine((dirBox.Text ?? "").Trim().Trim('"'), "HPGMissionCompanion.json"));
            oneClick.Checked = o.OneClickSet ? o.OneClick : !has;
            oneClickNote.Text = has ? "This folder has settings: the complete setup replaces the voices and levels (a copy is kept in backups\\settings)." : "";
        }

        void AddLog(string line)
        {
            logBox.AppendText(line + Environment.NewLine);
            Info.Log(line);
            Application.DoEvents();
        }

        void OnInstall(object sender, EventArgs e)
        {
            if (done) { Close(); return; }
            opt.Dir = dirBox.Text.Trim().Trim('"');
            opt.StartMenu = startMenu.Checked;
            opt.Desktop = desktop.Checked;
            opt.Launch = launch.Checked;
            opt.OneClick = oneClick.Checked;
            string err = Ops.ValidateDir(opt.Dir);
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string exe = Path.Combine(opt.Dir, Info.ExeName);
            if (Directory.Exists(opt.Dir) && !File.Exists(exe) && Directory.GetFileSystemEntries(opt.Dir).Length > 0)
            {
                if (MessageBox.Show(this, "The folder is not empty and does not hold the program. Install here anyway? Files of the program are overwritten; nothing else is removed.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            if (Ops.IsRunning(opt.Dir))
            {
                if (MessageBox.Show(this, "The program is running. It will be closed to update it.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            }
            install.Enabled = false;
            close.Enabled = false;
            dirBox.Enabled = false;
            oneClick.Enabled = false;
            try
            {
                Ops.Install(opt, AddLog);
                done = true;
                AddLog("Done.");
                install.Text = "Finish";
                install.Enabled = true;
                close.Visible = false;
                if (opt.Launch)
                {
                    // through the shell: the program inherits no handle of the setup (the setup got the port of the old program from the program that started it)
                    try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = opt.Dir, UseShellExecute = true }); }
                    catch (Exception ex) { AddLog("Could not start the program: " + ex.Message); }
                }
            }
            catch (NeedElevationException ex)
            {
                AddLog(ex.Message);
                AddLog("Starting the setup again with administrator rights...");
                int rc = 1;
                try { rc = Ops.RunElevated(opt, opt.Dir); }
                catch (Exception ex2) { AddLog("The setup could not be started with administrator rights: " + ex2.Message); }
                if (rc == 0) { done = true; install.Text = "Finish"; install.Enabled = true; close.Visible = false; AddLog("Done (by the setup with administrator rights)."); }
                else { AddLog("The install failed, see the log " + Path.Combine(Path.GetTempPath(), "HPGMissionCompanionSetup.log")); install.Enabled = true; close.Enabled = true; dirBox.Enabled = true; oneClick.Enabled = true; }
            }
            catch (Exception ex)
            {
                AddLog("ERROR: " + ex.Message);
                MessageBox.Show(this, "The install failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = true;
                close.Enabled = true;
                dirBox.Enabled = true;
                oneClick.Enabled = true;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Options o = Options.Parse(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (o.Uninstall)
                {
                    string dir = o.Dir ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string selfDir = Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location));
                    if (!o.FromTemp && string.Equals(selfDir, Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        // an executable cannot delete itself: a copy in the temp folder does the uninstall once this one has ended
                        try { Ops.StartUninstallFromTemp(args, dir); return 0; }
                        catch (Exception ex) { Info.Log("the uninstaller could not start from the temp folder, it runs here: " + ex.Message); }
                    }
                    if (o.WaitPid > 0)
                    {
                        try { using (Process old = Process.GetProcessById(o.WaitPid)) old.WaitForExit(20000); }
                        catch (Exception) { }
                    }
                    if (!o.Silent)
                    {
                        if (MessageBox.Show("Remove " + Info.Name + " from " + dir + "?", Info.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                        bool dataThere = File.Exists(Path.Combine(dir, "HPGMissionCompanion.json")) || Directory.Exists(Path.Combine(dir, "voices")) || Directory.Exists(Path.Combine(dir, "piper"));
                        if (dataThere)
                            o.RemoveData = MessageBox.Show("Also delete the settings, the Piper engine, the voices, the audio folder, the backups and the voice packs in this folder?\r\n\r\nChoose No to keep them.", Info.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    }
                    List<string> lines = new List<string>();
                    Ops.Uninstall(dir, o.RemoveData, delegate (string s) { lines.Add(s); Info.Log(s); });
                    if (!o.Silent) MessageBox.Show(Info.Name + " was removed.", Info.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (o.Silent)
                {
                    if (o.Dir == null) o.Dir = Info.DefaultDir();
                    try { Ops.Install(o, delegate (string s) { Info.Log(s); }); }
                    catch (NeedElevationException ex)
                    {
                        Info.Log(ex.Message);
                        return Ops.RunElevated(o, o.Dir);
                    }
                    if (o.Launch)
                    {
                        // through the shell: the program inherits no handle of the setup (see the install window)
                        try { Process.Start(new ProcessStartInfo(Path.Combine(o.Dir, Info.ExeName)) { WorkingDirectory = o.Dir, UseShellExecute = true }); }
                        catch (Exception) { }
                    }
                    return 0;
                }
                if (o.Screenshot != null)
                {
                    using (SetupForm f = new SetupForm(o))
                    {
                        f.Show();
                        Application.DoEvents();
                        using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(o.Screenshot, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    return 0;
                }
                Application.Run(new SetupForm(o));
                return 0;
            }
            catch (Exception ex)
            {
                Info.Log("ERROR: " + ex);
                if (!o.Silent || o.ShowErrors) MessageBox.Show(ex.Message + "\r\n\r\nThe log of the setup: " + Path.Combine(Path.GetTempPath(), "HPGMissionCompanionSetup.log"), Info.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
