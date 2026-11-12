// HPG Mission Companion - main window: service, actors and voices, Piper, radio effect, tools, about.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HPGMissionCompanion
{
    internal sealed class CleanDialog : Form
    {
        readonly CheckedListBox list = new CheckedListBox();
        readonly Label note = new Label();
        readonly CheckBox backup = new CheckBox();
        readonly List<CleanGroup> groups;

        public CleanDialog(List<CleanGroup> groups)
        {
            this.groups = groups;
            Text = "Clean the mission work folder";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 372);

            Label info = new Label();
            info.Text = "Tick what to delete. Only the first group is ticked for you.";
            info.Location = new Point(12, 12);
            info.Size = new Size(536, 20);
            Controls.Add(info);

            list.Location = new Point(12, 36);
            list.Size = new Size(536, 148);
            list.CheckOnClick = true;
            foreach (CleanGroup g in groups) list.Items.Add(g, g.Default);
            list.SelectedIndexChanged += delegate { ShowNote(); };
            Controls.Add(list);

            note.Location = new Point(12, 192);
            note.Size = new Size(536, 84);
            note.ForeColor = SystemColors.GrayText;
            Controls.Add(note);

            backup.Text = "Back up the files to a zip first (folder backups next to the program)";
            backup.Checked = true;
            backup.AutoSize = true;
            backup.Location = new Point(12, 288);
            Controls.Add(backup);

            Button ok = new Button();
            ok.Text = "Delete";
            ok.Location = new Point(368, 328);
            ok.Size = new Size(85, 30);
            ok.Click += delegate { Confirm(); };
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(463, 328);
            cancel.Size = new Size(85, 30);
            Controls.Add(cancel);
            CancelButton = cancel;
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        void ShowNote()
        {
            CleanGroup g = list.SelectedItem as CleanGroup;
            note.Text = g != null ? g.Note : "";
        }

        public bool BackupFirst { get { return backup.Checked; } }

        public List<CleanGroup> Selected
        {
            get
            {
                List<CleanGroup> r = new List<CleanGroup>();
                foreach (object o in list.CheckedItems) r.Add((CleanGroup)o);
                return r;
            }
        }

        void Confirm()
        {
            List<CleanGroup> sel = Selected;
            int n = 0;
            foreach (CleanGroup g in sel) n += g.Files.Count;
            if (n == 0) { MessageBox.Show("Nothing is ticked.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool config = false, settings = false;
            foreach (CleanGroup g in sel) { if (g.Key == "config") config = true; if (g.Key == "settings") settings = true; }
            string warn = "Delete " + n + " file" + (n == 1 ? "" : "s") + "?"
                + (config ? "\n\nThis includes your saved games and profiles." : "")
                + (settings ? "\n\nThis includes global.json: every setting of every HPG mission goes back to its default." : "")
                + (backup.Checked ? "\n\nA zip backup is made first." : "\n\nNo backup will be made.");
            if (MessageBox.Show(warn, Text, MessageBoxButtons.YesNo, config || settings ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class MainForm : Form
    {
        readonly bool startHidden;
        readonly bool exitWithSimArg;
        readonly string simRoot;
        bool allowShow;
        readonly Icon appIcon;

        readonly TabControl tabs = new TabControl();
        readonly Button saveButton = new Button();
        readonly CheckBox listenAllBox = new CheckBox();
        readonly Label saveLabel = new Label();
        readonly ComboBox overlapBox = new ComboBox();     // Volume tab: voices at the same time
        bool ensureBusy, noVoiceAnnounced, actorsBlocked;
        int actorsMissingCount, actorsDefaultedCount;      // told to the mission: it shows the message on its pages (user request of 2026-10-19)
        DateTime nextActorCheck = DateTime.MaxValue, lastActorAlert = DateTime.MinValue;
        readonly CheckBox advancedCheck = new CheckBox();      // footer: show or hide the advanced groups of the tabs
        readonly ToolTip tips = new ToolTip();
        int tabHover = -1;
        bool suppress;
        bool dirty;

        // service tab
        readonly Label serviceLabel = new Label();
        readonly Button startStop = new Button();
        readonly TextBox audioBox = new TextBox();
        readonly ComboBox defaultLangBox = new ComboBox();
        readonly TrackBar masterBar = new TrackBar();
        readonly Label masterLabel = new Label();
        readonly CheckBox simCheck = new CheckBox();
        readonly CheckBox minimizedCheck = new CheckBox();
        readonly CheckBox exitCheck = new CheckBox();
        readonly Label simInfo = new Label();
        readonly CheckBox inhibitCheck = new CheckBox();
        readonly Label inhibitInfo = new Label();
        readonly TextBox testBox = new TextBox();
        readonly RichTextBox logBox = new RichTextBox();
        readonly Button exitButton = new Button();
        readonly GroupBox logGroup = new GroupBox();
        readonly GroupBox advBox = new GroupBox();
        readonly GroupBox profileGroup = new GroupBox();      // profile: setup in one click, save and load of the settings
        readonly Label footNote = new Label();
        readonly CheckBox autoIndexCheck = new CheckBox();
        readonly SynchronizationContext uiContext;
        bool indexRunning;
        List<SimTarget> sims = new List<SimTarget>();

        // actors tab
        readonly ComboBox langBox = new ComboBox();
        readonly Label[] actorLabel = new Label[Actors.Count];
        readonly ComboBox[] voiceBox = new ComboBox[Actors.Count];
        readonly TrackBar[] speedBar = new TrackBar[Actors.Count];
        readonly Label[] speedLabel = new Label[Actors.Count];
        readonly Button[] testButton = new Button[Actors.Count];
        readonly Button[] poolButton = new Button[Actors.Count];       // "Voices..." of the actors whose voice can be drawn (dispatcher, ground unit, heli rescuer)
        readonly TextBox[] nameBox = new TextBox[7];       // the names of the crew and the technicians (actors 1-7), used when the crew names are not random
        static readonly string[] NameRoles = { "PLT", "CPL", "PAX3", "PAX1", "PAX2", "TECHM", "TECHF" };      // role of actor 1..7

        // piper tab
        readonly Label engineLabel = new Label();
        readonly Button engineButton = new Button();
        readonly ListView catalogList = new ListView();
        static readonly string[] CatalogColumns = { "Voice", "Region", "Quality", "Size", "Voice type", "Status" };
        int catalogSortColumn;                  // click a column header of the Piper voices list to sort by it; click again to reverse
        bool catalogSortAscending = true;
        readonly Button installButton = new Button();
        readonly Button removeButton = new Button();
        readonly Button testVoiceButton = new Button();
        readonly Button refreshButton = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label progressLabel = new Label();
        Label piperHint;
        List<CatalogVoice> catalog = new List<CatalogVoice>();
        bool busy;
        bool catalogRequested;

        // voice packs tab
        readonly ListView packList = new ListView();
        readonly Label packInfo = new Label();
        readonly Panel dropZone = new Panel();
        readonly ComboBox bridgeBox = new ComboBox();
        readonly RadioButton varRandom = new RadioButton();
        readonly RadioButton varCoherent = new RadioButton();
        readonly RadioButton namesFunc = new RadioButton();
        readonly RadioButton namesRandom = new RadioButton();
        readonly Label bridgeInfo = new Label();
        Label bridgeHint;
        const string BridgeHintText = "the lines of the mission are spoken with the texts of this pack";

        // volume tab
        readonly CheckBox missionVolCheck = new CheckBox();
        readonly TrackBar attInBar = new TrackBar();
        readonly TrackBar attOutBar = new TrackBar();
        readonly Label attInLabel = new Label();
        readonly Label attOutLabel = new Label();
        readonly Label mixInfo = new Label();
        readonly GroupBox audioOutBox = new GroupBox();          // Volume tab, advanced: device and level of the headset, the external voices and the notifications

        // radio tab
        readonly TrackBar noiseBar = new TrackBar();
        readonly Label noiseLabel = new Label();
        readonly TrackBar radioNoiseBar = new TrackBar();
        readonly Label radioNoiseLabel = new Label();
        readonly TrackBar extRadioNoiseBar = new TrackBar();
        readonly Label extRadioNoiseLabel = new Label();
        readonly ComboBox beepBox = new ComboBox();
        readonly TrackBar beepBar = new TrackBar();
        readonly Label beepLabel = new Label();
        readonly CheckBox tailCheck = new CheckBox();
        readonly ComboBox qualityBox = new ComboBox();

        // tools tab
        readonly ComboBox workCombo = new ComboBox();
        readonly Label workInfo = new Label();
        readonly ComboBox indexCombo = new ComboBox();
        readonly Button indexButton = new Button();
        readonly TextBox toolsOut = new TextBox();
        readonly CheckBox sharedAutoCheck = new CheckBox();
        readonly CheckBox sharedDbCheck = new CheckBox();
        readonly Label sharedInfo = new Label();
        readonly Button sharedSyncButton = new Button();
        readonly Button sharedApplyButton = new Button();
        readonly Button sharedRestoreButton = new Button();
        bool sharedRunning;
        bool dbSimSeen;
        DateTime nextSharedSync = DateTime.MaxValue;     // set when the program has started: the first check, then every 30 minutes
        readonly ComboBox missionCombo = new ComboBox();
        readonly Button updateButton = new Button();
        readonly Label updateInfo = new Label();
        bool updateRunning;
        bool updateBalloon;
        readonly CheckBox autoUpdateCheck = new CheckBox();
        readonly Button whatsNewButton = new Button();
        readonly TextBox updatesOut = new TextBox();
        readonly RadioButton updBoth = new RadioButton();
        readonly RadioButton updMission = new RadioButton();
        readonly RadioButton updApp = new RadioButton();
        TabPage updatesPage;
        public static bool QuietPort;       // --screenshot: a busy port is only logged (the program of the user is probably running), no question
        readonly CheckBox updPackCheck = new CheckBox();
        readonly CheckBox readyCheck = new CheckBox();
        long logBytes;
        bool logCapped;
        bool updatesAvailable;
        bool updChecked, updMissionNew, updAppNew, updPackNew, updGood;            // what the last check found: it was made, a newer mission, a newer program, everything up to date and every source read
        string updateIdleText = "Program version " + AppInfo.Version;   // the status line when nothing is newer

        Server server;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        readonly string logFile;
        bool reallyExit;
        bool simSeen;
        DateTime simLastSeen = DateTime.UtcNow;
        DateTime nextSimCheck = DateTime.UtcNow;
        bool started;

        public MainForm(bool startHidden, bool exitWithSim, string logFile, string simRoot)
        {
            this.startHidden = startHidden;
            this.exitWithSimArg = exitWithSim;
            this.logFile = logFile ?? (QuietPort ? null : NewLogFile());       // one file for each start in the logs folder, unless --log-file names one
            this.simRoot = simRoot;
            allowShow = !startHidden;

            Icon ico = null;
            try { ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            appIcon = ico ?? SystemIcons.Application;

            Text = AppInfo.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 662);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = appIcon;

            uiContext = SynchronizationContext.Current;
            Ctx.Log = AppendLog;
            Ctx.ActorAlert = delegate (int actor, string lang)
            {
                // a main actor was spoken without its voice (speech thread): tell it and give it a default at once (at most one message every 20 s)
                if ((DateTime.UtcNow - lastActorAlert).TotalSeconds < 20) return;
                lastActorAlert = DateTime.UtcNow;
                Ui(delegate
                {
                    AppendLog("[WARN] The actor " + Actors.Names[actor - 1] + " (" + lang + ") was spoken without an assigned voice");
                    EnsureActors(false);
                });
            };
            if (this.logFile != null) AppendLog("[INFO] " + AppInfo.Title + " started" + (startHidden ? " minimized" : "") + ", " + Environment.OSVersion + ", log file " + this.logFile);

            tabs.Location = new Point(12, 12);
            tabs.Size = new Size(736, 594);
            ConfigureTabs();
            Controls.Add(tabs);
            advancedCheck.Checked = Ctx.Settings.ShowAdvanced;
            BuildServiceTab();
            BuildActorsTab();
            BuildPiperTab();
            BuildPacksTab();
            BuildRadioTab();
            BuildVolumeTab();
            BuildToolsTab();
            BuildUpdatesTab();
            BuildAboutTab();
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedIndex == 2) EnsureCatalog(); };

            saveButton.Text = "Save settings";
            saveButton.Location = new Point(12, 618);
            saveButton.Size = new Size(116, 32);
            saveButton.Click += delegate { SaveSettings(); };
            Controls.Add(saveButton);
            updateButton.Text = "Check for updates";               // opens the Updates tab; turns amber when something newer is published
            updateButton.Location = new Point(136, 618);
            updateButton.Size = new Size(150, 32);
            updateButton.Click += delegate { CheckUpdates(false); };
            Controls.Add(updateButton);
            saveLabel.AutoSize = true;
            saveLabel.Location = new Point(296, 626);
            Controls.Add(saveLabel);
            advancedCheck.Text = "Show advanced options";
            advancedCheck.AutoSize = true;
            advancedCheck.Location = new Point(478, 625);
            advancedCheck.CheckedChanged += delegate
            {
                Ctx.Settings.ShowAdvanced = advancedCheck.Checked;
                ApplyAdvanced();
                if (!suppress) MarkDirty();
            };
            Controls.Add(advancedCheck);
            exitButton.Text = "Exit program";
            exitButton.Location = new Point(628, 618);
            exitButton.Size = new Size(120, 32);
            exitButton.Click += delegate { ExitApp(true); };
            Controls.Add(exitButton);

            tray.Icon = appIcon;
            tray.Text = AppInfo.Name;
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Open", delegate { ShowWindow(); });
            menu.MenuItems.Add("Exit", delegate { ExitApp(true); });
            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.BalloonTipClicked += delegate
            {
                // the balloon of an automatic check that found something newer: ask the question (the check opens the Updates tab)
                if (!updateBalloon) return;
                updateBalloon = false;
                ShowWindow();
                CheckUpdates(false);
            };
            tray.Visible = true;

            tick.Interval = 300;
            tick.Tick += delegate { OnTick(); };
            tick.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(allowShow ? value : false);
        }

        // test helper: saves a picture of every tab (--screenshot <base path>), taken from the screen so that every control is drawn
        public void Snapshot(string basePath)
        {
            allowShow = true;
            Show();
            Activate();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(100); }
            AppendLog("[SERVER] Started on 0.0.0.0:5997 (sample line)");
            AppendLog("[ACCEPT] client 127.0.0.1:50000 (sample line)");
            AppendLog("[RAW] {\"Text\":\"<actor1><vol:40><radio>Pilot to dispatch.\"} (sample line)");
            AppendLog("[VOICE] actor 1, EN, ryan - high - male (Piper, en_US), speed 1.0, vol 40, radio: Pilot to dispatch. (sample line)");
            AppendLog("[RENDER] 120 ms, 118 KB (sample line)");
            AppendLog("[WARN] voice 'xyz' not installed, using the actor voice (sample line)");
            AppendLog("[ERROR] File does not exist: missing.wav (sample line)");
            AppendLog("[INFO] Start with the simulator ON (--minimized --exit-with-sim) (sample line)");
            AppendLog("[INTERRUPT] (sample line)");
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(100); }
            advancedCheck.Checked = false;
            for (int t = 0; t < tabs.TabPages.Count; t++)
            {
                tabs.SelectedIndex = t;
                for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
                SaveShot(basePath + "-" + t + ".png");
            }
            tabs.SelectedIndex = 0;
            advancedCheck.Checked = true;      // the Service tab again with the advanced options shown
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            SaveShot(basePath + "-0-advanced.png");
            for (int vt = 0; vt < tabs.TabPages.Count; vt++)
                if (tabs.TabPages[vt].Text == "Volume")       // the Volume tab with the advanced audio output group, and scrolled down to it
                {
                    tabs.SelectedIndex = vt;
                    for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
                    SaveShot(basePath + "-volume-advanced.png");
                    tabs.TabPages[vt].AutoScrollPosition = new Point(0, 300);
                    for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
                    SaveShot(basePath + "-volume-advanced-2.png");
                    tabs.TabPages[vt].AutoScrollPosition = new Point(0, 0);
                }
            tabs.SelectedIndex = 0;
            advancedCheck.Checked = false;
            updMissionNew = true; updAppNew = true;
            SetUpdateState("UPDATE AVAILABLE: mission 0.997 169.48 and program 0.13 (sample)", true);      // the Updates tab in amber, with the footer button
            SelectUpdatesTab();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            SaveShot(basePath + "-updates-alert.png");
            updMissionNew = false; updAppNew = false; updChecked = true; updGood = true;
            SetUpdateState("UP TO DATE: mission 0.997 169.48, program " + AppInfo.Version + " (sample)", false);      // everything up to date: green line, button Up to date
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            SaveShot(basePath + "-updates-ok.png");
            updChecked = false; updGood = false;
            SetUpdateState("Program version " + AppInfo.Version, false);
            MarkDirty();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            SaveShot(basePath + "-dirty.png");
            for (int i = 0; i < langBox.Items.Count; i++) if (((LangInfo)langBox.Items[i]).Code == "IT") langBox.SelectedIndex = i;      // the Piper voices tab in Italian: the community voices
            tabs.SelectedIndex = 2;
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            if (catalogList.Items.Count > 0) { catalogList.Items[0].Selected = true; catalogList.Items[0].EnsureVisible(); }
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(80); }
            SaveShot(basePath + "-piper-it.png");
            reallyExit = true;
            StopServer();
            PiperEngine.StopAll();
            tray.Visible = false;
        }

        void SaveShot(string path)
        {
            Rectangle r = Bounds;
            using (Bitmap bmp = new Bitmap(r.Width, r.Height))
            {
                try
                {
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                }
                catch (Exception)
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height));
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        void ShowWindow()
        {
            allowShow = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ---- tabs: drawn by the program so that they stand out (the system tabs are almost the colour of the page) ----
        static readonly Color TabSelectedBack = Color.FromArgb(0, 92, 140);
        static readonly Color TabIdleBack = Color.FromArgb(196, 212, 229);
        static readonly Color TabHoverBack = Color.FromArgb(222, 234, 247);
        static readonly Color TabBorder = Color.FromArgb(110, 135, 165);
        static readonly Color TabAlertBack = Color.FromArgb(255, 200, 90);
        static readonly Color TabAlertSelectedBack = Color.FromArgb(200, 100, 0);

        void ConfigureTabs()
        {
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(80, 34);
            tabs.Padding = new Point(6, 4);
            tabs.DrawItem += DrawTab;
            tabs.MouseMove += delegate (object sender, MouseEventArgs e)
            {
                int h = -1;
                for (int i = 0; i < tabs.TabCount; i++) if (tabs.GetTabRect(i).Contains(e.Location)) { h = i; break; }
                if (h != tabHover) { tabHover = h; tabs.Invalidate(); }
            };
            tabs.MouseLeave += delegate { if (tabHover != -1) { tabHover = -1; tabs.Invalidate(); } };
        }

        void DrawTab(object sender, DrawItemEventArgs e)
        {
            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle r = tabs.GetTabRect(e.Index);
            r.Width -= 2;
            bool alert = updatesAvailable && tabs.TabPages[e.Index] == updatesPage;         // something newer is published: the Updates tab is amber
            Color back = alert ? (selected ? TabAlertSelectedBack : TabAlertBack) : (selected ? TabSelectedBack : (e.Index == tabHover ? TabHoverBack : TabIdleBack));
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(TabBorder)) e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (Font f = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, f, r, selected ? Color.White : (alert ? Color.FromArgb(90, 40, 0) : Color.FromArgb(25, 35, 50)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        // ---- small helpers ----------------------------------------------------------------------
        static Label Lbl(Control page, string text, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            if (w > 0) l.Size = new Size(w, h); else l.AutoSize = true;
            page.Controls.Add(l);
            return l;
        }

        static Button Btn(Control page, string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            page.Controls.Add(b);
            return b;
        }

        // ---- startup, log, timer ----------------------------------------------------------------
        void OnTick()
        {
            if (!started)
            {
                started = true;
                Startup();
            }
            string line;
            bool any = false;
            while (logQueue.TryDequeue(out line))
            {
                AppendColored(line);
                any = true;
            }
            if (any)
            {
                if (logBox.TextLength > 60000)
                {
                    int cut = logBox.GetFirstCharIndexFromLine(logBox.Lines.Length / 2);
                    // a read-only RichTextBox refuses the replacement (the text stays above the limit) and rings the Windows error sound at every tick that logs something (tester reports 2026-10-20 and 2026-10-22)
                    if (cut > 0)
                    {
                        logBox.ReadOnly = false;
                        try { logBox.Select(0, cut); logBox.SelectedText = ""; }
                        finally { logBox.ReadOnly = true; }
                    }
                }
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }
            if (inhibitInfo.Text != SimLink.Status) inhibitInfo.Text = SimLink.Status;
            string mix = Mixer.Describe();
            if (mixInfo.Text != mix) mixInfo.Text = mix;
            string bridge = Say.BridgeStatus();
            if (bridgeInfo.Text != bridge) bridgeInfo.Text = bridge;
            if ((exitWithSimArg || Ctx.Settings.SharedDbPatch) && DateTime.UtcNow >= nextSimCheck)
            {
                nextSimCheck = DateTime.UtcNow.AddSeconds(5);
                if (Tools.SimulatorRunning()) { simSeen = true; dbSimSeen = true; simLastSeen = DateTime.UtcNow; }
                else if ((simSeen || dbSimSeen) && (DateTime.UtcNow - simLastSeen).TotalSeconds > 15)
                {
                    // the simulator is closed: now the offline databases of HPG can be changed (the option of the Tools tab)
                    if (dbSimSeen && Ctx.Settings.SharedDbPatch)
                    {
                        dbSimSeen = false;
                        SharedResult d = SharedData.ApplyDb(SharedData.FindDbFolders(), false);
                        SharedLog(d);
                    }
                    dbSimSeen = false;
                    if (exitWithSimArg && simSeen)
                    {
                        AppendLog("[INFO] The simulator was closed: leaving.");
                        ExitApp(false);
                    }
                    simSeen = false;
                }
            }
            if (started && Ctx.Settings.SharedAutoSync && DateTime.UtcNow >= nextSharedSync && !sharedRunning)
            {
                nextSharedSync = DateTime.UtcNow.AddMinutes(30);
                SharedSync(true);
            }
            if (started && DateTime.UtcNow >= nextActorCheck)
            {
                // voices installed or removed meanwhile: the service follows (it stops without voices, it starts again when they are back)
                nextActorCheck = DateTime.UtcNow.AddSeconds(30);
                bool wasBlocked = actorsBlocked;
                bool ok = EnsureActors(false);
                if (ok && wasBlocked && server == null) StartServer(true);
            }
            // the program is discontinued (2026-11-12): the published mission is no longer read, so the mission never announces a build that this program cannot install
        }

        // the log is dark green; each kind of message has its own colour, voices in yellow
        static Color LogColor(string line)
        {
            if (line.IndexOf("[ERROR]", StringComparison.Ordinal) >= 0) return Color.FromArgb(255, 110, 110);
            if (line.IndexOf("[WARN]", StringComparison.Ordinal) >= 0) return Color.FromArgb(255, 180, 70);
            if (line.IndexOf("[VOICE]", StringComparison.Ordinal) >= 0) return Color.FromArgb(255, 232, 110);
            if (line.IndexOf("[RAW]", StringComparison.Ordinal) >= 0) return Color.FromArgb(160, 210, 170);
            if (line.IndexOf("[SERVER]", StringComparison.Ordinal) >= 0 || line.IndexOf("[ACCEPT]", StringComparison.Ordinal) >= 0
                || line.IndexOf("[DISCONNECT]", StringComparison.Ordinal) >= 0) return Color.FromArgb(110, 225, 255);
            if (line.IndexOf("[INTERRUPT]", StringComparison.Ordinal) >= 0 || line.IndexOf("[PLAY]", StringComparison.Ordinal) >= 0
                || line.IndexOf("[RENDER]", StringComparison.Ordinal) >= 0 || line.IndexOf("[SILENT]", StringComparison.Ordinal) >= 0) return Color.FromArgb(175, 190, 255);
            if (line.IndexOf("[INFO]", StringComparison.Ordinal) >= 0) return Color.FromArgb(235, 255, 235);
            return Color.FromArgb(205, 240, 205);
        }

        void AppendColored(string line)
        {
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = LogColor(line);
            logBox.AppendText(line + "\n");
        }

        // a new log file for this start (logs\hmc-<date>-<time>.log next to the program); the ten newest are kept
        static string NewLogFile()
        {
            try
            {
                string dir = Path.Combine(Ctx.BaseDir, "logs");
                Directory.CreateDirectory(dir);
                string[] old = Directory.GetFiles(dir, "hmc-*.log");
                Array.Sort(old, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < old.Length - 9; i++) { try { File.Delete(old[i]); } catch (Exception) { } }
                return Path.Combine(dir, "hmc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            }
            catch (Exception) { return null; }
        }

        void AppendLog(string line)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            logQueue.Enqueue(stamped);
            if (logFile != null)
            {
                try
                {
                    lock (logQueue)
                    {
                        if (logCapped) return;
                        if (logBytes > 20L * 1024 * 1024) { logCapped = true; stamped += Environment.NewLine + "[INFO] the log reached 20 MB: nothing more is written to this file"; }
                        File.AppendAllText(logFile, stamped + Environment.NewLine);
                        logBytes += stamped.Length + 2;
                    }
                }
                catch (Exception) { }
            }
        }

        void Startup()
        {
            VoiceLibrary.Refresh();
            VoicePacks.Refresh();
            Synth.PruneOrphanedCache();
            LoadCatalogFromCache();
            sims = SimAutostart.Detect(simRoot);
            suppress = true;
            simCheck.Checked = false;
            if (sims.Count == 0)
            {
                simCheck.Enabled = false;
                simInfo.Text = "No Microsoft Flight Simulator installation found on this PC (the simulator writes UserCfg.opt in its folder once it has run).";
            }
            else
            {
                bool any = false;
                StringBuilder sb = new StringBuilder("Simulators found: ");
                for (int i = 0; i < sims.Count; i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(sims[i].Name);
                    if (SimAutostart.IsEnabled(sims[i])) any = true;
                }
                simInfo.Text = sb.ToString();
                simCheck.Checked = any;
            }
            suppress = false;
            UpdateSimOptions();
            FillActors();
            FillCatalogList();
            FillPacks();
            UpdateEngineState();
            // the service and the simulator link first: the mission does not speak while its voice server is away, so after a restart of the program every
            // second counts; the index, the update check and the tools come after
            if (Settings.PortWas != 0)
            {
                // the mission system connects to localhost:5997 only (tester report 2026-10-19: the service listened on 5993, the mission never connected: no checklist voice)
                AppendLog("[WARN] The saved service port was " + Settings.PortWas + ": the mission connects to port " + Settings.MissionPort + " only, the port is fixed to " + Settings.MissionPort);
                Settings.PortWas = 0;
                try { Ctx.Settings.Save(Ctx.SettingsPath); } catch (Exception) { }
            }
            EnsureActors(false);       // every main actor has a voice before the service starts (the popup comes when the window is up)
            StartServer(true);
            // the counter of the shared waypoint table goes to the simulator like the other HMC variables: a variable must be known before the link connects
            int storedRev = SharedData.StoredDataRev();
            if (storedRev > 0) SimLink.Publish(SharedData.VarRev, storedRev);
            SimLink.Start(delegate { return Ctx.Settings.AutoAudioInhibit; });
            nextSharedSync = DateTime.UtcNow.AddSeconds(10);
            RefreshTools();
            if (Ctx.Settings.AutoIndex && indexCombo.Items.Count > 0) UpdateIndex(true);
            if (Ctx.Settings.AutoUpdateCheck) CheckUpdates(true);
            AnnounceDiscontinued();
            nextActorCheck = DateTime.UtcNow.AddSeconds(30);
            if (actorsBlocked)
                Ui(delegate
                {
                    if (noVoiceAnnounced) return;
                    noVoiceAnnounced = true;
                    ShowWindow();
                    MessageBox.Show(this, "There is no voice for the crew, the dispatcher, the ground unit and the heli rescuer, so the service did not start.\n\nInstall the default voices: Service tab, tick Show advanced options at the bottom, then One click (it downloads the Piper engine and the recommended voices, about 530 MB). You can also add a voice in Windows: Settings, Time & language, Speech.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            if (Ctx.Voices.Count == 0) AppendLog("[ERROR] No voice is installed.");
            if (Program.ShowEvent != null)
            {
                // the window may not exist yet (started minimized): the message goes through the synchronization context of the interface thread
                System.Threading.SynchronizationContext ui = System.Threading.SynchronizationContext.Current;
                Thread listen = new Thread(delegate ()
                {
                    while (true)
                    {
                        try
                        {
                            Program.ShowEvent.WaitOne();
                            if (ui != null) ui.Post(delegate { Ctx.Log("[INFO] The program was started a second time: this window is shown"); ShowWindow(); }, null);
                        }
                        catch (Exception) { return; }
                    }
                });
                listen.IsBackground = true;
                listen.Start();
            }
            ThreadPool.QueueUserWorkItem(delegate { try { int n = AppSounds.EnsureAll(); if (n > 0) Ctx.Log("[INFO] " + n + " notification sound(s) copied into the audio folder"); } catch (Exception ex) { Ctx.Log("[WARN] notification sounds: " + ex.Message); } });
            // started minimized (by the simulator) while the simulator runs: the pilot says that the program is ready
            if (startHidden && Ctx.Settings.ReadyAnnounce && server != null && Tools.SimulatorRunning())
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(2500);
                    try { string line = Say.ReadyText(); Ctx.Log("[INFO] Ready announcement"); Ctx.Player.EnqueueTest(line); }
                    catch (Exception ex) { Ctx.Log("[WARN] ready announcement: " + ex.Message); }
                });
            }
        }

        void MarkDirty()
        {
            dirty = true;
            saveLabel.ForeColor = Color.Red;
            saveLabel.Text = "Modified settings, please save";
        }

        void SetSaveInfo(string text)
        {
            saveLabel.ForeColor = SystemColors.ControlText;
            saveLabel.Text = text;
        }

        // the voices chosen in the Actors tab (the language shown there) go into the settings
        void CaptureVoices()
        {
            LangInfo li = CurrentLang();
            if (li != null)
            {
                for (int i = 0; i < Actors.Count; i++)
                {
                    VoiceInfo v = voiceBox[i].SelectedItem as VoiceInfo;
                    if (v != null) Ctx.Settings.Get(li.Code, i + 1).Voice = v.Id;
                }
            }
        }

        // ---- the main actors always have a voice (user request of 2026-10-19) ----------------------------
        // the numbers that the mission shows on its pages (L:HMC_ACTORS_MISSING, L:HMC_ACTORS_DEFAULTED); the program itself only writes the log and the popup
        void PublishActorState()
        {
            SimLink.Publish(ActorRules.VarMissing, actorsMissingCount);
            SimLink.Publish(ActorRules.VarDefaulted, actorsDefaultedCount);
        }

        // Gives every main actor of every language a voice: the voice of the user when it is installed, else a Microsoft voice, else an installed Piper voice.
        // false when a language that the service needs has no voice at all: the service must not run (the red message, the log, the popup).
        bool EnsureActors(bool popup)
        {
            if (ensureBusy) return !actorsBlocked;
            ensureBusy = true;
            try
            {
                List<string> required = ActorRules.Required(Ctx.Settings);
                List<string> given = new List<string>(), blocked = new List<string>();
                int missingNow = 0, defaultedNow = 0;
                foreach (LangInfo li in LangInfo.All)
                {
                    List<int> filled = ActorRules.Fill(Ctx.Settings, li, Ctx.Voices);
                    if (filled.Count > 0)
                    {
                        defaultedNow += filled.Count;
                        string what = li.Name + ": " + ActorRules.Names(filled);
                        given.Add(what);
                        AppendLog("[WARN] No voice was assigned to " + what + ": a default voice was given");
                    }
                    List<int> still = ActorRules.Missing(Ctx.Settings, li, Ctx.Voices);
                    if (still.Count > 0 && required.Contains(li.Code)) { blocked.Add(li.Name + " (" + ActorRules.Names(still) + ")"); missingNow += still.Count; }
                }
                actorsDefaultedCount += defaultedNow;
                actorsMissingCount = missingNow;
                PublishActorState();
                if (given.Count > 0)
                {
                    VoiceDraw.Reset();
                    try { Ctx.Settings.Save(Ctx.SettingsPath); } catch (Exception) { }
                }
                actorsBlocked = blocked.Count > 0;
                if (actorsBlocked)
                {
                    string msg = "No voice is installed for " + string.Join("; ", blocked.ToArray()) + ": the service cannot run. Install the default voices (Service tab, Show advanced options, One click) or a voice in Windows (Settings, Time & language, Speech).";
                    AppendLog("[ERROR] " + msg);
                    if (server != null) StopServer();
                    if (popup && !noVoiceAnnounced)
                    {
                        noVoiceAnnounced = true;
                        Ui(delegate
                        {
                            ShowWindow();
                            MessageBox.Show(this, "There is no voice for " + string.Join("; ", blocked.ToArray()) + ".\n\nWithout a voice for the crew, the dispatcher, the ground unit and the heli rescuer the service does not start.\n\nInstall the default voices: Service tab, tick Show advanced options at the bottom, then One click (it downloads the Piper engine and the recommended voices, about 530 MB). You can also add a voice in Windows: Settings, Time & language, Speech.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                    }
                    return false;
                }
                noVoiceAnnounced = false;
                return true;
            }
            finally { ensureBusy = false; }
        }

        void SaveSettings()
        {
            CaptureVoices();
            try
            {
                Ctx.Settings.Save(Ctx.SettingsPath);
                dirty = false;
                actorsDefaultedCount = 0;          // the user has seen the defaults: the mission stops showing the message
                PublishActorState();
                SetSaveInfo("Settings saved.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- service tab -------------------------------------------------------------------------
        // Basic part: start / stop, the language of the texts, the master volume and the log. Everything else is in the Advanced box,
        // shown only while "Show advanced options" (window footer) is ticked.
        void BuildServiceTab()
        {
            TabPage page = new TabPage("Service");
            tabs.TabPages.Add(page);

            startStop.Text = "Stop service";
            startStop.Location = new Point(10, 10);
            startStop.Size = new Size(130, 30);
            startStop.Click += delegate { if (server != null) StopServer(); else StartServer(false); };
            page.Controls.Add(startStop);
            serviceLabel.AutoSize = true;
            serviceLabel.Location = new Point(160, 17);
            serviceLabel.Text = "Service: stopped";
            page.Controls.Add(serviceLabel);

            Lbl(page, "Language of the texts that have no language tag:", 10, 58, 0, 0);
            defaultLangBox.DropDownStyle = ComboBoxStyle.DropDownList;
            defaultLangBox.Location = new Point(300, 54);
            defaultLangBox.Width = 140;
            foreach (LangInfo l in LangInfo.All) defaultLangBox.Items.Add(l);
            defaultLangBox.SelectedItem = LangInfo.ByCode(Ctx.Settings.DefaultLanguage);
            defaultLangBox.SelectedIndexChanged += delegate
            {
                LangInfo l = defaultLangBox.SelectedItem as LangInfo;
                if (l != null) { Ctx.Settings.DefaultLanguage = l.Code; MarkDirty(); }
            };
            page.Controls.Add(defaultLangBox);

            profileGroup.Text = "Settings profile";
            profileGroup.Location = new Point(10, 88);
            profileGroup.Size = new Size(708, 74);
            page.Controls.Add(profileGroup);
            Button oneClick = Btn(profileGroup, "Set up in one click...", 10, 20, 170, 28);
            oneClick.Click += delegate { OneClickSetup(); };
            Button saveProfile = Btn(profileGroup, "Save settings to a file...", 190, 20, 170, 28);
            saveProfile.Click += delegate { SaveProfileToFile(); };
            Button loadProfile = Btn(profileGroup, "Load settings from a file...", 370, 20, 180, 28);
            loadProfile.Click += delegate { LoadProfileFromFile(); };
            Label profileInfo = Lbl(profileGroup, "One click: the recommended voices, radio and volumes, plus the Piper engine and voices they need (internet, about 530 MB).", 10, 52, 690, 18);
            profileInfo.ForeColor = SystemColors.GrayText;
            new ToolTip().SetToolTip(oneClick, "Applies the recommended profile and downloads what it needs. Your current settings are kept in backups\\settings.");
            new ToolTip().SetToolTip(saveProfile, "Writes the voices, speeds, radio, volumes and options to a file you can keep or give to somebody. Folders, the mission file, the port and the audio devices are not saved.");
            new ToolTip().SetToolTip(loadProfile, "Reads such a file (or a copy of HPGMissionCompanion.json) and applies it, downloading the Piper voices it needs.");

            logGroup.Text = "Log";
            logGroup.Location = new Point(10, 168);
            page.Controls.Add(logGroup);
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(10, 42, 24);
            logBox.ForeColor = Color.FromArgb(205, 240, 205);
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            logBox.WordWrap = true;
            logBox.Font = new Font("Consolas", 9F);
            logBox.Location = new Point(10, 20);
            logGroup.Controls.Add(logBox);

            // advanced box
            advBox.Text = "Advanced";
            advBox.Location = new Point(10, 296);
            advBox.Size = new Size(708, 232);
            page.Controls.Add(advBox);

            listenAllBox.Text = "Accept connections from other computers (Windows Firewall may ask to open the port " + Settings.MissionPort + ")";
            listenAllBox.AutoSize = true;
            listenAllBox.Location = new Point(10, 23);
            listenAllBox.Checked = Ctx.Settings.ListenAll;
            listenAllBox.CheckedChanged += delegate
            {
                Ctx.Settings.ListenAll = listenAllBox.Checked;
                MarkDirty();
                if (server != null) { StopServer(); StartServer(false); }      // the new address takes effect at once
            };
            advBox.Controls.Add(listenAllBox);

            Lbl(advBox, "Audio folder (wav files of the mission):", 10, 54, 0, 0);
            audioBox.Location = new Point(10, 72);
            audioBox.Size = new Size(590, 22);
            audioBox.Text = Ctx.AudioDir;
            audioBox.TextChanged += delegate { Ctx.AudioDir = audioBox.Text; Ctx.Settings.AudioFolder = audioBox.Text; MarkDirty(); };
            advBox.Controls.Add(audioBox);
            Button browse = Btn(advBox, "Browse...", 608, 70, 90, 26);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = Ctx.AudioDir;
                    if (d.ShowDialog(this) == DialogResult.OK) audioBox.Text = d.SelectedPath;
                }
            };

            inhibitCheck.Text = "Auto set default audio inhibit L:AUDIOINHIBIT";
            inhibitCheck.AutoSize = true;
            inhibitCheck.Checked = Ctx.Settings.AutoAudioInhibit;
            inhibitCheck.Location = new Point(10, 104);
            inhibitCheck.CheckedChanged += delegate { Ctx.Settings.AutoAudioInhibit = inhibitCheck.Checked; MarkDirty(); };
            advBox.Controls.Add(inhibitCheck);
            inhibitInfo.AutoSize = true;
            inhibitInfo.Location = new Point(360, 106);
            inhibitInfo.ForeColor = SystemColors.GrayText;
            inhibitInfo.Text = SimLink.Status;
            advBox.Controls.Add(inhibitInfo);

            simCheck.Text = "Start this program when Microsoft Flight Simulator starts (2020 and 2024)";
            simCheck.AutoSize = true;
            simCheck.Location = new Point(10, 126);
            simCheck.CheckedChanged += delegate { UpdateSimOptions(); if (!suppress) ApplyAutostart(); };
            advBox.Controls.Add(simCheck);
            readyCheck.Text = "Pilot says Ready at start";
            readyCheck.AutoSize = true;
            readyCheck.Checked = Ctx.Settings.ReadyAnnounce;
            readyCheck.Location = new Point(520, 126);
            readyCheck.CheckedChanged += delegate { Ctx.Settings.ReadyAnnounce = readyCheck.Checked; if (!suppress) MarkDirty(); };
            new ToolTip().SetToolTip(readyCheck, "When the program starts minimized and the simulator is running, the pilot says that the program is ready.");
            advBox.Controls.Add(readyCheck);
            Button purgeCache = Btn(advBox, "Purge audio cache", 444, 46, 126, 24);
            new ToolTip().SetToolTip(purgeCache, "Deletes cached generated speech and dialect translations in audio\\cache.");
            purgeCache.Click += delegate
            {
                string cacheDir = Path.Combine(Path.Combine(Ctx.BaseDir, "audio"), "cache");
                int fileCount = 0;
                long totalBytes = 0;
                if (Directory.Exists(cacheDir))
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                FileInfo fi = new FileInfo(f);
                                totalBytes += fi.Length;
                                fileCount++;
                            }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception) { }
                }
                if (fileCount == 0)
                {
                    MessageBox.Show("The audio cache is currently empty.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string sizeStr = totalBytes >= 1048576 ? (totalBytes / 1048576.0).ToString("0.1", CultureInfo.InvariantCulture) + " MB" : ((totalBytes + 1023) / 1024) + " KB";
                DialogResult res = MessageBox.Show(
                    "Purge audio cache (" + fileCount + " file" + (fileCount == 1 ? "" : "s") + ", " + sizeStr + ")?\n\nThis deletes all synthesized audio and dialect translations in audio\\cache.",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (res != DialogResult.Yes) return;
                long freed;
                int deleted = Synth.PurgeCache(out freed);
                string freedStr = freed >= 1048576 ? (freed / 1048576.0).ToString("0.1", CultureInfo.InvariantCulture) + " MB" : ((freed + 1023) / 1024) + " KB";
                AppendLog("[CACHE] Purged audio cache: " + deleted + " files (" + freedStr + ") deleted");
                MessageBox.Show("Audio cache purged (" + deleted + " file" + (deleted == 1 ? "" : "s") + ", " + freedStr + " freed).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            Button openLogs = Btn(advBox, "Open log folder", 578, 46, 120, 24);
            openLogs.Click += delegate
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(logFile ?? Path.Combine(Ctx.BaseDir, "logs", "x"))); System.Diagnostics.Process.Start(Path.GetDirectoryName(logFile ?? Path.Combine(Ctx.BaseDir, "logs", "x"))); }
                catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            new ToolTip().SetToolTip(openLogs, "One log file for each start of the program (the last 10 are kept): " + (logFile ?? "none in this run"));
            minimizedCheck.Text = "Start minimized, in the notification area";
            minimizedCheck.AutoSize = true;
            minimizedCheck.Checked = Ctx.Settings.StartMinimized;
            minimizedCheck.Location = new Point(30, 148);
            minimizedCheck.CheckedChanged += delegate
            {
                Ctx.Settings.StartMinimized = minimizedCheck.Checked;
                if (!suppress) { MarkDirty(); if (simCheck.Checked) ApplyAutostart(); }
            };
            advBox.Controls.Add(minimizedCheck);
            exitCheck.Text = "Close it when the simulator closes";
            exitCheck.AutoSize = true;
            exitCheck.Checked = Ctx.Settings.ExitWithSim;
            exitCheck.Location = new Point(300, 148);
            exitCheck.CheckedChanged += delegate
            {
                Ctx.Settings.ExitWithSim = exitCheck.Checked;
                if (!suppress) { MarkDirty(); if (simCheck.Checked) ApplyAutostart(); }
            };
            advBox.Controls.Add(exitCheck);
            simInfo.Location = new Point(30, 170);
            simInfo.Size = new Size(660, 30);
            simInfo.ForeColor = SystemColors.GrayText;
            advBox.Controls.Add(simInfo);

            Lbl(advBox, "Try a text:", 10, 206, 0, 0);
            testBox.Location = new Point(80, 202);
            testBox.Size = new Size(520, 22);
            testBox.Text = "<actor3><speed:1.1>Crew three, ready for the next step.";
            advBox.Controls.Add(testBox);
            Button speak = Btn(advBox, "Speak", 608, 200, 90, 26);
            speak.Click += delegate { Ctx.Player.EnqueueTest(testBox.Text); };
            new ToolTip().SetToolTip(testBox, "Tags: <actor3> <Microsoft David> <ryan> <speed:1.2> <vol:60> <radio> <EN> <IT> <FR> <DE>");

            footNote.Text = "Closing the window keeps the program running in the notification area.";
            footNote.AutoSize = true;
            footNote.ForeColor = SystemColors.GrayText;
            footNote.Location = new Point(10, 532);
            page.Controls.Add(footNote);

            ApplyAdvanced();
        }

        // ---- settings profile --------------------------------------------------------------------
        void OneClickSetup()
        {
            ProfileData p;
            try { p = Profiles.Recommended(); }
            catch (Exception ex) { MessageBox.Show("Cannot read the recommended profile: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            RunProfile(p, "Set up in one click");
        }

        void LoadProfileFromFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Load settings from a file";
                d.Filter = "Settings profile (*.json)|*.json|All files (*.*)|*.*";
                if (Directory.Exists(Profiles.ProfilesDir)) d.InitialDirectory = Profiles.ProfilesDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ProfileData p;
                try { p = Profiles.Parse(File.ReadAllText(d.FileName, Encoding.UTF8)); }
                catch (Exception ex) { MessageBox.Show("Cannot use this file: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (p.Name.Length == 0) p.Name = Path.GetFileNameWithoutExtension(d.FileName);
                RunProfile(p, "Load settings");
            }
        }

        void SaveProfileToFile()
        {
            CaptureVoices();
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Save settings to a file";
                d.Filter = "Settings profile (*.json)|*.json";
                d.FileName = "my-settings.json";
                try { Directory.CreateDirectory(Profiles.ProfilesDir); d.InitialDirectory = Profiles.ProfilesDir; } catch (Exception) { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                bool names = false;
                if (Ctx.Settings.CrewNames.Count > 0)
                    names = MessageBox.Show("Include the crew names you typed in the Actors tab?\n\nChoose No to leave them out (for a file you give to somebody).", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                try
                {
                    ProfileData p = Profiles.FromSettings(Ctx.Settings, Path.GetFileNameWithoutExtension(d.FileName), "Saved by the HPG Mission Companion " + AppInfo.Version + " on " + DateTime.Now.ToString("yyyy-MM-dd"), names);
                    File.WriteAllText(d.FileName, Profiles.ToJson(p), new UTF8Encoding(false));
                    AppendLog("[INFO] Settings written to " + d.FileName);
                    MessageBox.Show("Settings saved to " + d.FileName, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("Cannot save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // applies a profile: asks first, downloads what it needs (Piper engine, voices), keeps the old settings in backups\settings, then restarts the program
        void RunProfile(ProfileData p, string title)
        {
            if (busy) { MessageBox.Show("A download is running (Piper voices tab). Try again when it ends.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ProfileNeeds needs;
            Cursor = Cursors.WaitCursor;
            try { needs = Profiles.Plan(p, delegate (string s) { AppendLog("[INFO] " + s); }); }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                if (MessageBox.Show("The list of Piper voices cannot be read (" + ex.Message + ").\n\nApply the settings without downloading the voices? Voices that are not on this PC use the default one.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                needs = new ProfileNeeds();
            }
            Cursor = Cursors.Default;
            StringBuilder q = new StringBuilder();
            q.Append("This applies the settings profile" + (p.Name.Length > 0 ? " \"" + p.Name + "\"" : "") + ": the voices and speeds of the actors, the radio, the volumes and the options of the program.\n");
            q.Append("Folders, the mission file, the audio devices and the port are not changed. The settings you have now are kept in backups\\settings.\n\n");
            q.Append(needs.Any ? "To download (internet needed): " + needs.Describe() + ".\n" : "Nothing to download.\n");
            if (needs.Unknown.Count > 0) q.Append("Not in any list of Piper voices, the default voice is used: " + string.Join(", ", needs.Unknown.ToArray()) + ".\n");
            if (p.StartWithSim) q.Append("The program will start with Microsoft Flight Simulator.\n");
            q.Append("\nThe program restarts at the end. Continue?");
            if (MessageBox.Show(q.ToString(), title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            busy = true;
            using (ProfileProgressForm f = new ProfileProgressForm(title))
            {
                f.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        bool ok = true;
                        try { if (needs.Any) ok = Profiles.Download(needs, f.AddLine, f.SetProgress); }
                        catch (Exception ex) { f.AddLine("ERROR " + ex.Message); ok = false; }
                        f.BeginInvoke((MethodInvoker)delegate
                        {
                            try { Profiles.Finish(p, simRoot, f.AddLine); }
                            catch (Exception ex) { f.AddLine("ERROR " + ex.Message); ok = false; }
                            f.Finished(ok ? "Done. The program restarts when you close this window." : "Done, but something went wrong (see the lines below). The program restarts when you close this window.");
                        });
                    });
                };
                f.ShowDialog(this);
            }
            busy = false;
            RestartApp();
        }

        // a new copy of this program waits for this one to end (--after-pid: the mutex, the port and the Piper processes are free by then); no shell, no timer
        void RestartApp()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--after-pid " + Process.GetCurrentProcess().Id);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Ctx.BaseDir;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Cannot restart the program, start it again by hand: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            ExitApp(false);
        }

        // the advanced groups follow the footer box; the log takes the room that the Advanced box leaves
        void ApplyAdvanced()
        {
            bool on = advancedCheck.Checked;
            advBox.Visible = on;
            audioOutBox.Visible = on;
            int bottom = on ? advBox.Top - 6 : 526;
            logGroup.Size = new Size(708, bottom - logGroup.Top);
            logBox.Size = new Size(686, logGroup.Height - 30);
        }

        // the two sub-options only make sense with the simulator option
        void UpdateSimOptions()
        {
            bool on = simCheck.Checked && sims.Count > 0;
            minimizedCheck.Enabled = on;
            exitCheck.Enabled = on;
        }

        string AutostartArgs()
        {
            List<string> a = new List<string>();
            if (minimizedCheck.Checked) a.Add("--minimized");
            if (exitCheck.Checked) a.Add("--exit-with-sim");
            return string.Join(" ", a.ToArray());
        }

        void ApplyAutostart()
        {
            string exe = Application.ExecutablePath;
            Ctx.Settings.StartWithSim = simCheck.Checked;
            StringBuilder done = new StringBuilder();
            foreach (SimTarget t in sims)
            {
                try
                {
                    if (simCheck.Checked) SimAutostart.Enable(t, exe, AutostartArgs()); else SimAutostart.Disable(t);
                    done.Append(t.Name).Append("; ");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Cannot change " + t.ExeXml + ":\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            AppendLog("[INFO] Start with the simulator " + (simCheck.Checked ? "ON (" + AutostartArgs() + ")" : "OFF") + ": " + done);
            try { Ctx.Settings.Save(Ctx.SettingsPath); } catch (Exception) { }
        }

        void StartServer(bool automatic)
        {
            if (!EnsureActors(!automatic)) { SetServiceState(false); return; }       // no service without a voice for every main actor
            Server s = new Server();
            int port = Ctx.Settings.Port;
            try
            {
                s.Start(port, Ctx.Settings.ListenAll);
            }
            catch (SocketException)
            {
                portBusyNote = Tools.PortOwner(port);
                AppendLog("[WARN] Port " + port + " is busy" + (portBusyNote.Length > 0 ? ": used by " + portBusyNote : "") + ". The voices of the missions in app mode (SimConnect) do not need the port; only the old text service does.");
                if (QuietPort) { AppendLog("[WARN] Port " + port + " is busy (picture run: no question asked)"); SetServiceState(false); return; }
                // right after an update the previous program may still be closing: wait a few seconds before asking
                bool freed = false;
                for (int attempt = 0; attempt < 8 && !freed; attempt++)
                {
                    Thread.Sleep(500);
                    try { s.Start(port, Ctx.Settings.ListenAll); freed = true; } catch (SocketException) { }
                }
                if (freed) { server = s; portBusyNote = ""; SetServiceState(true); return; }
                if (automatic) { SetServiceState(false); return; }       // at the start of the program no question: the log and the label say who holds the port
                List<Process> old = new List<Process>();
                foreach (string n in new string[] { "HPGMissionAudioService", "HPGMissionAudioOneCore", "HPGMissionAudioLauncher", "HPGVoiceHub" })
                    old.AddRange(Process.GetProcessesByName(n));
                DialogResult answer = old.Count > 0
                    ? MessageBox.Show("Port " + port + " is busy: another HPG audio service is running" + (portBusyNote.Length > 0 ? " (" + portBusyNote + ")" : "") + ".\nStop it and start this one?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    : MessageBox.Show("Port " + port + " is busy: " + (portBusyNote.Length > 0 ? portBusyNote : "another program") + " uses it.\nClose that program to start this service. The voices of the missions in app mode do not need the port.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { SetServiceState(false); return; }
                foreach (Process p in old) { try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
                try { s.Start(port, Ctx.Settings.ListenAll); }
                catch (Exception ex) { AppendLog("[ERROR] " + ex.Message); SetServiceState(false); return; }
            }
            server = s;
            portBusyNote = "";
            SetServiceState(true);
        }

        void StopServer()
        {
            if (server == null) return;
            server.Stop();
            server = null;
            SetServiceState(false);
        }

        string portBusyNote = "";        // who holds the port when the service cannot start

        void SetServiceState(bool running)
        {
            startStop.Text = running ? "Stop service" : "Start service";
            serviceLabel.Text = running ? "Service: running on port " + Ctx.Settings.Port : "Service: stopped" + (portBusyNote.Length > 0 ? " (port " + Ctx.Settings.Port + " used by " + portBusyNote.Split(new char[] { '(' })[0].Trim() + ")" : "");
            tray.Text = running ? AppInfo.Name + " - running" : AppInfo.Name + " - stopped";
        }

        // ---- actors tab --------------------------------------------------------------------------
        void BuildActorsTab()
        {
            TabPage page = new TabPage("Actors and voices");
            tabs.TabPages.Add(page);
            Lbl(page, "Language:", 10, 17, 0, 0);
            langBox.DropDownStyle = ComboBoxStyle.DropDownList;
            langBox.Location = new Point(78, 13);
            langBox.Width = 160;
            foreach (LangInfo l in LangInfo.All) langBox.Items.Add(l);
            langBox.SelectedIndex = 0;
            langBox.SelectedIndexChanged += delegate { FillActors(); FillCatalogList(); };
            page.Controls.Add(langBox);
            Lbl(page, "Choose the voice and the usual speed of each actor, for the language selected above. The mission picks the actor with a tag, " +
                "for example  <actor3> Hello  (the voice, the speed and the volume can also be forced by the mission). Changes work at once; press Save settings to keep them.",
                10, 46, 706, 36);
            Lbl(page, "Name (optional)", 376, 84, 0, 0).ForeColor = SystemColors.GrayText;
            for (int i = 0; i < Actors.Count; i++)
            {
                int index = i;
                int y = 102 + i * 34;       // 12 actors: the rows end at 510, inside the visible part of the tab
                actorLabel[i] = Lbl(page, (i + 1) + "  " + Actors.Names[i], 10, y + 5, 140, 20);

                voiceBox[i] = new ComboBox();
                voiceBox[i].DropDownStyle = ComboBoxStyle.DropDownList;
                voiceBox[i].Location = new Point(155, y + 2);
                voiceBox[i].Size = new Size(215, 24);
                if (i < nameBox.Length)
                {
                    nameBox[i] = new TextBox();
                    nameBox[i].Location = new Point(376, y + 2);
                    nameBox[i].Size = new Size(90, 24);
                    nameBox[i].MaxLength = 20;
                    nameBox[i].Enabled = Ctx.Settings.Names != "random";
                    string role = NameRoles[i];
                    string cur;
                    if (Ctx.Settings.CrewNames.TryGetValue(role, out cur)) nameBox[i].Text = cur;
                    nameBox[i].TextChanged += delegate
                    {
                        if (suppress) return;
                        string nm = nameBox[index].Text.Trim();
                        if (nm.Length == 0) Ctx.Settings.CrewNames.Remove(NameRoles[index]); else Ctx.Settings.CrewNames[NameRoles[index]] = nm;
                        MarkDirty();
                        Say.ApplyUserNamesNow();
                    };
                    page.Controls.Add(nameBox[i]);
                }
                voiceBox[i].SelectedIndexChanged += delegate { OnVoiceChanged(index); };
                page.Controls.Add(voiceBox[i]);
                if (Actors.CanDraw(i + 1))
                {
                    poolButton[i] = Btn(page, "Voices...", 376, y + 1, 90, 26);
                    poolButton[i].Click += delegate { EditPool(index); };
                }

                speedBar[i] = new TrackBar();
                speedBar[i].Minimum = 5;
                speedBar[i].Maximum = 25;
                speedBar[i].Value = 10;
                speedBar[i].TickFrequency = 5;
                speedBar[i].LargeChange = 5;
                speedBar[i].Location = new Point(472, y - 2);
                speedBar[i].Size = new Size(150, 32);
                speedBar[i].ValueChanged += delegate { OnSpeedChanged(index); };
                page.Controls.Add(speedBar[i]);

                speedLabel[i] = Lbl(page, "1.0", 626, y + 5, 34, 20);

                testButton[i] = Btn(page, "Test", 662, y + 1, 56, 26);
                testButton[i].Click += delegate { TestActor(index); };
            }
            int yc = 13;
            Lbl(page, "Call sign:", 262, yc + 4, 60, 20);
            callModeBox = new ComboBox();
            callModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            callModeBox.Location = new Point(326, yc);
            callModeBox.Size = new Size(200, 24);
            callModeBox.Items.Add("Airline of the aircraft (aircraft.cfg)");
            callModeBox.Items.Add("Custom name");
            callModeBox.SelectedIndex = Ctx.Settings.CallSignMode == "custom" ? 1 : 0;
            page.Controls.Add(callModeBox);
            callBox = new TextBox();
            callBox.Location = new Point(532, yc);
            callBox.Size = new Size(186, 24);
            callBox.MaxLength = 30;
            callBox.Text = Ctx.Settings.CallSign ?? "";
            callBox.Enabled = Ctx.Settings.CallSignMode == "custom";
            callBox.TextChanged += delegate
            {
                if (suppress) return;
                Ctx.Settings.CallSign = callBox.Text.Trim();
                MarkDirty();
                Say.ApplyCallSign();
            };
            callModeBox.SelectedIndexChanged += delegate
            {
                if (suppress) return;
                Ctx.Settings.CallSignMode = callModeBox.SelectedIndex == 1 ? "custom" : "airline";
                callBox.Enabled = callModeBox.SelectedIndex == 1;
                MarkDirty();
                Say.ApplyCallSign();
            };
            page.Controls.Add(callBox);
            Lbl(page, "A name is spoken where a text uses it ({@PLT}); an empty name keeps the job role. Names are ignored while the crew names are random (Voice packs tab). The call sign is said where a text uses {@HEMS}: the atc_airline of the aircraft.cfg of the livery (Pelikan 3) or the custom name; empty = HEMS.", 10, 514, 706, 32).ForeColor = SystemColors.GrayText;
        }

        ComboBox callModeBox;
        TextBox callBox;

        LangInfo CurrentLang() { return (LangInfo)langBox.SelectedItem; }

        // the name boxes of the Actors tab work only while the crew names are not random
        void UpdateNameBoxes()
        {
            foreach (TextBox b in nameBox) if (b != null) b.Enabled = Ctx.Settings.Names != "random";
        }

        void FillActors()
        {
            LangInfo li = CurrentLang();
            if (li == null) return;
            EnsureActors(false);
            List<VoiceInfo> fam = VoiceLibrary.OfFamily(Ctx.Voices, li.Family);
            List<VoiceInfo> auto = VoiceLibrary.AutoOfFamily(Ctx.Voices, li.Family);
            suppress = true;
            for (int i = 0; i < Actors.Count; i++)
            {
                ActorSetting a = Ctx.Settings.Get(li.Code, i + 1);
                voiceBox[i].Items.Clear();
                if (Actors.CanDraw(i + 1)) voiceBox[i].Items.Add(RandomItem);
                foreach (VoiceInfo v in fam) voiceBox[i].Items.Add(v);
                VoiceInfo sel = VoiceLibrary.ById(fam, a.Voice);
                if (sel == null && auto.Count > 0) sel = auto[i % auto.Count];     // the voice the program takes for it (never an online voice)
                if (a.Random && Actors.CanDraw(i + 1)) voiceBox[i].SelectedItem = RandomItem; else voiceBox[i].SelectedItem = sel;
                if (poolButton[i] != null) poolButton[i].Visible = a.Random && Actors.CanDraw(i + 1);
                voiceBox[i].Enabled = fam.Count > 0;
                int sv = (int)Math.Round(a.Speed * 10);
                speedBar[i].Value = Math.Max(speedBar[i].Minimum, Math.Min(speedBar[i].Maximum, sv));
                speedLabel[i].Text = (speedBar[i].Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
            }
            suppress = false;
            if (fam.Count == 0 && !dirty) SetSaveInfo("No " + li.Name + " voice is installed: add one in the Piper voices tab or in Windows (Settings).");
            else if (!dirty && saveLabel.Text.StartsWith("No ")) SetSaveInfo("");
        }

        void OnVoiceChanged(int i)
        {
            if (suppress) return;
            ActorSetting a = Ctx.Settings.Get(CurrentLang().Code, i + 1);
            if (voiceBox[i].SelectedItem is string)       // the item "Random voice"
            {
                a.Random = true;
                if (poolButton[i] != null) poolButton[i].Visible = true;
                VoiceDraw.Reset();
                MarkDirty();
                return;
            }
            VoiceInfo v = voiceBox[i].SelectedItem as VoiceInfo;
            if (v == null) return;
            a.Random = false;
            a.Voice = v.Id;
            if (poolButton[i] != null) poolButton[i].Visible = false;
            VoiceDraw.Reset();
            MarkDirty();
        }

        const string RandomItem = "Random voice (see Voices...)";

        // the voices a random actor may take: a list with a check mark for each voice of the language, its sex and quality, where it is used by other actors,
        // sortable, with copy and paste of the choices (PoolForm); nothing checked or everything checked = every voice
        void EditPool(int i)
        {
            LangInfo li = CurrentLang();
            if (li == null) return;
            List<VoiceInfo> fam = VoiceLibrary.OfFamily(Ctx.Voices, li.Family);
            ActorSetting a = Ctx.Settings.Get(li.Code, i + 1);
            string who = Actors.Names[i];
            using (PoolForm f = new PoolForm("Voices for " + who + " (" + li.Name + ")", li.Code, fam, a.Pool, VoicePool.UsedMap(li.Code, fam), delegate (VoiceInfo v)
            {
                // Test: the voice selected in the list (or the first checked one) says a sample sentence, so that the voices of the pool can be heard
                if (v == null || v.Keys == null || v.Keys.Length == 0) return;
                Ctx.Player.EnqueueTest("<" + v.Keys[0] + "><" + li.Code + ">" + string.Format(li.Sample, who));
            }))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                List<string> ids = f.CheckedIds();
                a.Pool = (ids.Count == 0 || ids.Count == fam.Count) ? null : ids;
                VoiceDraw.Reset();
                MarkDirty();
            }
        }

        void OnSpeedChanged(int i)
        {
            speedLabel[i].Text = (speedBar[i].Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (suppress) return;
            Ctx.Settings.Get(CurrentLang().Code, i + 1).Speed = speedBar[i].Value / 10.0;
            MarkDirty();
        }

        void TestActor(int i)
        {
            LangInfo li = CurrentLang();
            string who = Actors.Names[i];
            int p = who.IndexOf(" (");
            if (p > 0) who = who.Substring(0, p);
            Ctx.Player.EnqueueTest("<actor" + (i + 1) + "><" + li.Code + ">" + string.Format(li.Sample, who));
        }

        // ---- piper tab ---------------------------------------------------------------------------
        void BuildPiperTab()
        {
            TabPage page = new TabPage("Piper voices");
            tabs.TabPages.Add(page);
            engineLabel.Location = new Point(10, 14);
            engineLabel.Size = new Size(470, 40);
            page.Controls.Add(engineLabel);
            engineButton.Location = new Point(490, 10);
            engineButton.Size = new Size(228, 32);
            engineButton.Click += delegate { InstallEngine(); };
            page.Controls.Add(engineButton);

            piperHint = Lbl(page, "Select a voice and press Install (60 to 140 MB each, downloaded from the Piper voices repository).", 10, 60, 708, 34);

            catalogList.View = View.Details;
            catalogList.FullRowSelect = true;
            catalogList.MultiSelect = false;
            catalogList.HideSelection = false;
            catalogList.Location = new Point(10, 98);
            catalogList.Size = new Size(708, 330);
            catalogList.Columns.Add("Voice", 190);
            catalogList.Columns.Add("Region", 70);
            catalogList.Columns.Add("Quality", 80);
            catalogList.Columns.Add("Size", 70);
            catalogList.Columns.Add("Voice type", 90);
            catalogList.Columns.Add("Status", 190);
            catalogList.SelectedIndexChanged += delegate
            {
                UpdateEngineState();
                // a community voice: who made it, the licence and what its page says, before the download
                CatalogVoice cv = SelectedCatalog();
                if (cv != null && cv.BaseUrl != null && !busy) progressLabel.Text = "Community voice: " + cv.Source + ". Licence: " + cv.License + ". " + cv.Note;
            };
            catalogList.ColumnClick += delegate (object s, ColumnClickEventArgs e) { OnCatalogColumnClick(e.Column); };
            page.Controls.Add(catalogList);

            installButton.Text = "Install voice";
            installButton.Location = new Point(10, 438);
            installButton.Size = new Size(120, 30);
            installButton.Click += delegate { InstallSelectedVoice(); };
            page.Controls.Add(installButton);
            removeButton.Text = "Remove voice";
            removeButton.Location = new Point(138, 438);
            removeButton.Size = new Size(120, 30);
            removeButton.Click += delegate { RemoveSelectedVoice(); };
            page.Controls.Add(removeButton);
            testVoiceButton.Text = "Listen";
            testVoiceButton.Location = new Point(266, 438);
            testVoiceButton.Size = new Size(100, 30);
            testVoiceButton.Click += delegate { TestSelectedVoice(); };
            page.Controls.Add(testVoiceButton);
            refreshButton.Text = "Refresh list";
            refreshButton.Location = new Point(598, 438);
            refreshButton.Size = new Size(120, 30);
            refreshButton.Click += delegate { FetchCatalog(); };
            page.Controls.Add(refreshButton);

            progress.Location = new Point(10, 482);
            progress.Size = new Size(708, 20);
            page.Controls.Add(progress);
            progressLabel.Location = new Point(10, 508);
            progressLabel.Size = new Size(708, 40);
            page.Controls.Add(progressLabel);
        }

        void UpdateEngineState()
        {
            if (Ctx.PiperInstalled)
            {
                engineLabel.Text = "Piper engine: installed.";
                engineButton.Text = "Reinstall the Piper engine";
            }
            else
            {
                engineLabel.Text = "Piper engine: not installed. It is needed to use the Piper voices (22 MB download).";
                engineButton.Text = "Install the Piper engine (22 MB)";
            }
            engineButton.Enabled = !busy;
            CatalogVoice sel = SelectedCatalog();
            bool installed = sel != null && PiperInstaller.IsInstalled(sel);
            installButton.Enabled = !busy && sel != null && !installed;
            removeButton.Enabled = !busy && installed;
            testVoiceButton.Enabled = !busy && installed && Ctx.PiperInstalled;
            refreshButton.Enabled = !busy;
        }

        CatalogVoice SelectedCatalog()
        {
            return catalogList.SelectedItems.Count > 0 ? catalogList.SelectedItems[0].Tag as CatalogVoice : null;
        }

        void LoadCatalogFromCache()
        {
            try
            {
                if (File.Exists(Ctx.CatalogPath)) catalog = PiperInstaller.LoadCatalog(File.ReadAllText(Ctx.CatalogPath, Encoding.UTF8));
            }
            catch (Exception) { }
        }

        void EnsureCatalog()
        {
            if (catalogRequested || catalog.Count > 0) return;
            FetchCatalog();
        }

        void FetchCatalog()
        {
            if (busy) return;
            catalogRequested = true;
            busy = true;
            UpdateEngineState();
            progressLabel.Text = "Getting the list of voices...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                List<CatalogVoice> list = null;
                try
                {
                    string json = Net.GetString(PiperInstaller.CatalogUrl);
                    list = PiperInstaller.LoadCatalog(json);
                    Directory.CreateDirectory(Path.GetDirectoryName(Ctx.CatalogPath));
                    File.WriteAllText(Ctx.CatalogPath, json, new UTF8Encoding(false));
                }
                catch (Exception ex) { error = ex.Message; }
                Ui(delegate
                {
                    busy = false;
                    if (error != null) progressLabel.Text = "Cannot get the list of voices (" + error + "). Check the internet connection and press Refresh list.";
                    else { catalog = list; progressLabel.Text = list.Count + " voices in the list."; }
                    FillCatalogList();
                    UpdateEngineState();
                });
            });
        }

        // the sort of the Piper voices list: column 0 name, 1 region, 2 quality, 3 size, 4 voice type, 5 installed; ties by name, then best quality first
        int CompareCatalog(CatalogVoice a, CatalogVoice b)
        {
            int r = 0;
            switch (catalogSortColumn)
            {
                case 1: r = string.Compare(a.LangCode, b.LangCode, StringComparison.OrdinalIgnoreCase); break;
                case 2: r = VoiceLibrary.QualityRank(a.Quality) - VoiceLibrary.QualityRank(b.Quality); break;
                case 3: r = a.OnnxSize.CompareTo(b.OnnxSize); break;
                case 4: r = string.Compare(a.Speakers > 1 ? "mixed" : VoiceLibrary.GenderHint(a.Name), b.Speakers > 1 ? "mixed" : VoiceLibrary.GenderHint(b.Name), StringComparison.OrdinalIgnoreCase); break;
                case 5: r = (PiperInstaller.IsInstalled(a) ? 0 : 1) - (PiperInstaller.IsInstalled(b) ? 0 : 1); break;      // ascending: installed first
            }
            if (r != 0) return catalogSortAscending ? r : -r;
            r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (r != 0) return catalogSortColumn == 0 && !catalogSortAscending ? -r : r;
            return VoiceLibrary.QualityRank(b.Quality) - VoiceLibrary.QualityRank(a.Quality);
        }

        void OnCatalogColumnClick(int column)
        {
            if (column == catalogSortColumn) catalogSortAscending = !catalogSortAscending;
            else { catalogSortColumn = column; catalogSortAscending = true; }
            FillCatalogList();
        }

        void FillCatalogList()
        {
            LangInfo li = CurrentLang();
            if (li == null) return;
            CatalogVoice keep = SelectedCatalog();
            for (int i = 0; i < CatalogColumns.Length && i < catalogList.Columns.Count; i++)
                catalogList.Columns[i].Text = CatalogColumns[i] + (i == catalogSortColumn ? (catalogSortAscending ? "  ^" : "  v") : "");
            catalogList.Items.Clear();
            if (piperHint != null) piperHint.Text = "Voices for " + li.Name + " (change the language in the Actors and voices tab). Select one and press Install (60 to 140 MB each). Rows marked Community voice come from other authors: their licence is shown below.";
            List<CatalogVoice> rows = new List<CatalogVoice>();
            foreach (CatalogVoice c in catalog) if (c.Family == li.Family) rows.Add(c);
            foreach (CatalogVoice c in PiperInstaller.Community) if (c.Family == li.Family && !rows.Exists(delegate (CatalogVoice o) { return o.Key == c.Key; })) rows.Add(c);
            rows.Sort(CompareCatalog);
            foreach (CatalogVoice c in rows)
            {
                string gender = VoiceLibrary.GenderHint(c.Name);
                ListViewItem item = new ListViewItem(c.Speakers > 1 ? c.Name + " (" + c.Speakers + " speakers)" : c.Name);
                item.SubItems.Add(c.LangCode);
                item.SubItems.Add(c.Quality);
                item.SubItems.Add((c.OnnxSize / 1048576) + " MB");
                item.SubItems.Add(c.Speakers > 1 ? "mixed" : gender);
                item.SubItems.Add(PiperInstaller.IsInstalled(c) ? (c.BaseUrl != null ? "Installed (community)" : "Installed") : (c.BaseUrl != null ? "Community voice" : ""));
                item.Tag = c;
                catalogList.Items.Add(item);
                if (keep != null && ReferenceEquals(keep, c)) { item.Selected = true; item.EnsureVisible(); }
            }
            if (rows.Count == 0 && catalog.Count == 0) progressLabel.Text = "The list of voices is not loaded yet.";
        }

        void InstallEngine()
        {
            if (busy) return;
            busy = true;
            UpdateEngineState();
            progress.Value = 0;
            progressLabel.Text = "Downloading the Piper engine...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { PiperInstaller.InstallEngine(ProgressReporter("Downloading the Piper engine")); }
                catch (Exception ex) { error = ex.Message; }
                Ui(delegate
                {
                    busy = false;
                    progress.Value = error == null ? 100 : 0;
                    progressLabel.Text = error == null ? "Piper engine installed." : "Cannot install the Piper engine: " + error;
                    VoiceLibrary.Refresh();
                    FillActors();
                    UpdateEngineState();
                });
            });
        }

        void InstallSelectedVoice()
        {
            CatalogVoice c = SelectedCatalog();
            if (c == null || busy) return;
            if (!Ctx.PiperInstalled)
            {
                MessageBox.Show("Install the Piper engine first (button at the top of this page).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            busy = true;
            UpdateEngineState();
            progress.Value = 0;
            string label = "Downloading the voice " + c.Name + " (" + c.Quality + ")";
            progressLabel.Text = label + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { PiperInstaller.InstallVoice(c, ProgressReporter(label)); }
                catch (Exception ex) { error = ex.Message; }
                Ui(delegate
                {
                    busy = false;
                    progress.Value = error == null ? 100 : 0;
                    progressLabel.Text = error == null
                        ? "Voice " + c.Name + " installed. Its licence is in voices\\piper\\" + c.Key + ".MODEL_CARD.txt"
                        : "Cannot install the voice: " + error;
                    VoiceLibrary.Refresh();
                    FillActors();
                    FillCatalogList();
                    UpdateEngineState();
                });
            });
        }

        void RemoveSelectedVoice()
        {
            CatalogVoice c = SelectedCatalog();
            if (c == null || busy) return;
            if (MessageBox.Show("Remove the voice " + c.Name + " (" + c.Quality + ") from this PC?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            PiperInstaller.RemoveVoice(c);
            VoiceLibrary.Refresh();
            FillActors();
            FillCatalogList();
            UpdateEngineState();
            progressLabel.Text = "Voice " + c.Name + " removed.";
        }

        void TestSelectedVoice()
        {
            CatalogVoice c = SelectedCatalog();
            LangInfo li = CurrentLang();
            if (c == null || li == null) return;
            Ctx.Player.EnqueueTest("<" + c.Key + "><" + li.Code + ">" + string.Format(li.Sample, c.Name));
        }

        Action<long, long> ProgressReporter(string label)
        {
            int last = -1;
            return delegate (long done, long total)
            {
                int pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                if (pct == last) return;
                last = pct;
                Ui(delegate
                {
                    progress.Value = pct;
                    progressLabel.Text = label + "... " + pct + "% (" + (done / 1048576) + " of " + (total / 1048576) + " MB)";
                });
            };
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (uiContext != null) uiContext.Post(delegate { if (!IsDisposed) a(); }, null);
                else a();
            }
            catch (Exception) { }
        }

        // ---- voice packs tab ---------------------------------------------------------------------
        void BuildPacksTab()
        {
            TabPage page = new TabPage("Voice packs");
            tabs.TabPages.Add(page);
            Lbl(page, "A voice pack is an XML file with the texts of the mission messages, several options for each. The mission speaks a message with the tag <say:KEY>. " +
                "The packs are kept in the voicepacks folder next to this program; a new version of a pack moves the old one to voicepacks\\obsolete.", 10, 10, 706, 38);

            packList.View = View.Details;
            packList.FullRowSelect = true;
            packList.GridLines = true;
            packList.HideSelection = false;
            packList.MultiSelect = false;
            packList.Location = new Point(10, 54);
            packList.Size = new Size(706, 150);
            packList.Columns.Add("Pack", 110);
            packList.Columns.Add("Language", 70);
            packList.Columns.Add("Version", 56);
            packList.Columns.Add("Base", 44);
            packList.Columns.Add("Messages", 66);
            packList.Columns.Add("State", 354);
            packList.SelectedIndexChanged += delegate { ShowPackDetail(); };
            page.Controls.Add(packList);

            dropZone.Location = new Point(10, 214);
            dropZone.Size = new Size(706, 70);
            dropZone.BorderStyle = BorderStyle.FixedSingle;
            dropZone.BackColor = Color.FromArgb(236, 244, 255);
            dropZone.AllowDrop = true;
            dropZone.Paint += delegate (object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(90, 130, 190), 2))
                {
                    pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    e.Graphics.DrawRectangle(pen, 6, 6, dropZone.Width - 14, dropZone.Height - 14);
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    e.Graphics.DrawString("Drop a voice pack here (.xml file), or press Browse", Font, Brushes.SteelBlue, new RectangleF(0, 0, dropZone.Width, dropZone.Height), sf);
                }
            };
            dropZone.DragEnter += OnPackDrag;
            dropZone.DragOver += OnPackDrag;
            dropZone.DragDrop += delegate (object sender, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) InstallPacks(files);
            };
            page.Controls.Add(dropZone);

            Button browse = Btn(page, "Browse...", 10, 292, 100, 30);
            browse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Choose a voice pack";
                    d.Filter = "Voice pack (*.xml)|*.xml|All files (*.*)|*.*";
                    d.Multiselect = true;
                    if (d.ShowDialog(this) == DialogResult.OK) InstallPacks(d.FileNames);
                }
            };
            Button open = Btn(page, "Open folder", 120, 292, 110, 30);
            open.Click += delegate
            {
                try { Directory.CreateDirectory(VoicePacks.Dir); System.Diagnostics.Process.Start(VoicePacks.Dir); }
                catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            Button reload = Btn(page, "Reload", 240, 292, 90, 30);
            reload.Click += delegate { VoicePacks.Refresh(); FillPacks(); packInfo.Text = "Packs read again from " + VoicePacks.Dir; };
            Button remove = Btn(page, "Remove selected", 340, 292, 112, 30);
            remove.Click += delegate { RemovePack(); };
            Button template = Btn(page, "New translation...", 462, 292, 124, 30);
            template.Click += delegate { NewTranslation(); };
            Button complete = Btn(page, "Add missing texts", 596, 292, 120, 30);
            complete.Click += delegate { AddMissingTexts(); };

            GroupBox gb = new GroupBox();
            gb.Text = "Simulator link of the packs";
            gb.Location = new Point(10, 330);
            gb.Size = new Size(706, 128);
            page.Controls.Add(gb);
            Lbl(gb, "Active pack used by the mission:", 10, 22, 0, 0);
            bridgeHint = Lbl(gb, BridgeHintText, 190, 22, 0, 0);
            bridgeHint.ForeColor = SystemColors.GrayText;
            bridgeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            bridgeBox.Location = new Point(10, 42);
            bridgeBox.Width = 200;
            bridgeBox.SelectedIndexChanged += delegate
            {
                if (suppress || bridgeBox.SelectedItem == null) return;
                Ctx.Settings.BridgePack = (string)bridgeBox.SelectedItem;
                MarkDirty();
                Say.ApplySettings();
                ShowActivePackWarning();
            };
            gb.Controls.Add(bridgeBox);
            Lbl(gb, "Texts:", 230, 46, 0, 0);
            Panel varPanel = new Panel();            // radio buttons of the same container exclude each other: one panel for each pair
            varPanel.Location = new Point(276, 38);
            varPanel.Size = new Size(420, 26);
            gb.Controls.Add(varPanel);
            varRandom.Text = "Random each time";
            varRandom.AutoSize = true;
            varRandom.Location = new Point(4, 4);
            varCoherent.Text = "Coherent in a conversation";
            varCoherent.AutoSize = true;
            varCoherent.Location = new Point(144, 4);
            varPanel.Controls.Add(varRandom);
            varPanel.Controls.Add(varCoherent);
            Lbl(gb, "Crew names:", 10, 78, 0, 0);
            Panel namesPanel = new Panel();
            namesPanel.Location = new Point(86, 70);
            namesPanel.Size = new Size(500, 26);
            gb.Controls.Add(namesPanel);
            namesFunc.Text = "Job roles, or the names typed in Actors";
            namesFunc.AutoSize = true;
            namesFunc.Location = new Point(4, 4);
            namesRandom.Text = "Random names for each shift";
            namesRandom.AutoSize = true;
            namesRandom.Location = new Point(270, 4);
            namesPanel.Controls.Add(namesFunc);
            namesPanel.Controls.Add(namesRandom);
            varRandom.Checked = Ctx.Settings.Variety != "coherent"; varCoherent.Checked = Ctx.Settings.Variety == "coherent";
            namesFunc.Checked = Ctx.Settings.Names != "random"; namesRandom.Checked = Ctx.Settings.Names == "random";
            varCoherent.CheckedChanged += delegate { if (suppress) return; Ctx.Settings.Variety = varCoherent.Checked ? "coherent" : "random"; MarkDirty(); Say.ApplySettings(); };
            namesRandom.CheckedChanged += delegate { if (suppress) return; Ctx.Settings.Names = namesRandom.Checked ? "random" : "functions"; MarkDirty(); Say.ApplySettings(); UpdateNameBoxes(); };
            UpdateNameBoxes();
            bridgeInfo.Location = new Point(10, 102);
            bridgeInfo.Size = new Size(686, 22);
            bridgeInfo.ForeColor = SystemColors.GrayText;
            gb.Controls.Add(bridgeInfo);

            packInfo.Location = new Point(10, 464);
            packInfo.Size = new Size(706, 34);
            packInfo.ForeColor = SystemColors.GrayText;
            page.Controls.Add(packInfo);
            Lbl(page, "In the mission:  {\"speak\":\"<pack:EN:1>\"}  declares the pack and the version it needs;  {\"speak\":\"<radio><say:CPL.5>\"}  speaks a message;  " +
                "{\"speak\":{\"text\":\"<say:KEY>{0}//{1}\",\"params\":[...]}}  fills {0} and {1} in the text. All the tags and the pack format are in the guide of the About tab.",
                10, 500, 706, 48);
        }

        static bool HasXml(DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return false;
            foreach (string f in files) if (f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void OnPackDrag(object sender, DragEventArgs e)
        {
            e.Effect = HasXml(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void FillPacks()
        {
            suppress = true;
            bridgeBox.Items.Clear();
            foreach (VoicePack bp in VoicePacks.Packs) if (bp.Usable) bridgeBox.Items.Add(bp.Name);
            string chosen = Say.BridgePackName;
            if (bridgeBox.Items.Contains(chosen)) bridgeBox.SelectedItem = chosen; else if (bridgeBox.Items.Count > 0) bridgeBox.SelectedIndex = 0;
            suppress = false;
            packList.Items.Clear();
            VoicePack reference = PackCheck.Reference();
            foreach (VoicePack p in VoicePacks.Packs)
            {
                ListViewItem it = new ListViewItem(p.Name.Length > 0 ? p.Name : "(" + Path.GetFileName(p.File) + ")");
                it.SubItems.Add(p.Language);
                it.SubItems.Add(p.Version > 0 ? p.Version.ToString() : "-");
                it.SubItems.Add(p == reference ? "base" : (p.BaseVer > 0 ? p.BaseVer.ToString() : "-"));
                it.SubItems.Add(p.Messages.Count.ToString());
                // red: unusable, texts missing against the base pack, or versions that cannot work together; orange: problems in the file, other values than the base pack, a base pack that moved on
                PackReport rep = p.Usable ? PackCheck.Compare(p, reference) : new PackReport();
                string state;
                if (!p.Usable) state = "NOT USABLE: " + (p.Problems.Count > 0 ? p.Problems[0] : "no name or version");
                else
                {
                    state = rep.Level > 0 ? rep.Summary() : (p.Problems.Count == 0 ? rep.Summary() : "");
                    if (p.Problems.Count > 0) state = (state.Length > 0 && state != "OK" ? state + "; " : "") + p.Problems.Count + " problem(s): " + p.Problems[0];
                }
                it.SubItems.Add(state);
                it.Tag = p;
                if (!p.Usable || rep.Level == 2) it.ForeColor = Color.Firebrick;
                else if (p.Problems.Count > 0 || rep.Level == 1) it.ForeColor = Color.DarkOrange;
                packList.Items.Add(it);
            }
            if (packList.Items.Count == 0) packInfo.Text = "No voice pack installed. Drop an XML file above.";
            ShowActivePackWarning();
        }

        // under the choice of the active pack: the same warning in red when that pack lacks texts or does not fit the program
        void ShowActivePackWarning()
        {
            if (bridgeHint == null) return;
            VoicePack p = VoicePacks.Find(Say.BridgePackName);
            PackReport rep = p != null ? PackCheck.Compare(p, PackCheck.Reference()) : null;
            if (rep == null || rep.Level < 2) { bridgeHint.Text = BridgeHintText; bridgeHint.ForeColor = SystemColors.GrayText; return; }
            bridgeHint.Text = rep.Missing.Count > 0 ? "INCOMPLETE: " + rep.Missing.Count + " texts missing, spoken in English" : "Does not fit the program: see the list above";
            bridgeHint.ForeColor = Color.Firebrick;
        }

        void ShowPackDetail()
        {
            if (packList.SelectedItems.Count == 0) return;
            VoicePack p = packList.SelectedItems[0].Tag as VoicePack;
            if (p == null || !p.Usable) return;
            VoicePack reference = PackCheck.Reference();
            PackReport rep = PackCheck.Compare(p, reference);
            StringBuilder sb = new StringBuilder();
            sb.Append(p.Name).Append(": ").Append(p == reference ? "the base pack (" + p.Messages.Count + " messages). A translation copies it." : rep.Summary());
            if (rep.Missing.Count > 0)
            {
                sb.Append(" First missing keys: ");
                for (int i = 0; i < rep.Missing.Count && i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(rep.Missing[i]);
                sb.Append(". Press Add missing texts to add them in English.");
            }
            packInfo.ForeColor = rep.Level == 2 ? Color.Firebrick : (rep.Level == 1 ? Color.DarkOrange : SystemColors.GrayText);
            packInfo.Text = sb.ToString();
        }

        // a new pack file made from the base pack, to translate
        void NewTranslation()
        {
            VoicePack reference = PackCheck.Reference();
            if (reference == null) { packInfo.ForeColor = Color.Firebrick; packInfo.Text = "The base pack " + PackCheck.BaseName + " is not installed."; return; }
            string lang = Ctx.Settings.DefaultLanguage;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "New translation of the pack " + reference.Name;
                d.Filter = "Voice pack (*.xml)|*.xml";
                d.FileName = (lang.Length > 0 && lang != "EN" ? lang : "MyPack") + ".xml";
                d.InitialDirectory = VoicePacks.Dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string name = Path.GetFileNameWithoutExtension(d.FileName);
                    string code = lang.Length > 0 && lang != "EN" ? lang : "IT";
                    string msg = PackCheck.ExportTemplate(reference, name, code, d.FileName);
                    packInfo.ForeColor = SystemColors.ControlText;
                    packInfo.Text = msg + " Change language=\"\" in the file if it is not " + code + ", translate the texts, then press Reload.";
                }
                catch (Exception ex) { packInfo.ForeColor = Color.Firebrick; packInfo.Text = "ERROR: " + ex.Message; }
            }
        }

        // the keys that the base pack gained since the pack was made, added in English at the end of the pack
        void AddMissingTexts()
        {
            if (packList.SelectedItems.Count == 0) { packInfo.ForeColor = Color.Firebrick; packInfo.Text = "Select a pack in the list first."; return; }
            VoicePack p = packList.SelectedItems[0].Tag as VoicePack;
            try
            {
                string msg = PackCheck.AddMissing(p, PackCheck.Reference());
                FillPacks();
                packInfo.ForeColor = SystemColors.ControlText;
                packInfo.Text = msg;
                AppendLog("[INFO] Voice packs: " + msg);
            }
            catch (Exception ex) { packInfo.ForeColor = Color.Firebrick; packInfo.Text = "ERROR: " + ex.Message; }
        }

        void InstallPacks(string[] files)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string f in files)
            {
                if (!f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) { sb.AppendLine(Path.GetFileName(f) + ": not an XML file."); continue; }
                try
                {
                    sb.AppendLine(VoicePacks.Install(f, delegate (string q) { return MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }));
                }
                catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); }
            }
            FillPacks();
            packInfo.ForeColor = sb.ToString().Contains("ERROR") ? Color.Firebrick : SystemColors.ControlText;
            packInfo.Text = sb.ToString().Trim();
            AppendLog("[INFO] Voice packs: " + sb.ToString().Trim().Replace("\r\n", " "));
        }

        void RemovePack()
        {
            if (packList.SelectedItems.Count == 0) { packInfo.Text = "Select a pack in the list first."; return; }
            VoicePack p = packList.SelectedItems[0].Tag as VoicePack;
            if (p == null) return;
            if (MessageBox.Show(this, "Remove the pack " + (p.Name.Length > 0 ? p.Name : Path.GetFileName(p.File)) + "?\n\nThe file moves to voicepacks\\obsolete.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Directory.CreateDirectory(VoicePacks.ObsoleteDir);
                File.Move(p.File, Path.Combine(VoicePacks.ObsoleteDir, Path.GetFileNameWithoutExtension(p.File) + "-removed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml"));
                VoicePacks.Refresh();
                FillPacks();
                packInfo.ForeColor = SystemColors.ControlText;
                packInfo.Text = "Removed " + p.Name + ".";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ---- radio tab ---------------------------------------------------------------------------
        void BuildRadioTab()
        {
            TabPage page = new TabPage("Radio effect");
            page.AutoScroll = true;
            tabs.TabPages.Add(page);
            RadioSettings rs = Ctx.Settings.Radio;
            Lbl(page, "Three sounds. The intercom (ICS) of the helicopter: a click and the hiss set here. A radio call (<radio>): the beep and the radio hiss set here, a band filter like a radio speaker " +
                "and a short squelch burst at the end. A call on an external radio (<radio:ext>, the dispatcher and the hospital): a double beep and the external radio hiss set here. Without a tag the voice is clean. " +
                "These values are the same for every voice.", 10, 10, 696, 52);

            Lbl(page, "Hiss (intercom):", 10, 80, 0, 0);
            noiseBar.Minimum = 0; noiseBar.Maximum = 100; noiseBar.TickFrequency = 10; noiseBar.LargeChange = 10;
            noiseBar.Value = rs.NoiseLevel;
            noiseBar.Location = new Point(190, 68);
            noiseBar.Size = new Size(300, 36);
            noiseBar.ValueChanged += delegate { noiseLabel.Text = noiseBar.Value + "%"; if (!suppress) { Ctx.Settings.Radio.NoiseLevel = noiseBar.Value; MarkDirty(); } };
            page.Controls.Add(noiseBar);
            noiseLabel.Text = rs.NoiseLevel + "%";
            noiseLabel.AutoSize = true;
            noiseLabel.Location = new Point(496, 80);
            page.Controls.Add(noiseLabel);

            Lbl(page, "Hiss (radio):", 10, 132, 0, 0);
            radioNoiseBar.Minimum = 0; radioNoiseBar.Maximum = 100; radioNoiseBar.TickFrequency = 10; radioNoiseBar.LargeChange = 10;
            radioNoiseBar.Value = rs.RadioNoiseLevel;
            radioNoiseBar.Location = new Point(190, 120);
            radioNoiseBar.Size = new Size(300, 36);
            radioNoiseBar.ValueChanged += delegate { radioNoiseLabel.Text = radioNoiseBar.Value + "%"; if (!suppress) { Ctx.Settings.Radio.RadioNoiseLevel = radioNoiseBar.Value; MarkDirty(); } };
            page.Controls.Add(radioNoiseBar);
            radioNoiseLabel.Text = rs.RadioNoiseLevel + "%";
            radioNoiseLabel.AutoSize = true;
            radioNoiseLabel.Location = new Point(496, 132);
            page.Controls.Add(radioNoiseLabel);

            Lbl(page, "Hiss (external radio):", 10, 184, 0, 0);
            extRadioNoiseBar.Minimum = 0; extRadioNoiseBar.Maximum = 100; extRadioNoiseBar.TickFrequency = 10; extRadioNoiseBar.LargeChange = 10;
            extRadioNoiseBar.Value = rs.ExtRadioNoiseLevel;
            extRadioNoiseBar.Location = new Point(190, 172);
            extRadioNoiseBar.Size = new Size(300, 36);
            extRadioNoiseBar.ValueChanged += delegate { extRadioNoiseLabel.Text = extRadioNoiseBar.Value + "%"; if (!suppress) { Ctx.Settings.Radio.ExtRadioNoiseLevel = extRadioNoiseBar.Value; MarkDirty(); } };
            page.Controls.Add(extRadioNoiseBar);
            extRadioNoiseLabel.Text = rs.ExtRadioNoiseLevel + "%";
            extRadioNoiseLabel.AutoSize = true;
            extRadioNoiseLabel.Location = new Point(496, 184);
            page.Controls.Add(extRadioNoiseLabel);

            Lbl(page, "Start beep (radio):", 10, 228, 0, 0);
            beepBox.DropDownStyle = ComboBoxStyle.DropDownList;
            beepBox.Location = new Point(190, 224);
            beepBox.Width = 160;
            foreach (string b in AudioFx.BeepTypes) beepBox.Items.Add(b);
            beepBox.SelectedItem = rs.Beep;
            if (beepBox.SelectedIndex < 0) beepBox.SelectedItem = "Beep";
            beepBox.SelectedIndexChanged += delegate { if (!suppress && beepBox.SelectedItem != null) { Ctx.Settings.Radio.Beep = (string)beepBox.SelectedItem; MarkDirty(); } };
            page.Controls.Add(beepBox);

            Lbl(page, "Beep volume:", 10, 266, 0, 0);
            beepBar.Minimum = 0; beepBar.Maximum = 100; beepBar.TickFrequency = 10; beepBar.LargeChange = 10;
            beepBar.Value = rs.BeepLevel;
            beepBar.Location = new Point(190, 254);
            beepBar.Size = new Size(300, 36);
            beepBar.ValueChanged += delegate { beepLabel.Text = beepBar.Value + "%"; if (!suppress) { Ctx.Settings.Radio.BeepLevel = beepBar.Value; MarkDirty(); } };
            page.Controls.Add(beepBar);
            beepLabel.Text = rs.BeepLevel + "%";
            beepLabel.AutoSize = true;
            beepLabel.Location = new Point(496, 266);
            page.Controls.Add(beepLabel);

            tailCheck.Text = "Squelch burst when the transmission ends";
            tailCheck.AutoSize = true;
            tailCheck.Checked = rs.Tail;
            tailCheck.Location = new Point(10, 306);
            tailCheck.CheckedChanged += delegate { if (!suppress) { Ctx.Settings.Radio.Tail = tailCheck.Checked; MarkDirty(); } };
            page.Controls.Add(tailCheck);

            Lbl(page, "Radio sound:", 10, 336, 0, 0);
            qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
            qualityBox.Location = new Point(190, 332);
            qualityBox.Width = 160;
            foreach (string q in AudioFx.Qualities) qualityBox.Items.Add(q);
            qualityBox.SelectedItem = rs.Quality;
            if (qualityBox.SelectedIndex < 0) qualityBox.SelectedItem = "Aviation";
            qualityBox.SelectedIndexChanged += delegate { if (!suppress && qualityBox.SelectedItem != null) { Ctx.Settings.Radio.Quality = (string)qualityBox.SelectedItem; MarkDirty(); } };
            page.Controls.Add(qualityBox);
            Lbl(page, "Clean: wide band.  Aviation: narrow band, compressed.  Rough: narrower, distorted.", 190, 360, 0, 0).ForeColor = SystemColors.GrayText;

            Button withRadio = Btn(page, "Radio call", 10, 384, 130, 30);
            withRadio.Click += delegate { TestRadio("<radio>"); };
            Button ics = Btn(page, "Intercom (ICS)", 150, 384, 130, 30);
            ics.Click += delegate { TestRadio("<ics>"); };
            Button ext = Btn(page, "External radio", 290, 384, 130, 30);
            ext.Click += delegate { TestRadio("<radio:ext>"); };
            Button without = Btn(page, "Clean voice", 430, 384, 130, 30);
            without.Click += delegate { TestRadio("<radio:off>"); };

            Lbl(page, "In the mission text:", 10, 422, 0, 0);
            Lbl(page, "<radio>Pilot to dispatch, ready.", 30, 442, 0, 0);
            Lbl(page, "<ics>Pilot to crew, intercom.", 30, 459, 0, 0);
            Lbl(page, "<radio:ext>Dispatcher call, external radio.", 30, 476, 0, 0);
            Lbl(page, "<radio:off>Back to the clean voice.     <zone:far>  a voice heard very low (30 to 100 m away).", 30, 493, 0, 0);
            Lbl(page, "<radio><beep:double>Radio call with another beep (none, click, beep, double, chirp).", 30, 510, 0, 0);
            Lbl(page, "<vol:60><radio>Quieter radio call.", 30, 527, 0, 0);
            Lbl(page, "The beep, the hiss and the filter follow the volume of the text (<vol:..>) and the master volume of the Volume tab. " +
                "Who hears what (intercom, radio, voice, silence) is decided by the program from the context of the mission, see the guide of the About tab.", 10, 548, 696, 40).ForeColor = SystemColors.GrayText;
        }

        // ---- volume tab --------------------------------------------------------------------------
        void BuildVolumeTab()
        {
            TabPage page = new TabPage("Volume");
            page.AutoScroll = true;
            tabs.TabPages.Add(page);
            Lbl(page, "How loud a voice is: the master volume below, the volume tag of the text (<vol:..>), the volumes of the mission, the position of the view and, for a voice in the air, " +
                "the distance from the helicopter. The level is applied to each line when its turn to be spoken comes (not while it is playing): a change of a slider is heard from the next line.", 10, 12, 706, 50);

            Lbl(page, "Master volume of the voices:", 10, 78, 0, 0);
            masterBar.Minimum = 0; masterBar.Maximum = 100; masterBar.TickFrequency = 10; masterBar.LargeChange = 10;
            masterBar.Value = Math.Max(0, Math.Min(100, Ctx.Settings.MasterVolume));
            masterBar.Location = new Point(190, 70);
            masterBar.Size = new Size(260, 36);
            masterBar.ValueChanged += delegate
            {
                masterLabel.Text = masterBar.Value + "%";
                Ctx.Settings.MasterVolume = masterBar.Value;
                MarkDirty();
            };
            page.Controls.Add(masterBar);
            masterLabel.Text = masterBar.Value + "%";
            masterLabel.AutoSize = true;
            masterLabel.Location = new Point(456, 78);
            page.Controls.Add(masterLabel);
            Lbl(page, "(the mission can set a text with <vol:50>)", 500, 78, 0, 0).ForeColor = SystemColors.GrayText;

            missionVolCheck.Text = "Follow the volumes of the mission";
            missionVolCheck.AutoSize = true;
            missionVolCheck.Location = new Point(10, 122);
            missionVolCheck.Checked = Ctx.Settings.UseMissionVolumes;
            missionVolCheck.CheckedChanged += delegate { Ctx.Settings.UseMissionVolumes = missionVolCheck.Checked; if (!suppress) MarkDirty(); };
            page.Controls.Add(missionVolCheck);
            Lbl(page, "Crew voices and sounds follow L:VOLUME_CREW, the checklist voice (actor 11) follows L:VOLUME_CHK: the two sliders of the mission tablet. 100 is the full level. " +
                "The Test buttons of this program ignore them.", 30, 146, 690, 36).ForeColor = SystemColors.GrayText;

            Lbl(page, "A voice (not a radio call) from inside, view outside the helicopter:", 10, 192, 0, 0);
            attOutBar.Minimum = 0; attOutBar.Maximum = 30; attOutBar.TickFrequency = 3; attOutBar.LargeChange = 3;
            attOutBar.Value = Ctx.Settings.AttenCamOutside;
            attOutBar.Location = new Point(10, 212);
            attOutBar.Size = new Size(420, 36);
            attOutBar.ValueChanged += delegate { attOutLabel.Text = attOutBar.Value == 0 ? "off" : "-" + attOutBar.Value + " dB"; Ctx.Settings.AttenCamOutside = attOutBar.Value; if (!suppress) MarkDirty(); };
            page.Controls.Add(attOutBar);
            attOutLabel.AutoSize = true;
            attOutLabel.Location = new Point(440, 220);
            attOutLabel.Text = attOutBar.Value == 0 ? "off" : "-" + attOutBar.Value + " dB";
            page.Controls.Add(attOutLabel);

            Lbl(page, "A voice from outside (technicians, ground unit, patient, crew on the ground), view inside the helicopter:", 10, 262, 0, 0);
            attInBar.Minimum = 0; attInBar.Maximum = 30; attInBar.TickFrequency = 3; attInBar.LargeChange = 3;
            attInBar.Value = Ctx.Settings.AttenCamInside;
            attInBar.Location = new Point(10, 282);
            attInBar.Size = new Size(420, 36);
            attInBar.ValueChanged += delegate { attInLabel.Text = attInBar.Value == 0 ? "off" : "-" + attInBar.Value + " dB"; Ctx.Settings.AttenCamInside = attInBar.Value; if (!suppress) MarkDirty(); };
            page.Controls.Add(attInBar);
            attInLabel.AutoSize = true;
            attInLabel.Location = new Point(440, 290);
            attInLabel.Text = attInBar.Value == 0 ? "off" : "-" + attInBar.Value + " dB";
            page.Controls.Add(attInLabel);

            Lbl(page, "Radio calls, the intercom and external radio calls are not attenuated: they are heard in the headset wherever the view is. Who is aboard and how far the technician and the " +
                "ground unit are comes from the context of the mission (L:HMC_CREW_PRESENT, L:HMC_ZONE_*); the view from the simulator (CAMERA STATE: 2 is the cockpit, every other view counts as outside). " +
                "With somebody aboard, a voice in the air that comes from more than 30 m is heard very low and from more than 100 m not at all; when everybody is outside together the voices keep the normal level. " +
                "Without the mission context or the view nothing is reduced. Sound files (ringtones, notifications) follow L:VOLUME_CREW and the notification level of the advanced options below.", 10, 336, 706, 96).ForeColor = SystemColors.GrayText;
            Lbl(page, "Voices at the same time:", 10, 444, 0, 0);
            overlapBox.DropDownStyle = ComboBoxStyle.DropDownList;
            overlapBox.Location = new Point(166, 440);
            overlapBox.Width = 540;
            overlapBox.Items.Add("Radio, crew and checklist take turns (the sounds and urgent calls may overlap)");
            overlapBox.Items.Add("Everything together (radio, crew and checklist speak at the same time)");
            overlapBox.Items.Add("One line at a time in the whole program");
            overlapBox.SelectedIndex = Ctx.Settings.OverlapMode == 0 ? 2 : (Ctx.Settings.OverlapMode == 2 ? 1 : 0);
            overlapBox.SelectedIndexChanged += delegate
            {
                Ctx.Settings.OverlapMode = overlapBox.SelectedIndex == 2 ? 0 : (overlapBox.SelectedIndex == 1 ? 2 : 1);
                Ctx.Settings.OverlapLanes = Ctx.Settings.OverlapMode != 0;
                if (!suppress) MarkDirty();
            };
            page.Controls.Add(overlapBox);
            Lbl(page, "With turns a line of the crew waits while the dispatcher speaks and the other way round, with fewer and clearer overlaps; a time critical call (callouts of the hoist, V, engine fire) and the tablet tones are never held back. Lines that wait too long are dropped, as before.", 10, 468, 706, 34).ForeColor = SystemColors.GrayText;
            mixInfo.Location = new Point(10, 508);
            mixInfo.Size = new Size(706, 22);
            page.Controls.Add(mixInfo);

            BuildAudioOutBox(page);
        }

        // Advanced: where each kind of sound goes and how loud (the user's request of 2026-10-11). Headset = intercom, radio calls, external radio calls
        // and the notifications (also with the helicopter off); external = voices in the air; the notifications have their own level. The levels
        // multiply with the master volume, the tag of the text and the volumes and the view of the mission.
        void BuildAudioOutBox(TabPage page)
        {
            audioOutBox.Text = "Audio output (advanced)";
            audioOutBox.Location = new Point(10, 538);
            audioOutBox.Size = new Size(664, 292);
            page.Controls.Add(audioOutBox);
            AudioOutSettings ao = Ctx.Settings.AudioOut;
            List<string> devices = WaveOutDevices.Names();

            Lbl(audioOutBox, "Headset: intercom, radio calls, external radio calls and the notifications (also with the helicopter off)", 10, 22, 644, 18);
            ComboBox headsetCombo = AudioDeviceCombo(10, 44, devices, ao.HeadsetDevice, delegate (string n) { Ctx.Settings.AudioOut.HeadsetDevice = n; });
            AudioLevel(44, 406, 446, 170, ao.HeadsetLevel, delegate (int v) { Ctx.Settings.AudioOut.HeadsetLevel = v; }, 336, delegate { TestRadio("<ics>"); });

            Lbl(audioOutBox, "External: voices in the air (technicians, ground unit, patient, the crew outside the helicopter)", 10, 92, 644, 18);
            ComboBox externalCombo = AudioDeviceCombo(10, 114, devices, ao.ExternalDevice, delegate (string n) { Ctx.Settings.AudioOut.ExternalDevice = n; });
            AudioLevel(114, 406, 446, 170, ao.ExternalLevel, delegate (int v) { Ctx.Settings.AudioOut.ExternalLevel = v; }, 336, delegate { TestRadio("<radio:off>"); });

            Lbl(audioOutBox, "Notifications: ringtones, new message tone, tablet alarm. They use the device of the headset, and their own level:", 10, 162, 644, 18);
            AudioLevel(186, 10, 56, 300, ao.NotifyLevel, delegate (int v) { Ctx.Settings.AudioOut.NotifyLevel = v; }, 440, delegate { Ctx.Player.PlayNotificationTest("AND.NEW.MSG.wav"); });

            Lbl(audioOutBox, "The default is the Windows default device and every level at 100. A level multiplies the master volume, the volume tag of the text, the volumes of the mission (L:VOLUME_CREW, L:VOLUME_CHK) and the view; " +
                "the notifications have no radio effect. The list shows the output devices as Windows names them (cut at 31 characters): a device that is not there when a sound plays is replaced by the default one, and the log says so. " +
                "A change is heard from the next sound.", 10, 228, 644, 56).ForeColor = SystemColors.GrayText;
            audioOutBox.Visible = advancedCheck.Checked;
        }

        // a drop-down of the output devices: the first line is the Windows default device ("" in the settings)
        ComboBox AudioDeviceCombo(int x, int y, List<string> devices, string current, Action<string> set)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Location = new Point(x, y);
            c.Size = new Size(320, 24);
            c.Items.Add(WaveOutDevices.DefaultName);
            foreach (string d in devices) c.Items.Add(d);
            int idx = 0;
            for (int i = 0; i < devices.Count; i++) if (string.Equals(devices[i], current, StringComparison.OrdinalIgnoreCase)) idx = i + 1;
            if (idx == 0 && !string.IsNullOrEmpty(current)) { c.Items.Add(current + " (not found)"); idx = c.Items.Count - 1; }
            c.SelectedIndex = idx;
            c.SelectedIndexChanged += delegate
            {
                string chosen = c.SelectedIndex <= 0 ? "" : c.SelectedItem.ToString();
                if (chosen.EndsWith(" (not found)")) chosen = chosen.Substring(0, chosen.Length - " (not found)".Length);
                set(chosen);
                if (!suppress) MarkDirty();
            };
            audioOutBox.Controls.Add(c);
            return c;
        }

        // a level slider with its percentage and a Test button on the line of y
        void AudioLevel(int y, int captionX, int barX, int barW, int value, Action<int> set, int testX, EventHandler test)
        {
            Lbl(audioOutBox, "Level:", captionX, y + 6, 0, 0);
            TrackBar bar = new TrackBar();
            bar.Minimum = 0; bar.Maximum = 100; bar.TickFrequency = 10; bar.LargeChange = 10;
            bar.Value = Math.Max(0, Math.Min(100, value));
            bar.Location = new Point(barX, y - 4);
            bar.Size = new Size(barW, 36);
            Label pct = new Label();
            pct.AutoSize = true;
            pct.Location = new Point(bar.Right + 2, y + 6);
            pct.Text = bar.Value + "%";
            bar.ValueChanged += delegate { pct.Text = bar.Value + "%"; set(bar.Value); if (!suppress) MarkDirty(); };
            audioOutBox.Controls.Add(bar);
            audioOutBox.Controls.Add(pct);
            Button t = Btn(audioOutBox, "Test", testX, y - 1, 60, 26);
            t.Click += test;
        }
        void TestRadio(string tag)
        {
            LangInfo li = CurrentLang();
            Ctx.Player.EnqueueTest("<actor1><" + li.Code + ">" + tag + string.Format(li.Sample, Actors.Names[0]));
        }

        // ---- tools tab ---------------------------------------------------------------------------
        // Two boxes: the work folder of the mission and the mission index. The updates have their own tab.
        void BuildToolsTab()
        {
            TabPage page = new TabPage("Tools");
            tabs.TabPages.Add(page);

            GroupBox workBox = new GroupBox();
            workBox.Text = "Work folder";
            workBox.Location = new Point(10, 8);
            workBox.Size = new Size(708, 156);
            page.Controls.Add(workBox);
            Lbl(workBox, "The tables, saved games and snapshots the mission writes. Close the simulator before cleaning it.", 10, 22, 688, 18);
            workCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            workCombo.Location = new Point(10, 48);
            workCombo.Size = new Size(520, 24);
            workCombo.SelectedIndexChanged += delegate { UpdateWorkInfo(); };
            workBox.Controls.Add(workCombo);
            Button browseWork = Btn(workBox, "Browse...", 538, 46, 76, 27);
            browseWork.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "Choose the work folder of the mission (hpg-airbus-h145-ap\\work)";
                    if (d.ShowDialog(this) == DialogResult.OK) { Ctx.Settings.WorkFolder = d.SelectedPath; MarkDirty(); RefreshTools(); }
                }
            };
            Button rescan = Btn(workBox, "Rescan", 620, 46, 76, 27);
            rescan.Click += delegate { RefreshTools(); };
            Button openWork = Btn(workBox, "Open folder", 10, 84, 140, 30);
            openWork.Click += delegate { OpenWork(); };
            Button clean = Btn(workBox, "Clean up...", 160, 84, 140, 30);
            clean.Click += delegate { CleanWork(); };
            workInfo.Size = new Size(686, 18);
            workInfo.Location = new Point(10, 124);
            workInfo.ForeColor = SystemColors.GrayText;
            workBox.Controls.Add(workInfo);

            GroupBox indexBox = new GroupBox();
            indexBox.Text = "Mission index";
            indexBox.Location = new Point(10, 172);
            indexBox.Size = new Size(708, 132);
            page.Controls.Add(indexBox);
            Lbl(indexBox, "The HPG tool Update Mission Index, to run after a mission file is added or changed in the Community folder.", 10, 22, 688, 18);
            indexCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            indexCombo.Location = new Point(10, 48);
            indexCombo.Size = new Size(596, 24);
            indexBox.Controls.Add(indexCombo);
            Button browseIdx = Btn(indexBox, "Browse...", 614, 46, 76, 27);
            browseIdx.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Choose Update Mission Index.cmd";
                    d.Filter = "Update Mission Index.cmd|*.cmd|All files|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) { Ctx.Settings.IndexTool = d.FileName; MarkDirty(); RefreshTools(); }
                }
            };
            indexButton.Text = "Update mission index";
            indexButton.Location = new Point(10, 88);
            indexButton.Size = new Size(180, 30);
            indexButton.Click += delegate { UpdateIndex(false); };
            indexBox.Controls.Add(indexButton);
            autoIndexCheck.Text = "Update the index automatically when the app starts";
            autoIndexCheck.AutoSize = true;
            autoIndexCheck.Checked = Ctx.Settings.AutoIndex;
            autoIndexCheck.Location = new Point(204, 94);
            autoIndexCheck.CheckedChanged += delegate { Ctx.Settings.AutoIndex = autoIndexCheck.Checked; MarkDirty(); };
            indexBox.Controls.Add(autoIndexCheck);

            GroupBox sharedBox = new GroupBox();
            sharedBox.Text = "Shared data: hangar and hospital waypoints, offline databases";
            sharedBox.Location = new Point(10, 310);
            sharedBox.Size = new Size(708, 172);
            page.Controls.Add(sharedBox);
            Lbl(sharedBox, "The hangar and hospital points of the mission are kept on GitHub for everybody (plus your own). The program writes them into the work folder and the mission reads them " +
                "at the next dispatch. The airports, aprons and helipads of the HPG tablet can get the same changes (only with the simulator closed; the original files are kept).", 10, 18, 688, 44);
            sharedSyncButton.Text = "Update now";
            sharedSyncButton.Location = new Point(10, 66);
            sharedSyncButton.Size = new Size(110, 28);
            sharedSyncButton.Click += delegate { SharedSync(false); };
            sharedBox.Controls.Add(sharedSyncButton);
            sharedAutoCheck.Text = "Update the waypoints when the program starts and every 30 minutes";
            sharedAutoCheck.AutoSize = true;
            sharedAutoCheck.Checked = Ctx.Settings.SharedAutoSync;
            sharedAutoCheck.Location = new Point(132, 72);
            sharedAutoCheck.CheckedChanged += delegate { Ctx.Settings.SharedAutoSync = sharedAutoCheck.Checked; MarkDirty(); };
            sharedBox.Controls.Add(sharedAutoCheck);
            Button world = Btn(sharedBox, "World Database Update...", 10, 100, 200, 28);
            world.Click += delegate { OpenWorldDb(); };
            Button share = Btn(sharedBox, "Share my points...", 218, 100, 150, 28);
            share.Click += delegate { ShareShared(); };
            sharedDbCheck.Text = "Apply the changes to the offline databases of HPG when the simulator is closed";
            sharedDbCheck.AutoSize = true;
            sharedDbCheck.Checked = Ctx.Settings.SharedDbPatch;
            sharedDbCheck.Location = new Point(10, 136);
            sharedDbCheck.CheckedChanged += delegate { Ctx.Settings.SharedDbPatch = sharedDbCheck.Checked; MarkDirty(); };
            sharedBox.Controls.Add(sharedDbCheck);
            sharedApplyButton.Text = "Apply now";
            sharedApplyButton.Location = new Point(444, 130);
            sharedApplyButton.Size = new Size(116, 28);
            sharedApplyButton.Click += delegate { SharedApplyDb(true); };
            sharedBox.Controls.Add(sharedApplyButton);
            sharedRestoreButton.Text = "Restore the originals";
            sharedRestoreButton.Location = new Point(566, 130);
            sharedRestoreButton.Size = new Size(130, 28);
            sharedRestoreButton.Click += delegate { SharedRestoreDb(); };
            sharedBox.Controls.Add(sharedRestoreButton);
            sharedInfo.AutoSize = false;
            sharedInfo.Location = new Point(444, 100);
            sharedInfo.Size = new Size(254, 28);
            sharedInfo.ForeColor = SystemColors.GrayText;
            sharedInfo.Text = SharedData.StatusLine();
            sharedBox.Controls.Add(sharedInfo);

            toolsOut.Multiline = true;
            toolsOut.ReadOnly = true;
            toolsOut.ScrollBars = ScrollBars.Vertical;
            toolsOut.Font = new Font("Consolas", 9F);
            toolsOut.Location = new Point(10, 490);
            toolsOut.Size = new Size(708, 72);
            page.Controls.Add(toolsOut);
        }

        // ---- shared data -------------------------------------------------------------------------------
        void SharedLog(SharedResult r)
        {
            foreach (string line in r.Lines) { ToolsLog(line); Ctx.Log(line); }
        }

        // downloads the published waypoints, writes the table in the work folder(s) and raises the counter the mission watches
        void SharedSync(bool automatic)
        {
            if (sharedRunning) return;
            sharedRunning = true;
            sharedSyncButton.Enabled = false;
            if (!automatic) ToolsLog("--- Shared data " + DateTime.Now.ToString("HH:mm:ss"));
            List<string> works = SharedData.TargetWorkFolders(Ctx.Settings.WorkFolder);
            bool patch = Ctx.Settings.SharedDbPatch;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SharedResult r = SharedData.Sync(SharedData.RepoBase, works);
                if (r.DataRev > 0) SimLink.Publish(SharedData.VarRev, r.DataRev);
                SharedResult d = null;
                bool simOn = Tools.SimulatorRunning();
                if (patch && !simOn && !r.FromCache) d = SharedData.ApplyDb(SharedData.FindDbFolders(), false);
                Ui(delegate
                {
                    // an automatic check that found nothing new stays quiet (the log keeps only the warnings and what changed)
                    if (!automatic || r.Written > 0 || !r.Ok || r.FromCache || r.Lines.Exists(delegate (string l) { return l.StartsWith("[WARN]", StringComparison.Ordinal); })) SharedLog(r);
                    if (d != null) SharedLog(d);
                    else if (patch && simOn && !automatic) ToolsLog("[INFO] Offline databases: the simulator is running; the changes are applied when it closes.");
                    sharedInfo.Text = SharedData.StatusLine();
                    sharedRunning = false;
                    sharedSyncButton.Enabled = true;
                });
            });
        }

        void SharedApplyDb(bool manual)
        {
            if (Tools.SimulatorRunning())
            {
                if (manual) MessageBox.Show("Microsoft Flight Simulator is running. Close it first: the databases are read by the tablet of HPG while it runs.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> folders = SharedData.FindDbFolders();
            if (folders.Count == 0) { ToolsLog("[INFO] Offline databases: no HPG database folder found."); return; }
            if (manual && MessageBox.Show("Apply the shared changes (and yours) to the offline databases of HPG?\n\n" + string.Join("\n", folders.ToArray()) +
                "\n\nThe original files are kept and \"Restore the originals\" puts them back.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ToolsLog("--- Offline databases " + DateTime.Now.ToString("HH:mm:ss"));
            SharedLog(SharedData.ApplyDb(folders, false));
            sharedInfo.Text = SharedData.DbStatusLine();
        }

        void SharedRestoreDb()
        {
            if (Tools.SimulatorRunning()) { MessageBox.Show("Microsoft Flight Simulator is running. Close it first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ToolsLog("--- Restore the offline databases " + DateTime.Now.ToString("HH:mm:ss"));
            SharedLog(SharedData.RestoreDb());
            sharedInfo.Text = SharedData.DbStatusLine();
        }

        // the window to enter hangars, hospitals and database changes; what was saved is used at once (the waypoint table is written again, the databases wait for Apply now)
        void OpenWorldDb()
        {
            using (WorldDbForm f = new WorldDbForm())
            {
                f.ShowDialog(this);
                if (f.WaypointsChanged) SharedSync(false);
                if (f.DatabasesChanged)
                {
                    sharedInfo.Text = "Database changes saved: press Apply now.";
                    if (Ctx.Settings.SharedDbPatch) ToolsLog("[INFO] Database changes saved: they are applied when the simulator is closed, or press Apply now.");
                }
            }
        }

        // the program cannot write to the repository: it puts the user's points on the clipboard and opens the page for a new GitHub issue, where the user pastes them
        void ShareShared()
        {
            if (MessageBox.Show("Share your own points?\n\nThe contents of your two files (My points, My database changes) are copied to the clipboard and the page for a new issue of the mission repository opens in your browser. " +
                "Paste the text there; you need a GitHub account. Nothing is sent by the program itself.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Clipboard.SetText(SharedData.ShareText());
                Process.Start(SharedData.IssueUrl + "?title=" + Uri.EscapeDataString("Shared waypoints and database changes"));
                ToolsLog("Your points are on the clipboard: paste them into the new issue.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ---- updates tab -------------------------------------------------------------------------
        // Opened by the button Check for updates of the window footer. The scope (both, only the mission, only the program) is chosen here.
        void BuildUpdatesTab()
        {
            TabPage page = new TabPage("Updates");
            updatesPage = page;
            tabs.TabPages.Add(page);

            Lbl(page, "This program is discontinued and will not be updated any more (2026-11-12). The mission and the voice packs are no longer updated from here; " +
                "only a newer version of the program itself can still be installed from this tab (check at the start of the program or on the button Check for updates; " +
                "nothing is downloaded without the question). Use the button The new Companion... to move to its successor.", 10, 10, 706, 62);

            GroupBox fileBox = new GroupBox();
            fileBox.Text = "Mission file to update";
            fileBox.Location = new Point(10, 78);
            fileBox.Size = new Size(708, 64);
            page.Controls.Add(fileBox);
            missionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            missionCombo.Location = new Point(10, 26);
            missionCombo.Size = new Size(596, 24);
            missionCombo.Items.Add("(the mission file to update is looked for when you press Check for updates)");
            missionCombo.SelectedIndex = 0;
            missionCombo.SelectedIndexChanged += delegate
            {
                MissionCopy c = missionCombo.SelectedItem as MissionCopy;
                if (c != null && !string.Equals(Ctx.Settings.MissionFile, c.Path, StringComparison.OrdinalIgnoreCase)) { Ctx.Settings.MissionFile = c.Path; MarkDirty(); }
            };
            fileBox.Controls.Add(missionCombo);
            Button browseMission = Btn(fileBox, "Browse...", 614, 24, 76, 27);
            browseMission.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Choose the R&E mission file (everywhere_all.json in a hpgmission folder)";
                    d.Filter = "Mission files|*.json|All files|*.*";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    Ctx.Settings.MissionFile = d.FileName; MarkDirty();
                    UpdLog("Mission file chosen: " + d.FileName + " (press Check for updates)");
                }
            };

            GroupBox whatBox = new GroupBox();
            whatBox.Text = "What to update";
            whatBox.Location = new Point(10, 150);
            whatBox.Size = new Size(708, 96);
            page.Controls.Add(whatBox);
            updBoth.Text = "Both: the mission first, then the program";
            updBoth.AutoSize = true;
            updBoth.Location = new Point(14, 22);
            updMission.Text = "Only the mission";
            updMission.AutoSize = true;
            updMission.Location = new Point(14, 44);
            updApp.Text = "Only the program";
            updApp.AutoSize = true;
            updApp.Location = new Point(14, 66);
            whatBox.Controls.Add(updBoth);
            whatBox.Controls.Add(updMission);
            whatBox.Controls.Add(updApp);
            updBoth.Checked = Ctx.Settings.UpdateWhat == "both";
            updMission.Checked = Ctx.Settings.UpdateWhat == "mission";
            updApp.Checked = Ctx.Settings.UpdateWhat == "app";
            EventHandler scopeChanged = delegate
            {
                if (suppress) return;
                Ctx.Settings.UpdateWhat = updMission.Checked ? "mission" : (updApp.Checked ? "app" : "both");
                MarkDirty();
            };
            updPackCheck.Text = "Also the default voice packs when newer ones exist (XML only)";
            updPackCheck.AutoSize = true;
            updPackCheck.Location = new Point(330, 22);
            updPackCheck.Checked = Ctx.Settings.UpdatePack;
            updPackCheck.CheckedChanged += delegate { if (suppress) return; Ctx.Settings.UpdatePack = updPackCheck.Checked; MarkDirty(); };
            whatBox.Controls.Add(updPackCheck);
            updBoth.CheckedChanged += scopeChanged;
            updMission.CheckedChanged += scopeChanged;
            updApp.CheckedChanged += scopeChanged;

            missionCombo.Enabled = false;
            browseMission.Enabled = false;
            updBoth.Enabled = false; updMission.Enabled = false; updPackCheck.Enabled = false;
            suppress = true;
            updApp.Checked = true;      // the only choice left (the check ignores the saved choice)
            suppress = false;
            updApp.Enabled = false;
            whatsNewButton.Text = "What's new...";
            whatsNewButton.Location = new Point(10, 256);
            whatsNewButton.Size = new Size(120, 30);
            whatsNewButton.Click += delegate { ShowWhatsNew(); };
            page.Controls.Add(whatsNewButton);
            autoUpdateCheck.Text = "Check for updates when the program starts";
            autoUpdateCheck.AutoSize = true;
            autoUpdateCheck.Checked = Ctx.Settings.AutoUpdateCheck;
            autoUpdateCheck.Location = new Point(144, 262);
            autoUpdateCheck.CheckedChanged += delegate { Ctx.Settings.AutoUpdateCheck = autoUpdateCheck.Checked; MarkDirty(); };
            page.Controls.Add(autoUpdateCheck);
            Button newCompanionButton = Btn(page, "The new Companion...", 548, 256, 170, 30);
            newCompanionButton.Click += delegate { ShowDiscontinued(); };
            updateInfo.AutoSize = true;
            updateInfo.Location = new Point(10, 296);
            updateInfo.ForeColor = SystemColors.GrayText;
            updateInfo.Text = "Program version " + AppInfo.Version;
            page.Controls.Add(updateInfo);

            updatesOut.Multiline = true;
            updatesOut.ReadOnly = true;
            updatesOut.ScrollBars = ScrollBars.Vertical;
            updatesOut.Font = new Font("Consolas", 9F);
            updatesOut.Location = new Point(10, 322);
            updatesOut.Size = new Size(708, 220);
            page.Controls.Add(updatesOut);
        }

        void UpdLog(string line)
        {
            updatesOut.AppendText(line + Environment.NewLine);
        }

        void SelectUpdatesTab()
        {
            if (updatesPage != null) tabs.SelectedTab = updatesPage;
        }

        Font boldFont;
        string updateSummary = "";

        // the state shown by the Updates tab (amber), the status line and the footer button (amber text on a light fill) while something newer is published
        void SetUpdateState(string info, bool available)
        {
            updatesAvailable = available;
            if (boldFont == null) boldFont = new Font(Font, FontStyle.Bold);
            bool good = !available && updChecked && updGood && !updateRunning;
            updateInfo.Text = info;
            updateInfo.ForeColor = available ? Color.FromArgb(170, 70, 0) : (good ? Color.FromArgb(0, 110, 40) : SystemColors.GrayText);
            updateInfo.Font = available || good ? boldFont : Font;
            // the footer button says what was found: Update: mission / program / both, Up to date, or the plain Check for updates
            if (updateRunning) updateButton.Text = "Checking...";
            else if (available)
            {
                int count = (updMissionNew ? 1 : 0) + (updAppNew ? 1 : 0) + (updPackNew ? 1 : 0);
                updateButton.Text = count >= 3 ? "Update: all" : (count == 0 ? "Update available" : "Update: " + (updMissionNew ? "mission" : "") + (updMissionNew && (updAppNew || updPackNew) ? "+" : "") + (updAppNew ? "program" : "") + (updAppNew && updPackNew ? "+" : "") + (updPackNew ? "pack" : ""));
            }
            else if (good) updateButton.Text = "Up to date";
            else updateButton.Text = "Check for updates";
            if (available)
            {
                updateButton.FlatStyle = FlatStyle.Flat;
                updateButton.FlatAppearance.BorderColor = Color.FromArgb(200, 100, 0);
                updateButton.BackColor = Color.FromArgb(255, 232, 170);
                updateButton.ForeColor = Color.FromArgb(150, 55, 0);
                updateButton.Font = boldFont;
            }
            else
            {
                updateButton.FlatStyle = FlatStyle.Standard;
                updateButton.UseVisualStyleBackColor = true;
                updateButton.ForeColor = SystemColors.ControlText;
                updateButton.Font = Font;
            }
            tabs.Invalidate();
        }

        // the check is over (or stopped): the button works again and the status line shows the news, if any are still waiting
        void UpdateIdle()
        {
            updateRunning = false;
            updateButton.Enabled = true;
            SetUpdateState(updatesAvailable ? updateSummary : updateIdleText, updatesAvailable);
        }

        // the result of a check, for the status line, the footer button and the Updates tab
        void RememberCheck(bool missionNew, bool appNew, bool packNew, string summary, string upToDate, bool allRead)
        {
            updChecked = true;
            updMissionNew = missionNew;
            updAppNew = appNew;
            updPackNew = packNew;
            updGood = !missionNew && !appNew && !packNew && allRead;
            if (missionNew || appNew || packNew) updateSummary = summary;
            else updateIdleText = allRead ? upToDate : "Nothing newer found, but a source could not be read: see the log below";
        }

        void ToolsLog(string line)
        {
            toolsOut.AppendText(line + Environment.NewLine);
        }

        void RefreshTools()
        {
            string keepWork = (workCombo.SelectedItem as WorkFolderInfo) != null ? ((WorkFolderInfo)workCombo.SelectedItem).Path : Ctx.Settings.WorkFolder;
            workCombo.Items.Clear();
            List<WorkFolderInfo> works = Tools.FindWorkFolders(Ctx.Settings.WorkFolder);
            foreach (WorkFolderInfo w in works) workCombo.Items.Add(w);
            for (int i = 0; i < workCombo.Items.Count; i++)
                if (string.Equals(((WorkFolderInfo)workCombo.Items[i]).Path, keepWork, StringComparison.OrdinalIgnoreCase)) { workCombo.SelectedIndex = i; break; }
            if (workCombo.SelectedIndex < 0 && workCombo.Items.Count > 0) workCombo.SelectedIndex = 0;
            UpdateWorkInfo();

            indexCombo.Items.Clear();
            foreach (string t in Tools.FindIndexTools(Ctx.Settings.IndexTool)) indexCombo.Items.Add(t);
            if (indexCombo.Items.Count > 0)
            {
                int pick = 0;
                for (int i = 0; i < indexCombo.Items.Count; i++)
                    if (((string)indexCombo.Items[i]).IndexOf("hpg-airbus-h145\\", StringComparison.OrdinalIgnoreCase) >= 0) { pick = i; break; }
                indexCombo.SelectedIndex = pick;
            }
            indexButton.Enabled = indexCombo.Items.Count > 0;
            AppendLog("[INFO] Tools: " + works.Count + " work folder(s), " + indexCombo.Items.Count + " index tool(s) found");
        }

        void UpdateWorkInfo()
        {
            WorkFolderInfo w = workCombo.SelectedItem as WorkFolderInfo;
            workInfo.Text = w == null ? "No work folder found: use Browse." : w.Path;
        }

        void OpenWork()
        {
            WorkFolderInfo w = workCombo.SelectedItem as WorkFolderInfo;
            if (w == null) { MessageBox.Show("No work folder found: use Browse.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { Process.Start("explorer.exe", "\"" + w.Path + "\""); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void CleanWork()
        {
            WorkFolderInfo w = workCombo.SelectedItem as WorkFolderInfo;
            if (w == null) { MessageBox.Show("No work folder found: use Browse.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (Tools.SimulatorRunning() &&
                MessageBox.Show("Microsoft Flight Simulator is running. Files in use cannot be deleted and the mission can write them again.\n\nContinue anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            List<CleanGroup> groups = Tools.Scan(w.Path);
            if (groups.Count == 0) { MessageBox.Show("There is nothing to clean in this folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            List<string> files = new List<string>();
            bool backup;
            using (CleanDialog d = new CleanDialog(groups))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                backup = d.BackupFirst;
                foreach (CleanGroup g in d.Selected) files.AddRange(g.Files);
            }
            ToolsLog("--- Clean up " + DateTime.Now.ToString("HH:mm:ss") + " " + w.Path);
            if (backup)
            {
                try
                {
                    string zip = Tools.Backup(w.Path, files, Path.Combine(Ctx.BaseDir, "backups"));
                    ToolsLog("Backup: " + zip);
                }
                catch (Exception ex)
                {
                    ToolsLog("[ERROR] the backup failed, nothing was deleted: " + ex.Message);
                    return;
                }
            }
            List<string> failed = new List<string>();
            int n = Tools.Delete(files, failed);
            ToolsLog("Deleted " + n + " of " + files.Count + " files.");
            foreach (string f in failed) ToolsLog("[not deleted] " + f);
            RefreshTools();
        }

        void UpdateIndex(bool automatic)
        {
            string cmd = indexCombo.SelectedItem as string;
            if (cmd == null || indexRunning) return;
            indexRunning = true;
            indexButton.Enabled = false;
            ToolsLog("--- Update mission index " + DateTime.Now.ToString("HH:mm:ss") + (automatic ? " (automatic, at start) " : " ") + cmd);
            ThreadPool.QueueUserWorkItem(delegate
            {
                int rc = -1;
                string error = null;
                try { rc = Tools.RunIndex(cmd, delegate (string line) { Ui(delegate { ToolsLog(line); }); }); }
                catch (Exception ex) { error = ex.Message; }
                string file = Tools.IndexFileOf(cmd);
                string fileInfo = "";
                if (file != null)
                {
                    FileInfo fi = new FileInfo(file);
                    fileInfo = "Index file: " + file + " (" + (fi.Length / 1024) + " KB, written " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + ")";
                }
                Ui(delegate
                {
                    if (error != null) ToolsLog("[ERROR] " + error);
                    else ToolsLog("Finished, exit code " + rc + ".");
                    if (fileInfo.Length > 0) ToolsLog(fileInfo);
                    ToolsLog("If a flight is already running, press REFRESH at the top of the tablet to pick up the new index.");
                    indexRunning = false;
                    indexButton.Enabled = indexCombo.Items.Count > 0;
                });
            });
        }

        // ---- the successor (this program is discontinued, user decision of 2026-11-12) ---------------
        bool discontinuedOpen;

        // at every start: the notice itself when the window is shown, else a message in the corner that opens it
        void AnnounceDiscontinued()
        {
            Ui(delegate
            {
                if (Visible) ShowDiscontinued();
                else UpdatePopup.Announce(AppInfo.Name + ": discontinued", "This program will not be updated any more. Click to choose what to do.", "Open", delegate { ShowWindow(); ShowDiscontinued(); });
            });
        }

        void ShowDiscontinued()
        {
            if (discontinuedOpen) return;
            discontinuedOpen = true;
            try
            {
                using (NewCompanionForm f = new NewCompanionForm())
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    AppendLog("[INFO] New Companion: " + f.NewExe + (f.UninstallThis ? ", this program is uninstalled" : ", this program stays installed"));
                    try { NewCompanion.StartNew(f.NewExe); }
                    catch (Exception ex) { MessageBox.Show(this, "The new Companion is installed but could not be started: " + ex.Message + "\n\n" + f.NewExe, Text); }
                    if (f.UninstallThis)
                    {
                        try { NewCompanion.StartUninstallThis(); }
                        catch (Exception ex) { MessageBox.Show(this, "This program could not be uninstalled: " + ex.Message, Text); }
                    }
                    else AppendLog("[INFO] Simulator start entry: " + NewCompanion.AutostartOff());
                    ExitApp(false);
                }
            }
            finally { discontinuedOpen = false; }
        }

        // ---- updates -----------------------------------------------------------------------------
        // Check for updates (user click only): the installed mission file and the index, the published mission, the published program.
        void CheckUpdates(bool automatic)
        {
            if (updateRunning) return;
            updateRunning = true;
            updateButton.Enabled = false;
            if (!automatic) SelectUpdatesTab();             // the button of the footer opens the Updates tab
            SetUpdateState("Checking...", updatesAvailable);
            Ctx.Settings.LastUpdateCheck = DateTime.UtcNow.ToString("o");
            try { Ctx.Settings.Save(Ctx.SettingsPath); } catch (Exception) { }
            string chosen = missionCombo.SelectedItem is MissionCopy ? ((MissionCopy)missionCombo.SelectedItem).Path : Ctx.Settings.MissionFile;
            string indexCmd = indexCombo.SelectedItem as string;
            if (!automatic) UpdLog("--- Check for updates " + DateTime.Now.ToString("HH:mm:ss"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> lines = new List<string>();
                List<MissionCopy> copies = new List<MissionCopy>();
                MissionCopy target = null;
                RemoteMission rm = null;
                RemoteApp ra = null;
                bool indexKnown = false, indexListsFile = false;
                lines.Add("The mission and the voice packs are no longer updated by this program (discontinued 2026-11-12); only the program itself can still be updated.");
                try
                {
                    ra = Updates.ReadRemoteApp(Updates.ReleasesApi);
                    lines.Add("Published program: " + ra.Tag + (ra.AssetUrl == null ? " (no setup file in the release)" : "") + ", this one is " + AppInfo.Version);
                }
                catch (Exception ex) { lines.Add("[ERROR] the published program could not be read: " + ex.Message); }
                List<RemotePack> rpks = new List<RemotePack>();       // stays empty: the voice packs are not updated here any more

                bool missionNew = rm != null && target != null && rm.Release.CompareTo(target.Release) > 0;
                bool appNew = ra != null && ra.AssetUrl != null && Updates.AppIsNewer(ra);
                string scope = "app";          // the mission and the voice packs are not updated by this program any more
                bool doMission = missionNew && scope != "app";
                bool doApp = appNew && scope != "mission";
                List<RemotePack> packsNew = new List<RemotePack>();
                string packsKept = "";
                foreach (RemotePack k in rpks)
                {
                    string packWhy;
                    if (Updates.PackIsNewer(k, out packWhy)) packsNew.Add(k);
                    else
                    {
                        if (packWhy.Length > 0) lines.Add("The published voice pack " + k.Info.Name + " is not installed: " + packWhy + ".");
                        if (k.Info.Usable) packsKept += ", voice pack " + k.Info.Name + " " + (VoicePacks.Find(k.Info.Name) != null ? VoicePacks.Find(k.Info.Name).Version : k.Info.Version);
                    }
                }
                bool packNew = packsNew.Count > 0;
                bool doPack = packNew && !doApp;            // a new program brings its own pack with the setup
                if (packNew && doApp) lines.Add("The voice pack comes with the new program.");
                if (missionNew && !doMission) lines.Add("The mission is newer but the option Only the program is selected: it is not installed.");
                if (appNew && !doApp) lines.Add("The program is newer but the option Only the mission is selected: it is not installed.");
                if (rm != null && target == null) lines.Add("No R&E mission file was identified: choose it with Browse and check again.");
                if (target != null && indexKnown && !indexListsFile && !missionNew)
                    lines.Add("[NOTICE] the mission index does not list the release of your mission file: press Update mission index.");
                if (!doMission && !doApp && !doPack) lines.Add(missionNew || appNew ? "Nothing to install with the chosen option." : "Nothing to update.");
                List<string> whatList = new List<string>();
                if (doMission) whatList.Add("mission " + rm.Release);
                if (doApp) whatList.Add("program " + ra.Tag);
                if (doPack) foreach (RemotePack k in packsNew) whatList.Add("voice pack " + k.Info.Name + " " + k.Info.Version);
                string newWhat = string.Join(", ", whatList.ToArray());
                bool allRead = ra != null && !lines.Exists(delegate (string x) { return x.StartsWith("[ERROR]"); });
                string upToDate = "UP TO DATE: program " + AppInfo.Version
                    + (doPack ? "" : packsKept)
                    + (missionNew && !doMission ? " (a newer mission exists but is not selected)" : "") + (appNew && !doApp ? " (a newer program exists but is not selected)" : "");

                Ui(delegate
                {
                    RememberCheck(doMission, doApp, doPack, "UPDATE AVAILABLE: " + newWhat, upToDate, allRead);
                    if (automatic)
                    {
                        bool any = doMission || doApp || doPack;
                        updatesAvailable = any;
                        if (any)
                        {
                            foreach (string l in lines) UpdLog(l);
                            updateSummary = "UPDATE AVAILABLE: " + newWhat;
                            AppendLog("[INFO] Updates: " + newWhat + " available");
                            // a message of its own in the corner of the screen: the Windows balloon is not always shown
                            UpdatePopup.Announce(AppInfo.Name + ": update available", "New: " + newWhat + ". Open the Updates tab to see what is new and install it.", "Open Updates", delegate { ShowWindow(); CheckUpdates(false); });
                        }
                        else AppendLog("[INFO] Updates: nothing newer" + (lines.Exists(delegate (string x) { return x.StartsWith("[ERROR]"); }) ? " (a source could not be read, see the Updates tab: press Check for updates)" : ""));
                        UpdateIdle();
                        return;
                    }
                    foreach (string l in lines) UpdLog(l);
                    if (copies.Count > 0)
                    {
                        missionCombo.Items.Clear();
                        foreach (MissionCopy c in copies) missionCombo.Items.Add(c);
                        if (target != null) missionCombo.SelectedItem = target;
                    }
                    updatesAvailable = doMission || doApp || doPack;
                    updateSummary = "UPDATE AVAILABLE: " + newWhat;
                    bool go = false;
                    if (doMission || doApp || doPack)
                    {
                        StringBuilder q = new StringBuilder();
                        if (doMission) q.AppendLine("Mission: " + rm.Release + " is available (yours: " + (target.Missing ? "none installed" : target.Release.ToString()) + ").");
                        if (doApp) q.AppendLine("Program: " + ra.Tag + " is available (yours: " + AppInfo.Version + ").");
                        if (doPack) foreach (RemotePack k in packsNew) q.AppendLine("Voice pack " + k.Info.Name + ": version " + k.Info.Version + " is available (yours: " + (VoicePacks.Find(k.Info.Name) != null ? VoicePacks.Find(k.Info.Name).Version.ToString() : "none") + "). Only the XML is replaced; the old one goes to voicepacks\\obsolete.");
                        q.AppendLine();
                        if (doMission && target.Missing) q.AppendLine("The mission file does not exist yet: it will be created in " + Path.GetDirectoryName(target.Path) + ".\n");
                        else if (doMission && !target.Release.HasRelease) q.AppendLine("The mission file to replace is a file without a release number (" + target.Path + ").\n");
                        if (Tools.SimulatorRunning()) q.AppendLine("Microsoft Flight Simulator is running. The mission file can be replaced now; when the update is done, if the flight has started, press REFRESH at the top of the tablet.\n");
                        q.Append("Download and install now?\n\n");
                        if (doMission) q.Append("The mission file is replaced first (the old one is kept beside it with the extension .json.bak) and the mission index is updated. ");
                        if (doApp) q.Append("Then the program is updated: it closes, the setup installs the new version and starts it again.");
                        StringBuilder notes = new StringBuilder();
                        if (doMission) notes.AppendLine("WHAT'S NEW IN THE MISSION, " + rm.Release + "\n\n" + (string.IsNullOrEmpty(rm.Notes) ? "No release notes are published for this source." : Updates.PlainNotes(rm.Notes)) + "\n");
                        if (doApp) notes.AppendLine("WHAT'S NEW IN THE PROGRAM, " + ra.Tag + "\n\n" + (string.IsNullOrEmpty(ra.Notes) ? "No release notes are published for this version." : Updates.PlainNotes(ra.Notes)) + "\n");
                        if (doPack) foreach (RemotePack k in packsNew) notes.AppendLine("WHAT'S NEW IN THE VOICE PACK, " + k.Info.Name + " " + k.Info.Version + "\n\n" + (string.IsNullOrEmpty(k.Notes) ? "No notes are published for this pack." : Updates.PlainNotes(k.Notes)) + "\n");
                        go = NotesForm.Ask(this, Text + " - update available", q.ToString().TrimEnd(), notes.ToString()) == DialogResult.Yes;
                    }
                    if (!go) { UpdateIdle(); return; }
                    InstallUpdates(doMission ? target : null, doMission ? rm : null, doApp ? ra : null, doPack ? packsNew : null, indexCmd);
                });
            });
        }

        // the release notes of the mission and of the program as published on GitHub, also when nothing is newer
        void ShowWhatsNew()
        {
            whatsNewButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                StringBuilder n = new StringBuilder();
                try
                {
                    string label;
                    string text = Updates.ReadReleaseNotes(Updates.ReleasesApi, out label);
                    n.AppendLine("PROGRAM " + label + " (this one is " + AppInfo.Version + ")\n\n" + (text.Length > 0 ? text : "The release has no notes."));
                }
                catch (Exception ex) { n.AppendLine("PROGRAM\n\nThe notes of the program could not be read: " + ex.Message); }
                Ui(delegate
                {
                    whatsNewButton.Enabled = true;
                    NotesForm.View(this, Text + " - what's new", "Release notes of the latest published version of the program (GitHub).", n.ToString());
                });
            });
        }

        // mission first (download, backup, replace, index), then the program (download, setup, exit)
        void InstallUpdates(MissionCopy target, RemoteMission rm, RemoteApp ra, List<RemotePack> rpks, string indexCmd)
        {
            updateInfo.Text = "Updating...";
            string tmp = Path.Combine(Path.GetTempPath(), "hmc-update");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                string setup = null;
                try
                {
                    Directory.CreateDirectory(tmp);
                    if (target != null && rm != null)
                    {
                        string file = Path.Combine(tmp, "everywhere_all-" + rm.Release.Major + "." + rm.Release.Minor + ".json");
                        long shown = -1;
                        Ui(delegate { UpdLog("Downloading the mission " + rm.Release + " (" + rm.Source + ")..."); });
                        Updates.DownloadMission(rm, file, delegate (long done, long total)
                        {
                            long step = total > 0 ? done * 4 / total : 0;
                            if (step != shown) { shown = step; Ui(delegate { UpdLog("  " + (done / 1024) + " of " + (total / 1024) + " KB"); }); }
                        });
                        string backup = Updates.ReplaceInstalled(target.Path, file, null);
                        try { File.Delete(file); } catch (Exception) { }
                        Ui(delegate { UpdLog((backup == null ? "Mission file created: " : "Mission file replaced: ") + target.Path); if (backup != null) UpdLog("The previous file is kept beside it: " + backup); });
                        if (indexCmd != null)
                        {
                            Ui(delegate { UpdLog("Updating the mission index..."); });
                            int rc = Tools.RunIndex(indexCmd, delegate (string line) { Ui(delegate { UpdLog(line); }); });
                            Ui(delegate { UpdLog("Index tool finished, exit code " + rc + ". If a flight is already running, press REFRESH at the top of the tablet to load the new mission."); });
                        }
                        else Ui(delegate { UpdLog("[NOTICE] no index tool is known: run Update Mission Index yourself."); });
                    }
                    if (rpks != null)
                    {
                        foreach (RemotePack rpk in rpks)
                        {
                            if (rpk == null || rpk.File == null || !File.Exists(rpk.File)) continue;
                            // the XML only: Install keeps the old version in voicepacks\obsolete (a newer version is never asked about)
                            string msg = VoicePacks.Install(rpk.File, delegate (string q) { return true; });
                            Ui(delegate { UpdLog("Voice pack: " + msg); FillPacks(); });
                        }
                    }
                    if (ra != null)
                    {
                        setup = Path.Combine(tmp, "HPGMissionCompanionSetup-" + ra.Tag + ".exe");
                        long shown = -1;
                        Ui(delegate { UpdLog("Downloading the program " + ra.Tag + "..."); });
                        Updates.DownloadSetup(ra, setup, delegate (long done, long total)
                        {
                            long step = total > 0 ? done * 4 / total : 0;
                            if (step != shown) { shown = step; Ui(delegate { UpdLog("  " + (done / 1024) + " of " + (total / 1024) + " KB"); }); }
                        });
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                Ui(delegate
                {
                    if (error != null)
                    {
                        UpdLog("[ERROR] " + error);
                        UpdLog("The update stopped here: what was already replaced is kept, press Check for updates to go on.");
                        UpdateIdle();
                        return;
                    }
                    if (setup == null)
                    {
                        UpdLog("Update finished.");
                        updatesAvailable = false;
                        updChecked = true; updMissionNew = false; updAppNew = false; updPackNew = false; updGood = true;
                        updateIdleText = "UP TO DATE: the update is installed." + (Tools.SimulatorRunning() ? " A flight is running: press REFRESH at the top of the tablet." : "");
                        UpdateIdle();
                        return;
                    }
                    try
                    {
                        if (dirty) SaveSettings();
                        UpdLog("Starting the setup: this program closes and starts again.");
                        Process.Start(Updates.SetupStart(setup, Ctx.BaseDir));
                        ExitApp(false);
                        Environment.Exit(0);       // this program must be gone when the setup starts the new one (the port is free, nothing keeps the files)
                    }
                    catch (Exception ex)
                    {
                        UpdLog("[ERROR] the setup could not be started: " + ex.Message + " (the file is " + setup + ")");
                        UpdateIdle();
                    }
                });
            });
        }

        // ---- about tab ---------------------------------------------------------------------------
        void BuildAboutTab()
        {
            TabPage page = new TabPage("About");
            tabs.TabPages.Add(page);
            PictureBox pic = new PictureBox();
            try { pic.Image = new Icon(appIcon, 64, 64).ToBitmap(); } catch (Exception) { }
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.Location = new Point(14, 14);
            pic.Size = new Size(64, 64);
            page.Controls.Add(pic);
            Label title = Lbl(page, AppInfo.Name, 96, 18, 0, 0);
            title.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
            Lbl(page, "Version " + AppInfo.Version, 98, 56, 0, 0);

            string built = "";
            try { built = File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm"); } catch (Exception) { }
            Lbl(page, "Build date:   " + built, 14, 104, 0, 0);
            Lbl(page, "Author:   " + AppInfo.Author, 14, 128, 0, 0);
            Lbl(page, "Discord:", 14, 152, 0, 0);
            LinkLabel invite = new LinkLabel();
            invite.Text = "invitation";
            invite.AutoSize = true;
            invite.Location = new Point(72, 152);
            invite.LinkClicked += delegate { try { System.Diagnostics.Process.Start(AppInfo.DiscordInvite); } catch (Exception) { } };
            page.Controls.Add(invite);
            LinkLabel channel = new LinkLabel();
            channel.Text = "channel of the program";
            channel.AutoSize = true;
            channel.Location = new Point(140, 152);
            channel.LinkClicked += delegate { try { System.Diagnostics.Process.Start(AppInfo.DiscordChannel); } catch (Exception) { } };
            page.Controls.Add(channel);
            Lbl(page, "GitHub:", 330, 152, 0, 0);
            LinkLabel gitApp = new LinkLabel();
            gitApp.Text = "program";
            gitApp.AutoSize = true;
            gitApp.Location = new Point(384, 152);
            gitApp.LinkClicked += delegate { try { System.Diagnostics.Process.Start(AppInfo.GitHubApp); } catch (Exception) { } };
            page.Controls.Add(gitApp);
            LinkLabel gitMission = new LinkLabel();
            gitMission.Text = "mission";
            gitMission.AutoSize = true;
            gitMission.Location = new Point(444, 152);
            gitMission.LinkClicked += delegate { try { System.Diagnostics.Process.Start(AppInfo.GitHubMission); } catch (Exception) { } };
            page.Controls.Add(gitMission);

            Lbl(page, "A companion program for the HPG H145 missions: voices for the mission actors (Windows and Piper voices, volume, radio effect, voice packs), " +
                "the mission work folder and the mission index.", 14, 188, 700, 36);
            Lbl(page, "Credits and licences", 14, 228, 0, 0).Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            Lbl(page, "Text to speech: the voices installed in Windows, and Piper (github.com/rhasspy/piper, MIT licence) with the voices of the rhasspy/piper-voices " +
                "repository. Every Piper voice has its own licence: read voices\\piper\\<voice>.MODEL_CARD.txt before using a voice outside your own PC. " +
                "Piper uses eSpeak NG (GPL) for the phonemes. The engine and the voices are downloaded from their sources when you press Install; they are not part of this program. " +
                "The Swiss German voices marked (STT4SG) come from the research demo of FHNW (stt4sg.fhnw.ch, non-commercial licence): they run on a server on the internet, and the text is sent there only when you choose one of those voices. " +
                "Full list of the licences: THIRD_PARTY.md in the repository.",
                14, 250, 700, 92);
            Lbl(page, "Contributors", 14, 346, 0, 0).Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            Lbl(page, "Daikan (GitHub d41k4n): co-author of the missions, testing and bug reports; the German voice pack, the Swiss German voices, the separate hiss of radio and external radio calls, " +
                "the squelch-tail recordings, the audio cache tools. kekkeruuu: co-author of the missions.",
                14, 366, 700, 32);

            Lbl(page, "Script developer guide (tags, voice packs, the say tag, versions, testing)", 14, 402, 0, 0).Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            TextBox guide = new TextBox();
            guide.Multiline = true;
            guide.ReadOnly = true;
            guide.ScrollBars = ScrollBars.Vertical;
            guide.WordWrap = true;
            guide.Font = new Font("Consolas", 8.5F);
            guide.BackColor = Color.White;
            guide.Location = new Point(14, 424);
            guide.Size = new Size(700, 124);
            guide.Text = LoadGuide();
            guide.Select(0, 0);
            page.Controls.Add(guide);
        }

        // the developer guide is compiled into the program (docs\DEVELOPER_GUIDE.md)
        static string LoadGuide()
        {
            try
            {
                using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("DEVELOPER_GUIDE.md"))
                {
                    if (s == null) return "(the guide is not in this build; see docs\\DEVELOPER_GUIDE.md in the repository)";
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
            }
            catch (Exception ex) { return "(cannot read the guide: " + ex.Message + ")"; }
        }

        // ---- closing -----------------------------------------------------------------------------
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                allowShow = false;
                tray.ShowBalloonTip(2000, AppInfo.Name, "Still running here. Right-click the icon to exit.", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }

        void ExitApp(bool ask)
        {
            if (ask && dirty)
            {
                DialogResult r = MessageBox.Show("The settings were modified.\n\nSave them before leaving?\n\nYes = save and exit, No = exit without saving, Cancel = stay.", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) SaveSettings();
            }
            reallyExit = true;
            // whatever blocks while closing (a native call of the simulator link, a Piper process), the program is gone after four seconds
            Thread watchdog = new Thread(delegate () { Thread.Sleep(4000); Environment.Exit(0); });
            watchdog.IsBackground = true;
            watchdog.Start();
            tick.Stop();
            SimLink.Stop();
            StopServer();
            PiperEngine.StopAll();
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }
    }
}
