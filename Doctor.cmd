@echo off
REM Work out why the game is not updating, and say so in plain words.
REM
REM Double-click this in the folder you think the game lives in. It changes nothing.
REM
REM It exists because "none of your changes are taking" has now been diagnosed wrong twice from
REM this end, both times plausibly, and the missing ingredient each time was a fact about the
REM machine that nobody could see from the other side of the conversation. Guessing is the actual
REM bug being fixed here.
setlocal EnableDelayedExpansion
title HitboxClone doctor

set "PROJ=%~dp0"
set "PROJ=%PROJ:~0,-1%"
set "BRANCH=claude/game-install-repo-2toaht"
set "BAD="

echo.
echo ============================================
echo   HitboxClone - what is actually going on
echo ============================================
echo.
echo   This folder:  %PROJ%
echo.

REM ---------------------------------------------------------------------------
REM 1. The Desktop shortcut. Top of the list because it is the failure that looks
REM    exactly like every other one: a folder updated perfectly, and a shortcut that
REM    launches a different folder that never was.
REM ---------------------------------------------------------------------------

set "LNKTARGET="
for /f "delims=" %%T in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=[Environment]::GetFolderPath('Desktop')+'\HitboxClone.lnk'; if (Test-Path $p) { (New-Object -ComObject WScript.Shell).CreateShortcut($p).TargetPath }" 2^>nul') do set "LNKTARGET=%%T"

if not defined LNKTARGET goto :noshortcut

echo   Desktop icon launches:
echo     !LNKTARGET!

if /i "!LNKTARGET!"=="%PROJ%\Play.cmd" goto :shortcutok
echo.
echo   ^>^> THIS IS THE PROBLEM. The Desktop icon starts a DIFFERENT folder
echo      than this one. Updating this folder will never change what that
echo      icon launches.
echo.
echo      Fix: run Setup.cmd in whichever folder you want to keep, which
echo      repoints the icon at it. Keep ONE folder.
set "BAD=1"
goto :gitcheck

:shortcutok
echo     ^(matches this folder - good^)
goto :gitcheck

:noshortcut
echo   Desktop icon: not found. You may be launching Play.cmd directly,
echo   or the icon has a different name.

:gitcheck
echo.
where git >nul 2>&1
if not errorlevel 1 goto :havegit
echo   git: NOT INSTALLED. Nothing can update without it.
echo        Get it from https://git-scm.com/download/win
set "BAD=1"
goto :launcher
:havegit

set GIT=git -C "%PROJ%"

%GIT% rev-parse --is-inside-work-tree >nul 2>&1
if not errorlevel 1 goto :isrepo
echo   ^>^> THIS IS THE PROBLEM. This folder is not a git clone at all, so
echo      there is nothing for an updater to fetch. It is a copy of the
echo      files, frozen whenever it was made.
echo.
echo      Fix: clone fresh somewhere and run Setup.cmd there.
set "BAD=1"
goto :launcher
:isrepo

for /f "delims=" %%R in ('%GIT% remote get-url origin 2^>nul') do set "ORIGIN=%%R"
for /f "delims=" %%B in ('%GIT% rev-parse --abbrev-ref HEAD 2^>nul') do set "NOW=%%B"
for /f "delims=" %%H in ('%GIT% rev-parse --short HEAD 2^>nul') do set "HERE=%%H"

echo   Remote:  !ORIGIN!
echo   Branch:  !NOW!
echo   Commit:  !HERE!

echo !ORIGIN! | findstr /i "4-Restoration" >nul
if not errorlevel 1 goto :remoteok
echo.
echo   ^>^> This clone does not point at the 4-Restoration repository, so it
echo      cannot receive any of the work. That is the problem.
set "BAD=1"
:remoteok

if /i "!NOW!"=="%BRANCH%" goto :branchok
echo.
echo   ^>^> THIS IS THE PROBLEM. Development happens on
echo        %BRANCH%
echo      and this clone is on !NOW!, which does not have any of it.
echo      An updater checking !NOW! will keep saying "up to date", and
echo      will be telling the truth.
echo.
echo      Fix:  git -C "%PROJ%" checkout %BRANCH%
echo            git -C "%PROJ%" pull
set "BAD=1"
goto :launcher
:branchok

echo.
echo   Fetching to compare against the server...
%GIT% fetch origin --quiet 2>nul
for /f "delims=" %%H in ('%GIT% rev-parse --short "origin/%BRANCH%" 2^>nul') do set "THERE=%%H"
for /f "delims=" %%N in ('%GIT% rev-list --count "HEAD..origin/%BRANCH%" 2^>nul') do set "BEHIND=%%N"

echo   Server:  !THERE!
if "!BEHIND!"=="0" echo   You are up to date. This folder genuinely has everything.
if not "!BEHIND!"=="0" echo   ^>^> You are !BEHIND! commits behind. Run:  git -C "%PROJ%" pull
if not "!BEHIND!"=="0" set "BAD=1"

:launcher
echo.

REM ---------------------------------------------------------------------------
REM 2. Whose launcher is this? Two different chats have written one, and they
REM    print different things, which is how this was finally spotted.
REM ---------------------------------------------------------------------------

if exist "%PROJ%\Update.cmd" goto :hasupdater
echo   Update.cmd: missing. Play.cmd here cannot self-update.
set "BAD=1"
goto :content
:hasupdater

findstr /c:"Up to date at" "%PROJ%\Update.cmd" >nul 2>&1
if not errorlevel 1 goto :mineupdater
echo   Update.cmd: present, but not the one from this conversation.
echo               ^(A launcher that prints "Already up to date." with no
echo                commit hash is a different one, checking something else.^)
set "BAD=1"
goto :content
:mineupdater
echo   Update.cmd: the current one - good.

:content
REM ---------------------------------------------------------------------------
REM 3. Content spot-check. Cheaper to trust than a commit hash somebody has to
REM    read out, and it answers "is the new stuff actually on disk".
REM ---------------------------------------------------------------------------

if exist "%PROJ%\assets\surfaces\brick_base_color.png" echo   Materials:  present
if not exist "%PROJ%\assets\surfaces\brick_base_color.png" echo   Materials:  MISSING - this folder predates the texture work
if exist "%PROJ%\src\story\Missions.cs" echo   Story mode: present
if not exist "%PROJ%\src\story\Missions.cs" echo   Story mode: MISSING - this folder predates story mode

echo.
echo ============================================
if defined BAD echo   Something above is marked with ^>^>. That is the fix.
if not defined BAD echo   This folder looks correct and current.
if not defined BAD echo   If the game still looks old, check the Desktop icon
if not defined BAD echo   line above - you may be launching a different copy.
echo ============================================
echo.
pause
