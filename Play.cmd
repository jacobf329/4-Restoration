@echo off
REM Double-click this to build and play. It is the only thing you need to run the game.
REM Build first, launch second - godot.cmd does NOT compile C#, so skipping the build
REM silently runs whatever assembly was left over from last time.
setlocal
title HitboxClone

REM %~dp0 ends in a backslash, which would escape the closing quote. Trim it.
set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"

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
echo Launching...
"%GODOT_EXE%" --path "%PROJ%"
