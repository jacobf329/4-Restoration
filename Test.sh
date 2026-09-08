#!/bin/bash
#
# Build, then run the whole self-test suite. The Linux counterpart of Play.cmd, and the only
# command that actually verifies anything here.
#
# The build is not optional and is the reason this script exists. Godot does NOT compile C# on
# launch, so `godot --headless -- --selftest` on its own will happily run the *previous* assembly
# and report a full green suite for code that was never compiled. That has happened, and the pass
# it reported was meaningless.
set -euo pipefail

PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Same search order as Play.cmd, so both platforms answer "where is Godot" the same way.
if [ -n "${GODOT:-}" ] && [ -x "${GODOT}" ]; then
  GODOT_EXE="$GODOT"
elif [ -n "${GODOT_HOME:-}" ] && compgen -G "$GODOT_HOME/Godot_v*_mono_linux.x86_64" >/dev/null; then
  GODOT_EXE="$(echo "$GODOT_HOME"/Godot_v*_mono_linux.x86_64 | head -1)"
elif command -v godot >/dev/null 2>&1; then
  GODOT_EXE="$(command -v godot)"
else
  echo "Could not find Godot. Set GODOT_HOME to the folder holding the mono build," >&2
  echo "or run .claude/hooks/session-start.sh to install it." >&2
  exit 1
fi

echo "Building..."
dotnet build "$PROJ/HitboxClone.csproj" --nologo -v minimal

echo
echo "Running the self-test..."

# --selftest goes after a bare `--`, or Godot eats it as one of its own arguments and the suite
# silently never runs.
"$GODOT_EXE" --headless --path "$PROJ" -- --selftest
