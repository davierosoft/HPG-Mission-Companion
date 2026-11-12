@echo off
rem Builds HPGMissionCompanion.exe and the installer HPGMissionCompanionSetup.exe with the .NET Framework 4 compiler that ships with Windows (no SDK needed).
rem Usage: tools\build.cmd [output folder]    (default: bin next to the repo root)
setlocal enabledelayedexpansion
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0..\bin
if not exist "%OUT%" mkdir "%OUT%"
pushd "%~dp0.."
set RES=/resource:docs\DEVELOPER_GUIDE.md,DEVELOPER_GUIDE.md /resource:profiles\Recommended.json,profile.Recommended.json
rem the notification sounds of the pack (tablet tones, alarm, dead) are embedded too (AppSounds): the program copies them into the audio folder when they are missing
for %%F in (assets\app-sounds\*.wav) do set RES=!RES! /resource:"%%F",snd.%%~nxF
rem the recordings of the squelch tail are embedded in the program (AudioFx)
for %%F in (assets\squelch-tail\*.wav) do set RES=!RES! /resource:"%%F",%%~nxF
"%FW%\csc.exe" /nologo /target:winexe /win32icon:assets\app.ico !RES! /out:"%OUT%\HPGMissionCompanion.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll src\Core.cs src\AudioFx.cs src\Tools.cs src\Engines.cs src\Server.cs src\Install.cs src\Mixer.cs src\Names.cs src\Sched.cs src\SharedData.cs src\WorldDb.cs src\WorldDbForm.cs src\VoicePacks.cs src\SimLink.cs src\Updates.cs src\NewCompanion.cs src\Profiles.cs src\NotesForm.cs src\PoolForm.cs src\MainForm.cs src\Program.cs
set RC=%ERRORLEVEL%
rem the voice packs of the repository are copied into the output folder
if %RC%==0 if exist "voicepacks\*.xml" (
  if not exist "%OUT%\voicepacks" mkdir "%OUT%\voicepacks"
  for %%F in (voicepacks\*.xml) do copy /y "%%F" "%OUT%\voicepacks\%%~nxF" >nul
)
rem SimConnect.dll comes from the MSFS SDK (not stored in the repository); copy it next to the program when the SDK is installed
if %RC%==0 call :copydll
rem the installer (setup\Setup.cs) embeds the program, SimConnect.dll when it is next to the program and the voice packs of the repository
if %RC%==0 if not defined NOSETUP call :setup
popd
exit /b %RC%

:setup
setlocal enabledelayedexpansion
set RES=/resource:"%OUT%\HPGMissionCompanion.exe",app.exe
if exist "%OUT%\SimConnect.dll" set RES=!RES! /resource:"%OUT%\SimConnect.dll",SimConnect.dll
for %%F in (voicepacks\*.xml) do set RES=!RES! /resource:"%%F",pack.%%~nxF
"%FW%\csc.exe" /nologo /target:winexe /win32icon:assets\app.ico /out:"%OUT%\HPGMissionCompanionSetup.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll !RES! setup\Setup.cs
endlocal & set RC=%ERRORLEVEL%
goto :eof

:copydll
for %%D in (A B C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if exist "%%D:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll" (
    echo Copying SimConnect.dll from %%D:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll
    copy /y "%%D:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll" "%OUT%\SimConnect.dll" >nul
    goto :eof
  )
)
for %%D in (A B C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if exist "%%D:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll" (
    echo Copying SimConnect.dll from %%D:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll
    copy /y "%%D:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll" "%OUT%\SimConnect.dll" >nul
    goto :eof
  )
)
echo WARNING: MSFS SDK not found, SimConnect.dll was not copied
goto :eof
