# HPG Mission Companion

Version 0.40

A companion program for the HPG H145 missions in Microsoft Flight Simulator (2020 and 2024). It runs the voice service of the missions with natural voices, one voice per mission actor, and has tools for the mission work folder and the mission index.

- **Voice service**: the WebSocket voice server the HPG mission system expects (`localhost:5997`), speaking with the voices installed in Windows and with [Piper](https://github.com/rhasspy/piper) voices that the app downloads on request. The text of a `speak` command carries tags that choose the actor, the voice, the speed, the volume, the language and a radio effect. See `docs/architecture/TAGS_AND_PROTOCOL.md`.
- **Actors**: ten actors (Pilota, Copilota, Doctor, HEMS1, HEMS2, Frank, Sarah, Dispatcher, Ground unit, Patient), a voice and a speed for each, per language (English, French, Italian, German).
- **Radio effect**: beep, hiss, band filter and squelch tail, adjustable.
- **Tools**: three boxes, the mission work folder (open and clean, grouped, with a zip backup), the mission index (the HPG `Update Mission Index` tool, also at start, option on by default) and the shared data (the hangar and hospital waypoints of the mission downloaded from GitHub into the work folder, and, if you allow it, the shared changes to the offline airport, apron and helipad lists of the HPG tablet; see `docs/architecture/SHARED_DATA.md`).
- **Updates**: this program is discontinued (version 0.40 is the last). At every start a notice offers the successor, the new Random & Everywhere Companion: uninstall this program and install the new one, or keep this program and start the new one. The mission and the voice packs are no longer updated from here; the Updates tab only looks for a newer version of the program itself.
- **Voice packs**: XML files with the texts of the mission messages (several options each); a mission speaks one with `<say:KEY>` and passes dynamic parts as arguments separated by `//`. Drag and drop in the Voice packs tab. Translated packs: the checklists and the dispatch descriptions of the mission have keys (`<text:KEY>English`), so a pack in another language speaks them too; the list compares every pack with the base pack EN and shows in red the packs that lack texts or do not fit; New translation writes the base pack as a file to translate. The guide for mission authors is `docs/DEVELOPER_GUIDE.md` and the About tab.
- **Voice app mode**: with the mission in app mode (`L:AUDIOINHIBIT` 1, voice code APP) the program reads the LVAR counters of the mission through SimConnect and speaks the matching message of the pack; radio rule, sounds without voice, conversations. See `docs/architecture/VOICEPACKS.md`.
- **Simulator link**: keeps `L:AUDIOINHIBIT` at 1 through SimConnect while the simulator runs (option on by default; needs `SimConnect.dll` from the MSFS SDK next to the program).
- **Start with the simulator**: optional, minimized in the notification area, closing with the simulator.

The program is a single Windows executable built with the compiler that ships with Windows (.NET Framework 4, no SDK). The Piper engine and the voices are not part of this repository.

## Install

Run `HPGMissionCompanionSetup.exe` (from the releases page; the box "Complete setup" in its window, ticked on a new install, applies the recommended voices and settings and downloads the Piper engine and voices they need, about 500 MB; in the program the same is the button Set up in one click of the Service tab, which also saves and loads your settings): it installs for the current user (no administrator rights) in `%LOCALAPPDATA%\Programs\HPG Mission Companion` or a folder you choose, with Start menu and desktop shortcuts, the voice pack EN and `SimConnect.dll`. An update keeps your settings, Piper and the voices. Remove it from Windows Apps (or the Start menu shortcut); the uninstaller asks before deleting your settings and voices. Silent install: `HPGMissionCompanionSetup.exe /S /dir="D:\HPG"`.

## Antivirus

The program and the setup are not signed (a code signing certificate costs money) and few people download them, so an antivirus may stop or warn about a new release (a tester reported Avast on the update). Check what you have: the page of each release shows the SHA-256 of the file (Digest), and `Get-FileHash HPGMissionCompanionSetup.exe` in PowerShell must print the same value; the update of the program checks it before it starts the setup. If your antivirus still blocks a file that matches, you can report it as a false positive (Avast: https://www.avast.com/false-positive-file-form.php, Microsoft Defender: https://www.microsoft.com/en-us/wdsi/filesubmission). The program does not use a shell to restart or uninstall itself (`tools/check.js` fails on `ping 127.0.0.1` and `taskkill`).

## Build

```
tools\build.cmd            builds bin\HPGMissionCompanion.exe and bin\HPGMissionCompanionSetup.exe (the installer)
tools\build.cmd "D:\run"   builds into another folder (for example the runtime folder)
```

Run `node tools/check.js` for the static checks (version agreement, source list, ASCII, no binaries tracked).

## Layout

| Path | Content |
| --- | --- |
| `src/` | C# sources (core and tags, audio effects, engines and player, server, Piper installer, mixer, voice packs, simulator link, window, program) |
| `setup/` | the installer and uninstaller of the program (`Setup.cs`) |
| `assets/` | the icon and the script that draws it |
| `tools/` | build script, static checks, branch guard, release ledger |
| `docs/` | architecture and runtime test procedures |
| `tests/` | standalone HPG test missions used to test the app from the simulator |

Rules for working in this repository: `AGENTS.md`. Third-party software and licences: `THIRD_PARTY.md`.

## Status

Static checks pass; the simulator behaviour (tags arriving through the HPG `speak` command, radio and volume by ear, the real use of the tools) is still being validated. See `docs/testing/RUNTIME_VALIDATION.md`. Anyone may use the program; modifying or redistributing it needs the explicit written consent of the author (see Licence).

## Licence

Use is free for everyone: you may run the program (a build from the author, or built unchanged from this repository) for any purpose, read the source and fork it on GitHub for reading. Modifying the code, copying or redistributing the program or the code, publishing derivative works, sublicensing or selling needs the explicit written consent of the author, as described in LICENSE. Third-party components keep their own licences (THIRD_PARTY.md).

## Contributors

- **Daikan** (GitHub d41k4n): co-author of the missions, testing and bug reports; German voice pack and translation tools, Swiss German voices (STT4SG), separate hiss of radio and external radio calls, squelch-tail recordings, audio cache tools, SimConnect.dll search on every drive (changelog 0.30 and earlier).
- **kekkeruuu**: co-author of the missions.

Their contributions are integrated with their history in the repository; the licences of the components they brought in are in `THIRD_PARTY.md`.
