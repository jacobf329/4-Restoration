@echo off
REM Double-click this to play the Battlefront clone: the battle station, forty
REM soldiers, five command posts.
REM
REM Play.cmd does the real work - update, build, find the tools, launch - and this
REM only chooses which scene it opens. Duplicating any of that here is how the two
REM launchers would drift apart, and the one that gets run less is the one that would
REM quietly stop updating.
REM
REM Set HITBOX_NO_UPDATE=1 to skip the pull for a session, same as Play.cmd.

set "HITBOX_SCENE=res://scenes/Battlefront.tscn"
call "%~dp0Play.cmd"
