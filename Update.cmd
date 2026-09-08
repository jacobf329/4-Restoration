@echo off
REM Double-click this to get the latest version of the game, then play as normal.
REM
REM Why this exists: development happens on a branch, and a fresh clone lands you on
REM main, which is the initial commit and nothing else. Play.cmd builds whatever is in
REM this folder and builds it correctly - so a laptop that never pulled will keep
REM launching a perfectly good copy of a months-old game and give no hint that it has.
REM That is exactly what happened, and "the updates didn't take" is what it looks like
REM from the outside. This closes it.
setlocal EnableDelayedExpansion
title HitboxClone update

set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"

REM The branch development happens on. If that ever moves to main, change this one line
REM - everything below is written against it rather than assuming a branch name.
set "BRANCH=claude/game-install-repo-2toaht"

echo.
echo ============================================
echo   HitboxClone - update
echo ============================================
echo.

where git >nul 2>&1
if errorlevel 1 (
  echo   git is not installed, so this cannot fetch anything.
  echo   Get it from https://git-scm.com/download/win and run this again.
  echo.
  pause
  exit /b 1
)

REM Every git command is run against this folder explicitly. Double-clicking a .cmd can
REM start it in a different working directory than the one it lives in, and a bare `git
REM pull` that lands in C:\Windows is a confusing way to fail.
set "GIT=git -C "%PROJ%""

%GIT% rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
  echo   This folder is not a git clone, so there is nothing to update.
  echo   Re-clone the repository and run Setup.cmd in the new folder.
  echo.
  pause
  exit /b 1
)

REM Stop on local edits rather than helpfully stashing them. Anything uncommitted here is
REM either work or a mistake, and silently moving somebody's work somewhere they do not
REM know to look for it is the worse of the two outcomes.
for /f "delims=" %%S in ('%GIT% status --porcelain 2^>nul') do (
  echo   You have local changes in this folder:
  echo.
  %GIT% status --short
  echo.
  echo   Nothing has been touched. Commit them, or undo them with
  echo     git -C "%PROJ%" checkout .
  echo   and then run this again.
  echo.
  pause
  exit /b 1
)

echo Fetching...
%GIT% fetch origin --prune
if errorlevel 1 (
  echo.
  echo   Could not reach GitHub. Check the network and try again.
  echo.
  pause
  exit /b 1
)

for /f "delims=" %%B in ('%GIT% rev-parse --abbrev-ref HEAD 2^>nul') do set "NOW=%%B"
for /f "delims=" %%H in ('%GIT% rev-parse --short HEAD 2^>nul') do set "WAS=%%H"

if /i not "!NOW!"=="%BRANCH%" (
  echo Switching from !NOW! to %BRANCH%...
  %GIT% checkout %BRANCH%
  if errorlevel 1 (
    echo.
    echo   Could not switch branch. The message above says why.
    echo.
    pause
    exit /b 1
  )
  echo.
)

REM Fast-forward only. A plain pull on a diverged clone opens a merge - possibly in vim,
REM on a machine whose owner wants to play a game - and there is no situation here where
REM that is the right thing to do unattended.
echo Updating...
%GIT% merge --ff-only origin/%BRANCH%
if errorlevel 1 (
  echo.
  echo   This clone has commits that are not on the server, so it cannot be
  echo   fast-forwarded. Nothing has been changed. Ask before going further -
  echo   the fix depends on whether those commits matter.
  echo.
  pause
  exit /b 1
)

for /f "delims=" %%H in ('%GIT% rev-parse --short HEAD 2^>nul') do set "NEW=%%H"

echo.
if /i "!WAS!"=="!NEW!" (
  echo   Already up to date at !NEW!.
) else (
  echo   Updated !WAS! -^> !NEW!. What changed:
  echo.
  %GIT% --no-pager log --oneline --no-decorate !WAS!..!NEW!
)

echo.
echo ============================================
echo   Done. Launch HitboxClone as usual.
echo.
echo   The first launch after a big update is
echo   slow - Godot re-imports new models.
echo ============================================
echo.
pause
