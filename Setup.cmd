@echo off
REM Double-click this once on a machine that has never run the game. It installs the two
REM things the repository deliberately does not carry - Godot and the .NET SDK - and then
REM points Play.cmd at them. After it finishes, Play.cmd is all you ever need again.
REM Safe to run twice: it installs only what is genuinely missing.
setlocal EnableDelayedExpansion
title HitboxClone setup

set "DOTNET_URL=https://dotnet.microsoft.com/download/dotnet/8.0"
set "GODOT_URL=https://godotengine.org/download/windows/"

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

set "GODOT_EXE="
if defined GODOT_HOME if exist "%GODOT_HOME%\godot.cmd" set "GODOT_EXE=%GODOT_HOME%\godot.cmd"
if not defined GODOT_EXE if defined GODOT_HOME if exist "%GODOT_HOME%\godot.exe" set "GODOT_EXE=%GODOT_HOME%\godot.exe"
if not defined GODOT_EXE if defined GODOT_HOME for %%I in ("%GODOT_HOME%\Godot_v*_mono_win64.exe") do set "GODOT_EXE=%%~fI"
if not defined GODOT_EXE if exist "H:\dev-tools\godot\godot.cmd" set "GODOT_EXE=H:\dev-tools\godot\godot.cmd"
if not defined GODOT_EXE for %%I in (godot.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"
if not defined GODOT_EXE for %%I in (Godot_v4.7.1-stable_mono_win64.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"

if defined DOTNET_OK (echo   [found]   .NET 8 SDK   !DOTNET_EXE!) else (echo   [missing] .NET 8 SDK)
if defined GODOT_EXE (echo   [found]   Godot        !GODOT_EXE!) else (echo   [missing] Godot .NET/mono)
echo.

if defined DOTNET_OK if defined GODOT_EXE (
  echo   Both already installed - nothing to do.
  echo   Double-click Play.cmd.
  echo.
  pause
  exit /b 0
)

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
  echo.
  pause
  exit /b 1
)

REM Each install is checked. A package id can be renamed or dropped upstream, and a
REM winget failure that scrolls past unnoticed would otherwise surface much later as
REM Play.cmd reporting the tool missing with no hint that an install was even tried.
set "FAILED="

if not defined DOTNET_OK (
  echo Installing the .NET 8 SDK...
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  if errorlevel 1 (
    echo   .NET SDK install did not succeed - get it from !DOTNET_URL!
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

if defined FAILED (
  echo ============================================
  echo   Setup finished with something unresolved.
  echo   Read the messages above, install what is
  echo   named there, then run Setup.cmd again.
  echo ============================================
  echo.
  pause
  exit /b 1
)

echo ============================================
echo   Setup finished.
echo.
echo   Close this window, open a new one, and
echo   double-click Play.cmd.
echo.
echo   The new window matters: PATH and GODOT_HOME
echo   changed just now, and this window is still
echo   holding the values from before they did.
echo ============================================
echo.
pause
