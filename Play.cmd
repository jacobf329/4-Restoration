@echo off
REM Double-click this to update, build and play. It is the only thing you need to run
REM the game, on any machine.
REM
REM Three steps, and each one exists because skipping it produced a real bug:
REM   1. Update - a clone that never pulls launches a months-old game perfectly happily
REM      and says nothing, which is indistinguishable from the game ignoring you.
REM   2. Build  - godot.cmd does NOT compile C#, so skipping it silently runs whatever
REM      assembly was left over from last time.
REM   3. Launch.
REM
REM Set HITBOX_NO_UPDATE=1 to skip step 1 for a session.
setlocal EnableDelayedExpansion

REM ---------------------------------------------------------------------------
REM Re-run from a copy of this file in TEMP before doing anything else.
REM
REM Not paranoia. cmd.exe reads a batch file incrementally, by byte offset, while it
REM runs - so a script that updates itself and then keeps going resumes at a stale
REM offset in a file whose bytes have moved, and executes whatever fragment of a line
REM now sits there. The one file a self-updating launcher is most likely to receive an
REM update to is the launcher, so this is the common case rather than the edge case.
REM
REM Running from a detached copy means git may rewrite Play.cmd and Update.cmd freely;
REM the next launch picks up the new ones. The project folder is passed along because
REM %~dp0 points at TEMP from inside the copy.
REM ---------------------------------------------------------------------------

if /i not "%~1"=="--inplace" goto :stage
set "PROJ=%~2"
goto :main

:stage

set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"

set "STAGE=%TEMP%\hitbox-launch"
md "!STAGE!" 2>nul
copy /y "%~f0" "!STAGE!\play.cmd" >nul 2>&1
copy /y "%PROJ%\Update.cmd" "!STAGE!\Update.cmd" >nul 2>&1

if not exist "!STAGE!\play.cmd" goto :nostage
call "!STAGE!\play.cmd" --inplace "!PROJ!"
set "RC=!ERRORLEVEL!"
exit /b !RC!

:nostage

REM No writable TEMP. Carry on in place rather than refusing to launch, but do not
REM update - rewriting this file underneath itself is the one thing that must not happen.
echo   Could not stage the launcher in TEMP; skipping the update this launch.
set "HITBOX_NO_UPDATE=1"

:main
title HitboxClone

REM ---------------------------------------------------------------------------
REM Step 1: update.
REM
REM Never fatal. Update.cmd /auto reports what it skipped and returns success, because
REM launching an old copy always beats refusing to launch - somebody on a train with no
REM signal wants to play, not to hear about a fetch.
REM ---------------------------------------------------------------------------

if not defined HITBOX_NO_UPDATE goto :doupdate
echo Skipping the update ^(HITBOX_NO_UPDATE is set^).
echo.
goto :updated

:doupdate
REM The staged copy beside this one, if the staging worked; the real one otherwise.
if exist "%~dp0Update.cmd" call "%~dp0Update.cmd" /auto "%PROJ%"
if not exist "%~dp0Update.cmd" if exist "%PROJ%\Update.cmd" call "%PROJ%\Update.cmd" /auto "%PROJ%"
echo.

:updated

REM ---------------------------------------------------------------------------
REM Find the tools, rather than assuming where they live.
REM
REM This used to hardcode H:\dev-tools, which is where they sit on the machine the
REM game was written on and nowhere else. The moment the project was cloned to a
REM laptop the launcher pointed at a drive that did not exist, with no message
REM explaining why. Checked in order: an explicit override, then that original
REM location, then whatever is on PATH.
REM ---------------------------------------------------------------------------

set "DOTNET_EXE="
if defined DOTNET_HOME if exist "%DOTNET_HOME%\dotnet.exe" set "DOTNET_EXE=%DOTNET_HOME%\dotnet.exe"
if not defined DOTNET_EXE if exist "H:\dev-tools\dotnet\dotnet.exe" set "DOTNET_EXE=H:\dev-tools\dotnet\dotnet.exe"
if not defined DOTNET_EXE for %%I in (dotnet.exe) do if not "%%~$PATH:I"=="" set "DOTNET_EXE=%%~$PATH:I"

REM GODOT_HOME is checked for three filenames, not one. The portable copy here is reached
REM through a godot.cmd shim, but an ordinary Windows install has no shim at all - it is a
REM single exe still carrying its version in the name. Checking only for the shim would
REM miss every normal install and report "not found" while pointing straight at it.
set "GODOT_EXE="
if defined GODOT_HOME if exist "%GODOT_HOME%\godot.cmd" set "GODOT_EXE=%GODOT_HOME%\godot.cmd"
if not defined GODOT_EXE if defined GODOT_HOME if exist "%GODOT_HOME%\godot.exe" set "GODOT_EXE=%GODOT_HOME%\godot.exe"
if not defined GODOT_EXE if defined GODOT_HOME for %%I in ("%GODOT_HOME%\Godot_v*_mono_win64.exe") do set "GODOT_EXE=%%~fI"
if not defined GODOT_EXE if exist "H:\dev-tools\godot\godot.cmd" set "GODOT_EXE=H:\dev-tools\godot\godot.cmd"
if not defined GODOT_EXE for %%I in (godot.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"
if not defined GODOT_EXE for %%I in (Godot_v4.7.1-stable_mono_win64.exe) do if not "%%~$PATH:I"=="" set "GODOT_EXE=%%~$PATH:I"

if not defined DOTNET_EXE (
  echo.
  echo ============================================
  echo   Could not find the .NET SDK.
  echo.
  echo   Install .NET 8 SDK, or set DOTNET_HOME to
  echo   the folder containing dotnet.exe.
  echo ============================================
  echo.
  pause
  exit /b 1
)

if not defined GODOT_EXE (
  echo.
  echo ============================================
  echo   Could not find Godot.
  echo.
  echo   This project needs Godot 4.7.1 .NET ^(mono^).
  echo   The plain build will not run C# and will
  echo   fail with script errors.
  echo.
  echo   Put it on PATH, or set GODOT_HOME to the
  echo   folder containing godot.cmd or the exe.
  echo ============================================
  echo.
  pause
  exit /b 1
)

REM Step 2: build.
echo Building HitboxClone...
"%DOTNET_EXE%" build "%PROJ%\HitboxClone.csproj" --nologo -v minimal
if errorlevel 1 (
  echo.
  echo ============================================
  echo   BUILD FAILED - not launching.
  echo   The errors are above.
  echo ============================================
  echo.
  pause
  exit /b 1
)

echo.
REM Step 3: launch.
echo Launching...
"%GODOT_EXE%" --path "%PROJ%"
