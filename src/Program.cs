// HPG Mission Companion
// A voice service for the HPG missions: text to speech with the Windows voices and with Piper, one voice per mission actor.
//
// Command line:
//   (none)                   show the window and start the service
//   --minimized              start hidden (notification area); used when the simulator starts the program
//   --exit-with-sim          leave when Microsoft Flight Simulator closes
//   --silent                 do everything but the sound (for checks)
//   --log-file <path>        also write the log to a file
//   --port <n> (tests only: the port of the mission is fixed to 5997 and the settings never change it)  --audio <dir>
//   --list-file <path>       write the voices to a file and exit
//   --selftest <wav> --text "<tagged text>"   synthesize the first part of the text to a wav file and exit
//   --autostart on|off       add or remove this program in the EXE.xml of the simulators found (and exit)
//   --expand <report> --text "<tagged text>" | --lines <file> [--repeat N] [--seed N]   (lines: @watch NAME VALUE, @settings k=v) what a mission text becomes (voice pack expansion and tags), no audio
//   --atc-test <report> [--seconds N]   read ATC ID, ATC AIRLINE and ATC FLIGHT NUMBER of the aircraft from the running simulator (call sign test)
//   --oneclick <report> [--profile <file>] [--no-download]   apply a settings profile (default: the recommended one), download the Piper engine and voices it needs, keep the old settings in backups\settings; the report grows line by line
//   --install-pack <xml>     install a voice pack (an older version of the same pack is archived in voicepacks\obsolete)
//   --packs-check <report>   list the voice packs of the voicepacks folder, their problems and what they lack against the base pack EN
//   --queue-test <report> [--log <hmc log>] [--pack <EN.xml>]   the decisions of the speech scheduler (lanes, priorities) on made-up cases; with a log, its lines replayed with the old and the new queue
//   --shared-test <report> --source <folder|url> --state <folder> [--work <folder>] [--db <folder>] [--apply-db] [--restore-db] [--dry-run]   the shared data: the waypoint table and the changes to the offline databases (test folders only)
//   --world-test <report> --state <folder>   the logic of the window World Database Update (unicode check, records, the user's files); --world-shot <png base> --state <folder>   a picture of each tab
//   --pack-template <xml> --lang IT [--name IT] [--report <file>]   write the base pack as the start of a translation
//   --pack-add-missing <pack> [--report <file>]   add the English texts of the keys that a pack lacks to its file (the old file goes to voicepacks\obsolete)
//   --simlink-check <report> find SimConnect.dll, try one connection, write the result and exit
//   --sim-root <dir>       (test) use this folder instead of the simulator folders for the start with the simulator
//   --test-autostart <on|off> (with --sim-root) add or remove the entry and exit
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// keep in step with AppInfo.Version in Core.cs
[assembly: AssemblyTitle("HPG Mission Companion")]
[assembly: AssemblyProduct("HPG Mission Companion")]
[assembly: AssemblyVersion("0.40.0.0")]
[assembly: AssemblyFileVersion("0.40.0.0")]

namespace HPGMissionCompanion
{
    internal static class Program
    {
        // a second start of the program tells the first one to show its window (the mutex only lets one run)
        internal static EventWaitHandle ShowEvent;

        [STAThread]
        static int Main(string[] args)
        {
            string logFile = Option(args, "--log-file");
            string listFile = Option(args, "--list-file");
            string selftest = Option(args, "--selftest");
            string simRoot = Option(args, "--sim-root");
            string autostartTest = Option(args, "--test-autostart");
            Ctx.Silent = Has(args, "--silent");
            Ctx.Settings = Settings.Load(Ctx.SettingsPath);
            string portArg = Option(args, "--port");
            int port;
            if (portArg != null && int.TryParse(portArg, out port)) Ctx.Settings.Port = port;
            string audioArg = Option(args, "--audio");
            Ctx.AudioDir = audioArg != null ? audioArg : (string.IsNullOrEmpty(Ctx.Settings.AudioFolder) ? Path.Combine(Ctx.BaseDir, "audio") : Ctx.Settings.AudioFolder);

            if (Has(args, "--purge-cache"))
            {
                long bytes;
                int count = Synth.PurgeCache(out bytes);
                string sizeStr = bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.1", CultureInfo.InvariantCulture) + " MB" : ((bytes + 1023) / 1024) + " KB";
                Console.WriteLine("Purged audio cache: " + count + " file(s), " + sizeStr + " freed.");
                return 0;
            }

            if (autostartTest != null && simRoot != null)
            {
                foreach (SimTarget t in SimAutostart.Detect(simRoot))
                {
                    if (autostartTest == "on") SimAutostart.Enable(t, Application.ExecutablePath, "--minimized --exit-with-sim");
                    else SimAutostart.Disable(t);
                }
                return 0;
            }

            // --oneclick <report>: the setup in one click without the window (the installer runs it)
            string oneClick = Option(args, "--oneclick");
            if (oneClick != null) return Profiles.RunCli(oneClick, Option(args, "--profile"), !Has(args, "--no-download"), simRoot);

            if (Has(args, "--install-engine") || Option(args, "--install-voice") != null)
            {
                StringBuilder rep = new StringBuilder();
                string reportPath = Option(args, "--report") ?? Path.Combine(Ctx.BaseDir, "install-report.txt");
                int rc = 0;
                try
                {
                    long lastShown = -1;
                    Action<long, long> prog = delegate (long done, long total)
                    {
                        long mb = done / (4 * 1048576);
                        if (mb != lastShown) { lastShown = mb; rep.AppendLine("  " + (done / 1048576) + " of " + (total / 1048576) + " MB"); }
                    };
                    if (Has(args, "--install-engine"))
                    {
                        rep.AppendLine("installing the engine");
                        PiperInstaller.InstallEngine(prog);
                        rep.AppendLine("engine installed: " + Ctx.PiperExe + " exists=" + Ctx.PiperInstalled);
                    }
                    string vkey = Option(args, "--install-voice");
                    if (vkey != null)
                    {
                        string json = File.Exists(Ctx.CatalogPath) ? File.ReadAllText(Ctx.CatalogPath, Encoding.UTF8) : Net.GetString(PiperInstaller.CatalogUrl);
                        Directory.CreateDirectory(Path.GetDirectoryName(Ctx.CatalogPath));
                        File.WriteAllText(Ctx.CatalogPath, json, new UTF8Encoding(false));
                        CatalogVoice found = null;
                        foreach (CatalogVoice c in PiperInstaller.LoadCatalog(json)) if (c.Key == vkey) found = c;
                        if (found == null) throw new InvalidOperationException("voice " + vkey + " not in the catalog");
                        rep.AppendLine("installing the voice " + vkey);
                        PiperInstaller.InstallVoice(found, prog);
                        rep.AppendLine("voice installed: " + PiperInstaller.IsInstalled(found));
                    }
                }
                catch (Exception ex) { rep.AppendLine("ERROR: " + ex); rc = 1; }
                File.WriteAllText(reportPath, rep.ToString());
                return rc;
            }

            // --autostart on|off: adds or removes this program in the EXE.xml of the simulators found (same as the Service tab checkbox)
            string autostart = Option(args, "--autostart");
            if (autostart != null)
            {
                StringBuilder rep = new StringBuilder();
                string reportPath = Option(args, "--report") ?? Path.Combine(Ctx.BaseDir, "autostart-report.txt");
                List<string> a = new List<string>();
                if (Ctx.Settings.StartMinimized) a.Add("--minimized");
                if (Ctx.Settings.ExitWithSim) a.Add("--exit-with-sim");
                string cmdline = string.Join(" ", a.ToArray());
                foreach (SimTarget t in SimAutostart.Detect(simRoot))
                {
                    try
                    {
                        if (autostart == "on") SimAutostart.Enable(t, Application.ExecutablePath, cmdline); else SimAutostart.Disable(t);
                        rep.AppendLine(autostart + ": " + t.ExeXml + " (" + cmdline + ")");
                    }
                    catch (Exception ex) { rep.AppendLine("ERROR " + t.ExeXml + ": " + ex.Message); }
                }
                if (autostart == "on") { Ctx.Settings.StartWithSim = true; Ctx.Settings.Save(Ctx.SettingsPath); }
                File.WriteAllText(reportPath, rep.ToString());
                return 0;
            }
            // tools, for checks: --scan-work <dir> | --clean-work <dir> --groups debug,test [--no-backup] | --run-index <cmd>   (all with --report <path>)
            string scanWork = Option(args, "--scan-work");
            string cleanWork = Option(args, "--clean-work");
            string runIndex = Option(args, "--run-index");
            if (scanWork != null || cleanWork != null || runIndex != null)
            {
                StringBuilder rep = new StringBuilder();
                string reportPath = Option(args, "--report") ?? Path.Combine(Ctx.BaseDir, "tools-report.txt");
                int rc = 0;
                try
                {
                    if (scanWork != null || cleanWork != null)
                    {
                        string dir = scanWork ?? cleanWork;
                        List<CleanGroup> groups = Tools.Scan(dir);
                        foreach (CleanGroup g in groups) rep.AppendLine("group " + g.Key + " default=" + g.Default + " | " + g.ToString());
                        if (cleanWork != null)
                        {
                            string want = "," + (Option(args, "--groups") ?? "") + ",";
                            List<string> files = new List<string>();
                            foreach (CleanGroup g in groups) if (want.IndexOf("," + g.Key + ",", StringComparison.Ordinal) >= 0) files.AddRange(g.Files);
                            if (!Has(args, "--no-backup")) rep.AppendLine("backup: " + Tools.Backup(dir, files, Path.Combine(dir, "..\\backup-test")));
                            List<string> failed = new List<string>();
                            int n = Tools.Delete(files, failed);
                            rep.AppendLine("deleted " + n + " of " + files.Count + "; failed " + failed.Count);
                        }
                    }
                    if (runIndex != null)
                    {
                        int exit = Tools.RunIndex(runIndex, delegate (string line) { rep.AppendLine("> " + line); });
                        rep.AppendLine("exit code " + exit);
                        rep.AppendLine("index file: " + Tools.IndexFileOf(runIndex));
                    }
                }
                catch (Exception ex) { rep.AppendLine("ERROR: " + ex); rc = 1; }
                File.WriteAllText(reportPath, rep.ToString());
                return rc;
            }

            // --check-updates <report> [--community <dir>] [--index-file <file>] [--github-mission <api url>] [--github-app <api url>]: the reads of the Check for updates button, no download
            // --test-replace <installed> --with <file> --backup <dir> [--report <file>]: the replacement of the mission file (check it on a dummy copy)
            string checkUpdates = Option(args, "--check-updates");
            string testReplace = Option(args, "--test-replace");
            if (checkUpdates != null || testReplace != null)
            {
                StringBuilder rep = new StringBuilder();
                int rc = 0;
                try
                {
                    if (testReplace != null)
                    {
                        string backup = Updates.ReplaceInstalled(testReplace, Option(args, "--with"), Option(args, "--backup"));
                        rep.AppendLine("replaced " + testReplace + "; backup " + backup);
                        string t, i;
                        Updates.ParseHead(Updates.ReadHead(testReplace), out t, out i);
                        rep.AppendLine("now: " + t);
                        File.WriteAllText(Option(args, "--report") ?? testReplace + ".report.txt", rep.ToString());
                        return 0;
                    }
                    string community = Option(args, "--community");
                    List<string> roots = community != null ? new List<string>(new string[] { community }) : Tools.CommunityRoots();
                    List<MissionCopy> copies = Updates.FindInstalled(roots, null);
                    rep.AppendLine("installed copies: " + copies.Count);
                    foreach (MissionCopy c in copies) rep.AppendLine("  " + c + " | " + c.Size + " bytes | " + c.Path);
                    string indexFile = Option(args, "--index-file");
                    if (indexFile != null)
                    {
                        foreach (MissionRelease r in Updates.ReadIndexReleases(indexFile)) rep.AppendLine("index lists: " + r);
                    }
                    string ghMission = Option(args, "--github-mission");
                    if (ghMission != null)
                    {
                        try { RemoteMission m = Updates.ReadRemoteMissionGitHub(ghMission); rep.AppendLine("github mission: " + m.Release + " size " + m.Size + " sha256 " + m.Sha256 + " " + m.Url); }
                        catch (Exception ex) { rep.AppendLine("github mission: " + ex.Message); }
                    }
                    string ghApp = Option(args, "--github-app");
                    if (ghApp != null)
                    {
                        try { RemoteApp a = Updates.ReadRemoteApp(ghApp); rep.AppendLine("github app: " + a.Tag + " newer=" + Updates.AppIsNewer(a) + " asset " + a.AssetName + " size " + a.AssetSize + " sha256 " + a.Sha256); }
                        catch (Exception ex) { rep.AppendLine("github app: " + ex.Message); }
                    }
                    // --github-pack <EN|DE>: the pack published apart from the program (release voicepack-<name>, asset <name>.xml)
                    string ghPack = Option(args, "--github-pack");
                    if (ghPack != null)
                    {
                        try
                        {
                            RemotePack rp = Updates.ReadRemotePack(Updates.PackApi(ghPack), Path.Combine(Path.GetTempPath(), "hmc-update"), ghPack);
                            if (rp == null || rp.Info == null) rep.AppendLine("github pack " + ghPack + ": nothing is published");
                            else { string why; bool newer = Updates.PackIsNewer(rp, out why); rep.AppendLine("github pack " + ghPack + ": version " + rp.Info.Version + " newer=" + newer + (why.Length > 0 ? " (" + why + ")" : "") + " installed " + (VoicePacks.Find(ghPack) != null ? VoicePacks.Find(ghPack).Version.ToString() : "none")); }
                        }
                        catch (Exception ex) { rep.AppendLine("github pack " + ghPack + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) { rep.AppendLine("ERROR: " + ex); rc = 1; }
                File.WriteAllText(checkUpdates ?? (testReplace + ".report.txt"), rep.ToString());
                return rc;
            }

            // --simlink-check <report>: finds SimConnect.dll and tries to connect once (without a simulator it must report "waiting")
            string simlinkCheck = Option(args, "--simlink-check");
            if (simlinkCheck != null)
            {
                StringBuilder rep = new StringBuilder();
                string dll = SimLink.FindDll();
                rep.AppendLine("SimConnect.dll: " + (dll ?? "not found"));
                if (dll != null)
                {
                    Ctx.Log = delegate (string s) { rep.AppendLine(s); };
                    SimLink.Start(delegate { return true; });
                    Thread.Sleep(3500);
                    SimLink.Stop();
                    rep.AppendLine("status: " + SimLink.Status);
                }
                File.WriteAllText(simlinkCheck, rep.ToString());
                return dll != null ? 0 : 1;
            }

            // --atc-test <report> [--seconds N]: connects to the simulator and writes what ATC ID, ATC AIRLINE and ATC FLIGHT NUMBER of the aircraft contain
            // (the livery sets them in aircraft.cfg: atc_id, atc_airline, atc_flight_number); tells whether the call sign of the helicopter can be read
            string atcTest = Option(args, "--atc-test");
            if (atcTest != null)
            {
                StringBuilder rep = new StringBuilder();
                string dll = SimLink.FindDll();
                rep.AppendLine("SimConnect.dll: " + (dll ?? "not found"));
                if (dll != null)
                {
                    int secs = 40;
                    int.TryParse(Option(args, "--seconds") ?? "", out secs);
                    if (secs < 5) secs = 40;
                    Ctx.Log = delegate (string s) { rep.AppendLine(s); };
                    int got = 0;
                    SimLink.OnAtcStrings = delegate (string id, string airline, string number)
                    {
                        got++;
                        rep.AppendLine("ATC ID='" + id + "' ATC AIRLINE='" + airline + "' ATC FLIGHT NUMBER='" + number + "'");
                    };
                    SimLink.Start(delegate { return false; });
                    Thread.Sleep(secs * 1000);
                    SimLink.Stop();
                    rep.AppendLine("status: " + SimLink.Status);
                    rep.AppendLine("readings: " + got + (got == 0 ? " (no reading: the simulator is not running or the aircraft is not loaded)" : ""));
                }
                File.WriteAllText(atcTest, rep.ToString());
                return dll != null ? 0 : 1;
            }

            // --install-pack <xml> [--report <file>]: install a voice pack into the voicepacks folder (the old version of the same pack goes to voicepacks\obsolete)
            string installPack = Option(args, "--install-pack");
            if (installPack != null)
            {
                string reportPath = Option(args, "--report") ?? Path.Combine(Ctx.BaseDir, "install-pack-report.txt");
                string msg;
                int rc = 0;
                try { msg = VoicePacks.Install(installPack, delegate (string q) { return true; }); }
                catch (Exception ex) { msg = "ERROR: " + ex.Message; rc = 1; }
                File.WriteAllText(reportPath, msg + Environment.NewLine);
                return rc;
            }

            // --packs-check <report>: lists the voice packs of the voicepacks folder with their problems
            string packsCheck = Option(args, "--packs-check");
            if (packsCheck != null)
            {
                StringBuilder rep = new StringBuilder();
                VoicePacks.Refresh();
                rep.AppendLine("folder: " + VoicePacks.Dir);
                VoicePack baseRef = PackCheck.Reference();
                foreach (VoicePack p in VoicePacks.Packs)
                {
                    rep.AppendLine("pack " + (p.Name.Length > 0 ? p.Name : "(no name)") + " version " + p.Version + " language " + p.Language + " messages " + p.Messages.Count + (p.BaseVer > 0 ? " basever " + p.BaseVer : "") + " file " + Path.GetFileName(p.File));
                    foreach (string problem in p.Problems) rep.AppendLine("  problem: " + problem);
                    if (!p.Usable || baseRef == null) continue;
                    PackReport pr = PackCheck.Compare(p, baseRef);
                    rep.AppendLine("  against " + PackCheck.BaseName + " " + baseRef.Version + ": " + (p == baseRef ? "the base pack" : pr.Summary()) + " [level " + pr.Level + "]");
                    for (int i = 0; i < pr.Missing.Count && i < 8; i++) rep.AppendLine("  missing: " + pr.Missing[i]);
                    for (int i = 0; i < pr.Values.Count && i < 8; i++) rep.AppendLine("  values differ: " + pr.Values[i]);
                }
                File.WriteAllText(packsCheck, rep.ToString());
                return 0;
            }

            // --actors-test <report>: the rules of the main actors on made-up voice lists (no voice, Microsoft voices, Piper only, a voice no longer installed, a random actor)
            string actorsTest = Option(args, "--actors-test");
            if (actorsTest != null)
            {
                StringBuilder rep = new StringBuilder();
                int failed = 0;
                Action<bool, string> check = delegate (bool ok, string what) { rep.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; };
                Func<string, string, string, VoiceInfo> voice = delegate (string id, string engine, string family) { VoiceInfo v = new VoiceInfo(); v.Id = id; v.Engine = engine; v.Family = family; v.Display = id; v.Keys = new string[] { id }; v.Rank = engine == "windows" ? 5 : 3; return v; };
                LangInfo en = LangInfo.ByCode("EN");
                List<VoiceInfo> none = new List<VoiceInfo>();
                List<VoiceInfo> ms = new List<VoiceInfo>(new VoiceInfo[] { voice("win:A", "windows", en.Family), voice("win:B", "windows", en.Family), voice("piper:p1", "piper", en.Family), voice("stt4sg:x", "stt4sg", en.Family) });
                List<VoiceInfo> piper = new List<VoiceInfo>(new VoiceInfo[] { voice("piper:p1", "piper", en.Family), voice("piper:p2", "piper", en.Family), voice("stt4sg:x", "stt4sg", en.Family) });
                Settings s = new Settings(); s.Normalize();
                check(ActorRules.Missing(s, en, ms).Count == 8, "a new setting: the eight main actors have no voice");
                check(ActorRules.Fill(s, en, none).Count == 0 && ActorRules.Missing(s, en, none).Count == 8, "no voice at all: nothing can be given, the eight actors stay missing (the service does not start)");
                check(ActorRules.Fill(s, en, piper).Count == 8, "Piper voices only: the eight actors get one");
                check(!ActorRules.Missing(s, en, piper).Contains(1) && s.Get("EN", 1).Voice.StartsWith("piper:"), "the crew gets an installed Piper voice, never the online one");
                Settings s2 = new Settings(); s2.Normalize();
                List<int> filled = ActorRules.Fill(s2, en, ms);
                check(filled.Count == 8 && ActorRules.Missing(s2, en, ms).Count == 0, "Microsoft and Piper voices: the eight actors get one");
                check(s2.Get("EN", 1).Voice.StartsWith("win:") && s2.Get("EN", 2).Voice.StartsWith("win:") && s2.Get("EN", 12).Voice.StartsWith("win:"), "the Microsoft voices come first");
                check(s2.Get("EN", 8).Random && s2.Get("EN", 9).Random && s2.Get("EN", 12).Random && !s2.Get("EN", 1).Random, "the dispatcher, the ground unit and the heli rescuer draw at random, the crew does not");
                check(!s2.Get("EN", 6).Random && string.IsNullOrEmpty(s2.Get("EN", 6).Voice), "the other actors (technicians, patient, checklist) are left as they are");
                check(ActorRules.Fill(s2, en, ms).Count == 0, "a second pass changes nothing");
                s2.Get("EN", 3).Voice = "win:GONE";
                check(ActorRules.Missing(s2, en, ms).Count == 1 && ActorRules.Missing(s2, en, ms)[0] == 3, "a voice that is no longer installed counts as missing");
                check(ActorRules.Fill(s2, en, ms).Count == 1 && s2.Get("EN", 3).Voice.StartsWith("win:") && s2.Get("EN", 3).Voice != "win:GONE", "...and is replaced");
                Settings s4 = new Settings(); s4.Normalize();
                s4.Get("EN", 4).Voice = "piper:p1"; ActorRules.Fill(s4, en, ms);
                check(s4.Get("EN", 4).Voice == "piper:p1", "the chosen installed voice is not replaced");
                Settings s5 = new Settings(); s5.Normalize(); s5.DefaultLanguage = "EN"; s5.BridgePack = "";
                check(ActorRules.Required(s5).Count == 1 && ActorRules.Required(s5)[0] == "EN", "the language the service needs: the default language");
                check(ActorRules.IsMain(1) && ActorRules.IsMain(5) && ActorRules.IsMain(8) && ActorRules.IsMain(9) && ActorRules.IsMain(12) && !ActorRules.IsMain(6) && !ActorRules.IsMain(10) && !ActorRules.IsMain(11), "main actors: the crew (1-5), dispatcher 8, ground unit 9, heli rescuer 12");
                Settings s6 = new Settings(); s6.Port = 5993; Settings.PortWas = 0; s6.Normalize();
                check(s6.Port == 5997 && Settings.PortWas == 5993, "a saved port 5993 is set back to the fixed port 5997 and remembered for the log");
                Settings.PortWas = 0;
                Settings s7 = new Settings(); s7.Normalize();
                check(s7.Port == 5997 && Settings.PortWas == 0, "the fixed port 5997 needs no message");
                rep.AppendLine(failed == 0 ? "ALL PASS" : failed + " FAILED");
                File.WriteAllText(actorsTest, rep.ToString());
                return failed == 0 ? 0 : 1;
            }

            // --pack-template <out.xml> --lang IT [--name IT] [--report <file>]: a translation of the base pack as a new file
            // --pack-add-missing <pack name> [--report <file>]: adds the English texts of the keys that a pack lacks to its file
            string packTemplate = Option(args, "--pack-template");
            string packAdd = Option(args, "--pack-add-missing");
            if (packTemplate != null || packAdd != null)
            {
                string reportPath = Option(args, "--report") ?? Path.Combine(Ctx.BaseDir, "pack-tools-report.txt");
                string msg;
                int rc = 0;
                try
                {
                    VoicePacks.Refresh();
                    if (packTemplate != null)
                    {
                        string lang = Option(args, "--lang") ?? "";
                        msg = PackCheck.ExportTemplate(PackCheck.Reference(), Option(args, "--name") ?? lang, lang, packTemplate);
                    }
                    else
                    {
                        VoicePack target = VoicePacks.Find(packAdd);
                        if (target == null) throw new InvalidOperationException("The pack " + packAdd + " is not installed.");
                        msg = PackCheck.AddMissing(target, PackCheck.Reference());
                    }
                }
                catch (Exception ex) { msg = "ERROR: " + ex.Message; rc = 1; }
                File.WriteAllText(reportPath, msg + Environment.NewLine);
                return rc;
            }

            // --audio-devices <report>: the output devices of Windows and a silent test of each one (0.3 s of silence, nothing is heard): the open path of the audio output
            string devReport = Option(args, "--audio-devices");
            if (devReport != null)
            {
                StringBuilder rep = new StringBuilder();
                Ctx.Log = delegate (string s) { rep.AppendLine("log: " + s); };
                byte[] silence = new byte[44 + 13230];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(silence, 0); BitConverter.GetBytes(36 + 13230).CopyTo(silence, 4); Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(silence, 8);
                BitConverter.GetBytes(16).CopyTo(silence, 16); BitConverter.GetBytes((short)1).CopyTo(silence, 20); BitConverter.GetBytes((short)1).CopyTo(silence, 22);
                BitConverter.GetBytes(22050).CopyTo(silence, 24); BitConverter.GetBytes(44100).CopyTo(silence, 28); BitConverter.GetBytes((short)2).CopyTo(silence, 32); BitConverter.GetBytes((short)16).CopyTo(silence, 34);
                Encoding.ASCII.GetBytes("data").CopyTo(silence, 36); BitConverter.GetBytes(13230).CopyTo(silence, 40);
                List<string> names = WaveOutDevices.Names();
                rep.AppendLine("devices: " + names.Count);
                for (int i = -1; i < names.Count; i++)
                {
                    bool ok = false;
                    try { ok = new WaveOutPlayback().Play(silence, i); } catch (Exception ex) { rep.AppendLine("error: " + ex.Message); }
                    rep.AppendLine((i < 0 ? "default" : i + " " + names[i]) + ": " + (ok ? "ok" : "FAILED"));
                }
                rep.AppendLine("resolve empty: " + WaveOutDevices.Resolve(""));
                if (names.Count > 0) rep.AppendLine("resolve " + names[0] + ": " + WaveOutDevices.Resolve(names[0]));
                rep.AppendLine("resolve unknown: " + WaveOutDevices.Resolve("no such device"));
                File.WriteAllText(devReport, rep.ToString(), new UTF8Encoding(false));
                return 0;
            }

            // --ensure-sounds <report> [--audio <folder>]: copies the notification sounds of the program that are missing into the audio folder and lists them
            string ensureSounds = Option(args, "--ensure-sounds");
            if (ensureSounds != null)
            {
                StringBuilder ens = new StringBuilder();
                Ctx.Log = delegate (string s) { ens.AppendLine("log: " + s); };
                ens.AppendLine("audio folder: " + Ctx.AudioDir);
                int copiedNow = AppSounds.EnsureAll();
                foreach (string n in AppSounds.Names())
                {
                    string p = Path.Combine(Ctx.AudioDir, n);
                    ens.AppendLine(n + (File.Exists(p) ? " " + new FileInfo(p).Length + " bytes" : " MISSING"));
                }
                ens.AppendLine("copied: " + copiedNow);
                File.WriteAllText(ensureSounds, ens.ToString());
                return 0;
            }
            // --port-owner <report> [--port N]: who listens on the port (default 5997)
            string portOwnerReport = Option(args, "--port-owner");
            if (portOwnerReport != null)
            {
                int portNo;
                if (!int.TryParse(Option(args, "--port") ?? "5997", out portNo)) portNo = 5997;
                string portUser = Tools.PortOwner(portNo);
                File.WriteAllText(portOwnerReport, "port " + portNo + ": " + (portUser.Length > 0 ? portUser : "nobody") + "\r\n");
                return 0;
            }
            // --pool-test <report>: the random voices window driven on a made-up list (sort, used for, copy and paste through the clipboard)
            string poolTestReport = Option(args, "--pool-test");
            if (poolTestReport != null) return PoolSelfTest.Run(poolTestReport);
            // --queue-test <report> [--log <hmc log>] [--pack <EN.xml>]: the decisions of the speech scheduler on made-up cases and, with a log of the program, the replay of its lines with the old and the new queue
            string queueReport = Option(args, "--queue-test");
            if (queueReport != null)
            {
                VoicePacks.Refresh();
                return QueueSelfTest.Run(queueReport, Option(args, "--log"), Option(args, "--pack"), Has(args, "--smoke"));
            }
            // --shared-test <report> --source <folder|url> --state <folder> [--work <folder>] [--db <folder>] [--apply-db] [--restore-db] [--dry-run]: the shared data (waypoint table, offline database changes)
            string sharedReport = Option(args, "--shared-test");
            if (sharedReport != null)
            {
                StringBuilder rep = new StringBuilder();
                int rc = 0;
                try
                {
                    string state = Option(args, "--state");
                    if (state == null) throw new InvalidOperationException("--state <folder> is needed (the test never uses the real data folder)");
                    SharedData.StateOverride = state;
                    SharedResult r;
                    string work = Option(args, "--work");
                    string source = Option(args, "--source");
                    if (source != null)
                    {
                        List<string> works = new List<string>();
                        if (work != null) works.Add(work);
                        r = SharedData.Sync(source, works);
                        foreach (string s in r.Lines) rep.AppendLine(s);
                        rep.AppendLine("sync ok=" + r.Ok + " revision=" + r.Revision + " datarev=" + r.DataRev + " hangars=" + r.Hangars + " hospitals=" + r.Hospitals + " local=" + r.LocalHangars + "/" + r.LocalHospitals + " written=" + r.Written + " cache=" + r.FromCache);
                        if (!r.Ok) rc = 1;
                    }
                    string db = Option(args, "--db");
                    if (Has(args, "--apply-db") && db != null)
                    {
                        r = SharedData.ApplyDb(new string[] { db }, Has(args, "--dry-run"));
                        foreach (string s in r.Lines) rep.AppendLine(s);
                        rep.AppendLine("apply-db ok=" + r.Ok);
                        if (!r.Ok) rc = 1;
                    }
                    if (Has(args, "--restore-db"))
                    {
                        r = SharedData.RestoreDb();
                        foreach (string s in r.Lines) rep.AppendLine(s);
                        rep.AppendLine("restore-db ok=" + r.Ok);
                        if (!r.Ok) rc = 1;
                    }
                }
                catch (Exception ex) { rep.AppendLine("ERROR: " + ex); rc = 1; }
                File.WriteAllText(sharedReport, rep.ToString());
                return rc;
            }
            // --world-test <report> --state <folder>: the logic of the window World Database Update (unicode check, records, the user's files); --world-shot <base png path> --state <folder>: a picture of every tab
            string worldReport = Option(args, "--world-test");
            string worldShot = Option(args, "--world-shot");
            if (worldReport != null || worldShot != null)
            {
                SharedData.StateOverride = Option(args, "--state") ?? Path.Combine(Path.GetTempPath(), "hmc-world-test");
                if (worldReport != null) return WorldDbSelfTest.Run(worldReport);
                Application.EnableVisualStyles();
                return WorldDbSelfTest.Shot(worldShot);
            }
            // --port-leak-test <report>: a program started while the service runs must not keep the port after the service stopped
            string leakReport = Option(args, "--port-leak-test");
            if (leakReport != null) return Server.LeakTest(leakReport);
            string poolShot = Option(args, "--pool-shot");
            if (poolShot != null) return PoolSelfTest.Shot(poolShot);
            // --expand <report> --text "<tagged text>" [--repeat N] [--seed N]: what the program would speak for a mission text (no audio)
            string expandReport = Option(args, "--expand");
            if (expandReport != null)
            {
                StringBuilder rep = new StringBuilder();
                Ctx.Log = delegate (string s) { rep.AppendLine("log: " + s); };
                string seedArg = Option(args, "--seed");
                int seedValue;
                if (seedArg != null && int.TryParse(seedArg, out seedValue)) Say.Seed(seedValue);
                string repeatArg = Option(args, "--repeat");
                int repeat;
                if (repeatArg == null || !int.TryParse(repeatArg, out repeat) || repeat < 1) repeat = 1;
                VoiceLibrary.Refresh();
                VoicePacks.Refresh();
                Dictionary<string, double> watchSeen = new Dictionary<string, double>();
                Say.Sink = delegate (string s) { rep.AppendLine("bridge speaks: " + s); };
                List<string> texts = new List<string>();
                string linesFile = Option(args, "--lines");
                if (linesFile != null) { foreach (string ln in File.ReadAllLines(linesFile, Encoding.UTF8)) if (ln.Trim().Length > 0) texts.Add(ln); }
                else texts.Add(Option(args, "--text") ?? "");
                for (int r = 0; r < repeat * texts.Count; r++)
                {
                    string text = texts[r % texts.Count];
                    rep.AppendLine("input: " + text);
                    // test lines for the simulator-variable bridge: "@watch NAME VALUE" (a variable changes; the first value of a name is the baseline),
                    // "@settings variety=coherent names=random bridge=EN name.PLT=Mark"
                    if (text.StartsWith("@"))
                    {
                        string[] w = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (w[0] == "@watch" && w.Length >= 3)
                        {
                            double val; double prev;
                            string wname = string.Join(" ", w, 1, w.Length - 2);       // a name may have spaces (CAMERA STATE)
                            double.TryParse(w[w.Length - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val);
                            bool first = !watchSeen.TryGetValue(wname, out prev);
                            Say.OnWatch(wname, first ? 0 : prev, val, first);
                            watchSeen[wname] = val;
                        }
                        else if (w[0] == "@atc" && w.Length >= 2)      // "@atc ID|AIRLINE|NUMBER": what the simulator says about the aircraft (underscores are spaces)
                        {
                            string[] ap = (w[1] + "||").Replace('_', ' ').Split('|');
                            Say.OnAtc(ap[0], ap[1], ap[2]);
                        }
                        else if (w[0] == "@settings")
                        {
                            for (int k = 1; k < w.Length; k++)
                            {
                                string[] kv = w[k].Split('=');
                                if (kv.Length != 2) continue;
                                if (kv[0] == "variety") Ctx.Settings.Variety = kv[1];
                                else if (kv[0] == "names") Ctx.Settings.Names = kv[1];
                                else if (kv[0] == "bridge") Ctx.Settings.BridgePack = kv[1];
                                else if (kv[0] == "callsign") { Ctx.Settings.CallSign = kv[1].Replace('_', ' '); Ctx.Settings.CallSignMode = kv[1].Length > 0 ? "custom" : "airline"; }
                                else if (kv[0] == "random")      // random=8,9,12: those actors take a drawn voice (the language of the settings)
                                {
                                    foreach (string an in kv[1].Split(',')) { int ai; if (int.TryParse(an, out ai) && Actors.CanDraw(ai)) Ctx.Settings.Get(Ctx.Settings.DefaultLanguage, ai).Random = true; }
                                    VoiceDraw.Reset();
                                }
                                else if (kv[0].StartsWith("name.")) Ctx.Settings.CrewNames[kv[0].Substring(5).ToUpperInvariant()] = kv[1];
                            }
                            Ctx.Settings.Normalize();
                            Say.ApplySettings();
                        }
                        continue;
                    }
                    List<string> notes = new List<string>();
                    string wavFile;
                    string expanded = Say.Process(text, notes, out wavFile);
                    rep.AppendLine("expanded: " + expanded);
                    if (wavFile != null) rep.AppendLine("sound file: " + wavFile);
                    List<Segment> parsed = TagParser.Parse(expanded, notes);
                    Say.ApplyDelivery(parsed);
                    foreach (Segment s in parsed)
                    {
                        Plan p = Resolver.Resolve(s, Ctx.Settings, Ctx.Voices);
                        rep.AppendLine("segment: actor=" + p.Actor + " voice=" + (p.Voice != null ? p.Voice.Display : "(none)") + " speed=" + p.Speed + " vol=" + p.Volume + " level=" + (p.Volume * Mixer.MissionFactor(p.Actor) * Mixer.SpatialFactor(p.Radio, Say.SpeakerInside(p.Actor))).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " radio=" + p.Radio + (p.Radio && p.Fx != null ? " fx=" + p.Fx : "") + (p.Far ? " far=1" : "") + (p.Radio && p.Beep != null ? " beep=" + p.Beep : "") + (s.PauseBefore > 0 ? " pause_before=" + s.PauseBefore.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "") + " text=" + s.Text);
                    }
                    foreach (string n in notes) rep.AppendLine("note: " + n);
                }
                File.WriteAllText(expandReport, rep.ToString());
                PiperEngine.StopAll();
                return 0;
            }

            if (listFile != null)
            {
                VoiceLibrary.Refresh();
                List<string> lines = new List<string>();
                foreach (VoiceInfo v in Ctx.Voices) lines.Add(v.Family + " | " + v.Engine + " | " + v.Display + " | " + v.Id);
                File.WriteAllLines(listFile, lines.ToArray());
                return 0;
            }

            if (selftest != null)
            {
                string text = Option(args, "--text") ?? "Selftest of the voice hub.";
                StringBuilder report = new StringBuilder();
                try
                {
                    VoiceLibrary.Refresh();
                    List<string> notes = new List<string>();
                    List<Segment> segs = TagParser.Parse(text, notes);
                    foreach (string n in notes) report.AppendLine("note: " + n);
                    foreach (Segment s in segs)
                    {
                        Plan p = Resolver.Resolve(s, Ctx.Settings, Ctx.Voices);
                        report.AppendLine("segment: actor=" + s.Actor + " voice=" + s.Voice + " lang=" + s.Lang + " speed=" + s.Speed + " vol=" + s.Volume + " radio=" + s.Radio + (s.PauseBefore > 0 ? " pause_before=" + s.PauseBefore.ToString("0.##", CultureInfo.InvariantCulture) : "") + " text=" + s.Text);
                        report.AppendLine("  plan: " + (p.Voice != null ? p.Voice.Display : "(none)") + " speed=" + p.Speed + " lang=" + p.Lang + " " + p.Note);
                    }
                    int code = 1;
                    if (segs.Count > 0)
                    {
                        Plan p = Resolver.Resolve(segs[0], Ctx.Settings, Ctx.Voices);
                        if (p.Voice != null)
                        {
                            DateTime t0 = DateTime.UtcNow;
                            byte[] raw = Synth.Render(p.Voice, p.Speed, segs[0].Text);
                            report.AppendLine("rendered " + raw.Length + " bytes in " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms: " + AudioFx.Describe(raw));
                            byte[] wav = AudioFx.Apply(raw, p.Volume, Ctx.Settings.MasterVolume, p.Radio, Ctx.Settings.Radio);
                            report.AppendLine("after effects (vol " + p.Volume + ", radio " + p.Radio + "): " + AudioFx.Describe(wav));
                            File.WriteAllBytes(selftest, wav);
                            code = wav.Length > 1000 ? 0 : 1;
                        }
                    }
                    File.WriteAllText(selftest + ".txt", report.ToString());
                    PiperEngine.StopAll();
                    return code;
                }
                catch (Exception ex)
                {
                    report.AppendLine("ERROR: " + ex);
                    File.WriteAllText(selftest + ".txt", report.ToString());
                    PiperEngine.StopAll();
                    return 1;
                }
            }

            // a copy started by the restart of the program (--after-pid) waits for the one that is ending: the mutex and the port are free when it goes on
            int afterPid;
            if (int.TryParse(Option(args, "--after-pid") ?? "", out afterPid) && afterPid > 0)
            {
                try { using (Process old = Process.GetProcessById(afterPid)) old.WaitForExit(20000); }
                catch (Exception) { }                         // the process is already gone
            }

            bool createdNew = true;
            Mutex mutex = null;
            if (Option(args, "--screenshot") == null)         // a screenshot run may happen while the program is running
            {
                mutex = new Mutex(true, "Local\\HPGMissionCompanion", out createdNew);
                try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HPGMissionCompanion.show"); } catch (Exception) { ShowEvent = null; }
                if (!createdNew)
                {
                    try { if (ShowEvent != null) ShowEvent.Set(); } catch (Exception) { }
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ctx.Player = new Player();
            MainForm.QuietPort = Option(args, "--screenshot") != null;
            MainForm form = new MainForm(Has(args, "--minimized"), Has(args, "--exit-with-sim"), logFile, simRoot);
            string shot = Option(args, "--screenshot");
            if (shot != null) { form.Snapshot(shot); return 0; }
            Application.Run(form);
            GC.KeepAlive(mutex);
            return 0;
        }

        static bool Has(string[] args, string name)
        {
            foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Option(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
