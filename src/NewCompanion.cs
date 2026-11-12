using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HPGMissionCompanion
{
    // This program is discontinued (the user's decision of 2026-11-12): the successor is the Random & Everywhere Companion (an experimental build by the
    // R&E team, repository KeKKERUUU/RandomEverywhereInstaller). This file reads the newest release of the successor, downloads its setup (sha256 checked)
    // and runs the two choices of the notice: uninstall this program, or keep it. Nothing happens without a click; nothing is downloaded before the click.
    internal sealed class NewRelease
    {
        public string Tag;
        public string AssetUrl;
        public long AssetSize;
        public string Sha256;
    }

    internal static class NewCompanion
    {
        // the index of the newest test build of the experimental Companion (path, sha256 and size of its files) and the build known when this was written
        public const string BuildHost = "https://pub-a388c1ca8fc14efd972f00e5d4b1ebe4.r2.dev/";
        public const string BuildIndex = BuildHost + "test/latest.json";
        public const string KnownSetupPath = "test/builds/5a3ac93e2a/RandomEverywhereCompanionSetup.exe";
        public const string KnownSetupSha256 = "d462e0ff6acacd9c889a09865500d6dcb20a498779e5fbb2c1a327d7c0f9147b";
        public const long KnownSetupSize = 60523857;
        public const string ReleasePage = "https://github.com/KeKKERUUU/RandomEverywhereInstaller/releases/latest";
        public const string SetupAsset = "RandomEverywhereCompanionSetup.exe";
        public const string ExeName = "RandomAndEverywhereCompanion.exe";
        // the discussion of the experimental Companion on the R&E Discord
        public const string DiscordThread = "https://discord.com/channels/1285277168103981197/1562848679142887484";
        // Inno Setup uninstall key of the successor (AppId of tools/setup/Companion.iss in its repository)
        const string InstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B86291CF-DB21-4A36-B093-889D2E4761C5}_is1";

        public static string Message
        {
            get
            {
                return AppInfo.Name + " is discontinued: it will not be updated any more.\r\n\r\n" +
                    "Its successor is the new Random & Everywhere Companion (experimental). The updates of the mission and of the voice packs do not come through " +
                    "this program any more.\r\n\r\n" +
                    "What do you want to do?";
            }
        }

        // true when this folder was installed by the setup (its uninstaller is there); a copy of the exe run from anywhere else cannot be uninstalled
        public static bool CanUninstallThis { get { return File.Exists(Path.Combine(Ctx.BaseDir, "Uninstall.exe")); } }

        public static string FindInstalledExe()
        {
            List<string> folders = new List<string>();
            foreach (RegistryHive hive in new RegistryHive[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey b = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey k = b.OpenSubKey(InstallKey))
                        {
                            if (k == null) continue;
                            string loc = k.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc)) folders.Add(loc);
                        }
                    }
                    catch (Exception) { }
                }
            }
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Random & Everywhere Companion"));
            foreach (string f in folders)
            {
                try { string exe = Path.Combine(f.Trim().Trim('"'), ExeName); if (File.Exists(exe)) return exe; }
                catch (Exception) { }
            }
            return null;
        }

        // the newest build from the index; when the index cannot be read, the build known at the time of this version (same host)
        public static NewRelease ReadLatest()
        {
            NewRelease known = new NewRelease();
            known.Tag = "build 5a3ac93e2a";
            known.AssetUrl = BuildHost + KnownSetupPath;
            known.AssetSize = KnownSetupSize;
            known.Sha256 = KnownSetupSha256;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(Net.GetString(BuildIndex));
                object assets, id;
                if (!d.TryGetValue("assets", out assets) || !(assets is System.Collections.IEnumerable)) return known;
                foreach (object o in (System.Collections.IEnumerable)assets)
                {
                    Dictionary<string, object> e = o as Dictionary<string, object>;
                    if (e == null) continue;
                    object name, path, size, sha;
                    if (!e.TryGetValue("name", out name) || !string.Equals(name as string, SetupAsset, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.TryGetValue("path", out path) || path == null || !e.TryGetValue("sha256", out sha) || sha == null) return known;
                    NewRelease r = new NewRelease();
                    r.AssetUrl = BuildHost + path.ToString().TrimStart('/');
                    r.Sha256 = sha.ToString().ToLowerInvariant();
                    r.AssetSize = e.TryGetValue("size", out size) && size != null ? Convert.ToInt64(size) : 0;
                    r.Tag = d.TryGetValue("buildId", out id) && id != null ? "build " + id : "latest";
                    return r;
                }
            }
            catch (Exception) { }
            return known;
        }

        // downloads the setup of the successor to the temp folder and checks size and sha256 (a failed check deletes the file)
        public static string DownloadSetup(NewRelease r, Action<long, long> progress)
        {
            string dir = Path.Combine(Path.GetTempPath(), "hmc-new-companion");
            string dest = Path.Combine(dir, SetupAsset);
            Net.Download(r.AssetUrl, dest, progress, null, r.AssetSize);
            FileInfo fi = new FileInfo(dest);
            if (r.AssetSize > 0 && fi.Length != r.AssetSize) { File.Delete(dest); throw new IOException("the download is incomplete (" + fi.Length + " of " + r.AssetSize + " bytes), try again"); }
            if (!string.IsNullOrEmpty(r.Sha256))
            {
                string got;
                using (FileStream s = File.OpenRead(dest)) got = BitConverter.ToString(SHA256.Create().ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                if (got != r.Sha256) { File.Delete(dest); throw new IOException("the setup is corrupted (sha256 differs), try again"); }
            }
            return dest;
        }

        public static void OpenDiscord() { OpenUrl(DiscordThread); }
        public static void OpenReleasePage() { OpenUrl(ReleasePage); }
        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("The link could not be opened: " + ex.Message + "\n\n" + url, AppInfo.Name); }
        }

        // runs the setup of the successor (it asks for the administrator right itself, the wizard is shown) and waits for it; null = done, else the reason
        public static string RunSetup(string setupFile)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(setupFile);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(setupFile);
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "the setup did not start";
                    p.WaitForExit();
                    if (p.ExitCode != 0) return "the setup was not completed (exit code " + p.ExitCode + ")";
                }
                return null;
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? "the administrator right was refused" : "the setup could not start: " + ex.Message;
            }
        }

        public static void StartNew(string exe)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi);
        }

        // this program leaves the EXE.xml of the simulators, so the simulator does not start it next to the successor (the same call as the Service tab checkbox)
        public static string AutostartOff()
        {
            string done = "";
            try
            {
                foreach (SimTarget t in SimAutostart.Detect(null))
                {
                    try { SimAutostart.Disable(t); done += (done.Length > 0 ? "; " : "") + t.ExeXml; }
                    catch (Exception ex) { done += (done.Length > 0 ? "; " : "") + "ERROR " + t.ExeXml + ": " + ex.Message; }
                }
            }
            catch (Exception ex) { done = "ERROR " + ex.Message; }
            Ctx.Settings.StartWithSim = false;
            try { Ctx.Settings.Save(Ctx.SettingsPath); } catch (Exception) { }
            return done;
        }

        // silent uninstall of this program: the settings, voices and packs stay in the folder (no /removedata); the uninstaller closes this program
        public static void StartUninstallThis()
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Ctx.BaseDir, "Uninstall.exe"), "/uninstall /S");
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Ctx.BaseDir;
            Process.Start(psi);
        }
    }

    // The notice. ShowDialog returns OK when a choice was carried out (Chosen says which) and Cancel for "Not now".
    internal sealed class NewCompanionForm : Form
    {
        public bool UninstallThis;        // the choice that was carried out: true = uninstall this program, false = keep it
        public string NewExe;             // the installed successor, when a choice was carried out
        readonly Button uninstallButton = new Button();
        readonly Button keepButton = new Button();
        readonly Button discordButton = new Button();
        readonly Button laterButton = new Button();
        readonly Label status = new Label();
        readonly ProgressBar bar = new ProgressBar();
        bool busy;

        public NewCompanionForm()
        {
            Text = AppInfo.Name + " - discontinued";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(560, 330);

            Label msg = new Label();
            msg.Text = NewCompanion.Message;
            msg.UseMnemonic = false;      // the & of the name Random & Everywhere is a character, not a shortcut
            msg.Location = new Point(16, 14);
            msg.Size = new Size(528, 112);
            Controls.Add(msg);

            uninstallButton.Text = "Uninstall this program and install the new Companion";
            uninstallButton.Location = new Point(16, 132);
            uninstallButton.Size = new Size(528, 34);
            uninstallButton.Enabled = NewCompanion.CanUninstallThis;
            uninstallButton.Click += delegate { Run(true); };
            Controls.Add(uninstallButton);

            keepButton.Text = "Keep this program and start the new Companion";
            keepButton.Location = new Point(16, 172);
            keepButton.Size = new Size(528, 34);
            keepButton.Click += delegate { Run(false); };
            Controls.Add(keepButton);

            Label note = new Label();
            note.ForeColor = SystemColors.GrayText;
            note.UseMnemonic = false;
            note.Location = new Point(16, 212);
            note.Size = new Size(528, 50);
            note.Text = (NewCompanion.CanUninstallThis
                ? "Uninstalling keeps your settings, voices and packs in this folder. "
                : "This copy was not installed by the setup, so it cannot be uninstalled from here: delete its folder yourself if you want it gone. ") +
                "In both cases the new Companion is installed first (if it is not already), this program closes and no longer starts with the simulator.";
            Controls.Add(note);

            status.UseMnemonic = false;
            status.Location = new Point(16, 266);
            status.Size = new Size(528, 20);
            Controls.Add(status);
            bar.Location = new Point(16, 288);
            bar.Size = new Size(300, 14);
            bar.Visible = false;
            Controls.Add(bar);

            discordButton.Text = "Open the Discord discussion";
            discordButton.Location = new Point(16, 290);
            discordButton.Size = new Size(200, 30);
            discordButton.Click += delegate { NewCompanion.OpenDiscord(); };
            Controls.Add(discordButton);

            laterButton.Text = "Not now";
            laterButton.Location = new Point(444, 290);
            laterButton.Size = new Size(100, 30);
            laterButton.DialogResult = DialogResult.Cancel;
            Controls.Add(laterButton);
            CancelButton = laterButton;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) e.Cancel = true;      // the download or the setup is running
            base.OnFormClosing(e);
        }

        void SetStatus(string text) { status.Text = text; }

        void Run(bool uninstallThis)
        {
            if (busy) return;
            busy = true;
            uninstallButton.Enabled = false;
            keepButton.Enabled = false;
            laterButton.Enabled = false;
            discordButton.Visible = false;
            bar.Visible = false;
            SetStatus("Looking for the new Companion...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                string exe = null;
                try
                {
                    exe = NewCompanion.FindInstalledExe();
                    if (exe == null)
                    {
                        Post(delegate { SetStatus("Reading the latest release..."); });
                        NewRelease r = NewCompanion.ReadLatest();
                        if (r.AssetUrl == null) throw new InvalidOperationException("the latest release has no " + NewCompanion.SetupAsset);
                        long shown = -1;
                        string setup = NewCompanion.DownloadSetup(r, delegate (long done, long total)
                        {
                            long pct = total > 0 ? done * 100 / total : 0;
                            if (pct == shown) return;
                            shown = pct;
                            Post(delegate { bar.Visible = true; bar.Value = (int)Math.Min(100, pct); SetStatus("Downloading the new Companion " + r.Tag + ": " + pct + " %"); });
                        });
                        Post(delegate { bar.Visible = false; SetStatus("The setup of the new Companion is open: complete it (this window waits)."); });
                        error = NewCompanion.RunSetup(setup);
                        if (error == null)
                        {
                            exe = NewCompanion.FindInstalledExe();
                            if (exe == null) error = "the setup finished but the new Companion was not found";
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    busy = false;
                    if (error != null)
                    {
                        SetStatus("Nothing was changed: " + error + ".");
                        uninstallButton.Enabled = NewCompanion.CanUninstallThis;
                        keepButton.Enabled = true;
                        laterButton.Enabled = true;
                        discordButton.Visible = true;
                        if (MessageBox.Show(this, "The new Companion could not be installed from here (" + error + ").\n\nOpen the download page of the new Companion in the browser?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            NewCompanion.OpenReleasePage();
                        return;
                    }
                    UninstallThis = uninstallThis;
                    NewExe = exe;
                    DialogResult = DialogResult.OK;
                });
            });
        }

        void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (Exception) { }
        }
    }
}
