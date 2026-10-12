@echo off
REM Double-click this once on a machine that has never run the game. It installs the two
REM things the repository deliberately does not carry - Godot and the .NET SDK - points
REM Play.cmd at them, and puts a HitboxClone shortcut on the Desktop.
REM Safe to run twice: it installs only what is genuinely missing, and re-running is also
REM how you rebuild the shortcut after moving the folder.
setlocal EnableDelayedExpansion
title HitboxClone setup

set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"

REM ---------------------------------------------------------------------------
REM Refuse to run from anywhere that is not the game folder.
REM
REM Everything below assumes this file is sitting in a clone, because %~dp0 is the
REM only thing it has to go on. Downloaded on its own into Downloads and run there,
REM it installed the tools quite happily, reported success, and wrote Desktop
REM shortcuts pointing at a Play.cmd and a Battlefront.cmd that do not exist - so the
REM first thing the person saw was Windows asking them to go and find the game
REM themselves. Setup that cannot work should say so before it does anything, not
REM leave broken icons behind and call it done.
REM
REM project.godot is the check because it is the one file that is unambiguously this
REM game and is never anywhere else.
REM ---------------------------------------------------------------------------

if exist "%PROJ%\project.godot" goto :inproject

echo.
echo ============================================
echo   This is not the game folder.
echo.
echo   Setup.cmd is running from:
echo     %PROJ%
echo.
echo   but there is no project.godot here, so this
echo   is not a clone of the game - probably this
echo   file was downloaded on its own.
echo.
echo   Get the whole thing instead. Open a Command
echo   Prompt and run:
echo.
echo     cd %%USERPROFILE%%
echo     git clone https://github.com/jacobf329/4-Restoration.git
echo.
echo   Then open that new 4-Restoration folder and
echo   run the Setup.cmd that is inside it.
echo.
echo   No git? Install it from
echo     https://git-scm.com/download/win
echo ============================================
echo.
pause
exit /b 1

:inproject
set "DOTNET_URL=https://dotnet.microsoft.com/download/dotnet/8.0"
set "GODOT_URL=https://godotengine.org/download/windows/"
set "GIT_URL=https://git-scm.com/download/win"

echo.
echo ============================================
echo   HitboxClone - first-time setup
echo ============================================
echo.

REM ---------------------------------------------------------------------------
REM Detect what is already here, using exactly the search order Play.cmd uses. Any
REM other order would install a second copy of something Play.cmd already finds.
REM ---------------------------------------------------------------------------

set "DOTNET_EXE="
if defined DOTNET_HOME if exist "%DOTNET_HOME%\dotnet.exe" set "DOTNET_EXE=%DOTNET_HOME%\dotnet.exe"
if not defined DOTNET_EXE if exist "H:\dev-tools\dotnet\dotnet.exe" set "DOTNET_EXE=H:\dev-tools\dotnet\dotnet.exe"
if not defined DOTNET_EXE for %%I in (dotnet.exe) do if not "%%~$PATH:I"=="" set "DOTNET_EXE=%%~$PATH:I"

REM Finding dotnet.exe proves nothing on its own - the runtime ships the same exe as the
REM SDK and cannot compile, which is the failure the README warns about. Only an 8.x row
REM in --list-sdks means a build will actually work.
set "DOTNET_OK="
if defined DOTNET_EXE (
  for /f "tokens=1 delims= " %%V in ('"!DOTNET_EXE!" --list-sdks 2^>nul') do (
    echo %%V | findstr /b /c:"8." >nul && set "DOTNET_OK=1"
  )
)

REM git is a tool the game needs, not just a tool the developer needs. Play.cmd updates
REM the clone on every launch, so a machine without git plays whatever it was last given
REM and never gets another change - which is the failure this was all written to close.
set "GIT_OK="
where git >nul 2>&1
if not errorlevel 1 set "GIT_OK=1"

set "GODOT_EXE="
if defined GODOT_HOME if exist "%GODOT_HOME%\godot.cmd" set "GODOT_EXE=%GODOT_HOME%\godot.cmd"
if not defined GODOT_EXE if defined GODOT_HOME if exist "%GODOT_HOME%\godot.exe" set "GODOT_EXE=%GODOT_HOME%\godot.exe"
if not defined GODOT_EXE if defined GODOT_HOME for %%I in ("%GODOT_HOME%\Godot_v*_mono_win64.exe") do set "GODOT_EXE=%%~fI"
if not defined GODOT_EXE if exist "H:\dev-tools\godot\godot.cmd" set "GODOT_EXE=H:\dev-tools\godot\godot.cmd"
if not defined GODOT_EXE for %%I in (godot.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"
if not defined GODOT_EXE for %%I in (Godot_v4.7.1-stable_mono_win64.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"

if defined DOTNET_OK (echo   [found]   .NET 8 SDK   !DOTNET_EXE!) else (echo   [missing] .NET 8 SDK)
if defined GODOT_EXE (echo   [found]   Godot        !GODOT_EXE!) else (echo   [missing] Godot .NET/mono)
if defined GIT_OK (echo   [found]   git) else (echo   [missing] git)
echo.

REM Nothing missing still falls through to the shortcut below, rather than exiting here -
REM rebuilding the shortcut after moving the folder is a reason to re-run this on a
REM machine that is already fully set up.
set "FAILED="
if defined DOTNET_OK if defined GODOT_EXE if defined GIT_OK goto :update

REM ---------------------------------------------------------------------------
REM winget does the installing. It ships with Windows 11 and current Windows 10.
REM Without it there is no unattended path, so name the downloads rather than
REM half-installing something.
REM ---------------------------------------------------------------------------

where winget >nul 2>&1
if errorlevel 1 (
  echo   winget is not available here, so setup cannot install
  echo   anything for you. Download these by hand:
  echo.
  if not defined DOTNET_OK echo     .NET 8 SDK ^(the SDK, not the runtime^)  !DOTNET_URL!
  if not defined GODOT_EXE echo     Godot 4.7.1 .NET/mono ^(not the plain build^)  !GODOT_URL!
  if not defined GIT_OK    echo     git                                       !GIT_URL!
  echo.
  pause
  exit /b 1
)

REM Each install is checked. A package id can be renamed or dropped upstream, and a
REM winget failure that scrolled past unnoticed would otherwise surface much later as
REM Play.cmd reporting the tool missing, with no hint an install had even been tried.

if not defined DOTNET_OK (
  echo Installing the .NET 8 SDK...
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  if errorlevel 1 (
    echo   .NET SDK install did not succeed - get it from !DOTNET_URL!
    set "FAILED=1"
  )
  echo.
)

if not defined GIT_OK (
  echo Installing git...
  winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
  if errorlevel 1 (
    echo   git install did not succeed - get it from !GIT_URL!
    set "FAILED=1"
  )
  echo.
)

if not defined GODOT_EXE (
  echo Installing Godot ^(.NET / mono build^)...
  winget install --id GodotEngine.GodotEngine.Mono -e --accept-source-agreements --accept-package-agreements
  if errorlevel 1 (
    echo   Godot install did not succeed - get the .NET/mono build from !GODOT_URL!
    set "FAILED=1"
  )
  echo.
)

REM ---------------------------------------------------------------------------
REM Point Play.cmd at Godot.
REM
REM winget installs Godot as a portable package rather than putting it on PATH, and the
REM mono build keeps its version in the filename, so Play.cmd cannot find it unaided -
REM setup would otherwise "succeed" and Play.cmd would still say Godot is missing.
REM GODOT_HOME is the first thing Play.cmd checks, and setx makes it permanent.
REM ---------------------------------------------------------------------------

if not defined GODOT_EXE (
  for /f "delims=" %%I in ('dir /b /s "!LOCALAPPDATA!\Microsoft\WinGet\Packages\Godot_v*_mono_win64.exe" 2^>nul') do set "GODOT_EXE=%%~fI"
  if not defined GODOT_EXE for /f "delims=" %%I in ('dir /b /s "!ProgramFiles!\Godot_v*_mono_win64.exe" 2^>nul') do set "GODOT_EXE=%%~fI"
)

if defined GODOT_EXE (
  for %%I in ("!GODOT_EXE!") do set "GODOT_DIR=%%~dpI"
  set "GODOT_DIR=!GODOT_DIR:~0,-1!"
  setx GODOT_HOME "!GODOT_DIR!" >nul
  echo   GODOT_HOME set to !GODOT_DIR!
  echo.
)

REM ---------------------------------------------------------------------------
:update

REM ---------------------------------------------------------------------------
REM Pull BEFORE writing the shortcuts, not after.
REM
REM The order used to be the other way round, and it meant setup made shortcuts for
REM whatever launchers the clone happened to have before the update. A machine that
REM was behind therefore finished setup with no Battlefront icon - the launcher it was
REM being set up to run arrived thirty seconds later, by which point the shortcuts had
REM already been written - and the only way to get one was to run setup a second time
REM for reasons nothing on screen explained.
REM ---------------------------------------------------------------------------

if not exist "%PROJ%\Update.cmd" goto :noupdater
echo Getting the latest version...
echo   ^(a clone that is well behind has a few hundred MB to fetch - give it a minute^)
echo.
call "%PROJ%\Update.cmd" /auto "%PROJ%"
echo.
:noupdater

:shortcut

REM ---------------------------------------------------------------------------
REM The Desktop shortcuts.
REM
REM They target the .cmd launchers rather than Godot, so launching from the Desktop
REM still builds first - a shortcut straight to the engine would quietly run whatever
REM assembly was left over from last time, which is the trap Play.cmd exists to close.
REM cmd.exe cannot write a .lnk, so this is the one thing handed to PowerShell.
REM ---------------------------------------------------------------------------

set "ICON="
if defined GODOT_EXE if /i "!GODOT_EXE:~-4!"==".exe" set "ICON=!GODOT_EXE!"

set "LNK=%USERPROFILE%\Desktop\HitboxClone.lnk"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\HitboxClone.lnk'); $lnk.TargetPath = $env:PROJ + '\Play.cmd'; $lnk.WorkingDirectory = $env:PROJ; $lnk.Description = 'HitboxClone - build and play'; if ($env:ICON) { $lnk.IconLocation = $env:ICON + ',0' }; $lnk.Save()"

REM Checked by looking for the file rather than by errorlevel: the COM Save() reports
REM success through the object, so a PowerShell that failed to write anything can still
REM exit 0 and leave you with no shortcut and no complaint. OneDrive also relocates the
REM Desktop, which is why the path is asked for rather than assumed - and why the
REM USERPROFILE fallback below is only a fallback.
if not exist "!LNK!" for /f "delims=" %%D in ('powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')" 2^>nul') do set "LNK=%%D\HitboxClone.lnk"

REM A second shortcut for updating.
REM
REM On the Desktop rather than left in the folder because of how the game is actually
REM played: the folder is opened once, at setup, and never again - everything after that
REM happens from the Desktop icon. An update script nobody can see is an update script
REM nobody runs, and a clone that never pulls launches a months-old game perfectly
REM happily and says nothing about it.
set "ULNK=%USERPROFILE%\Desktop\Update HitboxClone.lnk"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Update HitboxClone.lnk'); $lnk.TargetPath = $env:PROJ + '\Update.cmd'; $lnk.WorkingDirectory = $env:PROJ; $lnk.Description = 'HitboxClone - fetch the latest version'; if ($env:ICON) { $lnk.IconLocation = $env:ICON + ',0' }; $lnk.Save()"

REM A third shortcut, for the Battlefront clone.
REM
REM The two games share one launcher and differ only by which scene it opens, so the
REM only thing standing between somebody and the new one was knowing to type a Godot
REM command line. A machine set up to playtest it should have it on the Desktop next
REM to the other one, for the same reason the updater is there: a thing nobody can
REM see is a thing nobody runs.
set "BLNK=%USERPROFILE%\Desktop\Battlefront.lnk"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Battlefront.lnk'); $lnk.TargetPath = $env:PROJ + '\Battlefront.cmd'; $lnk.WorkingDirectory = $env:PROJ; $lnk.Description = 'Battlefront - the battle station'; if ($env:ICON) { $lnk.IconLocation = $env:ICON + ',0' }; $lnk.Save()"

if exist "!LNK!" (
  echo   Desktop shortcut created: HitboxClone
  echo   Desktop shortcut created: Battlefront
  echo   Desktop shortcut created: Update HitboxClone
  echo.
) else (
  echo   Could not create the Desktop shortcut. Right-drag Play.cmd
  echo   to the Desktop and pick "Create shortcuts here" instead.
  echo.
  set "FAILED=1"
)

REM ---------------------------------------------------------------------------
REM Put the clone on the branch the game is actually developed on.
REM
REM A fresh clone lands on the repository's default branch, which is not where the work
REM is - so a machine set up correctly, with every tool installed, would still build and
REM launch a version of the game from before any of it existed. That is not a
REM hypothetical: it is what both machines were doing. Update.cmd knows which branch is
REM wanted, so setup finishes by asking it rather than by repeating the answer here.
REM ---------------------------------------------------------------------------

if defined FAILED (
  echo ============================================
  echo   Setup finished with something unresolved.
  echo   Read the messages above, deal with what is
  echo   named there, then run Setup.cmd again.
  echo ============================================
  echo.
  pause
  exit /b 1
)

echo ============================================
echo   Setup finished.
echo.
echo   Double-click HitboxClone on your Desktop.
echo   It updates itself every launch, so that is
echo   the only thing you ever need to run.
echo.
echo   The Desktop icon now points HERE:
echo     %PROJ%
echo   If you keep more than one copy of the game,
echo   that is the one it will launch. Run Doctor.cmd
echo   if anything ever looks out of date.
echo.
echo   "Update HitboxClone" is there for fetching
echo   without playing. You will rarely want it.
echo.
echo   First launch is slow - Godot imports every
echo   model in assets\ before the game appears.
echo ============================================
echo.
pause
