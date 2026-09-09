@echo off
REM Fetch the latest version of the game.
REM
REM Double-click to update without playing. Play.cmd also calls this with /auto on every
REM launch, which is the usual way it runs - see the header there.
REM
REM   /auto    quiet, never pauses, and never fatal. A machine with no network, or a
REM            folder with local edits in it, still gets to play the copy it has.
REM
REM Written flat, with labels instead of nested if/else blocks. Batch parses a
REM parenthesised block in one go and expands the variables in it before running any of
REM it, which makes nesting the place these files break - and this one cannot be run on
REM the machine it is written on.
setlocal EnableDelayedExpansion

set "AUTO="
set "PROJ="

:args
REM No `if cond a ^& b` here. In batch the ampersand separates commands at the top level
REM rather than binding to the if, so the tail runs whether the condition held or not -
REM which in this exact loop meant every argument was shifted past without ever being
REM read, the project folder was never set, and the update silently did nothing.
if "%~1"=="" goto :argsdone
if /i not "%~1"=="/auto" goto :argproj
set "AUTO=1"
shift
goto :args

:argproj
set "PROJ=%~1"
shift
goto :args

:argsdone

REM Play.cmd runs this from a copy in TEMP and passes the project folder, because %~dp0
REM points at TEMP from there. A double-click passes nothing and uses its own location.
if defined PROJ goto :haveproj
set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"
:haveproj

REM The branch development happens on.
REM
REM Falls back to whatever the remote's default branch is if this one is gone, so
REM merging the work into main later does not strand a machine on a branch that no
REM longer exists - which is the same class of failure this whole file exists to fix.
set "BRANCH=claude/game-install-repo-2toaht"

if defined AUTO goto :nobanner
title HitboxClone update
echo.
echo ============================================
echo   HitboxClone - update
echo ============================================
echo.
:nobanner

REM ---------------------------------------------------------------------------
REM Everything below that can go wrong is survivable in /auto mode. The rule is that
REM launching an old copy of the game always beats refusing to launch: somebody on a
REM train with no signal wants to play, not to be told about a fetch.
REM ---------------------------------------------------------------------------

where git >nul 2>&1
if not errorlevel 1 goto :havegit
if defined AUTO echo   [update skipped] git is not installed - playing the copy in this folder.
if defined AUTO exit /b 0
echo   git is not installed, so this cannot fetch anything.
echo   Get it from https://git-scm.com/download/win, then run this again.
echo.
pause
exit /b 1
:havegit

REM Run against the project folder explicitly. A double-clicked .cmd can start in a
REM different working directory than the one it lives in, and a bare `git pull` that
REM lands in C:\Windows is a confusing way to fail.
set GIT=git -C "%PROJ%"

%GIT% rev-parse --is-inside-work-tree >nul 2>&1
if not errorlevel 1 goto :haveclone
if defined AUTO exit /b 0
echo   This folder is not a git clone, so there is nothing to update.
echo   Re-clone the repository and run Setup.cmd in the new folder.
echo.
pause
exit /b 1
:haveclone

REM Stop on local edits rather than helpfully stashing them. Anything uncommitted here
REM is either work or a mistake, and silently moving somebody's work somewhere they do
REM not know to look for it is the worse of those two outcomes.
set "DIRTY="
for /f "delims=" %%S in ('%GIT% status --porcelain 2^>nul') do set "DIRTY=1"
if not defined DIRTY goto :clean

echo   [update skipped] this folder has local changes:
echo.
%GIT% status --short
echo.
echo   Nothing was touched. Commit them, or undo them with
echo     git -C "%PROJ%" checkout .
echo   and updating will start working again.
echo.
if defined AUTO exit /b 0
pause
exit /b 1
:clean



echo Contacting GitHub...
%GIT% fetch origin --prune --quiet
if not errorlevel 1 goto :fetched
if defined AUTO echo   [update skipped] could not reach GitHub - playing the copy in this folder.
if defined AUTO exit /b 0
echo.
echo   Could not reach GitHub. Check the network and try again.
echo.
pause
exit /b 1
:fetched

REM Which branch to be on: the configured one if the server still has it, otherwise
REM whatever the server says its default is.
set "WANT="
%GIT% rev-parse --verify --quiet "refs/remotes/origin/%BRANCH%" >nul 2>&1
if not errorlevel 1 set "WANT=%BRANCH%"
if defined WANT goto :havewant

REM rev-parse --abbrev-ref prints origin/<branch>. Split on the FIRST slash only and keep
REM the remainder, because a branch name may itself contain slashes - counting fields
REM would turn "origin/claude/game-install-repo" into "claude", which is not a branch and
REM would fail a checkout with a baffling message.
for /f "tokens=1* delims=/" %%A in ('%GIT% rev-parse --abbrev-ref origin/HEAD 2^>nul') do set "WANT=%%B"
if not defined WANT set "WANT=main"
echo   %BRANCH% is gone from the server - following !WANT! instead.
:havewant

set "NOW="
set "WAS="
REM Says which folder and which branch, every time.
REM
REM "Already up to date" is a true and completely useless sentence when there are two clones on
REM the machine and the one being updated is not the one being launched. That went undiagnosed
REM across two conversations. Naming the folder and the branch makes the same line diagnostic.
if defined AUTO echo Checking %PROJ% ^(!WANT!^) for updates...
if not defined AUTO echo Fetching %PROJ% ^(!WANT!^)...

for /f "delims=" %%B in ('%GIT% rev-parse --abbrev-ref HEAD 2^>nul') do set "NOW=%%B"
for /f "delims=" %%H in ('%GIT% rev-parse --short HEAD 2^>nul') do set "WAS=%%H"

if /i "!NOW!"=="!WANT!" goto :onbranch
echo Switching from !NOW! to !WANT!...
%GIT% checkout !WANT! --quiet
if not errorlevel 1 goto :onbranch
if defined AUTO echo   [update skipped] could not switch branch - playing the copy in this folder.
if defined AUTO exit /b 0
echo.
echo   Could not switch to !WANT!. The message above says why.
echo.
pause
exit /b 1
:onbranch

REM Fast-forward only. A plain pull on a diverged clone opens a merge - possibly in vim,
REM on a machine whose owner wants to play a game - and there is no situation here where
REM that is the right thing to do unattended.
%GIT% merge --ff-only "origin/!WANT!" --quiet
if not errorlevel 1 goto :merged
echo   [update skipped] this clone has commits the server does not, so it cannot be
echo   fast-forwarded. Nothing was changed. Worth asking about before going further.
if defined AUTO exit /b 0
echo.
pause
exit /b 1
:merged

set "NEW="
for /f "delims=" %%H in ('%GIT% rev-parse --short HEAD 2^>nul') do set "NEW=%%H"

if /i not "!WAS!"=="!NEW!" goto :changed
if defined AUTO echo   Up to date at !NEW!.
if defined AUTO exit /b 0
echo.
echo   Already up to date at !NEW!.
goto :done

:changed
echo.
echo   Updated !WAS! -^> !NEW!. What changed:
echo.
%GIT% --no-pager log --oneline --no-decorate !WAS!..!NEW!
echo.
if defined AUTO exit /b 0

:done
echo ============================================
echo   Done. Launch HitboxClone as usual.
echo ============================================
echo.
pause
exit /b 0
